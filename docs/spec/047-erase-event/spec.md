# Spec 047: Erase event

Status: proposed (2026-11-13). Issue: context-and-oss/NimBus#291, part of the Operations cleanup
gap analysis (#292). Builds on Spec 042 (Recall wrongly published messages, #290) and stays
consistent with #298 (keep the event that blocks its session, merged as 39843a06).

Convention: anything marked **(new)** is proposed by this spec and does not exist on master.
Everything else cited (files, types, methods, enum values, API paths, CLI commands) exists on
master as of `d162c683`.

## Problem

Sometimes an event goes out that never should have: a payload containing personal data or a
secret. The operator then has to remove every copy of that event from everywhere NimBus keeps it.
NimBus has no single operation for this. Copies sit in several places, and the operations that
exist each cover only part of them:

- **Operations → Delete single event** (`DELETE /api/v1/admin/endpoint/{endpointId}/event/{eventId}`)
  calls `IMessageTrackingStore.RemoveMessage`, which is a *soft* delete. SQL Server sets
  `UnresolvedEvents.Deleted = 1` and keeps `MessageContentJson` forever. Cosmos DB sets
  `deleted = true` with a 60-second TTL. Message history and audits are untouched.
- Resubmit and Skip archive the row (`TryArchiveUnresolvedEvent`, `ArchiveFailedEvent`). The
  archived row keeps its payload: on SQL Server indefinitely, on Cosmos DB for its 30-day TTL.
- **Delete all events** (`PurgeStoredMessages`) removes message history for a whole endpoint, but
  keeps audits.
- Nothing removes a Service Bus copy of one event, and nothing touches audit `Data`.

The goal is removal. When an erasure reports **Completed**, no payload copy remains in any place
this spec lists. When it cannot remove a copy, it says so and reports **Incomplete**. It never
reports an event as erased while a copy remains.

## Settled ambiguities

| Topic | Decision |
|---|---|
| Spec number | **047.** On master the `docs/spec/` folders run to 045 (`045-routing-keys`); 041 is taken by the open PR #275 (`docs/spec/041-on-prem-rabbitmq-transport`), and 042–045 are on master. 046 is free on master but a `046-mcp-guided-actions` draft is in progress elsewhere, so this spec takes 047 to avoid a collision. 047 is free on master and on every open PR when this PR is opened. |
| "auditData" | The issue's `auditData` is a local variable name in WebApp controllers (for example `PostResubmitWithChangesEventIdsAsync` in `EventImplementation.OperatorActions.cs`). The stored field is **`MessageAuditEntity.Data`**, and this spec uses that name. `MessageAuditEntity.Comment` is a second free-text field and is covered too. |
| Dependency on #290 | Spec 042 (`docs/spec/042-message-recall/spec.md`) is on master and defines the selection (event types + sender + enqueued window + session prefix). It is not implemented yet. Erase **does not wait** for it: phases 1–5 take explicit ids only. Phase 6 adds the Recall selection as a *picker* that resolves into ids, and is gated on Spec 042's selection code landing. Erase does not define its own filter. |
| SearchEvents filter `Data` | **Out of scope.** See [Audit type inventory](#audit-type-inventory). |
| Erasure boundary | Message store, Service Bus, the operator operation journal and the Integration Intelligence classification store are inside. The SDK outbox (`IOutbox`) and the other stores in [Erasure boundary](#erasure-boundary) are outside, and the preview and result name them. |

## Terms

- **Event copy.** One tracked event on one endpoint, identified by the pair
  `(endpointId, eventId)`. A forwarder rule gives every fan-out copy a fresh `EventId`
  (`TopologyDescriptor.ForwardAction`: `SET user.EventId = newid()`), so the copies of one publish
  on different endpoints share no event id. They do share the broker `MessageId` of the
  `EventRequest`.
- **Place.** A kind of storage location (a table or container, or a Service Bus entity) listed in
  [Places](#decision-2-places-touched).
- **Copy.** One item in one place that carries payload bytes of an event copy: a row, a document,
  an audit entry or a Service Bus message.
- **Outstanding copy.** A copy that execution neither removed nor confirmed absent.

## Decision 1: Input

**Chosen: explicit event copies, as a list of `{ endpointId, eventId }`, at most 100 per request.**
The preview can widen the list in two ways, and both resolve into explicit pairs before anything
is erased:

1. **Fan-out siblings.** For each listed copy the preview reads its `EventRequest` messages
   (`GetEventHistory(eventId)`) and looks up other endpoints' messages with the same broker
   `MessageId` and `EventTypeId` (`SearchMessages` with a `MessageId` prefix, re-checked for an
   exact match). It offers them as checkboxes, preselected. The operator decides; nothing is
   added silently.
2. **Recall selection (phase 6).** A Spec 042 selection is run through 042's store-row match and
   its results are offered as pairs, capped at the batch size.

**Freezing the set.** `POST …/erase/preview` returns a `selectionHash` (new): the lowercase hex
SHA-256 of the sorted, de-duplicated, case-normalised list of `endpointId/eventId` pairs.
`POST …/erase/execute` takes the same list plus the `selectionHash`. The server recomputes the
hash; a mismatch refuses the request (`SelectionChanged`). Execution therefore covers exactly the
event copies the operator previewed. The *places* are re-read at execution time: a copy that
appeared after the preview (a retry, a late Resolver write) is found and erased, because erasing
it is the point.

**Batch size.** 100 event copies per request. Execution per copy touches up to 11 Service Bus
entities and five stores, and needs session locks (Decision 3), so 100 keeps a request within the
WebApp's request timeout. Larger erasures are run as several requests from the same card.

Rejected:

- **Bare event ids without the endpoint.** Tracking rows are per endpoint (Cosmos DB keeps them
  in the endpoint's container), and the store has no cross-endpoint lookup by event id. The pair
  is the key every existing store method takes.
- **A live filter evaluated at execution time.** The set could differ from the one previewed (new
  matches, an edited window), and erasure cannot be undone.
- **Defining Erase's own filter.** It would duplicate Spec 042's selection and drift from it.
- **Automatic sibling expansion.** `MessageId` is a content hash for SDK publishes, so two
  separate publishes with identical content share it. Their payload is identical, but the operator
  may not want to erase the other event's tracking, so the expansion is offered, not forced.
- **Batches above 100.** Each copy may need three session locks and several entity scans. Larger
  batches would need client-driven continuation (as Spec 042 has), which buys nothing at the
  volumes an erasure has (a handful of leaked events).

## Decision 2: Places touched

Every listed place gets exactly one treatment. **Delete** means the item is removed from storage
(hard delete, no soft-delete flag, no TTL wait). **Redact** means the item stays, and every field
that can hold payload bytes is overwritten in storage. Masking in the UI or API (for example the
`[Sensitive]` masking for non-PiiReaders) is not redaction and does not count.

| # | Place | Where | Treatment | Why |
|---|---|---|---|---|
| P1 | **Tracking rows**, every status: Pending (incl. hand-off), Deferred, Failed, DeadLettered, Unsupported, Completed, Skipped, **including archived and soft-deleted rows** | SQL `UnresolvedEvents` (any `Deleted` value); Cosmos DB the endpoint's container (any `deleted` value); in-memory store | **Delete** | The row's purpose is live state. An erased event has no state an operator acts on, and the erasure's own audit record (Decision 7) keeps the identifiers. Archived rows are included because on master they keep `MessageContentJson`. The one exception, a row that blocks its session, is not erased at all (Decision 5). |
| P2 | **Event report marker** | SQL `EventReports`; Cosmos DB event-reports container | **Delete** | It holds no payload (flag and ticket id), but it is keyed by `(endpointId, eventId)` and would be an orphan. Deleting it keeps the event fully gone. |
| P3 | **Message history**: every `MessageEntity` with the event id | SQL `Messages`; Cosmos DB messages container (partition `eventId`); in-memory | **Delete** | Each entry carries `MessageContent` (`EventContent.EventJson`, or `ErrorContent` whose `ErrorText`, `ExceptionStackTrace`, `DeadLetterErrorDescription` can quote payload). An entry stripped of content has no value in the Flow tab; the erasure audit keeps the count. |
| P4 | **Audit entries tied to the event** whose type is on the [payload list](#audit-type-inventory), plus every entry's `Comment` | SQL `MessageAudits`; Cosmos DB audits container (partition `eventId`); in-memory | **Redact** | The audit trail is the record of who did what. Deleting the rows would erase operator accountability and change `GetResubmitCounts`. See the field list below. |
| P5 | **Compose audit entries** whose `Data` holds the event's payload | Same as P4, but stored with an empty event id (`AuditLogService` passes `eventId ?? string.Empty`; `PostComposeNewEventAsync` passes none) | **Redact** | Same as P4. Matched by content, see below. |
| P6 | **Failure classifications** for the event (when Integration Intelligence is registered) | SQL `dbo.FailureClassifications` (`StateJson`); Cosmos DB classification container (`CosmosClassificationStore`) | **Delete** | Found by this spec: `FailureClassification.Rationale` is agent free text, and when `EventPayloadIncluded` is true it was derived from the payload. A classification of an erased event has no use. |
| P7 | **Endpoint subscription** `<E>/<E>` (`TopologyDescriptor.EndpointSubscription`): active and scheduled messages | Service Bus | **Delete** | Queued copies, retries and continuation requests carry the content. |
| P8 | **Deferred subscription** `<E>/Deferred` (`TopologyDescriptor.DeferredSubscription`) | Service Bus | **Delete** | Parked copies keep `EventId`, `SessionId` and content (`ResponseService.SendToDeferredSubscription`). |
| P9 | **Dead-letter queues** `<E>/<E>/$deadletterqueue` and `<E>/Deferred/$deadletterqueue` | Service Bus | **Delete** | Dead-lettered copies keep their body. |
| P10 | **Resolver entities** (found by this spec): `<E>/Resolver` forwarder DLQ (`ResolverFanoutSubscription`), `Resolver/Resolver` (`ResolverSubscription`) and its DLQ | Service Bus | **Delete** | The fan-out rule `to-<E>` forwards a copy of every message addressed to the endpoint to the Resolver, payload included, and the Resolver writes P1 and P3 from it. A copy left there re-creates the rows after erasure. |
| P11 | **Producer-side forwarder DLQ** (found by this spec): `<P>/<E>/$deadletterqueue` (`ConsumerForwarderSubscription`, `DynamicForwardSubscription`) | Service Bus | **Delete** | A forward that failed leaves the original publish there. It carries the publisher's event id, not the copy's, so it is matched by broker `MessageId` + `EventTypeId` + `SessionId` from the copy's `EventRequest`. |
| P12 | **Operator operation journal** (found by this spec, Spec 044 phase 2): every `OperatorOperation` with the copy's `EndpointId` and `EventId`, when an `IOperatorOperationStore` is registered | SQL `SqlServerOperatorOperationStore`; Cosmos DB `CosmosDbOperatorOperationStore`; in-memory `InMemoryOperatorOperationStore` | **Redact** | `OperatorOperation.Reason` is the operator's or agent's free text about the event, which can quote the payload, and `Detail` can carry refusal or error text. The rest of the row is ids, hashes and status. Deleting it would reset the per-day and per-message workload budgets (`McpAccessSettings.WorkloadResubmitsPerDay`, `WorkloadResubmitsPerMessage`) that count journaled operations, and the row expires anyway (`ExpiresAtUtc`). A **non-terminal** operation (`IsTerminal` false) is not redacted: `OperatorOperationReconciler` may still restore or confirm its row. It is outstanding with reason `OperationInFlight`, and a rerun after the operation settles redacts it. |

### Redaction fields (P4, P5)

Cleared or replaced in storage:

- `MessageAuditEntity.Data` is replaced with `{"erased":true,"erasureId":"<id>"}`.
- `MessageAuditEntity.Comment` is set to null.

Kept, so the audit trail and session history stay consistent: `AuditorName`, `AuditTimestamp`,
`AuditType`, `AccessDenied`, `EventId`, `EndpointId`, the stored `EventTypeId`, the document or row
id and `CreatedAt`, and the CloudEvents attributes (`CloudEventId`, `CloudEventSource`,
`CloudEventType`, `CloudEventSubject`), which are envelope identifiers rather than payload.

### Redaction fields (P12)

Cleared or replaced in storage: `OperatorOperation.Reason` is replaced with `[erased <erasureId>]`
and `OperatorOperation.Detail` is set to null.

Kept: `OperationId`, `TenantId`, `Caller`, `ClientId`, `IsWorkload`, `ActorName`, `IdempotencyKey`,
`ArgumentsHash`, `Action`, `EndpointId`, `EventId`, `SessionId`, `SourceMessageId`,
`MessageVersion`, `Status`, `ObservedStatus`, `EvidenceMessageId`, the timestamps and
`ExpiresAtUtc`/`TimeToLiveSeconds`. `ArgumentsHash` is a SHA-256 over the arguments, not the
arguments, so it keeps no payload bytes. Because the hash is kept, a retry with the old
idempotency key still finds the stored, now redacted operation (`TryCreateOperatorOperation`
returns the existing one) and does not run the command again.

**Compose matching (P5).** A Compose entry is a copy of the erased payload when its `EndpointId`
is the event's producer (`UnresolvedEvent.From`, or the history `From`), its `EventTypeId` matches,
and the `EventContent` inside its `Data` equals the event's `EventJson`. Equality is JSON deep
equality after parsing. If `Data` was truncated (`AuditLogService.TruncationSuffix`, 4 KB limit),
it matches when the untruncated part is a prefix of the serialised `{EventTypeId, EventContent}`
body built from the stored payload. Because a matching entry contains the erased payload by
definition, redacting it is correct even if it came from a different publish. The rule lives in
one shared helper, `ErasureRules.ComposeDataMatches` **(new)** in
`NimBus.MessageStore.Abstractions`, next to `BlockedEventRules`, so every provider applies it the
same way.

### Places checked and left off the list

- `<E>/DeferredProcessor` (`DeferredProcessorSubscription`): holds `ProcessDeferredRequest`
  messages, built with an empty `MessageContent` (`ResponseService.SendProcessDeferredRequest`).
- `<E>Reply` (`ReplySubscription`): carries replies to requests, not copies of an event.
- Inbox records (`IInboxStore`, `CosmosInboxStore`): keyed by `(endpointId, messageId)`, no
  content.
- Service Bus session state (`SessionState` in `src/NimBus.ServiceBus/SessionState.cs`): holds
  ids and counters such as `BlockedByEventId` and `DeferredCount`, no content. Execution corrects
  the counters (Decision 3).
- Event schemas (`EventSchema`): type definitions, not instances.

Rejected treatments:

- **Leave in place with a reason.** Not allowed for a listed place: it keeps the bytes the
  operator is erasing.
- **Soft delete (`RemoveMessage`) for P1.** It keeps the payload (SQL forever, Cosmos for 60 s
  to 30 days).
- **Redact P1 instead of deleting.** A payload-free row keeps showing up in searches and counts as
  an event nobody can act on. Deleting it matches Spec 042 and Purge subscription (#305).
- **Delete P4 audit rows.** It removes operator accountability and the resubmit counts.
- **Redact P3 instead of deleting.** Hollow history entries would mislead the Flow tab, and
  Skip/Resubmit of the event is no longer possible anyway.

## Decision 3: Outcome model

An erasure request runs **preconditions**, then **execution**. Execution starts only when every
precondition passes.

### Preconditions

Checked in this order; the first failure refuses the request.

| Code (new) | Fails when |
|---|---|
| `Forbidden` | The caller is not a site Owner (Decision 6). Recorded as access denied. |
| `InvalidRequest` | The list is empty, has a blank id or a duplicate pair, or names an endpoint the platform doesn't know. |
| `BatchTooLarge` | More than 100 pairs. |
| `SelectionChanged` | The `selectionHash` doesn't match the list. |
| `UnknownEvent` | A pair is unknown (rule below). The result names every unknown pair. |
| `ConfirmationMismatch` | The typed confirmation doesn't match (Decision 6). |
| `AuditUnavailable` | The erasure's *started* audit record (Decision 7) cannot be written. |

**Known event copy.** A pair `(E, id)` is known when at least one of these holds:

1. a tracking row exists for it on `E` in any status, archived or soft-deleted included;
2. message history exists for `id` with `EndpointId = E`;
3. an `EraseEvent` **(new)** audit record exists for `id` with `EndpointId = E`. Execution never
   deletes or redacts `EraseEvent` records, so an id erased by an earlier run, fully or partly,
   stays known and a rerun executes instead of being refused.

A failed precondition alone decides Refused. Nothing is checked, deleted or redacted, apart from
the access-denied audit row for `Forbidden`.

### Execution

Per event copy, in this order. The order removes the Service Bus sources before the stored rows,
so the Resolver can't re-create a row after it was deleted.

1. **Session-safety check** (Decision 5). A blocking copy is not executed further; all its places
   are reported outstanding with reason `BlocksSession`.
2. **Lock the session.** Accept the copy's session on `<E>/<E>`, `<E>/Deferred` and
   `Resolver/Resolver` (all session-enabled; the Resolver's messages carry the event's session id,
   `ResponseService`). While these locks are held no handler, DeferredProcessor or Resolver can
   process that session, so no new copy or row is written for it. A lock that can't be taken
   within 5 s makes every place of the copy outstanding with reason `SessionBusy`, and nothing else
   is done for it. Locking `Resolver/Resolver` pauses resolver writes for that session id on every
   endpoint for the duration, which is seconds.
3. **Service Bus search and removal** (P7–P11), defined below.
4. **Session state.** If parked copies were removed from `<E>/Deferred`, rewrite the session state
   under the lock as Spec 042 does: `DeferredCount -= removed`, floored at 0, and
   `SessionState.MarkAllReplayed()` when nothing is left parked for the session. The block is
   never cleared here.
5. **Store erasure** (P1–P6, P12): one call to `EraseEventCopyAsync` **(new)** per copy
   (Decision 4), one to the classification store and one to the operator operation journal.
6. **Verify.** Call `EraseEventCopyAsync` again. Every place must now report `AlreadyAbsent` (or
   `Redacted` for an entry redacted in step 5). Anything else is outstanding with reason
   `ReappearedDuringErasure`.
7. Release the locks.

### Service Bus search

A Service Bus copy matches when its `EventId` application property equals the erased id, or, on
P11, when `MessageId`, `EventTypeId` and `SessionId` equal those of the copy's `EventRequest`.

- **Session entities** (P7, P8, `Resolver/Resolver`): with the session lock held, peek the whole
  session from the first sequence number, including scheduled messages. A scheduled match is
  cancelled (`ServiceBusSender.CancelScheduledMessageAsync` on the topic). An active match is
  removed by receiving up to the last match, completing the matches and abandoning the rest, as
  Spec 042 does, with the same at-risk rule: if a message that would be abandoned has a delivery
  count at or above `ServiceBusMaxDeliveryCount - 2`, the copy is not received and is outstanding
  with reason `SessionAtRisk`.
- **Non-session entities** (P9, P10 DLQs, P11): peek the whole entity, up to 100,000 messages. A
  match is received in peek-lock and completed; non-matches received on the way are abandoned
  (dead-letter queues don't apply `MaxDeliveryCount`).
- **Confirmed absent** means the search covered the whole session or entity and found no match. A
  search that hit the 100,000 bound, timed out, or failed on the entity makes the copy outstanding
  with reason `SearchIncomplete`, because its absence is not confirmed.
- **Locked or in flight.** A match visible to peek but not receivable (held under another
  receiver's lock, or completed by someone else between peek and receive) is re-peeked once. If it
  is still there it is outstanding with reason `MessageLocked`; if it is gone it counts as
  `AlreadyAbsent`. Execution never waits for a lock to expire and never dead-letters a copy.
- **Deferred or dead-lettered and not receivable now.** A parked copy whose `<E>/Deferred` session
  can't be locked is `SessionBusy`. A dead-lettered copy that can't be received is
  `MessageLocked`. Both are outstanding, the event's other places are still erased (except the
  `SessionBusy` case, where none are, step 2), and a rerun picks them up.
- **Entity missing.** An entity that doesn't exist (for example no forwarder for `<P>/<E>`) counts
  as `AlreadyAbsent` for that place, because nothing can be stored in it.

### Per-place results and aggregation

Each `(event copy, place)` ends with one state: `Removed`, `Redacted`, `AlreadyAbsent` or
`Outstanding` (with a reason). The request's outcome:

1. If any precondition failed: **Refused**, naming the failed precondition.
2. Otherwise, if any `(copy, place)` is `Outstanding`: **Incomplete**, listing every outstanding
   copy by event id, endpoint, place and reason.
3. Otherwise: **Completed**.

Whether a delete or redact was attempted plays no part. An execution where every removal attempt
failed is **Incomplete**, not Refused: its preconditions passed. A rerun where every place reports
`AlreadyAbsent` is **Completed**: its preconditions passed (the ids are known through rule 3) and
nothing is outstanding. The three outcomes cover every request and never overlap.

### Idempotency and retry

Every step re-reads current state, so a rerun on the same pairs is safe. Copies removed earlier
report `AlreadyAbsent`, redacted entries report `AlreadyAbsent` for P4/P5 (their `Data` no longer
matches), and outstanding copies are either finished or reported outstanding again.

**Retry is operator-driven.** The result card offers *Retry outstanding*, which reruns the
erasure on the copies that had an outstanding place. Rejected: **automatic background retry.** The
common causes (a busy session, a blocking event, an at-risk session) need an operator decision,
and a background job that keeps locking sessions and receiving messages would interfere with
normal processing without anyone watching.

## Decision 4: Store contract

One new member on `IMessageTrackingStore` **(new)**:

```csharp
/// <summary>
/// Erases one event copy from this store: hard-deletes its tracking row (every status, archived
/// and soft-deleted included) unless the row blocks its session, deletes its event report marker
/// and every message-history entry for the event id on the endpoint, and redacts the
/// payload-carrying audit entries for the event id and the Compose audit entries that hold
/// <paramref name="payloadJson"/>. Never touches <see cref="MessageAuditType.EraseEvent"/>
/// records. Idempotent: a call that finds nothing left reports every place as AlreadyAbsent.
/// A failed write is reported as Outstanding for its place, never thrown, so the caller can
/// aggregate; cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
Task<EventErasureResult> EraseEventCopyAsync(
    EventErasureRequest request,
    CancellationToken cancellationToken = default);
```

New types in `NimBus.MessageStore.Abstractions` **(new)**:

```csharp
public sealed record EventErasureRequest(
    string EndpointId,
    string EventId,
    string ErasureId,
    string? ProducerEndpointId,   // for Compose matching (P5); null skips it
    string? EventTypeId,
    string? PayloadJson);         // the EventJson to match Compose entries against

public enum ErasurePlace { TrackingRow, EventReport, MessageHistory, EventAudits, ComposeAudits }

public enum ErasurePlaceState { Removed, Redacted, AlreadyAbsent, Outstanding }

public sealed record ErasurePlaceResult(
    ErasurePlace Place,
    ErasurePlaceState State,
    int Count,                    // items removed or redacted; 0 for AlreadyAbsent
    string? OutstandingReason);   // BlocksSession, WriteFailed; null unless Outstanding

public sealed record EventErasureResult(
    string EndpointId,
    string EventId,
    IReadOnlyList<ErasurePlaceResult> Places);  // exactly one entry per ErasurePlace
```

The interface has **no default implementation**: an unforwarded default member silently bypasses
the real store (AGENTS.md), and a default that throws would let external providers compile and
fail at runtime. External providers get a compile error, which the release notes call out.
`InstrumentingMessageTrackingStoreDecorator` (`src/NimBus.OpenTelemetry/Instrumentation/`)
forwards it explicitly with an activity named after the method, and
`tests/NimBus.OpenTelemetry.Tests/StoreDecoratorTests.cs` covers it.

### Behaviour

Shared by every provider (pinned by the conformance suite):

- **Blocking row.** If the tracking row satisfies `BlockedEventRules.BlocksItsSession`, the call
  changes nothing and returns every place as `Outstanding` with reason `BlocksSession`. This is the
  store-level guard; the service checks first (Decision 5).
- **Nothing left.** Every place reports `AlreadyAbsent` with `Count = 0`.
- **Partial failure.** Each place is written independently. A write that throws a provider error
  (after the provider's own transient retry) makes that place `Outstanding` with reason
  `WriteFailed`; the other places still run. If every write fails, every place is `Outstanding`
  and the service reports Incomplete. Only cancellation and argument errors throw.
- **Order.** Audits, then history, then the report marker, then the tracking row last, so a failure
  part-way leaves the row in place and the event stays visible and known.

Per provider:

- **SQL Server.** One transaction per place, not one for the whole call: audit redaction can touch
  many rows, and a single transaction would hold locks across tables. `DELETE FROM UnresolvedEvents
  WHERE EndpointId = @E AND EventId = @V` (no `Deleted` filter), `DELETE FROM Messages WHERE
  EventId = @V AND EndpointId = @E`, `DELETE FROM EventReports …`, and `UPDATE MessageAudits SET
  Data = @Marker, Comment = NULL WHERE EventId = @V AND AuditType IN (@PayloadTypes) …` plus
  `Comment = NULL` on the event's other rows. Compose rows are read by `(EndpointId, EventTypeId,
  AuditType = 'Compose')` through `IX_MessageAudits_Endpoint_EventType_Created`, matched with
  `ErasureRules.ComposeDataMatches` in process, and updated by `Id`. Calls go through the
  transient-retry shim (`SqlConnectionTransientExtensions`).
- **Cosmos DB.** No cross-container transactions exist. Tracking row: point delete in the
  endpoint's container (read first to apply the blocking rule; deletes the document whatever its
  `deleted` flag). History: query the messages container's `eventId` partition, filter
  `endpointId`, delete each. Audits: query the `eventId` partition and replace each matching
  document under its ETag (412 → re-read once, then `WriteFailed`). Compose: query the empty-id
  partition by `endpointId`, `eventTypeId` and `audit.AuditType`, match in process, replace under
  ETag. A 404 on delete counts as already gone.
- **In-memory.** Same rules over its dictionaries under its existing lock; used by unit tests and
  the conformance suite.

The classification store (P6) gets its own member on `IFailureClassificationStore` **(new)**:
`Task<int> DeleteForEventAsync(string endpointId, string eventId, CancellationToken
cancellationToken = default)`, returning the number removed, implemented by
`SqlClassificationStore`, `CosmosClassificationStore`, `AtomicClassificationStore` (forwarding) and
`UnavailableClassificationStore` (returns 0). The erasure service maps an exception to `Outstanding`
/ `WriteFailed` for place `FailureClassifications`, and 0 to `AlreadyAbsent`.

The operator operation journal (P12) gets its own member on `IOperatorOperationStore` **(new)**:

```csharp
/// <summary>
/// Redacts every terminal operation for the event on the endpoint (Reason replaced with
/// <paramref name="reasonMarker"/>, Detail cleared) under optimistic concurrency, and counts the
/// non-terminal ones it left alone. Idempotent: an already redacted operation is not counted.
/// </summary>
Task<OperatorOperationRedaction> RedactOperatorOperationsForEventAsync(
    string endpointId,
    string eventId,
    string reasonMarker,
    CancellationToken cancellationToken = default);

public sealed record OperatorOperationRedaction(int Redacted, int InFlight);   // new
```

Implemented by `SqlServerOperatorOperationStore`, `CosmosDbOperatorOperationStore` and
`InMemoryOperatorOperationStore` (`src/NimBus.Testing/Conformance/`), with no default
implementation, for the same reason as above. When no journal is registered the place is
`AlreadyAbsent`, because nothing can be stored there. The service maps `Redacted > 0` to
`Redacted`, `InFlight > 0` to `Outstanding` / `OperationInFlight`, an exception to `Outstanding` /
`WriteFailed`, and `(0, 0)` to `AlreadyAbsent`. A version conflict on one operation is re-read once,
then counted as `InFlight`, so the copy stays outstanding and a rerun finishes it. New tests in
`OperatorOperationStoreConformanceTests` (`src/NimBus.Testing/Conformance/`):
`RedactForEvent_RedactsTerminalOperations_KeepsIdsAndHash`,
`RedactForEvent_LeavesNonTerminalOperations_CountsThemInFlight`,
`RedactForEvent_OnlyTouchesTheEndpointAndEvent` and `RedactForEvent_SecondCallRedactsNothing`.

### Conformance tests (`NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`)

New tests, run against every provider:

1. `EraseEventCopy_DeletesTrackingRowInEveryStatus` (DataRow per status, Pending without hand-off
   through Skipped).
2. `EraseEventCopy_DeletesArchivedAndSoftDeletedRows` (after `ArchiveFailedEvent` and after
   `RemoveMessage`).
3. `EraseEventCopy_DeletesHistoryOnlyForTheEndpoint` (same event id on another endpoint is kept).
4. `EraseEventCopy_RedactsPayloadAudits_KeepsIdentifiers` (`Data` marker, `Comment` null, auditor,
   timestamp, type, ids unchanged; `GetResubmitCounts` unchanged).
5. `EraseEventCopy_LeavesNonPayloadAuditData` (for example `ReportEvent`, `FailureClassified`).
6. `EraseEventCopy_RedactsMatchingComposeAudit_AndNotOthers` (same type, different content kept;
   truncated match).
7. `EraseEventCopy_NeverTouchesEraseEventRecords`.
8. `EraseEventCopy_RefusesBlockingRow` (Failed, and Pending with `PendingSubStatus = "Handoff"`:
   nothing changes, every place `Outstanding`/`BlocksSession`).
9. `EraseEventCopy_SecondCallReportsAlreadyAbsent`.
10. `EraseEventCopy_UnknownPairReportsAlreadyAbsent`.
11. `EraseEventCopy_DeletesEventReportMarker`.

Failure mapping (`WriteFailed`) is tested per provider with the existing fault seams (the
Cosmos adapter fakes in `NimBus.MessageStore.CosmosDb.Tests`, a failing `DbConnection` in the SQL
tests, a throwing in-memory subclass), not in the conformance suite, which runs on healthy stores.

Rejected contract shapes:

- **`Task<bool>`, like `RemoveMessage`.** It can't tell removed from already absent from
  outstanding, so it can't feed the outcome model.
- **Several members (delete row, delete history, redact audits).** The service would have to
  orchestrate ordering and the blocking guard, and every provider would expose half-erasure
  primitives that other callers could misuse.
- **A batch member taking 100 pairs.** Cosmos DB partitions by event id, so a batch is a loop in
  every provider anyway, and one bad copy would complicate the per-copy result.
- **Throwing on a failed write.** The service would lose the places that did succeed and couldn't
  report them.
- **A default interface implementation.** See above: it would silently bypass the store behind the
  decorator, or fail at runtime instead of compile time.
- **Extending `RemoveMessage` to hard-delete.** It is a soft delete on purpose for the existing
  Delete operations, and changing it alters their behaviour.

## Decision 5: Session safety

**Chosen: per event copy, during execution. A copy whose tracking row blocks its session
(`BlockedEventRules.BlocksItsSession`: Failed, or Pending with a hand-off) is not erased at all.**
All its places are `Outstanding` with reason `BlocksSession`, so the request is **Incomplete** and
that copy is never Completed. The rest of the batch executes.

This is consistent with #298: Operations → Delete keeps such an event and the single delete
refuses it with 409 (`EventBlocksSessionException`). The endpoint's session state names the event
in `SessionState.BlockedByEventId`; removing the row would leave the session blocked by an event no
one can see, and removing its history would also break Skip, which looks the row's message up
(`IOperatorCommands.FindByMessageAsync`, then `GetMessage(eventId, LastMessageId)`).

The endpoint learns nothing from the erasure, because the erasure changes neither its block nor
its row. Later messages in the session stay parked, as intended, until the operator acts.

**Finishing the erasure.** The result lists the copy with a *Skip it first* link, and the card says:

1. Skip the event (`POST /api/v1/event/skip/{eventId}/{messageId}`, the event's Skip button), or
   settle the hand-off. The endpoint unblocks the session through `StrictMessageHandler.HandleSkipRequest`
   and replays what is parked.
2. If other parked events in that session are also to be erased, erase them **before** step 1, so
   the replay doesn't deliver them (Spec 042's rule). The preview flags this: a selected Deferred
   copy whose session is blocked by another selected copy.
3. Run *Retry outstanding*. The row is now Skipped (archived), no longer blocks, and is erased.

Rejected:

- **A precondition that refuses the whole batch.** One blocking event would stop the erasure of
  every other event, which is usually most of the batch.
- **Skip automatically, then erase.** Skip releases the session and replays parked events: a
  processing side effect the operator didn't ask for, and it could deliver other leaked events
  parked behind it.
- **Redact the blocking row in place.** Skip and Resubmit still read the history entry that holds
  the content, so either the history keeps the payload or Skip breaks.
- **Delete it and clear the block.** It silently changes processing order on the endpoint, which
  #298 rejected.

## Decision 6: Surfaces

### API (in `src/NimBus.WebApp/api-spec.yaml`, tag `Admin`)

`POST /api/v1/admin/erase/preview` **(new)**, operationId `post-admin-erase-preview`.

```yaml
EraseEventCopy:            # new
  type: object
  required: [endpointId, eventId]
  properties:
    endpointId: { type: string }
    eventId: { type: string }
ErasePreviewRequest:       # new
  type: object
  required: [copies]
  properties:
    copies: { type: array, maxItems: 100, items: { $ref: '#/components/schemas/EraseEventCopy' } }
ErasePreviewResponse:      # new
  type: object
  required: [selectionHash, copies, siblings, notCovered, confirmationText]
  properties:
    selectionHash: { type: string }
    confirmationText: { type: string }      # what the operator must type
    copies:
      type: array
      items:
        type: object
        required: [endpointId, eventId, known, places]
        properties:
          endpointId: { type: string }
          eventId: { type: string }
          known: { type: boolean }
          status: { type: string, nullable: true }        # tracking-row status, or null
          sessionId: { type: string, nullable: true }
          blocksSession: { type: boolean }
          previouslyErased: { type: boolean }             # an EraseEvent record exists
          places:
            type: array
            items:
              type: object
              required: [place, count]
              properties:
                place: { $ref: '#/components/schemas/ErasurePlace' }
                count: { type: integer }                  # items found
                estimate: { type: boolean }               # Service Bus bound reached
    siblings: { type: array, items: { $ref: '#/components/schemas/EraseEventCopy' } }
    warnings: { type: array, items: { type: string } }
    notCovered: { type: array, items: { type: string } }  # stores outside the boundary
```

`POST /api/v1/admin/erase/execute` **(new)**, operationId `post-admin-erase-execute`.

```yaml
EraseExecuteRequest:       # new
  type: object
  required: [copies, selectionHash, confirmation]
  properties:
    copies: { type: array, maxItems: 100, items: { $ref: '#/components/schemas/EraseEventCopy' } }
    selectionHash: { type: string }
    confirmation: { type: string }
    previousErasureId: { type: string, nullable: true }   # set by "Retry outstanding"
EraseResult:               # new; returned with 200 for every outcome, Refused included
  type: object
  required: [erasureId, outcome, copies, outstanding, notCovered]
  properties:
    erasureId: { type: string, nullable: true }   # null when refused before the audit record
    outcome: { type: string, enum: [Completed, Incomplete, Refused] }
    refusal:
      type: object
      nullable: true
      properties:
        precondition: { type: string, enum: [Forbidden, InvalidRequest, BatchTooLarge, SelectionChanged, UnknownEvent, ConfirmationMismatch, AuditUnavailable] }
        detail: { type: string }
        unknownCopies: { type: array, items: { $ref: '#/components/schemas/EraseEventCopy' } }
    copies:
      type: array
      items:
        type: object
        required: [endpointId, eventId, places]
        properties:
          endpointId: { type: string }
          eventId: { type: string }
          places:
            type: array
            items:
              type: object
              required: [place, state, count]
              properties:
                place: { $ref: '#/components/schemas/ErasurePlace' }
                state: { type: string, enum: [Removed, Redacted, AlreadyAbsent, Outstanding] }
                count: { type: integer }
                reason: { type: string, nullable: true }
    outstanding:
      type: array
      items:
        type: object
        required: [endpointId, eventId, place, reason]
        properties:
          endpointId: { type: string }
          eventId: { type: string }
          place: { $ref: '#/components/schemas/ErasurePlace' }
          entity: { type: string, nullable: true }   # Service Bus entity path
          reason: { type: string, enum: [BlocksSession, SessionBusy, SessionAtRisk, MessageLocked, SearchIncomplete, WriteFailed, OperationInFlight, ReappearedDuringErasure] }
    notCovered: { type: array, items: { type: string } }
ErasurePlace:              # new
  type: string
  enum: [TrackingRow, EventReport, MessageHistory, EventAudits, ComposeAudits, FailureClassifications,
         OperatorOperations, EndpointSubscription, DeferredSubscription, DeadLetterQueues, ResolverEntities, ProducerForwarderDeadLetter]
```

Every outcome returns 200 with an `EraseResult`, so the client reads one shape. A `Forbidden`
refusal also returns 200 with `outcome: Refused` rather than 403, because the refusal is the
result; the Operations card hides the action for non-Owners anyway. A malformed body that fails
schema binding is a 400 problem response from the framework. `EraseEvent` is added to both audit
enums in the spec.

### WebApp

Operations gets **Erase event** in the *Cleanup* group (where Spec 042 puts Recall).

1. **Input.** Rows of endpoint + event id (paste or add), or *Erase* from an event's details page,
   which pre-fills the pair. Phase 6 adds *From a Recall selection*.
2. **Preview.** A table per copy: status, session, a count per place (Service Bus counts marked
   "≥" when estimated), and flags: *blocks its session* (red, with *Skip it first*),
   *previously erased*, *unknown* (red, execution will be refused). Below it, the sibling
   checkboxes and the warnings. A fixed **Not covered** panel lists every store outside the
   boundary, so the operator isn't told every copy is gone.
3. **Confirmation.** A text field; the Run button stays disabled until it matches exactly:
   `ERASE <n> EVENT COPIES` (for example `ERASE 3 EVENT COPIES`, `ERASE 1 EVENT COPIES`), where
   `<n>` is the number of selected pairs. The server returns it as `confirmationText` and checks it
   again (`ConfirmationMismatch`). The card warns that erasure can't be undone.
4. **Result.** A banner with the outcome: green *Completed*, amber *Incomplete*, red *Refused*
   with the failed precondition. Incomplete lists the outstanding copies in a table (event id,
   endpoint, place, entity, reason, and an action link per reason: *Skip it first* for
   `BlocksSession`, Operations → Session state for `SessionBusy`/`SessionAtRisk`), plus *Retry
   outstanding* and *Download result (JSON)*. The **Not covered** panel is repeated.

### Role

**Site Owner** (`AccessRole.Owner` on the site scope, `IsSiteOwnerAsync` in
`AdminImplementation`), like every Operations action. PiiReader is **not** required: the preview
never shows payload (counts and ids only), and erasing is not reading. Rejected: **Contributor on
the endpoint** (erasure removes audit content and spans endpoints; it is an administrative act),
**Owner + PiiReader** (it would block an Owner from removing leaked data they are not allowed to
read, which is backwards).

### CLI

**No `nb` command in v1.** `nb container event delete` and the other `nb container` commands talk
to Cosmos DB directly with a connection string: they bypass the WebApp's site-Owner check and the
always-recorded audit, can't take Service Bus session locks consistently with the WebApp, and work
on Cosmos DB only. An erasure must run once, in one place, with one audit trail. Rejected: **an
`nb` command calling the WebApp API.** `nb` has no authenticated WebApp API client today; adding
one is a separate feature. When it exists, `nb event erase` can wrap the two endpoints (exit 0
Completed, 2 Incomplete, 3 Refused, 1 usage error).

## Decision 7: Audit

A new `MessageAuditType.EraseEvent` **(new)**, appended last (Cosmos persists the numeric value),
added to `AuditSettingsProvider.AlwaysRecorded` and to both api-spec audit enums. It is written
with `IAuditLogService.LogRequiredAuditAsync`.

Per event copy and per run, two records, each with `EventId` = the copy's event id and `EndpointId`
= its endpoint:

1. **Started**, written before execution as the last precondition. If it can't be written for
   any copy, the request is refused with `AuditUnavailable`. It is what makes the pair known on a
   rerun (Decision 3).
2. **Finished**, written after execution with the result.

`Data` (JSON, never payload):

```json
{
  "stage": "started | finished",
  "erasureId": "<guid>",
  "previousErasureId": "<guid or null>",
  "batchSize": 3,
  "outcome": "Completed | Incomplete",          // finished only: the request's outcome
  "copyOutcome": "Completed | Incomplete",      // finished only: this copy's outcome
  "places": { "TrackingRow": { "state": "Removed", "count": 1 }, "MessageHistory": { "state": "Removed", "count": 4 } },
  "outstanding": [ { "place": "DeadLetterQueues", "entity": "Billing/Billing/$deadletterqueue", "reason": "MessageLocked" } ],
  "eventTypeId": "CustomerUpdated",
  "sessionId": "<session id>"
}
```

It records identifiers, counts per place, the outcome and the outstanding copies. It never records
the payload, a payload hash or the matched Compose content. `AuditorName` is the Owner.

A refused request writes one record with `EventId` empty and `Data` = `{ "outcome": "Refused",
"precondition": "…", "copies": [ { "endpointId", "eventId" } ] }` — except `AuditUnavailable`,
which can't be recorded and is logged. A `Forbidden` refusal is recorded with `AccessDenied` set.

The structured audit log (`AuditLogService` also emits every record to `ILogger`, so to
Application Insights) receives the same `Data`, which contains no payload.

**A rerun writes new records** (`previousErasureId` links them) and never updates the old ones.
Rejected: **updating the earlier record.** Audits are append-only, no store has an audit update
member besides this erasure's redaction, and a history of attempts is what an auditor wants.
Rejected: **one record per run with every event id in it.** Audits are partitioned and searched by
event id; per-copy records make `GetMessageAudits(eventId)` show the erasure on the event and make
rule 3 of "known" a point read.

## Audit type inventory

Every `MessageAuditType` value on master, checked against where it is written. **Payload list**:
the value's `Data` can carry event payload or free text tied to the event; it is redacted (P4,
P5). `Comment` is cleared on every audit row of an erased event whatever its type. On master two
writers set it: the Skip of deferred tracking (`EventImplementation.Deferred.cs`, the operator's
reason, free text) and Reconcile stale pending (`AdminService.Reconcile.cs`, a fixed sentence).

| Type | On payload list | Written in | `Data` on master / reason |
|---|---|---|---|
| Resubmit | **Yes** | `OperatorCommandCoordinator` (`Describe`) | Includes `reason`, operator free text tied to the event, which may quote the payload. |
| ResubmitWithChanges | **Yes** | `EventImplementation.OperatorActions.cs` (`PostResubmitWithChangesEventIdsAsync`, `JsonConvert.SerializeObject(body)`), then `OperatorCommandCoordinator` | The whole request body, including `EventContent` (the edited payload). |
| Skip | **Yes** | `OperatorCommandCoordinator` (`Describe`); `EventImplementation.Deferred.cs` | `reason` free text; the deferred variant's `Comment` is the reason. |
| Retry | No | `ResolverService` | No `Data`. |
| Comment | No | `ResolverService` (stale-write note) | A fixed sentence with the message type and message id. |
| CompleteHandoff | **Yes** | `HandoffSettlementService` (from `EventImplementation.Handoff.cs` and `AgentImplementation`) | The operator note or the agent's `Result`, free-form; an agent result typically echoes the processed data. |
| FailHandoff | **Yes** | Same | The failure reason / agent `ErrorText`, free-form. |
| SearchEvents | No — **out of scope** | `EventImplementation.Search.cs`, `FailedImplementation.cs` | The operator's search filter. It can hold payload fragments typed into the payload filter, but it is not a copy of an event, has no event id (it is written with an empty one), and one filter relates to any number of events. Erasing it would need a text search over every audit row. The preview's Not covered panel names it, and a future "redact audit text" operation can cover it. |
| GetEventDetails | No | `EventImplementation.Queries.cs` | No payload: the access is recorded, not the content. |
| GetEndpointDetails | No | `EndpointImplementation` | Endpoint id only. |
| EnableEndpoint, DisableEndpoint, EnableEndpointSend, DisableEndpointSend | No | `EndpointImplementation` | Endpoint state changes. |
| PurgeMessages | No | `AdminImplementation`, `EndpointImplementation` | Endpoint, subscription, session, counts. |
| Compose | **Yes** (P5) | `EventImplementation.OperatorActions.cs` (`PostComposeNewEventAsync`, `JsonConvert.SerializeObject(body)`) | The whole body, including `EventContent`. Written without an event id, so matched by content. |
| ReportEvent | **Yes** | `OperatorCommandCoordinator` | Includes `reason` free text and the ticket id. |
| GrantRole, RevokeRole | No | `AccessControlImplementation` | Scope, role, entry. |
| ManageSubscription | No | `AdminImplementation` | Topic, subscription, action. |
| UpdateHeartbeatSettings, SendHeartbeatNow, EnableEndpointHeartbeat, DisableEndpointHeartbeat | No | `AdminImplementation` | Heartbeat settings and endpoint ids. |
| DeleteStorageContainer | No | `AdminImplementation` | Container name. |
| ReconcileStalePending | No | `AdminImplementation`, `AdminService.Reconcile.cs` | Counts and ids. |
| FailureClassified | No | `FailureClassificationRecorder`, `FailureClassificationService`, `IntegrationIntelligenceAuditMiddleware`, `OperatorActionTools` | Provider, model, category, guidance, flags, outcome: metadata only. (The classification itself is P6.) |
| UpdateIntelligenceSettings | No | `IntelligenceSettingsController` | Configuration. |
| UpdateSimulationSettings, ControlSimulation, UpdateSimulationConfig | No | `SimulationImplementation` | Simulator configuration and actions. |
| AcknowledgeEndpoint, ClearEndpointAcknowledgement | No | `MonitorImplementation` | Endpoint-level reason, not tied to an event. |
| UpdateAuditSettings | No | `AdminImplementation` | Disabled types. |
| CommandNotSent | No | `OperatorCommandCoordinator`, `OperatorOperationJournal` | Command name and exception type name only. |
| UpdateMcpSettings, McpAccessRefused | No | `McpAccessImplementation`, `McpAccessRefusals`, `McpActivityService` | Policy revisions, tool, client, reason code. |
| AgentApiKeyCreated, AgentApiKeyRevoked, AgentApiKeyRemoved | No | `AgentKeysImplementation` | Key id, agent id, name. |
| CopyEndpointData | No | `AdminImplementation` | Time window, statuses, counts. |
| ChangeSessionState | No | `AdminImplementation.SessionState.cs` | Session id, action, state before/after (ids and counters). |
| DashboardTokenCreated, DashboardTokenRevoked, DashboardTokenRemoved | No | `DashboardTokensImplementation` | Token id, name, expiry. |
| UpdateEndpointMetadata | No | `EndpointImplementation` | Ownership metadata before and after (owner, team, email, contacts, source system, repository and ticket link): endpoint-level, not tied to an event. |
| EraseEvent **(new)** | No | Erase service (new) | Decision 7: never payload. Never redacted by erasure. |

## Erasure boundary

**Inside** (the operation removes copies here and reports on them): the NimBus message store
(tracking rows, event reports, message history, audits) for the configured provider, Service Bus
(P7–P11), the operator operation journal (P12) when one is registered, and the Integration
Intelligence classification store when the extension is registered.

**Outside** (not covered; listed in the preview's and result's *Not covered* panel):

- **SDK outbox** (`IOutbox`, `OutboxMessage.Payload`, `src/NimBus.Core/Outbox/`). It lives in the
  publishing application's own database, which the WebApp has no connection to. Dispatched rows
  stay until that application's `IOutboxCleanup` removes them.
- **Logs and telemetry**: Application Insights / Log Analytics. The audit writer's structured log
  carries `Data` (so past ResubmitWithChanges and Compose payloads), and
  `PostResubmitWithChangesEventIdsAsync` logs the body at Information.
- **Target systems** that handled the event, and external hand-off systems.
- **The LLM provider** that received `FailureClassificationInput.EventPayloadJson`.
- **Copies made by Operations → Copy endpoint data** in another store.
- **Exports** downloaded by operators (for example Spec 042's CSV) and backups / point-in-time
  restore of Cosmos DB and SQL Server.
- **SearchEvents audit entries** (see the inventory).

## Implementation phases

Each phase is one PR.

### Phase 1 — Store contract (Abstractions, three providers, decorator, conformance)

- `EraseEventCopyAsync`, the request/result types, `ErasureRules.ComposeDataMatches`, and
  `MessageAuditType.EraseEvent` (appended last).
- Implemented in Cosmos DB, SQL Server and in-memory; forwarded by
  `InstrumentingMessageTrackingStoreDecorator`.

Acceptance:

1. The 11 conformance tests in Decision 4 exist and pass on in-memory, and on SQL Server and Cosmos
   DB in CI (no skipped conformance tests).
2. A per-provider test shows a failed write maps to `Outstanding`/`WriteFailed` and the other
   places still run, including a test where every write fails.
3. `StoreDecoratorTests` (`tests/NimBus.OpenTelemetry.Tests`) asserts the decorator calls the inner store for the new
   member.
4. `dotnet build src/NimBus.slnx -c Release` has no new CS warnings.

### Phase 2 — Classification store and operator operation journal

- `IFailureClassificationStore.DeleteForEventAsync` in SQL, Cosmos, Atomic and Unavailable stores.
- `IOperatorOperationStore.RedactOperatorOperationsForEventAsync` in the SQL Server, Cosmos DB and
  in-memory journals.

Acceptance:

1. Tests in `NimBus.Extensions.IntegrationIntelligence.Tests` show every revision and operation
   for the event on the endpoint is deleted, another endpoint's are kept, and a second call
   returns 0.
2. The four journal conformance tests in Decision 4 pass on in-memory, and on SQL Server and
   Cosmos DB in CI (no skipped conformance tests).

### Phase 3 — Service Bus search and removal

- An erasure Service Bus component (new) in `NimBus.WebApp` that locks the three sessions, searches
  and removes per Decision 3, cancels scheduled matches, applies the at-risk rule and corrects
  session state. If Spec 042 has landed it reuses 042's session remover; otherwise this phase
  builds it so 042 can reuse it.

Acceptance:

1. Unit tests over a session-aware fake bus: match on `EventId`; P11 match on
   `MessageId`+`EventTypeId`+`SessionId`; scheduled match cancelled; `SessionBusy`, `MessageLocked`,
   `SessionAtRisk`, `SearchIncomplete` (bound reached) each yield the right outstanding reason;
   missing entity is `AlreadyAbsent`; `DeferredCount` corrected and `MarkAllReplayed` only when
   nothing is parked; the block is never cleared.
2. An emulator test (`[TestCategory("Emulator")]`) erases a parked copy and a dead-lettered copy
   and confirms both are gone and the session's other messages are untouched and still processed
   in order.

### Phase 4 — Erase service and API

- `api-spec.yaml`: the two paths and schemas of Decision 6, `EraseEvent` in both audit enums;
  `AuditSettingsProvider.AlwaysRecorded` includes it.
- The service: preconditions, `selectionHash`, known-pair rule, session-safety check, execution
  order, verify pass, aggregation, audit records.

Acceptance (API tests in `NimBus.WebApp.Tests`):

1. Each precondition refuses with its code and changes nothing (store and fake bus unchanged).
2. All writes failing → `Incomplete` with every place outstanding.
3. A rerun on fully erased pairs → `Completed`, every place `AlreadyAbsent`, not `UnknownEvent`.
4. A blocking copy in a batch → that copy `BlocksSession`, the others erased, outcome
   `Incomplete`; after Skip and a rerun → `Completed`.
5. A row re-created between step 5 and 6 → `ReappearedDuringErasure`.
6. Started and finished `EraseEvent` records are written, contain no payload (asserted by
   searching `Data` for the test payload), and a rerun writes new records linked by
   `previousErasureId`.
7. A non-Owner gets `Refused`/`Forbidden` with an access-denied audit row.
8. The build with NSwag generation enabled succeeds.

### Phase 5 — WebApp card

- Operations → Cleanup → *Erase event*, and the *Erase* action on event details.

Acceptance:

1. Vitest: preview rendering (counts, estimates, flags, siblings, Not covered panel), the Run
   button enabled only for the exact confirmation text, each outcome banner, the outstanding table
   with its action links, *Retry outstanding* sending `previousErasureId`.
2. A screenshot of the preview and of an Incomplete result in the PR (AGENTS.md).

### Phase 6 — Recall selection picker (after Spec 042's selection lands)

- *From a Recall selection* in the input step, resolving 042's selection into pairs (max 100).

Acceptance: Vitest and API tests show the selection resolves to the same pairs 042's store-row
match returns, is capped at 100 with a warning, and execution still takes only the frozen pairs.

## Out of scope

- Implementing any of the above (this PR is the spec only).
- Retention for SQL Server (AF-199).
- Erasing data outside the boundary, and redacting SearchEvents entries.
