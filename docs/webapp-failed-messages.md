# Failed messages page (WebApp)

The **Failed** page (`/Failed`, under OBSERVE in the sidebar) is one place to find and act on
every unresolved failure — events currently `Failed`, `DeadLettered` or `Unsupported` — across
all the endpoints you can read. The sidebar badge shows the size of that backlog.

## What the page shows

- **Range** — `1h`, `12h`, `1d` (default), `3d`, `7d` or `30d`. When failures older than the
  range exist, the page says how many and offers to widen it.
- **Summary tiles** — totals per status in the range, the number of endpoints affected and
  the busiest bucket.
- **Failures over time** — a stacked bar chart of failures per bucket (5 minutes for `1h` up
  to a day for `30d`), split by status or by endpoint. A failure counts in the bucket of the
  time it **last** entered its status (`UpdatedAt`), so the chart and the list always describe
  the same events. Click a bar to narrow the list to that window; click it again, or the chip,
  to clear it. The Metrics page's failure series is message *history* instead, and includes
  failures that were since resolved.
- **Filters** — endpoint, event type, event ID, last message ID, session ID, publisher (From),
  subscriber (To), error text (case-insensitive substring) and status toggles.
- **Views**
  - **List** — the endpoint page's columns plus **Endpoint** and the last error, with the
    "N blocked" chip for failures holding deferred messages in their session. Resubmit and
    Skip per row or for the selection. Columns can be shown or hidden.
  - **By endpoint** — counts per endpoint and status; **Show failures** narrows the list.
  - **By error** — error category, then normalized pattern, over every matching failure.
    Expand a category into its patterns and a pattern into its failures; Resubmit or Skip a
    whole category, a pattern or one failure. Up to 5,000 failures are grouped; the view says
    so when a filter matches more.
  - **By cause**, **By guidance** and **By origin** — the same failures grouped by their
    saved classification; see [Grouping by saved classification](#grouping-by-saved-classification).
    These three views appear only while [failure intelligence](integration-intelligence.md)
    is on.

The endpoint page's **Failed** tile opens this page filtered to that endpoint. Filters live in
the URL, so a view can be shared or bookmarked.

## Grouping by saved classification

When [failure intelligence](integration-intelligence.md) is on, agents record a classification
for a failed attempt: a likely cause, recovery guidance and a fault origin. The Failed page
groups unresolved failures by each of them:

- **By cause** — the saved category, such as *Transient dependency* or *Missing reference
  data*.
- **By guidance** — the guidance saved with the result, such as *Retry may help* or *Change
  likely required*. Guidance describes the failure when it was analyzed. It is not a current
  retry instruction and is never recomputed with today's thresholds; the confirmation for
  resubmitting or skipping several failures from these views says so.
- **By origin** — where the fault lies, such as *Source data* or *Target system*. A result
  recorded before fault origins existed groups under *Origin not assessed*.

Each group expands to its error patterns and then to its failures, and every level can be
resubmitted or skipped as a whole, as in **By error**. A failure row shows its saved cause and
guidance, and opening it shows the Failure intelligence card with the rationale.

A failure is matched to the classification of its **latest** failed attempt. An event that was
resubmitted and failed again is *Not analyzed* until the new attempt is classified.

**Coverage** is shown above the groups and on each group and pattern, also in **By error**:

| State | Meaning |
|---|---|
| Classified | A known cause and confident guidance. |
| Uncertain | The result is inconclusive or unrecognized, or was saved for another event. |
| Not analyzed | No saved result for the latest failed attempt. |
| Not eligible | The endpoint is outside the analysis allow-list, or the event is `Unsupported` (no handler ran, so there is no failure to analyze). |

When saved classifications can't be read, the three views show error groups and say why: the
classification storage can't be read, or the filter matches more failures than the enrichment
limit. When classifications can be read but agents can't record new ones (read-only mode), the
view says so, and *Not analyzed* failures stay that way until an Owner fixes the settings.

The views show unresolved failures only. Failed attempts of events that have since completed
or been skipped are not listed; the Metrics page charts those. The former **Insights** page
(`/Insights`), which counted every failed attempt, was folded into these views, and its links
redirect here.

## Access

The page covers the endpoints you hold **Reader** on. Saved classifications are read only for
failures on those endpoints. Naming an endpoint you cannot read in the
filter is refused (403) and audited. Every search and error grouping writes a `SearchEvents`
audit row. Resubmit and Skip keep their own Contributor checks.

## API

`POST /api/v1/failed/search`, `POST /api/v1/failed/histogram` and `POST /api/v1/failed/error-groups` —
see [WebApp REST API](webapp-rest-api.md#failed). They are backed by
`IMessageTrackingStore.GetFailedEventsAcrossEndpoints` and `GetFailedEventHistogram`, which
every storage provider implements (see [Storage providers](storage-providers.md)).
