# Spec 043 — Intelligent Failed Message Insights

Status: draft for discussion (2026-11-06, revision 2: review findings applied). Phase 1 scope;
implementation not started.
Feature area: WebApp Insights and Integration Intelligence.
Related: [Spec 033 — Failure Classification](../033-integration-intelligence-failure-classification/spec.md),
[Integration Intelligence](../../integration-intelligence.md).

## 1. Problem and purpose

Failed Message Insights groups failures by normalized error text. This reveals repeated error
signatures, but operators must open individual failures to discover saved classifications and
recovery guidance. Different error patterns can also describe the same kind of failure.

Make existing Failure intelligence results useful across a selected set of failure occurrences.
Help operators understand what kinds of problems occurred, their impact, and what to investigate
first, while preserving the evidence and uncertainty behind each result.

The guiding principle is to minimize the failures an operator needs to inspect or analyze without
overstating what NimBus knows. A shared classification is not proof of a shared incident, and an
individually analyzed failure does not have a confirmed root cause.

## 2. Delivery boundary

Phase 1 is **read-only enrichment from saved classifications**. It includes:

- Three grouping perspectives over the same failure occurrences: error patterns, likely failure
  causes, and recovery guidance.
- Classification coverage for the selected scope and each group.
- Endpoint filtering, paginated occurrence drill-down, paginated group detail, and links to
  existing message details.
- Historical classification provenance and a clear distinction from current event status.
- A read-only activation mode that serves saved classifications to Insights, the existing
  classification GET routes, and the MCP read tool when no provider is configured.
- Efficient SQL/Cosmos reads, explicit freshness, and graceful degradation.

Phase 1 does not initiate analysis, create jobs, infer classifications for unexamined failures,
generate explanations, identify incidents, or perform recovery actions. Provider question sets,
taxonomy, guidance rules, and processing behavior remain as defined by Failure intelligence. The
MCP `nimbus_get_metrics` failures view keeps its current error-pattern output (section 8.4).

Later increments are described in section 12 and are not acceptance requirements for Phase 1.

## 3. Existing implementation and required changes

| Component | Current behavior | Required change |
|---|---|---|
| `MetricsImplementation.BuildFailedInsightsAsync` | Groups normalized error text and caches the finished aggregate for 30 s in a single-flight cache keyed by preset name or custom bounds | Split into a cached occurrence projection and a separately, briefly cached enrichment (section 8.3); build the additional perspectives from the same occurrence set |
| `FailedMessageInfo` / `IMetricsStore.GetFailedMessageInsights` | Projects endpoint, event type, error text, enqueue time, and event ID | Include the exact failure occurrence's message ID. No new overload: the endpoint filter is applied in memory |
| `IFailureClassificationStore` | Reads the latest result for one failure ID, plus revision history. Each failure is one document holding every operation (SQL `StateJson`; Cosmos item `state` partitioned by failure ID) | Add a bounded bulk-read contract returning the latest completed result and latest operation state per failure ID, separate from analysis orchestration |
| `FailureClassificationService` | Coordinates individual analysis, authorization, evidence, and persistence; registered only when a provider is ready | Preserve existing analysis semantics; do not invoke it to enrich Insights. Add a read-only service that needs no provider |
| `FailureGuidanceRules` | Derives guidance when a result is created | Display persisted guidance without recalculating it under today's settings |
| Integration Intelligence registration | Registers storage, the retention worker, the schema initializer, and the controllers only when analysis is ready; a missing API key is a validation error that blocks all of them | Split activation into read-only and analysis-ready modes (section 9) |
| `IntegrationIntelligenceController` | Hosts the classification GET and history routes together with POST analyze; pruned as a unit when not ready | Serve the GET routes in read-only mode; prune only POST analyze |
| MCP `OperatorClassificationSource` | `nimbus_get_classification` is available only when `FailureClassificationService` is registered | Read through the read-only service |
| MCP `nimbus_get_metrics` failures view | Consumes the failed-insights response, hides endpoints, and recomputes the total from kept groups | Unchanged in Phase 1; requests the overview without enrichment (section 8.4) |

Implementation areas are the WebApp, message-store projections, the intelligence extension's
read capabilities, the MCP read tool, and associated tests. Core, SDK, Resolver, transport, and
adapter projects must not acquire a dependency on the intelligence extension.

## 4. Scope, identity, and counting

### 4.1 Failure occurrence

The counting unit is a persisted failed attempt, identified by its exact `MessageId`, with
`EndpointId` and `EventId` retained as source coordinates. An `ErrorResponse` message's
`MessageId` is assigned by the broker (`ResponseService.CreateResponse` leaves it null), so it is
globally unique and a safe join key. An event that fails three times contributes three
occurrences. The UI uses **Failure occurrences**, rather than implying a count of distinct events
or currently unresolved messages.

Preserve the current metrics population: `ErrorResponse` occurrences selected by
`EnqueuedTimeUtc`. Dead-lettered failures are `ErrorResponse` messages carrying
`DeadLetterReason` and are part of it; the broker's dead-letter queue and other message types
are outside Phase 1. A classification without a matching occurrence does not enter these totals.

The classification join uses `FailureMessageId == MessageId` and verifies the coordinates:
`EndpointId` compares case-insensitively, as `AllowedEndpoints` and the MCP tools do; `EventId`
and `MessageId` compare ordinally. Never join on `EventId` alone, error text, or a representative
occurrence. Mismatched coordinates are an enrichment integrity problem, not evidence that the
occurrence has never been analyzed: the occurrence is `uncertain` with an **Identity mismatch**
marker in details, and a telemetry counter increments.

Duplicate `(EndpointId, EventId, MessageId)` tuples in the projection are collapsed to one
occurrence, keeping the earliest row, and counted in telemetry. Built-in SQL enforces
`UNIQUE (EventId, MessageId)`, so duplicates can come only from custom providers. If any
occurrence in scope has a null or empty `MessageId`, enrichment status is `unsupported` for the
whole scope (section 8.1); error patterns still render.

### 4.2 Filters and time

- Preserve existing presets and custom-range validation, including the 90-day custom maximum.
- Resolve a preset once to a fixed UTC window with `[from, to)` boundaries. The resolved window
  is cached under the preset key (section 8.3). The overview returns the window it was built
  from; drill-down and group-detail requests send it back unchanged, so every request in one
  operator interaction reads the same cached occurrence projection.
- Display times in browser local time, as the rest of the WebApp does (`formatMoment`); there is
  no server-side timezone setting.
- Add an optional exact endpoint filter, applied in memory to the cached full projection. It
  adds no `IMetricsStore` overload in Phase 1. An omitted filter selects the authorized site
  scope.
- Selected window, endpoint, perspective, and group live in the page URL, so navigating to
  message details and back, retrying after an error, and sharing a `limitExceeded` link all
  return to the same scope.
- Switching grouping perspective preserves the selected window and endpoint.
- Group selection narrows drill-down without changing the overview's scope totals.

### 4.3 Historical population and current status

An occurrence remains part of the historical window if the event later completes or is skipped.
Its saved guidance describes the evidence at analysis time and is not a current retry instruction.

Occurrence drill-down shows current event status, when available, separately from failure time
and classification time. Resolve status per page with the existing
`IMessageTrackingStore.GetEventsByIds(endpointId, eventIds)`, grouping the page's rows by
endpoint: at most one call per endpoint per page, bounded by the page size. No new storage member
is needed. Missing current status is shown as unavailable; it must not imply unresolved.

An occurrence whose event has since completed or been skipped can no longer be analyzed: the
evidence builder requires a currently Failed or DeadLettered event. Phase 1 does not compute this
per occurrence in the overview, because it would need current status for every occurrence in
scope; such occurrences stay `notAnalyzed`, and the coverage text says so (section 5). Phase 1
does not add an unresolved-only filter or recovery actions to Insights.

## 5. Classification and coverage semantics

Use the latest completed, readable classification revision for each occurrence. A newer running,
failed, or unknown-outcome analysis request does not remove an earlier completed result. Each
occurrence contributes at most once to every grouping perspective, regardless of revision count.

The bulk read also returns the latest operation state for each failure, so a drill-down row can
carry a marker, **Newer analysis running**, **Outcome unknown**, or **Last analysis failed**,
beside the completed result it counts. The single-failure GET route answers 409 or 503 in those
states today instead of returning the earlier result; the marker explains why the details page
may differ. Aligning the GET route is Decision 4.

When enrichment succeeds for the complete selected scope, each occurrence has exactly one
coverage state:

| State | Definition |
|---|---|
| `classified` | A completed result has a recognized cause other than `unknown`, recognized guidance, and guidance other than `Uncertain` |
| `uncertain` | A completed result exists but has cause `unknown`, guidance `Uncertain`, category/guidance values this reader does not recognize, or coordinates that do not match the occurrence |
| `notAnalyzed` | A successful lookup establishes that no completed result exists, and analysis could be requested for the occurrence's endpoint |
| `notEligible` | No completed result exists and the occurrence's endpoint is outside the configured `AllowedEndpoints`, so analysis cannot be requested |

These labels describe available analysis, not verified causes. Unknown request outcomes and
provider errors are execution states; they do not become coverage buckets or completed results.
`notAnalyzed` includes occurrences that can no longer be analyzed because their event has since
completed or been skipped (section 4.3); the UI labels the group **Not analyzed** with the note
*includes failures that can no longer be analyzed*.

Persisted guidance is a Newtonsoft integer of `FailureGuidance`, not a name, and the category is
a free string. After deserialization the reader validates both: guidance must be a defined
`FailureGuidance` value and category must be one of the taxonomy names, including `unknown`.
Anything else is `uncertain` and keeps the raw value in details. An out-of-range integer
deserializes into the enum without error, so an explicit `Enum.IsDefined` check is required.

The following invariants apply globally and within each group:

```text
totalOccurrences = classified + uncertain + notAnalyzed + notEligible
analyzed = classified + uncertain
sum(group.occurrenceCount) = totalOccurrences  // independently for each perspective
```

Example: **25 failure occurrences · 18 analyzed**, with 15 classified, 3 uncertain, 6 not
analyzed, and 1 not eligible. Show counts with percentages so small samples remain apparent.

If enrichment is disabled, unavailable, unsupported, or exceeds its workload limit, coverage
counts are unavailable, not zero. A failed lookup must never make occurrences appear
`notAnalyzed`. Phase 1 does not present partially enriched totals as complete coverage.

## 6. Grouping perspectives

All three perspectives describe the same selected population. Their counts are not additive
across perspectives. Order groups by occurrence count descending, then a stable group key
(section 8.2).

### 6.1 Error patterns

Preserve the existing normalized-category and normalized-pattern hierarchy. The visible label
is **Error pattern**, avoiding confusion with intelligence cause categories. Add coverage to
groups when available. Existing groups remain usable without intelligence enrichment.

### 6.2 Likely failure causes

Use the existing taxonomy for occurrences in the `classified` state:

- Transient dependency.
- Authentication / configuration.
- Contract / schema.
- Business rule.
- Missing reference data.
- Application defect.
- Messaging platform.

Use separate **Uncertain**, **Not analyzed**, and **Not eligible** groups for the corresponding
coverage states. For an uncertain result, preserve the provider's suggested category and
confidence in the occurrence details; do not count it as an established category assignment in
the overview.

Retain endpoint, event type, and normalized error pattern within each cause group so unrelated
systems or problems remain distinguishable. The overview embeds, per group, its endpoints, event
types, and the top **10** underlying error patterns by count with a count of the remainder; the
full pattern list of one group is served by the paginated group-detail route (section 8.2). The
overview response is therefore bounded regardless of scope size. The interface describes these
as likely cause categories and does not label them incidents or identified root causes.

### 6.3 Recovery guidance

Use the persisted guidance independently of the cause perspective:

| Stored guidance | Display | Interpretation |
|---|---|---|
| `RetryMayHelp` | Retry may help | Processing may succeed unchanged once a temporary condition clears |
| `ChangeLikelyRequired` | Change likely required | Data, configuration, permissions, code, or another persistent condition probably needs to change |
| `Investigate` | Investigate | More investigation is needed before choosing an action |
| `Uncertain` | Uncertain | The saved result does not support reliable guidance |

Occurrences without a completed result enter **Not analyzed** or **Not eligible** as in
section 5. Unrecognized guidance enters **Uncertain**, retaining the original value in details.
A result with cause `unknown` may still have stored `Investigate` guidance: coverage uncertainty
and guidance grouping are distinct. Guidance groups embed their top patterns the same way as
cause groups. Never generate a second AI decision or recompute historical guidance using current
thresholds.

## 7. Operator experience

The page keeps the time selector and adds an endpoint filter and **Group by** control with
**Error patterns**, **Likely failure causes**, and **Recovery guidance**. Error patterns is the
default. A scope summary shows total occurrences, coverage, and both data timestamps. Window,
endpoint, perspective, and selected group are reflected in the URL (section 4.2).

The chart and details list reflect the same perspective. Each group shows its occurrence count,
coverage, affected endpoints and event types, latest occurrence, and its top underlying patterns
with an **and N more** link that opens the paginated group detail. Large pattern lists are never
rendered at once.

Selecting a group opens a paginated occurrence list with failure time, endpoint, event type,
error pattern, analysis state (including the newer-operation marker from section 5), saved
cause/guidance where available, and current event status. Each row links to the exact
occurrence in the existing details experience.

Classification details expose analysis time, provider/model, revision, question-set version,
category confidence, and persisted guidance. Existing records do not store a guidance-threshold
snapshot. Show **Threshold settings not recorded** for such records; never substitute current
settings. Adding threshold snapshots to future results is a separate, backward-compatible change.

Empty scope shows **No failure occurrences in this period**. Disabled or unavailable enrichment
leaves error patterns visible and explains why the other perspectives and coverage are
unavailable. `limitExceeded` shows the configured limit and offers a narrower window or an
endpoint filter. Retain selected filters during retries. Do not trigger analysis from load,
refresh, filtering, expansion, or navigation.

## 8. Read path, API, and storage

### 8.1 Read flow

1. Authorize the caller and validate the selected scope.
2. Read the failure-occurrence projection through the cache (section 8.3), including exact
   attempt identities. Apply the endpoint filter in memory, collapse duplicate identities, and
   return `unsupported` if any identity is missing.
3. If enabled, supported, and within the ceiling, bulk-read the latest completed classification
   and latest operation state for those identities, through the enrichment cache (section 8.3).
4. Honor classification tombstones (the `Deleted` flag lives in the same document, so this costs
   nothing) and verify coordinates (section 4.1). Do **not** re-read the message store to prove
   source existence: an occurrence is in the projection only because its row existed at most one
   occurrence-cache lifetime ago, and that cached row already exposes the same error text through
   the error-pattern groups. The retention worker tombstones classifications of deleted sources
   within 60 seconds. The exposure window for a saved classification of a deleted source is
   therefore at most the occurrence-cache TTL plus 60 seconds; the plan states it. If that is
   unacceptable for a deployment, invalidate the occurrence and enrichment caches when an event
   or endpoint is deleted rather than add a per-request existence read.
5. Build all grouping perspectives and coverage from the same occurrence set and result map.
6. Serve occurrence drill-down and group detail from the same cached projection and enrichment,
   with the original scope and selected group predicate.

Use an extension-owned query abstraction for classification enrichment, keeping SQL/Cosmos
details out of the WebApp. SQL and Cosmos must provide equivalent semantics. Prefer a separate
bulk-read contract over breaking third-party implementations of the existing classification
store interface. Its response must distinguish successful absence from unavailable storage.

**Storage layout.** Each failure's classification state is one document holding every operation
and result: SQL `dbo.FailureClassifications.StateJson`, or the Cosmos item `state` in a container
partitioned by `failureMessageId`, up to about 1.5 MB. There is no projection of the latest
result. Phase 1 therefore reads whole documents in bounded batches, Cosmos `ReadManyItemsAsync`
with `(id: "state", partitionKey: failureMessageId)` tuples and SQL `WHERE FailureMessageId IN (…)`
batches, and keeps only the latest completed result and latest operation per document. A
per-failure latest-result summary written at `CompleteAsync`, with fallback to the full document
for older records, is an optimization to adopt only if Decision 1's measurements require it. An
unbounded cross-partition query over the classification container is not acceptable.

**Absent versus unavailable.** `ReadManyItemsAsync` silently omits missing items and SQL returns
no row for an unknown ID. The bulk contract checks once per bulk read that the container or
table exists (as `ReadAsync` does today with `ReadContainerAsync`) and maps a missing container
or table, or any storage error, to `unavailable`; only a successful read with no row means
absent.

Add failure identity to built-in SQL, Cosmos, and in-memory metrics projections and their
conformance tests. Preserve compatibility for existing consumers. If a custom provider cannot
supply trustworthy attempt identities, return enrichment status `unsupported`; do not guess.
Any new storage interface member must also follow the repository's provider, decorator, and
conformance requirements; Phase 1 as specified adds none.

Bulk reads retrieve only what the layout allows and keep the minimum fields in memory. Bound
batch sizes, concurrent requests, total work, and response sizes. Do not issue `GetLatestAsync`
once per occurrence, load payloads or stack traces for enrichment, or scan the complete
classification store without scope constraints.

The ceiling below bounds only the classification read. The occurrence projection read remains
unbounded, as today: Cosmos drains every matching row, and SQL has no index leading on
`(MessageType, EnqueuedTimeUtc)`, so the query scans. Decision 1 measures it on both providers
and decides whether to add an index migration.

For this draft, propose a configurable enrichment ceiling of **10,000 occurrences per scope**,
counted after the endpoint filter. Above the ceiling, return `limitExceeded` with the configured
limit and invite a narrower scope; existing error-pattern insights remain available. Do not
analyze or sample a prefix and present it as the whole selection. Confirm this default using
provider measurements before implementation is declared ready.

### 8.2 API contract direction

Extend the existing metrics failed-insights response additively and introduce two read-only
routes under `/api/metrics/failed-insights`: an occurrence drill-down and a group detail that
pages the error patterns of one cause or guidance group. Define the final contract in
`src/NimBus.WebApp/api-spec.yaml`; generated C# and TypeScript contracts are not hand-edited.
Existing clients retain the current error-pattern response fields and behavior.

The overview adds resolved `from`/`to`, selected endpoint, `totalOccurrences` (always equal to
the existing `totalFailed`, which stays for current clients), occurrence read time,
classification read time, enrichment status, nullable coverage with the four states of
section 5, and cause/guidance groups with embedded top-10 patterns and a remainder count.
Enrichment status is one of `available`, `disabled`, `unavailable`, `unsupported`, or
`limitExceeded`. Cause/guidance totals and coverage are omitted or null unless fully available.
An internal option builds the overview without enrichment for the MCP path (section 8.4).

**Group keys** are opaque and stable within one response. Error-pattern keys are a hash of the
normalized category or pattern text (SHA-256, hex, truncated to 32 characters); cause and
guidance keys are the taxonomy or guidance name plus `uncertain`, `notAnalyzed`, and
`notEligible`. Keys are never truncated display labels and never carry query text, so they fit
a query string regardless of error-text length. They are not durable across releases, because
`ErrorPatternNormalizer` rules change; a key the server cannot resolve within the current scope
returns 400.

The drill-down and group detail accept the fixed time window, endpoint filter, perspective,
group key, page size, and continuation cursor. Default page size is 50; maximum is 100.
Order occurrences by failure time descending, then failure message ID; order patterns by count
descending, then key. The cursor is a keyset, `(failure time, message ID)` or `(count, key)`,
encoded opaquely; it carries no secret and needs no signing because the scope and group are
re-sent and re-authorized on every request. Reject a cursor that does not match the request's
scope and group with 400.

Pagination is a live read, not a transactional snapshot across requests. New classifications or
source deletions can change membership between pages. Return read timestamps, preserve stable
ordering, and offer refresh; do not claim snapshot consistency. Within a response, totals and
groups must derive from the same enrichment pass.

### 8.3 Cache and freshness

Use the existing single-flight `IStoreResultCache` in two levels. First, a preset name resolves
to a fixed window and is cached for the metrics TTL (30 s), as today; a custom range is its own
fixed window. Second, the occurrence projection is cached under the resolved `[from, to)` for
the metrics TTL. Drill-down and group-detail requests, which carry the resolved window, hit the
second level and never trigger a second projection read for the same interaction.

Cache the enrichment result (result map and operation states) under the resolved window and
endpoint filter, in the same single-flight cache, for a short TTL: proposed 15 seconds, never
longer than the metrics TTL. Without it, every poll, dashboard, and drill-down page is a full
bulk read of up to 10,000 documents, and concurrent loads multiply it. With it, concurrent loads
share one in-flight read. The finished enriched aggregate is derived from the two cached inputs
and is not cached separately.

An explicit refresh bypasses the enrichment cache but not the occurrence cache, so newly
committed classification revisions show on the next successful read while new failure
occurrences can still lag by the metrics TTL. Return both read timestamps so the UI can explain
this. Refresh after returning from message details.

Authorization stays outside shared caches. Tombstoned classifications are never exposed; the
exposure window for a deleted source is stated in section 8.1 step 4. Client responses arriving
after scope changes must be discarded.

### 8.4 MCP operator tools

`nimbus_get_metrics view=failures` consumes the same overview and hides endpoints outside the
caller's scope by recomputing the total from kept groups. Coverage counts and cause/guidance
totals cannot be split per endpoint, so in Phase 1 the tool keeps its current output and requests
the overview without enrichment: no classification read happens on the MCP path. Hidden-endpoint
rules for the other perspectives are a follow-up (section 12).

`nimbus_get_classification` reads through the read-only service (section 9), so it answers in
read-only mode exactly as the WebApp does. `nimbus_classify_failure` stays unavailable without a
ready provider.

## 9. Authorization, activation, and availability

Preserve the current site-Reader requirement for cross-endpoint Insights. Endpoint filtering
does not lower that requirement or introduce endpoint-only access to the page in Phase 1.
Apply the same permission checks to overview, group detail, occurrence drill-down, and links.

Activation has three modes:

| Mode | Condition | Registered and served |
|---|---|---|
| Off | `NimBus:IntegrationIntelligence:Enabled` false, classification disabled, a configuration binding failure, or unreadable saved settings (`IntelligenceSettingsBootstrap.FailClosed`) | Nothing. Classification storage is not read; status is absent. Unchanged |
| Read-only | Enabled, storage settings and host adapter present, but `FailureClassificationOptions.Validate()` reports provider, threshold, or evidence errors (for example a missing `ApiKey`: the state after a key-ring rotation with no deployment key) | Store, schema initializer, retention worker, and a read-only classification service. Served: Insights enrichment, the classification GET and history routes, MCP `nimbus_get_classification`. Status reports `ProviderNotConfigured` as today. POST analyze is pruned and `nimbus_classify_failure` is unavailable |
| Ready | As today | Everything in read-only mode plus analysis |

Reads use none of the provider, threshold, or evidence settings, so every validation error
permits read-only mode; only a binding failure or unreadable saved settings fails closed. The
three consumers of saved classifications must agree in every mode: Insights must never show a
classification that the details page or the MCP reports as absent.

Reads do not provision containers, create schema, write analysis records, export evidence, or
call a provider. Existing startup/provisioning paths retain responsibility for storage setup.
Preserve source-existence and retention protections from Spec 033 as specified in section 8.1
step 4, and existing error-text visibility rules; aggregate enrichment must not introduce payload
or stack-trace disclosure.

If classification storage fails, return usable error-pattern insights with enrichment status
`unavailable` and no apparent zero coverage. A missing container/table is unavailable, not an
empty result set. Ordinary message-store failure remains an Insights read error; do not show an
empty-success state. Cancellation and time budgets propagate through all reads.

## 10. Acceptance criteria and verification

| Scenario | Required outcome |
|---|---|
| One event has several failed attempts with different classifications | Each result joins only to its exact attempt; all occurrences count once |
| A failure has several completed revisions and a newer incomplete operation | Only the latest completed revision contributes; the drill-down row carries the newer-operation marker |
| Complete enrichment of a nonempty scope | Coverage partitions (four states) and group totals reconcile independently for every perspective |
| Saved cause is `unknown` or guidance is `Uncertain` | Coverage follows section 5; stored guidance is preserved according to section 6 |
| A stored guidance integer is outside `FailureGuidance`, or a category string is outside the taxonomy | Reader degrades to `uncertain`, keeps the raw value in details, and invents no supported category |
| A result's coordinates do not match its occurrence | `uncertain` with an identity-mismatch marker; never `notAnalyzed`; the telemetry counter increments |
| No result exists and the endpoint is outside `AllowedEndpoints` | `notEligible`, counted in totals and in its own group |
| Confidence thresholds change after analysis | Historical guidance and coverage are not recomputed under the new thresholds |
| A selected event has since completed or been skipped | Historical occurrence remains, with current status displayed separately; status comes from `GetEventsByIds`, at most one call per endpoint per page |
| Endpoint filter and `[from, to)` boundaries | Overview and drill-down include exactly the matching occurrences |
| Preset overview, then drill-down with the returned bounds | Same occurrence set; one projection read for the interaction |
| A custom provider returns duplicate identities, or an occurrence without a `MessageId` | Duplicates count once; a missing identity makes the scope `unsupported` while error patterns still render |
| A Reader pages through a group, or an unauthorized caller reuses its cursor | Scope persists; authorization is rechecked; no unauthorized data is returned |
| A group key or cursor from another scope, or from a previous release, is sent | 400; no data |
| New analysis completes for a visible occurrence | The next explicit refresh sees it without waiting for metrics-cache expiry |
| Several dashboards load the same scope at once | One bulk classification read (single-flight); no per-occurrence reads |
| A classification is tombstoned | It is not exposed. A source deleted within the last occurrence-cache lifetime may still appear as an occurrence, as today, until the retention worker tombstones it within the window stated in section 8.1 |
| Intelligence is disabled, unsupported, over limit, or its storage fails | Error patterns work; coverage is unavailable rather than reported as zero or not analyzed |
| Read-only mode (enabled, storage readable, no provider key) | Insights enrichment, the classification GET routes, and MCP `nimbus_get_classification` return the same saved result; POST analyze returns 404 |
| MCP `nimbus_get_metrics view=failures` with hidden endpoints | Output unchanged from today; no classification read on the MCP path |
| 10,000 occurrences spread over many patterns | Overview response stays bounded (top-10 patterns per group); group detail pages the rest |
| The page loads, refreshes, filters, expands, or pages | Zero provider calls and zero analysis/recovery writes |
| Workload reaches configured limits | Batches, concurrency, cancellation, and response sizes remain bounded; no silent partial totals |

Implementation uses RED-GREEN-REFACTOR for new behavior. Extend `MetricsImplementationTests`,
`OperatorInsightToolsTests`, the metrics conformance suite, and the classification-store tests
across SQL, Cosmos, and in-memory implementations. Include provider query-count/workload checks
to detect per-occurrence reads, plus frontend tests for grouping, coverage, unavailable states,
URL-state filter preservation, pagination, and stale-response handling.

Before an implementation PR, run the relevant backend and frontend tests, frontend build, and
Release build/checks required by the repository. Run live SQL/Cosmos conformance where configured
and report any skipped integration tests. Capture a representative finished-UI screenshot in the
PR description. This draft itself introduces no runtime code.

## 11. Decisions to resolve before implementation

1. Validate the proposed 10,000-occurrence enrichment ceiling, batch sizes, concurrency,
   enrichment-cache TTL, and latency/RU budget with SQL and Cosmos measurements, including the
   occurrence projection read and whether SQL needs an index on `(MessageType, EnqueuedTimeUtc)`.
   Fix the accepted defaults in the plan.
2. Specify the bulk-read contract (whole-document reads versus a latest-result summary), the
   read-only service shape, the final additive API schemas for overview, drill-down, and group
   detail, the hash format for group keys, and the cursor encoding, without changing public APIs
   incompatibly.
3. Confirm the exact UI placement for group expansion, group detail, and occurrence drill-down
   with a small mockup. Ensure chart, table, and coverage remain legible on narrow screens.
4. Decide whether to align the single-failure GET route with Insights by returning the latest
   completed result together with the operation state instead of 409/503, and whether that is a
   Phase 1 or follow-up change.

Resolve these decisions in the implementation plan under `docs/plan/`.

## 12. Follow-up increments

### Selective analysis

Let authorized operators choose individual failures or select occurrences within an error
pattern. Show the exact request scope and maximum workload before execution. Reuse the existing
single-failure service, evidence controls, rate limits, idempotency, and unknown-outcome semantics.
Execution progress is separate from classification coverage. This needs its own execution design
and does not imply a background bulk-job system in Phase 1.

### Representative pattern hypotheses

Allow an operator to analyze identified samples from a pattern and display the result as a
**representative hypothesis**, with sample identities, sampling scope, sample size, provenance,
and uncertainty. Sampling must consider endpoint, event type, error pattern, and time context;
normalized text alone is not sufficient evidence of equivalence.

Only individually analyzed samples contribute to individual coverage and cause/guidance totals.
Never persist a representative result as if every occurrence was analyzed. The hypothesis needs
its own identity, lifecycle, and presentation contract before implementation.

### Resumable bulk execution

Introduce server-side jobs, budgets, progress, cancellation, and resumability only when operator
usage justifies them. Coordinate independent single-failure operations without silently retrying
requests with ambiguous completion. Preserve earlier completed classifications through partial
failures, and distinguish execution status from result coverage.

### Hidden-endpoint rules for the MCP

Define how coverage and cause/guidance groups are presented when the caller cannot see every
endpoint, then let `nimbus_get_metrics` return them.

### Latest-result summary

If measurements show whole-document bulk reads are too expensive, write a latest-result summary
at `CompleteAsync` and read it in bulk, falling back to the full document for older records.

### Status-based eligibility

Compute "can no longer be analyzed" per occurrence (event completed, skipped, or no longer
Failed/DeadLettered) once current status is available for the whole scope at acceptable cost,
and split it out of `notAnalyzed`.

## Revision history

- Revision 2 (2026-11-06): applied review findings. Added the read-only activation mode shared
  by Insights, the GET routes, and the MCP (sections 3, 9); the `notEligible` coverage state and
  the never-analyzable note (5); operation-state markers and reader validation of persisted
  values (5); the whole-document bulk-read layout and absent-versus-unavailable rule (8.1);
  tombstone-only source checks with a stated exposure window (8.1); hashed group keys, keyset
  cursors, top-10 embedded patterns, and the group-detail route (6.2, 8.2); the two-level
  occurrence cache, the enrichment cache with refresh bypass, and single-flight (8.3); the MCP
  failures view without enrichment (8.4); identity, duplicate, and `unsupported` rules (4.1);
  in-memory endpoint filter, URL state, browser-local time, and `GetEventsByIds` (4.2, 4.3);
  the unbounded projection read and SQL index note (8.1, 11); Decision 4.
- Revision 1 (2026-11-06): initial draft.
