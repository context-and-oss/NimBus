# Spec 042: Recall wrongly published messages

Status: proposed (2026-11-04; extended 2026-11-15 with store selection from `EventRequest` history, complete paged scans, the skip guard for failed removals, the audit enum position, the test plan, change inventory and phases). Issue: context-and-oss/NimBus#290, part of the Operations cleanup gap analysis (#292). #291 (erase one event's data everywhere) builds on the selection defined here.

Related: #284 (audit Operations cleanup actions), #286 (DLQ delete), #287 (bulk Skip unblocks sessions), #289 (purge leaves Pending rows, closed by #305), #295 (Delete dead-lettered also clears the DLQ), #298 (keep the event that blocks its session).

This is the one authoritative Recall spec. A second draft for the same issue (`docs/spec/042-recall-messages/`, from AgentFactory task AF-198) was folded into it and removed. That draft's diverging decisions are recorded under [Rejected alternatives](#rejected-alternatives). The number 042 stays with this folder, which master has used since #315.

Conventions: **(new)** marks a type, member, path or constant this spec proposes. Everything else named in code font exists on `master`. Paths are relative to the repository root.

## Problem

An integration publishes messages it shouldn't have: event type X, from publisher P, between T1 and T2. Every endpoint that consumes X from P received its own copy. An operator has to:

1. stop P from publishing more;
2. remove the copies no subscriber has processed yet, wherever they wait;
3. skip the copies that failed, so their sessions continue;
4. list the copies that were already processed, so the data can be corrected in the target systems.

Operations has no way to do steps 2–4 across endpoints:

- **Purge subscription** drains one subscription by state and "enqueued before". It has no event-type, sender or lower-bound filter, so it removes good messages along with bad ones.
- **Skip messages** works on one endpoint, by status and "updated before".
- **Endpoint → Events** filters by event type, sender and enqueued window, but it works one endpoint and one page (100 rows) at a time, and it can't remove anything from Service Bus.

At volume (#292 scenario 2: a replayed full sync of about 50,000 messages) none of this is usable.

## Decisions

Taken with the user on 2026-11-04. On 2026-11-15 the user confirmed that this document's decisions win over the AF-198 draft.

| Topic | Decision |
|---|---|
| Rows of removed copies | **Deleted**, as Purge subscription does since #305. The pre-run export (see Preview) keeps a record of what was recalled. |
| Operations group | **Cleanup**. |
| Payload filter | **Not in v1.** |
| Delivery-count safety limit | **Overridable per run** within a hard floor (see Session ordering). |
| Execution model | **Client-driven batches**, like Reconcile stale pending (spec 032). No server-side background job. |
| CLI / MCP | **Not in v1.** |
| Audit | One run record, plus one record per completed endpoint. |

## Goals

- Select copies by **event type(s) + sender + enqueued window**, optionally narrowed by a session id prefix.
- Show a preview: per subscribing endpoint, where the matching copies are and what will happen to each.
- Remove the unprocessed copies from Service Bus: the endpoint subscription, the `Deferred` subscription and the dead-letter queue. Delete their stored rows.
- Skip the failed copies through the existing operator command, so the endpoint itself unblocks their sessions.
- Export the copies that were already processed, as CSV.
- Keep session ordering intact. A recalled copy must never be processed because it was replayed after a block was released.
- Scale to tens of thousands of copies without one long HTTP request, and never leave a matching copy unexamined because a subscription is large.

## Non-goals

- Payload-content matching (#292 scenario 4, a "payload marker"), on stored rows or on Service Bus copies. It can come later as an extra filter.
- Erasing data: deleting message history and audits. That is #291, which reuses this selection.
- Undoing processed copies. NimBus can't reverse what a handler did; it lists those copies.
- A CLI or MCP counterpart. Both can follow once the service exists, as clients of the API below.
- Changing the existing Operations cards (Purge subscription, Skip messages, Delete by status, Delete dead-lettered). The only shared change is a refactor of Purge's stranded-row predicate (see Stored rows), with no behaviour change.

## Selection

The request carries a `RecallSelection` (new):

| Field | Required | Meaning |
|---|---|---|
| `eventTypeIds[]` | yes, 1–20 | Event type ids. Exact, ordinal. |
| `from` | yes | Publisher P. Exact, case-insensitive. |
| `enqueuedFrom` | yes | UTC, inclusive. |
| `enqueuedTo` | yes | UTC, exclusive. After `enqueuedFrom`; at most 31 days later. |
| `sessionIdPrefix` | no | Case-insensitive prefix of the session id. |

Both bounds of the window are required. A window whose end is in the future is refused while P's topic is send-enabled: the preview offers the kill switch instead (see Preview). An event type id that is not in the platform (`IPlatform.EventTypes`) or in a dynamic forward (`IPlatform.DynamicForwards`), or a `from` that is not an endpoint, is rejected with `400 ValidationProblemDetails` naming the field.

The window and the publisher always describe the **original `EventRequest`** a consumer received, never a later message about the event (see Which copies, per endpoint).

## How copies are found

### Which endpoints

A publish from P reaches a consumer through a forwarder subscription on P's topic. That subscription forwards to the consumer's topic with a rule per event type (`TopologyDescriptor.ConsumerForwarderSubscription`). It may also be a dynamic forward (`AgentDyn-<consumer>`, `TopologyDescriptor.DynamicForwardPrefix`, spec 022). The forward action sets `From = P`, `To = <consumer>` and a **new `EventId` per copy** (`TopologyDescriptor.ForwardAction`), so copies on different endpoints share no event id.

The consuming endpoints are therefore read from P's topic on the bus, not only from the compiled platform. They are the forwarder subscriptions with a rule whose filter starts with `ForwardFilter(X)`: plain, or keyed by routing key (`KeyedForwardFilter`, `UnmatchedForwardFilter`, spec 045). Their `ForwardTo` names the consumer. Reading the bus also covers dynamic forwards. The compiled platform's consumers of X (`IPlatform.GetConsumers`) are added too, so a consumer whose forwarder was deleted still shows up, with a warning.

### Which copies, per endpoint

A copy is matched in two ways, and both are needed.

**1. Store: the endpoint's `EventRequest` history records.** The tracking row (`UnresolvedEvent`) can't be used for selection. `ResolverService.CreateUnresolvedEvent` copies `From` and `EnqueuedTimeUtc` from the latest message about the event. For a Failed or Completed row, that is the subscriber's `ErrorResponse` or `ResolutionResponse`: its `From` is the subscriber and its time is when the response was sent. A row filter on `From = P` and the window would miss exactly the copies the recall must skip and export, and their requests have already left Service Bus.

The Resolver stores every message as a `MessageEntity` before it updates the row. For an `EventRequest`, `ResolverService.CreateMessageEntity` records `EndpointId` = the consumer (`To`), `From` = P and the request's `EnqueuedTimeUtc`. That record never changes. Per endpoint E the recall:

1. Pages `IMessageTrackingStore.SearchMessages(new MessageFilter { EndpointId = E, MessageType = EventRequest, EventTypeId = [X…], SessionId = prefix, EnqueuedAtFrom = T1, EnqueuedAtTo = T2 }, token, 250)` until the continuation token is null. Every filter member is only a hint. Providers differ: `EndpointId`, `EventId` and `SessionId` are case-insensitive prefixes; `From` is a substring on Cosmos DB; Cosmos DB widens times to whole seconds (`CosmosDateBounds`); and `InMemoryMessageStore` ignores `EventTypeId`, `MessageType` and the window. So every record is re-checked in process: exact `EndpointId`, `MessageType == EventRequest`, `EventTypeId` ∈ X (ordinal), `From` equal to P (case-insensitive), `T1 <= EnqueuedTimeUtc < T2`, and the session prefix. `From` isn't passed as a hint, because of its substring semantics. The records that pass are the endpoint's **original copies**.
2. Reads each original copy's row with `GetEventsByIds(E, ids)` in chunks of 50. Each id is the composite `{eventId}_{sessionId}`, the only form every provider resolves: Cosmos DB uses it as the document id and partition key, and returns nothing for a bare event id.
3. Classifies each row by its **current** `ResolutionStatus` and `PendingSubStatus` (see What happens to each copy), whatever the row's own `From` or `EnqueuedTimeUtc` say. A Failed or Completed copy whose response arrived after T2 is still matched, because its request was enqueued inside the window.

An original copy without a row is counted as *history only* (nothing to act on). A row whose `EventRequest` record was deleted (Endpoint → Delete all events, `PurgeStoredMessages`) can't be found this way. It is still found when its message is on Service Bus (rule 2). The preview states this limit.

**2. Service Bus messages** match when either:

- the `EventId` application property is in the endpoint's **matched event set**: the event ids of the original copies from (1) plus those of messages matched by the next rule. This catches retries, resubmissions and parked copies, whose `From` and enqueued time differ. The message's `EventTypeId` must also be in X; or
- `EventTypeId` ∈ X, `From` = P, `MessageType` = `EventRequest`, and the broker enqueued time is in [T1, T2). This catches copies the Resolver hasn't recorded yet, because rows lag the bus. A message without a `From` property (`MessageHelper` omits it when empty) doesn't match this rule.

Only `EventRequest`, `RetryRequest` and `ResubmissionRequest` messages are removed. Control messages (`SkipRequest`, `HandoffCompletedRequest`, `HandoffFailedRequest`, `ContinuationRequest`, `ProcessDeferredRequest`) never are: removing a queued `SkipRequest` would leave its session blocked.

Parked copies keep `EventId`, `EventTypeId` and `SessionId` (`ResponseService.SendToDeferredSubscription`; `OriginalSessionId` is used when present), so the first rule applies on the `Deferred` subscription as well. Their enqueued time is the parking time, so the second rule is not used there.

### Scanning is complete, in bounded pages

No scan has a total limit. A cap would leave every match behind it unexamined: narrowing the window doesn't shrink the messages in front of a match, and a rerun meets the same cap. The existing `SubscriptionAdminService.CompleteMatchingAsync` and `PeekMatchingAsync` stop at their private 100,000-message `MatchScanSnapshotLimit`, so the recall doesn't reuse them.

- Each source of an endpoint is walked in pages of 250 (`RecallScanPageSize`, new): history by continuation token; the subscription `<E>/<E>`, its dead-letter queue, `<E>/Deferred`, and the P-side forwarder dead-letter queues by `PeekMessagesAsync(250, fromSequenceNumber)`, advancing past the last peeked sequence number until a page is empty. On session-enabled subscriptions a plain receiver peeks across sessions, as `SubscriptionAdminService` already does before it accepts a session.
- The walk position is an opaque **continuation token** (new) that the server returns and the client sends back. It encodes the source and the next sequence number, or the history token. Only matches are kept between pages (event id, session id, sequence number, state), never bodies.
- A source is **complete** only when its walk reaches an empty page or a null history token. A page that fails three times with backoff stops the call with an error; the client retries the same token. Neither preview nor execute ever reports a partial walk as complete.
- History pages depend on providers paginating `SearchMessages`. Cosmos DB and SQL Server do. `InMemoryMessageStore.SearchMessages` today takes `maxItemCount` results and returns no continuation token, so a match behind the first page would be silently missed. Phase 1 gives it real offset paging (see Change inventory) and adds a multi-page conformance test.

## What happens to each copy

| Where the copy is | Stored status | Action |
|---|---|---|
| Queued on `<E>/<E>` (active or Service Bus–deferred) | Pending, TooManyRequests | Remove the message. Delete the row. |
| Parked on `<E>/Deferred` | Deferred | Remove the message, then fix the session state (below). Delete the row. |
| `<E>/<E>/$deadletterqueue` | DeadLettered | Remove the message. Skip the row through the operator command. |
| Not on the bus: the handler failed | Failed, Unsupported | Skip through the operator command. |
| Handed off (`PendingSubStatus = Handoff`) | Pending | **Leave it.** An external job owns it; list it under "needs manual action". |
| Processed | Completed | List and export. |
| Already Skipped | Skipped | Nothing. Counted. |
| Message on the bus with no row | — | Remove the message. Counted as "unrecorded". |

The P side is scanned too: a forward that failed leaves the message in `<P>/<consumer>/$deadletterqueue`. Matching messages there are removed.

The run is all-or-nothing per endpoint: there are no per-action opt-ins. The preview and the pre-run export are the operator's checkpoint.

## Session ordering

The endpoint enforces ordering per session: a failed event blocks the session, later events are parked on the `Deferred` subscription, and a Skip of the blocking event replays them (`StrictMessageHandler.HandleSkipRequest` → `ContinueWithAnyDeferredMessages`). Two rules follow:

1. **Remove parked copies before releasing a block.** If the blocking copy were skipped first, the replay would deliver the other recalled copies parked behind it to the handler.
2. **Keep the session state consistent with what is left.** Removing parked messages behind the endpoint's back leaves `DeferredCount` too high. The recall corrects it under the session lock, the same way Operations → Session state (#312, `SessionStateService`) changes state.

Per affected session on endpoint E, in this order:

1. Accept the session on `<E>/<E>`. This pauses the endpoint for that session only. If a receiver holds it, the session is reported as *busy* and retried in a later batch. Nothing in it is touched.
2. Accept the session on `<E>/Deferred`. If the DeferredProcessor holds it (a replay is running), release both locks and report *busy*.
3. Peek the parked messages. Receive and complete the matching ones, up to the last match.
4. Peek the queued messages on `<E>/<E>`. Receive and complete the matching ones, up to the last match.
5. **Verify.** Peek both subscriptions for the session again. The session is *cleared* only if every matching parked and queued copy was completed and none is left. If any complete failed, or a match is still there, the session is *removal incomplete*: steps 6 and 7 still run for what was removed, but step 8 is not.
6. Rewrite the session state under the lock:
   - `DeferredCount -= removedParked`, floored at 0;
   - if nothing is left on the `Deferred` subscription for the session, call `SessionState.MarkAllReplayed()`, which keeps the block.

   If the rewrite fails, the session is *removal incomplete* too.
7. Release both locks.
8. Only for a *cleared* session: if it is blocked (`SessionState.BlockedByEventId`) by a copy this recall selected, send the operator Skip for it (`IOperatorCommands.SkipAsync`). The endpoint unblocks the session and replays what is still parked, which by now is only messages the recall didn't select. A block by an event the recall didn't select is left alone and listed. A *removal incomplete* session keeps its block, so the bad copy still parked can't replay. It is listed and retried by a rerun.
9. Delete the stored rows of the removed copies (see Stored rows).

Failed and Unsupported copies whose session has nothing parked or queued are skipped without accepting the session: the Skip is the endpoint's own command and takes the session lock itself.

**Receiving is not selective.** Service Bus can't receive an active message by sequence number. To reach a match, the recall receives everything before it in the session, completes the matches and abandons the rest, which costs each abandoned message one delivery. Two mitigations:

- The recall stops at the last match, so messages after it aren't touched.
- Before receiving, the peek shows each message's delivery count. A session is *at risk* when any message that would be abandoned has a delivery count at or above the run's **at-risk threshold**. An at-risk session isn't touched; it's reported for the operator to handle with Session state or Session purge.

The at-risk threshold defaults to `Constants.ServiceBusMaxDeliveryCount - 2` (8, with the limit of 10). The operator can change it per run, between 1 and `ServiceBusMaxDeliveryCount - 1`.

- The threshold can't go above `ServiceBusMaxDeliveryCount - 1`. A message one delivery from the limit therefore always makes its session at risk, whatever the override. Abandoning that message would dead-letter it, which would block its session on a good event.
- The override is recorded in the run's audit record.

The preview shows how many non-matching messages the run will touch, and how many sessions are at risk at the chosen threshold.

## Stored rows

Rows are deleted with `IMessageTrackingStore.RemoveMessage(eventId, sessionId, endpointId)`, which removes the tracking row only. Message history (`MessageEntity` records) and audits are always kept; erasing them is #291.

| State | Tracking row |
|---|---|
| Queued copy removed (Pending, not Handoff; TooManyRequests) | Deleted. |
| Parked copy removed (Deferred) | Deleted. |
| Dead-lettered copy removed | Kept, and becomes Skipped through the operator command. |
| Failed, Unsupported | Kept, and becomes Skipped through the operator command. The Skip is what releases the session. |
| Pending + Handoff | Kept, left alone. |
| Pending whose message wasn't found (in flight) | Kept; it will settle. Counted. |
| Completed, Skipped | Kept. |

The rule is the one Purge subscription adopted for #289 in #305, so the two share one definition: the private `AdminService.IsLeftByPurge` moves to `BlockedEventRules.IsStrandedByRemoval(UnresolvedEvent)` (new, public, same body). A row is deleted only when it is Deferred, or Pending (or TooManyRequests) and not `BlockedEventRules.BlocksItsSession(row)`, and this run completed its message. Before deleting, the row is re-read with `GetEvent(endpointId, eventId)` and the rule is applied to the fresh row, so a row that moved on during the run (for example to Failed) is kept. A row that blocks its session (#298) is never deleted.

Deleted rows disappear from Endpoint → Events and from the event's history view. The record of what was recalled is therefore:

- the **pre-run export** (see Preview), which the card asks for before the run;
- the audit records, which carry the selection and the counts.

**A late Resolver write can re-create a deleted row.** The Resolver records a copy from its own fan-out subscription, so its Pending write can land after the recall deleted the row. The stale-write guard (spec 030) can't stop this, because there is no row left to compare against. Purge subscription has the same exposure.

The recall closes it with a **final pass**. After the last batch it re-runs the selection on each endpoint. It deletes every selected Pending or Deferred row whose copy is no longer on the bus, unless that session was reported busy or removal incomplete. With P's sending disabled and the window closed, every such row is a leftover of the recall.

Deleting needs no new store member: `RemoveMessage` exists on every provider.

## Preview

`POST /api/v1/admin/recall/preview` (new) takes the selection, an optional `endpointId`, an optional continuation token and the at-risk threshold.

- **Without `endpointId`** it returns the endpoint set (see Which endpoints) and the warnings. It doesn't scan.
- **With `endpointId`** it walks that endpoint's sources from the token for at most 20 pages, and returns that slice's counts, the session ids with matches in the slice, and the next token. The client calls again until the token is null, adds up the counts, and unions the session ids to count distinct sessions. An endpoint's preview is **complete** only when its token is null.

Per consuming endpoint, the preview shows:

- counts by location: queued, parked, dead-lettered, unrecorded, P-side dead-lettered;
- counts by stored status: Pending, Deferred, Failed, DeadLettered, Unsupported, Handoff, Completed, Skipped, and *history only*;
- the sessions it will touch, how many it will unblock, how many are blocked by an event outside the recall, and how many are *at risk*;
- the number of non-matching messages that would be received and abandoned;
- whether the scan is complete.

The preview also offers the **pre-run export**: every stored row the selection matches, in every status. Since the run deletes rows, this is the operator's record of what was recalled.

Warnings:

- P's topic is send-enabled. The preview offers the existing kill switch (Operations → Endpoints, send disable) and refuses a window that ends in the future until sending is disabled.
- A consumer of X in the compiled platform has no forwarder on the bus.
- The P-side forwarder dead-letter queues hold matching messages.
- Rows whose `EventRequest` history was deleted can only be found while their message is on the bus.

The card keeps **Run** disabled until every endpoint's preview is complete. The server doesn't depend on that: execute walks every source to the end itself (see Execution).

## Execution

A recall runs as **client-driven batches**, like Reconcile stale pending (spec 032).

- `POST /api/v1/admin/recall/execute` (new) takes the selection, an endpoint, an optional `recallId` and continuation token, a batch size (default 50 sessions, at most 200) and the at-risk threshold (default `ServiceBusMaxDeliveryCount - 2`, allowed 1 to `ServiceBusMaxDeliveryCount - 1`).
- Each call walks the endpoint's sources from the token in pages until it has collected the batch's sessions, or until it has read 20 pages. It processes those sessions as in Session ordering, skips their Failed and Unsupported copies, and deletes their rows. It returns counts, busy, at-risk and removal-incomplete sessions, errors and the next token. A null token means every source of the endpoint was walked to the end.
- The first call (no `recallId`) creates the recall id and writes the run audit record. If that record can't be written, the call is refused.
- Every batch re-runs the selection on the sessions it touches, so a reload, a retry or a second operator can't double-process. A copy that is already removed no longer matches, a deleted row is gone, and a row that is already Skipped is counted, not touched. A Skip is sent only for a row that `IOperatorCommands.FindCurrentAsync` still returns as Failed, DeadLettered or Unsupported.
- **Partial failure.** A failed complete, a Skip that isn't accepted, or a failed row delete is counted for its state and listed with the event id. The batch continues. A Skip result of `Forbidden` or `AuditUnavailable` stops all further Skips in the call, as `AdminService.SkipMessagesAsync` does. A page that can't be read fails the call with an error, and the client may retry it with the same token. A session reported busy or removal incomplete is listed and retried in the final pass.
- **Cancellation.** The card loops through the endpoints and batches, shows progress, and can be stopped between batches. Stopping between batches leaves no half-processed session, because a session is processed whole within one call. A stopped run's endpoints are reported as *incomplete* in the result, and their audit records say so.
- A **final pass** per endpoint (`finalPass: true` on the last call) retries busy and removal-incomplete sessions. It also deletes leftover rows that a late Resolver write re-created (see Stored rows).
- **Rerun safety.** Rerunning the same selection is safe and is how a stopped or incomplete run is finished. Removed messages are gone, the history records still select the copies already handled, their current status decides that nothing more is done, and a Skip is never sent twice for the same row.

`GET /api/v1/admin/recall/export?…selection…&statuses=…` (new) streams the matching stored rows as CSV:

- With no statuses it exports every status; this is the pre-run export.
- After the run, `statuses=Completed,Pending` exports the processed copies and the Handoff rows, which need correcting in the target systems.
- Columns: `endpointId, eventId, sessionId, eventTypeId, from, status, enqueuedTimeUtc, updatedAtUtc, correlationId, webAppLink`. `from` and `enqueuedTimeUtc` come from the `EventRequest` history record, so they are P and the request time, not the response's. UTF-8, RFC 4180 quoting, ISO 8601 UTC times.
- A `payload` column is included only for callers with `PiiReader`.

All paths use the `/api/v1/` prefix that every `api-spec.yaml` path has used since #363. They are tagged `Admin`, require the site Owner role like every Operations action (`AdminImplementation.IsSiteOwnerAsync`), and return `403`, audited as access denied, otherwise.

| Method | Path | operationId | Request | Response |
|---|---|---|---|---|
| POST | `/api/v1/admin/recall/preview` | `post-admin-recall-preview` | `RecallPreviewRequest` | `200 RecallPreviewResult`; `400 ValidationProblemDetails`; `403` |
| POST | `/api/v1/admin/recall/execute` | `post-admin-recall-execute` | `RecallExecuteRequest` | `200 RecallBatchResult`; `400`; `403`; `409` when the run audit record can't be written |
| GET | `/api/v1/admin/recall/export` | `get-admin-recall-export` | query: the selection, `endpointId`, `statuses` | `200 text/csv`; `400`; `403` |

Schemas (all new): `RecallSelection`; `RecallPreviewRequest` (`selection`, `endpointId`, `continuationToken`, `atRiskThreshold`); `RecallPreviewResult` (`endpoints[]`, `warnings[]`, `endpointCounts: RecallEndpointCounts`, `affectedSessionIds[]`, `continuationToken`); `RecallEndpointCounts` (the location and status counts above, `abandonCost`, `atRiskSessions`, `blockedByOther`); `RecallExecuteRequest` (`selection`, `endpointId`, `recallId`, `continuationToken`, `batchSize`, `atRiskThreshold`, `finalPass`); `RecallBatchResult` (`recallId`, `counts: RecallEndpointCounts`, `rowsDeleted`, `skipsSent`, `busySessions[]`, `atRiskSessions[]`, `removalIncompleteSessions[]`, `blockedByOtherSessions[]`, `errors[]`, `continuationToken`). Generated code (`Controllers/ApiContract.g.cs`, `ClientApp/src/api-client/index.ts`) comes from NSwag at build time and is never hand-edited.

## Audit

- A new `MessageAuditType.RecallMessages` (new), **appended after the current last member, `UpdateEndpointMetadata`** (value 45), so it gets the value 46. Cosmos DB persists the numeric value, so inserting it anywhere else would shift `ChangeSessionState`, `DashboardTokenCreated`, `DashboardTokenRevoked`, `DashboardTokenRemoved` and `UpdateEndpointMetadata` and misclassify existing records. If another member is appended first, `RecallMessages` still goes last. The existing enum-position test in `tests/NimBus.WebApp.Tests/AdminCleanupAuditTests.cs` is extended to pin every existing value and the new one.
- It's added to both api-spec audit enums and to `AuditSettingsProvider.AlwaysRecorded`.
- One record per recall. It's written before the first batch and carries the selection and the recall id, and the at-risk threshold when it differs from the default.
- One record per completed endpoint, with its counts by location and stored status, rows deleted, Skips sent, and the busy, at-risk and removal-incomplete session counts, plus whether the endpoint was walked to the end. Deleted event ids aren't listed, because there can be tens of thousands; the pre-run export is that list. The record fits within `AuditLogService`'s 4 KB data limit: it holds about 20 counters and one endpoint id.
- A denied attempt is recorded as access denied.
- Payloads are never written to the audit.
- Each Skip also writes its own `Skip` audit row through `IOperatorCommands.SkipAsync`, with the reason `Recall <recallId>` (`OperatorCommandContext`), so the per-event trail links back to the run.

## UI

Operations gets **Recall messages** in the *Cleanup* group (`cleanup` in `OPERATION_GROUPS`). It is Site Owner only, like every Operations action.

1. **Selection:** event types (from the platform, multi-select), publisher, enqueued from/to (both required, UTC shown), optional session prefix. An advanced at-risk threshold field shows the default.
2. **Preview:** a table per endpoint with the counts above, a scan-progress indicator per endpoint, the warnings, and a link to Endpoint → Events pre-filtered with the same selection. A *Download recalled copies* button gives the pre-run export. The Run button stays disabled until every endpoint's scan is complete and the export has been downloaded or explicitly skipped.
3. **Run:** typed confirmation of the publisher id (`ConfirmDestructiveAction`), then a progress bar per endpoint, then the result with busy, at-risk, removal-incomplete and blocked-by-other sessions listed, each linked to Operations → Session state.
4. **Export:** the processed-copies CSV.

The run deletes rows and removes messages, so the card warns that it can't be undone.

## Testing

CI (`.github/workflows/dotnet.yml`) runs the unit tests, the conformance suite on in-memory, Cosmos DB and SQL Server, and `[TestCategory("Emulator")]` tests on the Service Bus emulator (spec 027). No test needs a live Service Bus namespace.

| Phase | Tests | Where | Needs Service Bus |
|---|---|---|---|
| 1 | **Unit** (`tests/NimBus.WebApp.Tests`, new `RecallMatcherTests`): exact `From`, `EventTypeId` and `EndpointId` re-checks after prefix and substring hints (`Billing` doesn't match `BillingV2`); window bounds inclusive/exclusive; both Service Bus match rules; `OriginalSessionId` on parked copies; control messages never match; a message without `From` doesn't match the property rule. `BlockedEventRulesStrandedTests` (new) for `IsStrandedByRemoval`; the existing `AdminPurgeSubscriptionRowsTests` stay green unchanged. `ResolverServiceTests` (existing, `tests/NimBus.Resolver.Tests`) gains a case: after P's `EventRequest` and the subscriber's `ErrorResponse`, the `EventRequest` history record still has `EndpointId` = subscriber, `From` = P and the request time, while the row's `From` is the subscriber. | CI | no |
| 1 | **Conformance** (`src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`, all three providers): `SearchMessages` pagination: seed 30 `EventRequest` records for one endpoint, page with `maxItemCount: 10`, follow continuation tokens to null, and assert that every record, including the oldest, comes back exactly once. `GetEventsByIds` with composite ids returns rows of every status for that endpoint only. | CI (Cosmos DB and SQL Server live) | no |
| 2 | **Unit** (new `RecallScannerTests`, with `FakeRecallBus` (new, extends `FakePurgeBus` to several topics, the `Deferred` subscription and the dead-letter queues) and `InMemoryMessageStore`): a Failed and a Completed copy whose responses came from the subscriber after T2 are matched through history; a copy whose request is outside the window isn't. A history match behind 300 newer records (more than one 250-record page) is found. A bus match behind 1,000 messages is found with the page size set to 10, and the token is null only after the last page. Endpoint discovery from forwarder rules, including keyed rules and `AgentDyn-` forwards, and the missing-forwarder warning. **API** (`AdminRecallApiTests`, new): Owner check and access-denied audit, validation (both window bounds, a future end while send-enabled, unknown event type or publisher), a preview changes nothing. | CI | no |
| 3 | **Unit** (new `RecallExecutorTests`, `FakeRecallBus`, `InMemoryMessageStore`, a fake `IOperatorCommands`): a parked copy is removed before the Skip of its blocking copy is sent. **A parked completion that fails, with the scan still completing, sends no Skip for that session, which is reported removal incomplete; a rerun that removes it then sends the Skip.** Session state after removal: `DeferredCount` corrected, and `MarkAllReplayed` only when nothing is parked. Handoff rows are left alone; rows of removed copies are deleted and those of skipped copies become Skipped; a blocking row is never deleted. At-risk sessions aren't touched, the override is honoured, and it can't go above `ServiceBusMaxDeliveryCount - 1`. The final pass deletes a Pending row re-created after its copy was removed. Idempotent reruns send no second Skip. `Forbidden` stops further Skips. A refused run audit runs nothing. **Audit**: `AdminCleanupAuditTests` pins every existing `MessageAuditType` value, `RecallMessages == 46` and that it's the maximum; one run record and one record per endpoint, each under 4 KB in the worst case. CSV header, quoting and history-sourced `from`/`enqueuedTimeUtc`. | CI | no |
| 3 | **Emulator** (`RecallEmulatorTests`, new, `[TestCategory("Emulator")]`, on the simulation fixture): P publishes good and bad events to two consumers; a bad event fails on one session, with good and bad events parked behind it; the recall removes the bad copies, unblocks the session, and only the good events are handled, in order; the processed bad copies appear in the export. | CI (emulator) | emulator |
| 4 | **ClientApp** (Vitest `recall-messages.test.tsx`, new): the selection form, the preview loop and summing over continuation tokens, Run disabled until every scan is complete and the export is handled, the typed confirmation, the batch loop, stop and resume, the export link. `pages/operations.test.tsx` gains a case that `/Operations/recall` renders the card in the Cleanup group and the other cards are unchanged. | CI (`npm run test:ci`) | no |

No CLI tests: a CLI is a non-goal.

**Storage conformance rule.** This spec adds no storage interface member, so no provider gains a member and `InstrumentingMessageTrackingStoreDecorator` needs no new forward. The in-memory change is a behaviour fix to an existing member, covered by the new conformance case on all three providers. If a later change adds a store member for recall (for example a list of event ids on `MessageFilter`), it must be implemented on Cosmos DB, SQL Server and in-memory, covered by this suite on all three, and forwarded by the decorator with an `InstrumentationConformanceTests` case.

## Change inventory

| Project | File / path | Change | Phase |
|---|---|---|---|
| NimBus.Testing | `src/NimBus.Testing/Conformance/InMemoryMessageStore.cs` | `SearchMessages` honours `continuationToken`: a stable order (`EnqueuedTimeUtc` descending, then `MessageId` ordinal) and offset tokens, as `GetFailedEventsAcrossEndpoints` does | 1 |
| NimBus.Testing | `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs` | multi-page `SearchMessages` case; composite-id `GetEventsByIds` case | 1 |
| NimBus.MessageStore.Abstractions | `src/NimBus.MessageStore.Abstractions/BlockedEventRules.cs` | add `IsStrandedByRemoval` | 1 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/AdminService.Purge.cs` | use `IsStrandedByRemoval`; no behaviour change | 1 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/Recall/RecallMatcher.cs`, `RecallSelectionValidator.cs` | new | 1 |
| NimBus.Resolver.Tests | `tests/NimBus.Resolver.Tests/ResolverServiceTests.cs` | history-record case | 1 |
| NimBus.WebApp.Tests | `tests/NimBus.WebApp.Tests/RecallMatcherTests.cs`, `BlockedEventRulesStrandedTests.cs` | new | 1 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/Recall/RecallEndpointResolver.cs`, `RecallScanner.cs`, `RecallContinuationToken.cs` | new | 2 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/AdminService.Recall.cs` (new partial), `IAdminService.cs` | preview (2); execute, export (3) | 2, 3 |
| NimBus.WebApp | `src/NimBus.WebApp/api-spec.yaml` | `POST /api/v1/admin/recall/preview` and its schemas (2); `POST /api/v1/admin/recall/execute`, `GET /api/v1/admin/recall/export` and their schemas (3); `RecallMessages` in both audit enums (2) | 2, 3 |
| NimBus.WebApp | `src/NimBus.WebApp/Controllers/ApiContract/AdminImplementation.cs` | `PostAdminRecallPreviewAsync` (2); `PostAdminRecallExecuteAsync`, `GetAdminRecallExportAsync` (3) | 2, 3 |
| NimBus.MessageStore.Abstractions | `src/NimBus.MessageStore.Abstractions/MessageAuditEntity.cs` | append `MessageAuditType.RecallMessages` after `UpdateEndpointMetadata` | 2 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/AuditSettingsProvider.cs` | `RecallMessages` in `AlwaysRecorded` | 2 |
| NimBus.WebApp | `src/NimBus.WebApp/Services/Recall/RecallExecutor.cs`, `RecallCsvWriter.cs` | new | 3 |
| NimBus.WebApp.Tests | `tests/NimBus.WebApp.Tests/FakeRecallBus.cs`, `RecallScannerTests.cs`, `AdminRecallApiTests.cs` (2); `RecallExecutorTests.cs`, `RecallEmulatorTests.cs` (3); `AdminCleanupAuditTests.cs` (enum positions, 2) | new / extended | 2, 3 |
| NimBus.WebApp ClientApp | `src/NimBus.WebApp/ClientApp/src/components/admin/recall-messages.tsx`, `recall-messages.test.tsx` | new | 4 |
| NimBus.WebApp ClientApp | `src/NimBus.WebApp/ClientApp/src/models/manage-pages.ts` (one `OPERATIONS` entry, group `cleanup`), `components/admin/operations.tsx` (one `CARDS` entry), `pages/operations.test.tsx` | one entry each, one test | 4 |
| docs | `docs/webapp-rest-api.md` (recall paths, 2–3); `docs/recall-messages.md` (new operator runbook, 4, added to `website/sidebars.js`) | reference and runbook | 2–4 |

No change to `NimBus.Core`, `NimBus.ServiceBus`, the Resolver's source, the Cosmos DB or SQL Server providers, the SQL schema or the Service Bus topology. If the multi-page conformance case exposes a paging bug in a live provider, phase 1 fixes it in that provider.

## Phases

Each phase is one PR, merges independently and leaves `master` releasable.

### Phase 1: selection rules and store paging

Acceptance criteria:

1. `InMemoryMessageStore.SearchMessages` returns a continuation token while more records remain, and the new multi-page conformance case passes on in-memory, Cosmos DB and SQL Server in CI.
2. The composite-id `GetEventsByIds` conformance case passes on all three providers.
3. `RecallMatcher` implements both Service Bus match rules and the in-process history re-check; `RecallMatcherTests` cover each row of Which copies, per endpoint.
4. The `ResolverServiceTests` case proves the `EventRequest` history record keeps P's `From` and the request time after the subscriber's response.
5. `AdminService.PurgeSubscriptionAsync` uses `BlockedEventRules.IsStrandedByRemoval`, and `AdminPurgeSubscriptionRowsTests` pass unchanged.
6. No API or UI change.

### Phase 2: preview API

Acceptance criteria:

1. `POST /api/v1/admin/recall/preview` without `endpointId` returns the endpoint set from forwarder rules (plain, keyed and `AgentDyn-`) plus compiled consumers, and the warnings.
2. With `endpointId`, repeated calls return counts that sum to the seeded totals, and a null token only after every source was walked to the end; a match behind 1,000 messages and a history match behind 300 records are counted.
3. Failed and Completed copies whose responses fall outside the window are counted through their history records.
4. A non-Owner gets `403` and an access-denied `RecallMessages` record; invalid selections get `400` naming the field.
5. `MessageAuditType.RecallMessages` is 46, the last member, and every existing value is unchanged (asserted).
6. A preview changes nothing on Service Bus or in the store (asserted on the fakes).
7. `dotnet build src/NimBus.slnx -c Release` with NSwag generation enabled succeeds.

### Phase 3: execute, final pass, export and audit

Acceptance criteria:

1. Each session is processed in the Session ordering order; a test proves no Skip is sent before that session's parked copies are removed.
2. A session whose matching parked or queued copy failed to complete, or is still present after removal, gets no Skip and is reported removal incomplete; a rerun completes it.
3. Every Stored rows rule holds: blocking rows are never deleted, stranded rows are deleted, history is never deleted, and the final pass deletes re-created rows.
4. At-risk sessions aren't touched and the threshold can't exceed `ServiceBusMaxDeliveryCount - 1`.
5. Skips go through `IOperatorCommands.SkipAsync` with the reason `Recall <recallId>`; `Forbidden` and `AuditUnavailable` stop further Skips.
6. One run record before the first batch (a refused write runs nothing) and one record per completed endpoint, each within 4 KB.
7. The CSV has the documented columns, with `from` and `enqueuedTimeUtc` from the history record.
8. A rerun after a completed run removes, skips and deletes nothing more.
9. The emulator test passes in CI.

### Phase 4: WebApp UI

Acceptance criteria:

1. `/Operations/recall` shows the card in the Cleanup group; every existing card renders as before.
2. The preview loops each endpoint's tokens to completion and shows per-endpoint counts and scan progress.
3. Run stays disabled until every scan is complete and the pre-run export was downloaded or explicitly skipped, and until the typed publisher id matches.
4. Progress, stop and resume, the busy, at-risk, removal-incomplete and blocked-by-other lists, and the export work against mocked API responses.
5. `npm run test:ci` and `npm run build` pass; the PR includes a screenshot (AGENTS.md); `docs/recall-messages.md` is in the portal sidebar.

## Rejected alternatives

- **Select rows on their own `From` and `EnqueuedTimeUtc`** (`EventFilter.From`, `EnqueuedAtFrom/To`): those fields come from the latest message, so Failed and Completed copies would be missed.
- **A persistent "original request" projection on the row** (`UnresolvedEvent.OriginalFrom`, new): needs a Resolver change, a SQL migration, three providers and a backfill, while the `EventRequest` history record already holds both values.
- **Read rows by bare event ids:** Cosmos DB returns nothing for them.
- **A total scan cap with estimated counts:** matches behind the cap are never examined, and a rerun meets the same cap.
- **A server-side background job with run state and resume** (the AF-198 draft): it needs in-memory run state, ARR affinity on a scaled-out WebApp, and principal capture inside the job. Client-driven batches give progress and stop/resume with the request identity the other Operations actions use.
- **An `nb recall` CLI** (AF-198 draft): deferred, per the non-goals; it can follow as a client of these paths.
- **A single per-run audit record with a per-endpoint × state matrix** (AF-198 draft): it needs a larger, type-specific `AuditLogService` data limit. One record per endpoint stays within the existing limit, and is written as each endpoint completes.
- **Per-action opt-ins** (remove queued, skip failed, delete rows): the preview and pre-run export are the checkpoint; partial runs would leave sessions in states the ordering rules don't cover.
- **Skip parked (Deferred) rows instead of removing them:** a parked copy replays once its block clears, whatever a Skip recorded.
- **Delete Failed and Handoff rows:** the session would stay blocked by an event no one can see (#298).
- **Skip a session's blocker whenever the scan finishes:** a parked copy whose removal failed would then replay; the skip needs a cleared session.
- **Insert `RecallMessages` next to the other cleanup types:** shifts the persisted numeric values of later members.
