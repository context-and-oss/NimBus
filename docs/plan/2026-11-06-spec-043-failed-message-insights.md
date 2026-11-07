# Spec 043 — Intelligent Failed Message Insights: implementation plan

Date: 2026-11-06. Spec: [043](../spec/043-intelligent-failed-message-insights/spec.md) (revision 2).
Branches: one per PR below, prefixed `feat/insights-`. Spec PR: #323.

## Problem

Failed Message Insights groups failures by normalized error text only. Spec 043 adds two
groupings from saved failure classifications (likely cause, recovery guidance), four-state
coverage, an endpoint filter, occurrence drill-down and group detail, and a read-only activation
mode so saved results stay readable without a provider key. Phase 1 reads only.

This plan resolves the four decisions in spec section 11 and sequences the work into five PRs
that each leave `master` releasable. Everything below was checked against the working tree at
`1e0e72b0`.

## Decisions

### D1. Limits and cache lifetimes (proposed defaults; PR 1 measures and fixes them)

| Setting (`NimBus:Insights:*`) | Default | Rationale |
|---|---|---|
| `EnrichmentCeiling` | 10 000 occurrences, counted after the endpoint filter | Spec 8.1 |
| `EnrichmentCacheSeconds` | 15 | Spec 8.3; never above the 30 s metrics TTL |
| `BulkReadBatchSize` | 500 ids | SQL `IN` stays under the 2 100-parameter limit; Cosmos `ReadManyItemsAsync` tuples per call |
| `BulkReadConcurrency` | 4 batches in flight | 10 000 ids = 20 batches, 5 rounds |
| `TopPatternsPerGroup` | 10 | Spec 6.2 |

Bound in a new `FailedInsightsOptions` (WebApp, `Services/Insights/`), validated at startup
(ceiling 100–100 000, cache 0–30 s, batch 50–1 000, concurrency 1–8, patterns 1–50).

**Measurement step (PR 1, before PR 3 is opened).** Using the local Cosmos emulator and SQL
container recipe, seed 100 000 `ErrorResponse` rows over 30 days and 10 000 classification
documents with one to three operations each, then record:

- the occurrence projection read for the 1d, 7d and 30d presets on both providers (Cosmos RU
  from `FeedResponse.RequestCharge`, SQL duration and logical reads);
- one cold enrichment of 10 000 ids in 500-id batches (total RU, p95 duration);
- the same with the enrichment cache warm.

Accept the defaults if a cold 10 000-id enrichment stays under 50 000 RU and 3 s p95, and a
30-day projection read under 1 s on SQL. If the SQL projection read exceeds that, add a
migration in `src/NimBus.MessageStore.SqlServer/Schema/` (next free number) creating
`IX_Messages_MessageType_EnqueuedTimeUtc ON Messages (MessageType, EnqueuedTimeUtc) INCLUDE
(EndpointId, EventTypeId, EventId, MessageId)`; the error text still comes from
`JSON_VALUE(MessageContentJson, …)` per row. Record the numbers in this plan under
"Measurements" before PR 3.

### D2. Contracts

**Bulk read.** Whole documents, no summary field (spec 8.1). A new interface in the extension:

```csharp
public interface IFailureClassificationBulkReader
{
    Task<ClassificationBulkReadResult> ReadLatestAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken ct = default);
}
public sealed record ClassificationBulkReadResult(bool Available, IReadOnlyDictionary<string, ClassificationSnapshot> Snapshots);
public sealed record ClassificationSnapshot(FailureClassification? Latest, string? LatestOperationStatus, string? LatestOperationErrorCode, bool Deleted);
```

`AtomicClassificationStore` implements it through a new `protected abstract
IAsyncEnumerable<ClassificationDocument> ReadManyAsync(IReadOnlyList<string> ids, CancellationToken ct)`
and derives the snapshot from `Operations` exactly as `GetLatestAsync` and
`GetLatestOperationAsync` do (including the expiry-to-`AnalysisOutcomeUnknown` normalization).
Implementations:

- `SqlClassificationStore`: `SELECT FailureMessageId, StateJson FROM dbo.FailureClassifications WHERE FailureMessageId IN (@p0…)`
  per batch. `SqlException` number 208 (invalid object name) and any other storage error →
  `Available = false`.
- `CosmosClassificationStore`: `ReadContainerAsync` once per bulk read (NotFound → unavailable),
  then `ReadManyItemsAsync<ClassificationDocument>` with `("state", new PartitionKey(id))`
  tuples per batch. Missing items are absent only because the container check passed.
- `InMemoryClassificationStore` (test project) and `UnavailableClassificationStore`
  (`Available = false`).

A registered `IFailureClassificationStore` that does not implement the interface yields
enrichment status `unsupported`.

**Read-only service.** `FailureClassificationReader` (extension, sealed) with `GetLatestAsync`,
`GetHistoryAsync` (moved from `FailureClassificationService`, which delegates to keep its API)
and `ReadForInsightsAsync(ids)`; it depends on `IFailureClassificationStore`,
`IIntegrationIntelligenceHost` and `IMessageTrackingStore` only. `FailureClassificationService`
keeps `AnalyzeAsync`.

**Controllers.** Split `IntegrationIntelligenceController` into
`IntegrationIntelligenceReadController` (GET and GET `history`, depends on the reader) and
`IntegrationIntelligenceController` (POST only, depends on the service), both on
`api/integration-intelligence/failures/{eventId}/{messageId}/classification`. The feature
provider prunes all intelligence controllers when not readable and only the POST controller
when not ready.

**Registration.** In `AddNimBusIntegrationIntelligence`:
`readable = options.Enabled && bindingErrors.Length == 0 && hasStorage && hasHostAdapter`,
`ready = readable && validationErrors.Length == 0`. When readable: store, `SqlClassificationSchemaInitializer`,
`ClassificationRetentionWorker`, `FailureClassificationReader`, `IntegrationIntelligenceStatusService`.
When ready: redactor, evidence builder, HTTP client, provider, `FailureClassificationService`.
`IntegrationIntelligenceActivation` gains `bool Readable { get; init; }` (init property, not a
positional parameter, so existing constructions compile). `OperatorClassificationSource` takes
`IEnumerable<FailureClassificationReader>` for reads and keeps `IEnumerable<FailureClassificationService>`
for `AnalyzeAsync`; `IsAvailable` means a reader is present.

**API (`src/NimBus.WebApp/api-spec.yaml`, Metrics tag, site Reader).** All additions are
optional properties or new routes; `totalFailed` stays.

| Method | Route | Parameters → Result |
|---|---|---|
| GET | `/api/metrics/failed-insights` (existing) | adds `endpointId`, `refresh` (bool: bypass the enrichment cache) → `FailedInsightsOverview` + new fields |
| GET | `/api/metrics/failed-insights/occurrences` | `from`, `to` (required), `endpointId`, `perspective`, `groupKey`, `pageSize` (default 50, max 100), `continuationToken` → `InsightOccurrencePage`; 400 for an unknown key or a cursor from another scope |
| GET | `/api/metrics/failed-insights/patterns` | same scope, `perspective` (cause or guidance), `groupKey`, `pageSize`, `continuationToken` → `InsightGroupPatternPage` |

New schema members on `FailedInsightsOverview`: `from`, `to`, `endpointId`,
`totalOccurrences`, `occurrencesReadAt`, `classificationsReadAt`, `enrichment`
(`EnrichmentStatus`: available, disabled, unavailable, unsupported, limitExceeded),
`enrichmentLimit`, `coverage` (`InsightsCoverage`: classified, uncertain, notAnalyzed,
notEligible, analyzed, total; nullable), `causeGroups` and `guidanceGroups` (`InsightGroup[]`,
nullable). `ErrorPatternGroup` and `ErrorSubGroup` gain `groupKey` and nullable `coverage`.
`InsightGroup`: `groupKey`, `label`, `count`, `coverage`, `endpoints`, `eventTypes`,
`latestOccurrence`, `topPatterns` (`InsightGroupPattern`: `groupKey`, `normalizedPattern`,
`count`, `exampleErrorText`), `morePatterns`. `InsightOccurrence`: `messageId`, `eventId`,
`endpointId`, `eventTypeId`, `failedAt`, `errorPatternKey`, `normalizedPattern`,
`analysisState` (`InsightAnalysisState`: classified, uncertain, notAnalyzed, notEligible,
unavailable), `operationMarker` (nullable: newerAnalysisRunning, outcomeUnknown,
lastAnalysisFailed, identityMismatch), `category`, `categoryConfidence`, `guidance`,
`revision`, `classifiedAt`, `currentStatus` (nullable `ResolutionStatus`). Pages carry
`continuationToken` and `readAt`. The cursor parameter is named `continuationToken` like the
rest of the API.

**Group keys.** `ec:` + 32 hex chars of SHA-256 over the category string; `ep:` + the same
over the normalized pattern; cause keys are the taxonomy names; guidance keys are the
`FailureGuidance` names; plus `uncertain`, `notAnalyzed`, `notEligible`. Keys are resolved
against the current scope's groups; an unresolvable key is 400.

**Cursor.** Base64url JSON `{ "t": <failedAt ticks>, "m": <messageId>, "s": <scope hash> }`
for occurrences and `{ "c": <count>, "k": <groupKey>, "s": … }` for patterns, where the scope
hash is SHA-256 over `from|to|endpointId|perspective|groupKey`. A mismatching `s` is 400. No
signing: the scope is re-sent and re-authorized on every request.

### D3. Layout

Chart (top 15 groups of the current perspective, existing recharts bar) stays above. Below it:
the scope summary line (count, "analyzed" with a segmented coverage bar, both read times,
Refresh, and the enrichment banner when not `available`), then a two-column area at ≥ 1024 px:
group list left, drill-down right. Under 1024 px the columns stack and the drill-down opens
below the selected group. Groups show their top patterns inline with an "and N more" link that
opens the patterns page in the right column. The wireframe in the spec's visualization is the
reference; the PR 5 description carries the screenshot.

### D4. Single-failure GET alignment

Phase 1 keeps the GET route's current 409/503 answers and ships the drill-down marker (spec 5).
`intelligence-card.tsx` already renders those states; changing the route would widen Phase 1
and touch the MCP tool's error mapping. Alignment is a follow-up, to be scheduled with a
`docs/versioning.md` note because it changes an HTTP contract.

## Implementation

Delivered as five PRs, in order. Each has its own branch, RED-first tests and a Release build.

### PR 1 — `feat(store): expose the failed attempt's message id in failed-message insights`

- `FailedMessageInfo.MessageId` (`string?`, nullable so a custom provider that never sets it
  reads as `unsupported`).
- Projections: SQL `SqlServerMetricsStore.GetFailedMessageInsights` adds `MessageId` to the
  SELECT; Cosmos `CosmosDbMetricsStore` adds `c.message.MessageId` and a DTO property;
  `InMemoryMessageStore` maps `m.MessageId`.
- `MetricsStoreConformanceTests.GetFailedMessageInsights_returns_recent_failed_error_details`
  asserts the id.
- Measurement step from D1; results appended to this plan.

### PR 2 — `feat(intelligence): read saved classifications without a provider`

- `IFailureClassificationBulkReader`, `ClassificationBulkReadResult`, `ClassificationSnapshot`
  in `IntegrationIntelligenceContracts.cs`; `ReadManyAsync` on `AtomicClassificationStore`
  with the SQL, Cosmos, in-memory and unavailable implementations.
- `FailureClassificationReader`; `FailureClassificationService.GetLatestAsync/GetHistoryAsync`
  delegate to it.
- Controller split and feature-provider change; `IntegrationIntelligenceActivation.Readable`;
  registration split as in D2; `OperatorClassificationSource` over the reader.
- Telemetry: `nimbus.intelligence.insights.bulk_reads` (counter, tags `provider`, `outcome`)
  and `nimbus.intelligence.insights.documents` (histogram of ids per read) in
  `NimBusIntelligenceTelemetry`.
- `docs/integration-intelligence.md`: the three activation modes and what each serves.

### PR 3 — `feat(webapp): group failed insights by likely cause and recovery guidance`

- `api-spec.yaml`: the overview additions, `endpointId` and `refresh` parameters, and the new
  schemas (the two routes come in PR 4). NSwag regenerates `ApiContract.g.cs` and
  `api-client/index.ts`; the build that validates this must not set `SkipSpaBuild`.
- `Services/Insights/FailedInsightsService.cs` behind `IFailedInsightsQuery`:
  `BuildOverviewAsync(InsightsScope scope, bool enrich, bool refresh, CancellationToken)`.
  It owns window resolution and the two-level occurrence cache (keys
  `metrics:failed-insights:window:{preset}` and `metrics:failed-insights:rows:{fromTicks}-{toTicks}`),
  duplicate collapsing, missing-id detection, the in-memory endpoint filter, the ceiling, the
  enrichment cache (`metrics:failed-insights:enrichment:{fromTicks}-{toTicks}:{endpointId}`, TTL
  from options, bypassed when `refresh`), coordinate verification, reader validation
  (`Enum.IsDefined(typeof(FailureGuidance), value)` and the taxonomy set, which lives in
  `FailureClassificationQuestionSet` and is exposed as a static `KnownCategories`), coverage,
  the three perspectives, group keys and the top-N pattern embedding.
- `IInsightsEnrichmentSource` (WebApp) with one implementation over
  `IEnumerable<FailureClassificationReader>` plus `FailureClassificationOptions.AllowedEndpoints`
  for `notEligible`; absent reader → `disabled`.
- `MetricsImplementation.GetMetricsFailedInsightsAsync` delegates to the service with
  `enrich: true`. `OperatorQueries` takes `IFailedInsightsQuery` and calls it with
  `enrich: false`; `OperatorInsightToolsTests` fakes the new interface instead of the metrics
  failed-insights method.
- `FailedInsightsOptions` bound from `NimBus:Insights`.
- Telemetry in the WebApp meter: `nimbus.insights.identity_mismatches`,
  `nimbus.insights.duplicate_identities`, `nimbus.insights.enrichment` (counter, tag `status`).

### PR 4 — `feat(webapp): failed insights occurrence drill-down and group patterns`

- `api-spec.yaml`: the two routes and page schemas; `MetricsImplementation` implements the two
  generated members (match the generated nullability exactly: CS8767 fails Release).
- `FailedInsightsService.PageOccurrencesAsync` and `PagePatternsAsync` over the same cached
  inputs; keyset ordering, cursor encode/validate, 400 mapping.
- Current status per page: group rows by endpoint, call
  `IMessageTrackingStore.GetEventsByIds(endpointId, eventIds)` once per endpoint, map
  `ResolutionStatus`; a thrown `EndpointNotFoundException` or a missing event → `currentStatus`
  null (shown as unavailable).

### PR 5 — `feat(webapp): group-by, coverage and drill-down on the Insights page`

- `pages/insights.tsx`: URL state through `useSearchParams` (`period` or `from`/`to`,
  `endpoint`, `by`, `group`, `token`), the Group by control, the endpoint select (same endpoint
  list the Failed messages page uses), the scope summary, Refresh (`refresh=true`), a request
  sequence ref that discards late responses, and refetch on `focus`/return from details.
- New components under `components/insights/`: `scope-summary.tsx`, `enrichment-banner.tsx`,
  `coverage-bar.tsx`, `group-list.tsx`, `occurrence-table.tsx`, `group-patterns.tsx`. Rows link
  to the existing event details route with the occurrence's message id.
- Labels per spec 6 and 7: "Error pattern", "Not analyzed (includes failures that can no longer
  be analyzed)", "Not eligible", "Threshold settings not recorded".
- Docs: the Insights section of the WebApp documentation; screenshot in the PR.

## Tests (RED first)

PR 1
- Conformance: `GetFailedMessageInsights` returns each attempt's `MessageId` (memory, SQL,
  Cosmos).

PR 2
- `DurableStoreConformanceTests` (memory, sql, cosmos): bulk read of 1 200 ids where some are
  absent, some tombstoned, some with an active or expired newer operation; the result holds the
  latest completed result and the normalized operation state and distinguishes absent from
  `Available = false` (table/container dropped → unavailable).
- `IntelligenceActivationTests`: enabled with storage and no `ApiKey` → `Readable` true,
  `Ready` false, GET and `history` routes present, POST absent, status `ProviderNotConfigured`;
  binding failure → nothing registered; ready → all routes.
- `ServiceAndEvidenceTests`: `FailureClassificationService.GetLatestAsync` behaves as before
  through the reader (409 on active, 409 on unknown outcome, 503 on failed).
- MCP: `nimbus_get_classification` answers in read-only mode; `nimbus_classify_failure` reports
  the feature unavailable.

PR 3
- `FailedInsightsServiceTests` over `InMemoryMessageStore`, `InMemoryClassificationStore` and a
  real `StoreResultCache`:
  - three attempts of one event with different results count three occurrences, each joined to
    its own id;
  - latest completed revision wins over a newer active operation; the row marker is set;
  - coverage partitions and per-perspective sums reconcile for a mixed scope (classified,
    uncertain by `unknown`, uncertain by out-of-range guidance integer, uncertain by unknown
    category string, uncertain by mismatched endpoint, not analyzed, not eligible);
  - `[from, to)` boundaries and the endpoint filter select exactly the matching occurrences;
  - duplicate identities count once; a missing id makes the scope `unsupported`; error-pattern
    groups still present;
  - over the ceiling → `limitExceeded` with the limit; storage unavailable → `unavailable`
    and null coverage; no reader → `disabled`;
  - a recording bulk reader proves one bulk read per scope, batches of at most
    `BulkReadBatchSize`, and never a `GetLatestAsync` call;
  - preset overview then a request with the returned window reads the projection once;
  - two concurrent builds share one bulk read; `refresh` bypasses the enrichment cache but not
    the occurrence cache; a tombstoned classification is never exposed.
- `MetricsImplementationTests`: non-Reader is forbidden; invalid window is 400; `totalFailed`
  equals `totalOccurrences`.
- `OperatorInsightToolsTests`: the failures view calls the query with `enrich: false` and its
  output is unchanged with hidden endpoints.

PR 4
- Occurrence paging: ordering by failed time then id, page size bounds, cursor round trip,
  cursor from another scope → 400, unknown group key → 400, current status via one
  `GetEventsByIds` call per endpoint (recording store), unauthorized caller forbidden.
- Pattern paging: top-N embedding plus the remainder, and the page continuing past it.

PR 5 (Vitest, `npm run test:ci`)
- Grouping switches perspective without refetching the window; URL reflects the scope and a
  reload restores it.
- Coverage and the banner for each enrichment status; "Not analyzed" and "Not eligible" labels.
- Drill-down pages with the cursor, shows the row marker and current status, and links to the
  details route.
- A late response for a previous scope is discarded; Refresh sends `refresh=true`.
- Existing `insights.range.test.tsx` keeps passing.

## Verification

Per PR: `dotnet build src/NimBus.slnx -c Release` (zero CS warnings),
`dotnet test src/NimBus.slnx -c Release --no-build`; PR 3 onwards also
`npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`. PR 1 and PR 2 run the
live SQL and Cosmos conformance suites (`NIMBUS_SQL_TEST_CONNECTION`,
`NIMBUS_COSMOS_TEST_CONNECTION`) and report them; CI rejects skipped conformance tests. PR 5
captures the finished page (grouped by cause, one group open) for the PR description. The
measurement results from D1 must be in this plan before PR 3 is opened.

## Measurements

To be filled in by PR 1.

## Out of scope

- Selective analysis, representative hypotheses, resumable bulk execution, hidden-endpoint
  rules for the MCP failures view, a latest-result summary field, and status-based eligibility
  (spec section 12).
- Changing the single-failure GET route's 409/503 answers (D4).
- A `nb` CLI counterpart.
