# Spec 048 (SQL Server as the default provider): implementation plan

Status: **draft** (2026-11-14), revised the same day with the spec after a Codex review. Nothing
is implemented. Implements [Spec 048](../spec/048-sql-server-default-provider/spec.md). Planned
against master `015fcb0e`. Where this plan and the spec disagree, the spec wins.

## Delivery

NimBus doesn't stack PRs, so each release is one PR with a commit per step. Each step adds its
failing tests first (RED, then GREEN) and follows [CONTRIBUTING.md](../../CONTRIBUTING.md) and
Conventional Commits.

| PR | Steps | Ships as |
|---|---|---|
| 1 | Phase 0 results; provider intent; external SQL for the Resolver | A 4.x minor. It fixes a bug users hit today |
| 2 | SQL managed identity; SQL tier; pipelines; the warning | The next 4.x minor |
| 3 | The default flips; docs | The next major, or a minor under a versioning exception recorded by the repo owner (spec §5.6, §13) |

PR 3 opens only after PR 2 has shipped.

## Step 0: Phase 0

- **Environments.** Temporary resource groups in the non-production "Visual Studio Enterprise –
  MPN" subscription, deployed with today's `nb`, deleted at the end:
  - one Cosmos deployment and one provisioned-SQL deployment;
  - one external-SQL deployment against a separate server;
  - one Cosmos deployment moved to SQL, so the resource group holds both stores.
- **Load per delivery (spec §14 item 1).** Drive traffic with Settings → Simulation at three steady
  rates, ten minutes each.
  - Cosmos: read `TotalRequestUnits` by `CollectionName` from Azure Monitor and divide by
    deliveries.
  - SQL: read `dtu_consumption_percent` on S0, then S1 and S2.
- **External SQL today (item 2).** Confirm that the Resolver has no `SqlConnection` and fails to
  start.
- **SID users, `oid`, firewall error, `ad-only-auth` (items 3, 4, 6, 7).** Check each by hand with
  `sqlcmd` and `az` before any code: a user principal and a service principal, neither with Graph
  permissions.
- **Prices (item 10).**
- **Record the results** in `docs/spec/048-sql-server-default-provider/phase0-results.md`.
  - Update the price table in `docs/storage-providers.md`, replacing the 50-RU assumption and the
    suggested tiers.
  - If item 3 fails, stop and revise spec §5.3 to the Graph-permission fallback before step 2.

## PR 1

### Step 1: provider intent (spec §5.1)

**Code.**

- `StorageIntent` beside `NetworkIntent`: the tags `nimbus-storage-provider` and
  `nimbus-sql-mode`, parsing and `ToTags`.
- A strict `DeploymentDiscovery`. It throws on a failed `az resource list`, on output that isn't a
  JSON array, and on a failed app-settings read. It returns:
  - the resources: Cosmos account, SQL server (effective name), Resolver Function App, WebApp;
  - each app's `NimBus__StorageProvider`, `CosmosAccountEndpoint` and the server named in
    `SqlConnection`. Only the host name is kept; the setting's value never leaves the method or
    reaches a log.

  The lenient `DiscoverExistingLocationsAsync` stays for location pins only.
- `StorageResolver`: flag › tags › apps' settings › resources › default, with the refusals of the
  spec's tables. It is a pure function over `DeploymentDiscovery`'s result, so every row is a unit
  test.
- `CliHelpers.ParseStorageProvider` returns `StorageProviderChoice?`, with no default.
- Callers:
  - `InfrastructureDeployer` resolves first and writes the tags, through the network-tag merge
    helper, before the first change;
  - `SetupCommand` resolves once and passes the result to its steps;
  - `TopologyCommands` resolves before `ServiceBusTopologyProvisioner.ApplyAsync`
    (`TopologyCommands.cs:114`), with no default and no Cosmos fallback.

**Tests.**

- `StorageIntentTests`.
- `StorageResolverTests`: one test per row of spec §5.1 and §6.
- `DeploymentDiscoveryTests`: failed listing, malformed JSON, failed settings read, and the setting
  value never surfacing.
- Deployer and topology tests: a refusal happens before any `az deployment` and before any Service
  Bus change, and the tags are written before the first change.

### Step 2: external SQL for the Resolver (spec §5.2)

Done ahead of the rest of PR 1, for issue #391. The live check below is still open.

- `deploy.core.bicep`:
  - a secure `sqlConnectionString` parameter;
  - `sqlResolverSecretSettings` sets `SqlConnection` from it when `sqlMode == 'external'`.
- `InfrastructureDeployer`: pass `ResolveSqlConnectionString`'s value (`:419`) to the core
  deployment as a secure parameter, the same way the SQL admin password goes
  (`AzureDeploymentCommand.AddSecureParameter`).
- **Tests.**
  - Template: the Resolver gets `SqlConnection` in both SQL modes.
  - Deployer: the value is a secure parameter, not a process argument.
- **Docs.** Remove the known-gap paragraphs from `docs/storage-providers.md` and `docs/cli.md`, and
  restore external mode in the guidance.
- **Live.** Redeploy the Phase 0 external-SQL deployment; it processes Simulation traffic.

### Step 3: PR 1 release notes

- **Compatibility:**
  - the provider tags;
  - deployments that can't be resolved now stop with a message, where they used to fall back to
    Cosmos;
  - external SQL now configures the Resolver.
- Release following [versioning](../versioning.md#cutting-a-release).

## PR 2

### Step 4: SQL managed identity (spec §5.3)

**Template.**

- `azureSql.bicep`:
  - an `administrators` block from `entraAdminObjectId`, `entraAdminLogin` and
    `entraAdminPrincipalType`;
  - `azureADOnlyAuthentication: true` only when creating a new server;
  - `administratorLogin` and `administratorLoginPassword` only while SQL authentication is on.
- `deploy.core.bicep` and `deploy.webapp.bicep`:
  - a `sqlAppAuth` parameter (`password` or `managed-identity`) that picks the `SqlConnection`
    form;
  - the admin password is read only for `password`.

**CLI.**

- `--sql-entra-admin` and `--sql-entra-only`.
- The caller's object id comes from the `oid` claim of `az account get-access-token`. The JWT
  payload is decoded locally and never logged.
- A `SqlNetworkAccess` helper:
  - **Public mode.** Try to connect. On the "client with IP address … is not allowed" error, add
    `nimbus-nb-<UTC timestamp>` for that address, and remove it in `finally`. Also delete
    `nimbus-nb-*` rules older than an hour at the start of the run.
  - **Private mode.** Check that the SQL private endpoint resolves and accepts a connection before
    the user step.
- A `SqlContainedUserProvisioner`:
  - reads each app's managed-identity client id from ARM (spec §14 item 3 settles the call);
  - connects with an Entra token from `DefaultAzureCredential`;
  - runs `IF NOT EXISTS … CREATE USER [<name>] WITH SID = <client id>, TYPE = E` and the three role
    memberships, then verifies both users in `sys.database_principals`.
- A `nimbus-sql-auth` tag (`managed-identity` once done), and the stage order of spec §5.3:
  - new server: core deploy, then WebApp deploy, then users, then the tag;
  - existing server: both deploys with `sqlAppAuth=password`, then users, then both deploys with
    `managed-identity`, then the tag.
- `--sql-entra-only` runs `az sql server ad-only-auth enable`, and is refused until the tag says
  `managed-identity`. Later runs pin the state from `ad-only-auth get`.
- `--sql-admin-login` and `NIMBUS_SQL_ADMIN_PASSWORD` are required only while SQL authentication is
  on.

**Tests.** Every managed-identity bullet in spec §8, on the fake `IAzureCliRunner`. The
provisioner and the network helper get a fake SQL seam.

**Live (spec §14 items 3 to 7).**

- A new SQL deployment from a workstation (public mode), and one from `deploy.yml`.
- The staged move of the Phase 0 provisioned deployment under Simulation traffic, interrupted once
  at each stage and resumed.
- `--sql-entra-only` on it afterwards.

### Step 5: SQL tier (spec §5.4)

- `azureSql.bicep`: the SKU becomes an object parameter with no `@allowed` list, and
  `deploy.core.bicep` passes it through.
- `--sql-sku`, validated by the CLI against `Basic` and `S0` to `S4`. An existing database's whole
  SKU (`az sql db show --query sku`) is passed back unchanged. New databases get S0.
- **Tests:** validation; pass-through; an existing S3, Premium and vCore SKU kept whole.
- **Live (item 8):** resize the Phase 0 database to S3, then to a vCore SKU, running `infra apply`
  after each.
- **Docs:** replace the "scale up again after each run" guidance with `--sql-sku`.

### Step 6: pipelines (spec §5.5)

- `deploy.yml` and `pipelines/azure-pipelines-deploy.yml`: the four optional inputs and the optional
  `NIMBUS_SQL_CONNECTION_STRING` secret, each passed only when set.
- **Tests:** a script check that empty inputs add no arguments.
- **Live:** dispatch `deploy.yml` with `storage-provider: sqlserver` against a fresh resource group,
  and without inputs against the Phase 0 Cosmos deployment (still Cosmos).

### Step 7: PR 2 docs and release notes

- **Docs** (spec §10):
  - the authentication, tier and pipeline updates to the guidance;
  - `docs/cli.md`, `docs/azure-requirements.md` and `docs/private-networking.md`.
- **Release notes, Compatibility:**
  - SQL deployments move to managed identity on their next `infra apply`, with one extra
    deployment pass;
  - the Entra administrator requirement;
  - the temporary firewall rule;
  - the warning about the coming default.

## PR 3: the flip

- `ParseStorageProvider` has no default; `StorageResolver` supplies `sqlserver` and `provision`
  for a new deployment. The warning text is reversed (spec §5.6).
- `deploy.core.bicep`'s `storageProvider` default becomes `sqlserver`, and the pinning test moves
  with it.
- Docs:
  - examples in `docs/deployment.md` and the getting-started pages;
  - `--storage-provider`'s description in `docs/cli.md`;
  - the guidance's "at a glance" row.
- Release notes: under ⚠️ Breaking for a major, or Compatibility for a minor under the recorded
  exception.

## Verification gate (each PR)

```bash
dotnet build src/NimBus.slnx -c Release      # pin SDK 10.0.4xx if the default SDK is newer
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix website ci && npm --prefix website run build
```

- Record each step's live checks in the PR description.
- No WebApp UI changes, so no screenshot.
- The conformance suites don't change; CI still runs them without skips.

## Out of scope

- Porting `nb container` commands or Copy Endpoint Data to SQL (spec §5.7).
- Spec 047, which proceeds independently for Cosmos deployments.
