# Spec 048 (SQL Server as the default provider): implementation plan

Status: **draft** (2026-11-14). Nothing is implemented. Implements
[Spec 048](../spec/048-sql-server-default-provider/spec.md). Planned against master `015fcb0e`.
Where this plan and the spec disagree, the spec wins.

## Delivery

NimBus doesn't stack PRs, so each release is one PR with a commit per step. Each step adds its
failing tests first (RED, then GREEN) and follows [CONTRIBUTING.md](../../CONTRIBUTING.md) and
Conventional Commits.

| PR | Steps | Ships as |
|---|---|---|
| 1 | Phase 0 results; provider intent; SQL managed identity; SQL tier; pipelines; the warning | Release A (a 4.x minor) |
| 2 | The default flips; docs | Release B (the next minor) |

PR 2 opens after Release A has shipped.

## Step 0: Phase 0

- **Environments.** Two temporary resource groups in the non-production "Visual Studio
  Enterprise – MPN" subscription, one per provider, deployed with today's `nb`. Delete them at the
  end.
- **Load per delivery (spec §14 item 1).**
  - Drive traffic with Settings → Simulation at three steady rates, ten minutes each.
  - Cosmos: read RU per container from Azure Monitor (`TotalRequestUnits` split by
    `CollectionName`) and divide by deliveries.
  - SQL: read `dtu_consumption_percent` on S0, then S1 and S2.
- **Detection (item 5).** Create a resource group holding both a Cosmos account and a SQL server.
  This check needs the Phase 1 code and runs again in step 1's live check.
- **Prices (item 6).**
- Items 2 to 4 need the new code and run in steps 2 and 3.
- **Record the results** in `docs/spec/048-sql-server-default-provider/phase0-results.md`. Update
  the price table in `docs/storage-providers.md` from them: replace the 50-RU assumption and the
  suggested tiers.

## Step 1: provider intent (spec §5.1)

**Code.**

- `StorageIntent` beside `NetworkIntent`: the tags `nimbus-storage-provider` and
  `nimbus-sql-mode`, parsing and `ToTags`.
- `CliHelpers.ParseStorageProvider` returns `StorageProviderChoice?`, with no default. The
  options carry the nullable value.
- `InfrastructureDeployer`:
  - resolves flag › tag › detection › default before the pins, using `existingLocations` (already
    listed at `InfrastructureDeployer.cs:78`) to find the Cosmos account, the SQL server and the
    apps;
  - refuses contradictions and ambiguity with messages that name the flag;
  - writes the tags through the network-tag merge helper before `DeployCoreInfrastructureAsync`.
- `TopologyCommands` and `EndpointContainerProvisioner`: the same resolution without the default,
  so a SQL deployment no longer needs the flag.
- `SetupCommand` forwards the resolved value to its steps.
- The Release A warning (spec §5.5) is printed on a new deployment without the flag.

**Tests.** `StorageIntentTests`. An `InfrastructureDeployerStorageIntentTests` class on the fake
`IAzureCliRunner`, with one test per row of the spec's §5.1 table and §6 table.
`TopologyCommands` resolution tests.

**Live.** Run `infra apply` without flags against each Phase 0 resource group: tagged, nothing
else changed (`az deployment group what-if` first). Then run it against the resource group
holding both stores: refused.

## Step 2: SQL managed identity (spec §5.2)

**Template.**

- `azureSql.bicep`:
  - an `administrators` block from new `entraAdminObjectId`, `entraAdminLogin` and
    `entraAdminPrincipalType` parameters, and `azureADOnlyAuthentication`;
  - `administratorLogin` and `administratorLoginPassword` only when SQL authentication stays on.
- `deploy.core.bicep`:
  - `SqlConnection` becomes the password-free managed-identity string;
  - the SQL admin password is read only when SQL authentication stays on, never for app settings.

**CLI.**

- `--sql-entra-admin` and `--sql-entra-only`. The caller's object id comes from `az ad
  signed-in-user show`, or from `az account show` for a service principal.
- For an existing server, read its current mode from `az sql server ad-only-auth get` and pin it.
- A `SqlContainedUserProvisioner` connects with `Microsoft.Data.SqlClient` and an Entra token from
  `DefaultAzureCredential` (the CLI already resolves credentials this way, `CommandRunner.cs`). It
  runs `IF NOT EXISTS … CREATE USER … FROM EXTERNAL PROVIDER` and the three role memberships for
  `ResourceNaming`'s Resolver and WebApp names.
- **Ordering.** On an existing database, run the user step, then deploy. On a new database,
  deploy, then run the user step.
- **Private mode.** Behind the existing in-network check.
- `--sql-admin-login` and `NIMBUS_SQL_ADMIN_PASSWORD` are required only for a server that keeps SQL
  authentication.

**Tests.**

- Template tests: no password in `SqlConnection`; the `administrators` block.
- `SqlContainedUserProvisioner` tests over a fake command seam: the exact T-SQL, idempotent.
- Deployer tests: the ordering on new and existing databases, the pinning, and that flags are
  required only where they apply.

**Live (spec §14 items 2 and 3).**

- A new SQL deployment end to end. Record how long the apps' first connections fail.
- Move the Phase 0 SQL deployment while Simulation traffic runs: no failed or dead-lettered
  message.

## Step 3: SQL tier and pipelines (spec §5.3, §5.4)

- `azureSql.bicep` allows `S3` and `S4`. `deploy.core.bicep` passes `sqlSkuName`.
- `--sql-sku`. An existing database's tier is read with `az sql db show --query sku.name` and
  pinned. New databases get S0.
- `deploy.yml` and `pipelines/azure-pipelines-deploy.yml`: the four optional inputs and the
  optional `NIMBUS_SQL_CONNECTION_STRING` secret, passed only when set.

**Tests.**

- Tier pass-through and pinning, including a tier outside the allowed list.
- A pipeline check that the new inputs are passed only when set.

**Live (item 4).** Resize the Phase 0 database in the portal, then run `infra apply`: the size
survives. Dispatch `deploy.yml` with `storage-provider: sqlserver` against a fresh resource group.

## Step 4: Release A

- **Docs** (spec §10): the authentication and tier updates to the guidance, `docs/cli.md`,
  `docs/azure-requirements.md` and `docs/private-networking.md`.
- **Release notes** under Compatibility:
  - the warning;
  - the provider tags;
  - SQL deployments moving to managed identity on their next `infra apply`;
  - external-SQL deployments having to pass the flags once.
- **Release** following [versioning](../versioning.md#cutting-a-release).

## PR 2: Release B

- The default for a new deployment becomes `sqlserver` with `provision`, and the warning text is
  reversed (spec §5.5).
- Docs:
  - examples in `docs/deployment.md` and the getting-started pages;
  - `--storage-provider`'s description in `docs/cli.md`;
  - the guidance's "at a glance" row.
- A template test keeps `deploy.core.bicep`'s default at `cosmos`.
- Release notes under Compatibility.

## Verification gate (each PR)

```bash
dotnet build src/NimBus.slnx -c Release      # pin SDK 10.0.4xx if the default SDK is newer
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix website ci && npm --prefix website run build
```

- The live checks of each step are recorded in the PR description.
- No WebApp UI changes, so no screenshot.
- The conformance suites don't change; CI still runs them without skips.

## Out of scope

- Porting `nb container` commands or Copy Endpoint Data to SQL (spec §5.6).
- Spec 047, which proceeds independently for Cosmos deployments.
