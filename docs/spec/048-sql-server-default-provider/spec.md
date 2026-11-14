# Spec 048 — SQL Server as the default storage provider

Status: **proposed** (2026-11-14). Nothing is implemented. Target: 4.x minors (§9).
Implementation plan: [2026-11-14-spec-048-implementation-plan.md](../../plan/2026-11-14-spec-048-implementation-plan.md).
Baseline: master `015fcb0e` (v4.16.0 + 1).
Related: [Spec 047](../047-cosmos-capacity-modes/spec.md) (cheaper Cosmos capacity) and
[Spec 034](../034-private-networking/spec.md) Phase 4 (SQL managed identity, designed but not built),
which this spec takes over. The provider guidance in
[storage providers](../../storage-providers.md#choosing-a-provider) ships ahead of it.
Scope: the `nb` CLI (`infra apply`, `setup`, `topology apply`), `deploy/bicep/deploy.core.bicep`,
`deploy/bicep/templates/azureSql.bicep`, `deploy.yml`, the Azure Pipelines template and docs.
Unchanged: both storage providers, the storage contracts, the WebApp, the Resolver and how they
select a provider at runtime.

## 1. Summary

`nb` deploys Cosmos DB unless it is told otherwise. For most deployments SQL Server is the better
default: S0 costs about $15 a month whatever the endpoint count, against a Cosmos floor of about
$304 plus $23 per endpoint. This spec makes SQL Server the default for **new** deployments and
closes the gaps that would make that a step back:

1. **Provider intent.** A deployment's provider is recorded as a resource-group tag and detected
   for deployments that predate the tag. Reruns, pipelines and `nb topology apply` never switch
   an existing deployment's provider, whatever the default.
2. **Managed identity for provisioned SQL.** The apps stop carrying the SQL admin password. New
   SQL servers are Entra-only.
3. **SQL tier.** `--sql-sku` chooses the database tier, and reruns keep the deployed tier instead
   of resetting it to S0.
4. **Pipelines.** `deploy.yml` and the Azure Pipelines template can deploy SQL Server.
5. **The default flips** after one release that warns about it.

## 2. Current state

| Aspect | Today (`015fcb0e`) |
|---|---|
| CLI default | `CliHelpers.ParseStorageProvider` returns Cosmos when `--storage-provider` is absent (`CliHelpers.cs:13`). `infra apply`, `setup` and `topology apply` all parse it that way |
| Template default | `deploy.core.bicep:40`, `param storageProvider string = 'cosmos'` |
| Rerun safety | Nothing records the provider. A rerun without `--storage-provider sqlserver` against a SQL deployment deploys a Cosmos account and points the apps at it |
| `nb topology apply` | Defaults to Cosmos and creates endpoint containers (`EndpointContainerProvisioner`). A SQL deployment must pass `--storage-provider sqlserver` on every run |
| Pipelines | `deploy.yml` runs `infra apply` and `topology apply`, and the Azure Pipelines template runs `setup`. Neither passes `--storage-provider`, so both deploy Cosmos only |
| Provisioned SQL auth | `deploy.core.bicep:522` builds `SqlConnection` as `User ID=<admin>;Password=<admin password>` for both apps. `docs/cli.md` claimed managed identity until the docs fix that ships with this spec |
| SQL client | The SQL packages reference `Microsoft.Data.SqlClient.Extensions.Azure` 7.1.1, so `Authentication=Active Directory Managed Identity` connection strings work at runtime |
| SQL tier | `azureSql.bicep:18` allows `Basic`, `S0`, `S1`, `S2`, default `S0`. `deploy.core.bicep` passes no SKU, so `nb` always deploys S0, and a rerun redeploys S0 over a database resized in the portal |
| Runtime selection | The WebApp and the Resolver use `NimBus:StorageProvider`, or detect it from the connection settings present, falling back to Cosmos (`Startup.Platform.cs:148-165`, `ResolverStorageProvider.cs:20-33`). Deployed apps get their settings from the template |
| Local | The AppHost already defaults to SQL Server (`src/NimBus.AppHost/Program.cs:12-14`), as does the CrmErpDemo AppHost |
| Cosmos-only tools | `nb container` commands, Copy Endpoint Data and Topology → Storage ([storage providers](../../storage-providers.md#operator-tools-that-are-cosmos-only-in-v1)) |

## 3. Goals

1. A new deployment that doesn't name a provider gets SQL Server.
2. No run of `nb`, local or in a pipeline, switches an existing deployment's provider, whatever
   the default.
3. The provisioned SQL path reaches the Cosmos path's security bar: the apps hold no secret.
4. The SQL tier can be chosen, and survives reruns.
5. Both pipelines deploy either provider.

## 4. Non-goals

- Migrating data between providers, in either direction. Switching an existing deployment stays
  unsupported, and the CLI refuses it (§5.1).
- Removing or deprecating the Cosmos provider. Spec 047 continues for Cosmos deployments.
- Changing how the apps select a provider at runtime.
- Porting the Cosmos-only operator tools to SQL (§5.6).
- vCore and serverless Azure SQL tiers. The DTU tiers cover the scenarios in the docs; a vCore
  database can be brought with `--sql-mode external`.

## 5. Design

### 5.1 Provider intent

The resource-group tag `nimbus-storage-provider` (`cosmos` or `sqlserver`) and, for SQL,
`nimbus-sql-mode` (`provision` or `external`) record the deployment's provider. They are written
the same way as the network tags: `az tag update --operation Merge`, touching only these tags
(`InfrastructureDeployer.cs:733-760`). They are read through `ResourceGroupTags.ReadAsync`.

`infra apply` and `setup` resolve the provider in this order:

1. **The flag.** It must agree with the tag, or with detection when there is no tag. A flag that
   disagrees is refused before any deployment: switching providers isn't supported (§4), and the
   message says so.
2. **The tag.**
3. **Detection**, for deployments made before the tag existed:

   | Resource group holds | Result |
   |---|---|
   | The Cosmos account (`ResourceNaming` name) and no SQL server | `cosmos` |
   | The SQL server (effective name, `--sql-server-name` honoured) and no Cosmos account | `sqlserver`, `provision` |
   | Both | Refuse: pass `--storage-provider`. A deployment migrated from Cosmos to SQL leaves its old Cosmos account behind, so neither can be assumed |
   | Neither, but the Resolver Function App or the WebApp exists | Refuse: pass `--storage-provider`. This is an external-SQL deployment, or a broken one |
   | Nothing NimBus | A new deployment: the default (§5.5) |

4. **The default**, for a new deployment only.

The tags are written before the core deployment. If a deployment then fails part-way, the rerun
finds the tags and resolves the same provider. A resource group whose deployment never got past
the tag step holds only the tags, and resolves to what they say.

`topology apply` resolves the provider from the flag, then the tag, then detection, with the same
refusals. It never applies the default: it runs after `infra apply`, which has written the tag.
The default stays Cosmos only for `topology apply` against a deployment that predates the tag
and can't be detected. That deployment then behaves as it does today.

`--sql-mode` is resolved the same way, flag then tag. The external connection string
(`NIMBUS_SQL_CONNECTION_STRING`) stays an environment variable on every run; it is a secret and
isn't recorded.

### 5.2 Managed identity for provisioned SQL

This is Spec 034's Phase 4 design for Azure SQL (Spec 034 §5.12), taken over here because flipping
the default without it would make the default deployment less secure than today's.

- **Entra admin.** The server gets an Entra administrator: `--sql-entra-admin <object id>` (a
  group is recommended) or, when absent, the identity running `nb`. `azureSql.bicep` declares it
  as `administrators` with `azureADOnlyAuthentication`.
- **New servers are Entra-only.** They have no SQL login, so `--sql-admin-login` and
  `NIMBUS_SQL_ADMIN_PASSWORD` are no longer required for a new SQL deployment.
- **Existing servers keep SQL authentication on** (pinned from `az sql server show`) until the
  operator passes `--sql-entra-only`, so a deployment that still has other SQL-auth clients isn't
  cut off. Their apps move to managed identity on the next `infra apply` all the same.
- **Contained users.** After the core deployment, so the server and the apps' identities exist,
  `infra apply` connects to the database as the Entra admin and runs, idempotently, for the
  Resolver's and the WebApp's managed identities:
  `CREATE USER [<app name>] FROM EXTERNAL PROVIDER` and membership in `db_datareader`,
  `db_datawriter` and `db_ddladmin`. `db_ddladmin` is there because DbUp runs inside the apps in
  `AutoApply` mode (storage providers, "Provisioning modes"). Integration Intelligence and the
  SQL outbox and inbox use the same database and the same users.
- **Connection string.** `SqlConnection` becomes
  `Server=tcp:<fqdn>,1433;Initial Catalog=<db>;Authentication=Active Directory Managed Identity;Encrypt=true;`.
  It holds no secret. The template stops reading the admin password for app settings.
- **The order on an existing deployment.** The users are created before the template switches the
  connection string. So `infra apply` runs the user step first against the existing database, then
  deploys. On a new deployment the database doesn't exist before the deployment, so the order is
  deploy, then users. The apps then fail their first connections until the users exist. Phase 0
  measures that window; the apps' transient retry covers it.
- **Private networking.** The user step is a data-plane connection. Under `--network-mode private`
  it runs from the in-network runner, as the other data-plane checks already do (Spec 034 R5).
- **Pipelines.** The pipeline's service principal must be the Entra admin, or a member of the
  admin group. The docs say how to grant it.

### 5.3 SQL tier

- `azureSql.bicep`'s `skuName` allows `Basic`, `S0`, `S1`, `S2`, `S3` and `S4`, and
  `deploy.core.bicep` passes a `sqlSkuName` parameter through.
- `infra apply` and `setup` gain `--sql-sku`. For an existing database without the flag, the
  deployed tier is read (`az sql db show --query sku.name`) and pinned, like the plan pins, so a
  rerun no longer scales a resized database back to S0. A database whose deployed tier is outside
  the allowed list (resized to Premium or vCore in the portal) is pinned as is: the parameter
  takes any value, and the `@allowed` list applies only to the flag.
- New databases default to S0.
- `docs/storage-providers.md` explains how to read DTU percentage and when to move up (already in
  the guidance that ships ahead of this spec).

### 5.4 Pipelines

`deploy.yml` and `pipelines/azure-pipelines-deploy.yml` gain optional inputs: `storage-provider`,
`sql-mode`, `sql-sku` and `sql-entra-admin`, passed only when set. They also take the optional
secret `NIMBUS_SQL_CONNECTION_STRING`, for external mode. A pipeline run without the inputs
resolves the provider from the tag or detection (§5.1), so existing Cosmos environments keep
deploying Cosmos.

### 5.5 Flipping the default

- **Release A (a 4.x minor).** It ships §5.1 to §5.4 with the default still Cosmos. On a new
  deployment without `--storage-provider`, `infra apply` and `setup` print a warning: the default
  becomes SQL Server in the next minor; pass `--storage-provider cosmos` to keep Cosmos. The
  release notes say the same under Compatibility.
- **Release B (the next minor).** `ParseStorageProvider` no longer supplies a default. §5.1's
  resolution supplies `sqlserver`, with `provision`, for a new deployment. The warning reverses:
  "Deploying SQL Server, the default since vX.Y; pass `--storage-provider cosmos` for Cosmos DB."
  The release notes list the change under Compatibility ("config defaults that moved",
  [versioning](../../versioning.md)).
- **`deploy.core.bicep` keeps `cosmos` as its own default** until the next major. A direct
  template deployment against an existing Cosmos resource group without the parameter would
  otherwise try to provision SQL, and nothing can pin it there. `nb` always passes the parameter
  explicitly. The next major changes the template default and lists it under Breaking.

Why a minor, and not a major: existing deployments can't observe the change, because §5.1 pins
them. Only a brand-new resource group deployed without `--storage-provider` changes, and Release
A warns about it one release ahead. The repo owner confirms this when approving the spec (§13).

### 5.6 What SQL deployments still lack

These are documented, not built:

- **`nb container` commands** (Cosmos data plane). The WebApp's resubmit, skip and purge and the
  MCP operator tools work on both providers.
- **Copy Endpoint Data**. SQL deployments use SQL-native backup and restore.
- **Topology → Storage**. The WebApp already hides it on SQL deployments.

None of them is needed to run a deployment. Porting them would be a separate spec.

## 6. Existing deployments

| Deployment | After Release A | After Release B |
|---|---|---|
| Cosmos, deployed by `nb` | Detected; tagged `cosmos` on the next run. Nothing else changes | Unchanged |
| SQL, provisioned | Detected; tagged. The next `infra apply` creates the contained users and moves the apps to managed identity. SQL auth stays on unless `--sql-entra-only`. The tier is pinned | Unchanged |
| SQL, external | Refused until the operator passes `--storage-provider sqlserver --sql-mode external` once; then tagged | Unchanged |
| Both Cosmos and SQL in one resource group | Refused until the operator passes `--storage-provider` once; then tagged | Unchanged |
| New resource group, no flag | Cosmos, with a warning | SQL Server, provisioned, Entra-only |

## 7. Evaluation

### 7.1 Price

The estimates in [storage providers](../../storage-providers.md#price-at-different-loads), with
Spec 047's proposed Cosmos modes added for comparison (US list prices; 50 RU per delivery,
unmeasured):

| Scenario (deliveries a day) | SQL Server | Cosmos as deployed today | Cosmos serverless (Spec 047) | Cosmos autoscale (Spec 047) |
|---|---|---|---|---|
| Dev / test (1,000) | ~$15 (S0) | ~$421 | ~$1 | ~$158 |
| Small (50,000) | ~$15 (S0) | ~$540 | ~$22 | ~$204 |
| Medium (500,000) | ~$29–$74 (S1–S2) | ~$783 | ~$203 | ~$306 |
| Large (5,000,000) | ~$147–$294 (S3–S4) | ~$1,522 + `messages` raised | ~$1,951 | ~$720, with `messages`' max raised |

SQL Server is the cheapest in every scenario except a near-idle dev deployment, where Cosmos
serverless costs less. Phase 0 replaces the per-delivery and per-tier assumptions with
measurements (§14).

### 7.2 Other effects

- **Security improves.** The provisioned SQL path stops carrying the admin password, which today
  is the apps' live credential. New servers have no SQL login at all.
- **Rerun safety improves for everyone.** Today a forgotten `--storage-provider sqlserver`
  redeploys a SQL deployment onto Cosmos. With the tag that can't happen, whatever the default.
- **Pipelines gain SQL.** Today they deploy Cosmos only.
- **The Cosmos-only tools** remain unavailable to the new default (§5.6).
- **The tier is a manual decision.** SQL doesn't scale itself. The docs say how to watch DTU and
  when to move up, and `--sql-sku` makes it one flag.
- **New dependency in `infra apply`.** The contained-user step needs a SQL connection from the
  machine running `nb`, as the Entra admin. On a private deployment that already means the
  in-network runner.

### 7.3 Alternatives

| Alternative | Verdict |
|---|---|
| Flip the default only | Rejected. Reruns and pipelines would switch existing Cosmos deployments to SQL (§2, rerun safety), and the default would carry a password |
| Flip in a major (v5.0.0) | Not chosen. Existing deployments can't observe it once §5.1 pins them, and NimBus has no v5 branch planned. The repo owner confirms (§13) |
| Keep Cosmos the default, document SQL as cheaper | Rejected. Most new deployments would keep paying the Cosmos floor by default |
| Ask for the provider when it's absent | Rejected for `nb`'s non-interactive runs and pipelines. The warning release serves the same purpose |
| Azure SQL serverless (vCore, auto-pause) as the default tier | Rejected for now. It needs a vCore SKU, its minimum is above S0 when the database doesn't pause, and the Resolver's traffic keeps it from pausing |

## 8. Tests

**CLI (`tests/NimBus.CommandLine.Tests`):**

- **Provider resolution:** flag, tag and detection for each row of §5.1's table, including both
  refusals. A contradicting flag refuses before any `az deployment` call. The tags are written
  before the core deployment.
- **`topology apply`:** resolves from the tag; creates no Cosmos containers for `sqlserver`; and
  falls back to Cosmos only for an undetectable deployment without a tag.
- **SQL tier:** `--sql-sku` is passed through. An existing database's tier is pinned, including one
  outside the allowed list. A new database gets S0.
- **Managed identity:**
  - a new server is deployed Entra-only, with the admin from `--sql-entra-admin` or the caller;
  - an existing server keeps SQL authentication unless `--sql-entra-only`;
  - the contained-user statements run for both app identities, idempotently, before the
    connection-string switch on an existing database and after the deployment on a new one;
  - the connection string carries no password.
- **The default:** in Release A, the warning on a new deployment; in Release B, `sqlserver` and
  `provision` for a new deployment and Cosmos never chosen implicitly.

**Template:** `deploy.core.bicep` builds a password-free `SqlConnection` and passes `sqlSkuName`
through. `azureSql.bicep` declares the Entra administrator, with Entra-only authentication when
asked. A test pins `deploy.core.bicep`'s `storageProvider` default to `cosmos` (§5.5).

**Pipelines:** a workflow test, or a script check, confirms that the new inputs are passed only
when set.

**Live, run by hand and recorded in the PRs:** §14.

No store, conformance or WebApp tests change.

## 9. Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | Benchmarks and checks (§14) on temporary resource groups | Results in this folder; the docs' price table calibrated |
| 1 | Provider intent: tags, detection, refusals, `topology apply` (§5.1) | Release build green; every §6 row exercised against a test resource group |
| 2 | SQL managed identity (§5.2) | A new deployment is Entra-only and the apps hold no secret. An existing SQL deployment moves to managed identity with no failed message |
| 3 | SQL tier (§5.3) and pipelines (§5.4) | A rerun keeps a resized tier; both pipelines deploy SQL |
| A | Release A: Phases 1-3 with the warning (§5.5) | Released |
| B | Release B: the default flips (§5.5) | Released |

Phases 1 to 3 can ship in one minor or several; the flip waits for all three.

## 10. Documentation

- `docs/storage-providers.md`: the "Choosing a provider" guidance ships ahead of this spec. Each
  phase updates it: SQL's app authentication (Phase 2), the tier flag (Phase 3), the pipelines'
  support (Phase 3) and the default (Release B). Phase 0 replaces the estimates with
  measurements.
- `docs/cli.md`: `--sql-sku`, `--sql-entra-admin`, `--sql-entra-only`, the provider tags and the
  resolution order; the default in `--storage-provider`'s description.
- `docs/azure-requirements.md`: SQL Server as the default store; the Entra admin requirement for
  whoever runs `nb`; no secrets on either path.
- `docs/deployment.md` and the getting-started pages: examples without `--storage-provider` now
  mean SQL, and examples that need Cosmos say so.
- `docs/private-networking.md`: the contained-user step runs in-network.
- Release notes for A and B (§5.5).

## 11. Residual risks

1. **Unmeasured SQL capacity.** Until Phase 0 runs, nobody knows how much load S0 carries. A busy
   new deployment could start too small. The docs say how to read DTU and move up.
2. **The first minutes of a new SQL deployment.** The apps start before their contained users
   exist and retry until they do (§5.2).
3. **Entra admin access.** Whoever runs `nb`, local or a pipeline, must be the server's Entra
   admin or in the admin group. The docs and the error message say how to grant it.
4. **Operators who rely on `nb container`** get no CLI on SQL (§5.6).
5. **Direct template users** keep Cosmos as the template default until the next major (§5.5).

## 12. Decisions

1. **SQL Server becomes the default for new deployments**, after one warning release (§5.5).
2. **Provider intent is a resource-group tag**, with detection for older deployments and refusal
   when it is ambiguous (§5.1).
3. **Provisioned SQL moves to managed identity before the flip**, and new servers are Entra-only
   (§5.2).
4. **The SQL tier is selectable and pinned** (§5.3).

## 13. Open questions for the repo owner

1. **Release vehicle for the flip:** a minor with one warning release (proposed, §5.5), or v5.0.0?
2. **Entra-only for existing provisioned servers:** opt-in with `--sql-entra-only` (proposed), or
   default once their apps have moved to managed identity?

## 14. Facts to verify (Phase 0)

On temporary resource groups in a non-production subscription, deleted afterwards:

1. **Load per delivery.** Drive steady traffic with Settings → Simulation on one deployment per
   provider, at three rates. Record Cosmos RU per delivery (by container), and SQL DTU percentage
   per delivery rate on S0, S1 and S2. The results replace the docs' 50-RU assumption and the
   suggested tiers.
2. **Managed identity end to end.** A new Entra-only server, contained users created by the CLI as
   the Entra admin, both apps connecting with `Active Directory Managed Identity`, DbUp
   `AutoApply` succeeding with `db_ddladmin`. Measure how long the apps' first connections fail on
   a new deployment.
3. **Moving an existing SQL deployment** to managed identity under traffic, with no failed or
   dead-lettered message.
4. **Tier pinning.** A database resized in the portal, including to a tier outside the allowed
   list, survives `infra apply`.
5. **Detection** against a resource group holding both a Cosmos account and a SQL server, which is
   the state the 2026-08 migration of `rg-nimbus-dev` left behind.
6. **Prices** for S3 and S4 and the other figures in the docs, against the Azure pricing page.

## 15. References

- [Storage providers: choosing a provider](../../storage-providers.md#choosing-a-provider).
- Spec 034 (private networking): §5.12 identity hardening, R5 private operations.
- Spec 047 (Cosmos capacity modes).
- ADR-010 (pluggable message storage).
- [Azure SQL DTU purchasing model](https://learn.microsoft.com/azure/azure-sql/database/service-tiers-dtu).
- [Microsoft Entra authentication for Azure SQL](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-overview)
  and [Entra-only authentication](https://learn.microsoft.com/azure/azure-sql/database/authentication-azure-ad-only-authentication).
