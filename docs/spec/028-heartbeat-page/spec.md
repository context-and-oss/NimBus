# Spec 028 — Heartbeat Page

| | |
|---|---|
| **Status** | Implemented. Merged as context-and-oss/NimBus#112 and shipped in v3.1.0. The text below is the approved design, with the review findings incorporated. |
| **Date** | 2026-08-16 (reviewed 2026-08-16) |
| **Depends on** | Existing platform heartbeat (`docs/heartbeat.md`, `src/NimBus.WebApp/Services/Heartbeat/`), migration floor `0018_PlatformHeartbeat.sql` |
| **Deliverables** | `src/NimBus.MessageStore.Abstractions/States/HeartbeatUptimeDay.cs`, `HeartbeatGap.cs`, `HeartbeatHistoryFolder.cs`; per-probe interval persistence; a separate `IHeartbeatHistoryStore` implemented by three backends; `Schema/0019_HeartbeatHistory.sql` + `SqlServerSchemaInitializer.RequiredTables`; `CosmosContainerDefaults.ReservedContainerIds`; `GET /api/heartbeat/page`; `ClientApp/src/pages/heartbeat.tsx` + `App.tsx` route + `components/sidebar.tsx`; `src/NimBus.Testing/Conformance/HeartbeatHistoryStoreConformanceTests.cs` |
| **Reference** | A working implementation exists in the sibling DIS repo, branch `feat/heartbeat-page`. Port the **design**, not the code — §3 lists three places where NimBus is better factored and the DIS approach must not be copied. |

> **Every file:line citation in this spec was verified against the tree at the date above.** Where a claim is about observable behaviour rather than a line of code, the verification is stated inline. Two claims in the first draft were wrong and are corrected in place: §7's description of the sweep cadence, and §9.1's recommended serializer fix, which would have regressed two shipping pages.

An operator page at `/Heartbeat` answering *who is reporting, who has gone quiet, and for how long*, over a 7 / 30 / 90-day window — plus the SDK version each adapter is running. Requires new durable history: the platform currently retains 20 beats per endpoint, which is under two hours.

---

## 1. Motivation

Heartbeat data today is reachable only through a table inside the Admin page (`ClientApp/src/components/admin/heartbeat-card.tsx`). It answers *is this adapter alive right now* and nothing else. Three operator questions have no answer at all:

1. **Has this adapter been reliable?** There is no uptime figure over any period.
2. **When did it go quiet, and for how long?** There is no gap history.
3. **Which SDK is each adapter on?** The column exists on the Admin table but is buried in a settings surface, and it is the fastest way to spot adapters that predate a platform capability.

### 1.1 The blocking constraint

`HeartbeatRollup.MaxHeartbeatsPerEndpoint = 20` (`src/NimBus.MessageStore.Abstractions/HeartbeatRollup.cs:17`) caps the retained beat list, enforced on write in both backends (`src/NimBus.MessageStore.SqlServer/SqlServerMessageStore.cs:1372`, `:1391`) and asserted by conformance (`src/NimBus.Testing/Conformance/EndpointMetadataStoreConformanceTests.cs:97,108`).

At a five-minute interval, 20 beats is **100 minutes** of history. Uptime over 30 days is not derivable from it by any amount of cleverness. Durable per-day counters are a hard prerequisite, not an optimisation.

---

## 2. Goals and non-goals

**Goals.** A `/Heartbeat` page with liveness, uptime, a daily history strip, recent gaps and SDK version; durable history at roughly one row per adapter per day; parity across all three store backends, enforced by conformance tests.

**Non-goals.** Alerting or notification on gap detection. Per-event or per-message latency (that is the Metrics page). Archival of history beyond the 90-day window — pruning at 90 days is **in** scope (§11.1). Changing how heartbeats are *sent* or settled; this spec only observes what the existing sweep already produces. Changing the JSON enum serializer — §9.1 explains why that is a regression, not a cleanup.

---

## 3. Four places NimBus is better than the reference — do not port DIS's approach

The DIS implementation predates or lacks these. Copying it would be a downgrade.

1. **Conformance tests.** `src/NimBus.Testing/Conformance/` holds one `*StoreConformanceTests.cs` per store capability (`ServiceHealthStoreConformanceTests`, `AccessControlStoreConformanceTests`, `MetricsStoreConformanceTests`, …), run against every backend. DIS relies on a memory note reminding the author to update both stores. New store methods **must** ship with `HeartbeatHistoryStoreConformanceTests.cs` modelled on `ServiceHealthStoreConformanceTests.cs`.
2. **Three implementations, not two.** `NimBus.MessageStore.CosmosDb`, `NimBus.MessageStore.SqlServer`, and `InMemoryMessageStore` (`src/NimBus.Testing/Conformance/InMemoryMessageStore.cs`). DIS has two. All three need the new methods or the conformance suite will not compile.
3. **A shared hub helper.** `subscribeHeartbeatUpdates` in `ClientApp/src/lib/grid-events-connection.ts` (`HUB_URL = "/hubs/gridevents"`, `:10`), already used by the Admin card (`components/admin/heartbeat-card.tsx:14,72`). DIS hand-rolls a `HubConnectionBuilder` in each component. Use the helper.
4. **Status values are strings with a normalizer, not OpenAPI enums.** `HeartbeatOverviewRow.status` is `type: string` (`api-spec.yaml:4216`) and the client narrows it in `components/admin/heartbeat-status.ts`. This is what makes NimBus immune to the casing bug that reached review in DIS — §9.1 in full. It is also why the fix DIS would need here is a regression in this repo.

### 3.1 Naming collision — read before naming anything

`HeartbeatRollup` **already exists** in `NimBus.MessageStore.Abstractions` and means *trimming the retained beat list to 20* (`HeartbeatRollup.cs:20-28`). It also owns `BuildOverviewItem` (`:61`). It is not a daily rollup and has nothing to do with uptime.

Naming the new type `HeartbeatDailyRollup` — as DIS does — would place it one word from an unrelated class in the same namespace. **Use `HeartbeatUptimeDay` and `HeartbeatGap`.**

---

## 4. Data model

Shapes below are abbreviated — **ship them with XML doc comments on every public member** (`CLAUDE.md`, *Public API*) and with the Newtonsoft attributes the Cosmos backend needs (§4.2), matching `States/ServiceHealth.cs`. Non-nullable `string` with no initializer matches the house style in that folder; do not "fix" it.

```csharp
// src/NimBus.MessageStore.Abstractions/States/HeartbeatUptimeDay.cs
public class HeartbeatUptimeDay
{
    public string EndpointId { get; set; }
    public DateTime DayUtc { get; set; }        // midnight UTC
    public int Expected { get; set; }           // Received + Missed — probes SENT, see §4.3
    public int Received { get; set; }
    public int Missed { get; set; }
    public int ObservedSeconds { get; set; }    // sum of represented probe intervals — §4.3
    public int LongestGapSeconds { get; set; }
    public DateTime LastBeatUtc { get; set; }   // fold watermark — §5.1
}

// src/NimBus.MessageStore.Abstractions/States/HeartbeatGap.cs
public class HeartbeatGap
{
    public string EndpointId { get; set; }
    public DateTime FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }        // null => still silent
    public string SdkVersionBefore { get; set; }
    public string SdkVersionAfter { get; set; }
}
```

Cost: ~one row per adapter per day (11 adapters × 90 days ≈ 1000 rows), versus ~285k rows if each beat were stored. Gap rows are written only on a transition, so a healthy fleet costs none.

Add `IntervalSeconds` to the existing stored `Heartbeat` state. The pending write records the floored schedule interval that created the probe; a response update preserves that value. Legacy rows with zero use the current floored setting as a documented fallback. Without a per-probe value, changing heartbeat settings between send and fold silently rewrites historical coverage and gap severity.

### 4.1 SQL Server migration

`src/NimBus.MessageStore.SqlServer/Schema/0019_HeartbeatHistory.sql` (last is `0018_PlatformHeartbeat.sql`). Follow the existing idempotent `IF OBJECT_ID(...) IS NULL` style.

- `HeartbeatUptimeDays` — PK `(EndpointId, DayUtc)`; index on `DayUtc` including `EndpointId`.
- `HeartbeatGaps` — PK `(EndpointId, FromUtc)`; **index on `ToUtc`**.

> Table names are plural, matching `Heartbeats` / `EndpointSubscriptions` in `SqlServerSchemaInitializer.RequiredTables:31`. The *class* stays singular (`HeartbeatUptimeDay`) — it is one day.

> **Index on `ToUtc`, not `FromUtc`.** The window query filters on `ToUtc` (§6.1). DIS indexed `FromUtc` and its index does not serve its own query — it scans. Do not inherit that.

Add both table names to `RequiredTables` (`src/NimBus.MessageStore.SqlServer/SqlServerSchemaInitializer.cs:31`), or startup verification will pass over missing tables.

### 4.2 Cosmos DB layout — specify this before writing any store code

The first draft left this blank and it is the one part with a trap that corrupts unrelated data.

- **Containers.** Two: `heartbeatuptimedays` and `heartbeatgaps`, both partitioned on `/EndpointId`, created through `GetCachedContainerAsync` alongside `servicehealth` (`CosmosDbClient.cs:1345`). Lowercase, matching every other store-owned container id (`CosmosDbClient.cs:54-65`). Endpoint history is the natural partition; fleet reads remain bounded cross-partition queries over at most 90 daily rows per endpoint.
- **Document ids.** Composite and ordinally sortable for deterministic point upserts:
  `{endpointId}|{DayUtc:yyyy-MM-dd}` and `{endpointId}|{FromUtc:O}`. Carry the id in `[JsonProperty(PropertyName = "id")]` as `ServiceHealth.cs` does, and keep `EndpointId` as its own queryable field.
- **`ReservedContainerIds` — do not skip this.** `CosmosContainerDefaults.cs:31-36` lists every container the store owns, because an endpoint whose id equals a store container id resolves to *the same physical container and the same cache entry*, and whichever wins decides that container's partition-key path and TTL mode process-wide. The class documents this failure in its own XML comment. **Add both new ids to `ReservedContainerIds`**; `EnsureNotReservedEndpointId` then rejects the collision at registration instead of at 3 a.m.
- **Retention is free here.** Create these containers with "TTL on, no container default" (`DefaultTimeToLive = -1`). Uptime days and **closed** gaps get an item-level `ttl` of 90 days. Open gaps use `ttl = -1`; otherwise an outage lasting 90 days disappears while it is still active. See §11.1.

### 4.3 `Expected` counts probes sent, not time elapsed

`Expected = Received + Missed` counts only beats the platform actually sent. Nothing is recorded for a period when the WebApp was not running, so **a past day on which the WebApp ran for ten minutes and answered every probe scores `Missed = 0` but must not render a clean green cell** — asserting a full day of proven liveness the data does not support. The current UTC day is different: its denominator is only the portion of the day that has elapsed, so an adapter observed continuously since midnight stays green instead of remaining amber until 21:36 UTC.

Each stored probe carries the interval it represents. Folding adds that value to `ObservedSeconds`, capped at the UTC day boundary, so schedule changes within a day remain correct:

```csharp
var spanSeconds = day.DayUtc.Date == now.Date
    ? Math.Max(1, (now - day.DayUtc.Date).TotalSeconds)
    : 86400.0;
coverage = Math.Clamp(day.ObservedSeconds / spanSeconds, 0, 1);
```

Use the request's single captured `UtcNow` for the current-day denominator. For a legacy heartbeat whose stored interval is zero, use `HeartbeatSettings.IntervalSeconds` floored by `MinimumIntervalSeconds`. §8 uses `coverage` for day state and the tile copy; §10 tests mixed-interval days, the legacy fallback, and a healthy in-progress current day.

---

## 5. The fold — the part that must be exactly right

Arithmetic lives in a pure, side-effect-free static `HeartbeatHistoryFolder` in `NimBus.MessageStore.Abstractions`, so it is testable without a store:

```csharp
HeartbeatFoldResult Fold(
    string endpointId,
    IEnumerable<Heartbeat> beats,              // retained beats from metadata
    IEnumerable<HeartbeatUptimeDay> existing,
    HeartbeatGap? openGap,
    DateTime historyStartUtc,
    int fallbackIntervalSeconds)
```

Four rules follow. **Each was a defect found in review of the reference implementation** — they are field-observed, not theoretical. Each is stated with its failure mode and the test that catches it.

The fold consumes beats **in `StartTime` order** and applies §5.1 and §5.4 as one loop, not two filters. That ordering is load-bearing; §5.4 explains why.

### 5.1 Idempotency via watermark

The fold runs on every sweep and always sees the same retained window. Take only beats with `StartTime > max(LastBeatUtc)` across the endpoint's stored days.

*If missed:* every sweep re-counts the same 20 beats and uptime inflates without bound.
*Test:* fold the same beat set twice; the second call returns no rollups and no gaps.

### 5.2 Gap duration is measured from the gap's start

The sweep folds one new beat at a time, so a multi-hour outage arrives across many folds. A counter scoped to a single fold never climbs past one interval, and the daily strip never reaches the red state its own legend advertises.

```csharp
var runSeconds = (int)Math.Round((beat.StartTime - gap.FromUtc).TotalSeconds) + interval;
if (runSeconds > day.LongestGapSeconds) day.LongestGapSeconds = runSeconds;
```

*If missed:* a three-hour outage reports `LongestGapSeconds == 300` and renders amber.
*Test:* fold twelve consecutive five-minute misses **one beat per call**; assert `LongestGapSeconds == 3600`. The reference produced 300 before the fix.

The trailing interval is used only for the per-day `LongestGapSeconds`, because the last missed probe represents that interval. A closed gap ends at the first received probe, so `ToUtc - FromUtc` already includes the same interval under the normal schedule: twelve misses at minutes 0–55 and recovery at minute 60 is exactly 3600 seconds. `HeartbeatGapRow.durationSeconds` must therefore be `ToUtc - FromUtc`, or `UtcNow - FromUtc` while open — never add another interval.

### 5.3 `Unsupported` and settled `Unknown` count as received

`HeartbeatStatus.Unsupported`'s own doc comment in this repo (`States/HeartbeatStatus.cs`) reads *"The endpoint answered, but with an `UnsupportedResponse` … Reachability is still proven."* The Resolver also persists `SkipResponse` as `Unknown`; that is a settled response, not an in-flight probe (`ResolverService.cs:360-366`). Uptime measures reachability, so every stored status except `Off` and `Pending` counts as received.

*If missed:* every adapter on a pre-heartbeat SDK reports 0% uptime and a permanent ongoing gap, despite answering every probe.
*Test:* one `Unsupported` beat and one settled `Unknown` beat each yield `Received=1, Missed=0`, no gap; and each closes an open gap.

### 5.4 Stop at the first unsettled beat — do not filter past it

A `Pending` beat may still arrive; counting it as missed invents an outage that did not happen. `Pending` alone is never counted — but *how* it is excluded decides whether it is ever counted at all.

Skipping them and carrying on is wrong. The watermark is a single high-water mark (§5.1), so a later settled beat advances `LastBeatUtc` **past** the skipped one; when that beat settles on the next sweep it is already behind the watermark and is dropped forever. §6.2's replace-not-increment upsert means nothing ever reconciles it: the day is simply short one beat, silently, with no error anywhere.

> **Rule:** walk beats in `StartTime` order and **break** at the first `Pending` beat. Everything after it waits for the next fold. `Unknown` is terminal in persisted heartbeat rows and advances the watermark.

In practice only the newest beat is ever `Pending` — one probe is in flight per interval, and §7 folds after the sweep has settled anything older than the timeout — so breaking early costs one interval of latency and nothing else.

*Test:* a `Pending` beat does not raise `Expected` and does not move the watermark past the last settled beat.
*Test:* fold `[On, Pending, On]`; then re-fold with the middle beat settled to `On`. Final `Received == 3`. Filter-and-continue yields 2.

### 5.5 Gap lifecycle

- First miss with no open gap → open at `beat.StartTime`, set `SdkVersionBefore` from the newest earlier beat carrying a version.
- First received beat while a gap is open → set `ToUtc` and `SdkVersionAfter`.
- Never open a second gap while one is open.

---

## 6. Store methods

Six methods and one retention capability, implemented in **all three** backends, covered by `HeartbeatHistoryStoreConformanceTests`:

```csharp
bool PrunesHeartbeatHistoryAutomatically { get; }
Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc);
Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days);
Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc);
Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps);
Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore);
Task PruneHeartbeatHistory(DateTime cutoffUtc);
```

> No `Async` suffix and no `CancellationToken` — **that is deliberate and correct**, matching every existing member of `IEndpointMetadataStore`. Do not "fix" it here; a consistency pass on the whole interface is a separate change.

### 6.0 Put them on a new interface, not on `IEndpointMetadataStore`

`NimBus.MessageStore.Abstractions` ships to nuget.org as `Akaule.NimBus.MessageStore.Abstractions`. Adding four abstract members to a published interface is a source **and** binary break for any external implementer, and this repo's stated policy is backward-compatible bridges rather than breaks (`CLAUDE.md`, *Obsolete code*).

Declare and register `IHeartbeatHistoryStore` **alongside** `INimBusMessageStore`; do not add it to the aggregate's base-interface list. Adding a new abstract capability to `INimBusMessageStore` would still break every external aggregate implementation. `HeartbeatService` takes the new capability as an optional dependency so third-party stores continue to load; without it, current liveness still works and durable history is unavailable. Official providers register the capability to the same singleton instance.

### 6.1 Gap queries match on overlap, not start

```sql
WHERE ToUtc IS NULL OR ToUtc >= @From
```

An outage that began before the window and ended inside it is exactly the one an operator is hunting, and an ongoing gap belongs in every window. The reference filtered on `FromUtc` and lost both cases — on Cosmos it also lost ongoing gaps that began before the window, so the two backends disagreed.

### 6.2 Upsert replaces, it does not increment

The fold recomputes each row from the stored value plus new beats. An incrementing upsert would double-count on any retry.

Persist changed gaps before uptime days. `HeartbeatUptimeDay.LastBeatUtc` is the durable watermark; advancing it before a gap write succeeds can suppress the exact opening or closing transition on retry. Gap upserts use a stable endpoint/start key and are therefore safe to repeat if the later day write fails.

### 6.3 Conformance coverage

Round-trip; replace-on-upsert; the overlap predicate in all four cases (started-before/ended-inside, wholly-inside, ongoing-from-before-window, wholly-before); empty input returns empty without error.

---

## 7. Where the fold runs

In `HeartbeatService.RunScheduledTickAsync` (`src/NimBus.WebApp/Services/Heartbeat/HeartbeatService.cs:258`), **after** `await SweepTimeoutsAsync()` — the sweep is what settles unanswered beats to `Off`, so folding before it would see them as `Pending` and stop there under §5.4. Enumerate `_platform.Endpoints`, load their metadata with `GetMetadatas(ids)`, and exclude only explicit `IsHeartbeatEnabled == false`, exactly like `SendHeartbeatsAsync`; `GetMetadatasWithEnabledHeartbeat()` omits the default opt-out fleet and must not be used.

`GetMetadatas(ids)` must return complete metadata, including retained `Heartbeats`, on every provider. SQL Server loads the child rows in one batched query and groups them by endpoint; an N+1 call to `GetEndpointMetadata` is not acceptable. Pin this in `EndpointMetadataStoreConformanceTests`, because the fold's in-memory tests cannot detect an incomplete SQL batch projection.

Call it from the tick, **not from inside `SweepTimeoutsAsync`**: that method is also reachable on other paths, and the fold is a scheduled concern rather than part of sweeping.

This is the only place in the platform that observes every settled beat: the Resolver writes beats as replies arrive but runs in its own process, and the sweep is what settles the ones that never came back.

### 7.1 Gate the fold to one run per interval

`HeartbeatBackgroundService.PollInterval` is **30 seconds** (`HeartbeatBackgroundService.cs:17`), and `RunScheduledTickAsync:263` sweeps on *every* tick — deliberately, so timed-out probes settle even when scheduled sending is disabled. The sweep does **not** run on the heartbeat interval.

Folded ungated, the fold therefore runs ~10× per five-minute interval, forever: a full platform metadata read plus a `GetHeartbeatUptimeDays` read plus upserts, on every backend, with the Cosmos reads crossing partitions. §5.1 makes that harmless but not free.

Gate it with `IHeartbeatHistoryStore.TryClaimHeartbeatHistoryFold(dueBefore)`, backed by a durable `LastHeartbeatFoldAtUtc` field on the settings singleton and an atomic SQL update / Cosmos ETag write. The claim does **not** depend on `HeartbeatSettings.Enabled`, so manually sent probes are folded while scheduled fan-out is disabled. A process-local watermark is not sufficient under scale-out, and the send claim is not equivalent because it rejects disabled schedules.

Nothing is lost by folding less often: **retention is 20 beats ≈ 100 minutes at a five-minute interval, far longer than any fold gap**, so no settled beat scrolls out of the retained list before it is counted. That — not the sweep cadence — is the invariant that makes the fold safe. It is also the one to re-check if `MaxHeartbeatsPerEndpoint` or the interval ever moves.

### 7.2 Fail-soft, but not silent

Wrap in try/catch and log a warning; the sweep's own work is complete by then, and a failed fold costs history, not correctness.

Note the exposure the 100-minute window creates: a fold that fails *persistently* — a schema drift, a permissions change — loses every beat older than that window **permanently**, and the only evidence is a warning in a log nobody reads. Emit a counter (or surface a fold-failure age on Admin → Health) so the loss is visible while it is still recoverable.

---

## 8. API

Add to `src/NimBus.WebApp/api-spec.yaml`, regenerate via `api-gen.nswag`.

```
GET /api/heartbeat/page?windowDays=30   ->   HeartbeatPage      [Reader]
```

Clamp `windowDays` to `[1, 90]`; a wider window would report uptime over a period the data cannot cover.

| Schema | Fields |
|---|---|
| `HeartbeatPage` | `windowDays`, `adaptersReporting`, `adaptersTotal`, `adaptersNeedingAttention[]`, `fleetUptime` (nullable), `missedBeatsToday`, `adaptersMissingBeatsToday`, `longestGap`, `adapters[]`, `gaps[]` |
| `HeartbeatAdapterRow` | `endpointId`, `liveness`, `status`, `lastBeatUtc`, `roundTripMs`, `uptime` (nullable), `sdkVersion`, `days[]` |
| `HeartbeatDay` | `dayUtc`, `state`, `missed`, `expected`, `coverage`, `longestGapSeconds` |
| `HeartbeatGapRow` | `endpointId`, `fromUtc`, `toUtc`, `durationSeconds`, `ongoing`, `cause` |

**`status`, `liveness` and `state` are `type: string`, not OpenAPI enums.** That is the house pattern and it is not negotiable here — see §9.1, which is about how the existing ones are declared and what happens to code that assumes otherwise. Document the token list in each field's `description`, exactly as `HeartbeatOverviewRow.status` does (`api-spec.yaml:4216`).

Day state, in order:

- `none` — no row for that day.
- `gap` — `LongestGapSeconds >= 3600`.
- `partial` — `Missed > 0`, **or `coverage < 0.9`** (§4.3: the platform was not watching for the whole day, so the day cannot be reported as clean).
- `full` — everything else.

`coverage` ships on the row so the cell's tooltip can say *why* a day with no misses is not green: "platform observed 4h of this day".

Before building adapters or fleet totals, reconcile history rows case-insensitively by platform endpoint id and UTC day. Prefer the row whose `EndpointId` exactly matches the platform catalog casing; otherwise prefer the greatest `LastBeatUtc`, with ordinal endpoint id as the final deterministic tie-breaker. Cosmos partition keys and ids are case-sensitive, so both `Orders` and `orders` can exist even though the WebApp treats them as one adapter. The folder applies the same rule before deriving its watermark. Never allow a duplicate to throw or double-count fleet totals.

`durationSeconds` is the non-negative whole-second difference between `FromUtc` and `ToUtc`, or between `FromUtc` and the request's captured `UtcNow` for an ongoing gap. No interval is added (§5.2).

Aggregation formulas are part of the contract:

- adapter `uptime` = `sum(Received) / sum(Expected)` over returned days, or null when `sum(Expected) == 0`;
- `fleetUptime` uses the same weighted formula across all adapters (not an average of percentages);
- `adaptersReporting` counts current liveness `alive`; `adaptersTotal` is the platform catalog after explicit opt-outs are removed;
- `adaptersNeedingAttention` contains endpoint ids whose current liveness is `late` or `missing`, or whose requested-window uptime is below 99%;
- `missedBeatsToday` is the current UTC day's sum; `adaptersMissingBeatsToday` counts distinct adapters with a miss today;
- `longestGap` is the largest `durationSeconds` among returned gaps, or null when there are none;
- `cause` is `redeployed` only when both SDK versions are present and differ, `stillMissing` for an open gap, otherwise null.

### 8.0 Liveness mapping — all five statuses

| `HeartbeatStatus` | `liveness` |
|---|---|
| `On` | `alive` |
| `Unsupported` | `alive` |
| `Pending` | `late` |
| `Off` | `missing` |
| `Unknown`, or opted out | `notDeployed` |

> **`Unsupported` maps to `alive`.** The first draft omitted it from this mapping, which sent it to the `notDeployed` default — so an adapter answering every probe on a pre-heartbeat SDK would have rendered "not deployed" **while its own uptime column read 100%**, the exact defect §5.3 exists to prevent, resurfacing one layer up. If the page needs to distinguish the two, add a sixth value; do not let it fall through.

### 8.1 Four honesty rules

**Uptime is null, not zero, when nothing was expected.** An adapter never probed has no uptime; rendering 0% asserts an outage that never happened.

**A day the platform barely watched is not a clean day.** The mirror image of the rule above, and the one the first draft missed: `Expected` counts probes sent, so ten minutes of uptime with no misses scores `Missed = 0`. Green would assert 24 hours of proven liveness from four data points. `coverage` (§4.3) is what keeps that day amber.

**Do not label a calendar-day total "last 24h".** History is per-day, so a rolling 24-hour figure is not derivable — summing from yesterday's midnight spans up to 48 hours and overcounts badly late in the day. Report the UTC calendar day and say so on the tile. The reference shipped the wrong label first and had to rename the field.

**State only causes you can evidence.** An SDK version differing across a gap means a redeployment; an open gap means the adapter has not returned. Everything else renders blank. Editorial causes of the "store API maintenance window" kind are not derivable and must not be invented.

---

## 9. Client

New `ClientApp/src/pages/heartbeat.tsx`, registered as a `lazy()` import and route in `ClientApp/src/App.tsx` (`:16-29`) at `/Heartbeat`, nav entry in `components/sidebar.tsx` after Live Flow — and update `components/sidebar.test.tsx`, which asserts nav contents.

Layout: header with the window toggle; four stat tiles; Adapter status card (legend + table with the daily strip); Recent gaps card. Empty states for no adapters and no gaps; a distinct error state for a failed load.

### 9.1 The enum casing trap — and why NimBus already solved it

The trap is real. `src/NimBus.WebApp/Startup.cs:284` registers `new JsonStringEnumConverter()` with **no naming policy**. `System.Text.Json` ignores `[EnumMember]`, so a C# enum goes over the wire as its *member name* — `Alive`, `NotDeployed` — while an OpenAPI enum declaring `alive`, `notDeployed` generates a TypeScript union that never matches it. Every lookup keyed on a client constant misses, the page renders **every** adapter as "not deployed" and **every** strip cell as no-data, and unit tests that mock the client's spelling pass green. That is how it reached review in the reference implementation.

**NimBus does not have this bug, because it does not model these values as enums.** `HeartbeatOverviewRow.status` (`api-spec.yaml:4216`) is:

```yaml
status:
  type: string
  description: 'Last settled outcome: On, Off, Pending, Unknown or Unsupported.'
```

…paired with a client-side union and normalizer in `components/admin/heartbeat-status.ts` that degrades anything unrecognised rather than rendering a raw token. **Follow that pattern for `status`, `liveness` and `state` (§8) and the trap cannot fire.**

#### Do not "fix" the serializer

The first draft recommended `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` as the preferred option, in its own PR. **That change breaks two shipping pages.** `heartbeat-status.ts:36` is an exact match:

```ts
if (status === "On" || status === "Pending" || status === "Off" || status === "Unsupported") return status;
return "Unknown";
```

CamelCase the converter and every token falls through to `"Unknown"` — every row of `admin/heartbeat-card.tsx` and `admin/platform-services-card.tsx` renders "Unknown", which on a liveness surface reads as a fleet-wide outage. The blast-radius recipe the draft gave (grep `.toLowerCase() ===`) does not find this, because the comparison is neither lowercase nor loose.

`api-spec.yaml` also already declares PascalCase enums for the message-status schemas (`:3230-3239`, `:3334-3339` — `Pending`, `Deferred`, `Failed`, `DeadLettered`, `Unsupported`), whose generated TS unions would stop matching the wire the same day. The casing is a deliberate, load-bearing contract, not drift.

#### Still capture one real response body

Cheap, and it is the check that would have caught the above. Assert the observed spelling in the vitest fixtures (§10).

> Keep in mind if a normalizer is ever written defensively: `"NotDeployed".toLowerCase()` is `"notdeployed"`, not `"notDeployed"`. The lowercase idiom scattered through the client is not a general fix for multi-word values — matching the server's spelling exactly is.

### 9.1.1 Reuse `heartbeat-status.ts`

The page's status column shows the same five `HeartbeatStatus` tokens as the Admin card, so import `normalizeStatus`, `statusVariants` and `statusHints` rather than restating them. `liveness` and day `state` are new vocabularies — put their normalizers in the same module, so there stays exactly one place where a wire token becomes a rendered word.

### 9.2 Live refresh

Subscribe via `subscribeHeartbeatUpdates` (`lib/grid-events-connection.ts`); the broadcast name is `heartbeatupdate` (`src/NimBus.WebApp/Constants.cs:13`). Without it the page freezes at load, and on an operational page a stale "alive" reads as a live confirmation — worse than showing nothing.

### 9.3 Admin card

`components/admin/heartbeat-card.tsx` keeps settings, send-now and the per-endpoint include toggle, drops the liveness columns (status, round trip, SDK version, last seen), and links to the new page. Two tables over the same data can disagree; that card exists to change settings, not to report status. Keep the include toggle — it is a setting, not overview data.

---

## 10. Tests

**Fold (pure — highest value per line).** Counts received/missed; idempotent across repeated folds; watermark skips already-counted beats; ignores retained beats before `historyStartUtc` when no watermark remains; splits across midnight; longest gap equals the consecutive run; **gap duration survives one-beat-per-fold folding** (§5.2); `Unsupported` and settled `Unknown` count as received and close an open gap; opens and closes gaps; null/empty inputs. Plus, for the rules the first draft under-specified:

- **Stops at the first unsettled beat** (§5.4): fold `[On, Pending, On]`, then re-fold with the middle settled — `Received == 3`. Filter-and-continue yields 2 and never recovers.
- **Per-probe `IntervalSeconds` is folded into `ObservedSeconds`** so coverage is derivable (§4.3): a day with four probes at a 300 s interval yields `coverage ≈ 0.014`, not 1.0; a mixed-interval day remains correct after settings change.

**Conformance.** The six store methods across all three backends (§6.3).

**Cross-provider regression.** Batch metadata reads retain heartbeat rows, including the filtered overload used by the scheduled fold. SQL Server must execute this test on its conformance harness when `NIMBUS_SQL_TEST_CONNECTION` is available.

**Page (vitest).** Tiles; the SDK version column including the missing case; liveness labels; one strip cell per day; window toggle refetches with the new `windowDays`; gaps list with the ongoing pill; empty-gaps message; load failure; a hub update triggers a refetch. Plus:

- **`Unsupported` renders alive, not "not deployed"** (§8.0) — the mapping hole from the first draft.
- **A no-miss, low-coverage day renders amber, not green** (§4.3).

> **Fixtures must use the casing the server actually emits**, not the client constants. Include an explicit assertion that adapters do not all collapse to "not deployed" — that is the test which catches §9.1 regressing, and its absence is precisely why the bug survived review once already.

> The reverse guard belongs in this work too: `heartbeat-card.test.tsx` and `platform-services-card.test.tsx` must keep asserting PascalCase status tokens end-to-end, so a future serializer change cannot silently turn both tables to "Unknown" (§9.1).

---

## 11. Known trade-offs — decide these consciously

### 11.1 Retention — do it now, and it is cheaper than the draft assumed

Expressing "overlaps the window" against an id-range key is awkward on Cosmos; the reference reads the prefix and filters in memory, which is acceptable because gap rows are transition-only — but **nothing prunes either table**, so both grow forever rather than plateauing at the 90-day window the page can display.

Do it as part of this work; it is the kind of follow-up that never gets done. The cost is asymmetric and lower than it looks:

- **Cosmos: free.** The containers run "TTL on, no container default" (`DefaultTimeToLive = -1`). Set an item-level `ttl` of 90 days on uptime days and closed gaps; set `ttl = -1` on open gaps. The provider reports `PrunesHeartbeatHistoryAutomatically = true`, so the scheduled fold does not issue redundant cross-partition prune queries. §4.2.
- **SQL Server: one `DELETE`.** `DELETE FROM HeartbeatUptimeDays WHERE DayUtc < @Cutoff` plus the `ToUtc`-bounded equivalent for closed gaps, run beside the fold at its gated cadence (§7.1). Never delete a gap with `ToUtc IS NULL` — an ongoing outage that started 91 days ago is exactly the row an operator needs.

The in-memory store needs no retention beyond matching the same semantics in conformance.

### 11.2 Midnight-spanning gaps

Measuring from `gap.FromUtc` (§5.2) means a gap crossing midnight attributes its full elapsed length to both days' `LongestGapSeconds`. This is right for driving the red cell — the day *was* hit by a long outage — but it is not literally "longest gap within this day".

**Adopt the elapsed-length reading** and name the field for it in the API description and the strip tooltip: *"longest outage touching this day"*, not *"longest gap within this day"*. Splitting the gap at midnight would render the two halves of one six-hour night as two three-hour amber cells, which reads as two incidents.

### 11.3 Empty on day one

The history fills only as sweeps run, so a fresh database shows an empty strip and no uptime for the first days. State this in `docs/heartbeat.md` so it is not reported as a bug.

---

## 12. Definition of done

- [ ] `HeartbeatUptimeDay` + `HeartbeatGap` in Abstractions, XML-documented; per-probe interval persisted; no clash with `HeartbeatRollup` (§3.1)
- [ ] `HeartbeatHistoryFolder` pure, all §5 rules, fully unit-tested — including stop-at-first-unsettled (§5.4)
- [ ] `IHeartbeatHistoryStore` registered separately from `INimBusMessageStore`; compatibility fallback verified (§6.0)
- [ ] Six store methods on all three backends + `HeartbeatHistoryStoreConformanceTests`
- [ ] `0019_HeartbeatHistory.sql`; gaps indexed on `ToUtc`; both tables in `RequiredTables`
- [ ] **Cosmos containers, partition key and id scheme per §4.2 — and both container ids added to `ReservedContainerIds`**
- [ ] Fold wired into `RunScheduledTickAsync` after `SweepTimeoutsAsync`, gated to once per interval (§7.1), fail-soft with a failure signal (§7.2)
- [ ] Per-probe `IntervalSeconds` and per-day `ObservedSeconds` implemented; mixed-interval coverage drives day state and tile copy (§4.3)
- [ ] `GET /api/heartbeat/page` + regenerated client; `status`/`liveness`/`state` declared `type: string`, not enums (§8, §9.1)
- [ ] All five `HeartbeatStatus` values mapped, `Unsupported` → alive (§8.0)
- [ ] Gap rows use actual boundaries without double-adding an interval; daily severity uses represented probe intervals (§5.2)
- [ ] Page, `App.tsx` route, nav entry, `sidebar.test.tsx` updated; `heartbeat-status.ts` reused (§9.1.1)
- [ ] Response casing captured from a real body and asserted in fixtures; **no wire-contract change for existing PascalCase consumers** (§9.1)
- [ ] Hub subscription via `subscribeHeartbeatUpdates`
- [ ] Admin card trimmed, include toggle retained, links to the page
- [ ] Retention shipped: Cosmos item `ttl`, SQL `DELETE`, ongoing gaps never pruned (§11.1)
- [ ] `docs/heartbeat.md` updated: page contents, where history comes from, the per-day (not rolling-24h) caveat, `Unsupported` counting as received, the coverage caveat, empty-on-day-one
- [ ] Full solution build + all tests green — **Release build too**, where CS8767 fails while Debug stays green (`CLAUDE.md`)
