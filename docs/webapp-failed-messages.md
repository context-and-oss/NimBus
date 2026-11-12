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
  - **By error** — the Insights grouping (error category, then normalized pattern) over
    every matching failure. Expand a category into its patterns and a pattern into its
    failures; Resubmit or Skip a whole category, a pattern or one failure. Up to 5,000
    failures are grouped; the view says so when a filter matches more.

The endpoint page's **Failed** tile opens this page filtered to that endpoint. Filters live in
the URL, so a view can be shared or bookmarked.

## Access

The page covers the endpoints you hold **Reader** on. Naming an endpoint you cannot read in the
filter is refused (403) and audited. Every search and error grouping writes a `SearchEvents`
audit row. Resubmit and Skip keep their own Contributor checks.

## API

`POST /api/v1/failed/search`, `POST /api/v1/failed/histogram` and `POST /api/v1/failed/error-groups` —
see [WebApp REST API](webapp-rest-api.md#failed). They are backed by
`IMessageTrackingStore.GetFailedEventsAcrossEndpoints` and `GetFailedEventHistogram`, which
every storage provider implements (see [Storage providers](storage-providers.md)).
