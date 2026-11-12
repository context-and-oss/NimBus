# Monitor dashboard and dashboard tokens

Date: 2026-11-10. Branch `feat/monitor-dashboard`. File and line references are as of master
`f030350a`. Revised the same day after a cross-model review (Codex); see "Review changes" at
the end.

## Problem

`/Monitor` is a wall display built from one data source: per-endpoint status counts polled
every 5 s (`ClientApp/src/hooks/use-monitor-data.ts:283`). It shows what is failing and queued
right now, but not throughput, success rate, time to handle or history, and its only trend is
ten minutes of samples held in the browser. DIS is replacing its monitor with a dashboard that
adds those. This plan brings the same to NimBus as mockup D ("overview board"): a GO ring and
six tiles, three charts (last hour, last 3 days, time to handle) and one endpoint table.

A wall display also has no good way to sign in today. Loading `/Monitor` without a session
redirects to the login page (`Startup.Pipeline.cs:149-152`), so a wall PC needs a personal or
shared account. This plan adds **dashboard tokens**: a site Owner generates a token, puts it in
the Monitor URL on the wall PC, and can revoke it.

## What was asked, and what this plan adds

Asked: layout D without the per-endpoint sparkline column; site Reader role required; a token
that an Owner can generate and revoke, carried in the Monitor URL.

Added in support, each small and removable: optional token expiry, last-used time, and
deleting tokens that are already revoked or expired (so the list does not grow forever).

## What exists

| Need | State today |
|---|---|
| Status counts, ACK, oldest failure | `POST /api/endpoint/status/count` and `/api/monitor/acknowledgements`. Counts are cached 5 s per endpoint (`Services/StoreResultCache.cs:99`). Subscription status is stamped afterwards by a separate uncached read (`EndpointImplementation.cs:281-282`). `failedCount` includes dead-lettered and unsupported (`Mapper.cs:285`). The ACK list reads the store on every call and prunes expired or recovered rows (`Services/MonitorAcknowledgementService.cs:39-56`) |
| Counts per minute and per hour | `IMetricsStore.GetTimeSeriesMetrics(from, to, substringLength, bucketLabel)`: 16 = minute, 13 = hour. Fleet-wide only. "Failed" here counts `ErrorResponse` messages, i.e. failed attempts, not the unresolved state |
| Per-endpoint published, handled, failed | `IMetricsStore.GetEndpointMetrics(from, to)`. Published is keyed by the sender (`FromAddress`), handled and failed by the receiving endpoint (`SqlServerMetricsStore.cs:44-61`) |
| Per-endpoint queue and processing time | `IMetricsStore.GetEndpointLatencyMetrics(from, to)`: count, average, min, max for each, counted independently. Percentiles are deliberately absent (`MetricsImplementation.cs:91-97`) |
| Latency per time bucket | **Missing.** DIS branch `origin/alh/live-monitor` (in `c:\git\kl\DIS`) has `GetLatencyTimeSeriesMetrics` for Cosmos and SQL |
| Blocked sessions | **Missing.** The authoritative block is Service Bus session state (`src/NimBus.ServiceBus/SessionState.cs`). See decision 7 for what the store can and cannot tell |
| Last heartbeat per endpoint | `IHeartbeatService.GetOverviewAsync()` returns it. The reader-level `/api/heartbeat/page` adds two history queries per call and is uncached |
| Optional store capabilities | `ISubscriptionNotificationStore` and `IHeartbeatHistoryStore`: separate interfaces implemented by the provider aggregates and registered by casting the aggregate (`CosmosDbMessageStoreBuilderExtensions.cs:142-143`). Third-party providers that lack them keep compiling |
| Token issue, hash, revoke, authenticate | Spec 039 agent API keys: `Services/AgentApiKeys/*`, `AgentKeysImplementation.cs`, `IEndpointMetadataStore.cs:114-136`, `Schema/0024_AgentApiKeys.sql`. Agent-specific: no kind or scope field, keys are admitted only on `/api/agent/*` |
| Roles | Metrics and heartbeat require the site Reader role. Status counts and ACK lists filter per endpoint. A principal with no email or `oid` claim resolves to no role (`Services/EndpointAuthorizationService.cs:107-116`) |

## Decisions

1. **One read API for the page: a new `MonitorFeed` controller.** Three GET operations under
   `/api/monitor/feed/`, one per refresh cadence. The page stops calling the endpoint, metrics,
   heartbeat, app-status and access APIs directly. This gives the token exactly one place it is
   admitted, lets every wall share server-side caches, and replaces about eight client calls
   with three. The existing `Monitor` controller stays as it is: the MCP operator tools use its
   endpoint-filtered ACK list (`Mcp/Operations/OperatorQueries.cs:49`).
2. **Access is site Reader or a dashboard token.** A signed-in caller needs
   `HasRoleAsync(AccessRole.Reader)`; otherwise 403. This is a behaviour change: a user with
   only endpoint-level roles sees today's Monitor filtered to their endpoints and will instead
   see a "requires the Reader role" message.
3. **A token is read-only and reaches nothing else.** The token scheme is named only by the
   `MonitorFeed` policy on the `MonitorFeed` controller, the same construction as
   `AgentApiAuthorizationConvention` (`AgentApiKeyServiceCollectionExtensions.cs:62-72`). The
   ACK operations stay on the `Monitor` controller, which does not name the scheme, so a token
   gets 401 there. The token principal is not given a role in `EndpointAuthorizationService`;
   the feed implementation accepts it by its claim.
4. **New capabilities are separate, optional interfaces, not new members on existing ones.**
   Adding required members to `IMetricsStore`, `IMessageTrackingStore` or
   `IEndpointMetadataStore` breaks every third-party provider, which `docs/versioning.md:5`
   reserves for a major release. Instead:
   - `IMonitorMetricsStore` with the latency time series and the blocked-session count;
   - `IDashboardTokenStore` with the four token-record members.

   Both follow `ISubscriptionNotificationStore`: implemented by the Cosmos and SQL aggregates
   and by `InMemoryMessageStore`, registered by casting the aggregate. The feed resolves them
   with `GetService`; when a provider does not offer one, the affected numbers are reported as
   unavailable and the token admin API returns 501. No default interface methods are involved,
   so nothing can silently bypass a real store.
5. **The token travels in the URL fragment: `/Monitor#token=nbdt_…`.** That is "in the URL"
   as asked, and a fragment is never part of an HTTP request target or `Referer`
   (RFC 9110 §10.1.3), so the token stays out of server request logs and Application Insights
   request telemetry. It still lands in browser history, bookmarks and anything that copies
   the address; the Settings panel says so. The page sends the token in an
   `X-NimBus-Dashboard-Token` header; `Authorization: Bearer` is not usable because the "Az"
   policy scheme forwards it to JwtBearer. Neither the header nor the full client URL may be
   logged anywhere, server or client.
6. **The SPA shell for `/Monitor` is served without a session**, through a dedicated fallback
   endpoint (part 3). The server cannot see a fragment, so it cannot decide per request. The
   shell is static and holds no data. Without a token, the page redirects to `/` on the first
   401, which sends the browser to the right login page for the sign-in mode.
7. **Blocked sessions are an estimate from stored status, and the tile says so.** An exact
   count needs Service Bus session state, which is too slow to read per session every few
   seconds. The store-side rule: distinct non-empty `SessionId` among non-deleted unresolved
   events that can be a session's blocker, which is status Failed, status DeadLettered, or
   status Pending with `PendingSubStatus = Handoff`. Deferred rows are not used to find
   sessions: they also arise from replay ordering with no block
   (`StrictMessageHandler.cs:552-561`). Known differences from the real block state, verified
   in `StrictMessageHandler`:
   - A failed attempt blocks its session (`:176-180`): counted.
   - A pending hand-off blocks its session and defers its siblings (`:147-155`, `:528-531`):
     counted, by the hand-off row itself.
   - A retry that fails permanently dead-letters while the session stays blocked
     (`:219`, `:689-690`): counted.
   - A first attempt classified for dead-lettering does not block its session: counted anyway
     (overcount). It still needs operator action.
   - Sessions parked only behind replays: not counted (correct, they are not blocked).

   The tile reads "Blocked sessions" with a tooltip giving this definition, and its subtext
   shows the deferred-message count from the status counts.
8. **Average replaces p95.** The "end-to-end p95" tile becomes "time to handle, last hour":
   average queue wait plus average processing time, each weighted by its own count.
9. **Hand-drawn SVG charts, no chart library on this page.** The current page scales
   everything in `em` from one wall font size (`pages/monitor.tsx:33`); the three charts are
   simple lines and bars and follow the same approach.
10. **Dropped from the mockup:** the per-endpoint sparkline column, the "usual" comparisons on
    tiles, and resubmit lines in the ticker. "Processing against its 24 h average" in the table
    stays.

## Before implementation

`CONTRIBUTING.md:84-89` asks for an issue and an ADR discussion before a PR that touches the
storage abstractions. Open the issue with this plan, and record decisions 4 and 7 in an ADR
under `docs/adr/` unless the discussion concludes one is not needed.

## Metric definitions

All windows end at the request time `now` and start on a bucket boundary, so the current bucket
is partial and the client draws it dashed. The existing bucket generator emits every bucket
start from the floor of `from` while `< to` (`SqlServerMetricsStore.cs:288`), so `from` must be
a boundary for the counts below to hold.

| Series | `from` | Buckets |
|---|---|---|
| Last hour | `floor(now, minute) - 59 min` | 60 minute buckets, the last partial |
| Last 3 days | `floor(now, hour) - 71 h` | 72 hour buckets, the last partial |
| Time to handle | `floor(now, hour) - 23 h` | up to 24 hour buckets, sparse |
| Per-endpoint "last hour" figures | `now - 60 min` | one aggregate |
| Per-endpoint 24 h processing average | `now - 24 h` | one aggregate |

| Number | Definition | Shown when there is no data |
|---|---|---|
| Throughput now | Published in the last *complete* minute bucket | `0` |
| Success rate, last hour | handled ÷ (handled + failed), both counted as outcome messages (`ResolutionResponse`, `ErrorResponse`) in the last-hour window. Labelled "of attempts" because it counts attempts, not the unresolved state | `–` when the denominator is 0 |
| Time to handle, last hour | count-weighted average queue time + count-weighted average processing time, each from its own population | `–` when either count is 0 |
| Table "In" | handled + failed attempts at the endpoint, last hour | `–` when 0 |
| Table "Out" | messages published by the endpoint, last hour | `–` when 0 |
| Table "Fail %" | failed ÷ (handled + failed) at the endpoint, last hour | `–` when the denominator is 0 |
| Table "Unresolved" | `failedCount` from the status counts: failed + dead-lettered + unsupported, current state | `0` |
| Table processing and queue wait | count-weighted averages over the endpoint's event types, each from its own count | `–` when the count is 0 |

## Part 1: storage queries (`IMonitorMetricsStore`)

New interface in `NimBus.MessageStore.Abstractions`, with XML docs:

```csharp
/// Optional queries for the Monitor dashboard. Providers that implement it are registered for
/// it; the WebApp reports the affected numbers as unavailable when it is absent.
public interface IMonitorMetricsStore
{
    /// Queue and processing time per bucket for outcome messages enqueued in [from, to).
    /// Sparse: a bucket with no timed outcomes is absent. Bucket keys match
    /// IMetricsStore.GetTimeSeriesMetrics.
    Task<LatencyTimeSeriesResult> GetLatencyTimeSeriesMetrics(
        DateTime from, DateTime to, int substringLength, string bucketLabel);

    /// Distinct non-empty session ids among the endpoint's non-deleted unresolved events whose
    /// status is Failed or DeadLettered, or Pending with PendingSubStatus "Handoff".
    Task<int> GetBlockedSessionCount(string endpointId);
}
```

`LatencyTimeSeriesResult { BucketSize, DataPoints }` and
`LatencyTimeSeriesBucket { Timestamp, Queue, Processing }` go in
`States/EndpointMetrics.cs`, reusing `LatencyAggregate`.

### Latency time series

- **SQL** (`SqlServerMetricsStore.cs`): port the DIS query. Bucket with
  `DATEADD(unit, DATEDIFF(unit, 0, EnqueuedTimeUtc), 0)` as `GetTimeSeriesMetrics` does
  (`:187`); aggregate `COUNT`, `AVG(CAST(... AS FLOAT))`, `MIN`, `MAX` for each column.
- **Cosmos** (`CosmosDbMetricsStore.cs`): do not port the DIS single query. Follow the
  existing NimBus latency query (`:71-149`): two queries, one per column, each with
  `IS_DEFINED(x) AND x != null`, grouped by `SUBSTRING(c.message.EnqueuedTimeUtc, 0, n)`, and
  merge rows per bucket by count-weighted sum, so partial cross-partition aggregates combine
  correctly. `QueueTimeMs` is a `long?` that is likely persisted as JSON `null`, and the DIS
  filter (`IS_NUMBER(a) OR IS_NUMBER(b)`) lets such rows into `AVG`.
- **In-memory** (`InMemoryMessageStore.cs`, next to `:1300`).
- Message types: the same five as `GetEndpointLatencyMetrics` (`SqlServerMetricsStore.cs:104`),
  so the chart and the per-endpoint numbers agree. DIS also counts `PendingHandoffResponse`
  and `IgnoredResponse`; NimBus has no `IgnoredResponse`.

### Blocked session count

- **SQL**: `COUNT(DISTINCT SessionId)` on `UnresolvedEvents` for the endpoint, `Deleted = 0`,
  `SessionId` not null or empty, and `Status IN ('Failed', 'DeadLettered') OR
  (Status = 'Pending' AND PendingSubStatus = 'Handoff')`. The first two use
  `IX_UnresolvedEvents_EndpointId_SessionId_Status` (`Schema/0003_Events.sql:38`); the hand-off
  branch has the filtered index from `Schema/0011_HandoffLookup.sql:20`. Check the plan with
  the live SQL run.
- **Cosmos**: in the per-endpoint container, `SELECT c.sessionId … GROUP BY c.sessionId` with
  the same filter, counting rows in the client. A `COUNT` over a `DISTINCT` subquery is
  untested cross-partition; do not rely on it.
- **In-memory**: distinct over the dictionary.
- The status values and the hand-off sub-status must come from the same constants the
  providers already use; do not retype the strings.

### Wiring

- Implement the interface on `CosmosDbClient`, `SqlServerMessageStore` and
  `InMemoryMessageStore`, each forwarding to its provider store explicitly.
- Register it in `CosmosDbMessageStoreBuilderExtensions` and
  `SqlServerMessageStoreBuilderExtensions` by casting the aggregate, as for
  `ISubscriptionNotificationStore`. Extend the registration tests
  (`SqlServerMessageStoreRegistrationTests.cs:33`, `CosmosDbMessageStoreRegistrationTests.cs:70`).
- No change to `IMetricsStore`, `IMessageTrackingStore`, the OpenTelemetry decorator or the
  resolver test fakes.

### Tests first

New `MonitorMetricsStoreConformanceTests` in `src/NimBus.Testing/Conformance/`, run per provider
next to `MetricsStoreConformanceTests` (Cosmos, SQL, in-memory runners in
`tests/NimBus.MessageStore.*.Tests/*OtherStoreConformanceTests.cs`):

- Latency: request rows and untimed rows are excluded; a row with only one timing counts in
  that series only; empty buckets are absent; a series with no values has `Count = 0`; `to`
  is exclusive; minute and hour keys equal those of `GetTimeSeriesMetrics`; unequal
  populations average correctly. The DIS file `SqlServerLiveLatencyTimeSeriesTests.cs` has
  starting cases.
- Blocked sessions: two failed events on one session count once; a dead-lettered event counts;
  a pending hand-off counts and its deferred siblings add nothing; deferred-only sessions
  (replay parking) do not count; Pending without the hand-off sub-status, Completed, deleted
  rows and rows with no session id do not count; another endpoint's sessions do not count.

## Part 2: dashboard token store (`IDashboardTokenStore`)

```csharp
public interface IDashboardTokenStore
{
    Task<DashboardTokenSettings> GetDashboardTokenSettings();
    Task<bool> TrySetDashboardTokenSettings(DashboardTokenSettings settings, string? expectedRevision);
    Task<List<DashboardTokenUsage>> GetDashboardTokenUsage();
    Task RecordDashboardTokenUse(string tokenId, DateTime usedAtUtc);
}

public class DashboardTokenSettings   // singleton, id "DashboardTokens"
{
    public string? Revision { get; set; }
    public List<DashboardToken> Tokens { get; set; } = new();
}

public class DashboardToken
{
    public string TokenId { get; set; } = null!;      // 12 base32 characters
    public string Name { get; set; } = null!;         // "Operations wall, 3rd floor"
    public string SecretHash { get; set; } = null!;   // SHA-256, Base64
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }       // null = no expiry
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
}
```

Semantics mirror the agent key members (`IEndpointMetadataStore.cs:114-136`).

- **SQL**: `Schema/0025_DashboardTokens.sql` with tables `DashboardTokens` (JSON plus
  `Revision`) and `DashboardTokenUsage`, shaped like `0024_AgentApiKeys.sql`. Add both to the
  required tables in `SqlServerSchemaInitializer.cs` (`:49-50`).
- **Cosmos**: one document in the settings container with ETag compare-and-set, plus
  `DashboardTokenUsage-<id>` documents, as `CosmosDbEndpointMetadataStore.cs:351-438`.
- **In-memory**: as `InMemoryMessageStore.cs:968-1000`.
- Implement on the aggregates and register by casting, as in part 1.
- Tests: a `DashboardTokenStoreConformanceTests` suite with the same cases as the agent key
  tests in `EndpointMetadataStoreConformanceTests.cs:631-760`, run per provider.

## Part 3: WebApp back end

### Token authentication (`Services/DashboardTokens/`)

- `DashboardTokenFormat`: `nbdt_<tokenId>_<43 base64url characters>`. Extract the secret
  generation, SHA-256 hashing and constant-time comparison from `AgentApiKeyFormat.cs` into a
  shared internal helper used by both.
- `DashboardTokenService` (scoped): create, revoke, remove. Compare-and-set with three
  retries, as `AgentApiKeyService.cs:150-174`. Revoke keeps the first revocation. Remove works
  only on revoked or expired tokens. Expiry is optional but must be in the future when given.
  After a successful save, hand the new record to the provider so this instance applies it at
  once, as agent keys already do (`AgentApiKeyService.cs:168` → `AgentApiKeyProvider.Accept`).
- `DashboardTokenProvider` (singleton): re-read every 30 s after a success and every 5 s after
  a failure, single-flight. **Unlike `AgentApiKeyProvider`**, which keeps the last list for as
  long as reads keep failing (`AgentApiKeyProvider.cs:65-71`), this provider keeps a snapshot
  only until it is 2 minutes old; after that, while the store stays unreachable, every token is
  refused. A revoke therefore takes effect on another instance within 30 s normally and within
  2 minutes during a store outage.
- `DashboardTokenUsageRecorder`: last-used at most every 5 minutes.
- `DashboardTokenAuthenticationHandler`: scheme `DashboardToken`, header
  `X-NimBus-Dashboard-Token`. Principal `dashboard:<tokenId>` with a
  `nimbus_dashboard_token_id` claim and no email, `oid` or groups. A request without the
  header returns `NoResult`, so signed-in callers are unaffected. Never log the header value.
- `AddNimBusDashboardTokens()`: registers the scheme, never as a default, and the
  `MonitorFeed` policy naming the sign-in scheme plus `DashboardToken`. The sign-in scheme
  must be resolved from the final `AuthenticationOptions`, exactly as
  `AgentApiKeyServiceCollectionExtensions.cs:39-48`: MVC authenticates an action only with the
  schemes its policy names, so leaving the sign-in scheme out locks people out. The defaults
  are LocalDev, `Identity.Application`, `Identity.Application` and `OpenIdConnect` across the
  four sign-in modes (`AgentApiKeyStartupWiringTests`). A controller convention puts the policy
  on the generated `MonitorFeedApiController`. Call it next to the agent key registration
  (`Startup.Security.cs:179`).

### Admin API (tag `DashboardTokens`)

| Operation | Behaviour |
|---|---|
| `GET /api/admin/dashboard-tokens` | List with name, created, expiry, revoked and last-used. Never the secret or hash. Returns `csrfToken` |
| `POST /api/admin/dashboard-tokens` | Body `{ name, expiresAtUtc? }`. Returns the full token once |
| `POST /api/admin/dashboard-tokens/{tokenId}/revoke` | Idempotent |
| `DELETE /api/admin/dashboard-tokens/{tokenId}` | Only for revoked or expired tokens |

- Site Owner only, as `AgentKeysImplementation.cs:164`.
- Antiforgery partial like `Controllers/AgentKeysApiController.Antiforgery.cs`.
- Rate limiting: add `DashboardTokensApiController` to the admin allowlist in
  `RateLimiting/RateLimitPoliciesConvention.cs:72-79`; `RateLimitEndpointMetadataTests` fails
  otherwise.
- Audit: add `CreateDashboardToken`, `RevokeDashboardToken` and `RemoveDashboardToken` to
  `MessageAuditType` and to `AuditSettingsProvider.AlwaysRecorded` (`:54`), next to the agent
  key types, then record them as `AgentKeysImplementation.cs:56-106` does.
- Register the implementation in `Startup.Web.cs` near `:140`.

### Feed API (tag `MonitorFeed`)

Edit `src/NimBus.WebApp/api-spec.yaml` only; `wwwroot/api-spec.yaml` is copied at build.
Register `IMonitorFeedApiController → MonitorFeedImplementation` in `Startup.Web.cs` next to
the other controller registrations (`:124-125`).

| Operation | Client cadence | Content |
|---|---|---|
| `GET /api/monitor/feed/status` | 5 s | `environment`, `platformName`, `serverTimeUtc`, `generatedAtUtc`, `stale`, `tokenAccess`, and per endpoint: the `EndpointStatusCount` fields including subscription status, `blockedSessionCount` (null when unavailable), `heartbeat { liveness, lastAnsweredUtc }`, `acknowledgement` |
| `GET /api/monitor/feed/metrics` | 30 s | `lastHour` series; per endpoint: in, out, failed, average queue ms and processing ms with their counts; fleet totals for the tiles; `generatedAtUtc`, `stale` |
| `GET /api/monitor/feed/history` | 5 min | `lastThreeDays` series; `latencyLastDay` series (null when unavailable); per endpoint 24 h average processing ms; `generatedAtUtc`, `stale` |

Composition (`MonitorFeedImplementation`):

- Authorize first: the token claim, or site Reader.
- **Status**: every endpoint known to the platform, unfiltered. Extract the complete status
  projection from `EndpointImplementation.cs:261-284` (count with the storage-unavailable stub
  at `:286`, then `StampSubscriptionStatusAsync`) into a service both controllers call. The
  blocked-session query catches the same missing-container exceptions as the count path and
  returns null, never failing the response.
- **ACK list**: factor the list-and-prune logic of `MonitorAcknowledgementService.GetActiveAsync`
  so the feed reads it unfiltered through a cached snapshot (5 s). `MonitorImplementation`'s
  PUT and DELETE invalidate that snapshot.
- **Heartbeat**: `IHeartbeatService.GetOverviewAsync()` only. Share the liveness mapping with
  `HeartbeatImplementation.cs:166-172`. Use `LastEndTime`, the last answered probe, for
  `lastAnsweredUtc`; `/api/heartbeat/page` reports `LastStartTime`, the last probe sent.

Caching:

| Data | Key | TTL |
|---|---|---|
| Status counts | existing `endpoint-state-count:{id}` | 5 s |
| Blocked sessions | `monitor-feed:blocked-sessions:{id}` | 15 s |
| ACK snapshot | `monitor-feed:acks` | 5 s, invalidated on ACK writes |
| Heartbeat overview | `monitor-feed:heartbeat` | 30 s |
| Metrics composition | `monitor-feed:metrics:{minute bucket of now}` | 30 s |
| History composition | `monitor-feed:history:{hour bucket of now}` | 5 min |

- Cached values hold only shared data. `tokenAccess`, `serverTimeUtc` and anything else that
  depends on the caller or the moment are added to the response after the cache read.
- `StoreResultCache` never caches a failure (`StoreResultCache.cs:73-78`), so a store outage
  would otherwise send every wall's every poll to the store. The feed keeps the last good
  composition of each operation; when a refresh fails it returns that composition with
  `stale: true` and its `generatedAtUtc`, and tries the store again at most once per TTL
  (single-flight). With nothing cached yet, it returns 503.
- Every feed response sets `Cache-Control: no-store` itself. For signed-in callers the
  antiforgery cookie middleware calls `IAntiforgery.GetAndStoreTokens`, which also marks the
  response uncacheable; requests that carry only the token header skip that middleware
  (`Csrf/AntiforgeryTokenCookieMiddleware.cs:39-44`), so nothing else would.

### SPA shell

`OnPrepareResponse` cannot test for `/Monitor`: `MapFallbackToFile` rewrites the request path
to the file path before the static-file middleware runs, so the hook sees `/index.html`.
Instead, in `Startup.Pipeline.cs` before the general fallback (`:165`), map a dedicated
fallback for the exact route:

```csharp
endpoints.MapFallbackToFile("Monitor", "index.html", monitorShellOptions);
```

`monitorShellOptions` sets the same no-cache headers and file provider as `fallbackOptions`
but no login redirect. Route matching is case-insensitive, so `/monitor` is covered;
`/Monitor/x` still reaches the general fallback and redirects.

### Tests first (`tests/NimBus.WebApp.Tests/`)

- `DashboardTokens/`: format; handler (valid, unknown, malformed, revoked, expired);
  provider (cold start with the store down refuses; **authenticated, then cache expiry, then
  store outage**: still accepted inside 2 minutes, refused after; revoke on another instance
  applies within 30 s; a write on this instance applies at once; a refresh racing a write
  keeps the newer record); startup wiring in each sign-in mode, modelled on
  `AgentApiKeyStartupWiringTests`; admin API (Owner only, audited with the always-recorded
  types, antiforgery, admin rate-limit metadata, secret returned once, remove refused for a
  live token, 501 without an `IDashboardTokenStore`).
- Authorization, modelled on `AgentApiAuthorizationTests.cs:110`, in a host without
  development bypasses: a token reaches the three feed operations and nothing else, with
  explicit cases for ACK PUT and DELETE, `/api/metrics/*`, `/api/endpoint/status/count`, the
  admin token API and the SignalR hub. A signed-in Reader reaches the feed. A signed-in user
  with only an endpoint role gets 403.
- Feed: response content per operation; subscription status present; a missing endpoint
  container gives the stub and a null blocked-session count; two different callers inside a
  TTL cause one store read, and their caller-specific fields differ; a store failure after a
  good read returns the old composition with `stale: true` and does not call the store again
  inside the TTL; `Cache-Control: no-store` on every response; metric arithmetic at bucket
  boundaries, with zero denominators and unequal timing populations.
- `SpaFallbackTests`, through the real fallback pipeline: anonymous `/Monitor` and `/monitor`
  return the shell; anonymous `/Monitor/x`, `/Endpoints` and `/` still redirect.
- Test-host note from spec 039: a host that calls `context.AuthenticateAsync` needs a DI scope
  per request, because the handler provider is scoped and handlers cache their first result.

## Part 4: Monitor page

| File | Change |
|---|---|
| `lib/monitor-access.ts` (new) | Parse `#token=` from `location.hash`. Build the API client: with a token, `new Client(CookieAuth(), undefined, { fetch: tokenFetch })`, where `tokenFetch` adds the header (signature at `api-client/index.ts:81`); without, `new Client(CookieAuth())` as today. Keep the token in memory; never write it to `localStorage`, logs or error messages |
| `app.tsx` | In token mode, do not mount `CommandPaletteProvider`'s palette: it mounts even when closed and calls `useAccess()`, which fetches on mount (`command-palette-provider.tsx:58`, `hooks/use-access.ts:80`). Theme and toast providers need no change |
| `hooks/use-monitor-data.ts` | Read `feed/status` instead of three calls. Samples, ticker, fresh-failure pulse, stale detection and ACK mutations stay. ACK mutations keep using the existing `Monitor` API |
| `hooks/use-monitor-metrics.ts` (new) | Poll `feed/metrics` every 30 s and `feed/history` every 5 min; pause while the tab is hidden, as the status poll does |
| `pages/monitor.tsx` | New layout: header, summary row, charts row, endpoint table, footer. Replace `useEnv()` and `getApplicationStatus()` (`:54-71`) with the feed's `environment` and `platformName`, and `useAccess()` with "can ACK" from the existing access hook in session mode only. Keep the exported helpers (`endpointState`, `backlogLevel`, `failedPerMinuteSeries`, `formatElapsed`) and the `em` sizing |
| `components/monitor/` (new) | `summary-tiles.tsx` (ring plus six tiles), `line-bar-chart.tsx`, `endpoint-table.tsx` |

Behaviour:

- Tiles and table follow "Metric definitions". Table sorted failing, then backlog, then A to Z.
- Token mode: no ACK buttons, no palette, no access or app-status calls. A 401 or 403 from the
  feed shows a full-screen "token is invalid, expired or revoked" state and keeps retrying; it
  never navigates to login on a wall.
- Session mode: a 401 with no token navigates to `/`. A 403 shows "the Monitor requires the
  Reader role".
- A response with `stale: true` dims the affected section and shows its age; the existing
  connection-lost banner still keys off failed status polls.

Tests: update `pages/monitor.test.tsx` and `hooks/use-monitor-data.test.tsx`; add tests for
token parsing and the header; **a full `App` mount on `/Monitor#token=…` that records every
request and asserts only the three feed operations are called**; the token-invalid state; the
Reader-required state; tile arithmetic; table sort.

## Part 5: Settings panel

`components/admin/dashboard-tokens-settings.tsx`, modelled on `agent-keys-settings.tsx`: list
with status and last-used, create dialog (name, expiry preselected to one year, "no expiry"
allowed), revoke with confirmation, remove. After creation, show the complete Monitor URL with
a copy button, a note that it cannot be shown again, and a note that the URL is the credential:
anyone with it, including from browser history, sees the dashboard until it is revoked.
Register the panel where agent keys are: `pages/settings.tsx`, `models/manage-pages.ts`,
`components/settings/feature-status.ts`, `hooks/use-settings-status.ts`.

## Part 6: documentation

- `docs/authentication.md`: add dashboard tokens; line 36 still says agent keys have no UI.
- `docs/webapp-rest-api.md`: the feed and the admin token API.
- `docs/webapp-monitor.md` (new): the page, the metric definitions above (in particular the
  blocked-session estimate and that latency is an average), and wall setup with a token.
- The ADR from "Before implementation".
- Release notes per `docs/versioning.md`: a minor release. The two new store interfaces are
  optional; note them for anyone with their own provider.

## Verification

```bash
dotnet build src/NimBus.slnx -c Release
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
```

- The Release build must run with NSwag generation enabled (`SkipSpaBuild` unset), since the
  spec changes; inspect the generated client and controller diffs.
- Run the conformance suites against live SQL Server and Cosmos (`NIMBUS_SQL_TEST_CONNECTION`,
  `NIMBUS_COSMOS_TEST_*`, `NIMBUS_COSMOS_TEST_REQUIRED=1`) and confirm nothing skipped.
- Manual checks need real sign-in. The default AppHost turns on local-development
  authentication (`src/NimBus.AppHost/Program.cs:187`), which signs every request in as an
  administrator, so a private window is not a token-only caller there. Run the AppHost with
  `NIMBUS_IDENTITY=true` and the simulator on, then:
  1. Signed in as a Reader: the page fills, charts appear within 30 s, ACK works for a
     Contributor.
  2. Create a token in Settings and open the URL in a private window with no cookies: the page
     fills, no ACK buttons, no login redirect.
  3. In that window's dev tools, confirm the only API calls are the three feed operations and
     that no request URL contains the token.
  4. Revoke the token: the wall shows the token-invalid state within about 35 s.
  5. Sign in as a user with only an endpoint role: the Reader-required message appears.
- Capture a screenshot of the finished page with demo data for the PR description.

## Delivery

One branch and one pull request, with one commit per part above, after the issue from "Before
implementation". Stacked pull requests are not used.

## Not in this plan

- Percentiles, per-endpoint history, baselines ("usual at this hour").
- An exact blocked-session count from Service Bus session state.
- A public `/api/metrics/latency-timeseries` operation. The store query exists after part 1
  if the Metrics page wants it later.
- Token scopes beyond "the whole Monitor feed", per-endpoint tokens, or IP restrictions.
- Rate limiting of the feed. The caches and the stale-on-failure rule bound the store cost to
  a fixed number of reads per TTL, however many walls poll.
- Implementing `IDeferredMessageMetricsQuery` for the OpenTelemetry gauge.
- Hiding the Monitor link in the sidebar for users without the Reader role.

## Open questions

1. **Cosmos `GROUP BY` across partitions.** `RunBucketCountQuery` assigns each bucket
   (`CosmosDbMetricsStore.cs:331`) while the event-type query adds, with a comment that
   cross-partition `GROUP BY` can return partial aggregates (`:273-286`). The new queries merge
   by sum to be safe. Whether the existing count query loses data, which would affect the "last
   hour" and "last 3 days" charts, needs a test against a live account.
2. **Cost of the blocked-session query on Cosmos** when an endpoint has thousands of blocked
   sessions. It returns one row per session. Measure it during the live conformance run; if it
   is expensive, raise its cache time.

## Implementation notes

Where the implementation differs from the text above:

- **Subscription status in the feed comes from stored endpoint metadata only.** The endpoint
  status API probes Service Bus for an unknown status and saves the result; a wall polling every
  5 seconds does not. The Endpoints page keeps doing the probing.
- **Series windows end at the end of the current bucket** (`floor(now) + 1 bucket`), not at
  `now`. The bucket generator emits starts strictly before `to`, so ending at `now` drops the
  current bucket whenever `now` falls exactly on a boundary. Data cannot lie in the future, so the
  counts are the same.
- **The feed lists the endpoints the deployment shows** (`EndpointVisibility.IsListed`), as the
  endpoint list does, rather than every endpoint in the platform catalog.
- **The ADR is [ADR-018](../adr/018-optional-store-capabilities-and-dashboard-tokens.md).** The
  issue from "Before implementation" was not opened before the code; open it with the PR.

## Review changes

A Codex review of the first version found three blocking problems and eight others. All were
checked against the code and applied:

- The `/Monitor` path check would never match inside the fallback hook; replaced by a dedicated
  fallback endpoint.
- The blocked-session rule (Failed or Deferred) missed hand-offs and counted replay parking;
  replaced by decision 7, which also answers the old open question on dead-lettering.
- New required members on three store interfaces would break third-party providers; replaced
  by two optional interfaces (decision 4).
- Client construction, the command palette's access call, subscription status, cache
  boundaries and failure behaviour, `Cache-Control`, admin rate limiting, always-recorded
  audit types, the provider's behaviour during store outages, the AppHost's development sign-in
  and the exact metric windows and arithmetic.
