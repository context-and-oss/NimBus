# Spec 048 — SQL Server as the default storage provider

Status: **proposed** (2026-11-14), revised the same day after a Codex review (§16). Nothing is
implemented. Target: 4.x minors for the groundwork, and the next major for the default flip
unless the repo owner records an exception (§5.6, §13).
Implementation plan: [2026-11-14-spec-048-implementation-plan.md](../../plan/2026-11-14-spec-048-implementation-plan.md).
Baseline: master `015fcb0e` (v4.16.0 + 1).
Related: [Spec 047](../047-cosmos-capacity-modes/spec.md) (cheaper Cosmos capacity), and
[Spec 034](../034-private-networking/spec.md) §5.12, the SQL managed-identity design of its Phase 4,
designed but not built, which this spec takes over. The provider guidance in
[storage providers](../../storage-providers.md#choosing-a-provider) ships ahead of this spec.
Scope: the `nb` CLI (`infra apply`, `setup`, `topology apply`), `deploy/bicep/deploy.core.bicep`,
`deploy/bicep/templates/azureSql.bicep`, `deploy.yml`, the Azure Pipelines template and docs.
Unchanged: both storage providers, the storage contracts, and how the WebApp and the Resolver
select a provider at runtime.

## 1. Summary

`nb` deploys Cosmos DB unless it is told otherwise. For most deployments SQL Server is the better
default: S0 costs about $15 a month whatever the endpoint count, against a Cosmos floor of about
$304 plus $23 per endpoint. This spec makes SQL Server the default for **new** deployments. First
it closes the gaps that would make that a step back:

1. **Provider intent.** A deployment's provider is read from what its apps are configured with,
   then recorded as resource-group tags. Reruns, pipelines and `nb topology apply` never switch an
   existing deployment's provider, and any discovery failure stops the run instead of looking like
   a new deployment.
2. **External SQL works end to end.** Today `--sql-mode external` configures the WebApp but not the
   Resolver.
3. **Managed identity for provisioned SQL.** The apps stop carrying the SQL admin password, in a
   staged rollout that works on existing servers. New servers have SQL authentication disabled.
4. **SQL tier.** `--sql-sku` chooses the database tier, and reruns keep the deployed SKU instead of
   resetting it to S0.
5. **Pipelines.** `deploy.yml` and the Azure Pipelines template can deploy SQL Server.
6. **SQL retention.** Today a SQL deployment never deletes anything. Spec 050 adds a cleanup, and
   this spec gives SQL the same retention defaults as Cosmos in the release that flips the default
   (§5.8).
7. **The default flips** once all of that has shipped (§5.6).

## 2. Current state

| Aspect | Today (`015fcb0e`) |
|---|---|
| CLI default | `CliHelpers.ParseStorageProvider` returns Cosmos when `--storage-provider` is absent (`CliHelpers.cs:13`). `infra apply`, `setup` and `topology apply` all parse it that way |
| Template default | `deploy.core.bicep:40`, `param storageProvider string = 'cosmos'` |
| Rerun safety | Nothing records the provider. A rerun without `--storage-provider sqlserver` against a SQL deployment deploys a Cosmos account and points the apps at it |
| Resource discovery | `DiscoverExistingLocationsAsync` returns an empty map when `az resource list` fails or its output isn't JSON (`InfrastructureDeployer.cs:320-362`). That is harmless for location pins, but it can't be used to tell an existing deployment from a new one |
| `nb topology apply` | Applies the Service Bus topology first, then creates endpoint containers when the provider parses as Cosmos (`TopologyCommands.cs:114-120`). A SQL deployment must pass `--storage-provider sqlserver` on every run |
| Pipelines | `deploy.yml` runs `infra apply` and `topology apply`, and the Azure Pipelines template runs `setup`. Neither passes `--storage-provider`, so both deploy Cosmos only |
| Provisioned SQL auth | `deploy.core.bicep:521-523` builds the Resolver's `SqlConnection` as `User ID=<admin>;Password=<admin password>`, and the WebApp receives the same string (`InfrastructureDeployer.cs:238`). `azureSql.bicep` declares no Entra administrator and no server identity |
| External SQL | The connection string reaches only the WebApp's template (`deploy.webapp.bicep:23`). The core template sets the Resolver's `SqlConnection` only when `sqlMode == 'provision'` (`deploy.core.bicep:521`). So an external-SQL Resolver gets no SQL setting, falls back to Cosmos selection (`ResolverStorageProvider.cs:20-33`) and fails. A setting added by hand is lost on the next `infra apply`, which replaces the app settings |
| SQL client | The SQL packages reference `Microsoft.Data.SqlClient.Extensions.Azure` 7.1.1, so `Authentication=Active Directory Managed Identity` connection strings work at runtime |
| Schema startup | `SqlServerSchemaInitializer.StartAsync` opens its connection once, with no retry (`SqlServerSchemaInitializer.cs:123`). An app that can't connect fails to start |
| SQL tier | `azureSql.bicep:12-18` allows `Basic`, `S0`, `S1`, `S2`, default `S0`. `deploy.core.bicep` passes no SKU, so `nb` always deploys S0, and a rerun redeploys S0 over a database resized in the portal |
| Network | Public mode: the SQL server's only firewall rule is `AllowAllWindowsAzureIps` (`azureSql.bicep:42-49`), so a workstation can't connect. Private mode: `nb` checks private-endpoint resolution before data-plane steps, not SQL connectivity |
| Runtime selection | The WebApp and the Resolver use `NimBus:StorageProvider`, or detect it from the connection settings present, falling back to Cosmos (`Startup.Platform.cs:148-165`, `ResolverStorageProvider.cs:20-33`) |
| Retention | Cosmos expires data by item `ttl`: completed and skipped rows after 30 days, messages after 90, audits after a year. **SQL Server never deletes anything.** `RemoveMessage`, `ArchiveFailedEvent` and both `PurgeMessages` only set `Deleted = 1` (`SqlServerMessageTrackingStore.Writes.cs:276-310`), and no code deletes `UnresolvedEvents`, `Messages` or `MessageAudits` rows by age. A SQL deployment grows until it reaches its tier's limit, 250 GB on S0, which then rejects writes. Found on 2026-11-15; [Spec 050](../050-retention-settings/spec.md) adds the cleanup |
| Local | The AppHost already defaults to SQL Server (`src/NimBus.AppHost/Program.cs:12-14`), as does the CrmErpDemo AppHost |
| Cosmos-only tools | `nb container` commands, Copy Endpoint Data and Topology → Storage ([storage providers](../../storage-providers.md#operator-tools-that-are-cosmos-only-in-v1)) |

## 3. Goals

1. A new deployment that doesn't name a provider gets SQL Server.
2. No run of `nb`, local or in a pipeline, switches an existing deployment's provider. A discovery
   failure never reads as "nothing here".
3. Both SQL modes configure both apps.
4. The provisioned SQL path reaches the Cosmos path's security bar, on new and existing servers:
   the apps hold no secret.
5. The SQL tier can be chosen, and the deployed SKU survives reruns.
6. Both pipelines deploy either provider.
7. A new deployment on the default provider doesn't grow without bound.

## 4. Non-goals

- Migrating data between providers, in either direction. Switching an existing deployment stays
  unsupported, and the CLI refuses it (§5.1).
- Removing or deprecating the Cosmos provider. Spec 047 continues for Cosmos deployments.
- Changing how the apps select a provider at runtime.
- Porting the Cosmos-only operator tools to SQL (§5.7).
- Managed identity for external SQL. The operator's connection string decides its authentication.

## 5. Design

### 5.1 Provider intent

**Tags.** `nimbus-storage-provider` (`cosmos` or `sqlserver`) and, for SQL, `nimbus-sql-mode`
(`provision` or `external`). They are written the same way as the network tags,
`az tag update --operation Merge` touching only these tags (`InfrastructureDeployer.cs:733-760`),
and read through `ResourceGroupTags.ReadAsync`.

**Strict discovery.** Resolution uses a strict listing, separate from the lenient location pins. A
failed `az resource list`, output that isn't a JSON array, or a failed app-settings read stops the
run with the `az` error. Nothing is inferred from a listing that didn't succeed.

**Resolution.** `infra apply`, `setup` and `topology apply` resolve the provider before they change
anything, in this order:

1. **The flag**, which must agree with what steps 2 or 3 find. A disagreement is refused before any
   change: switching providers isn't supported (§4), and the message says so.
2. **The tags.**
3. **The apps' configuration**, for deployments made before the tags. When the Resolver Function
   App or the WebApp exists, `nb` reads its app settings
   (`az functionapp|webapp config appsettings list`, as `InfrastructureDeployer.cs:712` already
   does for the content share):

   | Apps' settings | Result |
   |---|---|
   | `NimBus__StorageProvider`, if set, wins; otherwise `CosmosAccountEndpoint` → Cosmos | `cosmos` |
   | `SqlConnection` whose server is this resource group's SQL server (effective name, `--sql-server-name` honoured) | `sqlserver`, `provision` |
   | `SqlConnection` pointing at any other server | `sqlserver`, `external` |
   | The two apps disagree, neither setting is present, or the settings can't be read | Refuse: pass `--storage-provider` (and `--sql-mode`) |

   The resource names alone never decide. A resource group can hold a Cosmos account left behind
   by a move to SQL (as `rg-nimbus-dev` did after 2026-08-08), or a SQL server the apps don't use.
4. **The resources**, when no NimBus app exists yet, which means a run that stopped before
   creating them: the Cosmos account alone → `cosmos`; the SQL server alone → `sqlserver`,
   `provision`; both → refuse.
5. **The default**, only when discovery succeeded and found no NimBus resource at all (§5.6).

**When the tags are written.** After resolution and before the first change. A run that stops
part-way leaves the tags, so the rerun resolves the same provider. A resource group that holds only
the tags resolves to them.

**`topology apply`** resolves before touching Service Bus, so a refusal leaves nothing half
applied. It never uses the default, because `infra apply` runs first and writes the tags, and it
has no Cosmos fallback of its own: a deployment it can't resolve is refused.

**`--sql-mode`** follows the same order. The external connection string
(`NIMBUS_SQL_CONNECTION_STRING`) stays an environment variable on every run: it is a secret, so it
isn't recorded.

### 5.2 External SQL for both apps

`deploy.core.bicep` gains a secure `sqlConnectionString` parameter. In external mode it becomes the
Resolver's `SqlConnection`, as `deploy.webapp.bicep:23` already does for the WebApp.
`InfrastructureDeployer` passes the value from `ResolveSqlConnectionString` (`:419`) to both
templates as a secure parameter, never as a process argument. This is a bug fix on its own and
ships in the first release (§9). Until then the docs say that external mode doesn't configure the
Resolver.

### 5.3 Managed identity for provisioned SQL

This is Spec 034 §5.12's design for Azure SQL. This spec takes it over, because flipping the
default without it would make the default deployment less secure than today's. The run is staged,
and each stage is idempotent and recorded, so an interrupted run resumes where it stopped.

**Server prerequisites (template).**

- An Entra administrator: `--sql-entra-admin <object id>` (a group is recommended) or, when absent,
  the identity running `nb`. Its object id is taken from the `oid` claim of the caller's access
  token (`az account get-access-token`), which is the right id for both users and service
  principals. `az account show` gives a service principal's application id, which is the wrong one.
- On a new server, SQL authentication is disabled (`azureADOnlyAuthentication` in the server's
  `administrators` block at creation). The server may still hold a system-generated SQL admin
  account; it can't sign in.
- On an existing server, the template adds the administrator and leaves SQL authentication on.
  Turning it off is a separate, later step (below).

**Network path for the CLI's SQL connection.** The user step (below) connects from the machine
running `nb`:

- **Public mode.** `nb` first tries to connect. When the server refuses the client's address, its
  error names that address. `nb` then adds a firewall rule for that single address, named
  `nimbus-nb-<UTC timestamp>`, and removes it in a `finally`. At the start of every run it also
  deletes `nimbus-nb-*` rules older than an hour, left by a run that died. It never touches other
  rules.
- **Private mode.** The run already happens on the in-network runner (Spec 034 R5). Before the user
  step it checks that the SQL server's private endpoint resolves and accepts a connection, and it
  stops with a clear message otherwise.

**Contained users without Microsoft Graph.** `CREATE USER … FROM EXTERNAL PROVIDER` resolves the
name through Microsoft Graph. A service principal caller, such as either pipeline, then needs the
SQL server to have an identity with Graph read permissions, which only a privileged Entra role can
grant. So `nb` creates the users by id instead:
`CREATE USER [<app name>] WITH SID = <client id as binary>, TYPE = E`. The client id of each app's
managed identity comes from ARM (`identity.principalId` → the service principal's `appId`; §14
item 3). No Graph lookup is needed. Each user gets `db_datareader`, `db_datawriter` and
`db_ddladmin`. `db_ddladmin` is there because DbUp runs inside the apps in `AutoApply` mode
(storage providers, "Provisioning modes"). Integration Intelligence and the SQL outbox and inbox
use the same database and the same users. The step then verifies both users in
`sys.database_principals`.

**Stages on a new deployment.** Nothing runs NimBus code before `nb deploy apps`, so there is no
window in which the apps fail:

1. The core deployment creates the server, without SQL authentication, and the Resolver. The
   Resolver's `SqlConnection` already uses managed identity.
2. The WebApp deployment creates the WebApp, also with the managed-identity string.
3. The user step creates and verifies both users, now that both identities exist.
4. The tag `nimbus-sql-auth=managed-identity` is written. `nb deploy apps` (or `setup`'s next step)
   then starts the code.

**Stages on an existing provisioned deployment.** The apps run throughout, so the connection string
changes only after the users exist:

1. The core and WebApp deployments run with the administrator added, while the apps keep the
   password connection string (template parameter `sqlAppAuth=password`).
2. The user step creates and verifies both users.
3. The core and WebApp deployments run again with `sqlAppAuth=managed-identity`. The apps restart
   on the new setting and connect as themselves.
4. The tag `nimbus-sql-auth=managed-identity` is written. Later runs skip straight to that mode.

An interruption before stage 4 leaves the tag unset. The rerun repeats from stage 1, and every
stage is a no-op when its work is already done. Stage 3 restarts both apps once. Service Bus holds
messages meanwhile, and the Resolver's restart is like any deployment.

**Turning off SQL authentication on an existing server** is opt-in with `--sql-entra-only`. It runs
`az sql server ad-only-auth enable` only once the tag says `managed-identity`, so the apps can't be
cut off. Afterwards the template is told the state (pinned from `az sql server ad-only-auth get`),
and it no longer needs `--sql-admin-login` or `NIMBUS_SQL_ADMIN_PASSWORD`.

**Credentials no longer needed.** A new server needs neither `--sql-admin-login` nor
`NIMBUS_SQL_ADMIN_PASSWORD`. An existing server needs them only until its SQL authentication is
off.

**Pipelines.** The pipeline's service principal must be the Entra administrator, or a member of the
administrator group. In public mode it also needs to manage firewall rules on the server, which the
Contributor role on the resource group already grants. No Graph permission is needed.

### 5.4 SQL tier

- `--sql-sku` takes `Basic` or `S0` to `S4`. The CLI validates it; `azureSql.bicep`'s SKU becomes an
  object parameter (`name`, `tier`, `family`, `capacity`) with no `@allowed` list.
- For an existing database without the flag, `nb` reads the whole SKU (`az sql db show --query sku`)
  and passes it back unchanged. A database resized to Premium or vCore in the portal keeps its
  tier, family and capacity. `nb` never resizes a database unless `--sql-sku` asks.
- New databases get S0.
- The guidance in `docs/storage-providers.md` already says how to read DTU percentage and when to
  move up.

### 5.5 Pipelines

`deploy.yml` and `pipelines/azure-pipelines-deploy.yml` gain optional inputs: `storage-provider`,
`sql-mode`, `sql-sku` and `sql-entra-admin`. They also take the optional secret
`NIMBUS_SQL_CONNECTION_STRING`, for external mode. Each is passed only when set. A pipeline run
without them resolves the provider as §5.1 does, so existing Cosmos environments keep deploying
Cosmos.

### 5.6 Flipping the default

`docs/versioning.md` reserves behaviour changes a consumer can observe for a major. A pipeline or
script that creates new environments without `--storage-provider` would observe this one. So:

- **4.x minors** ship §5.1 to §5.5 with the default still Cosmos. On a new deployment without
  `--storage-provider`, `infra apply` and `setup` print a warning: the default becomes SQL Server in
  the next major; pass `--storage-provider cosmos` to keep Cosmos. The release notes repeat it under
  Compatibility.
- **The next major** flips it. `ParseStorageProvider` supplies no default; §5.1 supplies
  `sqlserver` with `provision` for a new deployment. The release notes list it under ⚠️ Breaking.
  `deploy.core.bicep`'s own default becomes `sqlserver` in the same release. Until then it stays
  `cosmos`: a direct template deployment against an existing Cosmos resource group without the
  parameter would otherwise try to provision SQL, and nothing could pin it.

Some refusals in the 4.x minors are new: an existing deployment whose provider can't be resolved
(§5.1). They replace today's silent fallback to Cosmos, which deploys a second store, so they are
bug fixes, listed under Compatibility.

The repo owner may instead record a versioning exception and flip the default in a minor after the
warning release. Existing deployments can't observe the flip, because §5.1 pins them (§13).

### 5.7 What SQL deployments still lack

These are documented, not built:

- **`nb container` commands** (Cosmos data plane). The WebApp's resubmit, skip and purge and the
  MCP operator tools work on both providers.
- **Copy Endpoint Data**. SQL deployments use SQL-native backup and restore.
- **Topology → Storage**. The WebApp already hides it on SQL deployments.

None of them is needed to run a deployment. Porting them would be a separate spec.

### 5.8 SQL retention

A SQL deployment keeps every row forever (§2). As the default, that would fill new deployments'
databases. At the Medium scenario's load, 500,000 deliveries a day, and an assumed 2 to 10 KB
stored per delivery (messages, tracking row and audits), the database grows by 1 to 5 GB a day,
so S0's 250 GB lasts between two months and eight. The fix is [Spec 050](../050-retention-settings/spec.md):

- Spec 050 adds Settings → Retention: four periods (open events, resolved events, message history,
  audit trail) and host-configuration pins. SQL rows older than their period are deleted by the
  WebApp retention job that AF-199 (GH#291) is building, which Spec 050 makes configurable.
- Spec 050 §13 leaves one question open: whether SQL's periods start out unlimited in 4.x, which
  is proposed, or take Cosmos's values at once, as AF-199 does today. With the proposal, nothing
  changes on upgrade until an Owner sets periods or a pin does. The rest of this section assumes
  the proposal. If the owner chooses the other way, SQL has the Cosmos defaults before the flip,
  and the next bullet is already done.
- **This spec changes SQL's defaults to Cosmos's** (open events unlimited, resolved events 30
  days, message history 90 days, audit trail 1 year) in the release that flips the default
  provider. That is the next major, or the minor under a recorded exception (§5.6). Existing SQL
  deployments then start deleting data older than those defaults, unless an Owner has saved other
  periods or a pin is set. The release notes list this under ⚠️ Breaking, with the periods and how
  to keep data longer: save the periods first, or pin them with `--retention-*`.
- **The flip waits for Spec 050's cleanup** (Spec 050 Phase 2) to have shipped in an earlier minor.
  So the cleanup has run on opted-in deployments before it runs by default.

## 6. Existing deployments

| Deployment | After the 4.x groundwork | After the flip |
|---|---|---|
| Cosmos, deployed by `nb` | Resolved from the apps' settings and tagged `cosmos`. Nothing else changes | Unchanged |
| SQL, provisioned | Resolved and tagged. The next `infra apply` runs the staged move to managed identity (§5.3). SQL authentication stays on unless `--sql-entra-only`. The SKU is pinned | Unchanged |
| SQL, external | Resolved from the WebApp's `SqlConnection` and tagged. The Resolver gets its `SqlConnection` for the first time (§5.2) | Unchanged |
| Apps disagree or carry no storage setting | Refused until the operator passes `--storage-provider` once; then tagged | Unchanged |
| A failed or partial discovery | Refused with the `az` error; nothing changes | Unchanged |
| New resource group, no flag | Cosmos, with a warning | SQL Server, provisioned, SQL authentication off, with the Cosmos retention defaults (§5.8) |
| SQL, with no retention saved or pinned | Nothing expires (Spec 050's upgrade default) | Starts deleting data older than the Cosmos defaults (§5.8) |

## 7. Evaluation

### 7.1 Price

The estimates in [storage providers](../../storage-providers.md#price-at-different-loads), with
Spec 047's proposed Cosmos modes added for comparison. They use US list prices and 50 RU per
delivery, unmeasured. Autoscale assumes steady traffic, but autoscale bills each hour's peak.

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

- **Security.** The provisioned SQL path stops carrying the admin password, which today is the
  apps' live credential. New servers have SQL authentication off.
- **Rerun safety, for everyone.** Today a forgotten `--storage-provider sqlserver` redeploys a SQL
  deployment onto Cosmos. After §5.1 that can't happen, whatever the default.
- **External SQL** starts working for the Resolver.
- **Pipelines gain SQL.** Today they deploy Cosmos only.
- **The Cosmos-only tools** stay unavailable to the new default (§5.7).
- **The tier is a manual decision.** SQL doesn't scale itself. The docs say how to watch DTU and
  when to move up, and `--sql-sku` makes the change one flag.
- **`infra apply` gains a SQL data-plane step.** It is the first one: a SQL connection from the
  machine running `nb`, as the Entra administrator. That needs a temporary firewall rule in public
  mode and the in-network runner in private mode.
- **One extra deployment pass**, once per existing provisioned deployment, during the move to
  managed identity (§5.3).
- **Bounded storage on the default.** With §5.8 a new SQL deployment keeps about the same data as a
  Cosmos one, and the price table's "stored data" column stays realistic over time. Without it,
  storage would grow until S0's 250 GB stopped writes.

### 7.3 Alternatives

| Alternative | Verdict |
|---|---|
| Flip the default only | Rejected. Reruns and pipelines would switch existing Cosmos deployments to SQL (§2, rerun safety), and the default would carry a password |
| Detect the provider from resource names | Rejected. A leftover Cosmos account or an unused SQL server would decide the provider; the apps' settings say what is in use (§5.1) |
| `CREATE USER … FROM EXTERNAL PROVIDER` | Rejected as the default path. A service principal caller needs a server identity with Graph permissions, which only a privileged Entra role can grant. Creating users by SID needs no Graph (§5.3) |
| Run the user step in Azure (a Bicep deployment script) | Not chosen. It avoids the firewall rule, but needs a storage account and a container instance per run, and private mode would need them inside the network |
| Flip in a 4.x minor | Possible only with a recorded versioning exception (§5.6, §13) |
| Keep Cosmos the default and document SQL as cheaper | Rejected. Most new deployments would keep paying the Cosmos floor by default |
| Azure SQL serverless (vCore, auto-pause) as the default tier | Rejected for now. Its minimum costs more than S0 when the database doesn't pause, and the Resolver's traffic keeps it from pausing. `--sql-sku` doesn't offer it; a database resized to it keeps its SKU (§5.4) |

## 8. Tests

**CLI (`tests/NimBus.CommandLine.Tests`):**

- **Resolution:** flag, tags, apps' settings and resources for each row of §5.1. That includes:
  - every refusal, and that a refusal happens before any `az deployment` and before Service Bus
    topology changes;
  - a failed or malformed `az resource list`, and a failed app-settings read, both refused;
  - `NimBus__StorageProvider` winning over the connection settings;
  - provisioned against external decided by the server in `SqlConnection`;
  - tags written after resolution and before the first change.
- **`topology apply`:** resolves before touching Service Bus; creates no Cosmos containers for
  `sqlserver`; refuses an unresolved deployment and never falls back to Cosmos.
- **External SQL:** the core deployment receives `sqlConnectionString` as a secure parameter, and
  the Resolver's settings carry it.
- **Managed identity:**
  - the administrator's object id comes from the token's `oid`, for a user and for a service
    principal;
  - a new server is deployed with SQL authentication off;
  - an existing server keeps it on until `--sql-entra-only`, which is refused before the tag says
    `managed-identity`;
  - the user step emits `CREATE USER … WITH SID …, TYPE = E` and the role memberships for both
    apps, idempotently, then verifies them;
  - the stage order on new and on existing deployments, and resuming after an interruption at each
    stage;
  - the public-mode firewall rule is added only after a refused connection, for the single
    address, and removed on success and on failure; stale `nimbus-nb-*` rules are removed and other
    rules left alone;
  - the private-mode SQL connectivity check;
  - no connection string carries a password once the tag says `managed-identity`.
- **SQL tier:** `--sql-sku` validated and passed through; an existing SKU, including vCore and
  Premium, passed back whole; new databases at S0.
- **The default:** the warning in the 4.x minors; in the flip release, `sqlserver` and `provision`
  for a new deployment, and Cosmos never chosen implicitly.

**Template:** `deploy.core.bicep` sets the Resolver's `SqlConnection` in both SQL modes; builds a
password-free string when `sqlAppAuth` is `managed-identity`; and passes the SKU object through.
`azureSql.bicep` declares the administrator, and SQL authentication off only for a new server. A
test pins `deploy.core.bicep`'s `storageProvider` default until the flip release.

**Pipelines:** a script check confirms that the new inputs and the secret are passed only when set.

**Live, run by hand and recorded in the PRs:** §14.

No store, conformance or WebApp tests change.

## 9. Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | Benchmarks and checks (§14) on temporary resource groups | Results in this folder; the docs' price table calibrated |
| 1 | Provider intent with strict discovery (§5.1); external SQL for the Resolver (§5.2) | Release build green; every §6 row exercised against test resource groups; an external-SQL deployment processes messages |
| 2 | SQL managed identity (§5.3) | A new deployment has SQL authentication off and the apps hold no secret. An existing SQL deployment moves to managed identity under traffic with no failed or dead-lettered message, from a workstation (public) and from a pipeline |
| 3 | SQL tier (§5.4) and pipelines (§5.5) | A rerun keeps a resized SKU; both pipelines deploy SQL |
| 4.x | Phases 1-3 released in one or more minors, with the warning (§5.6) | Released |
| Flip | The default changes in the next major (§5.6), and SQL gets the Cosmos retention defaults (§5.8) | Released. Requires Spec 050's SQL cleanup to have shipped in an earlier minor |

## 10. Documentation

- `docs/storage-providers.md`. The "Choosing a provider" guidance ships ahead of this spec, with
  external SQL's Resolver gap stated. Each phase then updates it:
  - Phase 1: external SQL works;
  - Phase 2: SQL's app authentication;
  - Phase 3: the tier flag and the pipelines;
  - the flip release: the default.

  Phase 0 replaces the estimates with measurements.
- `docs/cli.md`: `--sql-sku`, `--sql-entra-admin`, `--sql-entra-only`; the tags and the resolution
  order; the temporary firewall rule; the default in `--storage-provider`'s description.
- `docs/azure-requirements.md`: SQL Server as the default store; the Entra administrator
  requirement for whoever runs `nb`; no secrets on either path.
- `docs/deployment.md` and the getting-started pages: examples without `--storage-provider` mean
  SQL after the flip, and examples that need Cosmos say so.
- `docs/private-networking.md`: the SQL user step runs in-network, after its connectivity check.
- Release notes for each minor and the major (§5.6).

## 11. Residual risks

1. **Unmeasured SQL capacity.** Until Phase 0 runs, nobody knows how much load S0 carries, so a busy
   new deployment could start too small. The docs say how to read DTU and move up.
2. **Entra administrator access.** Whoever runs `nb`, locally or in a pipeline, must be the server's
   administrator or in the administrator group. The error message says how to grant it.
3. **The SID path** depends on Azure SQL accepting `WITH SID … TYPE = E` for managed identities from
   a service principal caller (§14 item 3). If it doesn't, the fallback is the server identity with
   Graph permissions, granted once by a privileged Entra role. It is documented as a manual
   prerequisite.
4. **A temporary firewall rule** opens the server to one address for the length of the user step.
   It is removed in a `finally` and swept on the next run.
5. **Operators who rely on `nb container`** get no CLI on SQL (§5.7).
6. **Retention defaults on existing SQL deployments.** At the flip, an existing SQL deployment
   whose Owner hasn't saved periods starts deleting old data (§5.8). The release notes, the
   warning minors and the Retention page's "Nothing expires" banner (Spec 050 §5.5) all point to
   this ahead of time.

## 12. Decisions

1. **SQL Server becomes the default for new deployments**, in the next major, after warning minors
   (§5.6).
2. **Provider intent comes from the apps' settings**, is recorded as tags, and is refused when
   ambiguous or when discovery fails (§5.1).
3. **External SQL is fixed for the Resolver** in the first release (§5.2).
4. **Provisioned SQL moves to managed identity before the flip**, in stages, with users created by
   SID (§5.3).
5. **The deployed SQL SKU is pinned whole**, and `--sql-sku` is the only way `nb` resizes (§5.4).
6. **SQL retention comes from Spec 050**, and SQL takes the Cosmos defaults at the flip (§5.8).

## 13. Open questions for the repo owner

1. **Release vehicle for the flip:** the next major (proposed, §5.6), or a minor under a recorded
   versioning exception?
2. **SQL authentication on existing provisioned servers:** off only with `--sql-entra-only`
   (proposed), or off by default once their apps have moved to managed identity?

## 14. Facts to verify (Phase 0)

On temporary resource groups in a non-production subscription, deleted afterwards:

1. **Load per delivery.** Drive steady traffic with Settings → Simulation on one deployment per
   provider, at three rates. Record Cosmos RU per delivery, by container, and SQL DTU percentage per
   delivery rate on S0, S1 and S2. The results replace the docs' 50-RU assumption and the suggested
   tiers.
2. **External SQL today.** Confirm that an external-SQL deployment's Resolver gets no
   `SqlConnection` and fails, and that §5.2 fixes it.
3. **Users by SID.** From a user and from a service principal that is the server's Entra
   administrator, with no Graph permission and no server identity: `CREATE USER … WITH SID = <client
   id>, TYPE = E` for a Function App's and a Web App's system-assigned identity, then both apps
   connecting with `Active Directory Managed Identity` and DbUp `AutoApply` succeeding. Confirm how
   to get each identity's client id from ARM.
4. **The administrator's object id** from the token's `oid` for a user and a service principal.
5. **Staged move** of an existing SQL deployment under Simulation traffic, interrupted at each stage
   and resumed: no failed or dead-lettered message.
6. **Firewall rule.** The refused-connection error carries the client address; the rule is added,
   used and removed.
7. **`az sql server ad-only-auth enable`** on an existing server, and the template's later runs
   leaving it on.
8. **SKU pinning.** A database resized in the portal to S3, Premium and a vCore SKU keeps its full SKU
   across `infra apply`.
9. **Resolution** against resource groups holding both stores, a leftover Cosmos account with SQL
   apps, and an external-SQL deployment.
10. **Prices** for S3 and S4 and the other figures in the docs, against the Azure pricing page.

## 15. References

- [Storage providers: choosing a provider](../../storage-providers.md#choosing-a-provider).
- Spec 034 (private networking): §5.12 identity hardening, R5 private operations.
- Spec 047 (Cosmos capacity modes).
- ADR-010 (pluggable message storage).
- [Azure SQL DTU purchasing model](https://learn.microsoft.com/azure/azure-sql/database/service-tiers-dtu).
- [Microsoft Entra authentication for Azure SQL](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-overview),
  [Entra-only authentication](https://learn.microsoft.com/azure/azure-sql/database/authentication-azure-ad-only-authentication),
  [creating a server with Entra-only authentication](https://learn.microsoft.com/azure/azure-sql/database/authentication-azure-ad-only-authentication-create-server),
  and [service principals as callers](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-service-principal).
- [Azure SQL firewall rules](https://learn.microsoft.com/azure/azure-sql/database/firewall-configure).

## 16. Review trail

- 2026-11-14: drafted.
- 2026-11-14: a Codex `review-change` pass made 12 findings. Claude checked each against the code
  and none was refuted. Changes:
  - Discovery failures are refused instead of reading as an empty resource group (§5.1).
  - The provider is resolved from the apps' settings, not resource names; `topology apply`
    resolves before any change and has no Cosmos fallback (§5.1).
  - External SQL now reaches the Resolver, a pre-existing bug (§2, §5.2). The docs state the gap
    until then.
  - Managed identity is staged so it works on existing servers. Users are created by SID, avoiding
    Graph permissions for pipeline callers. Public mode adds and removes a temporary firewall rule,
    and private mode checks SQL connectivity. The administrator's id comes from the token's `oid`.
    Turning SQL authentication off on an existing server is an explicit `ad-only-auth enable`. The
    unsupported claim that the apps retry their first connection is gone (§5.3).
  - The whole SKU is pinned, and the template parameter has no `@allowed` list (§5.4).
  - The flip moves to the next major, per `docs/versioning.md`, unless the repo owner records an
    exception (§5.6).
- 2026-11-15: added the SQL retention gap. A SQL deployment never deletes data, so the default
  would grow until S0's 250 GB stops writes. Spec 050 adds the cleanup, and this spec gives SQL the
  Cosmos retention defaults at the flip (§2, §5.8, §6, §9, §11).
