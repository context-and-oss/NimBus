# Storage Providers

NimBus persists message tracking, audit, resolver state, subscription, endpoint
metadata, and metrics data behind a set of provider-neutral contracts. Today there
are two implementations:

| Provider | Package | When to use |
|---|---|---|
| Cosmos DB | `NimBus.MessageStore.CosmosDb` | Greenfield Azure deployments where Cosmos is approved |
| SQL Server | `NimBus.MessageStore.SqlServer` | Organizations whose approved infrastructure does not include Cosmos DB |

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

### Retention

SQL Server has no TTL, so a retention job deletes old rows on a schedule that
mirrors the Cosmos DB TTLs (see [Retention of unresolved rows](#retention-of-unresolved-rows)
for the Cosmos DB side). Each rule compares against a cutoff of "now (UTC) minus the
retention period", computed once at the start of each run; a row exactly at the
cutoff is kept.

| Rule | Table | Deletes rows where | Period |
|---|---|---|---|
| R1 | `Messages` | `CreatedAtUtc` is older than the cutoff | 90 days (fixed) |
| R2 | `MessageAudits` | `CreatedAtUtc` is older than the cutoff | 365 days (fixed) |
| R3 | `UnresolvedEvents` | `Deleted = 1` (archived or removed) and `UpdatedAtUtc` is older than the cutoff | 30 days (fixed) |
| R4 | `UnresolvedEvents` | `Deleted = 0` (any status) and `UpdatedAtUtc` is older than the cutoff | `UnresolvedRetentionDays`; never by default |

No other table is touched.

| Option (`SqlServerMessageStoreOptions`) | Configuration key | Environment form | Default | Valid values |
|---|---|---|---|---|
| `UnresolvedRetentionDays` | `NimBus:SqlServer:UnresolvedRetentionDays` | `NimBus__SqlServer__UnresolvedRetentionDays` | `-1` (never) | `-1` or `1`–`365` whole days |
| `RetentionEnabled` | `NimBus:SqlServer:RetentionEnabled` | `NimBus__SqlServer__RetentionEnabled` | `true` | `true` / `false`; `false` issues no deletes |
| `RetentionInterval` | `NimBus:SqlServer:RetentionInterval` | `NimBus__SqlServer__RetentionInterval` | `01:00:00` (1 hour) | greater than zero, at most 49 days |

As on Cosmos DB, configuration binds first and the `AddSqlServerMessageStore(configure)`
delegate runs after it, so code wins. An invalid value fails host startup with a message
naming the option and the offending value.

**Only the WebApp runs the job.** `AddSqlServerMessageStore()` does not register it;
the WebApp adds it on its SQL Server path with `nimbus.AddSqlServerMessageStoreRetention()`
(calling it twice still registers one job). The Resolver and other hosts never delete.
The first run starts once the schema initializer has finished, and later runs start one
`RetentionInterval` apart. A failed run is logged at Error and the next run still happens.
Each completed run logs one Information entry with the rows deleted per rule and the
run duration.

Each rule deletes with `DELETE TOP (5000)` and repeats until a batch deletes fewer than
5,000 rows, so no single statement locks more than 5,000 rows. Schema script
`0025_RetentionIndexes.sql` adds the indexes that let R1 and R3 seek to old rows; deployments
on `VerifyOnly` must apply it before upgrading.

> **Retention deletes rows permanently.** A tracking row deleted by R4 can no longer be
> resubmitted or skipped, and its payload and status are gone — the same warning as for
> Cosmos DB expiry.

> **Known divergence from Cosmos DB.** On SQL Server, Completed and Skipped tracking rows
> keep `Deleted = 0`, so R3 never deletes them. They are kept until
> `UnresolvedRetentionDays` applies (never, by default), whereas Cosmos DB expires them
> after 30 days.

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

The Bicep templates skip Cosmos provisioning entirely when `storageProvider == 'sqlserver'`.

## Local development with Aspire

The AppHost reads `NIMBUS_STORAGE_PROVIDER`:

```bash
# Cosmos (default — requires ConnectionStrings:cosmos in user-secrets)
dotnet run --project src/NimBus.AppHost

# SQL Server — Aspire pulls the mssql container, creates the 'nimbusdb' database,
# and wires the connection string into the WebApp/Resolver automatically.
NIMBUS_STORAGE_PROVIDER=sqlserver dotnet run --project src/NimBus.AppHost
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

Two operator workflows currently work only with the Cosmos provider:

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
