# Failed Message Insights page (WebApp)

The **Insights** page (`/Insights`, under DIAGNOSE in the sidebar) groups the failures in a time
window so you can see what kinds of problems occurred and where to look first
([Spec 043](spec/043-intelligent-failed-message-insights/spec.md)). It counts **failure
occurrences**: every failed attempt. An event that failed three times counts three times, and an
occurrence stays in its window after the event later completes or is skipped.

The page only reads. It never starts an AI analysis and never resubmits or skips anything.

## What the page shows

- **Range**: `1h`, `12h`, `1d` (default), `3d`, `7d`, `30d` or a custom range of up to 90 days,
  plus an **endpoint** filter.
- **Group by**
  - **Error patterns** (default): failures grouped by normalized error text, as before. This
    always works, with or without failure intelligence.
  - **Likely failure causes**: grouped by the category saved by
    [failure intelligence](integration-intelligence.md), such as *Transient dependency* or
    *Missing reference data*, plus **Uncertain**, **Not analyzed** and **Not eligible**.
  - **Recovery guidance**: grouped by the guidance saved with each result, such as *Retry may
    help* or *Change likely required*. Guidance describes the failure when it was analyzed. It is
    not a current retry instruction, and it is never recomputed with today's thresholds.
- **Coverage**: how many occurrences have a saved analysis. **Classified** means a known cause
  and confident guidance. **Uncertain** means the result is inconclusive or unrecognized. **Not
  analyzed** means no saved result; this includes failures that can no longer be analyzed
  because their event has since completed or been skipped. **Not eligible** means the endpoint is
  outside the analysis allow-list.
- **Groups**: each shows its count, coverage, endpoints, event types, latest occurrence and top
  error patterns. **and N more** lists the rest.
- **View failures** opens a paged list of the group's failed attempts with their analysis,
  saved cause and guidance, when that result was made (**Analyzed at**), and the event's status
  now. A marker shows when a newer analysis is
  running, its outcome is unknown or it failed. Each row links to the event's details with that
  failed attempt's failure intelligence card outlined and scrolled into view; the card names the
  provider and model and notes that threshold settings were not recorded with the result.

Likely causes, recovery guidance and coverage need saved classifications. When they aren't
available the page keeps error patterns and says why: intelligence is off, its storage can't be
read, the message store can't identify failed attempts, or the selection has more failures
than the enrichment limit (10,000 by default; narrow the range or pick an endpoint). A link that
asks for causes or guidance in such a selection opens on error patterns. When saved
classifications can be read but no provider is configured (read-only mode), the page says that
new analysis is unavailable, so **Not analyzed** failures stay that way until a provider is
configured.

Saved classifications are read again when the page opens and on **Refresh**. New failures can
take up to 30 seconds to appear. Changing the range or endpoint clears the page until that
selection has loaded; **Refresh** keeps the current content, dimmed, until the new read arrives.
If a selection can't be loaded, the page says so and offers **Try again**. The range, endpoint, grouping and open group live in the URL,
so a view can be shared and coming back from event details returns to it.

## Access

Insights is cross-endpoint, so it requires the site **Reader** role. The endpoint filter doesn't
lower that requirement.

## API

`GET /api/metrics/failed-insights`, `GET /api/metrics/failed-insights/occurrences` and
`GET /api/metrics/failed-insights/patterns`; see
[WebApp REST API](webapp-rest-api.md#failed-insights) for parameters and the `NimBus:Insights`
limits.
