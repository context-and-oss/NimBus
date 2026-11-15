# Filter pills on Messages and Endpoint details

## Context

The Failed page filters with Application Insights-style pills (`components/failed-messages/failed-filter-pills.tsx`): a time pill, field pills that open small popovers, a (+) for rarer filters, one search box for IDs, and a "Reset filters" link. The Messages page (`message-filter-bar.tsx`, a 10-field grid with Search/Reset) and the Endpoint details Messages tab (`endpoint-details/filter/event-filtering.tsx` with Search/Reset/Failed buttons, a Hide reported checkbox and an Advanced filters popover with chips) use older form layouts. A mockup of both pages in the Failed page's style was approved for implementation. Outcome: both pages filter with the same pills, applied on change (no Search button), URL-driven as today.

Branch `feat/webapp-filter-pills` from `master`.

## 1. Shared pill kit — new `src/components/ui/filter-pills.tsx`

Move from `failed-filter-pills.tsx` (unchanged behavior) and export:
- `PillPopover`, `ChevronDown`, `pillClass`, `summary(values, none)`, `MultiSelectContent`, the add-filter funnel icon (`AddFilterIcon`), `ResetFiltersButton`.
- New `SingleSelectContent` (list of options, selected bold `text-primary-700`, applies on click and closes) — same markup as the Failed "View as" list.
- New `TimePill` — label prefix (e.g. "Enqueued", "Updated"), value `{ period, from, to }`, presets **Any time** + `PERIOD_OPTIONS` (reuse `periodLabel`), and **Custom…** opening `CustomRangeForm`. Pill text: `Enqueued: Any time` / `Last 7 days` / `dd/MM/yyyy HH:mm – …`; also renders open-ended ranges from old URLs (`From …`, `Until …`). Active (tinted) when not Any time.
- New `IdSearchBox` generalising `FailedSearchBox`: props `prefixes` (e.g. `{event, message, session}`), placeholder, `onSubmit`. Parses `prefix:value`; an unprefixed token is an event ID. Pure parse/format helpers go in `src/functions/filter-pills.functions.ts` (`parseIdQuery`, `idQueryOf`, `presetWindow(period, now)`), unit-tested.

`failed-filter-pills.tsx` imports these instead of defining them (Failed page unchanged; its tests must stay green).

`CustomRangeForm` gets an optional `validate` prop (default `customRangeError`) so message/event search isn't capped at the Failed chart's 90 days; those callers pass a validator that only checks start < end.

## 2. Messages page

`components/messages/message-filter-bar.tsx` → rewrite as `MessageFilterPills` (same file, default export renamed; `MessageFilterValues`, `EMPTY_MESSAGE_FILTER`, `toMessageSearchFilter` stay exported).
- `MessageFilterValues`: add `enqueuedPeriod: ""`; `eventTypeId` becomes `string[]` (API `MessageSearchFilter.eventTypeId` is already an array; old `?eventTypeId=X` URLs still parse via `getAll`).
- `toMessageSearchFilter(v, now = new Date())`: a preset sets `enqueuedAtFrom = now - span` (no upper bound); custom keeps `enqueuedFrom/To`.
- Pills, in order: `Enqueued: …` (TimePill) · `Message type = …` (SingleSelectContent, 2-column, "Any type" + `MessageSearchFilterMessageType` labelled by key) · `Endpoint = …` (single select; API takes one) · `Event type = …` (MultiSelectContent) · removable `From = X ×` / `To = X ×` when set · (+) with From (publisher)/To (subscriber) selects (reuse the Failed `AddFilterContent` shape) · `Reset filters` when anything is set.
- `IdSearchBox` with `event: message: session:`; Enter applies and bumps the page's `searchNonce` (keeps a manual refresh).
- `pages/messages-list.tsx`: render the pills + search box in place of the bar, each change → `applyFilters({...applied, ...patch})`. Rows-per-page select stays where it is.

## 3. Endpoint details, Messages tab

New `components/endpoint-details/filter/endpoint-filter-pills.tsx`, used by `events-panel.tsx` in place of `EventFiltering`, `AdvancedFiltersPopover` and `AdvancedFilterChips`.
- `EndpointFilterParams`: add `updatedPeriod: ""`; existing `updatedFrom/To`, `addedFrom/To`, `payload`, `status` (incl. the `*` sentinel), `eventTypeId`, `eventId`, `sessionId`, `maxResults` keep their URL names; `buildEventFilterFromParams` maps a preset to `updateAtFrom`.
- Pills: `Updated: …` (TimePill) · `Status = …` (checkbox list of every `ResolutionStatus` with a colour dot and, where `getEndpointStatusCountId` gives one, a count; header links **Unresolved** = `FAILED_STATUS_SET`, replacing the Failed button, and **All**) · `Event type = …` (MultiSelectContent) · removable pills when set: `Added: <range> ×`, `Payload contains <text> ×`, `Reported = Hidden ×` · (+) popover: Added range (from/to datetime-local), Payload contains, Hide reported checkbox, Apply · `Reset filters`.
- `IdSearchBox` with `event: session:`.
- Status summary line keeps its current behavior (failed → Failed page; deferred/pending narrow the list). Right side: rows select (50/100/250/500, moved from the old bar) and `ColumnChooser`; the Hide reported checkbox and Advanced filters button go (now in pills).
- `hideReported` stays local state, as today.
- Remove the now-unused `FilterContext` draft plumbing in `events-panel.tsx` and delete `filter/event-filtering.tsx`, `eventType-filtering.tsx`, `status-filtering.tsx`, `eventId-filtering.tsx`, `session-filtering.tsx`, `filtering-context.tsx`, `advanced-filters-popover.tsx` (+ its test). Keep `advanced-filters.ts` only for what the new pills reuse; otherwise delete it with its test. (Internal SPA modules, not public API.)

## Tests (RED first)

- `functions/filter-pills.functions.test.ts`: `parseIdQuery`/`idQueryOf` round-trip, bare token → event ID, quoted values, `presetWindow`.
- `components/ui/filter-pills.test.tsx`: TimePill preset/custom/any, open-ended label; SingleSelectContent applies and closes.
- `pages/messages-list.test.tsx`: choosing a message type / event types / From pill calls `postMessagesSearch` with the matching filter; search box `session:abc` sets `sessionId`; old `?eventTypeId=X&enqueuedFrom=…` URL still filters.
- `events-panel.paint.test.tsx`: Status "Unresolved" applies the failed set; Payload pill × clears `payload`; Updated preset sends `updateAtFrom`; existing summary-line, payload-URL and id-click tests stay green.
- `failed-filter-pills.test.tsx` unchanged and green.

## Verification

- In `ClientApp`: `npm run test:ci`, `npm run lint` (if present), `npm run build` (tsc + Vite).
- `dotnet build src/NimBus.slnx -c Release` to confirm the SPA build inside the WebApp still passes.
- Run the WebApp (`dotnet run --project src/NimBus.AppHost`, or the CrmErpDemo stack) and check both pages in Chrome: each pill filters, URLs round-trip with Back, Reset clears, phone width wraps. Take screenshots for the PR (AGENTS.md: WebApp layout change → screenshot in PR description).
- Open a PR with Conventional Commit title `feat(webapp): filter pills on Messages and Endpoint details`.
