# Plan: real support for `--resource-name-postfix`

## Context

`nb infra apply` / `nb setup` accept `--resource-name-postfix`, but it is a no-op: the option was
carried over from the legacy pipeline scripts, the bicep templates never consumed it, and
`src/NimBus.CommandLine/InfrastructureDeployer.cs` printed
"Ignoring …". Customers whose existing resources carry a postfix (e.g. `func-sol-dev-resolver-xyz`)
cannot point NimBus at them; a deploy would create a parallel set of unpostfixed resources.

Goal: one postfix, appended **at the very end** of the resource names the legacy pipeline
postfixed (the legacy pipeline's `deploy/bicep/deploy.core.bicep`), applied
consistently by both bicep templates and every CLI command, and **recorded as a resource-group
tag** so `nb topology apply`, `nb deploy apps` and endpoint-container provisioning find the same
resources without the flag being repeated.

## Naming rule

Postfix `p` (normalized: trimmed, a leading `-` dropped, lower-cased; only `[a-z0-9]` allowed,
otherwise fail with a clear message — no silent stripping, because a silently changed name would
miss the existing resource).

The legacy template postfixed only some names (L14–40 there): Service Bus, Cosmos, every
function app, the WebApp and the msgstore storage account — but **not** App Insights
(`ai-…-global-tracelog`) or the App Service Plans (`asp-…-management` / `asp-…-core`). The
NimBus `st{s}{e}func` account is not postfixed either; to reuse the existing legacy account
(`st{s}{e}msgstore{p}`) a new `--func-storage-account-name` override is added (§2a). NimBus must
reproduce exactly that split, or it would miss the existing plans and App Insights and create
duplicates.

| Resource | Today | With postfix `xyz` | Legacy |
|---|---|---|---|
| Service Bus | `sb-{s}-{e}` | `sb-{s}-{e}-xyz` | postfixed |
| Cosmos | `cosmos-{s}-{e}` | `cosmos-{s}-{e}-xyz` | postfixed |
| Resolver | `func-{s}-{e}-resolver` | `func-{s}-{e}-resolver-xyz` | postfixed |
| WebApp | `webapp-{s}-{e}-management` | `webapp-{s}-{e}-management-xyz` | postfixed |
| SQL server | `sql-{s}-{e}` | `sql-{s}-{e}-xyz` | none in legacy; postfixed because it is globally unique like the others |
| App Insights | `ai-{s}-{e}-global-tracelog` | **unchanged** | not postfixed |
| Plans | `asp-{s}-{e}-management/-core` | **unchanged** | not postfixed |
| Func storage | `st{s}{e}func` | **unchanged** (set via `--func-storage-account-name`) | legacy `st{s}{e}msgstore{p}` |

Keep the rule in one place in C# (a "postfixed" flag per name in `NamingConventions.Build`) and
mirror it in bicep with a comment pointing at the legacy layout, so the split is a deliberate
compatibility decision and not something a later cleanup "fixes".

Explicit overrides still win over the postfix: `--service-bus-namespace-name`, `--sql-server-name`,
and the new `--func-storage-account-name`.
Unchanged: database names (`MessageDatabase`), DNS zones. Private-endpoint names derive from
resource names via `PrivateEndpointNames`, so they follow automatically.

Fail fast (before login) when a postfixed name breaks an Azure limit: Service Bus (≤50),
Cosmos (≤44), SQL (≤63), sites (≤60). The storage override is validated separately (§2a).

Out of scope: legacy-only resources NimBus no longer deploys (the cosmos,
event, heartbeat and alerts function apps, the webjob, the Event Grid topic). The deployment
leaves them alone; their removal is a separate migration task.

## Changes

### 1. Naming — [CommandSupport.cs](src/NimBus.CommandLine/CommandSupport.cs)
- `NamingConventions.NormalizePostfix(string?)` → `string?` (validation above).
- `Build(solutionId, environment, serviceBusNamespaceName, resourceNamePostfix = null)`: append
  `-{p}` to the Service Bus, Cosmos, SQL, Resolver and WebApp names only (table above); App
  Insights, both plans and the func storage account stay as today; namespace override applied
  after.
- Add `string? ResourceNamePostfix` to `DeploymentNames` (needed by owner tag, bicep args).
- `NamingConventions.ValidateLengths(DeploymentNames)`.
- Add optional `ResourceNamePostfix` to `TopologyOptions` and `AppDeploymentOptions`
  (default null, keeps existing call sites compiling).

### 2. Recorded postfix — new tag `nimbus-resource-name-postfix`
- Constant next to `ServiceBusNamespaceTag` in [NetworkIntent.cs](src/NimBus.CommandLine/NetworkIntent.cs)
  (or a small `ResourceNameIntent` helper if it reads cleaner — not part of the network state).
- Resolution rule, shared helper `NamingConventions.ResolvePostfix(explicit, recorded)`:
  - explicit only → use it; recorded only → use it and print "Using the resource-name postfix
    'xyz' recorded on '<rg>'"; both and equal → fine;
  - both and **different → fail** ("would deploy a second, differently named set of
    resources"). Renaming is not an in-place operation in Azure.
- Write the tag in `InfrastructureDeployer` together with the network-intent tags (before the
  core deployment, same "interrupted run converges" reasoning), via the existing
  `TagUpdateArguments` merge path. Only written when a postfix is in effect.

### 2a. Function storage account override — `--func-storage-account-name`
Reuses the existing legacy storage account (e.g. `st{s}{e}msgstoreavk`) as the Resolver's
`AzureWebJobsStorage` / content-share / Flex deployment-container account.
- Option on `nb infra apply` and `nb setup`; `InfrastructureOptions.FuncStorageAccountName`.
  Help text: "Name of an existing storage account to use for the Resolver function app instead
  of st{solution}{env}func, e.g. the legacy msgstore account."
- Validate before login: 3–24 chars, lowercase letters and digits only (no normalization —
  a changed name would miss the account).
- `NamingConventions.Build` takes it as an override of `FuncStorageAccountName` (same pattern as
  the Service Bus namespace override), so location pinning
  ([InfrastructureDeployer.cs:482](src/NimBus.CommandLine/InfrastructureDeployer.cs#L482)) and
  private endpoints (`pe-{name}-{groupId}`) follow automatically.
- Recorded as RG tag `nimbus-func-storage-account` with the same resolution rule as the
  postfix (explicit/recorded/fail on mismatch). Only infra apply consumes it today, but
  recording it means a later `nb infra apply` without the flag does not create a new
  `st…func` account and move the Resolver onto it.
- Bicep ([deploy.core.bicep](deploy/bicep/deploy.core.bicep)): `param funcStorageAccountName string = ''`;
  `var effectiveFuncStorageAccountName = empty(funcStorageAccountName) ? 'st…func' : funcStorageAccountName`,
  used at L369, L576, L703 (mirrors `sqlServerName` / `effectiveSqlServerName`).
- **Don't silently mutate the reused account**: [storageaccount.bicep](deploy/bicep/templates/storageaccount.bicep)
  hard-codes `kind: 'StorageV2'` and `sku: Standard_LRS`; redeploying over a legacy account
  with another SKU or kind would change its replication or upgrade `Storage` → `StorageV2`
  (irreversible). Discover the existing account's `sku.name` and `kind`
  (`az storage account show --query "{sku:sku.name, kind:kind}"`), pass them as new
  `funcStorageSkuName` / `funcStorageKind` params (template defaults stay `Standard_LRS` /
  `StorageV2`), and print a "Keeping the existing storage account … (sku, kind)" line, like
  the plan pinning. Fail with a clear message if the kind is `BlobStorage`/`FileStorage`,
  which cannot host a function app. The template's public-network-access/ACL settings apply to
  the reused account as to any NimBus account — call that out in the docs.
- Existing data in the legacy account (msgstore blobs/queues) is untouched: the template only
  adds the `app-package-resolver` container and the content share.

### 3. `InfrastructureDeployer` — [InfrastructureDeployer.cs](src/NimBus.CommandLine/InfrastructureDeployer.cs)
- `ApplyAsync`: validate the explicit postfix before login (with the other syntax checks at
  L32–40); delete the "Ignoring …" block (L58–61); after `ResourceGroupTags.ReadAsync` resolve the
  postfix against the tag and pass it to `NamingConventions.Build` (L72) + `ValidateLengths`.
- `DeployCoreInfrastructureAsync` / `DeployWebAppInfrastructureAsync`: add
  `resourceNamePostfix={p}` only when set (unset → parameters identical to today, like
  `AddServiceBusParameters`).
- Existing-resource discovery (locations, plans, Service Bus, content share) already keys on
  `names.*`, so pinning works for the postfixed resources with no further change.

### 4. Bicep — [deploy.core.bicep](deploy/bicep/deploy.core.bicep), [deploy.webapp.bicep](deploy/bicep/deploy.webapp.bicep)
- `param resourceNamePostfix string = ''` with a description.
- `var nameSuffix = empty(resourceNamePostfix) ? '' : '-${toLower(resourceNamePostfix)}'`.
- Append it to `sbNamespace`, `cosmosAccountName`, `defaultSqlServerName`,
  `resolverFunctionAppName` (core L198–213) and `sbNamespace`, `managementWebAppName`,
  `cosmosAccountName` (webapp L92–118), plus the default `alertMailWebAppUrl` (core L449) and the
  `nimbus-deployment` ownership tag (core L242, webapp L310).
- Leave `appInsightsName`, `managementAppServicePlanName`, `coreAppServicePlanName`,
  `appServicePlanName` (webapp) and `funcStorageAccountName` untouched.
- The Elastic Premium content-share default derives from `resolverFunctionAppName`; a new
  postfixed Resolver gets a postfixed share name, and an existing one keeps its share through
  `existingResolverContentShare` as today.
- Packaged templates are embedded copies of these files (`BicepTemplateProvider`, ADR-015) —
  confirm they are picked up from `deploy/` at build time and need no separate edit.

### 5. Other consumers
- [PrivateNetworkCleanup.cs:25](src/NimBus.CommandLine/PrivateNetworkCleanup.cs#L25) `OwnerValue`:
  `{s}-{e}` + `-{p}` when set, matching the bicep tag.
- [ServiceBusTopologyProvisioner.cs:103](src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs#L103)
  `ResolveNamesAsync`: read the postfix tag alongside the namespace tag (one `group show` query
  for both tags) and apply the same resolution rule. Reuse it (or a shared
  `RecordedNames` helper) from:
  - [AppDeploymentService.cs:26](src/NimBus.CommandLine/AppDeploymentService.cs#L26) — today it
    ignores even the recorded namespace;
  - [EndpointContainerProvisioner.cs:36](src/NimBus.CommandLine/EndpointContainerProvisioner.cs#L36).
- Commands: in [InfraCommands.cs](src/NimBus.CommandLine/Commands/InfraCommands.cs) and
  [SetupCommand.cs](src/NimBus.CommandLine/Commands/SetupCommand.cs) keep the compatibility note
  and add which resources get the postfix, in one shared constant so both commands say the
  same thing:
  > "For compatibility with the legacy pipeline scripts. Appended as '-<value>' to the Service
  > Bus namespace, Cosmos DB account, SQL server, Resolver function app and management WebApp
  > names only. App Insights, the App Service plans and the function storage account are not
  > postfixed; use --func-storage-account-name to reuse an existing storage account."

  `nb setup` passes the postfix into its topology and app-deploy options too.
- Runtime note: when a postfix is in effect, `nb infra apply` prints the same split once,
  listing the actual names ("Resource-name postfix 'avk' applies to: sb-…-avk, cosmos-…-avk,
  func-…-resolver-avk, webapp-…-management-avk. Not postfixed: ai-…, asp-…-management,
  asp-…-core, st…"), so a pipeline log shows which existing resources will be matched. Add `--resource-name-postfix` to `nb topology apply` and `nb deploy apps` (in
  [DeployCommands.cs](src/NimBus.CommandLine/Commands/DeployCommands.cs)) as an explicit
  override for runs where tags can't be read/written.

### 6. Docs
- Update the CLI/deployment guide under `docs/` that documents `nb infra apply` options
  (find via grep for `resource-name-postfix` / `service-bus-namespace-name`), including the
  naming table and the "cannot change postfix in place" rule. State explicitly, next to the
  option, which resources are and are not postfixed (same wording as the help text), and
  document `--func-storage-account-name` with the legacy msgstore example and the SKU/kind and
  network-access behavior.

## Tests (MSTest, `tests/NimBus.CommandLine.Tests`)
- `NamingConventionsTests`: every name with/without postfix; App Insights, both plans and func
  storage are unchanged by a postfix (regression guard for the legacy split); leading `-`
  trimmed; invalid chars rejected; namespace override beats postfix; length violations.
- A test pinning the legacy names for a sample (`sb-…-avk`, `cosmos-…-avk`,
  `func-…-resolver-avk`, `webapp-…-management-avk`, `asp-…-core`, `ai-…-global-tracelog`).
- Func storage override: replaces the derived name; invalid names rejected before login;
  passed as `funcStorageAccountName`; existing SKU/kind discovered and passed through;
  unsupported kind fails; tag recorded and reused; mismatch fails; nothing passed when unset.
- Help text of `infra apply` and `setup` contains the postfix resource list
  (`CliApplicationFactoryTests` already asserts on option text).
- `InfrastructureDeployer*Tests` (follow the fake `IAzureCliRunner` pattern in
  `InfrastructureDeployerNetworkTests`): postfix passed to both deployments; no parameter
  when unset (regression for existing args); tag written; recorded tag used when flag absent;
  mismatch fails before any deployment; discovery/pinning queries use postfixed names;
  "Ignoring" message gone.
- Topology / app-deploy / endpoint-container: recorded postfix resolves the right names.
- `PrivateNetworkCleanup` owner value with postfix.
- Bicep: `az bicep build` on both entry templates (or the existing template build test if one
  exists) to catch syntax errors.

## Verification
- `dotnet build src/NimBus.slnx -c Release` and `dotnet test src/NimBus.slnx -c Release --no-build`.
- `az bicep build --file deploy/bicep/deploy.core.bicep` / `deploy.webapp.bicep`.
- Optional live check: `az deployment group what-if` (or `nb infra apply` against a scratch RG)
  with `--resource-name-postfix xyz` against an RG holding postfixed resources — expect
  Modify/NoChange on the existing resources, no Create of unpostfixed ones.
- Website build if a docs page changes: `npm --prefix website run build`.
