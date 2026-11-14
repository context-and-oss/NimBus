# Deletes keep the events that block a session

Date: 2026-10-29. Scope: [#288](https://github.com/context-and-oss/NimBus/issues/288).
Branch `fix/delete-blocking-event-strands-session`.

## Problem

Three operations delete a stored event without touching Service Bus:
- Operations → Delete by status
- Operations → Delete single event
- `nb container delete` and `nb container event delete`

When the event is the one blocking its session, the session state still names it as
`BlockedByEventId`. Every later message in the session is then deferred behind an event the UI no
longer shows, and Skip or Resubmit on the message can't release the session, because both look up
the row first.

Correction to #288: the session isn't left without a WebApp recovery. **Reprocess** on any Deferred
row (Endpoint → Events, `ReprocessDeferredAsync`) clears the block and replays the deferred
messages. The gap is that nothing stops the delete paths from stranding a session in the first
place.

## What blocks a session

`StrictMessageHandler` blocks a session in two places only:
- a handler failure, which sends an `ErrorResponse` (the row is `Failed`);
- a pending hand-off, which sends a `PendingHandoffResponse` (the row is `Pending` with
  `PendingSubStatus = "Handoff"`).

Unsupported and dead-lettered messages don't block it.

## Decision (agreed 2026-10-29)

The delete paths **keep** a row that blocks its session and say so. The alternative, skipping it
instead, was rejected: it would make a delete release the session and run the parked messages
downstream, a side effect a delete never had. Releasing a session stays the job of Skip, or of
Reprocess for one already stranded.

1. **The rule:** `BlockedEventRules.BlocksItsSession(UnresolvedEvent)` in
   `NimBus.MessageStore.Abstractions`, shared by the WebApp and the CLI.
2. **Delete by status** leaves such rows, counts them as failed with one summary error pointing to
   Skip messages, and deletes the rest.
   - Its preview's `count` is now the rows it will delete.
   - The new optional `CountResponse.blockingCount` reports the rows it will keep.
   - The card lists Skipped as a status, so skip-then-delete works from the UI.
3. **Delete single event** refuses such a row with 409 (`EventBlocksSessionException`), and the
   card says to skip it first.
4. **`nb container delete` and `nb container event delete`** leave such rows and print why.

## Compatibility

- `CountResponse.blockingCount` is additive.
- `DELETE /api/admin/endpoint/{id}/event/{eventId}` gains a 409.
- `IAdminService` gains `CountSessionBlockingEventsAsync`.
- `DeleteByStatusPreviewAsync` keeps its signature, but now excludes the rows the delete keeps.

## Tests

- **WebApp:**
  - Delete by status keeps Failed and Pending-hand-off rows and deletes the rest.
  - The preview counts both kinds.
  - Delete single event throws for a blocking row and still deletes any other row.
  - The controller answers 409.
- **CLI:** both deletes keep a blocking row and remove the rest.
- **Vitest:** the Delete by status note and the Skipped option, and the single-delete card's 409
  message.

## Follow-up (2026-10-31): Event Details → Delete

The first pass missed the Event Details Delete button. Its route,
`DELETE /api/event/invalid/{endpointId}/{eventId}/{sessionId}`, called `RemoveMessage` directly, and
the button showed for Failed events. Now:

- The route refuses a row that blocks its session with 409, using the same text as
  `EventBlocksSessionException`. The API spec documents the 404 and 409.
- The page doesn't offer Delete for a Failed event. Dead-lettered and Unsupported events keep it.
- A refused or failed delete says why in a toast instead of only changing the button to
  "Delete failed".

Not covered: a message-version check on that route. A Delete sent after the event has moved on to
a newer, non-blocking attempt still removes it.

## Out of scope

- Releasing a session already stranded: Reprocess on a Deferred row already does it.
- Deleting Deferred rows. A Deferred row doesn't block its session; its parked copy replays once
  the block clears, and the Resolver records it again.
