# NimBus CLI (`nb`)

Command-line tool for provisioning Azure infrastructure, managing Service Bus topology, deploying applications, and performing operational tasks on the NimBus platform.

## Installation

The CLI is distributed as a .NET tool (package `Akaule.NimBus.CommandLine` — the bare
`NimBus.*` prefix is reserved on nuget.org, assemblies and namespaces stay `NimBus.*`):

```bash
dotnet tool install --global Akaule.NimBus.CommandLine
```

Or run it npx-style without installing, via `dnx` (ships with the .NET 10 SDK). The `--`
separator is required so tool options aren't picked up by `dnx` itself:

```bash
dnx Akaule.NimBus.CommandLine -- <command> [options]
```

Or run directly from source:

```bash
dotnet run --project src/NimBus.CommandLine -- <command>
```

### One-command cloud install

A single command provisions the infrastructure, applies the Service Bus topology, and
deploys the resolver + management WebApp. No repository clone: the CLI carries the bicep
templates and downloads the applications built for its own version.

```bash
dnx Akaule.NimBus.CommandLine -- setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Prerequisites: .NET 10 SDK (provides `dnx`) and Azure CLI ≥ 2.60.0 logged in via
`az login` (≥ 2.70 recommended). Node.js is only needed for `--from-source`, where the
WebApp SPA builds during `dotnet publish`.
2.60.0 is the Microsoft-documented minimum for Flex Consumption: older versions push to
the legacy Kudu zipdeploy endpoint and fail with a misleading SSL/proxy error, so `nb`
refuses to deploy to a Flex Consumption plan with an older az.

## Global Options

| Option | Description |
|---|---|
| `-sbc`, `--sb-connection-string` | Azure Service Bus connection string, or a fully qualified namespace for Entra ID auth (overrides `AzureServiceBus_ConnectionString` env var) |
| `-dbc`, `--db-connection-string` | Cosmos DB connection string, or an account endpoint URI for Entra ID auth (overrides `CosmosDb_ConnectionString` env var) |
| `--unresolved-retention-days` | Retention in days stamped on unresolved rows this command rewrites: `-1` for unlimited (default) or `1`–`365` (overrides `NimBus__Cosmos__UnresolvedRetentionDays` env var). Registered on `nb container resubmit` |
| `-h`, `--help` | Show help for any command |

Connection strings can be set via environment variables instead of passing them on every call:

```bash
export AzureServiceBus_ConnectionString="Endpoint=sb://..."
export CosmosDb_ConnectionString="AccountEndpoint=https://..."
export NimBus__Cosmos__UnresolvedRetentionDays="365"
```

`NimBus__Cosmos__UnresolvedRetentionDays` is the environment form of the host
configuration key `NimBus:Cosmos:UnresolvedRetentionDays`, so one exported value
configures the NimBus hosts and the CLI alike. See
[Retention of unresolved rows](storage-providers.md#retention-of-unresolved-rows).

### Entra ID / managed identity

Instead of a connection string, pass a fully qualified Service Bus namespace or a Cosmos DB
account endpoint. The CLI then authenticates with `DefaultAzureCredential` (`az login`,
managed identity, environment credentials, etc.) — the same heuristic the WebApp and
Resolver use, so no keys need to be distributed:

```bash
export AzureServiceBus_ConnectionString="mybus.servicebus.windows.net"
export CosmosDb_ConnectionString="https://myaccount.documents.azure.com/"
```

The signed-in identity needs `Azure Service Bus Data Owner` on the namespace and a Cosmos
DB data-plane role (e.g. `Cosmos DB Built-in Data Contributor`) on the account.

---

## Commands

### `nb infra apply`

Deploy Azure infrastructure using bicep templates.

```bash
nb infra apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

The bicep templates ship inside the CLI package, so this command needs no repository
clone. `--repo-root` remains as the developer override for testing template changes
before they are released.

| Option | Required | Description |
|---|---|---|
| `--solution-id` | Yes | Solution identifier used in resource names |
| `--environment` | Yes | Environment name (dev, staging, prod) |
| `--resource-group` | Yes | Azure resource group name |
| `--repo-root` | No | Developer override: use the bicep templates in a repository clone instead of the ones shipped in the CLI |
| `--location` | No | Azure region for new resources. Default: the resource group's region |
| `--webapp-version` | No | Version string for web app settings |
| `--storage-provider` | No | Storage backend: `cosmos` (default) or `sqlserver` |
| `--sql-mode` | No | When `--storage-provider sqlserver`: `provision` (default, creates a new Azure SQL server + DB) or `external` (use an existing SQL Server) |
| `--sql-admin-login` | Conditional | Required when `--sql-mode provision` |
| `--sql-server-name` | No | Override the SQL server name (default: `sql-{solution-id}-{environment}`). Useful when the default DNS name is held in Azure's global namespace from a recent delete (24–72h cooldown). |
| `--resolver-plan` | No | Resolver Function App hosting plan: `FlexConsumption` (default for new deployments; FC1, scale-to-zero Linux) or `ElasticPremium` (EP1 Windows). Existing deployments keep their current plan type unless this flag is passed. |
| `--resolver-max-sessions` | No | Resolver Service Bus session concurrency per instance (1–200). Defaults to the template value (16). Applied as a template-owned host override (`AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions`), so it wins over the Resolver's `host.json`. |
| `--resolver-max-instances` | No | Resolver Function App instance ceiling. Elastic Premium: `0` (no cap, default) to `10`, applied as `functionAppScaleLimit`; passing `0` clears a cap set by an earlier deployment. Flex Consumption: `1`–`1000`, applied as `maximumInstanceCount` (default 100). |
| `--management-plan-sku` | No | SKU for the management App Service Plan hosting the WebApp. Default for new deployments: `B1` for `dev`/`development`, `S1` otherwise. Existing deployments keep their current SKU unless this flag is passed. |
| `--network-mode` | No | `public` or `private`. `private` puts every NimBus endpoint behind a private endpoint in your subnets and turns public network access off ([Private networking](private-networking.md)). Defaults to the mode recorded on the resource group, otherwise `public`. |
| `--allow-public-access` | No | With private mode: the transition pass for an existing deployment. Private endpoints, DNS and VNet integration are added while public access stays on. |
| `--skip-transition` | No | Switch an existing public deployment straight to private, accepting downtime while the apps join the VNet. |
| `--private-endpoint-subnet-id` | Private mode | Subnet the private endpoints take their addresses from. Must not be delegated. |
| `--resolver-subnet-id` | Private mode | Resolver VNet integration subnet: delegated to `Microsoft.App/environments` (Flex Consumption) or `Microsoft.Web/serverFarms` (Elastic Premium), in the Resolver's region. |
| `--webapp-subnet-id` | Private mode | WebApp VNet integration subnet, delegated to `Microsoft.Web/serverFarms`, in the WebApp's region. |
| `--private-dns` | Private mode | `create` (NimBus creates and links the privatelink zones; for VNets dedicated to NimBus), `existing` (zones in a hub resource group) or `external` (your policy or DNS servers write the records). |
| `--private-dns-zone-scope` | With `existing` | Resource group id holding the privatelink zones. |
| `--private-dns-link-vnet-id` | No | With `create`: VNet to link the zones to. Repeatable. Defaults to the private-endpoint subnet's VNet. |
| `--monitor-private-link` | Private mode | `none` in this release: telemetry leaves through your firewall to the public Azure Monitor endpoints. |
| `--service-bus-capacity` | No | Premium messaging units: `1`, `2`, `4`, `8` or `16`. Defaults to the existing namespace's capacity, otherwise 1. |
| `--service-bus-namespace-name` | No | Override the namespace name (default `sb-{solution-id}-{environment}`), e.g. a new Premium namespace next to the Standard one it replaces. Recorded, so `nb topology apply` follows it. |
| `--dns-wait` | No | Minutes to wait for this machine to resolve the private endpoints before locking at the end of a transition (default 10; `0` checks once). |

**Private networking.** Private mode deploys Service Bus Premium and creates every data
service and both apps with public network access disabled. `nb` checks the subnets'
delegation and region before deploying, refuses private mode on an existing Standard
namespace, and refuses a direct public → private switch of an existing deployment
without `--skip-transition`. Before deploying, it records the full network setup as tags on
the resource group, so a rerun without network options reproduces it. `--network-mode public`
on a deployment with a record reopens public access, then removes the apps' VNet
integration and deletes the private endpoints and DNS resources tagged
`nimbus-deployment={solution-id}-{environment}`. Details and the migration runbook:
[Private networking](private-networking.md).

Deployment secrets are intentionally not accepted as command-line options because process arguments can be inspected by other tools. Set the required environment variable before invoking `nb`:

| Environment variable | Required when |
|---|---|
| `NIMBUS_SQL_CONNECTION_STRING` | `--storage-provider sqlserver --sql-mode external` |
| `NIMBUS_SQL_ADMIN_PASSWORD` | `--storage-provider sqlserver --sql-mode provision` |
| `NIMBUS_IDENTITY_ADMIN_PASSWORD` | `nb setup` is given `--identity-admin-email` |

Canceling `nb` terminates its local Azure CLI process tree before removing the
ephemeral parameter file. An ARM deployment that Azure already accepted may
continue server-side, so check the resource group's deployment history after a
cancellation.

Deploys core infrastructure (Service Bus, App Insights, and either Cosmos DB or Azure SQL depending on `--storage-provider`) and the web app infrastructure via bicep. The provisioned SQL path uses AAD managed-identity auth (`Authentication=Active Directory Default`); the external path uses the supplied connection string verbatim. Resolves required resource endpoints/namespace settings. It creates no Application Insights API key: the WebApp queries Application Insights with its managed identity, which the web app template grants Reader on the component ([details](authentication.md#application-insights-log-queries)).

**Existing-resource location pinning.** Before deploying, the CLI lists the resources already in the target resource group and pins each known NimBus resource (Service Bus, App Insights, Cosmos, SQL Server, function storage, app service plans, function app, web app) to its current location. This avoids the `InvalidResourceLocation` error Azure raises when a same-named resource already exists in another region. Net-new resources use `--location`, or the resource group's region if unset. To actually move a resource between regions, delete it first.

**Existing-plan pinning.** The same applies to hosting plans: an existing core App Service Plan pins the resolver plan type (Azure cannot convert between Elastic Premium and Flex Consumption in place), and an existing management plan pins its SKU so re-runs never silently rescale it. Explicit `--resolver-plan` / `--management-plan-sku` flags win; a `--resolver-plan` that conflicts with the existing plan type fails with guidance (delete both the resolver Function App and the core plan first).

---

### `nb topology export`

Export the platform configuration to JSON.

```bash
nb topology export -o platform-config.json
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file path (default: `platform-config.json`) |

Outputs a JSON file with all endpoints, event types, and Service Bus identifiers for use by deployment scripts.

---

### `nb topology apply`

Provision the Service Bus topology for all endpoints.

```bash
nb topology apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|---|---|---|
| `--solution-id` | Yes | Solution identifier |
| `--environment` | Yes | Environment name |
| `--resource-group` | Yes | Resource group with the Service Bus namespace |
| `--platform-package` | No | NuGet package containing **your** catalog, as `Id@Version`. Required to provision a catalog that is not compiled into this CLI. |
| `--platform-feed` | No | Feed serving that package |
| `-a`, `--assembly` | No | Local assembly exposing a public parameterless `IPlatform` — the same thing from a build output instead of a feed |
| `--platform` | No | `IPlatform` type name when the assembly or package exposes more than one |
| `--service-bus-namespace-name` | No | Namespace name override. Defaults to the name `nb infra apply` recorded on the resource group, otherwise `sb-{solution-id}-{environment}`. |
| `--dns-wait` | No | Private deployments: minutes to wait for this machine to resolve the Service Bus private endpoint (default 10; `0` checks once). |

On a private deployment the command first checks that this machine resolves the Service Bus
private endpoint, and stops with the failing host names if it does not
([Pre-flight check](private-networking.md#pre-flight-check)).

The topology is generated from a compiled `PlatformConfiguration`. In a pipeline, resolve
it from the feed you already publish your contracts to — no clone and no build of your
solution:

```bash
nb topology apply --solution-id acme --environment dev --resource-group rg-acme-dev \
  --platform-package Acme.Contracts@1.4.0
```

The version is deliberately explicit: the catalog decides the Service Bus topology, so a
floating version would let a contracts release change routing without a reviewed change.

Locally, `--assembly ./src/Acme.Contracts/bin/Release/net10.0/Acme.Contracts.dll` does the
same from a build output. Use one or the other, not both.

| Environment variable | Purpose |
|---|---|
| `NIMBUS_PLATFORM_FEED` | Feed serving `--platform-package`. Falls back to `NIMBUS_ARTIFACT_FEED`, then nuget.org. |
| `NIMBUS_PLATFORM_FEED_TOKEN` | Token for a private feed (Azure Artifacts PAT), sent as Basic auth. Falls back to `NIMBUS_ARTIFACT_FEED_TOKEN`. |

Your contracts package must be built against a compatible NimBus version, since the CLI
loads it to resolve the `IPlatform` type.

Creates topics, subscriptions, and routing rules for each endpoint. Idempotent — only recreates entities if configuration has changed. Creates:
- Main subscription (session-enabled) per endpoint
- Resolver subscription (forwarding)
- Continuation and Retry subscriptions (forwarding back to self)
- Deferred subscription (session-enabled)
- DeferredProcessor subscription (sessions=OFF)
- Event-type forwarding subscriptions for cross-endpoint routing

---

### `nb deploy apps`

Deploy the resolver and web app to Azure.

```bash
nb deploy apps --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

| Option | Required | Description |
|---|---|---|
| `--solution-id` | Yes | Solution identifier |
| `--environment` | Yes | Environment name |
| `--resource-group` | Yes | Resource group with target apps |
| `--platform-package` | No | NuGet package containing your catalog, as `Id@Version`. Its assemblies are deployed with the WebApp. |
| `--platform-feed` | No | Feed serving that package |
| `--platform` | No | `IPlatform` type name when the package exposes more than one |
| `--from-source` | No | Build the applications from a repository clone instead of deploying the released artifacts |
| `--repo-root` | No | Repository root for a source build. Implies `--from-source`. |
| `--configuration` | No | Build configuration (default: `Release`). Source builds only. |
| `--only` | No | Deploy a single application: `resolver` \| `webapp`. Defaults to both. |
| `--dns-wait` | No | Private deployments: minutes to wait for this machine to resolve the apps' private endpoints before deploying (default 10; `0` checks once). The deployment goes through each app's Kudu/scm site, which private mode exposes only through its private endpoint. |

**Deploying your own catalog.** The management UI renders Endpoints, Event Types and PII
masking from an `IPlatform`, and the released WebApp artifact carries only NimBus's
built-in one. Pass `--platform-package` and the CLI adds your assemblies to a copy of the
released zip and points the site at them via `NimBus__PlatformType` /
`NimBus__PlatformAssembly`:

```bash
nb deploy apps --solution-id acme --environment dev --resource-group rg-acme-dev \
  --platform-package Acme.Contracts@1.4.0
```

The cached artifact is never modified, and an assembly the WebApp already ships is skipped
rather than replacing a tested binary. **The Resolver needs none of this** — it holds no
catalog, so one released `resolver.zip` is correct for every platform and adding endpoints
never requires redeploying it.

By default the CLI downloads the Resolver and WebApp built for **its own version** and
zip-deploys them, so the deploying machine needs neither the .NET SDK nor Node.js, and
the deployed bits are exactly the ones that release was tested with. Artifacts are cached
per version under the user's local application data directory.

| Environment variable | Purpose |
|---|---|
| `NIMBUS_ARTIFACT_FEED` | Base URL of the NuGet feed serving `Akaule.NimBus.Deploy` (default: `https://api.nuget.org`). Point this at a private mirror — for example Azure Artifacts — to deploy without reaching nuget.org. |
| `NIMBUS_ARTIFACT_FEED_TOKEN` | Token for a feed that requires authentication; sent as the password half of Basic auth. |

Running inside a repository clone does **not** change this — deploying a working tree has
to be asked for with `--from-source` (or `--repo-root`), which restores the previous
behaviour: `dotnet publish` both apps locally, stamping the version from the latest git
tag. If no artifacts exist for the installed CLI version the command fails and says so;
it never falls back to a source tree that could be a different revision.

Deploys via the Azure CLI. `--only webapp` skips the resolver build and deploy entirely (and vice versa) — useful for fast WebApp-only iterations. On a Flex Consumption resolver the zip is deployed directly (the app must stay running — the Azure CLI verifies host health after publishing); on Elastic Premium the app is stopped for the deployment and restarted afterwards.

---

### `nb setup`

Run infrastructure, topology, and app deployment in sequence.

```bash
nb setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

Combines `infra apply` → `topology apply` → `deploy apps` in a single command. Accepts all options from the individual commands, including `--storage-provider`, `--sql-mode`, `--sql-admin-login`, `--sql-server-name`, `--resolver-plan`, `--resolver-max-sessions`, `--resolver-max-instances`, `--management-plan-sku`, the private networking options, `--assembly`, and `--from-source`. On a private deployment the topology and app steps each check first that this machine resolves the private endpoints, so the check runs after the infrastructure step has created them. SQL and bootstrap-admin secrets use the environment variables documented under `nb infra apply`.

Like the individual commands, this needs no repository clone. Deploying your own event
catalog means passing `--platform-package`: the topology step provisions your endpoints and
the app step deploys the catalog with the WebApp. `--assembly` feeds only the topology step;
the app step then deploys the WebApp with its built-in catalog and clears any
`NimBus__PlatformType` / `NimBus__PlatformAssembly` settings, so the management UI does not
show your endpoints.

---

### `nb endpoint session delete`

Delete all messages on a Service Bus session and clear its state.

```bash
nb endpoint session delete <endpoint-name> <session-id> -sbc "..."
```

| Argument | Required | Description |
|---|---|---|
| `endpoint-name` | Yes | Name of the endpoint |
| `session` | Yes | Session ID to delete |

Accepts the session, removes all active and deferred messages from Service Bus, removes corresponding events from Cosmos DB, and clears the session state.

---

### `nb endpoint topics removeDeprecated`

Remove deprecated subscriptions and rules from a Service Bus topic.

```bash
nb endpoint topics removeDeprecated <endpoint-name> -sbc "..."
```

Compares the expected topology (from `PlatformConfiguration`) against the actual Service Bus state. Displays a visual tree with deprecated items highlighted in red, prompts for confirmation, then deletes them with progress tracking.

---

### `nb endpoint purge`

Purge messages from a Service Bus subscription by state and/or enqueued time.

```bash
nb endpoint purge <endpoint-name> --state Active,Deferred --before 2026-03-01T00:00:00 -sbc "..."
```

| Option | Required | Description |
|---|---|---|
| `--subscription` | No | Subscription name (defaults to endpoint name) |
| `--state` | No | Comma-separated states: `Active`, `Deferred` (default: all) |
| `--before` | No | Only purge messages enqueued before this UTC datetime |

Scans all messages, filters by state and time, organizes by session, prompts for confirmation, then completes matching messages.

---

### `nb container event delete`

Delete a specific event from Cosmos DB.

```bash
nb container event delete <endpoint-name> <event-id> -dbc "..."
```

---

### `nb container message delete`

Delete messages from the messages container filtered by the "To" field.

```bash
nb container message delete <to-field-value> -dbc "..."
```

Example: `nb container message delete BillingEndpoint` deletes all messages addressed to BillingEndpoint.

---

### `nb container delete`

Delete events from Cosmos DB by resolution status.

```bash
nb container delete <endpoint-name> -s failed,deadlettered -dbc "..."
```

| Option | Required | Description |
|---|---|---|
| `-s`, `--status` | No | Comma-separated statuses (default: `DeadLettered`) |

Valid statuses: `Pending`, `Deferred`, `Failed`, `DeadLettered`, `Unsupported`, `Completed`, `Skipped`

---

### `nb container resubmit`

Resubmit failed messages older than 10 minutes via Service Bus.

```bash
nb container resubmit <endpoint-name> -sbc "..." -dbc "..." --unresolved-retention-days 365
```

Finds failed events in Cosmos DB, updates their status, and sends `ResubmissionRequest` messages to the Manager topic for re-processing.

Resubmission **rewrites the whole tracking document**. On an account using bounded
retention, pass `--unresolved-retention-days` (or export
`NimBus__Cosmos__UnresolvedRetentionDays`) with the same value the hosts are
configured with — otherwise the rewritten rows revert to unlimited retention.

---

### `nb container copy`

Copy endpoint data (events + messages) from one Cosmos DB to another.

```bash
nb container copy <endpoint-name> -dbc "source-conn-string" --target-dbc "target-conn-string"
```

| Option | Required | Description |
|---|---|---|
| `--target-dbc` | Yes | Target Cosmos DB connection string |
| `--from` | No | Only copy events from this UTC datetime |
| `--to` | No | Only copy events up to this UTC datetime |
| `-s`, `--status` | No | Comma-separated statuses to copy (default: all) |
| `-b`, `--batch-size` | No | Documents per batch (default: all) |

Creates target containers if they don't exist. Removes TTL from copied documents to prevent premature expiration. Only copies messages for events that were copied.

Because copied documents carry no `ttl` field, they never expire in the target
until an operator backfills them — see
[the backfill procedure](storage-providers.md#the-setting-applies-to-newly-written-documents-only).
The target endpoint container is created with container-level TTL enabled, and an
endpoint name that collides with one of the store's own container ids is rejected
(see [Reserved endpoint ids](storage-providers.md#reserved-endpoint-ids)).

---

### `nb container skip`

Send the endpoint a skip for each matching event, as **Skip** in the WebApp does. The endpoint
releases the session the event blocks and replays the messages deferred behind it, and the
Resolver records the event as `Skipped` once the endpoint has handled the skip. The command reads
the events from Cosmos DB and sends the skips through Service Bus, so it needs both connection
strings.

```bash
nb container skip <endpoint-name> -s failed,deadlettered --before 2026-03-01T00:00:00 -sbc "..." -dbc "..."
```

| Option | Required | Description |
|---|---|---|
| `-s`, `--status` | Yes | Source statuses to skip: `failed`, `deadlettered`, `unsupported` |
| `--before` | No | Only skip events last updated before this UTC datetime |

Only `Failed`, `DeadLettered` and `Unsupported` events can be skipped. A `Deferred` event is parked
behind the event that blocks its session and runs once that block clears, so skip the blocking
event instead. `Pending`, `TooManyRequests` and `Published` messages are still on their way, and
`Completed` and `Skipped` are terminal.

---

### `nb catalog export`

Export the platform as a **full runnable [EventCatalog](https://www.eventcatalog.dev/)** in EventCatalog's native MDX format (the free path — the official EventCatalog AsyncAPI generator requires a paid Scale license), with a filtered AsyncAPI 3.0 document attached to every service via the `specifications` frontmatter so specs render in the EventCatalog UI.

```bash
nb catalog export -o ./eventcatalog
cd ./eventcatalog && npm install && npm run dev   # requires Node 22+
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Catalog directory (default: `./eventcatalog`) |
| `-a`, `--assembly` | No | Host assembly exposing a public parameterless `IPlatform`; default is the built-in platform. Attribute (`[AsyncApiMessage]`) enrichment applies; fluent host-DI enrichment is not observable through this path. |
| `--platform` | No | `IPlatform` type name when the assembly exposes more than one |
| `-t`, `--title` | No | Catalog title/organization, used only when scaffolding a missing `eventcatalog.config.js` (default: `NimBus`) |

Generated structure:
```
eventcatalog/
├── eventcatalog.config.js       # scaffolded once (stable cId), never overwritten
├── package.json / .gitignore / public/   # scaffolded once, never overwritten
├── domains/{SystemId}/index.mdx          # one per ISystem ('platform' fallback)
├── services/{EndpointId}/index.mdx       # sends/receives routed via the endpoint's channel
├── services/{EndpointId}/asyncapi.yaml   # per-service AsyncAPI 3.0, attached via specifications
├── events/{EventTypeId}/index.mdx        # + schema.json (self-contained JSON Schema, $defs)
├── commands/{EventTypeId}/index.mdx      # Command-derived contracts (ADR-014) + schema.json
└── channels/{EndpointId}.topic/index.mdx # Service Bus topic: address, amqp, at-least-once
```

Mapping highlights: `[AsyncApiMessage]` `Version` drives the message version and every service pin; `Deprecated` renders EventCatalog's deprecation banner; `Owner`/`Team`/`Tags` surface as badges; `[SessionKey]` is documented in the message body; dynamically-typed events (spec 022 `DynamicForward`) appear under `events/` with a `Dynamic event` badge and no schema.

> **Ownership rule.** The exporter fully owns the five generated directories `domains/`, `services/`, `events/`, `commands/`, `channels/` — they are **deleted and regenerated on every run** (removing resources for renamed/deleted endpoints and events). Everything else in the catalog directory — the scaffold files, `public/`, `teams/`, `users/`, custom pages, and any resources you add outside those five directories — is never touched. Scaffold files are created only when missing, so `eventcatalog.config.js` customizations (and its generated-once `cId`) survive re-export.

---

### `nb catalog asyncapi`

Export platform topology as an AsyncAPI 3.0 specification.

```bash
nb catalog asyncapi -o ./asyncapi.yaml
nb catalog asyncapi --format json -o ./asyncapi.json
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file (default: `./asyncapi.yaml`, or `./asyncapi.json` for `--format json`) |
| `-f`, `--format` | No | `yaml` (default) or `json`. When omitted, an `.json` output path is auto-detected as JSON. |

Generates an AsyncAPI 3.0 specification with:
- **Servers** — Azure Service Bus namespace (AMQP 1.0), with an `x-nimbus-topology` extension describing the topic-per-endpoint pattern, SQL-rule routing, and auto-forwarding.
- **Channels** — one per endpoint **topic** (both producers and consumers, since a consumer's own topic carries the auto-forwarded copy), with `x-servicebus` topic bindings.
- **Operations** — a `send` per producer and a `receive` per consumer. Each `receive` carries an `x-servicebus-delivery` extension documenting the **physical delivery path**: the consumer's own session subscription (`user.To = '<endpoint>'`) plus the forward subscription(s) on each producer topic (filter `user.EventTypeId = 'X' AND user.From IS NULL`, the rewrite action, and `forwardTo`).
- **Messages** — event types with a shared `NimBusMessageHeaders` header schema (the `user.*` application properties), `x-servicebus` message settings (session requirement, dead-letter, `MessageId`/`CorrelationId` conventions), an example payload, and `[Description]`/`[AsyncApiMessage]` enrichment.
- **Schemas** — JSON Schema from C# types (formats, required from `[Required]`/non-nullable, `[Range]`, enums, collections, and nested objects). Dynamically-typed events (spec 022 `DynamicForward`) appear as messages flagged `x-nimbus-dynamic`.

> **Mapping note.** Because there is no official AsyncAPI Service Bus binding, the document keeps portable **logical** channels/operations and carries Service Bus specifics via `x-servicebus*` / `x-nimbus*` specification extensions. See [`docs/asyncapi-mapping.md`](asyncapi-mapping.md) for the full NimBus → AsyncAPI concept mapping.

The spec can be used with:
- [EventCatalog AsyncAPI plugin](https://www.eventcatalog.dev/integrations/asyncapi) for architecture visualization
- [AsyncAPI HTML template](https://github.com/asyncapi/html-template) for documentation
- Schema validation and contract testing tools

> **Note.** `nb catalog asyncapi` is a backward-compatible alias for `nb asyncapi export` (below) and produces identical output from the same code path.

---

### `nb asyncapi`

Generate, validate, and diff AsyncAPI 3.0 documents for CI/CD governance. Three subcommands:

#### `nb asyncapi export`

Export the platform topology as an AsyncAPI 3.0 specification — identical generation to `nb catalog asyncapi`.

```bash
nb asyncapi export -o ./asyncapi.yaml
nb asyncapi export --format json -o ./asyncapi.json
# include fluent Publish<T>(o => o.AsyncApi…) enrichment recorded by a host assembly:
nb asyncapi export --assembly ./bin/MyApp.dll -o ./asyncapi.yaml
```

| Option | Required | Description |
|---|---|---|
| `-o`, `--output` | No | Output file (default: `./asyncapi.yaml`, or `./asyncapi.json` for `--format json`) |
| `-f`, `--format` | No | `yaml` (default) or `json`. When omitted, an `.json` output path is auto-detected as JSON. |
| `-a`, `--assembly` | No | Path to a host assembly exposing an `IAsyncApiDocumentProvider` or `IAsyncApiDocumentProviderFactory`. Use to include **fluent** `Publish<T>(o => o.AsyncApi…)` enrichment, which lives in the host's DI container and cannot be observed from the static built-in platform. When omitted, the built-in `PlatformConfiguration` is exported (attribute enrichment only). |
| `-p`, `--provider` | No | Provider/factory type name (full or simple) to select when `--assembly` exposes more than one candidate. |

Because `AddNimBusAsyncApiDocument(platform, (p, f, r) => AsyncApiExporter.Serialize(p, f, r))` registers a **private, DI-backed** `IAsyncApiDocumentProvider` (it has constructor dependencies the standalone CLI cannot instantiate), a host bridges to it by exposing a public, parameterless `IAsyncApiDocumentProviderFactory` whose `Create()` builds its container and resolves the provider:

```csharp
public sealed class MyAsyncApiFactory : IAsyncApiDocumentProviderFactory
{
    public IAsyncApiDocumentProvider Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(connectionString));
        services.AddNimBusPublisher("Orders", b => b.Publish<OrderPlaced>(o => o.AsyncApi.Owner = "orders-team"));
        services.AddNimBusAsyncApiDocument(platform, (p, f, r) => AsyncApiExporter.Serialize(p, f, r));
        return services.BuildServiceProvider().GetRequiredService<IAsyncApiDocumentProvider>();
    }
}
```

The CLI loads the assembly (`Assembly.LoadFrom`), instantiates the factory (or a directly-exposed public parameterless `IAsyncApiDocumentProvider`), and writes `GetDocument(format)` — so fluent enrichment surfaces from the CLI export. Export exits non-zero with a message when the assembly, factory, or provider cannot be loaded.

#### `nb asyncapi validate <file>`

Structurally validate a generated or hand-authored AsyncAPI 3.0 document. Checks `asyncapi: 3.0.0`; the presence of `info`, `channels`, `operations`, `components`; and that every `$ref` resolves to the **correct** section for its context (operation → channel; operation message → channel-scoped message → component message, or directly to component message; channel message → component message; message `payload`/`headers` → a component **schema**). A payload `$ref` that points at a non-schema node is rejected.

```bash
nb asyncapi validate ./asyncapi.yaml
```

**Exit codes** (for CI gating): `0` valid, non-zero when invalid (errors are printed, one per line).

#### `nb asyncapi diff <old-file> <new-file>`

Compare two AsyncAPI documents, classify added / removed / changed channels, operations, messages, and schemas (including schema properties), and flag breaking changes.

```bash
nb asyncapi diff ./asyncapi.previous.yaml ./asyncapi.yaml
```

Treated as **breaking**: a removed channel / operation / message / schema; a removed property or a property that becomes newly required; a property **effective-shape** change (`type`/`format`/`$ref`/array `items`); a removed enum value (including on an array `items` schema); a **tightened** validation bound (a `[Range]`-derived `minimum` raised or `maximum` lowered, or one newly added); a message-association removed from an operation; a same-key channel message whose `$ref` is **retargeted** to a different component message; an operation `action` flip or `channel` retarget; a `payload`/`headers` `$ref`, `contentType`, or session-semantics (`x-servicebus.requiresSession`/`sessionKeyProperty`) change. Additive/informational changes are **non-breaking** and still reported: new channels/operations/messages/schemas/properties, added enum values, a **relaxed or removed** validation bound (`minimum` lowered / `maximum` raised), and metadata changes at both the property level (`description`) and the schema root (`title` / `description` / `deprecated`) — so a metadata-only schema delta is still reported, never swallowed as "No differences".

**Exit codes** (for build gating): `0` when the only differences are non-breaking, non-zero when a breaking change is detected.

---

## Examples

### Full environment setup

```bash
# Default — Cosmos DB backend
nb setup --solution-id nimbus --environment dev --resource-group rg-nimbus-dev
```

### Deploy with SQL Server as the storage provider

Provision a fresh Azure SQL server + database (managed-identity auth from the WebApp / Resolver):

```powershell
$env:NIMBUS_SQL_ADMIN_PASSWORD = '<strong-password>'
nb setup `
  --solution-id nimbus --environment dev --resource-group rg-nimbus-dev `
  --storage-provider sqlserver `
  --sql-mode provision `
  --sql-admin-login nimbusadmin
Remove-Item Env:NIMBUS_SQL_ADMIN_PASSWORD
```

Reuse an existing SQL Server:

```powershell
$env:NIMBUS_SQL_CONNECTION_STRING = 'Server=tcp:my-existing.database.windows.net,1433;Initial Catalog=MessageDatabase;Authentication=Active Directory Default;Encrypt=true;'
nb setup `
  --solution-id nimbus --environment dev --resource-group rg-nimbus-dev `
  --storage-provider sqlserver `
  --sql-mode external
Remove-Item Env:NIMBUS_SQL_CONNECTION_STRING
```

The same options and environment variables work on `nb infra apply` if you prefer running infrastructure, topology, and app deployment as separate steps.

### Operational maintenance

```bash
# Purge old failed messages
nb container delete my-endpoint -s failed,deadlettered -dbc "..."

# Skip failed messages older than a week, so the sessions they block continue
nb container skip my-endpoint -s failed --before 2026-03-20T00:00:00 -sbc "..." -dbc "..."

# Clean up a blocked session
nb endpoint session delete my-endpoint session-123 -sbc "..." -dbc "..."

# Remove stale Service Bus subscriptions
nb endpoint topics removeDeprecated my-endpoint -sbc "..."
```

### Generate architecture documentation

```bash
# EventCatalog markdown
nb catalog export -o ./docs/eventcatalog

# AsyncAPI specification
nb catalog asyncapi -o ./docs/asyncapi.yaml
```

## Key Source Files

| File | Purpose |
|---|---|
| `src/NimBus.CommandLine/Program.cs` | Command definitions and CLI entry point |
| `src/NimBus.CommandLine/Endpoint.cs` | Session delete, topic cleanup, subscription purge |
| `src/NimBus.CommandLine/Container.cs` | Cosmos DB operations (delete, resubmit, copy, skip) |
| `src/NimBus.CommandLine/CommandRunner.cs` | Connection string handling and client factory |
| `src/NimBus.CommandLine/EventCatalogExporter.cs` | EventCatalog native-MDX catalog builder (pure, in-memory) |
| `src/NimBus.CommandLine/EventCatalogCli.cs` | `nb catalog export` disk semantics: scaffold, refresh, exit codes |
| `src/NimBus.CommandLine/PlatformLoader.cs` | Loads a public parameterless `IPlatform` from a host assembly |
| `src/NimBus.ServiceBus/AsyncApi/AsyncApiExporter.cs` | AsyncAPI 3.0 generation (canonical; the CommandLine `AsyncApiExporter.cs` is an obsolete bridge) |
| `src/NimBus.ServiceBus/AsyncApi/JsonSchemaBuilder.cs` | Shared reflection JSON Schema generation (AsyncAPI components + standalone `schema.json`) |
| `src/NimBus.CommandLine/ServiceBusTopologyProvisioner.cs` | Service Bus topology provisioning |
| `src/NimBus.CommandLine/InfrastructureDeployer.cs` | Azure infrastructure deployment |
| `src/NimBus.CommandLine/AppDeploymentService.cs` | App build and deployment |
| `src/NimBus.CommandLine/ColoredHelpTextGenerator.cs` | Colored help output |
