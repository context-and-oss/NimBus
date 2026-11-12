# Reconciling stale Pending rows

Operator reference for the **Reconcile Stale Pending** card on the NimBus WebApp's
**Operations** page. It answers one question: *this row says Pending, but did the endpoint
already answer?*

Before 3.7.0 every status write was last-writer-wins. A copy of a request that arrived after its
own outcome — delayed by topic auto-forward lag, or re-sent by `ScheduleRedelivery` — replaced the
settled row with `Pending`. The message history still holds the `ResolutionResponse` the endpoint
sent; only the one-row-per-event projection is wrong. Spec 030's stale-write guard stops new rows
going wrong. This card repairs the ones that already did.

The reconcile **re-applies a decision the Resolver already recorded**. It never resubmits, never
skips, never writes to history, and never invents a status: the only terminal it can produce is a
`ResolutionResponse` already stored for that event and session.

Access is site **Owner**, same as the rest of `/api/v1/admin`. Every repair is audited as
`ReconcileStalePending` — one audit row per run (on both the denied and the successful branch) and
one per repaired event, the latter carrying the previous status, the stale message, the response it
was repaired from, and your note.

## The procedure

1. Pick the endpoint and a **cut-off**. Only rows whose request was enqueued *before* the cut-off
   are considered. The default is an hour ago. Preview accepts any cut-off; the *repair* endpoint
   refuses one newer than 15 minutes, so a preview you cannot act on is still a preview you can read.
2. **Preview.** Nothing is written. Three tiles summarise the scan (candidates, repairable, rows
   that need a human) and the table gives one verdict per row with the messages and times it rests
   on. The preview classifies at most 500 candidates (2 000 with an explicit `maxRows`); when it
   stops short the card says so and the repair button reads **Repair all repairable rows**.
3. **Download CSV** if you want the list in the incident record. The preview is not stored
   anywhere.
4. **Repair N rows** (or **Repair all repairable rows**) — red, and it asks you to type the
   endpoint id. Only rows the rule called `Repairable` are touched, the preview is recomputed
   server-side (the client's list is never trusted), and any row written within the last 15
   minutes is skipped even then. The repair is not bound by the preview's page: the server walks
   every Pending row before the cut-off and stops only when it holds `maxRepairs` repairable ones,
   so operator-decision rows ahead in the scan never hide repairable rows behind them. The card
   sends `maxRepairs: 500` and asks again while a round comes back full, one audit entry per
   round; a full round that repaired nothing ends the loop.
5. The preview re-runs afterwards. A second run repairs nothing: the rows it fixed are no longer
   Pending.

A repair is conditional on the row still being Pending with the same last message id. If something
else settled the row between your preview and the repair, that row is reported as **skipped**, not
failed — re-preview and look again.

## Verdicts

Only `Repairable` is ever repaired automatically. Everything else is a decision for a person.

| Verdict | Meaning | What to do |
| --- | --- | --- |
| `Repairable` | A clean `ResolutionResponse` from the endpoint precedes the request copy that wrote the row, and nothing but request copies follow it. | Repair it. This is the incident signature. |
| `HistoryMissing` | Nothing is stored for the event: the history aged out or was purged. | Nothing this tool can do. Decide from the adapter's own logs, then skip or resubmit by hand. |
| `NoTerminal` | No terminal response from this endpoint in the row's session. In a fan-out, another subscriber's answer does not count. | Leave it. The event is genuinely in flight, or it never reached the endpoint. |
| `LatestTerminalIsError` | The latest terminal is an `ErrorResponse`. | Operator decision: resubmit or skip. A repair would hide a real failure. |
| `LatestTerminalIsSkip` | The latest terminal is a `SkipResponse`. | The row should read `Skipped`, not `Completed`. Skip it from the event page. |
| `LatestTerminalIsDeadLettered` | The `ResolutionResponse` carries a dead-letter description (a replayed dead-lettered copy). The Resolver projects such a message as `DeadLettered`, never `Completed`. | Handle it as a dead letter. |
| `ResponseNotBeforeRow` | The response is not older than the row's request. | Leave it: the row reflects a newer attempt that is still running. |
| `LaterControlMessage` | A control request or another non-request message followed the response. | Look at that message. A row written by a control copy (`SkipRequest`, `HandoffCompletedRequest`, …) always lands here by design — they are listed so you can see them, never repaired. |
| `LaterRequestCopy` | A request copy newer than the row is stored but not projected onto it yet. | Wait. Repairing now would only be undone when that copy is processed. |

## Cautions

- **Control-written rows are never repaired.** A row whose `MessageType` is `SkipRequest`,
  `ResubmissionRequest`, `HandoffCompletedRequest` or `HandoffFailedRequest` is listed for
  visibility but can never come out `Repairable` — its own control copy always follows the
  response, so it lands on `LaterControlMessage` (or earlier on the ladder: a row written by a
  `SkipRequest` whose `SkipResponse` came back reads `LatestTerminalIsSkip`, and one with no
  terminal at all reads `NoTerminal`). Settle those through the normal resubmit/skip path.
- **Rows written by a late `RetryRequest` or `ContinuationRequest` copy are not listed at all.**
  Spec 032 pins the candidate types to the five in Spec 030 §9 step 1, and those two are not among
  them. They would only ever classify `LaterControlMessage`, so nothing is repaired that should not
  be — but you get no verdict for them either. Diagnose those from the Flow tab.
- **Re-preview after any dead-letter replay.** Replaying a pre-3.7.0 copy out of the DLQ changes
  the history the verdict was computed from.
- **On Cosmos DB a Completed row expires 30 days after it is written**, repaired or not — the
  same TTL every terminal row gets. Repair inside that window or the row (and its verdict) is gone.
  SQL Server rows do not expire.
- **A row whose history was purged cannot be repaired**, only decided by hand: the outcome the
  repair would re-apply no longer exists.
- **Parked handoffs are not candidates.** A row with `PendingSubStatus = Handoff` is genuinely
  waiting for an external system; settle it from the handoff surface.

## Related

- `docs/spec/032-stale-pending-reconcile/spec.md` — the rule, the store primitive and the API.
- `docs/spec/030-stale-pending-guard/spec.md` — why the rows went wrong, and the guard that stops
  new ones.
- `docs/message-flows.md` — which messages may reopen a settled row.
- `docs/storage-providers.md` — the conditional terminal write each provider implements.
- `docs/service-bus-subscription-admin.md` — per-subscription backlog incident response.
