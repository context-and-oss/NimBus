# ADR-012: PendingHandoff Outcome for Async Message Completion

## Status
Accepted (introduced 2026-05; issue #15).
Amended 2026-08: the `IManagerClient.CompleteHandoff` / `FailHandoff` settlement members described below were superseded by `NimBus.SDK.IHandoffClient` / `IHandoffClientFactory` and removed in 3.0; the wire contract (`HandoffCompletedRequest` / `HandoffFailedRequest`) is unchanged.

## Context

NimBus today resolves a message at the moment the handler returns. That assumption breaks for adapters whose unit of work is an external long-running job — the canonical case is the D365 F&O DMF integration, where the import is asynchronous and the per-entity outcome only arrives later via a status checker.

Three forces collided:

1. **Service Bus peek-locks cannot be held for the duration.** F&O DMF jobs frequently exceed the 5-minute lock cap, so the inbound message has to be settled promptly. We cannot just hold the receiver and wait.
2. **Sibling messages on the same session must defer until the external work reports back**, otherwise per-session FIFO ordering breaks across the duration of the external job.
3. **Settling the audit row as Completed lies** (the work has not happened); settling as Failed lies the other way and pollutes dashboards / alerting.

The only existing way to keep a session blocked was to throw an exception from the handler — i.e., abuse the failure path for happy-path control flow. That is the status quo we wanted to leave behind. Pipeline middleware, telemetry filters, lifecycle observers, and ordinary code review all treat exceptions as faults; using them to signal a healthy in-flight handoff is wrong on every axis.

## Decision

Introduce a new handler outcome — **PendingHandoff** — signalled explicitly by a method on the handler context, mapped to the existing `ResolutionStatus.Pending` with a sub-status discriminator:

- `IEventHandlerContext.MarkPendingHandoff(reason, externalJobId, expectedBy)` records the outcome on a back-reference to `IMessageContext`. The handler returns normally afterwards.
- After the handler returns, `StrictMessageHandler.HandleEventRequest` inspects `messageContext.HandlerOutcome`. If it is `PendingHandoff`, it sends a new `PendingHandoffResponse` to the Resolver, calls `BlockSession`, and completes the inbound. It does NOT send `ResolutionResponse` and does NOT consult retry policy.
- Two new control messages — `HandoffCompletedRequest` and `HandoffFailedRequest` — drive terminal settlement from the Manager. The subscriber's `HandleHandoffCompletedRequest` / `HandleHandoffFailedRequest` overrides settle the audit row directly and DO NOT re-invoke the user handler.
- Two new `IManagerClient` methods (`CompleteHandoff`, `FailHandoff`) construct those control messages. They validate `pendingEntry.PendingSubStatus == "Handoff"` upfront before sending, so misuse fails fast with `InvalidOperationException`.
- `MessageEntity` and `UnresolvedEvent` gain four nullable fields — `PendingSubStatus`, `HandoffReason`, `ExternalJobId`, `ExpectedBy` — persisted by every storage provider satisfying the conformance suite.
- The WebApp Pending column counts Pending+Handoff entries; a small "Awaiting external" badge and a "Handoff details" section render on the message detail page when `PendingSubStatus = "Handoff"`. Operator Resubmit / Skip remain available unchanged.

`HandlerOutcome` (enum) and `HandoffMetadata` (record) live in `NimBus.Core.Messages`, NOT in `NimBus.SDK.EventHandlers`. This is non-obvious: the SDK references Core (not the other way around), so the back-reference from `EventHandlerContext` to `IMessageContext` requires the type to be defined where Core can see it. Putting it in the SDK would force a circular reference or a duplicated definition.

## Rejected Alternatives

### 1. Throw-based signalling (e.g. a new `PendingHandoffException`)

Modelled on the existing `SessionBlockedException` pattern: the handler throws, the catch branch sends the response and blocks the session.

Rejected because:
- It is control-flow-by-exception on a healthy code path. Every pipeline middleware, every lifecycle observer, every telemetry filter has to special-case the new exception to avoid mis-classifying it as a fault.
- It pollutes stack traces and `OnFailed` observer counts even when nothing is wrong.
- Code review and IDE tooling treat exceptions as exceptional. Adapter authors writing happy-path async code should not be obliged to throw.
- NFR-002 explicitly forbids adding new exception types for the happy path.

### 2. Repurposing `Resubmit` for the success case

Use the existing `IManagerClient.Resubmit` to drive Pending → Completed: the status checker, on success, would call `Resubmit` with the result payload, the subscriber would re-invoke the handler, the handler would now succeed, and the existing `ResolutionResponse` flow would close the audit.

Rejected because:
- `Resubmit` re-invokes the user handler. For a settlement that is already known to be successful, the handler runs a second time — wasted work, doubled middleware metrics, and the audit row is misattributed to a "resubmission" rather than a settlement.
- Telemetry conflates two semantically different events: "operator hit Resubmit" and "external system reported success".
- The handler would have to be written to detect the resubmit-after-handoff case to avoid double-effecting downstream side effects. That is exactly the kind of logic NimBus should absorb, not push to adapter authors.
- `IManagerClient.Skip` has the same shape problem on the failure side: skipping ≠ "external system reported failure".

The clean separation of concerns is: `Resubmit` / `Skip` are operator-initiated recovery primitives; `CompleteHandoff` / `FailHandoff` are status-checker-initiated settlement primitives. They map to different audit-row semantics and should not share code paths.

### 3. Adding a new `ResolutionStatus` value (e.g. `PendingHandoff`)

Treat the new state as a first-class status value alongside `Pending`, `Completed`, `Failed`, `Skipped`, `DeadLettered`, `Unsupported`.

Rejected because:
- Every dashboard tile, every aggregation query, every `Mapper.cs` count would need to be updated to know about the new status. The "Pending count" goal is "show in-flight work" — Pending+Handoff IS in-flight work. There is no operator-facing reason to split them at the column level.
- Storage migration impact across providers: every provider has to evolve its enum / status column.
- Forwarding subscriptions and Resolver classification logic would need a new branch.
- The sub-status discriminator approach (`PendingSubStatus = "Handoff"`) keeps the existing status machine intact and additive: legacy rows have `PendingSubStatus = null` and render exactly as before. New rows render with the badge.

The chosen design — `Pending` + a nullable sub-status — keeps the dashboard contract stable, the migration path empty (new columns are nullable), and the WebApp logic local to one badge component.

## Consequences

### Positive

- Handler code stays linear and reads as a happy path. No exception abuse.
- Settlement happens with a single control message; no extra round-trip through `ResubmissionRequest` to obtain a `ResolutionResponse` (NFR-003).
- The user handler is never re-invoked on settlement (SC-003); middleware metrics and audit attribution stay honest.
- Existing dashboards work unchanged. Pending+Handoff counts under Pending; the badge tells operators what's in flight.
- Pure additive change. Existing handlers, audit rows, transports, and operator Resubmit / Skip flows all keep working — adapters that don't call `MarkPendingHandoff` see zero behaviour change (FR-060, FR-063).
- Subscribers stay pure Service Bus consumers (NFR-005, ADR-002 respected). Adapter-owned correlation stores live in the adapter, not in NimBus.

### Negative

- One additional code path through `StrictMessageHandler.HandleEventRequest`. The post-handler `if (HandlerOutcome == PendingHandoff)` branch is a new joint that future maintainers need to reason about — particularly that the failure-path catch branches still take precedence (FR-012, edge case "calls MarkPendingHandoff and then throws").
- `HandoffFailedRequest`'s `errorText` is preserved verbatim, but `errorType` is wrapped in a synthetic `HandoffFailedException` so the existing `SendErrorResponse` path can fire. The audit row's `ErrorType` therefore reflects the wrapper, not the operator-supplied `errorType`. This is an explicit v1 trade-off — `errorText` is the field operators read; the `ErrorType` shape carries the round-trip wrapper. Not a bug, not worth fixing without scope discussion.
- The new fields are nullable on every storage provider. Every conformance test had to grow a round-trip case. This is a one-time cost; future provider implementations get the test for free.
- `NimBus.Core.Messages` now owns a type — `HandoffMetadata` — that conceptually originates from the SDK handler API. The dependency direction (Core ← SDK) makes this the only sensible home, but the layering is non-obvious and worth a one-line comment in code reviews.

### Operational

- The WebApp surfaces `HandoffReason`, `ExternalJobId`, and `ExpectedBy` on the message detail page. Operators can resubmit / skip Pending+Handoff entries with the same buttons as today (FR-042, SC-006).
- An optional Resolver-side timeout sweeper (FR-050) is in scope but not built in v1. Adapters with bounded reasonable wait times can opt in later via `ExpectedBy`. The default is "no sweeper running" — adapters with unbounded reasonable wait times stay unaffected.

## Update — 2026-05-20 — settlement API simplified

The original settlement surface was `IManagerClient.CompleteHandoff(MessageEntity, …)` / `FailHandoff(…)` in `NimBus.Manager`. Reviewing it against the canonical Erp.Api shape in CrmErpDemo surfaced two real frictions for adapter authors:

- **Discovery / packaging.** `NimBus.Manager` wasn't packable and wasn't transitively reachable from `NimBus.SDK`, so adapter authors had to add a second package reference and hand-register `IManagerClient`.
- **Bag-of-fields contract.** Settlement required hand-constructing a `MessageEntity` with seven specific fields plus `PendingSubStatus = "Handoff"`. The fields are *already* persisted by the adapter alongside its `ExternalJobId`, so the ceremony is busywork.

The fix introduces `NimBus.SDK.IHandoffClient` with two methods (`CompleteAsync(HandoffSettlement, …)` / `FailAsync(HandoffSettlement, …)`), where `HandoffSettlement` is a typed record carrying the six audit-row coordinates by name. `NimBus.Manager` is now packable; its `CompleteHandoff` / `FailHandoff` methods are `[Obsolete]` thin shims that produce byte-identical Service Bus control messages via the shared `HandoffControlMessageFactory`. Operator-tool methods on `IManagerClient` (`Resubmit`, `Skip`) are unchanged.

Why coords and not `externalJobId`: a "look the row up by external job id" design was considered but rejected because it requires an `IMessageTrackingStore` dependency in the settlement process. The canonical CrmErpDemo settlement process (`Erp.Api`) has no audit-DB access by design; the Resolver owns that DB. Requiring it would regress the dependency footprint. The new `GetPendingHandoffByExternalJobId` query on `IMessageTrackingStore` (and the `IX_UnresolvedEvents_ExternalJobId_PendingHandoff` filtered index in `0011_HandoffLookup.sql`) stays in for diagnostic / operator-UI scenarios where the calling process *does* have store access.

DI: `AddNimBusSubscriber` auto-registers `IHandoffClient` for the subscriber's endpoint. Settlement-only processes call `services.AddNimBusHandoffClient(endpoint)` explicitly. Neither path requires a tracking-store registration.

## Note — 2026-09-15 — history-only settlement projection evaluated and not adopted

The DIS platform records `HandoffCompletedRequest` / `HandoffFailedRequest` in message history only (`ShouldProjectToUnresolvedState`) and leaves the Pending+Handoff row untouched until the subscriber's terminal response. That rule was approved for NimBus, implemented, and reversed in the same day's code review, so the behaviour described above stands: the Resolver projects a settlement request as a plain Pending row with `PendingSubStatus` cleared, and the terminal `ResolutionResponse` / `ErrorResponse` flips it.

Why it stays: NimBus has an agent zone that DIS does not. `GetAgentReceiveAsync` is a non-claiming, oldest-first read of Pending+Handoff rows, and `HandoffSettlementService` gates settlement on that sub-status. Without the projection the zone would re-deliver the just-settled event until the subscriber's round trip completed, would admit a second settlement in that window, and would block the whole zone on that one row for the duration (the whole outage, if the subscriber were down). The projection is what moves the row out of the receive query within milliseconds of the settlement request reaching the Resolver. It is pinned by `ResolverServiceTests.Handle_HandoffSettlementRequest_ProjectsPlainPendingRow`.

What the DIS rule would have bought, and why it was not enough: parity with DIS, the handoff badge staying visible during settlement, and one fewer case in the stale-write guard of Spec 030. The guard keeps a control-request clause for Resubmit and Skip regardless, so a row last written by a settlement request is already protected against a late copy of the original request. A badge-preserving variant (project the settlement but keep `PendingSubStatus = "Handoff"`, and exclude settled rows from the receive query by `MessageType`) remains possible at the cost of a query change in all three storage providers.

## Note — 2026-10-11 — settlements in flight before the projection

The projection above takes milliseconds, but an agent that loops straight from settle back to receive is faster. In the CrmErpDemo the enrichment agent received the event it had just settled, processed it a second time, and settled it again; both settles passed the Pending+Handoff guard, so two control messages and two `CompleteHandoff` audit rows were written 30 ms apart.

`HandoffSettlementService` now claims the parked message in a process-wide `HandoffSettlementTracker` before sending the settlement. A second settle of the same parked message gets the same 400 it would get after the projection, with no second control message and no second audit row, so a settled hand-off still has exactly one audit row (ADR-002). `GetAgentReceiveAsync` skips a row whose settlement is in flight and keeps polling until the projection moves it out of the query. A claim is keyed by endpoint, event id and the parked row's message id (an event parked again by a new message is a new hand-off), is released if the send fails, and expires after five minutes so a settlement the Resolver never projects cannot hide the hand-off forever.

The tracker is per process. With several WebApp instances, a receive or settle that reaches another instance inside the projection window can still see the hand-off, so delivery there stays at-least-once. A store-backed lease would close that gap, at the cost of a new storage member in all three providers.

## See Also

- ADR-002: Centralized Resolver — establishes the audit-trail contract that PendingHandoff plugs into.
- ADR-001: Session-based ordering — establishes the FIFO contract that sibling-deferral preserves.
- `docs/error-handling.md` — exception-classification reference; PendingHandoff is explicitly NOT an exception path.
- `docs/message-flows.md` — flow diagrams; the PendingHandoff settlement flow is added there.
- `docs/sdk-api-reference.md` — `IEventHandlerContext.MarkPendingHandoff` API documentation with worked example.
