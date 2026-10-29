# Delete dead-lettered and the dead-letter queue

Date: 2026-10-28. Scope: [#286](https://github.com/context-and-oss/NimBus/issues/286), part of the
Operations cleanup gap analysis ([#292](https://github.com/context-and-oss/NimBus/issues/292)).
Branch `fix/delete-deadlettered-drains-dlq`.

## Problem

A stored event with status `DeadLettered` stands for a message the endpoint dead-lettered on its
own subscription (`MessageHandler` dead-letters it and notifies the Resolver). Operations →
Delete dead-lettered deleted those rows and nothing else, so every message stayed in
`<endpoint>/<endpoint>/$deadletterqueue`. Service Bus never empties a dead-letter queue on its
own, and the messages count toward the topic's size quota. Nothing in the WebApp could drain an
endpoint subscription's dead-letter queue: Purge drains the main queue only, and the Resolver
dead-letter view can list and replay but not delete.

## Decisions

1. **Delete dead-lettered removes the rows' own messages, matched on `EventId`.** Every NimBus
   message carries its event id as the `EventId` application property, and dead-lettering keeps
   it. A queue message that matches no stored event is left alone. It may be one NimBus never
   showed as dead-lettered, such as one Service Bus dead-lettered after too many deliveries, and
   removing it silently under this name would hide it.
2. **A separate action removes everything else.** Admin → Subscriptions gains **Purge dead
   letters**. It empties a subscription's dead-letter queue and transfer dead-letter queue, and is
   audited as a `ManageSubscription` action like Purge.
3. **Neither action makes a paused subscription receivable.** Purge does, but here it would hand
   the paused backlog to the endpoint. If Service Bus refuses the receive, the result says so.
4. **A row is deleted only when its message is verifiably gone.** The matched scan holds
   unmatched messages without renewing their locks (30 seconds on endpoint subscriptions), so on
   a large queue they come back before the scan ends. The scan therefore allows up to twice the
   number of deliveries in an initial peek snapshot (at most 100,000), holding returned messages
   again. This bounds
   the pass even when locks keep expiring. An empty receive or exhausted budget is not proof that
   all matches were reached, so a final peek finds matching messages still queued and keeps their
   rows for a rerun. If the queue can't be read at all, every row is kept.
5. **Counts survive a failure.** The drain loops report each removal as it happens, so a result
   and its audit record say what was removed before a later completion failed.
6. **The preview shows what will happen**: the stored events, their messages in the queue (by
   peeking), the queue total (runtime properties), and a pointer to Purge dead letters for the
   rest.

## API and compatibility

- `GET /api/admin/endpoint/{id}/deadlettered-preview` gains the optional `queueCount` and
  `matchingQueueCount` fields. Additive.
- `POST /api/admin/servicebus/topics/{topic}/subscriptions/{sub}/deadletters/purge` is new. It
  gets the Admin rate-limit policy through `RateLimitPoliciesConvention`.
- `IAdminService` gains `GetDeadLetterQueueCountsAsync`, and `ISubscriptionAdminService` gains
  `PurgeDeadLetterQueueAsync`. Both are WebApp service interfaces with no implementations outside
  the WebApp and its tests.
- `POST /api/admin/endpoint/{id}/delete-deadlettered` is now audited as `PurgeMessages`, like
  Delete all events.

## Tests

- `AdminDeleteDeadLetteredTests`:
  - The matched delete leaves unmatched messages queued and released.
  - With no rows, Service Bus isn't touched.
  - If the queue can't be read, the rows are kept.
  - Partial counts are reported.
  - A matching message behind expired locks is still reached.
  - Repeatedly expired unmatched locks end the scan without deleting still-queued rows.
  - Rows whose messages are still queued after the scan are kept.
  - Preview counts, and both audit records.
- `SubscriptionAdminServiceTests`: Purge dead letters drains both queues, reports partial counts,
  leaves a paused subscription paused, and refuses an unknown subscription.
- `DeadLetterQueueFakes`: a fake dead-letter queue with lock semantics: receive skips locked
  messages, locks can be expired mid-scan, a receive can come back empty early, and batches can be
  capped.
- Vitest: the Delete dead-lettered card's preview and confirmation, and the Purge dead letters
  button, dialog and feedback in the subscription manager.

## Out of scope

- `nb container delete` (default status `deadlettered`) and Operations → Delete by status with
  DeadLettered selected still delete rows only.
- Filtering a dead-letter purge by event type, time window or reason belongs to the recall
  operation ([#290](https://github.com/context-and-oss/NimBus/issues/290)).
- Purge and Purge dead letters still end their drain at the first empty receive. If Service Bus
  returns one early, messages are left behind, but the reported count stays accurate and no row
  is touched.

## Verification

```bash
dotnet build src/NimBus.slnx -c Release
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
```

Not covered: a run against a live Service Bus namespace. In particular, whether Service Bus
refuses dead-letter receives on a `ReceiveDisabled` subscription is unverified; both actions
report such a refusal rather than work around it.
