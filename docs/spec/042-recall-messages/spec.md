# Spec 042: Recall messages

Status: proposed (2026-11-01; revised 2026-11-13 after review: store selection from `EventRequest` history, composite-id row lookups, uncapped resumable scans, per-endpoint × state audit counts). Issue: context-and-oss/NimBus#290, part of the Operations cleanup gap analysis #292.
Related: #284 (audit Operations cleanup actions, closed by #303), #286 (DLQ delete), #287 (bulk Skip unblocks sessions), #289 (purge leaves Pending rows, closed by #305), #291 (personal-data erasure, out of scope), #295 (Delete dead-lettered also clears the DLQ), #298 (keep the event that blocks its session).

Numbering: `docs/spec/` ends at 040. Spec 041 has no folder, but `docs/plan/2026-10-21-session-order-after-unblock.md` and `docs/plan/2026-10-25-topology-single-source-cleanup.md` already refer to "Spec 041" (the Core bug fixes #276–#278), so this spec is 042.

Conventions used below: **(existing)** marks a type, member, file or path that is on `master` today; **(new)** marks something this spec proposes. Paths are relative to the repository root.

## 1. Problem

Integration P published event type X with wrong data between T1 and T2. Service Bus copied every event to the subscribers A, B and C. Each copy is now in one of these states:

- still queued on the endpoint's subscription (Service Bus state Active), or Service Bus–deferred (state Deferred);
- parked on the endpoint's `Deferred` subscription behind a failed event in the same session;
- Failed, blocking its session;
- Pending (in flight) or Pending with the `Handoff` sub-status (blocking its session);
- dead-lettered (in the dead-letter queue, row DeadLettered);
- Completed.

Today the operator can work only one endpoint, one status and one page at a time:

- **Purge subscription** (`AdminService.PurgeSubscriptionAsync`, existing) filters on Active/Deferred and "enqueued before". It has no event-type, publisher or lower time bound, so it also removes good messages, and it works on one endpoint.
- **Skip messages** and **Delete by status** (`AdminService.SkipMessagesAsync`, `AdminService.DeleteByStatusAsync`, existing) filter only on endpoint and status.
- **Endpoint → Events** has the right filters and a real Skip, but works on one endpoint and one page, with no delete and no export.

The operator's runbook (from the issue) is:

1. Stop P with the send kill switch, and optionally pause A–C with the receive kill switch.
2. Find every copy, with counts per endpoint and per state.
3. Remove the queued, deferred and dead-lettered copies from Service Bus.
4. Skip the failed copies through real SkipRequests so their sessions continue.
5. Export the completed copies so the target systems can be corrected, and optionally delete the stored rows.
6. Write one audit record per run.

**Recall messages** (new) is one Operations action that does steps 2–6 across endpoints. Step 1 stays manual: the kill switches already exist and are audited.

## 2. Overview

```
POST preview (filter) ──► RecallRun (Previewing) ──► discovery per endpoint:
                                                        a history: the endpoint's original EventRequest records (P, window)
                                                        b rows: looked up by {eventId}_{sessionId} from (a) and (c)
                                                        c Service Bus: full paged walk of subscription, Deferred, DLQ
                                                     ──► Previewed (every scan complete): counts per endpoint × state
                                                         │   (PreviewIncomplete: resume, or exclude the endpoint)
                                                         │
             typed confirmation + chosen actions ────────┤
                                                         ▼
POST execute ─────────────► RecallRun (Executing) ──► per endpoint, in order:
                                                        1 remove queued copies (main subscription)
                                                        2 remove parked copies (Deferred subscription)
                                                        3 remove dead-lettered copies (DLQ)
                                                        4 skip Failed / Handoff rows (IOperatorCommands.SkipAsync)
                                                        5 delete stranded rows
                                                        6 export Completed rows (CSV), optionally delete them
                                                     ──► Completed | CompletedWithErrors | Cancelled | Failed
                                                     ──► one RecallMessages audit record
```

Both phases run as background jobs in the WebApp. The WebApp UI and the `nb recall` CLI command are two clients of the same API. The store needs no new interface member (D1 rejected alternatives, D7.2).

## 3. Decisions

Each decision records the chosen option, then the rejected alternatives with a reason.

### D1. Selection

#### D1.1 Request filter

`RecallFilter` (new API schema, §D5.1):

| Field | Required | Meaning |
|---|---|---|
| `eventTypeIds[]` | yes, 1–20 | Event type ids to recall. Exact, ordinal match. |
| `from` | yes | The publishing endpoint (P). Exact, case-insensitive match. |
| `enqueuedFrom` | yes | UTC lower bound, inclusive. |
| `enqueuedTo` | yes | UTC upper bound, exclusive. Must be after `enqueuedFrom`; the window is at most 31 days. |
| `eventIds[]` | no, 0–1000 | Narrow to these event ids. Exact, ordinal match. |
| `sessionIdPrefix` | no | Narrow to sessions whose id starts with this value. Case-insensitive prefix. |
| `endpoints[]` | no | The endpoints to walk. Default: every subscriber of the selected event types (D1.3). |
| `includeMissingFrom` | no, default `false` | Also match copies that carry no publisher at all (D1.2). |

The window and the publisher always describe the **original `EventRequest`** copy on the endpoint, never a later message: the broker `EnqueuedTime` and `From` property of the `EventRequest` on Service Bus, and the `MessageEntity.EnqueuedTimeUtc` and `MessageEntity.From` (existing) of the endpoint's `EventRequest` history record in the store. The tracking row (`UnresolvedEvent`) is **not** used for selection: `ResolverService.CreateUnresolvedEvent` (existing, private) projects `From`, `EnqueuedTimeUtc` and `MessageType` from the latest message, so for a Failed or Completed row they describe the subscriber's response (its `From` is the subscriber, its time the response's enqueue time), not P's publish. The forward from P's topic to a subscriber's topic is immediate, so the endpoint enqueue time is within moments of the publish; the UI and CLI advise widening the window by a minute on each side and reading the preview.

#### D1.2 Matching mechanism per field

A copy "matches" only when every given criterion matches. The table names the mechanism on each data source.

| Field | Service Bus mechanism | Store mechanism |
|---|---|---|
| `eventTypeIds[]` | Application property `EventTypeId` (existing; set by `MessageHelper.ToServiceBusMessage` and `MessageHelper.CreateDeferredMessage` in `src/NimBus.ServiceBus/MessageHelper.cs`). | History: `MessageFilter.EventTypeId` (existing, `List<string>`) as a hint, re-checked in process with exact list membership on `MessageEntity.EventTypeId`. |
| `from` | Application property `From` (existing; set by `ToServiceBusMessage` only when non-empty; **not** set by `CreateDeferredMessage`). Matched on `EventRequest` messages only; see "Derived matches" below. | History: `MessageEntity.From` of the endpoint's `EventRequest` record, compared in process with case-insensitive equality. Not passed to the store as `MessageFilter.From`, because that is a substring match on Cosmos DB and a case-sensitive exact match in memory. |
| `enqueuedFrom` / `enqueuedTo` | Broker metadata: `ServiceBusReceivedMessage.EnqueuedTime` of the `EventRequest`. Not used on the `Deferred` subscription, where the enqueued time is the parking time. | History: `MessageFilter.EnqueuedAtFrom` / `EnqueuedAtTo` (existing) as a hint (Cosmos DB widens it to whole seconds, `CosmosDateBounds`; the in-memory store ignores it), then `MessageEntity.EnqueuedTimeUtc` re-checked in process, which also makes the upper bound exclusive. |
| `eventIds[]` | Application property `EventId` (existing), exact set membership. | History: one `SearchMessages` query per requested id with `MessageFilter.EventId` (existing, case-insensitive **prefix**), then exact ordinal equality on `MessageEntity.EventId` in process. Without `eventIds[]`, one query per endpoint over the window. |
| `sessionIdPrefix` | Broker metadata `SessionId` on the endpoint subscription and the dead-letter queue. On the `Deferred` subscription, application property `OriginalSessionId` (existing; equal to the broker `SessionId` there), falling back to `SessionId`. Case-insensitive prefix. | History: `MessageFilter.SessionId` (existing, case-insensitive prefix on every provider), re-checked in process. |
| `endpoints[]` | Subscription topology: selects which subscriptions are walked: for each endpoint E, topic E / subscription E (and its dead-letter queue) and topic E / subscription `Deferred` (`Constants.DeferredSubscriptionName`, existing, `src/NimBus.Core/Constants.cs`). Not a message property. | History: `MessageFilter.EndpointId` (existing, case-insensitive prefix) as a hint, re-checked in process with exact equality on `MessageEntity.EndpointId`. One query per endpoint. |
| (implicit) message type | Application property `MessageType` (existing). Only `EventRequest`, `RetryRequest` and `ResubmissionRequest` messages can match. Control messages (`SkipRequest`, `HandoffCompletedRequest`, `HandoffFailedRequest`, `ContinuationRequest`, `ProcessDeferredRequest`) are never removed: removing a queued `SkipRequest` would leave its session blocked. | History: `MessageFilter.MessageType = EventRequest` (existing) as a hint, re-checked in process. Rows: not filtered — a row is one event's state, whatever message last updated it. |

**Store discovery (history first, then rows).** The Resolver stores every message it handles as a `MessageEntity` before it updates the row (`ResolverService`, existing: `StoreMessage` then `UpdateState`), and for an `EventRequest` that record's `EndpointId` is the subscriber (`To`), its `From` the publisher and its `EnqueuedTimeUtc` the endpoint enqueue time. Per endpoint E the recall therefore:

1. pages `IMessageTrackingStore.SearchMessages(MessageFilter { EndpointId = E, MessageType = EventRequest, EventTypeId, SessionId, EnqueuedAtFrom, EnqueuedAtTo }, …)` (existing) to the end of its continuation tokens (or once per id in `eventIds[]`), and keeps the records that pass the in-process re-check of every row above — these are the **original copies**;
2. reads each original copy's tracking row with `IMessageTrackingStore.GetEventsByIds(E, ids)` (existing) in chunks of 50, where each id is the stored composite id `{eventId}_{sessionId}`. The composite form is the only one that works on every provider: Cosmos DB reads it as the document id and partition key (`CosmosDbMessageTrackingStore.GetEventsByIds`, existing, `ReadManyItemsAsync`), while SQL Server and in-memory accept both forms. A plain event id returns nothing on Cosmos DB, so the recall never passes one;
3. classifies each row by its **current** `ResolutionStatus` / `PendingSubStatus` (D2), whatever the row's own `From` or `EnqueuedTimeUtc` say. A Failed or Completed row whose response arrived after `enqueuedTo` is therefore still matched, because the original `EventRequest` was enqueued inside the window.

An original copy with no row is counted as `historyOnly` (nothing to act on). A row whose `EventRequest` history record was deleted (Endpoint → Delete all events, `PurgeStoredMessages`) cannot be matched by the store pass; it is still matched when its message is on Service Bus (derived match below). The preview states this limitation.

**Derived matches.** Some copies cannot be matched on their own properties:

- a `RetryRequest` or `ResubmissionRequest` carries the sender of the retry, not P, in `From`;
- a message parked on the `Deferred` subscription has no `From` at all and its enqueued time is the parking time;
- a dead-lettered message keeps its properties, but the store row is the authoritative record of what NimBus showed as dead-lettered (#295).

So, per endpoint, the recall first builds the **matched event set** (new concept): the `(eventId, sessionId)` pairs of (a) the original copies found in history and (b) `EventRequest` messages on the endpoint subscription or dead-letter queue that match the filter. Rows are read for the whole set (store discovery step 2), so a row found only through (b) is matched as well. A `RetryRequest`/`ResubmissionRequest` on the endpoint subscription, a message on the `Deferred` subscription, and a message in the dead-letter queue then match when their `EventId` is in the matched event set **and** their `EventTypeId` is in `eventTypeIds[]` (and, when given, their session matches `sessionIdPrefix`).

**Copies without `From`.** `MessageHelper` omits `From` when the message's `From` is empty. On Service Bus the `EventRequest` then has no `From` property. In history, `ResolverService.CreateMessageEntity` (existing) records a request's `From` as `GetFromOrDefault() ?? OriginatingFrom ?? Constants.Self`: a record that fell back to `OriginatingFrom` equal to P matches normally (the origin is P), and a record whose `From` is `Constants.Self` (existing, `"self"`) is treated as missing. By default a copy without `From` does not match, and the preview reports it per endpoint as `missingFrom` (it matched every other criterion). With `includeMissingFrom: true` it matches. The default keeps a recall from touching messages that some other tool or legacy publisher sent with the same event type.

#### D1.3 Default endpoint set

When `endpoints[]` is absent, the endpoint set is the union of:

- for each event type id, `IPlatform.GetConsumers(eventType)` (existing, `src/NimBus.Abstractions/Platform.cs`), where `eventType` is the `IPlatform.EventTypes` entry whose `Id` equals the id;
- for each `DynamicForward` in `IPlatform.DynamicForwards` (existing, spec 022; record in `src/NimBus.Abstractions/Endpoints/DynamicForward.cs`) whose `EventTypeId` equals an id and whose `SourceEndpoint` equals `from` (case-insensitive): its `TargetEndpoint`.

Dynamic forwards are included because provisioning creates a real forward subscription for each, so their copies land on the target endpoint exactly like compiled-event copies. A forward from another source endpoint cannot carry P's copies, so it is not included.

The publisher P itself is not in the default set. Its store rows (`Published`) are export-only (D2) and an operator who wants them adds P to `endpoints[]`.

#### D1.4 Validation and rejection

The API rejects the request with `400 ValidationProblemDetails`, naming each failing field, when:

- an event type id is neither the `Id` of an `IPlatform.EventTypes` entry nor the `EventTypeId` of a `DynamicForward`;
- `from` or an entry of `endpoints[]` does not exist (`EndpointVerificationService.EndpointExists`, existing);
- an explicit endpoint consumes none of the event types (neither a consumer nor a dynamic-forward target) — a typo guard; the response names the endpoints that do;
- the window is missing, inverted or longer than 31 days, or a list exceeds its bound;
- the resolved endpoint set (explicit or default) has more than 50 endpoints — the bound that keeps the per-endpoint × state audit record within its size limit (D6.1); the operator splits the recall with `endpoints[]`;
- the default endpoint set is empty.

Endpoints are normalised to the platform's canonical id casing before any store or Service Bus call, because Cosmos DB partitions match the endpoint id exactly.

#### D1 rejected alternatives

- **Make `from` optional.** Rejected: the publisher is the guard that keeps a recall from removing the same event type published correctly by another endpoint.
- **Treat a missing `From` as a match.** Rejected: over-broad by default; kept as the explicit `includeMissingFrom` opt-in instead.
- **Select rows on their own `From` and `EnqueuedTimeUtc` (`EventFilter.From`, `EventFilter.EnqueuedAtFrom/To`).** Rejected: those fields come from the latest message, so a Failed or Completed row carries the subscriber's `From` and the response time, and the recall would miss exactly the copies it must skip or export. Their original requests have already left Service Bus, so the Service Bus pass cannot recover them either.
- **A persistent "original request" projection on the row (new `UnresolvedEvent.OriginalFrom` / `OriginalEnqueuedTimeUtc`).** Rejected for now: needs a Resolver change, a SQL migration, three providers and a backfill for existing rows, while the `EventRequest` history record already holds both values for every row.
- **Read rows with plain event ids (`GetEventsByIds(endpointId, eventIds)`).** Rejected: returns nothing on Cosmos DB, whose document id is `{eventId}_{sessionId}`; the composite id is known from history or the Service Bus message.
- **Add `MessageFilter.EventIds` / `EventFilter.EventIds` (lists) or an endpoint list.** Rejected for now: one prefix query per id plus an exact re-check gives the same result without a new store member, which would need three providers, conformance tests and the decorator forward. If preview latency proves a problem, a later spec can add one under the rules in §D7.2.
- **Match `from` and the window on every Service Bus message.** Rejected: retries, resubmissions and parked copies do not carry P's `From` or the publish time, so they would be missed and their rows left behind.
- **Derive the endpoint set from the live Service Bus topology** (subscription rules). Rejected: the platform configuration is what provisioning applies (`ServiceBusTopologyProvisioner`), is available without management rights, and is already how the WebApp lists consumers.

### D2. Action per copy state

Four opt-in actions (new), chosen on execute. None is on by default; execute with no action selected is rejected.

| Action | Effect |
|---|---|
| `removeQueued` | Complete matching messages on the endpoint subscription (Active and Service Bus–deferred), the `Deferred` subscription and the dead-letter queue. |
| `skipFailed` | Send a real skip through `IOperatorCommands.SkipAsync` (existing, `src/NimBus.WebApp/Services/Operations/IOperatorCommands.cs`) for matching Failed rows and Pending+Handoff rows, so their sessions continue. |
| `deleteRows` | Delete the tracking rows that the removal left stranded (D3). |
| `deleteCompletedRows` | Delete the matching Completed rows after the run's CSV export was written. Requires `deleteRows`. |

The issue proposed three actions (remove queued, skip failed, delete rows). Deleting Completed rows is split into its own opt-in because those rows are the record a team needs to correct the target systems, unlike stranded rows, which describe nothing that will ever happen.

| Copy state | Where it is | Action | Opt-in |
|---|---|---|---|
| Active / Service Bus–deferred | Topic E / subscription E | Complete the message (`removeQueued`). | yes |
| Parked | Topic E / subscription `Deferred` | Complete the message (`removeQueued`). Done **before** any skip, so the drain that follows a skip cannot replay a bad parked copy. | yes |
| Dead-lettered | Dead-letter queue of topic E / subscription E | Complete the message (`removeQueued`), as #295 does for Delete dead-lettered. | yes |
| Failed (blocks its session) | Store row | Skip through `IOperatorCommands.SkipAsync` (`skipFailed`). The Resolver records Skipped from the endpoint's SkipResponse. | yes |
| Pending (not Handoff) | Store row; message queued or in flight | No command. The row is deleted by `deleteRows` only once its message was removed (D3). | (via `removeQueued` + `deleteRows`) |
| Pending + Handoff (blocks its session) | Store row | Skip through `IOperatorCommands.SkipAsync` (`skipFailed`); the WebApp channel accepts a skip on a hand-off (`OperatorCommandCoordinator`, existing). | yes |
| Deferred row | Store row for a parked copy | Deleted by `deleteRows` once the parked copy was removed (D3). | (via `removeQueued` + `deleteRows`) |
| DeadLettered row | Store row | Deleted by `deleteRows` once its DLQ message is verifiably gone (D3). | (via `removeQueued` + `deleteRows`) |
| Unsupported | Store row; nothing on Service Bus | Deleted by `deleteRows`. | yes |
| TooManyRequests | Store row; message rescheduled on Service Bus | Treated like Pending. | (via `removeQueued` + `deleteRows`) |
| Completed | Store row | **Export only**: never removed from Service Bus (nothing is there), never skipped. Row deleted only with `deleteCompletedRows`. | export always; delete opt-in |
| Published (publisher's own row) | Store row on P, only if P is in `endpoints[]` | Export only; never deleted. | — |
| Skipped | Store row | None (already settled); counted in the preview. | — |

Execution order per endpoint is fixed: main subscription → `Deferred` subscription → dead-letter queue → skips → row deletion → export → Completed-row deletion. Removing queued and parked copies before skipping matters: a skip releases the session, and the endpoint then drains the `Deferred` subscription and processes whatever is still queued.

Endpoints are processed one at a time, in the order of the endpoint set, so a cancellation leaves whole endpoints either done or untouched except for the one in progress.

**Paused subscriptions.** Service Bus rejects receive on a `ReceiveDisabled` subscription. If runbook step 1 paused E, `removeQueued` makes E's subscription receivable for the duration of E's removal steps and restores the status in a `finally`, as `SubscriptionAdminService` (existing) already does for Topology → Purge. Copies that E's own processor handles in that window become Completed or Failed rows; the store steps run afterwards, so they are still exported or skipped. The preview flags every paused endpoint, and the restore is reported per endpoint.

**Abandon cost.** Within a session, Service Bus hands messages out in order, so a non-matching message ahead of a matching one must be received and abandoned, which adds one to its delivery count. The recall stops receiving in a session once every matching sequence number found by the peek is settled, so each non-matching message is abandoned at most once per run. The preview reports this count per endpoint as `abandonCost`.

#### D2 rejected alternatives

- **Delete Failed and Handoff rows instead of skipping.** Rejected: the session stays blocked by an event no one can see (#288, #298).
- **Write Skipped to the store directly.** Rejected: only the endpoint can clear a session's block (#287).
- **Skip parked (Deferred) rows.** Rejected: `SkipRules.BulkSkipSourceStatuses` (existing) excludes Deferred, because the parked copy replays and runs once its block clears, whatever a skip recorded. Removing the parked copy is the only way to stop it.
- **Skip DeadLettered and Unsupported rows.** Rejected for recall: they block nothing, so a skip only adds a round trip. Removing the DLQ message and the row is what the operator wants for bad data.
- **One combined "recall everything" switch.** Rejected: an operator often wants the Service Bus and skip steps now and the destructive row deletion later, after checking the export.
- **Remove control messages for matched events.** Rejected: removing a queued `SkipRequest` or hand-off settlement would leave a session blocked.

### D3. Stored rows

Rows are deleted with `IMessageTrackingStore.RemoveMessage(eventId, sessionId, endpointId)` (existing), which removes the tracking row only. **Message history (`MessageEntity` records) and audit rows are always kept**, as Purge subscription (#305) and Delete dead-lettered (#295) keep them; erasing history is #291 and out of scope.

| State | Tracking row | History |
|---|---|---|
| Active / SB-deferred copy removed | Its Pending (non-Handoff) or Deferred row is deleted with `deleteRows`, using the #289 rule below. | kept |
| Parked copy removed | Its Deferred row is deleted with `deleteRows`. | kept |
| DLQ copy removed | Its DeadLettered row is deleted with `deleteRows` only if a peek after the removal shows no DLQ message with that `EventId` is left; otherwise kept and reported, so a rerun finds both (#295 rule). | kept |
| Failed | **Kept** — blocks its session (#298). Becomes Skipped through the endpoint when `skipFailed` is chosen. Never deleted by the recall, including on a rerun after the skip. | kept |
| Pending + Handoff | **Kept** — blocks its session (#298). Same as Failed. | kept |
| Pending (not Handoff) whose message was not found | **Kept** — the message is in flight; the row will settle. Counted as `kept` for its state (reason: in flight). | kept |
| Unsupported | Deleted with `deleteRows`. | kept |
| Completed | Kept, unless `deleteCompletedRows`; then deleted after the export is written. | kept |
| Skipped, Published | Kept. | kept |

**#289 rule adopted.** #289 is closed by #305, and the recall reuses its rule verbatim: a row is stranded, and deleted, when it is `Deferred`, or `Pending` and not `BlockedEventRules.BlocksItsSession(row)` (existing), and the run completed that row's message. The private predicate `AdminService.IsLeftByPurge` (existing) moves to `BlockedEventRules.IsStrandedByRemoval(UnresolvedEvent)` (new, public, same body) so Purge subscription and Recall share one definition. Before deleting, the row is re-read with `GetEvent(endpointId, eventId)` (existing) and the rule is re-applied to the fresh row, so a row that moved on during the run (for example to Failed) is kept.

**#298 rule adopted.** A row for which `BlockedEventRules.BlocksItsSession` is true is never deleted by any recall action. It is counted as `kept` for its state in the result (reason: blocks its session).

#### D3 rejected alternatives

- **Delete every matching row.** Rejected: deleting Failed or Handoff rows strands sessions (#298).
- **Delete history too.** Rejected: history is what the export and later investigation read; erasure is #291.
- **Delete a DeadLettered row whether or not its DLQ message is gone.** Rejected by #295: a row whose message is still queued must stay so a rerun finds both.
- **Delete Pending rows whose message was not found.** Rejected: the message may be locked by a processor right now; the row will settle.

### D4. Execution

#### D4.1 Preview and execute are both background jobs

- **Preview: background job.** It peeks every subscription and dead-letter queue of every endpoint in the set and pages the store; on a large namespace this takes minutes.
- **Execute: background job.** It completes messages, sends skips and deletes rows; it can run longer than the App Service front end allows a request (230 seconds).

A run (`RecallRun`, new) is created by the preview request and carries its filter. Execute is a transition of the same run: the operator cannot change the filter between seeing the counts and confirming. Execute re-matches live state as it goes; preview counts are informational. A preview older than 30 minutes must be re-run before execute (`409`). **Execute is refused (`409`) unless the preview is `Previewed`, which requires every scan of every endpoint in the set to be complete (D4.5).** A `PreviewIncomplete` run can be resumed or re-previewed without the failing endpoint; it can never be executed.

**Job host.** `RecallRunRegistry` (new, singleton) holds runs in memory on the WebApp instance that started them and runs each on a `RecallJobRunner` (new, `BackgroundService`) through a bounded channel. Limits: one executing run per WebApp instance (`409` otherwise), at most three previews at a time, runs kept for 24 hours. Run state is held in memory only; the durable record of an executed run is its audit record (D6). A run lost to a restart shows as not found; the per-skip audit rows and the store show what it did. A scaled-out WebApp needs ARR affinity so the client polls the instance that holds the run; the CLI keeps cookies for that reason.

**Identity inside the job.** `IOperatorCommands.SkipAsync`, `IEndpointAuthorizationService.HasRoleFreshAsync` and `IAuditLogService.LogAuditAsync` (existing) read the caller from the `HttpContext`. The runner captures the initiating request's `ClaimsPrincipal` (after claims transformation) and, for the job's async flow, creates a DI scope and sets `IHttpContextAccessor.HttpContext` to a `DefaultHttpContext` carrying that principal and the scope's `RequestServices`. Every skip therefore re-checks the operator's role against fresh access lists and is audited under the operator's name, exactly like Operations → Skip messages.

#### D4.2 Partial failure

- **Per message.** A failed complete, a skip that is not `Accepted`, or a failed row delete counts in that endpoint's `failed` for that state, with one error line naming the event id. The run continues.
- **Per endpoint.** If a subscription cannot be read at all, a session cannot be accepted, or its walk stops before reaching the end (D4.5), that step of that endpoint is marked `incomplete` with the reason and the cursor it reached, the rest of that endpoint's steps that depend on it are skipped (a DLQ that could not be walked to the end keeps its DeadLettered rows; a main or `Deferred` subscription that could not be walked to the end sends no skip on that endpoint, so no parked copy is released), and the run moves on to the next endpoint. The endpoint outcome is `Incomplete` and the run outcome `CompletedWithErrors`; the result lists the unfinished work per endpoint and subscription, and a rerun of the same filter finishes it (D4.4).
- **Run-wide refusals.** A skip result of `Forbidden` or `AuditUnavailable` stops all further skips in the run, as `SkipMessagesAsync` (existing) does, because it would refuse every row alike. The Service Bus steps of later endpoints still run.
- **Run outcome.** `Completed` (no failures), `CompletedWithErrors` (some step or message failed), `Cancelled`, or `Failed` (an unexpected exception stopped the run). The status-restore of a paused subscription is always attempted and its failure is an error line.

#### D4.3 Cancellation

`POST …/cancel` sets the run's `CancellationToken`. The runner stops before the next message, skip or row delete; the message or command in progress finishes, so nothing is left half-settled. A paused subscription's status is restored. The audit record is written with outcome `Cancelled` and the counts so far. Cancelling a preview just stops it.

#### D4.4 Rerun safety

Rerunning the same filter is safe and is the documented way to finish a run with errors:

- Removed messages are gone, so a rerun finds only what is left. A rerun is a new run that walks every source from the start (no cursor carries over between runs), so whatever an `Incomplete` endpoint left behind is found.
- Store selection reads the immutable `EventRequest` history record, so a copy that the first run skipped or exported still matches; its current status decides that nothing more is done to it.
- A skip is sent only for rows that are still Failed or Handoff, found with `IOperatorCommands.FindCurrentAsync` (existing) and claimed at their current version; a row skipped by the first run is Skipped (or Stale while the skip is in flight) and is not skipped twice.
- Row deletion re-reads each row and applies the D3 rules to its current state.
- The export is regenerated from the current Completed rows.

Each run writes its own audit record.

#### D4.5 Scanning: complete, bounded pages, resumable

There is **no total scan limit**. Every walk of a subscription, its dead-letter queue or the `Deferred` subscription (in preview and again in execute) and every history query pages to the end:

- **Pages.** Service Bus is read in pages of `RecallScanPageSize` (new, 250) with `PeekMessagesAsync(maxMessages, fromSequenceNumber)` (existing SDK call, as `SubscriptionAdminService` uses), advancing `fromSequenceNumber` past the last peeked message, until a page comes back empty. Session-enabled subscriptions are walked per session with `AcceptSessionAsync`, following `AdminService.PurgeSubscriptionAsync` (existing). History is paged by continuation token. Only the matched entries are kept (sequence number, event id, session id, state), never message bodies, so memory grows with matches, not with the subscription size.
- **Cursor.** After each page the run records a cursor per endpoint and source (`main`, `deferredSubscription`, `deadLetter`, `history`): the next sequence number, or the session id plus sequence number, or the continuation token. A transient page failure is retried three times with backoff; a page that still fails stops that source with `incomplete` and keeps its cursor.
- **Resume.** `POST …/runs/{runId}/preview/resume` (D5.1) continues every incomplete source of a `PreviewIncomplete` run from its cursor. Messages that arrive behind a cursor while the run is paused are found by execute, which walks again.
- **Completion.** A source is complete when its walk reached an empty page (Service Bus) or a null continuation token (history). The preview reaches `Previewed` only when every source of every endpoint is complete; otherwise it ends `PreviewIncomplete`. Neither preview nor execute ever reports a partial walk as done.
- **Match bound.** The only bound is on matches: a run holds at most 200,000 matched copies (new constant `RecallMaxMatchedCopies`). Reaching it ends the preview `PreviewIncomplete` with reason `tooManyMatches`. Unlike a scan cutoff, this bound is reached only by matches, so narrowing the window, the event ids or the endpoints always brings a recall under it.
- **Execute.** Execute walks each source to the end again (removing as it goes), so it also acts on copies enqueued after the preview. A walk that cannot be finished is reported `incomplete` per D4.2.

#### D4 rejected alternatives

- **Synchronous preview.** Rejected: walking every subscription can exceed the request timeout, and the issue expects a job.
- **Synchronous execute.** Rejected: same timeout, and a dropped connection would leave the operator with no result.
- **Persist runs in the store.** Rejected for now: needs a new store member on three providers for state that the audit record already makes durable where it matters. Can follow if multi-instance progress becomes necessary.
- **Execute with a filter in the request** (no preview run). Rejected: lets the confirmed counts and the executed filter drift apart.
- **Run endpoints in parallel.** Rejected: harder to cancel cleanly and to reason about, and Service Bus throughput per namespace is shared anyway.
- **Cap the scan at a total message count** (like `SubscriptionAdminService`'s private `MatchScanSnapshotLimit` of 100,000). Rejected: matches behind the cap are never inspected, narrowing the filter does not shrink the messages in front of them, and a rerun meets the same cap — the recall would silently leave copies behind.
- **Allow execute on an incomplete preview, acting on what was found.** Rejected: the operator would confirm counts that are known to be partial; execute is refused instead, and an execute-time walk that stops is reported as `Incomplete`.

### D5. Surfaces

#### D5.1 API (`src/NimBus.WebApp/api-spec.yaml`)

All paths are new, tagged `Admin`, require the site **Owner** role like every other Operations path (`AdminImplementation.IsSiteOwnerAsync`, existing), and return `403` (audited as `RecallMessages`, access denied) otherwise. Cookie-authenticated writes require the antiforgery token (#297) as all writes do.

| Method | Path | operationId | Request | Response |
|---|---|---|---|---|
| POST | `/api/admin/recall/preview` | `post-admin-recall-preview` | `RecallFilter` | `202 RecallRun`; `400 ValidationProblemDetails`; `429` when three previews are running |
| GET | `/api/admin/recall/runs/{runId}` | `get-admin-recall-run` | — | `200 RecallRun`; `404` |
| POST | `/api/admin/recall/runs/{runId}/preview/resume` | `post-admin-recall-preview-resume` | — | `202 RecallRun`; `404`; `409` unless the run is `PreviewIncomplete` |
| POST | `/api/admin/recall/runs/{runId}/execute` | `post-admin-recall-execute` | `RecallExecuteRequest` | `202 RecallRun`; `400` (no action, wrong confirmation); `409` (not `Previewed` — including `PreviewIncomplete` —, preview stale, already executed, another run executing) |
| POST | `/api/admin/recall/runs/{runId}/cancel` | `post-admin-recall-cancel` | — | `202 RecallRun`; `404`; `409` when finished |
| GET | `/api/admin/recall/runs/{runId}/export` | `get-admin-recall-export` | — | `200 text/csv` (D6.2); `404`; `409` before execute has exported |

Schemas (all new):

- `RecallFilter`: the fields in D1.1.
- `RecallActions`: `removeQueued`, `skipFailed`, `deleteRows`, `deleteCompletedRows` (booleans).
- `RecallExecuteRequest`: `actions: RecallActions`, `confirmation: string`. The confirmation must equal `recall <n>`, where `n` is `RecallPreview.actionableTotal` for the chosen actions (computed server-side from the preview). This makes the typed confirmation binding for the CLI as well as the UI.
- `RecallRun`: `runId`, `status` (`Previewing`, `Previewed`, `PreviewIncomplete`, `Executing`, `Completed`, `CompletedWithErrors`, `Cancelled`, `Failed`), `filter: RecallFilter`, `endpoints: string[]` (resolved set), `startedBy`, `createdAt`, `previewedAt`, `executedAt`, `finishedAt`, `progress: RecallProgress`, `preview: RecallPreview` (nullable), `result: RecallResult` (nullable).
- `RecallProgress`: `currentEndpoint`, `currentStep`, `endpointsDone`, `endpointsTotal`, `messagesScanned`.
- `RecallPreview`: `endpoints: RecallEndpointCounts[]`, `totals: RecallStateCounts`, `actionableTotal: { removeQueued, skipFailed, deleteRows, deleteCompletedRows }`, `warnings: string[]`.
- `RecallEndpointCounts`: `endpointId`, `subscriptionPaused: boolean`, `counts: RecallStateCounts`, `missingFrom: integer`, `historyOnly: integer`, `abandonCost: integer`, `scans: RecallScanState[]`, `error: string` (nullable).
- `RecallScanState`: `source` (`main`, `deferredSubscription`, `deadLetter`, `history`), `complete: boolean`, `messagesScanned: integer`, `reason: string` (nullable). The cursor itself stays server-side.
- `RecallStateCounts`: one integer per copy state, keyed by `RecallCopyState`.
- `RecallCopyState` (enum, fixed order used by the audit encoding, D6.1): `active`, `serviceBusDeferred`, `parked`, `deadLettered` (Service Bus messages); `failed`, `pending`, `handoff`, `deferredRows`, `deadLetteredRows`, `unsupported`, `tooManyRequests`, `completed`, `published`, `skipped` (store rows).
- `RecallResult`: `endpoints: RecallEndpointResult[]`, `totals: RecallStateOutcomes`, `errors: string[]` (first 200), `exportRowCount`.
- `RecallEndpointResult`: `endpointId`, `outcome` (`Done`, `DoneWithErrors`, `Incomplete`, `Failed`, `NotStarted`), `states: RecallStateOutcomes`, `incompleteSources: string[]`, `statusRestored: boolean` (nullable).
- `RecallStateOutcomes`: for each `RecallCopyState`, a `RecallStateOutcome`.
- `RecallStateOutcome`: `found` (copies in this state when execute reached them), `acted` (removed from Service Bus, skip sent, or exported — by state), `rowsDeleted`, `kept` (blocking, in flight, or DLQ message still present), `failed`.

#### D5.2 Server-side services

- `src/NimBus.WebApp/Services/Recall/` (new folder):
  - `RecallFilterValidator` — D1.4 validation and canonicalisation; resolves the default endpoint set from `IPlatform`.
  - `RecallMatcher` — pure functions: `Matches(ServiceBusReceivedMessage)`, `Matches(MessageEntity)` (an `EventRequest` history record), `Classify(UnresolvedEvent)` (the copy state of a row by its current status, D2), and the derived-match rule over a matched event set. No I/O, fully unit-tested.
  - `RecallScanner` — the preview: history pass and row lookup (D1.2 store discovery), then the paged, resumable walks of the main subscription, `Deferred` subscription and DLQ per endpoint (D4.5), with cursors held on the run.
  - `RecallExecutor` — the execute steps of D2 per endpoint, walking each source to the end with the same pager as the scanner and the session receive pattern of `AdminService.PurgeSubscriptionAsync` (existing) for session-enabled subscriptions. It does **not** reuse `SubscriptionAdminService.CompleteMatchingAsync` / `PeekMatchingAsync` (existing, internal static), because those stop at the private 100,000-message `MatchScanSnapshotLimit`.
  - `RecallAuditWriter` — D6.1 serialisation and size assertion.
  - `RecallRunRegistry`, `RecallJobRunner` — D4.1.
  - `RecallCsvWriter` — D6.2.
  - `ServiceCollectionExtensions.AddNimBusRecall()` — registration, called from `Startup.Web.cs`.
- `src/NimBus.WebApp/Services/IAdminService.cs`, `AdminService.Recall.cs` (new partial): `StartRecallPreviewAsync`, `GetRecallRun`, `ResumeRecallPreviewAsync`, `StartRecallExecuteAsync`, `CancelRecallRun`, `GetRecallExport`. The `AdminService` partial is the façade the controller calls, like the other Operations actions.
- `src/NimBus.WebApp/Services/AdminService.Purge.cs`: `IsLeftByPurge` replaced by `BlockedEventRules.IsStrandedByRemoval` (D3). No behaviour change.
- `src/NimBus.WebApp/Controllers/ApiContract/AdminImplementation.cs`: the six generated methods (`PostAdminRecallPreviewAsync`, `GetAdminRecallRunAsync`, `PostAdminRecallPreviewResumeAsync`, `PostAdminRecallExecuteAsync`, `PostAdminRecallCancelAsync`, `GetAdminRecallExportAsync`), with the Owner check and access-denied audit.
- Skips use `OperatorCommandContext(OperatorChannel.WebApp, Reason: "Recall <runId>")` (existing record), so every per-event Skip audit row names the run.

#### D5.3 WebApp UI

A new Operations entry, `/Operations/recall`, in the **Irreversible** group:

- `src/NimBus.WebApp/ClientApp/src/models/manage-pages.ts`: one entry appended to `OPERATIONS` (existing): `{ id: "recall", group: "danger", label: "Recall messages", takesEndpoint: false, description: "Find every copy of wrongly published messages across endpoints, remove the queued ones, skip the failed ones and export the completed ones." }`.
- `src/NimBus.WebApp/ClientApp/src/components/admin/operations.tsx`: one entry added to `CARDS` (existing): `recall: (p) => <RecallMessagesCard {...p} />`.
- `src/NimBus.WebApp/ClientApp/src/components/admin/recall-messages.tsx` (new) — `RecallMessagesCard`:
  1. **Filter form**: event types (multi-select from the platform's event types), publisher (select), window (two UTC datetime inputs, with the "widen by a minute" hint), optional event ids (textarea, one per line), session prefix, endpoints (multi-select pre-filled with the default set once event types and publisher are chosen), "Include copies without a publisher".
  2. **Preview**: progress while `Previewing`; then a table with one row per endpoint and one column per state (D5.1 `RecallStateCounts`), a totals row, paused/`missingFrom`/`historyOnly`/`abandonCost` badges, per-source scan state with a **Resume scan** button while the run is `PreviewIncomplete`, and the warnings. Execute is not offered until the run is `Previewed`.
  3. **Actions**: four checkboxes (D2), each showing the count it will act on.
  4. **Typed confirmation**: `ConfirmDestructiveAction` (existing, `components/admin/confirm-destructive-action.tsx`) with `confirmText` = `recall <n>`.
  5. **Execution**: per-endpoint progress and outcome, error list, a **Download CSV** button, and a **Cancel** button while running. The card polls `GET …/runs/{runId}` every 2 seconds while the run is active.
- Existing Operations cards are unchanged.

#### D5.4 CLI

`nb recall` (new), in `src/NimBus.CommandLine/Commands/RecallCommands.cs`, registered in `CliApplicationFactory` next to the other `Commands.*.Register` calls. It is a client of the D5.1 API — it does not touch Service Bus or the store directly.

```
nb recall --webapp-url <URL> --event-type <ID>[,<ID>…] --from <ENDPOINT>
          --enqueued-from <UTC> --enqueued-to <UTC>
          [--event-id <ID>[,<ID>…] | --event-ids-file <PATH>]
          [--session-prefix <PREFIX>] [--endpoint <ID>[,<ID>…]] [--include-missing-from]
          [--execute --remove-queued --skip-failed --delete-rows --delete-completed-rows
           --confirm "recall <n>"]
          [--export <PATH.csv>] [--token <BEARER>] [--json]
nb recall --webapp-url <URL> --run <RUN_ID> --resume [--token <BEARER>] [--json]
```

- A preview that ends `PreviewIncomplete` prints the incomplete sources per endpoint and the run id, does not execute even with `--execute`, and exits 1; `--run <RUN_ID> --resume` continues it (`post-admin-recall-preview-resume`) and prints the table again.
- Without `--execute` it runs a preview and prints the per-endpoint × state table (Spectre.Console, like the other commands), then the `--confirm` text an execute would need.
- With `--execute` it previews, checks `--confirm` against the server's expected text and stops with exit code 2 if it differs, then executes, prints progress per endpoint and the result table, and writes the CSV to `--export` when given.
- Authentication: `--token`, else a token from `az account get-access-token --resource <AzureAd client id>` through `AzureCliRunner` (existing). The WebApp accepts Entra bearer tokens (`Startup.Security.cs`, existing).
- `--json` prints the final `RecallRun` as JSON. Exit codes: 0 completed, 1 completed with errors / cancelled / failed, 2 usage or confirmation error.
- Ctrl+C sends `cancel` and waits for the run to stop.

#### D5 rejected alternatives

- **Extend Purge subscription / Skip messages / Delete by status.** Rejected: out of scope (existing cards stay unchanged), and each is single-endpoint by design.
- **Put the UI on Endpoint → Events or the event-type page.** Rejected: both are single-endpoint or read-only views; a cross-endpoint destructive action belongs with Operations, which is Owner-only and grouped by blast radius. A deep link from the event-type page can follow.
- **CLI talking to Service Bus and Cosmos DB directly**, like `nb container skip`. Rejected: duplicates the matching and D3 rules, bypasses the role check and audit, and would not support SQL Server storage.
- **A confirmation that is just "type RECALL".** Rejected: binding the count makes the operator read the preview.

### D6. Records

#### D6.1 Audit record

One record per executed run, written with `IAuditLogService.LogAuditAsync` (existing) when the run finishes (any outcome), under a new audit type `MessageAuditType.RecallMessages` (new), appended after `CopyEndpointData` because Cosmos DB persists the enum's numeric value. This matches #284/#303, where each Operations cleanup action writes one record per completed run and an access-denied record for a non-Owner. Previews are not audited (they change nothing), as with the other `*-preview` paths.

| Field | Value |
|---|---|
| `AuditType` | `RecallMessages` |
| `AuditorName` | The operator (from the captured principal, D4.1). |
| `AuditTimestamp` | When the run finished. |
| `AccessDenied` | `false`; `true` on the access-denied record written by the controller. |
| `EventId`, `EndpointId` | `null`: the run spans endpoints and events. |
| `Data` (JSON) | `v: 1`; `runId`; `filter` (all D1.1 fields; `eventIds` as `eventIdCount`, the first 20 ids and `eventIdsSha256`, the SHA-256 of the sorted, newline-joined list); `endpoints` (resolved set); `actions`; `outcome`; `stateOrder` (the `RecallCopyState` names in their fixed order); `outcomeOrder`: `["found","acted","rowsDeleted","kept","failed"]`; `totals`: 14 tuples, one per state in `stateOrder`, each `[found, acted, rowsDeleted, kept, failed]`; `perEndpoint`: `{ "<endpointId>": { "o": "<endpoint outcome>", "s": [14 tuples as in totals], "i": [incomplete sources] } }`, with **every** endpoint of the set present, including `NotStarted` ones; `exportRowCount`; `startedAt`, `finishedAt`. |

The record therefore holds counts **per endpoint and per copy state** (the D5.1 `RecallStateOutcome` for each `RecallCopyState`), including completed copies (`completed`: `found`, `acted` = exported, `rowsDeleted`), and keeps them after the in-memory run has expired. The removal counts stay split by source state (`active`, `serviceBusDeferred`, `parked`, `deadLettered`), and row deletions are attributed to the state the row was in (`pending`, `deferredRows`, `deadLetteredRows`, `unsupported`, `completed`).

**Size strategy: bound the input, never drop counts.** `AuditLogService` (existing) truncates `Data` at 4 KB (`DataTruncationLimit`), which a 50-endpoint breakdown can exceed. The recall does not shed data to fit; instead:

- `AuditLogService.CreateEntity` gets a per-type limit, `DataLimitFor(MessageAuditType)` (new, internal): 64 KB (`RecallDataTruncationLimit`, new) for `RecallMessages`, `DataTruncationLimit` for every other type, so no existing record changes. The durable columns already hold it: SQL Server `MessageAudits.Data` is `NVARCHAR(MAX)` (`Schema/0012_Audits_AccessDenied_Data.sql`, existing), a Cosmos DB document allows 2 MB, and the in-memory store has no limit. The structured log (`EmitStructuredLog`, existing) keeps the 4 KB cap for every type, so the Application Insights event may be truncated but the store record is complete.
- The inputs are bounded so the worst case fits with margin: at most 50 endpoints (D1.4), endpoint ids at most 50 characters (the Service Bus entity-name limit for a subscription), 14 states, 5 counters each at most 6 digits (a run holds at most 200,000 matched copies, D4.5), and at most 20 event ids of 100 characters. The worst case is about 40 KB.
- `RecallAuditWriter` (new, `Services/Recall/`) serialises the record, asserts it is within `RecallDataTruncationLimit`, and has a test that builds the worst case from these bounds. Exceeding the limit is a programming error caught by that test, never a runtime truncation.

Each skip additionally writes its own `Skip` audit row through `IOperatorCommands.SkipAsync`, with reason `Recall <runId>`, so the per-event trail links back to the run record.

#### D6.2 CSV export

Written during execute for every matching Completed row (and Published rows when P is in the set). UTF-8 with BOM, RFC 4180 quoting, one header row:

`endpointId, eventId, sessionId, eventTypeId, from, status, enqueuedTimeUtc, updatedAtUtc, lastMessageId, correlationId, recallRunId`

Times are ISO 8601 UTC. No payload columns: the export identifies the copies; payloads stay behind the PII permission (spec 021) in event details. The export is kept with the run (24 hours) and downloaded from `GET …/export`; it is written before any Completed row is deleted.

#### D6 rejected alternatives

- **Reuse `MessageAuditType.PurgeMessages`.** Rejected: recall also skips and exports; a dedicated type can be filtered and switched in Settings → Audit logging.
- **One audit record per endpoint.** Rejected: the issue asks for one record per run.
- **Keep the 4 KB limit and omit `perEndpoint` when it does not fit** (the previous draft). Rejected: the breakdown per endpoint and state is a required part of the record, and once the in-memory run expires it would be lost.
- **Spill the breakdown into extra audit rows or the CSV.** Rejected: the issue asks for one record per run, and the CSV covers completed copies only.
- **Raise `DataTruncationLimit` for every type.** Rejected: changes every existing record's limit (NFR-004 of the audit spec) for a need only recall has.
- **A required (fail-closed) audit before the run.** Rejected: the record must carry counts and outcome; the per-skip rows are already required audits.
- **Include payloads in the CSV.** Rejected: would leak personal data around the PII permission.

## 4. Test plan

CI (`.github/workflows/dotnet.yml`) runs the in-memory, Cosmos DB and SQL Server conformance suites and the unit tests; it has no Service Bus namespace. Service Bus behaviour is tested against fakes in CI, following `tests/NimBus.WebApp.Tests/FakePurgeBus.cs` (existing).

### D7.1 Per phase

| Phase | Tests | Runs in CI | Needs Service Bus |
|---|---|---|---|
| 1 | `RecallMatcherTests` (unit, `tests/NimBus.WebApp.Tests`): every field of D1.2; From case-insensitive equality vs. substring; window bounds inclusive/exclusive; missing `From` with and without `includeMissingFrom`; message types; derived matches for RetryRequest, Deferred-subscription and DLQ messages; `OriginalSessionId` fallback; `Matches(MessageEntity)` on history records, including a `From` filled from `OriginatingFrom` (matches) and `Constants.Self` (missing); exact re-check after the prefix `EventId` / `EndpointId` hints. `RecallFilterValidatorTests`: default set from `GetConsumers`, dynamic forwards with matching and non-matching source, each rejection in D1.4, canonical casing. `BlockedEventRulesStrandedTests` (new, `tests/NimBus.WebApp.Tests`) for `IsStrandedByRemoval`; existing `AdminPurgeSubscriptionRowsTests` stay green. `ResolverServiceTests` (existing file, `tests/NimBus.Resolver.Tests`) gains a case pinning what store discovery relies on: after an `EventRequest` from P and the subscriber's `ResolutionResponse` / `ErrorResponse`, the `EventRequest` history record still has `EndpointId` = subscriber, `From` = P and the request's `EnqueuedTimeUtc`, while the row's `From` is the subscriber. | yes | no |
| 2 | `RecallScannerTests` with a `FakeRecallBus` (new, extends the `FakePurgeBus` pattern to several topics, the `Deferred` subscription and the DLQ) and `InMemoryMessageStore`: counts per endpoint × state, paused flag, `abandonCost`, `historyOnly`, store-unavailable endpoint error. **Selection from history:** a Failed row and a Completed row whose responses came from the subscriber after `enqueuedTo` (row `From` = subscriber, row `EnqueuedTimeUtc` outside the window) are matched through their `EventRequest` history record; a row whose original request is outside the window is not. **Composite ids:** rows are read with `{eventId}_{sessionId}` ids, asserted on a store fake that, like Cosmos DB, returns nothing for a plain event id; `eventIds[]` runs one prefix query per id and drops a longer id sharing the prefix. **Complete scans:** with the page size set to 10, a subscription of 1,000 messages whose only match is the last message is matched; a page failure after retries ends `PreviewIncomplete` with that source's cursor; resume continues from the cursor and reaches `Previewed` without re-reading earlier pages; `tooManyMatches` at the match bound. `RecallRunRegistryTests`: limits, expiry, cancel during preview. `AdminRecallApiTests`: Owner check and access-denied audit, 400/404/409/429 responses (including more than 50 endpoints, resume on a run that is not `PreviewIncomplete`, and execute on a `PreviewIncomplete` run), generated contract round trip. | yes | no |
| 3 | `RecallExecutorTests` with `FakeRecallBus`, `InMemoryMessageStore` and a fake `IOperatorCommands`: step order (parked copies removed before any skip); each D3 row rule including #298 keep and #289 strand; DLQ row kept when its message is still queued; Forbidden/AuditUnavailable stop; per-message and per-endpoint failures; cancellation mid-endpoint; paused-status restore including on exception; a source whose execute walk stops is reported `Incomplete` with no skip sent on that endpoint, and a rerun finishes it; rerun finds nothing new; the job sets an `HttpContext` carrying the captured principal. `RecallAuditTests`: one record per run with the D6.1 fields; `perEndpoint` holds every endpoint with 14 per-state tuples (removals split by `active` / `serviceBusDeferred` / `parked` / `deadLettered`, row deletions under their source state, completed `found` / `acted` / `rowsDeleted`); the worst case built from the D6.1 bounds (50 endpoints of 50 characters, 6-digit counters, 20 event ids) serialises within `RecallDataTruncationLimit` and round-trips without loss; `Recall <runId>` skip reason. `AuditLogServiceTests` (existing file): `RecallMessages` data up to 64 KB is stored whole, every other type is still cut at 4 KB, and the structured log stays capped at 4 KB. `RecallCsvWriterTests`: header, quoting, BOM, UTC times. | yes | no |
| 3 (optional) | `RecallServiceBusLiveTests` (`[TestCategory("LiveServiceBus")]`): one publish to two subscribers, recall removes active, parked and dead-lettered copies on a real namespace. Skips unless `NIMBUS_SB_TEST_CONNECTION` (new) is set. It may point at a live namespace or at the Spec 027 emulator (`src/NimBus.ServiceBusEmulator`); whether the emulator covers sessions, deferral, peek by sequence number and the dead-letter queue well enough is checked when this test is written, and a live namespace is the reference. | skipped | yes — a live namespace or the emulator |
| 4 | Vitest `recall-messages.test.tsx` (new): form validation, default endpoints after choosing event type and publisher, preview table, action counts, typed confirmation (wrong text keeps execute disabled), polling and progress, cancel, CSV link, Resume scan and no execute on a `PreviewIncomplete` run; `operations.test.tsx` (existing) gains a case that `/Operations/recall` renders the card and the other cards are unchanged. | yes (`npm run test:ci`) | no |
| 5 | `RecallCommandTests` (new, `tests/NimBus.CommandLine.Tests`, against a stub `HttpMessageHandler`): option parsing and validation, preview table output, confirmation mismatch exit code 2, execute progress and exit codes, `--export` writes the file, `--json`, token from `AzureCliRunner` when `--token` is absent, Ctrl+C sends cancel, an incomplete preview exits 1 without executing, `--run --resume` calls the resume path. | yes | no |

### D7.2 Storage conformance

This spec adds **no** storage interface member, so no provider, conformance or `InstrumentingMessageTrackingStoreDecorator` (existing, `src/NimBus.OpenTelemetry/Instrumentation/`) change is required. Recall depends on existing behaviour that phase 1 pins down with new conformance cases in `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs` (existing), run for Cosmos DB, SQL Server and in-memory:

- `GetEventsByIds` with **composite** ids (`{eventId}_{sessionId}`) returns rows of every status (Pending, Deferred, Failed, DeadLettered, Completed, Skipped), and only rows of the given endpoint. The case uses composite ids only: Cosmos DB does not resolve plain event ids (its document id is the composite id), and recall never passes plain ids.
- `SearchMessages` with `EndpointId`, `MessageType = EventRequest`, `EventTypeId` (list), `SessionId` and `EnqueuedAtFrom/To` returns, among its results, every seeded `EventRequest` record of that endpoint with its stored `From`, `SessionId` and `EnqueuedTimeUtc` unchanged. The case seeds fewer records than `maxItemCount`, because the in-memory store returns a single page and ignores some hints (recall re-checks every field in process); continuation paging is covered per provider.

If a later change adds a store member for recall (for example `EventFilter.EventIds`), it must be implemented in all three providers, covered by this suite on all three, and forwarded by `InstrumentingMessageTrackingStoreDecorator` with an `InstrumentationConformanceTests` (existing) case.

## 5. Change inventory

| Project | File / path | Change | Phase |
|---|---|---|---|
| NimBus.MessageStore.Abstractions | `src/NimBus.MessageStore.Abstractions/BlockedEventRules.cs` | add `IsStrandedByRemoval` | 1 |
| NimBus.MessageStore.Abstractions | `src/NimBus.MessageStore.Abstractions/MessageAuditEntity.cs` | append `MessageAuditType.RecallMessages` | 2 |
| NimBus.Testing | `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs` | two conformance cases (D7.2) | 1 |
| NimBus.Resolver.Tests | `tests/NimBus.Resolver.Tests/ResolverServiceTests.cs` | one case pinning the `EventRequest` history record (D7.1) | 1 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/Recall/RecallFilterValidator.cs`, `RecallMatcher.cs` | new | 1 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/Recall/RecallScanner.cs`, `RecallRunRegistry.cs`, `RecallJobRunner.cs`, `ServiceCollectionExtensions.cs` | new | 2 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/Recall/RecallExecutor.cs`, `RecallCsvWriter.cs`, `RecallAuditWriter.cs` | new | 3 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/AuditLogService.cs` | `DataLimitFor(MessageAuditType)`, `RecallDataTruncationLimit` (D6.1) | 3 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/AdminService.Recall.cs`, `IAdminService.cs` | new partial; interface members | 2, 3 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/AdminService.Purge.cs` | use `IsStrandedByRemoval` | 1 |
| NimBus.WebApp | `src/NimBus.WebApp/Startup.Web.cs` | `AddNimBusRecall()` | 2 |
| NimBus.WebApp | `src/NimBus.WebApp/api-spec.yaml` | paths `/api/admin/recall/preview`, `/api/admin/recall/runs/{runId}`, `/api/admin/recall/runs/{runId}/preview/resume` (2); `…/execute`, `…/cancel`, `…/export` (3); schemas of D5.1 | 2, 3 |
| NimBus.WebApp | `src/NimBus.WebApp/Controllers/ApiContract/AdminImplementation.cs` | six methods | 2, 3 |
| NimBus.WebApp (generated) | `Controllers/ApiContract.g.cs`, `ClientApp/src/api-client/index.ts` | regenerated by NSwag at build time; never hand-edited | 2, 3 |
| NimBus.WebApp ClientApp | `src/NimBus.WebApp/ClientApp/src/components/admin/recall-messages.tsx`, `recall-messages.test.tsx` | new | 4 |
| NimBus.WebApp ClientApp | `src/NimBus.WebApp/ClientApp/src/models/manage-pages.ts`, `components/admin/operations.tsx`, `pages/operations.test.tsx` | one `OPERATIONS` entry, one `CARDS` entry, one test | 4 |
| NimBus.CommandLine | `src/NimBus.CommandLine/Commands/RecallCommands.cs`, `src/NimBus.CommandLine/RecallApiClient.cs` | new | 5 |
| NimBus.CommandLine | `src/NimBus.CommandLine/CliApplicationFactory.cs` | register `RecallCommands` | 5 |
| NimBus.WebApp.Tests | `tests/NimBus.WebApp.Tests/Recall*Tests.cs`, `FakeRecallBus.cs`, `AdminRecallApiTests.cs` | new | 1–3 |
| NimBus.WebApp.Tests | `tests/NimBus.WebApp.Tests/AuditLogServiceTests.cs` | per-type data limit cases | 3 |
| NimBus.CommandLine.Tests | `tests/NimBus.CommandLine.Tests/RecallCommandTests.cs` | new | 5 |
| docs | `docs/webapp-rest-api.md` (recall paths, phase 2–3), `docs/recall-messages.md` (new, operator runbook, phase 4), `docs/cli.md` (`nb recall`, phase 5) | API, runbook and command reference | 2–5 |

No change to `NimBus.Core`, `NimBus.ServiceBus`, `NimBus.Resolver` (source), the storage providers, the SQL schema or the Service Bus topology.

## 6. Phases

Each phase is one PR, merges independently, and leaves `master` releasable.

### Phase 1 — Selection and rules

Scope: `RecallMatcher`, `RecallFilterValidator`, `BlockedEventRules.IsStrandedByRemoval` (Purge subscription switched to it), the two conformance cases, the Resolver history test.

Acceptance criteria:
1. `RecallMatcher` implements every row of the D1.2 table (Service Bus messages and `EventRequest` history records) and the derived-match rule; `RecallMatcherTests` cover each, including missing `From` with and without `includeMissingFrom`.
2. `RecallFilterValidator` derives the default endpoint set from `IPlatform.GetConsumers` plus matching dynamic forwards and rejects each D1.4 case (including more than 50 endpoints) with a field-level error; tests prove each.
3. The `ResolverServiceTests` case proves the `EventRequest` history record keeps P's `From` and the request time after the subscriber's response.
4. `AdminService.PurgeSubscriptionAsync` uses `IsStrandedByRemoval`; `AdminPurgeSubscriptionRowsTests` pass unchanged.
5. The two conformance cases (composite-id `GetEventsByIds`, `EventRequest` `SearchMessages`) pass on in-memory, Cosmos DB and SQL Server in CI.
6. No API, UI or CLI change.

### Phase 2 — Preview job and API

Scope: `RecallScanner`, `RecallRunRegistry`, `RecallJobRunner`, `AddNimBusRecall`, `POST /api/admin/recall/preview`, `GET /api/admin/recall/runs/{runId}`, `POST /api/admin/recall/runs/{runId}/preview/resume`, schemas `RecallFilter`, `RecallRun`, `RecallProgress`, `RecallPreview`, `RecallEndpointCounts`, `RecallScanState`, `RecallStateCounts`, `RecallCopyState`, `MessageAuditType.RecallMessages` (access-denied records only in this phase).

Acceptance criteria:
1. A preview returns `202` with a run id and reaches `Previewed` with counts per endpoint × state matching a seeded `FakeRecallBus` + in-memory store, including paused, `missingFrom`, `historyOnly` and `abandonCost`.
2. `MessageAuditType.RecallMessages` is appended after `CopyEndpointData`; a non-Owner gets `403` and an access-denied `RecallMessages` audit record. Invalid filters get `400` naming the fields; a fourth concurrent preview gets `429`.
3. Store selection uses the `EventRequest` history record: a Failed and a Completed copy whose responses fall outside the window, with the subscriber as the row's `From`, are counted; rows are read by composite id only.
4. Scans have no total cap: a match behind 1,000 messages at page size 10 is found; a stopped source makes the run `PreviewIncomplete`; resume continues from its cursor; execute on a `PreviewIncomplete` run gets `409`.
5. A preview changes nothing on Service Bus or in the store (asserted on the fakes).
6. `dotnet build -c Release` with NSwag generation enabled succeeds.

### Phase 3 — Execute, cancel, export, audit

Scope: `RecallExecutor`, `RecallCsvWriter`, the execute/cancel/export paths, `RecallActions`, `RecallExecuteRequest`, `RecallResult`, `RecallEndpointResult`, `RecallStateOutcomes`, `RecallStateOutcome`, the per-run `RecallMessages` audit record, and the access-denied audit on the new paths.

Acceptance criteria:
1. Execute requires a fresh, complete (`Previewed`) preview and `confirmation == "recall <n>"`; otherwise `400`/`409`.
2. Steps run in the D2 order per endpoint; a test proves no skip is sent for a session before its parked copies were removed.
3. Every D3 rule holds: blocking rows are never deleted; stranded rows are deleted; DeadLettered rows whose DLQ message remains are kept; history is never deleted.
4. Skips go through `IOperatorCommands.SkipAsync` with reason `Recall <runId>` under the captured principal; `Forbidden`/`AuditUnavailable` stop further skips.
5. Cancellation stops before the next unit of work, restores paused subscription status and still writes the audit record with outcome `Cancelled`.
6. Exactly one `RecallMessages` audit record per executed run with the D6.1 fields, holding per-state counts for every endpoint of the set; the worst-case record from the D6.1 bounds is stored whole, and every other audit type keeps the 4 KB limit.
7. The CSV has the D6.2 header and is written before any Completed row is deleted.
8. A rerun of the same filter after a completed run removes, skips and deletes nothing more.
9. An execute walk that stops reports the endpoint `Incomplete` with its unfinished sources, sends no skip on that endpoint, and a rerun finishes it.

### Phase 4 — WebApp UI

Scope: `recall-messages.tsx`, the `OPERATIONS` and `CARDS` entries, Vitest tests, `docs/recall-messages.md`.

Acceptance criteria:
1. `/Operations/recall` shows the card in the Irreversible group; every existing card renders as before.
2. The preview table shows one row per endpoint and one column per state with totals and badges.
3. Execute stays disabled until at least one action is chosen and the typed text equals `recall <n>`.
4. Progress, per-endpoint outcome (including `Incomplete`), errors, Cancel, Resume scan on a `PreviewIncomplete` run and Download CSV work against mocked API responses; execute is not offered on a `PreviewIncomplete` run.
5. `npm run test:ci` and `npm run build` pass; the PR includes a screenshot (AGENTS.md).

### Phase 5 — CLI

Scope: `nb recall`, `RecallApiClient`, CLI reference docs.

Acceptance criteria:
1. Options and exit codes behave as in D5.4; `RecallCommandTests` cover preview, execute, confirmation mismatch, an incomplete preview (no execute, exit 1), `--run --resume`, `--export`, `--json` and cancellation on Ctrl+C.
2. Without `--token`, the command obtains a token through `AzureCliRunner`.
3. The command makes no Service Bus or store connection (asserted by constructing it without those options).

## 7. Out of scope

- Erasing personal data from history and payloads (#291). Recall deletes tracking rows only and never history; where a team needs erasure, it follows #291.
- Changing existing Operations cards (Purge subscription, Skip messages, Delete by status, Delete dead-lettered). The only shared change is moving Purge subscription's stranded-row predicate into `BlockedEventRules` without changing its behaviour.
- The kill switches of runbook step 1; they already exist and are audited.
- Correcting the data in target systems; recall only exports the list of completed copies.
- Recalling messages still on the publisher's own topic or on forward subscriptions: forwarding is immediate, so nothing rests there.
