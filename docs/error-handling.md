# Error Handling

How NimBus classifies and routes failures from a subscriber adapter — what each
exception type does, what the inbound message ends up as, and what the Resolver
records.

This is the reference for adapter authors. For the broader set of message
flows see [`message-flows.md`](message-flows.md); for the pipeline mechanics
see [`pipeline-middleware.md`](pipeline-middleware.md).

## Where errors happen

Two distinct call sites can throw inside the subscriber:

- **Pipeline middleware** — registered via `AddPipelineBehavior<T>()`. Wraps the
  terminal handler. Exceptions here are *not* wrapped — they bubble up raw.
- **Event handler** — your `IEventHandler<TEvent>.Handle(...)`. Exceptions here
  are caught inside `StrictMessageHandler.HandleEventContent` and either
  re-thrown (`TransientException`, `EventHandlerNotFoundException`) or wrapped
  according to the configured `IFailureDispositionClassifier`.

## Decision tree

```mermaid
flowchart TD
    Start([exception raised]) --> Origin{thrown<br/>from?}

    Origin -- pipeline middleware --> Mw{exception type}
    Origin -- event handler --> Hc{exception type}

    Mw -- MessageAlreadyDeadLetteredException --> MwDLQ[middleware already dead-lettered<br/>send dead-letter notification only]
    Mw -- any other Exception --> MwUnex[catch unexpected<br/>send dead-letter notification + DeadLetter inbound]

    Hc -- TransientException --> Trans[Abandon → SB redelivers after lock expiry<br/>no Resolver notification]
    Hc -- EventHandlerNotFoundException --> Unsup[SendUnsupportedResponse → Complete<br/>Resolver: Unsupported]
    Hc -- ordinary Exception --> Classify{failure disposition}
    Classify -- DeadLetter --> Perm[wrap as PermanentFailureException<br/>send dead-letter notification + DeadLetter inbound<br/>Resolver: DeadLettered]
    Classify -- Discard --> Disc[SendDiscardResponse + Complete<br/>session remains or becomes unblocked<br/>Resolver: Skipped with reason]
    Classify -- Retry --> Ech[wrap as EventContextHandlerException<br/>SendErrorResponse + BlockSession + Complete<br/>CheckForRetry → schedule RetryRequest if policy matches<br/>Resolver: Failed]

    classDef terminal fill:#fee2e2,stroke:#b91c1c,color:#7f1d1d
    classDef recover  fill:#dcfce7,stroke:#15803d,color:#14532d
    classDef neutral  fill:#e0f2fe,stroke:#0369a1,color:#0c4a6e

    class MwDLQ,MwUnex,Perm terminal
    class Trans,Ech recover
    class Unsup,Disc neutral
```

## Exception classification

| Exception | Origin | Inbound | Resolver | Adapter sees |
|---|---|---|---|---|
| `TransientException` | Handler | **Abandon** (lock expires, SB redelivers) | — | Same message redelivered; lifecycle observers fire `OnFailed` only |
| `EventHandlerNotFoundException` | Handler | Complete | `Unsupported` | One-shot — there's no handler, so nothing replays |
| `SessionBlockedException` | Handler — internal, raised when session is already blocked | Complete + parked on `Deferred` sub | `Deferred` | Will replay when session unblocks (resubmit / skip / retry success) |
| `EventContextHandlerException` (auto-wrap for the `Retry` disposition) | Handler | BlockSession + Complete | `Failed` | Retry scheduled if `IRetryPolicyProvider` matches; otherwise stays Failed until operator action |
| `PermanentFailureException` (auto-wrap for the `DeadLetter` disposition) | Handler | **DeadLetter** + notify Resolver | `DeadLettered` | No retry. SB DLQ + audit trail |
| Discarded handler exception (`Discard` disposition) | Handler | Complete; unblock first when processing a retry/resubmission | `Skipped`, with exception and classifier reason | No retry or DLQ; deferred session work continues |
| `MessageAlreadyDeadLetteredException` | Middleware (e.g. `ValidationMiddleware`) | already DeadLettered by middleware → notify Resolver only | `DeadLettered` | Validation failures, missing critical fields |
| Any other `Exception` from middleware | Middleware | **DeadLetter** + notify Resolver | `DeadLettered` | Includes raw user-middleware throws (e.g. demo's `ServiceModeMiddleware`) |

`Failed`, `DeadLettered` and `Unsupported` records all fall under the **Failed** column on
the endpoint dashboard and the Monitor (`Mapper.cs` aggregates
`state.FailedCount + state.DeadletterCount + state.UnsupportedCount`): none of them drains
without an operator. **Pending** counts only `Pending` records, including Pending+Handoff.

## Failure disposition matrix

| Disposition | Inbound message | Session | Resolver audit trail |
|---|---|---|---|
| `Retry` | Sends `ErrorResponse`, completes the inbound message, and schedules `RetryRequest` when policy and budget allow | Blocks after the initial failure; retry success or operator action releases it | `Failed`, including the handler error |
| `DeadLetter` | Dead-letters immediately; no retry budget is consumed | Does not create a retry block; an already-blocked retry session still requires operator action | `DeadLettered`, including the dead-letter reason |
| `Discard` | Sends an enriched `SkipResponse` and completes; never retries or enters the DLQ | Never leaves the session blocked; retry/resubmission discard unblocks and resumes deferred work | `Skipped`, with the exception type, message, and classifier name |

With no `IFailureDispositionClassifier` registered, NimBus preserves its prior
behavior: a registered legacy permanent-failure classifier maps permanent
exceptions to `DeadLetter`; every other ordinary exception maps to `Retry`.

> **Note on PendingHandoff.** `IEventHandlerContext.MarkPendingHandoff(...)` is
> **not** an exception path. The handler returns normally and
> `StrictMessageHandler` reads `messageContext.HandlerOutcome` after the
> handler returns to decide whether to send a `PendingHandoffResponse` and
> block the session. The outcome maps to `ResolutionStatus = Pending` with
> `PendingSubStatus = "Handoff"` — it shows up in the Pending column, not
> Failed. Settlement is driven by two new control messages
> (`HandoffCompletedRequest` / `HandoffFailedRequest`) issued by
> `IHandoffClient.CompleteAsync` / `FailAsync`, which DO NOT re-invoke
> the user handler. See [ADR-012](adr/012-pending-handoff.md) and the
> [PendingHandoff flow](message-flows.md#13-pendinghandoff-async-completion)
> in the message-flows reference.

## Flows

### Transient failure (abandon + redeliver)

The handler (or the transport) raises `TransientException` to signal an
infrastructure fault where redelivering the same message is enough, such as a
lost session lock. NimBus does not call SB Abandon explicitly —
`MessageContext.Abandon` is a no-op so the SB peek-lock can expire on its own
and the message is redelivered. No notification reaches the Resolver, so audit
visibility is intentionally minimal for this path, and every attempt counts
toward the subscription's max delivery count. For downstream outages and
throttling, use typed exceptions and retry rules instead (see
[When to throw what](#when-to-throw-what)).

```mermaid
sequenceDiagram
    participant Ep as Endpoint Topic
    participant Sub as SubscriberClient
    participant Lock as SB Peek-Lock

    Ep->>Sub: EventRequest via main sub
    Sub->>Sub: handler throws TransientException
    Sub->>Lock: Abandon (no-op, lock held)
    Note over Lock: peek-lock expires (~30s)
    Lock-->>Ep: redeliver same message
    Ep->>Sub: EventRequest (delivery #2)
```

### Permanent failure (`DeadLetter` → dead-letter)

The handler throws an exception that the `IFailureDispositionClassifier` maps
to `DeadLetter`. The built-in `DefaultPermanentFailureClassifier` (registered with
`ConfigurePermanentFailureClassifier`) does this for `FormatException`,
`InvalidCastException`, `ArgumentException`, `NotSupportedException`, plus
type names containing `Serialization` / `Deserialization` / `Validation`.
`StrictMessageHandler.HandleEventContent` wraps it in
`PermanentFailureException`. The base `MessageHandler` dead-letters the
inbound message and notifies the Resolver — there is no retry.

The SDK JSON handler also normalizes missing, `null`, malformed, and
over-depth payloads directly to `PermanentFailureException`. JSON is read with
an isolated safe configuration (`TypeNameHandling.None`, maximum depth 32), so
ambient Newtonsoft defaults cannot enable polymorphic type construction. These
wire-format failures bypass retry even when a retry policy would otherwise
match.

```mermaid
sequenceDiagram
    participant Ep as Endpoint Topic
    participant Sub as SubscriberClient
    participant Res as Resolver Topic
    participant Svc as ResolverService

    Ep->>Sub: EventRequest via main sub
    Sub->>Sub: handler throws (e.g. JsonSerializationException)
    Sub->>Sub: classifier → PermanentFailureException
    Sub->>Ep: ErrorResponse (To=Resolver) with<br/>DeadLetterReason + DeadLetterErrorDescription
    Sub->>Ep: DeadLetter inbound (SB DLQ)
    Ep->>Res: Resolver sub forwards
    Res->>Svc: classified DeadLettered (DLE description set)
    Note over Svc: Cosmos: status = DeadLettered<br/>reason = "Permanent failure: JsonSerializationException"
```

### Discarded failure (complete + continue)

Use `Discard` for a known poison message that cannot succeed and does not need
operator DLQ handling. NimBus records an enriched `SkipResponse`, completes the
inbound message, and returns normally so lifecycle observers receive
`OnMessageCompleted`. It sends no `ErrorResponse` or `RetryRequest` and performs
no dead-letter operation. If the disposition is selected during retry or
resubmission, NimBus unblocks the session and resumes its deferred messages
before completing the discarded message.

### Validation rejection (middleware dead-lettered)

`ValidationMiddleware` rejects messages with missing critical fields
(`EventId`, or `EventTypeId` on an `EventRequest`). It dead-letters the
inbound directly and throws `MessageAlreadyDeadLetteredException` so the
base handler skips a second DeadLetter call but still sends the Resolver
notification.

```mermaid
sequenceDiagram
    participant Ep as Endpoint Topic
    participant Mw as ValidationMiddleware
    participant Sub as SubscriberClient
    participant Res as Resolver Topic
    participant Svc as ResolverService

    Ep->>Mw: pipeline entry — context missing EventId
    Mw->>Ep: DeadLetter inbound (reason "EventId is required")
    Mw->>Sub: throw MessageAlreadyDeadLetteredException
    Sub->>Ep: ErrorResponse (To=Resolver)<br/>DeadLetterReason + DeadLetterErrorDescription set
    Note over Sub: inbound is already in DLQ — no second DeadLetter call
    Ep->>Res: Resolver sub forwards
    Res->>Svc: status = DeadLettered
```

### Unsupported event type

The receiving endpoint has no `IEventHandler` registered for the inbound
`EventTypeId`. Not a failure of the message — a topology mistake at the
catalog. The message is acked (Complete), no session block, and the Resolver
records `Unsupported`.

See Flow 9 in [`message-flows.md`](message-flows.md#9-unsupported-event-type).

For native `EventRequest` messages, the `EventTypeId` application property is
authoritative. Messages produced before that property existed may fall back to
the body value. If both values are present and disagree, or a known routed type
has an unreadable body, NimBus treats the message as a permanent malformed
message and dead-letters it without invoking the user handler.

### Handler failure with retry policy

The handler throws something the classifier does *not* mark permanent. NimBus
wraps it as `EventContextHandlerException`, sends `ErrorResponse` to the
Resolver (status `Failed`), blocks the session so siblings defer, completes
the inbound, then schedules a `RetryRequest` if a policy from
`IRetryPolicyProvider` matches.

See Flow 6 in [`message-flows.md`](message-flows.md#6-retry-automatic).

### Handler failure with no retry policy

Same as above but `CheckForRetry` finds no matching policy. The inbound is
completed, the Resolver records `Failed`, and the session stays blocked
until the operator resubmits or skips the failed event from the Manager UI.

See Flow 2 in [`message-flows.md`](message-flows.md#2-handler-failure--session-blocked).

### Dead-letter notification routing

Every dead-letter path above (`PermanentFailureException`,
`MessageAlreadyDeadLetteredException`, raw middleware throw) calls
`IResponseService.SendDeadLetterResponse` before the actual SB DeadLetter.
The notification is an `ErrorResponse`-typed message with the DLQ properties
populated; the Resolver short-circuits on `DeadLetterErrorDescription` and
classifies the audit record as `DeadLettered` regardless of `MessageType`.

See Flow 12 in [`message-flows.md`](message-flows.md#12-dead-letter-routed-to-resolver).
The notification is best-effort — if the publish fails, the SB DLQ remains
the source of truth and operators can recover via the Manager.

## Adapter author guidance

### When to throw what

| Situation | Throw |
|---|---|
| Downstream API timed out, refused the connection, or answered 408 / 5xx | Your own "unavailable" exception type, retried with an `AddExceptionRule<T>` rule. Each attempt is a broker-scheduled `RetryRequest` with its own audit record, and the session stays blocked so later events wait in order. |
| Downstream API answered 429 | Your own "throttled" exception type that implements `IRetryAfterHint`, retried with an `AddExceptionRule<T>` rule, so the retry waits at least the server's `Retry-After`. Exclude it from the circuit breaker. |
| Downstream API rejected the request (other 4xx) and an operator can fix the data | Your own "rejected" exception type with no retry rule. It is recorded as `Failed` and the session blocks until an operator resubmits or skips it. |
| Downstream API returned 400 with a structural problem the message can't fix on retry | Return `DeadLetter` from `IFailureDispositionClassifier` when operators need the DLQ payload; return `Discard` when a skipped audit record is sufficient |
| Known poison event/version that should neither retry nor create DLQ noise | Return `Discard`; NimBus records `Skipped`, completes, and lets the session continue |
| Don't have a handler for this event type | Don't catch `EventHandlerNotFoundException` — let it bubble; you'll see it as `Unsupported` |

Any other exception is recorded as `Failed` and retried only if a retry rule,
an event-type policy or the default policy matches it. The typed failures,
rules and breaker settings are worked through in
[building-adapters.md#resilience](building-adapters.md#resilience).

`TransientException` is not the tool for downstream outages. It takes the
[abandon path](#transient-failure-abandon--redeliver): no audit record reaches
the Resolver, retry policies and `Retry-After` are ignored, the same message is
redelivered only after its lock expires, and each attempt spends a Service Bus
delivery, so after 10 the message is dead-lettered by the broker rather than
failed in NimBus. Reserve it for infrastructure faults where redelivering the
identical message is the right answer and nothing is worth recording — NimBus
itself throws it for a lost session lock or a transient Service Bus SDK error.
Handlers that throw it must be idempotent. The circuit breaker counts it like
any other retryable failure.

### When NOT to swallow

A pipeline middleware that catches and swallows handler exceptions makes the
message look successful (`SendResolutionResponse` fires). This breaks the
audit trail. Re-throw unless you're explicitly compensating (e.g.
short-circuit success for known idempotent no-ops).

### Configuring failure dispositions

```csharp
builder.Services.AddNimBusSubscriber("billingendpoint", subscriber =>
{
    subscriber.WithFailureDispositions(new AdapterFailureDispositionClassifier());
});

sealed class AdapterFailureDispositionClassifier : IFailureDispositionClassifier
{
    public FailureDisposition Classify(
        Exception exception,
        string eventTypeId,
        string? endpointName) => exception switch
    {
        KnownBadEventVersionException => FailureDisposition.Discard,
        MyDomain.InvariantViolationException => FailureDisposition.DeadLetter,
        _ => FailureDisposition.Retry,
    };
}
```

`ConfigurePermanentFailureClassifier` registers a `DefaultPermanentFailureClassifier`
as the disposition classifier: permanent exceptions map to `DeadLetter`, everything
else to `Retry`. A classifier registered with `WithFailureDispositions` takes
precedence. The `IPermanentFailureClassifier` interface was removed in v4.0.0;
implement `IFailureDispositionClassifier` instead.

### Configuring retry

```csharp
builder.Services.AddNimBusSubscriber("billingendpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
    sub.ConfigureRetryPolicies(policies =>
    {
        policies.SetDefaultPolicy(new RetryPolicy
        {
            MaxRetries = 3,
            Strategy   = BackoffStrategy.Exponential,
            BaseDelay  = TimeSpan.FromSeconds(5),
            MaxDelay   = TimeSpan.FromMinutes(5),
            Jitter     = JitterMode.Bounded,
            BoundedJitterFactor = 0.25,
        });
    });
});
```

Jitter spreads retries that would otherwise use the same calculated backoff
delay `d`:

- `JitterMode.None` is the default and keeps the deterministic delay unchanged.
- `JitterMode.Full` selects a delay uniformly from `[d, 2d)`.
- `JitterMode.Bounded` selects `d * (1 + U[0, factor))`; the default factor of
  `0.25` produces delays from `d` up to (but not including) `1.25d`. The factor
  must be finite and non-negative.

`MaxDelay` is applied after jitter. Bounded jitter is useful when retry timing
must stay close to the configured backoff; full jitter provides a wider spread
when many sessions are likely to fail together.

To retry only some failures, match the exception type:

```csharp
sub.ConfigureRetryPolicies(policies => policies
    .AddExceptionRule<PartnerApiUnavailableException>(new RetryPolicy
    {
        MaxRetries = 4,
        Strategy   = BackoffStrategy.Exponential,
        BaseDelay  = TimeSpan.FromSeconds(15),
        MaxDelay   = TimeSpan.FromMinutes(2),
    }));
```

`AddExceptionRule<TException>` matches the handler's exception or any inner
exception of that type, subclasses included. The older
`AddExceptionRule(string, ...)` matches a substring of the exception text,
which includes stack traces, so prefer the typed form. Exception rules are
checked in registration order before event-type policies and the default
policy; the first match wins.

If the exception (or an inner exception) implements `IRetryAfterHint`, the
retry waits for the longer of the policy delay and its `RetryAfter`, capped at
`MaxDelay`. Use it to honour an HTTP `Retry-After` header. The policy still
decides whether to retry.

Without a retry policy, handler failures stay in `Failed` until an operator
resubmits or skips them — there is no implicit retry.

For the adapter pattern that combines typed failures, these rules, the circuit
breaker and the inbox, see
[building-adapters.md#resilience](building-adapters.md#resilience).

For widespread downstream outages, an opt-in endpoint circuit breaker can stop hosted receivers before every session consumes its retry budget. It counts retry/transient handler outcomes but never changes their exception or settlement behavior; an open circuit leaves messages untouched on the subscription. See [`circuit-breaker.md`](circuit-breaker.md).

### Custom middleware that needs to dead-letter

If you implement middleware that should reject a message outright (rather
than throwing into the retry path), call
`context.DeadLetter(reason, exception)` yourself and throw
`MessageAlreadyDeadLetteredException` so the base handler skips a second
DeadLetter and emits the Resolver notification. `ValidationMiddleware` is
the canonical example.

## Lifecycle observer hooks

For passive observation (alerting, metrics) without altering the flow:

| Observer call | Fires for |
|---|---|
| `OnMessageReceived` | every message before pipeline runs |
| `OnMessageCompleted` | success and discard paths — pipeline returns without throwing |
| `OnMessageFailed` | every catch branch in `MessageHandler.Handle` (transient, permanent, validation-rejected, unexpected) |
| `OnMessageDeadLettered` | only when the message ends up in DLQ — fires *after* the SB DeadLetter call |
| `OnCircuitStateChanged` | endpoint circuit transitions (Closed, Open, HalfOpen) |

Use the lifecycle observer for fan-out to channels like Slack/PagerDuty;
don't use it to gate processing — register a pipeline behavior for that.

## Where to look in source

| Concern | File |
|---|---|
| Catch ladder for the receiver | `src/NimBus.Core/Messages/MessageHandler.cs` |
| Handler-throw to retry/error mapping | `src/NimBus.Core/Messages/StrictMessageHandler.cs` (`HandleEventRequest`, `HandleEventContent`, `CheckForRetry`) |
| Failure disposition contract and legacy bridge | `src/NimBus.Core/Messages/IFailureDispositionClassifier.cs`, `DefaultFailureDispositionClassifier.cs` |
| Discard/dead-letter response shape | `src/NimBus.Core/Messages/ResponseService.cs` (`SendDiscardResponse`, `SendDeadLetterResponse`) |
| Validation middleware | `src/NimBus.Core/Pipeline/ValidationMiddleware.cs` |
| Resolver classification | `src/NimBus.Resolver/Services/ResolverService.cs` (`GetResultingStatus`) |
