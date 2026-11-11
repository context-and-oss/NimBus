# Azure Infrastructure Requirements

Everything an Azure platform/governance team needs to review and prepare **before** a NimBus deployment runs — what gets provisioned, which resource providers must be registered, and exactly which RBAC the deploying identity, the provisioned apps, and operators need. The how-to lives in the [Deployment Guide](deployment.md); this page is the reference.

## Resources provisioned

One resource group holds everything for a `{solutionId}` + `{environment}` pair. Bicep sources: [`deploy/bicep/deploy.core.bicep`](../deploy/bicep/deploy.core.bicep) and [`deploy/bicep/deploy.webapp.bicep`](../deploy/bicep/deploy.webapp.bicep).

| Resource | Name convention | Default SKU / configuration | Purpose |
|---|---|---|---|
| Service Bus namespace | `sb-{solutionId}-{environment}` (override: `--service-bus-namespace-name`) | **Standard** (topics are required; the Basic tier has no topics). **Premium** (1 messaging unit by default) with `--network-mode private` | Messaging backbone — one topic per endpoint plus the Resolver topic |
| Application Insights | `ai-{solutionId}-{environment}-global-tracelog` | `kind: web` component | Telemetry; the WebApp queries traces with its managed identity (Reader on the component) |
| Cosmos DB account *(default storage provider)* | `cosmos-{solutionId}-{environment}` | Standard offer (provisioned throughput), Session consistency, single region; database `MessageDatabase` with containers `messages` (partition `/eventId`, TTL 90 days) and `audits` (TTL 1 year). Per-endpoint containers are created **at runtime** by the apps (see [ADR-008](adr/)) | Message store + audit trail |
| Azure SQL server + database *(only `--storage-provider sqlserver --sql-mode provision`)* | `sql-{solutionId}-{environment}` / `MessageDatabase` | **S0** (allowed: Basic/S0/S1/S2), TLS ≥ 1.2, public network access **enabled** with the `AllowAllWindowsAzureIps` (0.0.0.0) firewall rule | Message store alternative. The open Azure-services rule admits every Azure tenant; `--network-mode private` replaces it with a private endpoint ([Private networking](private-networking.md)) |
| Storage account | `st{solutionId}{environment}func` | StorageV2, `Standard_LRS`, HTTPS-only; blob container `app-package-resolver` added on Flex Consumption | Functions host storage + Flex deployment package |
| App Service Plan (core) | `asp-{solutionId}-{environment}-core` | **FC1 Flex Consumption (Linux)** by default, or EP1 Elastic Premium (Windows) via `--resolver-plan` | Hosts the resolver Function App |
| App Service Plan (management) | `asp-{solutionId}-{environment}-management` | **B1** for `dev`/`development`, **S1** otherwise (override via `--management-plan-sku`) | Hosts the management WebApp |
| Function App | `func-{solutionId}-{environment}-resolver` | .NET 10 isolated worker, FTPS-only, system-assigned managed identity | The NimBus Resolver |
| Web App | `webapp-{solutionId}-{environment}-management` | HTTPS-only, FTPS-only, run-from-package, Always On (Basic+), system-assigned managed identity | Management UI |
| Role assignments | (deterministic GUIDs) | See [Role assignments created by the deployment](#role-assignments-created-by-the-deployment) | Managed-identity data-plane access |
| Private endpoints *(only `--network-mode private`)* | `pe-{resource}-{groupId}` | 5–8 endpoints in your subnet: Service Bus, store (none for an external SQL server), storage (blob, queue, table, plus file on Elastic Premium), Resolver and WebApp sites. Tagged `nimbus-deployment={solutionId}-{environment}` | Private access to every NimBus endpoint |
| Private DNS zones *(only `--private-dns create`)* | `privatelink.*` | One per service type, linked to your VNet with fallback to internet | Resolve the private endpoints |

With `--network-mode private` the Cosmos DB account, SQL server, storage account, Service Bus
namespace and both apps are created with public network access disabled, and both apps are
VNet-integrated. Subnets, DNS, egress and runners: [Private networking](private-networking.md).

Legacy names: `--resource-name-postfix` appends `-{postfix}` to the Service Bus, Cosmos DB, SQL
server, Function App and Web App names only, and `--func-storage-account-name` points the
Resolver at an existing storage account ([details](cli.md#legacy-resource-names)).

Naming constraint: the storage account name `st{solutionId}{environment}func` must be ≤ 24 lowercase alphanumeric characters — keep `solutionId` + `environment` within 17 characters combined.

## Resource provider registrations

The deployment uses these resource providers:

| Provider | Used for | When |
|---|---|---|
| `Microsoft.ServiceBus` | Namespace | Always |
| `Microsoft.Web` | Plans, Function App, Web App | Always |
| `Microsoft.Storage` | Functions storage | Always |
| `Microsoft.Insights` | Application Insights | Always |
| `Microsoft.DocumentDB` | Cosmos DB | `--storage-provider cosmos` (default) |
| `Microsoft.Sql` | Azure SQL | `--storage-provider sqlserver --sql-mode provision` |
| `Microsoft.EventGrid` | Optional storage-hook webhooks | Only if you use Event Grid storage hooks — nothing in the Bicep deploys Event Grid resources |
| `Microsoft.Network` | Private endpoints, private DNS zones | `--network-mode private` |
| `Microsoft.App` | Flex Consumption subnet delegation | `--network-mode private` with the Flex Consumption Resolver |

ARM registers a provider automatically on first use **only when the deploying identity holds the subscription-scoped `.../register/action` permission** (included in Contributor/Owner *at subscription scope*). A resource-group-scoped pipeline identity cannot register providers, so on a fresh subscription have an administrator pre-register once:

```bash
for ns in Microsoft.ServiceBus Microsoft.Web Microsoft.Storage Microsoft.Insights Microsoft.DocumentDB Microsoft.EventGrid; do
  az provider register --namespace $ns
done
```

`nb infra apply` attempts to register `Microsoft.EventGrid` itself; when the identity lacks permission it prints a warning and continues (it never blocks the deployment).

## Role assignments created by the deployment

For governance review — the Bicep grants the two system-assigned managed identities these roles:

| Role | Role ID | Scope | Granted to | When |
|---|---|---|---|---|
| Azure Service Bus Data Owner | `090c5cfd-751d-490a-894a-3ce6f1109419` | Service Bus namespace | Resolver + WebApp identities | Always |
| Cosmos DB Built-in Data Contributor *(Cosmos data-plane `sqlRoleAssignments`)* | `00000000-0000-0000-0000-000000000002` | Cosmos account | Resolver + WebApp identities | Cosmos provider |
| Cosmos DB Operator *(Azure control plane; account keys remain inaccessible)* | `230815da-be43-4aae-9cb4-875f7bd000aa` | `MessageDatabase` | WebApp identity | Cosmos provider; required by Topology → Storage container cleanup |
| Storage Blob Data Owner | `b7e6dc6d-f1e8-4753-8033-0f276bb0955b` | Functions storage account | Resolver identity | Flex Consumption plan (identity-based host storage + deployment package) |
| Reader | `acdd72a7-3385-48ef-bd42-f606fba81ae7` | Application Insights component | WebApp identity | When `appInsightsAppId` is set (always through `nb`); the event logs endpoint queries Application Insights with Microsoft Entra ([details](authentication.md#application-insights-log-queries)) |

No secrets are distributed to the apps on the Cosmos path — everything is managed identity. The provisioned-SQL path passes a SQL connection string as app settings instead.

## RBAC for the deploying identity (pipeline or user)

The identity running `nb infra apply` / the pipelines needs, **on the target resource group**:

- **Owner** — simplest, or
- **least privilege: Contributor + Role Based Access Control Administrator** (`f58310d9-a9f6-439a-9e8d-f62e7b41a168`).

Why Contributor alone is not enough: the table above is written as `Microsoft.Authorization/roleAssignments`, and Contributor's `NotActions` explicitly exclude `Microsoft.Authorization/*/Write`. The Cosmos entries are the exception — they are `Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments` resources and **are** covered by Contributor.

Hardening the RBAC Administrator grant with an [ABAC condition](https://learn.microsoft.com/azure/role-based-access-control/delegate-role-assignments-overview) restricting assignable roles to the four IDs the deployment needs. A condition written before the WebApp's Reader grant lists only the first three; add Reader, or the WebApp deployment fails when it assigns Reader:

```bash
az role assignment create \
  --role "Role Based Access Control Administrator" \
  --assignee-object-id <SP_OBJECT_ID> --assignee-principal-type ServicePrincipal \
  --scope /subscriptions/<SUB_ID>/resourceGroups/<RG> \
  --condition-version 2.0 \
  --condition "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {090c5cfd-751d-490a-894a-3ce6f1109419, b7e6dc6d-f1e8-4753-8033-0f276bb0955b, 230815da-be43-4aae-9cb4-875f7bd000aa, acdd72a7-3385-48ef-bd42-f606fba81ae7}))"
```

Everything else the CLI does is covered by resource-group Contributor:

- `az deployment group create` for both Bicep templates
- `nb topology apply` reads the Service Bus root connection string (`listKeys`) to create topics/subscriptions/rules
- `nb deploy apps` zip-deploys the Function App and Web App

Subscription-scope permissions are **not** required, provided the resource providers are pre-registered (previous section).

Private mode additionally needs rights outside the resource group: joining your subnets
(`Microsoft.Network/virtualNetworks/subnets/join/action`, for example Network Contributor on
the VNet) and, with `--private-dns existing`, Private DNS Zone Contributor on the hub DNS
resource group. See [Private networking › Permissions](private-networking.md#permissions-for-the-deploying-identity).

## Operator and developer access (after deployment)

| Who | Needs | Why |
|---|---|---|
| Operators using `nb` ops commands (`endpoint`, `container`) or local WebApp with Entra auth | **Azure Service Bus Data Owner** on the namespace + **Cosmos DB Built-in Data Contributor** on the account | Data-plane access via `DefaultAzureCredential` — same heuristic the apps use (see [docs/cli.md](cli.md)) |
| CI identity re-running deployments | Same as [deploying identity](#rbac-for-the-deploying-identity-pipeline-or-user) | Re-runs are idempotent; existing plans/regions are pinned |

## Tooling and platform constraints

| Requirement | Detail |
|---|---|
| Azure CLI ≥ **2.60.0** | Hard minimum for Flex Consumption zip deploys ([Microsoft-documented](https://learn.microsoft.com/azure/azure-functions/flex-consumption-how-to)); `nb deploy apps` refuses to deploy to a Flex plan with an older CLI. ≥ 2.70 recommended; `.bicepparam` files need ≥ 2.53 |
| Bicep CLI ≥ **0.35.1** | Required on every deployment path because the templates use `@secure()` outputs. Check with `az bicep version` and update with `az bicep upgrade` ([Microsoft documentation](https://learn.microsoft.com/azure/azure-resource-manager/bicep/outputs#secure-outputs)) |
| .NET 10 SDK + Node.js 22 | Wherever `nb deploy apps` builds the apps (the WebApp SPA builds during `dotnet publish`); pipeline agents need outbound access to nuget.org and the npm registry |
| Flex Consumption region availability | Not available in every region — check with `az functionapp list-flexconsumption-locations` |
| Region consistency | Apps must live in the same region as their plans; the CLI pins existing resources to their current region automatically |
| Plan-type immutability | ElasticPremium (Windows) ↔ FlexConsumption (Linux) cannot be converted in place — delete the resolver Function App *and* the core plan to switch |
| Azure SQL DNS cooldown | A deleted SQL server's name is held globally for 24–72 h; use `--sql-server-name` to redeploy sooner |
| Service Bus tier | Standard minimum (topics). Premium for private networking (private endpoints are Premium-only) or predictable throughput — the topology is tier-agnostic. Azure cannot convert Standard to Premium in place; `nb` keeps an existing Premium namespace Premium |
