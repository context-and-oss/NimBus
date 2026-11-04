# Spec 042: Recall wrongly published messages

Status: proposed (2026-11-04). Issue: context-and-oss/NimBus#290, part of the Operations cleanup
gap analysis (#292). #291 (erase one event's data everywhere) builds on the selection defined here.

## Problem

An integration publishes messages it shouldn't have: event type X, from publisher P, between T1
and T2. Every endpoint that consumes X from P received its own copy. An operator has to:

1. stop P from publishing more;
2. remove the copies no subscriber has processed yet, wherever they wait;
3. skip the copies that failed, so their sessions continue;
4. list the copies that were already processed, so the data can be corrected in the target
   systems.

Operations has no way to do steps 2–4 across endpoints:

- **Purge subscription** drains one subscription by state and "enqueued before". It has no
  event-type, sender or lower-bound filter, so it removes good messages along with bad ones.
- **Skip messages** works on one endpoint, by status and "updated before".
- **Endpoint → Events** filters by event type, sender and enqueued window, but it works one
  endpoint and one page (100 rows) at a time, and it can't remove anything from Service Bus.

At volume (#292 scenario 2: a replayed full sync of about 50,000 messages) none of this is usable.

## Decisions

Taken with the user on 2026-11-04.

| Topic | Decision |
|---|---|
| Rows of removed copies | **Deleted**, as Purge subscription does since #305. The pre-run export (see Preview) keeps a record of what was recalled. |
| Operations group | **Cleanup**. |
| Payload filter | **Not in v1.** |
| Delivery-count safety limit | **Overridable per run** within a hard floor (see Session ordering). |

## Goals

- Select copies by **event type(s) + sender + enqueued window**, optionally narrowed by a session
  id prefix.
- Show a preview: per subscribing endpoint, where the matching copies are and what will happen to
  each.
- Remove the unprocessed copies from Service Bus: the endpoint subscription, the `Deferred`
  subscription and the dead-letter queue. Delete their stored rows.
- Skip the failed copies through the existing operator command, so their sessions are unblocked
  by the endpoint itself.
- Export the copies that were already processed, as CSV.
- Keep session ordering intact. A recalled copy must never be processed because it was replayed
  after a block was released.
- Scale to tens of thousands of copies without one long HTTP request.

## Non-goals

- Payload-content matching (#292 scenario 4, a "payload marker"), on stored rows or on Service Bus
  copies. It can come later as an extra filter.
- Erasing data: deleting rows, message history and audits. That is #291, which reuses this
  selection.
- Undoing processed copies. NimBus can't reverse what a handler did; it lists those copies.
- A CLI or MCP counterpart. Both can follow once the service exists.

## How copies are found

### Which endpoints

A publish from P reaches a consumer through a forwarder subscription on P's topic. That
subscription forwards to the consumer's topic with a rule per event type
(`TopologyDescriptor.ConsumerForwarderSubscription`). It also may be a dynamic forward
(`AgentDyn-<consumer>`, spec 022). The forward action sets `From = P`, `To = <consumer>` and a
**new `EventId` per copy** (`ForwardAction`), so copies on different endpoints share no event id.

The consuming endpoints are therefore read from P's topic on the bus, not only from the compiled
platform. They are the forwarder subscriptions with a rule whose filter is `ForwardFilter(X)`, and
their `ForwardTo`. Reading the bus also covers dynamic forwards. The compiled platform's consumers
of X are added too, so a forwarder that was deleted still shows up, with a warning.

### Which copies, per endpoint

A copy is matched in two ways, and both are needed:

1. **Store rows:** `GetEventsByFilter` with `EndPointId = E`, `EventTypeId = [X…]`,
   `From = P`, `EnqueuedAtFrom = T1`, `EnqueuedAtTo = T2` (and `SessionId` prefix). `From` and
   `To` have provider-specific *substring* semantics, so results are re-checked for an exact
   `From` and `EventTypeId` before use (`Billing` must not match `BillingV2`).
2. **Service Bus messages** match when either:
   - the `EventId` application property is in the set from (1) for that endpoint (this catches
     retries, resubmissions and parked copies, whose `From` may differ); or
   - `EventTypeId` ∈ X, `From` = P, `MessageType` = `EventRequest`, and the enqueued time is in
     [T1, T2]. This catches copies the Resolver hasn't recorded yet, because rows lag the bus.

Parked copies keep `EventId`, `EventTypeId`, `From` and `SessionId`
(`ResponseService.SendToDeferredSubscription`), so both rules apply on the `Deferred`
subscription as well.

Both bounds of the window are required. A window whose end is in the future is refused while P can
still send: the preview offers the kill switch instead (see Preview).

## What happens to each copy

| Where the copy is | Stored status | Action |
|---|---|---|
| Queued on `<E>/<E>` | Pending | Remove the message. Delete the row. |
| Parked on `<E>/Deferred` | Deferred | Remove the message, then fix the session state (below). Delete the row. |
| `<E>/<E>/$deadletterqueue` | DeadLettered | Remove the message. Skip the row through the operator command. |
| Not on the bus: the handler failed | Failed, Unsupported | Skip through the operator command. |
| Handed off (`PendingSubStatus = Handoff`) | Pending | **Leave it.** An external job owns it; list it under "needs manual action". |
| Processed | Completed | List and export. |
| Already Skipped | Skipped | Nothing. Counted. |
| Message on the bus with no row | — | Remove the message. Counted as "unrecorded". |

The P side is scanned too: a forward that failed leaves the message in
`<P>/<consumer>/$deadletterqueue`. Matching messages there are removed.

## Session ordering

The endpoint enforces ordering per session: a failed event blocks the session, later events are
parked on the `Deferred` subscription, and a Skip of the blocking event replays them
(`StrictMessageHandler.HandleSkipRequest` → `ContinueWithAnyDeferredMessages`). Two rules follow:

1. **Remove parked copies before releasing a block.** If the blocking copy were skipped first, the
   replay would deliver the other recalled copies parked behind it to the handler.
2. **Keep the session state consistent with what is left.** Removing parked messages behind the
   endpoint's back leaves `DeferredCount` too high. The recall corrects it under the session
   lock, the same way Operations → Session state (#312) changes state.

Per affected session on endpoint E, in this order:

1. Accept the session on `<E>/<E>`. This pauses the endpoint for that session only. If a receiver
   holds it, the session is reported as *busy* and retried in a later batch. Nothing in it is
   touched.
2. Accept the session on `<E>/Deferred`. If the DeferredProcessor holds it (a replay is running),
   release both locks and report *busy*.
3. Peek the parked messages. Receive and complete the matching ones, up to the last match.
4. Peek the queued messages on `<E>/<E>`. Receive and complete the matching ones, up to the last
   match.
5. Rewrite the session state under the lock:
   - `DeferredCount -= removedParked`, floored at 0;
   - if nothing is left on the `Deferred` subscription for the session, call
     `SessionState.MarkAllReplayed()`, which keeps the block.
6. Release both locks.
7. If the session is blocked by a copy this recall selected, send the operator Skip for it
   (`IOperatorCommands.SkipAsync`). The endpoint unblocks the session and replays what is still
   parked, which by now is only messages the recall didn't select. A block by an event the recall
   didn't select is left alone and listed.
8. Delete the stored rows of the removed copies.

**Receiving is not selective.** Service Bus can't receive an active message by sequence number.
To reach a match, the recall receives everything before it in the session, completes the matches
and abandons the rest, which costs each abandoned message one delivery. Two mitigations:

- The recall stops at the last match, so messages after it aren't touched.
- Before receiving, the peek shows each message's delivery count. A session is *at risk* when any
  message that would be abandoned has a delivery count at or above the run's **at-risk
  threshold**. An at-risk session isn't touched; it's reported for the operator to handle with
  Session state or Session purge.

The at-risk threshold defaults to `ServiceBusMaxDeliveryCount - 2` (8, with the limit of 10).
The operator can change it per run, between 1 and `ServiceBusMaxDeliveryCount - 1`.

- The threshold can't go above `ServiceBusMaxDeliveryCount - 1`. A message one delivery from the
  limit therefore always makes its session at risk, whatever the override. Abandoning that
  message would dead-letter it, which would block its session on a good event.
- The override is recorded in the run's audit record.

The preview shows how many non-matching messages the run will touch, and how many sessions are at
risk at the chosen threshold.

## Stored rows

The rows of removed copies are **deleted**, as Purge subscription does since #305: Pending and
Deferred rows of copies the recall removed from the bus. Two kinds of rows are kept:

- Rows of copies skipped through the operator command (Failed, DeadLettered, Unsupported) become
  Skipped through the normal Resolver path. They stay, because the Skip is what released their
  session.
- Handoff and Completed rows are left alone.

Deleted rows disappear from Endpoint → Events and from the event's history. The record of what
was recalled is therefore:

- the **pre-run export** (see Preview), which the card asks for before the run;
- the audit records, which carry the selection and the counts.

Erasing the message history and audits as well is #291.

**A late Resolver write can re-create a deleted row.** The Resolver records a copy from its own
fan-out subscription, so its Pending write can land after the recall deleted the row. The
stale-write guard (spec 030) can't stop this, because there is no row left to compare against.
Purge subscription has the same exposure.

The recall closes it with a **final pass**. After the last batch it re-runs the selection on each
endpoint. It deletes every selected Pending or Deferred row whose copy is no longer on the bus,
unless that session was reported busy. With P's sending disabled and the window closed, every
such row is a leftover of the recall.

Deleting needs no new store member. `RemoveMessage(eventId, sessionId, endpointId)` exists on
every provider.

## Preview

`POST /api/admin/recall/preview` takes the selection and returns, per consuming endpoint:

- counts by location: queued, parked, dead-lettered, unrecorded;
- counts by stored status: Pending, Deferred, Failed, DeadLettered, Unsupported, Handoff,
  Completed, Skipped;
- the sessions it will touch, how many it will unblock, how many are blocked by an event outside
  the recall, and how many are *at risk*;
- the number of non-matching messages that would be received and abandoned;
- how many sessions are at risk at the chosen at-risk threshold.

The preview also offers the **pre-run export**: every stored row the selection matches, in every
status. Since the run deletes rows, this is the operator's record of what was recalled.

Warnings:

- P's topic is send-enabled. The preview offers the existing kill switch (Operations → Endpoints,
  send disable) and refuses a window that ends in the future until sending is disabled.
- A consumer of X in the compiled platform has no forwarder on the bus.
- The P-side forwarder dead-letter queues hold matching messages.

Counting queued and parked copies means peeking the subscriptions, which is bounded. The preview
peeks up to 100,000 messages per subscription. If a subscription holds more, its counts are
marked as estimates.

## Execution

A recall runs as **client-driven batches**, like Reconcile stale pending (spec 032).

- `POST /api/admin/recall/execute` takes the selection, an endpoint, an optional `recallId` and
  continuation token, and a batch size (default 50 sessions). It processes that many sessions and
  returns counts, busy and at-risk sessions, errors and the next continuation token.
- The first call (no `recallId`) creates the recall id and writes the audit record. If that
  record can't be written, the call is refused.
- The request also carries the at-risk threshold (default `ServiceBusMaxDeliveryCount - 2`,
  allowed 1 to `ServiceBusMaxDeliveryCount - 1`).
- Every batch re-runs the selection, so a reload, a retry or a second operator can't
  double-process. A copy that is already removed no longer matches, a deleted row is gone, and a
  row that is already Skipped is counted, not touched.
- The card loops through the endpoints and batches, shows progress, and can be stopped between
  batches.
- A **final pass** per endpoint retries busy sessions. It also deletes leftover rows that a late
  Resolver write re-created (see Stored rows).

`GET /api/admin/recall/export?…selection…&statuses=…` streams the matching stored rows as CSV:

- With no statuses it exports every status; this is the pre-run export.
- After the run, `statuses=Completed,Pending` exports the processed copies and the Handoff rows,
  which need correcting in the target systems.
- Columns: endpoint, event id, session, status, correlation id, enqueued, updated, and a WebApp
  link.
- A payload column is included only for callers with `PiiReader`.

## Audit

- A new `MessageAuditType.RecallMessages`, appended last (Cosmos persists the numeric value).
  It's added to both api-spec audit enums and to `AuditSettingsProvider.AlwaysRecorded`.
- One record per recall. It's written before the first batch, and carries the selection and the
  recall id.
- The run record also carries the at-risk threshold, when it differs from the default.
- One record per completed endpoint, with its counts, including rows deleted. Deleted event ids
  aren't listed, because there can be tens of thousands; the pre-run export is that list.
- A denied attempt is recorded as access denied.
- Payloads are never written to the audit.

## UI

Operations gets **Recall messages** in the *Cleanup* group. It is Site Owner only, like every
Operations action.

1. **Selection:** event types (from the platform, multi-select), publisher, enqueued from/to
   (both required, UTC shown), optional session prefix. An advanced at-risk threshold field shows
   the default.
2. **Preview:** a table per endpoint with the counts above, the warnings, and a link to
   Endpoint → Events pre-filtered with the same selection. A *Download recalled copies* button
   gives the pre-run export. The Run button stays disabled until the export has been downloaded
   or explicitly skipped.
3. **Run:** typed confirmation of the publisher id, then a progress bar per endpoint, then the
   result with busy, at-risk and blocked-by-other sessions listed, each linked to Operations →
   Session state.
4. **Export:** the processed-copies CSV.

The run deletes rows and removes messages, so the card warns that it can't be undone.

## Testing

- **Unit, over the in-memory store and a session-aware fake bus** (as in #312):
  - exact `From` and `EventTypeId` re-checks;
  - both match rules;
  - a parked copy is removed before the Skip of its blocking copy is sent;
  - session state after removal: `DeferredCount` corrected, and `MarkAllReplayed` only when
    nothing is parked;
  - Handoff rows are left alone;
  - the rows of removed copies are deleted, and the rows of skipped copies become Skipped;
  - at-risk sessions aren't touched, the threshold override is honoured, and it can't go above
    `ServiceBusMaxDeliveryCount - 1`;
  - the final pass deletes a Pending row re-created after its copy was removed;
  - idempotent re-runs;
  - a refused audit runs nothing.
- **Emulator** (`[TestCategory("Emulator")]`, so CI runs them), on the simulation fixture:
  - P publishes good and bad events to two consumers;
  - a bad event fails on one session, with good and bad events parked behind it;
  - the recall removes the bad copies, unblocks the session, and only the good events are
    handled, in order;
  - the processed bad copies appear in the export.
- **API:** authorization, validation (both window bounds, future end while send-enabled), and the
  audit records.
- **Vitest:** the selection form, the preview rendering, the batch loop, stop and resume, and the
  export link.
