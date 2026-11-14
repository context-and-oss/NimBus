# Storage Providers

NimBus persists message tracking, audit, resolver state, subscription, endpoint
metadata, and metrics data behind a set of provider-neutral contracts. Today there
are two implementations:

| Provider | Package | When to use |
|---|---|---|
| SQL Server | `NimBus.MessageStore.SqlServer` | Most deployments: the lowest cost at small and medium load, and the same price however many endpoints you add |
| Cosmos DB | `NimBus.MessageStore.CosmosDb` | High or bursty load, a managed-identity-only data path today, or an organization that standardizes on Cosmos |

[Choosing a provider](#choosing-a-provider) compares them, including what each costs at different
loads.

The Cosmos provider used to ship as `NimBus.MessageStore`. Update your
package reference to `NimBus.MessageStore.CosmosDb` and your registration
call to `AddCosmosDbMessageStore(...)`. Type namespaces are unchanged.

Exactly one provider must be registered per running application instance. The
NimBus builder validates this at `Build()` time and fails fast otherwise.

Each provider keeps a single public aggregate facade for `INimBusMessageStore`
and `IHeartbeatHistoryStore`. The facade delegates to provider-internal stores
for message tracking, subscriptions, endpoint metadata, heartbeat history, and
service health; this keeps registration and public construction compatibility
while allowing each persistence concern to evolve independently.

## Choosing a provider

Both providers implement the same contracts and pass the same conformance suite, so the
Resolver, the WebApp, the MCP server and the SDK behave the same on either. They differ in price,
in how they scale, in how the apps authenticate, and in a few operator tools.

### At a glance

| | SQL Server (Azure SQL) | Cosmos DB |
|---|---|---|
| What `nb` deploys | One Azure SQL database at **S0** (10 DTUs, 250 GB included) | One Cosmos account: 13 platform containers plus one container per endpoint, each at **400 RU/s** manual throughput |
| Fixed monthly cost | ~$15, whatever the endpoint count | ~$304, plus ~$23 per endpoint |
| Adding an endpoint | No storage change | `nb topology apply` creates the endpoint's container |
| Scaling | Change the database tier. `nb` deploys S0, and every `nb infra apply` sets it back to S0, so scale up in the portal again after each run | Raise a container's throughput; each container scales separately |
| App authentication | SQL login and password in the `SqlConnection` app setting | Managed identity only; no secrets |
| Bring your own store | Not usable yet: `--sql-mode external` configures the WebApp but not the Resolver ([known gap](#sql-only-deployment)) | No: `nb` always creates the account |
| Data retention | **Nothing is deleted**: tracking rows, messages and audits stay until you remove them, so the database keeps growing toward its tier's limit (250 GB on S0) | Expires automatically: completed rows after 30 days, messages after 90, audits after a year ([details](#retention-of-unresolved-rows)) |
| Cosmos-only tools | — | `nb container` commands, Copy Endpoint Data, Topology → Storage ([details](#operator-tools-that-are-cosmos-only-in-v1)) |
| Deploy pipelines (`deploy.yml`, `pipelines/azure-pipelines-deploy.yml`) | Not yet: neither passes `--storage-provider`, so both deploy Cosmos | Yes |

### Price at different loads

The table estimates the monthly bill for storage and throughput in four scenarios. A *delivery* is
one event processed for one subscribing endpoint, so an event that two endpoints subscribe to
counts twice. The Service Bus, App Service and Function App costs are the same for both providers
and aren't included.

| Scenario | Deliveries a day | Endpoints | Stored data | Cosmos load (average) | Cosmos DB as `nb` deploys it | SQL Server, suggested starting tier |
|---|---|---|---|---|---|---|
| Dev / test | 1,000 | 5 | 1 GB | ~1 RU/s | **~$421** (18 containers) | **~$15** (S0) |
| Small | 50,000 | 10 | 10 GB | ~29 RU/s | **~$540** (23 containers) | **~$15** (S0) |
| Medium | 500,000 | 20 | 50 GB | ~290 RU/s | **~$783** (33 containers) | **~$29–$74** (S1–S2) |
| Large | 5,000,000 | 50 | 200 GB | ~2,900 RU/s | **~$1,522**, plus raising the shared `messages` container above 400 RU/s ($5.84 a month per extra 100 RU/s) | **~$147–$294** (S3–S4), scaled in the portal |

How the numbers are worked out:

- **Prices** are US list prices for one region. Azure SQL DTU tiers: Basic $4.90 (2 GB), S0
  $14.72, S1 $29.43, S2 $73.61, S3 $147.19, S4 $294.38 a month, each S tier with 250 GB included.
  Cosmos: manual throughput at $0.008 per 100 RU/s-hour, so 400 RU/s is $23.36 a month, and
  storage at $0.25 per GB-month. Check the
  [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) for your region.
- **Cosmos load** assumes about **50 RU per delivery**. That is an estimate from counting the
  Resolver's store calls: each delivery is at least two messages (the event and its response),
  each stored in `messages` and written to the endpoint's tracking row. It has not been measured.
  The WebApp's own reads come on top.
- **Cosmos cost** is the throughput floor plus storage. Up to the Medium scenario the floor
  covers the load. In the Large scenario, about half the load lands on the shared `messages`
  container, which then needs more than its 400 RU/s.
- **SQL tiers are a starting point, not a measurement.** How many deliveries a DTU carries for
  NimBus has not been benchmarked. Start at the suggested tier, watch the database's **DTU
  percentage** in the portal or Azure Monitor, and move up a tier when it stays above about 80%.
  `nb` deploys S0 only, and every `nb infra apply` sets the database back to S0. For a larger tier,
  scale the database in the portal or with `az sql db update --service-objective`, and again after
  each `nb infra apply`.

### Which to choose

Choose **SQL Server** when:

- the load is small or medium. It costs a fraction of Cosmos's fixed floor, and endpoints add
  nothing;
- your organization already runs Azure SQL or SQL Server, or doesn't allow Cosmos DB.

Bringing your own SQL database (`--sql-mode external`) will also be a reason to choose SQL Server,
once the Resolver gap described under [SQL-only deployment](#sql-only-deployment) is fixed.

**Plan for SQL storage growth.** The SQL provider never deletes old data. Watch the database's
used space, and delete old rows yourself before it reaches the tier's limit: Completed and Skipped
rows in `UnresolvedEvents`, old `Messages` and old `MessageAudits`. Automatic cleanup is planned.

Choose **Cosmos DB** when:

- the load is high or very bursty and you would rather raise throughput per container than size
  and change a database tier;
- you need a data path with no secrets today. The provisioned SQL path puts the SQL admin password
  in an app setting;
- your operators depend on the [Cosmos-only tools](#operator-tools-that-are-cosmos-only-in-v1);
- you deploy through the `deploy.yml` workflow or the Azure Pipelines template, which support only
  Cosmos so far. SQL deployments run `nb` directly, passing `--storage-provider sqlserver` to
  `infra apply`, `topology apply` and `setup` on every run.

Switching an existing deployment between providers isn't supported in place: NimBus doesn't
migrate data between them. Choose at the first deployment.

## Status writes: the stale-write guard

`IMessageTrackingStore.Upload*Message` returns `Task<bool>`. Since 3.7.0 that bool
means something:

- `true` — the audit row was created or replaced.
- `false` — the write was **refused** because the row already holds a later
  outcome. Nothing was written, and the caller must not retry: the row is correct.
- provider failures, including a lost compare-and-swap, **throw**. An unavailable
  store is never reported as a refusal.

The rule lives in one place, `NimBus.MessageStore.StaleWriteGuard`, and every
provider applies it *inside* its non-terminal status write, atomically against the
row it is about to replace — Cosmos with a point read plus an `IfMatchEtag`
replace, SQL Server as a `WHEN MATCHED` predicate on the `UnresolvedEvents` MERGE
under `HOLDLOCK`, the in-memory store inside its `AddOrUpdate`. It decides from
three fields every provider already persists: the row's `ResolutionStatus`, the
`MessageType` that last wrote it, and its `ParentMessageId`.

What it refuses:

| Incoming write | Refused when the existing row is |
|---|---|
| `EventRequest`, `DeferralResponse` (a request-stage write) | terminal, or written by a control request or a handoff park |
| `PendingHandoffResponse` (a handoff park) | `Completed` or `Skipped` |
| `ResubmissionRequest`, `SkipRequest`, `RetryRequest`, `ContinuationRequest`, `HandoffCompletedRequest`, `HandoffFailedRequest` (control requests) | one whose `ParentMessageId` is this message's own id — the row already holds this message's outcome |

Terminal writes (`Failed`, `DeadLettered`, `Unsupported`, `Skipped`, `Completed`)
and content carrying `MessageType.Unknown` — CLI and test seeds — are unguarded and
always apply. Terminal-over-terminal reorders stay last-writer-wins.

Why: a copy of a message can reach the Resolver after the outcome it belongs to,
through auto-forward lag, a `ScheduleRedelivery` re-send, a dead-letter replay or a
double deferred drain. Without the guard its `Pending` projection replaced the
settled row; see [Spec 030](spec/030-stale-pending-guard/spec.md) for the incident.

**If you are writing a provider**, the guard is not optional — the shared
conformance suite fails you until you apply it, and a refusal must be the only
reason you return `false`.

### The one conditional terminal write

`IMessageTrackingStore.TryCompletePendingMessage(eventId, sessionId, endpointId,
expectedLastMessageId, content)` replaces a row with a Completed projection **only if** the row
still exists, is still `Pending`, is not soft-deleted, and its `LastMessageId` still equals
`expectedLastMessageId` (ordinal; null matches null). It returns `true` only when it replaced the
row and `false` for every refusal — missing, already terminal, another last message id, or a lost
compare-and-swap. There is no retry: a lost race means someone else decided, and the caller
re-reads. Provider failures throw.

It exists for one caller, the operator reconcile of rows corrupted before the guard shipped
([Spec 032](spec/032-stale-pending-reconcile/spec.md), driven from the Operations page). Every other
terminal write stays unguarded and unconditional.

The interface ships a **default implementation** — read the row, compare, then
`UploadCompletedMessage` — so an external provider keeps compiling. That fallback is *not* atomic
and says so in its XML docs. The three built-in providers override it with a single conditional
statement: Cosmos DB an `IfMatchEtag` upsert after a point read (412 → `false`), SQL Server one
`UPDATE … WHERE Status = 'Pending' AND LastMessageId = @Expected` with `@@ROWCOUNT`, the in-memory
store a `ConcurrentDictionary.TryUpdate`. The shared conformance suite pins the behaviour for all
of them.

## Lookups: missing rows and absent fields

Every provider answers single-row lookups the same way. The shared conformance suites pin
these rules:

- **A missing row is `null`, never an exception.** This covers `GetPendingEvent`,
  `GetFailedEvent`, `GetDeferredEvent`, `GetDeadletteredEvent`, `GetUnsupportedEvent`,
  `GetEvent`, `GetEventById`, `GetPendingHandoffByExternalJobId`, `GetMessage`,
  `GetLatestEventRequestMessage`, `GetFailedMessage`, `GetDeadletteredMessage` and
  `GetEndpointMetadata`. A row in another status or session, or one that was removed or
  archived, is also missing. `EndpointNotFoundException` means only that the endpoint's
  storage does not exist.
- **Optional strings round-trip exactly.** A field written as `null` reads back as `null`,
  and one written as `""` reads back as `""`.
- `GetEvent(endpointId, eventId)` returns the most recently updated row across sessions.
- `GetEventById` matches the stored id `{EventId}_{SessionId}`.
- `GetFailedMessage` returns the newest message on the endpoint that carries
  `ErrorContent`. `GetDeadletteredMessage` returns the newest message of any type.

Before v4.0.0, SQL Server and the in-memory store threw `EndpointNotFoundException` or
`MessageNotFoundException` where Cosmos DB returned `null`, and SQL Server read NULL text
columns back as `""`. SQL Server also dropped the handoff fields (`PendingSubStatus`,
`HandoffReason`, `ExternalJobId`, `ExpectedBy`) from message history rows.

## Cosmos DB

Add the package and register:

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddCosmosDbMessageStore();
});
```

Reads connection from configuration in this order:
1. `CosmosAccountEndpoint` — endpoint URL (uses `DefaultAzureCredential` for AAD)
2. Connection string named `cosmos`
3. `CosmosConnection` configuration value

For an explicit `CosmosClient` (tests, advanced scenarios):

```csharp
nimbus.AddCosmosDbMessageStore(myCosmosClient);
```

### Retention of unresolved rows

Unresolved tracking rows — the ones in `Pending`, `Failed`, `Deferred`,
`DeadLettered` or `Unsupported` — used to be written with expiry disabled, so a
failed event kept its full payload forever. `UnresolvedRetentionDays` makes that
an explicit operator decision.

| | |
|---|---|
| Option | `CosmosDbMessageStoreOptions.UnresolvedRetentionDays` |
| Configuration key | `NimBus:Cosmos:UnresolvedRetentionDays` |
| Environment form | `NimBus__Cosmos__UnresolvedRetentionDays` |
| Valid values | `-1` (unlimited — the default) or `1`–`365` whole days |
| Recommended when a bound is required | `180` or `365` |

The one-year maximum is a deliberate product bound, not a Cosmos limit — Cosmos
stores `ttl` as integer seconds and would accept far longer windows. It keeps the
longest configurable retention of a payload-bearing unresolved row at or below the
one-year retention of the audit documents that describe it. Operators who need a
longer window keep the default, `-1` (unlimited).

```csharp
nimbus.AddCosmosDbMessageStore(options => options.UnresolvedRetentionDays = 365);
```

```json
{
  "NimBus": {
    "Cosmos": {
      "UnresolvedRetentionDays": 365
    }
  }
}
```

Configuration binds first and the `configure` delegate runs after it, so code wins
over configuration. An invalid value fails host startup, naming the option and the
offending value — it does not wait for the first write.

The retention applies **only** to the five non-terminal statuses, and the window
slides forward on every rewrite of the row: an event that keeps retrying is
re-stamped on each write and does not expire mid-flight.

Everything else keeps its existing, hardcoded retention:

| Document | Retention |
|---|---|
| Unresolved rows (Pending / Failed / Deferred / DeadLettered / Unsupported) | configurable, unlimited by default |
| Terminal rows (Completed / Skipped) | 30 days |
| Soft-deleted rows | 60 seconds |
| Archived failed rows | 30 days |
| Per-message documents | 90 days |
| Audit documents | 1 year |

> **Expiry deletes the entire tracking document.** The audit metadata and the
> ability to resubmit that event are deleted with it — TTL is not a
> payload-stripping archive. Operators who must keep the metadata should stay on
> unlimited retention until the archive-style option exists (tracked separately as
> GH#94 fix option 2).

#### The setting applies to newly written documents only

Changing `UnresolvedRetentionDays` does not touch documents already in the store;
each keeps whatever `ttl` it was written with until it is rewritten. To backfill,
run this per endpoint container and patch each hit:

```sql
SELECT c.id FROM c
WHERE (c.ttl = -1 OR NOT IS_DEFINED(c.ttl))
  AND c.status IN ("Pending","Failed","Deferred","DeadLettered","Unsupported")
```

then `PatchOperation.Set("/ttl", <seconds>)` on each id. The `NOT IS_DEFINED`
branch matters: `nb container copy` and the WebApp copy both strip `ttl` before
upserting, so copied documents carry no `ttl` field at all. No tooling ships for
this; it is an operator action.

#### Container-level TTL on endpoint containers created before this change

Cosmos honours a document's `ttl` **only** when the container's
`DefaultTimeToLive` is set. Endpoint containers created before this change have it
unset, so every item TTL in them is inert — including the 30-day terminal and
60-second soft-delete values that have been written all along. Configuring
`UnresolvedRetentionDays` against such a container deletes nothing until an
operator enables it.

To enable it: Azure Portal → the Cosmos account → Data Explorer → the endpoint
container → **Settings → Time to Live → On (no default)**. Containers NimBus
creates from this change on need no action.

> **Flipping TTL on for an existing container activates the positive TTLs already
> stored in it.** Rows written earlier as terminal or archived (`ttl = 2592000`) or
> soft-deleted (`ttl = 60`) become deletion-eligible relative to their existing
> `_ts`, so anything already past that age is deleted shortly after the switch —
> soft-deleted rows effectively immediately. Export or back up the container
> **before** enabling TTL if you need those rows.

`DefaultTimeToLive = -1` means "TTL on, no container default": documents with no
`ttl`, and documents with `ttl = -1`, are still never expired.

#### Reserved endpoint ids

An endpoint may not be named `subscriptions`, `messages`, `audits`,
`eventschemas`, `eventreports`, `accesscontrol`, `Metadata` or `inbox` — those are
the store's own containers. Sharing one would mix endpoint tracking rows with
store data and make the container's TTL mode depend on call order, so the store
now rejects such an id instead. Comparison is case-sensitive, matching Cosmos:
`Messages` is a different container from `messages` and is allowed. An operator
who has renamed the inbox container via `CosmosInboxOptions.ContainerId` must
avoid that name too; only the default `inbox` is enforced in code.

#### Custom `ICosmosDatabaseAdapter` implementations

Endpoint-container creation now goes through
`CreateContainerIfNotExistsAsync(ContainerProperties, CancellationToken)`. An
adapter that does not implement it throws `NotSupportedException` rather than
silently creating a TTL-disabled container that accepts item TTLs and never
expires anything. The fix is a one-method override forwarding to
`Database.CreateContainerIfNotExistsAsync(containerProperties, …)`.

#### Provider scope

The SQL Server provider has no TTL mechanism and is unaffected by this setting;
retention there needs a delete job and is tracked separately.

## SQL Server

Add the package and register:

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddSqlServerMessageStore(options =>
    {
        // Optional. Defaults below.
        options.Schema = "nimbus";
        options.ProvisioningMode = SchemaProvisioningMode.AutoApply;
    });
});
```

Reads connection from configuration in this order:
1. `SqlConnection` configuration value
2. Connection string named `sqlserver`
3. `SqlServerConnection` configuration value

The provider registers an `IHostedService` that runs DbUp on startup. The schema
scripts are embedded in the package and applied idempotently to the configured
schema (default `nimbus`).

### Provisioning modes

| Mode | Behavior |
|---|---|
| `AutoApply` (default) | Apply pending DbUp scripts on startup. Best for development and managed environments. |
| `VerifyOnly` | Read the DbUp journal table and fail fast if any embedded script is unapplied. Best for production environments where DDL is performed by the deployment pipeline. |

When using `VerifyOnly`, run DbUp from the deployment pipeline against an
arbitrary connection string before the application starts.

### Schema layout

Single table per concern with `EndpointId` as a discriminator (no per-endpoint
table). Composite indexes target the dominant queries: per-endpoint status
counts, recent-events lists, per-event lookups. See
`src/NimBus.MessageStore.SqlServer/Schema/` for the canonical scripts.

## SQL-only deployment

To deploy NimBus without Cosmos DB at all (no Cosmos resources provisioned, no
Cosmos secrets required, no Cosmos packages referenced):

```bash
export NIMBUS_SQL_ADMIN_PASSWORD='<strong-password>'
nb infra apply \
  --solution-id mybus \
  --environment prod \
  --resource-group rg-mybus-prod \
  --storage-provider sqlserver \
  --sql-mode provision \
  --sql-admin-login dbadmin
unset NIMBUS_SQL_ADMIN_PASSWORD
```

Or to use an externally-provisioned SQL Server:

```bash
export NIMBUS_SQL_CONNECTION_STRING='Server=tcp:...;Initial Catalog=MessageDatabase;...'
nb infra apply \
  --solution-id mybus \
  --environment prod \
  --resource-group rg-mybus-prod \
  --storage-provider sqlserver \
  --sql-mode external
unset NIMBUS_SQL_CONNECTION_STRING
```

**Known gap in external mode.** The connection string reaches only the WebApp. The core template
sets the Resolver's `SqlConnection` only for `--sql-mode provision`. So the Resolver starts with no
storage setting, falls back to Cosmos and fails. Adding the setting by hand doesn't last, because
the next `nb infra apply` replaces the Resolver's app settings. Until this is fixed, use
`--sql-mode provision`.

The Bicep templates skip Cosmos provisioning entirely when `storageProvider == 'sqlserver'`.

## Local development with Aspire

The AppHost reads `NIMBUS_STORAGE_PROVIDER`:

```bash
# SQL Server (default) — Aspire pulls the mssql container, creates the 'nimbusdb' database,
# and wires the connection string into the WebApp/Resolver automatically.
dotnet run --project src/NimBus.AppHost

# Cosmos — requires a Cosmos connection string in the 'cosmos' parameter
NIMBUS_STORAGE_PROVIDER=cosmos dotnet run --project src/NimBus.AppHost
```

The SQL Server container is provisioned with a persistent data volume,
so tables and seeded users survive AppHost restarts. Docker Desktop must
be running.

### Local sign-in via NIMBUS_IDENTITY

Setting `NIMBUS_IDENTITY=true` when launching the AppHost wires the
`NimBus.Extensions.Identity` package into the management WebApp for the
duration of the Aspire run — the WebApp serves cookie-based
username/password sign-in at `/account/login` instead of the default
Entra ID flow. Off by default; the rest of the local-dev experience is
unchanged unless the env var is set.

Identity needs SQL, so flipping the switch also auto-provisions the
Aspire-managed SQL Server container even when the message store is
Cosmos. The container, the `nimbusdb` database, the `nimbus` schema,
the eight `AspNet*` tables, and the bootstrap admin are all created on
first run — no user-secrets setup required.

**Launch.**

```powershell
# PowerShell
$env:NIMBUS_IDENTITY = "true"
dotnet run --project src/NimBus.AppHost
```

```bash
# bash / zsh
NIMBUS_IDENTITY=true dotnet run --project src/NimBus.AppHost
```

Args form also works (`dotnet run --project src/NimBus.AppHost -- --NIMBUS_IDENTITY true`).

**First sign-in.** Open the WebApp URL from the Aspire dashboard.
Unauthenticated requests redirect to `/account/login`. Sign in as:

| Field | Default | Override env var |
|---|---|---|
| Email | `admin@local` | `NIMBUS_IDENTITY_ADMIN_EMAIL` |
| Password | `Local!Admin123` | `NIMBUS_IDENTITY_ADMIN_PASSWORD` |

The defaults are also printed to the AppHost console on start-up. A
successful sign-in sets a `NimBus.Identity` cookie and lands the SPA.

```powershell
$env:NIMBUS_IDENTITY = "true"
$env:NIMBUS_IDENTITY_ADMIN_EMAIL = "you@example.com"
$env:NIMBUS_IDENTITY_ADMIN_PASSWORD = "<your-pwd>"
dotnet run --project src/NimBus.AppHost
```

**Bootstrap is one-shot.** The admin is created only when the user
store is empty. After the first sign-in, change the password from the
UI; the override env vars become inert on subsequent runs. Drop the
`nimbus.AspNet*` tables to reseed.

**Security.** These defaults are for local dev only. For Azure deployment,
pass `--identity-admin-email` and set `NIMBUS_IDENTITY_ADMIN_PASSWORD` only
in the environment of the `nb setup` process (see *SQL-only deployment*
above). Remove it after the command completes. Never set
`NIMBUS_IDENTITY_ADMIN_PASSWORD=Local!Admin123` on a deployed slot.

Implementation reference: `src/NimBus.AppHost/Program.cs` (the env-var
resolution and validation block) and
`docs/sdk-api-reference.md` § Identity Extension (the underlying
`AddNimBusIdentity` surface).

## Operator tools that are Cosmos-only in v1

These operator tools currently work only with the Cosmos provider:

- **`nb container` commands** (`delete`, `resubmit`, `skip`, `copy`, `event delete`,
  `message delete`) connect to the Cosmos account directly. On SQL deployments, use the WebApp's
  operations or the MCP tools, which work on either provider.
- **Topology → Storage** lists and deletes Cosmos containers. The WebApp hides it on SQL
  deployments.
- **Copy Endpoint Data** (WebApp Advanced Operations + `nb` CLI) — uses Cosmos
  cross-account container copy. SQL deployments should use SQL-native
  backup/restore tools instead. The WebApp surfaces a clear error when the
  active provider does not support it.
- **Storage hook webhook** — Cosmos Change Feed → Event Grid → SignalR. SQL
  deployments receive the same SignalR push events from the Resolver
  write-path (provider-neutral), so realtime UI updates work the same way for
  the operator.

## Adding a new provider

A provider package implements `INimBusMessageStore` (which aggregates the four
storage contracts: `IMessageTrackingStore`, `ISubscriptionStore`,
`IEndpointMetadataStore`, `IMetricsStore`) and registers an
`IStorageProviderRegistration`. Run the shared
`MessageTrackingStoreConformanceTests` (in `NimBus.Testing.Conformance`) against
your implementation.

See `NimBus.MessageStore.SqlServer` for a complete reference implementation.
