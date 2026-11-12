# Deploying NimBus

How to stand up the NimBus platform (Service Bus, message store, resolver Function App, management WebApp) in your own Azure subscription — from a laptop, from CI/CD pipelines, or by driving the Bicep templates directly.

## Choose a path

| Path | Best for | What you run |
|---|---|---|
| [One command](#path-1-one-command) | Trying NimBus, dev environments | `dnx Akaule.NimBus.CommandLine -- setup ...` — no clone needed |
| [GitHub Actions](#path-2-github-actions) | Teams on GitHub, OIDC (no stored secrets) | The included `Deploy NimBus` workflow |
| [Azure DevOps](#path-3-azure-devops) | Enterprises on Azure DevOps | The included `pipelines/azure-pipelines-deploy.yml` |
| [Raw Bicep](#path-4-raw-bicep-self-service) | Platform teams with their own IaC tooling | `az deployment group create` + the sample `.bicepparam` files |

Every path can deploy NimBus **inside your virtual network** (private endpoints, no public
access, Service Bus Premium) with `--network-mode private` or the equivalent pipeline
settings. It needs your subnets, a DNS decision and a runner inside the network: see
[Private networking](private-networking.md).

## What a deployment consists of

Every path ultimately performs the same three layers, in order:

1. **Azure resources** — `deploy/bicep/deploy.core.bicep` (Service Bus namespace, App Insights, Cosmos DB *or* Azure SQL, Functions storage, hosting plans, resolver Function App, RBAC) followed by `deploy/bicep/deploy.webapp.bicep` (management WebApp + RBAC). Pure Bicep; this layer is replaceable by your own tooling. The templates ship inside the CLI package, so no clone is needed to deploy them.
2. **Service Bus topology** — `nb topology apply` creates the topics, session-enabled subscriptions, and SQL routing rules for every endpoint. The topology is *generated from the compiled `PlatformConfiguration`* — it cannot be expressed in Bicep, so this step always uses the `nb` CLI (or runs in-process, as the Aspire sample's provisioner does). Pass `--assembly` to provision a catalog other than the one compiled into the CLI.
3. **Application code** — `nb deploy apps` zip-deploys the Resolver and WebApp published for the CLI's own version, or builds them from a clone with `--from-source`.

`nb setup` chains all three. The CLI is defensive about existing environments: resources are pinned to their current region, hosting plans to their current plan type/SKU (see [docs/cli.md](cli.md) for the pinning rules).

## Prerequisites (all paths)

> Full reference for governance review — complete resource inventory, resource provider registrations, and the role-assignment matrix: **[Azure Infrastructure Requirements](azure-requirements.md)**.

**Tooling**

- Azure CLI ≥ 2.60.0 **required** for Flex Consumption deploys ([Microsoft-documented minimum](https://learn.microsoft.com/azure/azure-functions/flex-consumption-how-to); `nb` checks this before publishing). ≥ 2.70 recommended; `.bicepparam` support needs ≥ 2.53
- Bicep CLI ≥ 0.35.1 **required for every deployment path** because the templates use secure outputs. Check with `az bicep version` and update with `az bicep upgrade` ([secure-output requirement](https://learn.microsoft.com/azure/azure-resource-manager/bicep/outputs#secure-outputs))
- .NET 10 SDK wherever `nb` runs. Node.js 22 is needed **only** for source builds (`nb deploy apps --from-source`); the released artifacts ship the WebApp SPA already built

**RBAC for the deploying identity.** The Bicep creates role assignments (`Microsoft.Authorization/roleAssignments`: Azure Service Bus Data Owner, Reader on Application Insights for the WebApp and, on Flex Consumption, Storage Blob Data Owner), and plain **Contributor cannot write role assignments**. On the target resource group, grant the pipeline/service principal either:

- **Owner**, or
- least-privilege: **Contributor + Role Based Access Control Administrator** (role id `f58310d9-a9f6-439a-9e8d-f62e7b41a168`), ideally with an [ABAC condition](https://learn.microsoft.com/azure/role-based-access-control/delegate-role-assignments-overview) restricting assignable roles to Azure Service Bus Data Owner (`090c5cfd-751d-490a-894a-3ce6f1109419`), Storage Blob Data Owner (`b7e6dc6d-f1e8-4753-8033-0f276bb0955b`), Cosmos DB Operator (`230815da-be43-4aae-9cb4-875f7bd000aa`), and Reader (`acdd72a7-3385-48ef-bd42-f606fba81ae7`). A condition written before the WebApp's Reader grant lacks Reader; add it, or the WebApp deployment fails when it assigns Reader.

Cosmos data-plane role assignments (`Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments`) live under the DocumentDB provider and are covered by Contributor.

**Resource provider registration.** On a fresh subscription, all providers the deployment uses must be registered — ARM auto-registers them only when the deploying identity has subscription-scope permission, which resource-group-scoped pipeline identities lack. Pre-register once per subscription with an admin account ([full provider list](azure-requirements.md#resource-provider-registrations)):

```bash
for ns in Microsoft.ServiceBus Microsoft.Web Microsoft.Storage Microsoft.Insights Microsoft.DocumentDB Microsoft.EventGrid; do
  az provider register --namespace $ns
done
```

`Microsoft.EventGrid` backs only the optional storage-hook webhooks; `nb infra apply` tries to register it and warns-and-continues when the identity lacks permission.

**Brownfield namespaces: duplicate detection must be off on the Resolver topic.** From 3.7.0 the
Resolver re-sends a throttled message, and the WebApp replays a dead-lettered one, under the
message's *original* `MessageId`, so the tracking store can recognise a late copy as one it has
already answered. If the topic has `RequiresDuplicateDetection` enabled, the broker silently drops
that copy inside the detection window and the audit update is lost. The provisioner never enables
it, so a namespace NimBus created is fine; verify one it did not:

```bash
az servicebus topic show -g <rg> --namespace-name <ns> -n Resolver
```

`requiresDuplicateDetection` must be `false` (some CLI versions nest the entity settings under
`properties`, so read the field rather than relying on a `--query` path). Duplicate detection is create-time only, so turning it off means recreating
the topic — see [Spec 030](spec/030-stale-pending-guard/spec.md) §5.7 for the alternative if you
cannot.

## Path 1: One command

```bash
dnx Akaule.NimBus.CommandLine -- setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

No clone: the CLI carries the bicep templates and downloads the Resolver and WebApp built for its own version ([ADR-015](adr/015-customer-deployment-distribution.md)). `dnx` ships with the .NET 10 SDK and runs the published CLI without installing it (the `--` separator is required).

Deploying your own event catalog rather than the built-in demo one means adding `--platform-package Acme.Contracts@1.4.0` — resolved from the feed you already publish your contracts to, so the pipeline needs neither a NimBus clone nor a build of your own solution. It provisions your topology *and* travels with the WebApp so the management UI shows your endpoints. Full option reference, including `--resolver-plan`, `--management-plan-sku`, and the SQL Server storage variants: [docs/cli.md](cli.md).

To deploy unreleased changes from a working tree, add `--from-source --repo-root .` from a clone.

New resources go to the resource group's region unless you pass `--location`; resources that already exist keep their region.

**Configure WebApp sign-in.** The deployment creates no Entra app registration, and the WebApp refuses to start until it has a sign-in method. Choose one:

- **Username and password** (SQL Server storage): add `--identity-admin-email <email>` with `NIMBUS_IDENTITY_ADMIN_PASSWORD` set ([details](authentication.md#on-azure-via-the-nb-cli)).
- **Microsoft Entra ID**: register an app with the web redirect URI `https://<webapp-host>/.auth/login/aad/callback` (the WebApp's default `AzureAd:CallbackPath`), then set the WebApp app settings `AzureAd__TenantId`, `AzureAd__ClientId`, `AzureAd__Domain` and, for a confidential client, `AzureAd__ClientSecret` ([reference](authentication.md#configuration-reference)). Later `nb setup` runs keep these settings.

## Path 2: GitHub Actions

The repository ships [.github/workflows/deploy.yml](../.github/workflows/deploy.yml) — a manually triggered (`workflow_dispatch`) workflow that logs in with OIDC (no stored secrets) and runs `infra apply` → `topology apply` → `deploy apps`.

### 2.1 Create the Entra identity (once)

```bash
APP_ID=$(az ad app create --display-name "nimbus-github-deploy" --query appId -o tsv)
az ad sp create --id $APP_ID
SP_OBJECT_ID=$(az ad sp show --id $APP_ID --query id -o tsv)

# Grant RBAC on the target resource group (see Prerequisites; Owner also works)
az role assignment create \
  --role "Contributor" \
  --assignee-object-id $SP_OBJECT_ID --assignee-principal-type ServicePrincipal \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RESOURCE_GROUP>
az role assignment create \
  --role "Role Based Access Control Administrator" \
  --assignee-object-id $SP_OBJECT_ID --assignee-principal-type ServicePrincipal \
  --scope /subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RESOURCE_GROUP>
```

### 2.2 Add a federated credential per environment

The workflow job runs with `environment: ${{ inputs.environment }}`, so the OIDC token's subject is `repo:OWNER/REPO:environment:<name>` — **each environment name you deploy (dev, prod, ...) needs its own federated credential** whose subject matches exactly (no wildcards; a mismatch fails at token exchange, not at creation; max 20 credentials per app):

```bash
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-dev",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:OWNER/REPO:environment:dev",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

### 2.3 Configure repository variables

Under **Settings → Secrets and variables → Actions → Variables**, set:

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | The app registration's Application (client) ID (`$APP_ID`) |
| `AZURE_TENANT_ID` | Directory (tenant) ID |
| `AZURE_SUBSCRIPTION_ID` | Target subscription ID |

Environment-level variables override repository-level ones, so dev and prod can use different app registrations by defining `AZURE_CLIENT_ID` on each GitHub environment.

### 2.4 Run it

**Actions → Deploy NimBus → Run workflow** with your solution id, environment, and resource group. Optional inputs: `resolver-plan` (ElasticPremium | FlexConsumption), `management-plan-sku` (e.g. B1, S1, P1v3), and `location` — leave empty for the defaults and existing-plan pinning. For a private deployment, set `runs-on` to a runner inside the network and the `NB_*` network variables on the GitHub environment ([Private networking › Pipelines](private-networking.md#pipelines)).

## Path 3: Azure DevOps

The repository ships [pipelines/azure-pipelines-deploy.yml](../pipelines/azure-pipelines-deploy.yml) — a manually triggered pipeline that runs `nb setup` in one `AzureCLI@2` task.

1. **Create a service connection** — Project settings → Service connections → New service connection → **Azure Resource Manager** → *App registration (automatic)* with credential **Workload identity federation** (the recommended, secret-free default; the built-in `AzureCLI@2` task works with it unchanged). Scope it to the subscription or directly to the target resource group, then grant the generated identity the RBAC from [Prerequisites](#prerequisites-all-paths). The automatic flow requires sufficient permissions in Entra; orgs that block app registrations can use the *Managed identity* option instead. (The `az devops` CLI cannot create workload-identity connections — use the portal.)
2. **Import the pipeline** — Pipelines → New pipeline → select your repo → Existing Azure Pipelines YAML file → `pipelines/azure-pipelines-deploy.yml`.
3. **Run it** with your solution id, environment, resource group, and the service connection name. The same optional `resolverPlan` / `managementPlanSku` / `location` parameters are available. For a private deployment, set `agentPool` to an agent pool inside the network and fill in the network parameters ([Private networking › Pipelines](private-networking.md#pipelines)).

## Path 4: Raw Bicep (self-service)

For platform teams that provision Azure resources with their own tooling. Raw Bicep covers **layer 1 only** — the Service Bus topology and app deployment still need the `nb` CLI afterwards.

Sample parameter files live in [`deploy/bicep/parameters/`](../deploy/bicep/parameters/) (`.bicepparam` deployment needs Azure CLI ≥ 2.53; these templates require Bicep ≥ 0.35.1 for secure outputs).

### 4.1 Core infrastructure

```bash
az deployment group create \
  --resource-group rg-nimbus-dev \
  --template-file deploy/bicep/deploy.core.bicep \
  --parameters deploy/bicep/parameters/deploy.core.dev.bicepparam
```

Parameter notes (see the comments in the sample files):
- `resolverId` must be `Resolver` — it matches the compiled-in constant `src/NimBus.Core/Constants.cs`.
- `solutionId`/`environment` are woven into every resource name; the storage account `st{solutionId}{environment}func` must stay ≤ 24 characters, lowercase alphanumeric.
- Defaults: `storageProvider=cosmos`, `resolverPlan=FlexConsumption`, `managementPlanSku` empty → B1 for dev/development, S1 otherwise. **ElasticPremium ↔ FlexConsumption cannot be converted in place** — to switch, delete the resolver Function App *and* the core App Service Plan first.
- Resolver capacity: `resolverMaxConcurrentSessions` (default 16, range 1–200) is applied as a template-owned host override that wins over the Resolver's `host.json`. `resolverMaxInstances` (Elastic Premium `functionAppScaleLimit`; default 0 = no per-app cap, max 10; 0 is written explicitly, so it also clears a cap set by an earlier deployment) and `resolverFlexMaximumInstanceCount` (Flex Consumption `maximumInstanceCount`; default 100, range 1–1000) cap the instance count for their respective plan. Through the CLI these are `--resolver-max-sessions` and `--resolver-max-instances`.
- Endpoint alert mail (the WebApp's Alerts tab, see [notifications](notifications.md#endpoint-alerts-alerts-tab)): set `alertMailSmtpHost` and `alertMailFrom` to turn it on; `alertMailSmtpPort` (default 587), `alertMailSmtpUseSsl` (default true), `alertMailSmtpUsername` and the secure `alertMailSmtpPassword` are optional. `alertMailWebAppUrl` defaults to `https://webapp-{solutionId}-{environment}-management.azurewebsites.net`; set it when the WebApp has a custom domain. Through the CLI these are the `--alert-mail-*` options, with the password in `NIMBUS_ALERT_MAIL_SMTP_PASSWORD`. The Resolver's app settings are replaced on every deploy, so pass them on every deploy; a deploy without them turns alert mail off.
- **Brownfield note.** An unmodified `nb setup` now applies 16 sessions per instance to the Resolver through a template-owned app setting (previously 200 from `host.json`). The other consumer bounds (1 s session idle timeout, prefetch 0, dynamic concurrency off) ship in the Resolver's `host.json` with the binary. No instance cap is applied unless `--resolver-max-instances` is passed. Sessions and the instance cap roll back with another `nb setup`; changing the `host.json` values needs a Resolver redeploy.

### 4.2 WebApp infrastructure

`deploy.webapp.bicep` needs values produced by the core deployment. Obtain them the same way the CLI does (`InfrastructureDeployer.cs`):

```bash
az extension add --name application-insights --upgrade

APP_ID=$(az monitor app-insights component show \
  --app ai-nimbus-dev-global-tracelog --resource-group rg-nimbus-dev --query appId -o tsv)
IKEY=$(az monitor app-insights component show \
  --app ai-nimbus-dev-global-tracelog --resource-group rg-nimbus-dev --query instrumentationKey -o tsv)
COSMOS_ENDPOINT=$(az cosmosdb show \
  --resource-group rg-nimbus-dev --name cosmos-nimbus-dev --query documentEndpoint -o tsv)

# Keep secret values out of the az process arguments and deployment history.
# Store this file with owner-only permissions, never commit it, and delete it
# immediately after the deployment.
umask 077
SECURE_PARAMETERS=$(mktemp)
trap 'rm -f "$SECURE_PARAMETERS"' EXIT
cat > "$SECURE_PARAMETERS" <<EOF
{
  "\$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "instrumentationKey": { "value": "$IKEY" }
  }
}
EOF

az deployment group create \
  --resource-group rg-nimbus-dev \
  --template-file deploy/bicep/deploy.webapp.bicep \
  --parameters deploy/bicep/parameters/deploy.webapp.example.bicepparam \
  --parameters "@$SECURE_PARAMETERS" \
  --parameters appInsightsAppId="$APP_ID" \
    cosmosAccountEndpoint="$COSMOS_ENDPOINT"
rm -f "$SECURE_PARAMETERS"
trap - EXIT
```

The Service Bus namespace follows the convention `sb-{solutionId}-{environment}.servicebus.windows.net`.

For Cosmos deployments, the template also configures `CosmosAccountResourceId` and grants the WebApp's managed identity Cosmos DB Operator on `MessageDatabase`. The **Topology → Storage** view uses that ARM access to list and delete containers outside the current platform catalog; Cosmos data-plane Entra authentication cannot perform container deletion.

The template also grants the WebApp's managed identity Reader on the Application Insights component (`ai-{solutionId}-{environment}-global-tracelog`), because the event logs endpoint (`GET /api/v1/event/details/{endpointId}/{id}/logs`) queries Application Insights with that identity ([details](authentication.md#application-insights-log-queries)). No API key is involved: the `apiKey` parameter is deprecated and ignored, so leave it out.

**Re-running against an existing WebApp?** Pass `webAppExists=true`. The app-settings deployment is a full replace, and several settings are configured out of band — `webAppExists=true` makes the template read the site's current settings and carry those keys forward instead of wiping them (which, for the auth settings, takes authentication down). Four prefixes are preserved:

| Prefix | Why it is set out of band |
|---|---|
| `AzureAd__` | Entra ID sign-in; this template creates no app registration |
| `ServiceBusManagement__` | Optional portal deep-link config |
| `Authorization__` | Platform-admin grants |
| `RateLimiting__` | Operator-tuned request-rate limits ([Rate Limiting](rate-limiting.md)) |

The one exception is `RateLimiting__TrustForwardedForHeader`, which the template owns and sets itself — it describes deployment topology rather than a tuning preference, so a portal edit to it does **not** survive a re-run. The numeric limits and `RateLimiting__Enabled` do survive.

The `nb` CLI passes `webAppExists` automatically from its resource discovery; only raw-Bicep deployments need it by hand. Leave it `false` on first deployment — the template cannot read settings from a site that does not exist yet.

### 4.3 Topology and apps (still required)

```bash
nb topology apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
nb deploy apps   --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Neither needs a repository clone: `nb deploy apps` deploys the Resolver and WebApp released for the CLI's own version. To build them from a clone instead, add `--from-source`; that source build needs Node.js 22 for the WebApp SPA.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `The existing core App Service Plan is X but --resolver-plan requested Y` | Azure cannot convert ElasticPremium ↔ FlexConsumption in place. Delete the resolver Function App **and** the core plan, then redeploy. |
| WebApp fails to start: `No sign-in method is configured for the NimBus WebApp` | No Entra or username/password sign-in is configured. See [Configure WebApp sign-in](#path-1-one-command). |
| `InvalidResourceLocation` / region errors | A same-named resource exists in another region. The CLI pins existing resources automatically; raw-Bicep users must pass the per-resource `*Location` override params. To actually move a resource, delete it first. |
| `Failed to register Azure provider` warning | The identity lacks subscription-level `/register` permission. Harmless unless you use Event Grid storage hooks — then pre-register once per subscription (see Prerequisites). |
| SQL server name conflict after deleting an environment | Azure SQL server DNS names are held globally for 24–72 h after deletion. Use `--sql-server-name` (or the `sqlServerName` param) to pick a fresh name. |
| Flex Consumption zip deploy fails with `SSLEOFError` / "Certificate verification failed … behind a proxy" against `<app>.scm.azurewebsites.net` | The local Azure CLI is < 2.60.0 and pushed to the legacy Kudu zipdeploy endpoint — the proxy/certificate hint is a red herring. Run `az upgrade` (or `winget upgrade Microsoft.AzureCLI`) and retry. `nb` fails fast on this before publishing. |
| Resolver zip deploy reports failure on Flex Consumption | Update the Azure CLI (≥ 2.70 recommended). Do not stop the app before deploying — the CLI health-checks the running host after publishing. |
| Role assignment errors during Bicep deployment | The deploying identity lacks `Microsoft.Authorization/roleAssignments/write`, or an ABAC condition on its grant doesn't list the role being assigned (Reader is the most recent addition). See [Prerequisites](#prerequisites-all-paths). |
| `RoleAssignmentExists` from the WebApp role assignments | The identity already holds that role from a grant made outside the template, for example a manual Reader grant on Application Insights. Delete the manual assignment and re-run; the template then owns it. |
| Private-mode errors: Standard namespace, subnet delegation, pre-flight DNS check, recorded network mode | See [Private networking › Troubleshooting](private-networking.md#troubleshooting). |
| The event logs endpoint returns no entries | The WebApp queries Application Insights with its managed identity. Check that `AppInsights:ApplicationId` is set and that the identity has Reader on the component. See [Authentication](authentication.md#application-insights-log-queries). |
