# Fold Insights into the Failed page

The Insights page (`/Insights`) is retired. Its classification groupings (likely cause, recovery
guidance, fault origin) and its coverage figure move to the Failed page as three more groupings
next to **Group by error**, where every group can be resubmitted or skipped. The history view
(failed attempts whose event has since completed or been skipped) is dropped: Failed keeps
showing unresolved failures only.

One PR, commits per part (WebApp API, WebApp frontend, docs). No storage or MCP change.

Status: implemented on `feat/webapp-insights-into-failed`. Where the implementation differs
from the design below, [As built](#as-built) says how.

## Why

- Insights' default grouping, **Error patterns**, is the same grouping as Failed → **Group by
  error** (`FailedImplementation.BuildErrorGroups` says so). Failed's version has filters, a
  histogram and group-level Resubmit/Skip; Insights' version is read-only.
- When failure intelligence is off, unsupported or over the enrichment ceiling, Insights shows
  only that duplicated grouping.
- The part with no equivalent on Failed is the aggregate classification view. It is most useful
  next to the actions: "these 40 failures are *Transient dependency* / *Retry may help*" is a
  finding an operator wants to act on in place.
- Decision by the repo owner (2026-11-24): drop the history view rather than add an
  "include resolved" mode to Failed.

## What changes for the operator

| Today | After |
|---|---|
| Sidebar: Diagnose → Insights | Entry removed. `/Insights` redirects to `/Failed?view=error`; `?by=cause\|guidance\|origin` maps to the matching new view, `endpoint=` to `endpointId=`, and `period`/`from`/`to` to Failed's range parameters. `group` and `panel` are dropped. |
| Failed views: Failures · Group by endpoint · Group by error | Adds **Group by cause · Group by guidance · Group by origin**. The three are hidden when failure intelligence is off, and disabled with the existing banner text when saved classifications can't be read. |
| Coverage bar on Insights only | Shown above the grouped views and on each group row, whenever classifications are available (also on **Group by error**). |
| Insights counts failed attempts, including resolved ones | Gone. Counts are unresolved failures, the same number as the rest of the Failed page. |
| Drill-down table (Analysis, Likely cause, Guidance, Recorded by, Analyzed at, Status now) | Replaced by the group's failures, each opening the existing detail panel with the Failure intelligence card. Each failure row in a classification view shows its cause and guidance as badges. |

A classification group expands to its error patterns, then to its failures: the same three
levels as **Group by error**, so Insights' "top error patterns" per group needs no separate
panel.

## Design

### API: `POST /api/v1/failed/error-groups` gains `groupBy`

Edit `src/NimBus.WebApp/api-spec.yaml` (never the generated files):

- `FailedErrorGroupsRequest.groupBy`: `ErrorPattern` (default) | `Cause` | `Guidance` |
  `FaultOrigin`. Reuse the existing `InsightsPerspective` enum.
- `FailedErrorGroups` adds `enrichment` (`InsightsEnrichmentStatus`), `coverage`
  (`InsightsCoverage`, null unless `Available`) and `analysisAvailable`.
- `FailedErrorGroup` adds `groupKey`, `label` and `coverage`. For a classification perspective
  the group is the cause, guidance or origin and `subGroups` are its error patterns.
  `errorCategory` keeps its meaning for `ErrorPattern` and carries the label otherwise, so an
  existing client keeps working.
- `FailedErrorSubGroup` adds `coverage`. `FailedEventRef` adds `category`, `guidance`,
  `faultOrigin` and `analysisState` (`InsightAnalysisState`).

All additions are optional fields; nothing is removed or renamed.

`FailedImplementation.PostFailedErrorGroupsAsync` keeps its read (per-endpoint Reader scope via
`ResolveScopeAsync`, `ErrorGroupCap` of 5,000) and then:

1. Reads saved classifications for the failures' `LastMessageId`s through
   `IInsightsEnrichmentSource`. `LastMessageId` is the id the detail panel's Failure
   intelligence card already uses (`failure-detail-panel.tsx:385`), so an event that was
   resubmitted and failed again is *Not analyzed* until its new attempt is classified.
2. Assesses each failure with the rules Insights uses today.
3. Groups by the requested perspective.

Rules that differ from Insights:

- A failure without a `LastMessageId` is **Not analyzed**. It does not turn the whole response
  `Unsupported`, as one occurrence without a message id does on Insights.
- The scope is at most 5,000 failures, below the default `EnrichmentCeiling` of 10,000. If an
  operator lowered the ceiling below the scope, return `LimitExceeded` as today.
- `Truncated` already says the grouping stopped at the cap; coverage then describes the
  failures read, and the UI says so next to the existing truncation note.
- `Unsupported` events are never eligible for classification (the card treats only Failed and
  DeadLettered as eligible): count them as **Not eligible**.

### Shared assessment

`FailedInsightsService.Assess`, `Marker`, `Coverage` and the three label dictionaries move to
an internal `FailureAssessor` in `src/NimBus.WebApp/Services/Insights/`, taking
`(endpointId, eventId, messageId)` instead of an `OccurrenceRow`. `FailedInsightsService` and
`FailedImplementation` both call it. Behavior of the service is unchanged; its existing tests
are the characterization tests for the move.

The classification read is cached with `IStoreResultCache` under a key of the sorted message
ids' hash, using `EnrichmentCacheSeconds`, so switching between the three views reads storage
once. Authorization stays outside the cache: the ids come from events the caller was already
allowed to read.

### What stays

- `IFailedInsightsQuery` and `FailedInsightsService`: the MCP insight tool
  (`OperatorQueries.GetFailureInsightsAsync`) and the worklist
  (`OperatorWorklist` → `GetOccurrencesForEndpointsAsync`) use them.
- `GET /api/v1/metrics/failed-insights`, `/occurrences` and `/patterns`: the HTTP API is public
  API (`docs/versioning.md`). Mark the three operations `deprecated: true` in the spec, say
  "removed in the next major" in `docs/webapp-rest-api.md`, and keep their tests.
- `components/insights/insights-labels.ts` and `coverage-bar.tsx`: used by the Failure
  intelligence card and by the new views. Move both to `components/failed-messages/` only if
  the import paths bother a reviewer; otherwise leave them.

### Frontend (`src/NimBus.WebApp/ClientApp/src`)

- `pages/failed-messages.tsx`: `View` becomes `list | endpoint | error | cause | guidance |
  origin`. The error-groups effect runs for the four grouped views and sends `groupBy`. The
  tab list renders the three new tabs from the response's `enrichment` (hidden for `Disabled`,
  disabled with a title otherwise). `showSimilar` is unchanged.
- `components/failed-messages/failed-error-groups.tsx`: takes the perspective; uses
  `label ?? errorCategory` for the group title; renders `CoverageBar` per group and sub-group
  when present; shows cause and guidance badges on failure rows; shows the *Not analyzed*
  hint without the "can no longer be analyzed" sentence, which described resolved events.
- Group-level **Resubmit** on a guidance group keeps the existing confirm dialog and adds one
  line for classification views: "Guidance describes each failure when it was analyzed." Saved
  guidance is not a current retry instruction (`docs/webapp-insights.md`), so the dialog must
  not present it as one.
- `app.tsx`: the `/Insights` route renders a redirect component that maps the query string as
  in the table above. `components/sidebar.tsx`: remove the entry and its icon;
  `components/topbar.tsx`: remove the `Insights` case.
- Delete `pages/insights.tsx`, `pages/insights.test.tsx`, `pages/insights.range.test.tsx`,
  `components/insights/group-patterns.tsx` and `components/insights/occurrence-table.tsx`.

### Docs

- `docs/webapp-insights.md` is deleted and its still-valid content (what the groupings and the
  four coverage states mean, the guidance caveat) moves into the Failed page's documentation.
  Remove it from `website/sidebars.js` and `docs/README.md`; fix links from
  `docs/integration-intelligence.md`, `docs/webapp-rest-api.md` and
  `samples/CrmErpDemo/README.md` (the portal build fails on broken links).
- `docs/spec/043-intelligent-failed-message-insights/spec.md`: add a note at the top that the
  page was folded into Failed by this plan and that occurrence-based history is no longer shown
  in the WebApp. The spec is otherwise left as the record of the backend that remains.
- `docs/webapp-rest-api.md`: document `groupBy` and the new fields; mark the three metrics
  operations deprecated.

## Order of work

1. **Characterize.** Confirm `FailedInsightsServiceTests*` cover the assessment rules
   (identity mismatch, uncertain, not eligible, markers). Add a test for any rule that isn't.
2. **Extract `FailureAssessor`** with no behavior change; the existing tests stay green.
3. **API, RED then GREEN.** In `tests/NimBus.WebApp.Tests/FailedImplementationTests.cs` add
   failing tests, then implement:
   - `groupBy` absent returns today's response plus `enrichment`;
   - `Cause`, `Guidance` and `FaultOrigin` group as Insights does for the same classifications;
   - a failure without `LastMessageId` is *Not analyzed*; an `Unsupported` event is *Not eligible*;
   - a caller with Reader on one endpoint gets classifications for that endpoint only;
   - intelligence off returns `Disabled` and error-pattern groups for any `groupBy`;
   - PII redaction still applies to `errorText` (see `PayloadRedactionTests`).
4. **Frontend, RED then GREEN.** Extend `pages/failed-messages.test.tsx` and
   `components/failed-messages/failed-error-groups.test.tsx`: the new tabs appear only when
   enriched; a cause group expands to patterns and failures; Resubmit on a group sends its
   events; `/Insights?by=guidance&endpoint=x&period=7d` lands on the matching Failed URL.
5. **Remove the page**, sidebar entry and its tests.
6. **Docs.**

## Verification

```bash
dotnet build src/NimBus.slnx -c Release          # with NSwag generation enabled (no SkipSpaBuild)
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
npm --prefix website ci && npm --prefix website run build
```

Manually, on the CrmErpDemo stack with failure intelligence recordable and a few classified
failures: the three views group as Insights did for the same failures, coverage adds up to the
failure count, a group Resubmit clears the group, and `/Insights` redirects. Capture a
screenshot of **Group by cause** for the PR description (WebApp feature).

## As built

- **Tab visibility comes from the histogram.** The page loads the grouping only in a grouped
  view, so the list view could not tell whether to offer the three tabs. `FailedHistogram`
  gained `classificationsAvailable` (failure intelligence is on), which the page always has.
  A deep link to a classification view while intelligence is off renders error groups.
- **No `deprecated: true` in the spec.** The three `metrics/failed-insights*` operations are
  marked deprecated in their descriptions and in `docs/webapp-rest-api.md` only. The flag
  was left out because NSwag may turn it into `[Obsolete]` on generated members, and Release
  makes a CS0618 in their callers an error. This was not tried; the flag can be added with the
  removal work if the generated code allows it.
- **`FailedErrorGroups.enrichmentLimit`** was added so the limit-exceeded message can name the
  limit, as the Insights overview did.
- **The classification read is not cached per view.** One cache entry, keyed by the hash of the
  failures' message ids, serves the four groupings for `EnrichmentCacheSeconds`. There is no
  Refresh button: a classification recorded meanwhile shows after that lifetime.
- **Failure rows show cause and guidance as one muted line**, not badges; the row's status
  badge stays the only badge.
- **Frontend tests were written after the frontend change**, not before it. The API tests were
  run red first (11 failing) and then green.
- `components/insights/` keeps `coverage-bar.tsx` and `insights-labels.ts`; the labels file lost
  the exports only the removed page used.

## Not done

- No history of resolved failures in the WebApp. The MCP insight tool and the deprecated
  metrics endpoints still report failed attempts.
- No removal of the `metrics/failed-insights*` endpoints or of `IFailedInsightsQuery`; the
  endpoints go in the next major.
- No bar chart of group sizes. The count column and the histogram cover it; add one only if
  operators miss it.
- Insights' "From endpoint · event type" source list per group is not carried over in this
  change. `UnresolvedEvent.OriginatingFrom` is available, so it can follow if the fault-origin
  view needs it.
- No new role. The views follow Failed's per-endpoint Reader scope, so a user with Reader on
  some endpoints now sees classification groupings for those endpoints; Insights required
  site-wide Reader. The detail panel already shows such a user the same classifications one
  failure at a time.
