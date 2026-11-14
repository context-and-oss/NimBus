# SDK API Reference

Concise reference for the NimBus SDK public interfaces and registration methods.

## DI Registration

### AddNimBusPublisher

```csharp
services.AddNimBusPublisher("EndpointName");
```

Registers `IPublisherClient` as a singleton. The endpoint name determines the Service Bus topic.

### AddNimBusSubscriber

```csharp
services.AddNimBusSubscriber("EndpointName", sub =>
{
    sub.AddHandlersFromAssemblyContaining<OrderPlacedHandler>();
    sub.ConfigureRetryPolicies(policies => { ... });
});
```

Registers `ISubscriberClient` with handler mappings and optional retry policies.
`AddHandlersFromAssemblyContaining<TMarker>()` scans the marker type's assembly
for concrete `IEventHandler<TEvent>` implementations. Use
`AddHandler<TEvent,THandler>()` when you want to register or override a handler
explicitly.

#### UseInbox

Register a provider and opt a subscriber into record-on-success deduplication:

```csharp
services.AddNimBusSqlServerInbox(connectionString);
services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
    sub.UseInbox(options =>
    {
        options.DeduplicationStore = InboxStore.SqlServer;
        options.RetentionPeriod = TimeSpan.FromDays(7);
        options.CleanupInterval = TimeSpan.FromHours(1);
    });
});
```

The selectable providers are `InboxStore.SqlServer`, `InboxStore.Cosmos`, and
`InboxStore.InMemory`. The matching keyed provider registration is mandatory;
subscriber startup fails if it is missing. Records are keyed by the
`(endpoint, MessageId)` pair — byte-exact on every provider via a shared
identity hash, so ids differing only by case or trailing whitespace stay
distinct — and endpoints sharing one physical store never skip each other's
fan-out deliveries. The endpoint side of the key is the configured
`NimBusSubscriberOptions.Endpoint`, not the message's `To` address, so
external CloudEvents (whose `To` is only the mapped event type) deduplicate
under the same stable scope as native messages. A fresh delivery costs
exactly one store check (run ahead of the session-state guards) and one
record. A successful handler is recorded before the Resolver response and
broker settlement; pending handoffs are not recorded so redelivery can
recreate the pending state. Store failures follow the transient redelivery
path, while a missing or unsupported-length `MessageId` logs a warning and
runs the handler without deduplication. Cleanup is scoped to the subscriber's
own endpoint, so differently configured retention periods can share one
physical store. The Cosmos provider additionally requires the account's
effective consistency level to be `Strong` (or an explicit
`CosmosInboxOptions.AllowRelaxedConsistency` acknowledgment) so duplicate
checks observe records committed by a crashed consumer on another replica.
`UseInbox` requires NimBus's own subscriber wiring: registration fails fast
when a custom `ISubscriberClient` is already present, and startup validation
fails the host when one is registered afterwards and would override the
NimBus-composed client, because the inbox decorator could never reach that
client. See [Consumer inbox](inbox-pattern.md) for provider setup, cleanup,
and atomicity limits.

### AddNimBusReceiver

```csharp
services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "EndpointName";
    opts.SubscriptionName = "EndpointName";
});
```

Registers a hosted service that listens to a Service Bus session-enabled subscription using `ServiceBusSessionProcessor`.

### AddNimBus

```csharp
services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddLifecycleObserver<AlertingObserver>();
    nimbus.AddExtension<MyExtension>();
});
```

Registers the middleware pipeline and lifecycle observers. Call before `AddNimBusSubscriber`.

---

## Publishing

### IPublisherClient

```csharp
public interface IPublisherClient
{
    Task Publish(IEvent @event);
    Task Publish(IEvent @event, string sessionId, string correlationId);
    Task Publish(IEvent @event, string sessionId, string correlationId, string messageId);
    Task PublishFromContext(IEvent @event, IEventHandlerContext context,
        string messageId, CancellationToken cancellationToken = default);
    Task PublishBatch(IEnumerable<IEvent> events);
    Task PublishBatches(IEnumerable<IEvent> events, string correlationId = null);
}
```

| Method | Description |
|---|---|
| `Publish(event)` | Publish a single event. SessionId from `event.GetSessionId()`, auto-generated CorrelationId and MessageId. |
| `Publish(event, sessionId, correlationId)` | Publish with explicit session and correlation IDs. Overrides `GetSessionId()`. |
| `Publish(event, sessionId, correlationId, messageId)` | Publish with all IDs explicit (for deterministic deduplication). |
| `PublishFromContext(event, context, messageId, cancellationToken)` | Publish a workflow follow-up with an explicit deterministic ID while preserving the inbound session, correlation, and origin lineage and setting the inbound message as parent. |
| `PublishBatch(events)` | Publish multiple events as a Service Bus batch. Respect batch size limits. |
| `PublishBatches(events, correlationId)` | **Preferred for bulk publish.** Publishes any number of events, automatically paged to the Service Bus batch size; each event is built and serialized exactly once. |

**Session ID controls ordering.** By default, `Publish(event)` uses the event's `GetSessionId()` method. The explicit overloads let the publisher control ordering independently:

```csharp
// Default: session ID from OrderPlaced.GetSessionId() (= OrderId)
await publisher.Publish(order);

// Explicit: group multiple event types under a shared session for cross-event ordering
await publisher.Publish(order, customerId.ToString(), correlationId);

// Explicit with deterministic message ID for deduplication
await publisher.Publish(order, customerId.ToString(), correlationId, $"order-{order.OrderId}");

// Workflow follow-up: preserve inbound ordering, correlation, and lineage
await publisher.PublishFromContext(
    reserveInventory,
    context,
    messageId: $"{order.OrderId}:reserve-inventory:{state.Version}",
    cancellationToken: cancellationToken);
```

`PublishFromContext` requires a non-empty outgoing `MessageId`. Derive it from
durable workflow identity, logical transition, and workflow version or attempt;
retries of the same transition must reproduce the same value. The built-in
`PublisherClient` supports native and CloudEvents publishing through both the
direct sender and transactional SQL outbox. Custom `IPublisherClient`
implementations must implement this additive method before using it.

---

## Subscribing

### IEventHandler\<T\>

```csharp
public interface IEventHandler<T> where T : IEvent
{
    Task Handle(T message, IEventHandlerContext context, CancellationToken cancellationToken = default);
}
```

| Parameter | Type | Description |
|---|---|---|
| `message` | `T` | Deserialized event object |
| `context` | `IEventHandlerContext` | Message metadata, including workflow session, correlation, and lineage |
| `cancellationToken` | `CancellationToken` | For async cancellation |

Handlers are resolved from DI — use constructor injection for dependencies.

### IEventHandlerContext

```csharp
public interface IEventHandlerContext
{
    string EventId { get; }
    string CorrelationId { get; }
    string EventType { get; }
    string MessageId { get; }
    string SessionId { get; }
    string ParentMessageId { get; }
    string OriginatingMessageId { get; }

    // Read-only state set by MarkPendingHandoff (default: HandlerOutcome.Default).
    HandlerOutcome Outcome { get; }
    HandoffMetadata HandoffMetadata { get; }

    // Signal that the handler has handed work off to a long-running external system.
    // Idempotent — last call wins. See "Async completion via PendingHandoff" below.
    void MarkPendingHandoff(string reason, string externalJobId = null, TimeSpan? expectedBy = null);
}
```

`ParentMessageId` identifies the message that caused the inbound message.
`OriginatingMessageId` identifies the first message in its lineage. Legacy
messages without those application properties expose `self` through the
built-in context; additive interface defaults keep existing custom context
implementations source-compatible.

#### Async completion via PendingHandoff

When a handler triggers an external long-running job (e.g. a D365 F&O DMF
import) and the per-entity outcome only arrives later, calling
`ctx.MarkPendingHandoff(...)` and returning normally tells NimBus to:

1. Send a `PendingHandoffResponse` to the Resolver — the audit row is
   recorded as `ResolutionStatus = Pending` with `PendingSubStatus = "Handoff"`.
2. Block the session so sibling messages on the same session defer (FIFO)
   until the external job settles.
3. Complete the inbound Service Bus message — no peek-lock held for the
   duration of the external work.

The user handler is **not** re-invoked when the external system reports
back. Settlement is driven by `IHandoffClient.CompleteAsync` (success) or
`IHandoffClient.FailAsync` (failure) — registered automatically by
`AddNimBusSubscriber` and standalone via `services.AddNimBusHandoffClient(endpoint)`
in settlement-only processes; processes that settle for endpoints resolved
at runtime use `IHandoffClientFactory` (`AddNimBusHandoffClientFactory`).
The legacy `IManagerClient.CompleteHandoff` / `FailHandoff` bridge was
removed in 3.0; `IManagerClient` retains only the operator recovery
primitives (`Resubmit`, `Skip`).
See [ADR-012](adr/012-pending-handoff.md), the practitioner-level walkthrough
in [pending-handoff.md](pending-handoff.md), and the
[PendingHandoff message flow](message-flows.md#13-pendinghandoff-async-completion).

```csharp
public class CreateOrderHandler : IEventHandler<OrderPlaced>
{
    private readonly IDmfClient _dmf;
    private readonly ICorrelationStore _correlations;

    public CreateOrderHandler(IDmfClient dmf, ICorrelationStore correlations)
    {
        _dmf = dmf;
        _correlations = correlations;
    }

    public async Task Handle(OrderPlaced order, IEventHandlerContext ctx, CancellationToken ct)
    {
        // Trigger the long-running external import.
        var jobId = await _dmf.QueueImportAsync(order, ct);

        // Record (eventId, jobId) so the adapter's status checker can
        // settle via IHandoffClient.CompleteAsync / FailAsync later.
        await _correlations.SaveAsync(ctx.EventId, jobId, ct);

        // Tell NimBus this is a healthy in-flight handoff. The handler
        // returns normally — no exception, no failure-path side effects.
        ctx.MarkPendingHandoff(
            reason: "DMF import in flight",
            externalJobId: jobId,
            expectedBy: TimeSpan.FromMinutes(15));
    }
}
```

`MarkPendingHandoff` is idempotent (last call wins). If the handler calls
it AND then throws, the failure path takes precedence — an `ErrorResponse`
is sent and the PendingHandoff metadata is discarded. PendingHandoff is NOT
an exception path; see [`error-handling.md`](error-handling.md).

**See also**: a runnable end-to-end showcase wiring this onto the ERP adapter
in the CRM/ERP demo — [`samples/CrmErpDemo/README.md#showcase-pendinghandoff-async-erp-imports`](../samples/CrmErpDemo/README.md#showcase-pendinghandoff-async-erp-imports).

### ISubscriberClient

```csharp
public interface ISubscriberClient
{
    // Azure Functions isolated worker
    Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken ct);
    Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken ct);
    Task Handle(ProcessSessionMessageEventArgs args, CancellationToken ct);
    void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> factory) where TEvent : IEvent;
}
```

`Handle(message, sessionActions, ct)` is obsolete and will be removed in v5: `ServiceBusSessionMessageActions` cannot complete or dead-letter messages. Bind `ServiceBusMessageActions` in the trigger and call `Handle(message, messageActions, sessionActions, ct)` instead — see [Azure Functions hosting](azure-functions-hosting.md#4-create-the-function) for the trigger binding.

Typically not used directly — `AddNimBusSubscriber` + `AddNimBusReceiver` handle registration and hosting.

---

## Events

### Event (base class)

```csharp
public abstract class Event : IEvent
{
    public abstract string GetSessionId();
}
```

The base class provides a default `GetSessionId()` that returns a random GUID (no ordering). Use `[SessionKey]` or override `GetSessionId()` for ordered processing.

### Command (base class)

```csharp
public abstract class Command : Event
{
}
```

A marker for messages with imperative intent and exactly one recipient. Commands publish, route, order, and audit exactly like events; the platform additionally enforces at provisioning time that a `Command`-derived type has exactly one consuming endpoint declared in the catalog (`PlatformValidation.EnsureCommandConsumers` — zero or multiple consumers fail `nb topology apply`). Prefer imperative names (`PlaceCustomerOnCreditHold`) for commands and past tense (`CustomerCreated`) for events. See [ADR-014](adr/014-commands-single-consumer.md).

### SessionKey Attribute

Declare the ordering key declaratively — no method override needed:

```csharp
[SessionKey(nameof(OrderId))]
public class OrderPlaced : Event
{
    public Guid OrderId { get; set; }
    public decimal TotalAmount { get; set; }
}
```

The base `Event.GetSessionId()` reads the attribute via reflection and returns `OrderId.ToString()`.

**Precedence order:**
1. Publisher override — `Publish(event, sessionId, ...)` always wins
2. Method override — `GetSessionId()` override on the event class
3. Attribute — `[SessionKey(nameof(Prop))]` on the event class
4. Default — `Guid.NewGuid().ToString()` (no ordering)

### Recommended attributes

| Attribute | Purpose |
|---|---|
| `[SessionKey(nameof(Prop))]` | Declares the session ordering key (alternative to overriding `GetSessionId()`) |
| `[Description("...")]` | Event and property descriptions (used in AsyncAPI export) |
| `[Required]` | Marks required fields (used in JSON Schema generation) |
| `[Range(min, max)]` | Numeric validation range (used in JSON Schema) |

---

## Middleware Pipeline

### IMessagePipelineBehavior

```csharp
public interface IMessagePipelineBehavior
{
    Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default);
}
```

| Parameter | Type | Description |
|---|---|---|
| `context` | `IMessageContext` | Full message context with metadata, session state, and control methods |
| `next` | `MessagePipelineDelegate` | Delegate to invoke the next behavior or terminal handler |
| `cancellationToken` | `CancellationToken` | Cancellation token |

Call `await next(context, ct)` to continue the pipeline. Skip it to short-circuit.

### Built-in Middleware

| Middleware | Description |
|---|---|
| `LoggingMiddleware` | Logs message processing with timing, event metadata, and outcome |
| `ValidationMiddleware` | Dead-letters messages with missing EventId or EventTypeId |

Consumer-side metrics (`nimbus.message.received`, `nimbus.message.processed`, `nimbus.message.process.duration`) and the `NimBus.Process` span are emitted by `NimBusConsumerInstrumentation` from the transport adapter, not by a pipeline behavior — every subscriber path gets them regardless of pipeline registration.

### IMessageLifecycleObserver

```csharp
public interface IMessageLifecycleObserver
{
    Task OnMessageReceived(MessageLifecycleContext context, CancellationToken ct = default);
    Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken ct = default);
    Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken ct = default);
    Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception exception = null, CancellationToken ct = default);
}
```

All methods have default no-op implementations. Override only the hooks you need.

---

## Retry Policies

### RetryPolicy

```csharp
public class RetryPolicy
{
    public int MaxRetries { get; set; }
    public BackoffStrategy Strategy { get; set; } = BackoffStrategy.Fixed;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan? MaxDelay { get; set; }
    public JitterMode Jitter { get; set; } = JitterMode.None;
    public double BoundedJitterFactor { get; set; } = 0.25;

    public TimeSpan GetDelay(int retryAttempt, Random? rng = null);
}
```

### BackoffStrategy

| Strategy | Delay Calculation |
|---|---|
| `Fixed` | Always `BaseDelay` |
| `Linear` | `BaseDelay × (attempt + 1)` |
| `Exponential` | `BaseDelay × 2^attempt` |

### JitterMode

| Mode | Delay |
|---|---|
| `None` | The calculated delay `d` |
| `Full` | `d × (1 + U[0, 1))` |
| `Bounded` | `d × (1 + U[0, BoundedJitterFactor))` |

`MaxDelay` caps the delay after jitter.

### Registration

`ConfigureRetryPolicies` configures a `DefaultRetryPolicyProvider`:

```csharp
sub.ConfigureRetryPolicies(policies =>
{
    // Exception type: the exception or any inner exception, subclasses included
    policies.AddExceptionRule<PartnerApiUnavailableException>(new RetryPolicy { ... });

    // Exception text: a case-insensitive substring of "{exception.InnerException} {exception}"
    policies.AddExceptionRule("timeout", new RetryPolicy { ... });

    // Either kind of exception rule, limited to some event types
    policies.AddExceptionRule<PartnerApiThrottledException>(new RetryPolicy { ... }, "OrderPlaced", "OrderCancelled");

    // Per event type
    policies.AddEventTypePolicy("OrderPlaced", new RetryPolicy { ... });

    // Default fallback
    policies.SetDefaultPolicy(new RetryPolicy { ... });
});
```

Exception rules, typed and text, are checked in registration order and the
first match wins. Then the event type policy applies, then the default. With no
match, the message is not retried. Text rules see stack traces too, so a short
fragment can match unrelated text; prefer typed rules for exceptions you own.

### IRetryAfterHint

```csharp
public interface IRetryAfterHint
{
    TimeSpan? RetryAfter { get; }
}
```

When the handler's exception, or an inner exception, implements
`IRetryAfterHint`, the retry is scheduled after the longer of the policy delay
and `RetryAfter`, capped at the policy's `MaxDelay`. The policy still decides
whether a retry happens. Implement it on an exception that carries an HTTP
`Retry-After` value.

### IRetryPolicyProvider

```csharp
public interface IRetryPolicyProvider
{
    RetryPolicy GetRetryPolicy(string eventTypeId, string exceptionMessage, string? endpoint = null);

    RetryPolicy GetRetryPolicy(string eventTypeId, Exception exception, string? endpoint = null) =>
        GetRetryPolicy(eventTypeId, $"{exception?.InnerException} {exception}", endpoint);
}
```

The message handler calls the `Exception` overload. Its default implementation
forwards to the string overload, so existing custom providers keep working;
override it to match on types. A `null` policy means no retry.

See [building-adapters.md#resilience](building-adapters.md#resilience) for how
an adapter combines these.

---

## Outbox

### IOutbox

```csharp
public interface IOutbox
{
    Task StoreAsync(OutboxMessage message, CancellationToken ct = default);
    Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int maxCount, CancellationToken ct = default);
    Task MarkAsDispatchedAsync(string messageId, CancellationToken ct = default);
    Task MarkAsDispatchedAsync(IEnumerable<string> messageIds, CancellationToken ct = default);
}
```

Custom providers must return pending rows in creation order, and rows that
share a `CreatedAtUtc` in the order they were stored: the dispatcher sends rows
in exactly this order, so per-session FIFO depends on it. Providers must also
implement both checkpoint overloads idempotently. Repeating a
checkpoint—including after a partially committed batch—must succeed and
preserve each row's original dispatch timestamp. Provider authors can execute
this contract by deriving a test fixture from
`NimBus.Testing.Conformance.OutboxConformanceTests`.

SQL Server implementation: `NimBus.Outbox.SqlServer`. It dispatches rows in the
order they were stored, using a `SequenceNumber` that SQL Server assigns on
insert, so the order never depends on publisher clocks. `EnsureTableExistsAsync`
adds the column, its sequence and the dispatch-order index to tables created by
earlier versions; rows already pending at the upgrade dispatch first, in their
original order. If you manage the schema yourself, apply the DDL that
`EnsureTableExistsAsync` runs. Register via:

```csharp
services.AddNimBusSqlServerOutbox(options =>
{
    options.ConnectionString = "...";
    options.TableName = "Outbox";
});
```

---

## IMessageContext

Full context available to middleware and internal handlers:

| Property | Type | Description |
|---|---|---|
| `EventId` | `string` | Unique event identifier |
| `MessageId` | `string` | Unique message identifier |
| `EventTypeId` | `string` | Authoritative routing type; legacy native messages fall back to the body value when the application property is absent |
| `MessageType` | `MessageType` | Enum: EventRequest, RetryRequest, etc. |
| `SessionId` | `string` | Session for ordered processing |
| `CorrelationId` | `string` | Correlation tracking |
| `From` | `string` | Sender endpoint |
| `To` | `string` | Receiver endpoint |
| `EnqueuedTimeUtc` | `DateTime` | When message was enqueued |
| `RetryCount` | `int?` | Current retry attempt |
| `MessageContent` | `MessageContent` | Event payload and error content |

Control methods: `Complete()`, `Abandon()`, `DeadLetter()`, `Defer()`, `BlockSession()`, `UnblockSession()`.

---

## Message Scheduling

Schedule messages for future delivery and cancel them if no longer needed.

### ISender

```csharp
Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default);
Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default);
```

### PublisherClient

```csharp
// Schedule an event for 30 minutes from now
var seq = await publisher.Schedule(orderReminder, DateTimeOffset.UtcNow.AddMinutes(30));

// Cancel if no longer needed (e.g., payment received before timeout)
await publisher.CancelScheduled(seq);
```

### Outbox integration

When using the transactional outbox, scheduled messages are persisted with `ScheduledEnqueueTimeUtc` and dispatched via `ScheduleMessage` by the `OutboxDispatcher`. Note: `CancelScheduledMessage` throws `NotSupportedException` in outbox mode because the Service Bus sequence number is only assigned after dispatch.

For process-manager identity, state/outbox atomicity, and stale-safe timeout
handling, see [Application-Level Orchestration](orchestration.md).

---

## Request/Response

Send a typed request and await a typed response with timeout handling. Uses Azure Service Bus sessions for reply correlation.

### PublisherClient

```csharp
var status = await publisher.Request<GetOrderStatus, OrderStatusResponse>(
    new GetOrderStatus { OrderId = orderId },
    timeout: TimeSpan.FromSeconds(30));
```

| Parameter | Type | Description |
|---|---|---|
| `request` | `TRequest` | The request event (must extend `Event`) |
| `timeout` | `TimeSpan` | Maximum time to wait for a response |
| `cancellationToken` | `CancellationToken` | Optional cancellation |

Returns `TResponse`, or throws `TimeoutException` if no response is received within the timeout, or `RequestReplyException` when the responder's handler failed (the error reply carries the remote exception's type and message so callers fail fast instead of waiting out the timeout).

**Requirements:** The `PublisherClient` must know its endpoint identity and have a `ServiceBusClient` — `AddNimBusPublisher` registrations, `CreateAsync`, and the `ServiceBusClient` constructor all qualify. The reply arrives on the publisher's own `{endpoint}-reply` session subscription, provisioned automatically by `nb topology apply`.

### How it works

1. Requester generates a unique `replySessionId` and stamps `ReplyTo = {its own endpoint}` and `ReplyToSessionId` on the outgoing request (which routes by event type like any event)
2. Requester opens a `ServiceBusSessionReceiver` on its own `{endpoint}-reply` subscription for that session id
3. Responder's `RequestJsonHandler` invokes the registered `IRequestHandler` and sends the JSON-serialized response as a raw session message to the `ReplyTo` topic (application property `To = '{endpoint}-reply'` matches the reply subscription's rule and nothing else — replies are not NimBus events and are not audited)
4. Requester receives the reply, checks its `ReplyStatus`, deserializes the body as `TResponse`, and returns

### IRequestHandler\<TRequest, TResponse\>

```csharp
public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IEvent
    where TResponse : class
{
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken = default);
}
```

### Defining request and response types

```csharp
// Request — a normal NimBus event
[Description("Query the current status of an order.")]
[SessionKey(nameof(OrderId))]
public class GetOrderStatus : Event
{
    [Required]
    public Guid OrderId { get; set; }
}

// Response — a plain C# class (serialized as JSON)
public class OrderStatusResponse
{
    public Guid OrderId { get; set; }
    public string Status { get; set; }
    public DateTime LastUpdated { get; set; }
}
```

### Implementing a request handler

```csharp
public class GetOrderStatusHandler : IRequestHandler<GetOrderStatus, OrderStatusResponse>
{
    private readonly IOrderRepository _repository;

    public GetOrderStatusHandler(IOrderRepository repository)
    {
        _repository = repository;
    }

    public async Task<OrderStatusResponse> Handle(GetOrderStatus request, CancellationToken ct)
    {
        var order = await _repository.GetByIdAsync(request.OrderId, ct);
        return new OrderStatusResponse
        {
            OrderId = order.Id,
            Status = order.Status,
            LastUpdated = order.UpdatedAt
        };
    }
}
```

### Registering the responder

```csharp
services.AddNimBusSubscriber("OrdersEndpoint", sub =>
{
    sub.AddRequestHandler<GetOrderStatus, OrderStatusResponse, GetOrderStatusHandler>();
});
```

The handler is resolved from DI per message. If the request type is published as a plain event (no `ReplyTo`), the handler still runs and the response is discarded. A handler exception sends a best-effort error reply and then rethrows — the normal Resolver audit, retry policy, and session-blocking behavior are unchanged.

In-memory testing: `NimBusTestFixture.RegisterRequestHandler` dispatches requests through the same `RequestJsonHandler` and captures replies on `fixture.ReplyDispatcher.SentReplies`; the live `Request` session receive itself needs real Service Bus.

### Message model properties

| Property | Type | Description |
|---|---|---|
| `ReplyTo` | `string` | Reply destination set by the requester |
| `ReplyToSessionId` | `string` | Session ID for reply correlation |

These are set automatically by `PublisherClient.Request()` and mapped to the corresponding `ServiceBusMessage` properties.

### Error handling

| Scenario | Behavior |
|---|---|
| No response within timeout | Throws `TimeoutException` |
| Responder's handler threw | Throws `RequestReplyException` (carries remote `ErrorType`/`ErrorText`) |
| Caller cancels via `CancellationToken` | Throws `OperationCanceledException` |
| No `ServiceBusClient` or endpoint identity | Throws `InvalidOperationException` |
| Reply subscription missing | Throws `InvalidOperationException` with `nb topology apply` guidance |

### Limitations

- The `{endpoint}-reply` session subscription must exist (created by `nb topology apply` and the emulator topology; endpoint ids must stay ≤ 44 characters to fit Service Bus's 50-character subscription-name cap)
- Not supported via the transactional outbox (request/response is inherently synchronous)
- Each request creates and disposes a `ServiceBusSessionReceiver` — not suited for high-throughput scenarios (use pub/sub for that)
- **At most one reply per request.** With inbox deduplication enabled on the responder, a redelivered duplicate request skips the handler and sends no reply — the requester times out. Orphaned replies expire after 5 minutes (message TTL)
- Give a request type exactly one consuming endpoint, or multiple responders will race to reply (first reply wins). Deriving request types from `Command` enforces this at provisioning time

---

## Permanent Failure Classification

Classify exceptions as permanent (unrecoverable) to dead-letter immediately without consuming retry budget.

`DefaultPermanentFailureClassifier` is an `IFailureDispositionClassifier` that maps
permanent exceptions to `FailureDisposition.DeadLetter` and everything else to
`FailureDisposition.Retry`. (The `IPermanentFailureClassifier` interface was removed in
v4.0.0.)

### Default permanent types

`DefaultPermanentFailureClassifier` classifies these as permanent:
- `FormatException`, `InvalidCastException`, `ArgumentException` (and subtypes), `NotSupportedException`
- Exception type names containing: `Serialization`, `Deserialization`, `Validation`

### Registration

```csharp
// Option 1: Use defaults
services.AddNimBusSubscriber("BillingEndpoint", sub =>
    sub.WithFailureDispositions(new DefaultPermanentFailureClassifier()));

// Option 2: Configure via subscriber builder
services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<OrderPlacedHandler>();
    sub.ConfigurePermanentFailureClassifier(classifier =>
    {
        classifier.AddPermanentExceptionType<MyBusinessRuleException>();
        classifier.AddPermanentExceptionNamePattern("Constraint");
    });
});
```

**Behavior:** When a handler throws a classified permanent exception, the message is dead-lettered immediately. No `RetryRequest` is sent, no session is blocked, and the retry budget is not consumed.

---

## Health Checks

Standard `IHealthCheck` implementations for ASP.NET Core.

### Registration

Both extensions hang off `IHealthChecksBuilder` and resolve their client from DI:

```csharp
var healthChecks = services.AddHealthChecks()
    .AddServiceBusHealthCheck();   // checks ServiceBusClient.IsClosed
healthChecks.AddCosmosDbHealthCheck();  // calls ReadAccountAsync
```

### Checking whether the platform is alive

These probes cover the transport and the store, not the platform's own liveness.
For "is the Resolver running?" and "are my endpoints reachable?", use the
platform heartbeat (the Heartbeat page and Settings → Heartbeat probing) — it probes every catalog endpoint
without an adapter-side handler and settles a Resolver liveness probe on every
tick. See [heartbeat.md](heartbeat.md).

### Endpoints

| Path | Purpose |
|---|---|
| `/health` | All health checks |
| `/alive` | Liveness checks (tagged `"live"`) |
| `/ready` | Readiness checks (tagged `"ready"`) |

---

## In-Memory Testing

`NimBus.Testing` provides an in-memory transport for running the full pipeline without Azure Service Bus.

### InMemoryMessageBus

```csharp
var bus = new InMemoryMessageBus();
var publisher = new PublisherClient(bus);

// Publish events
await publisher.Publish(new OrderPlaced { OrderId = Guid.NewGuid() });

// Deliver to handler
await bus.DeliverAll(messageHandler);

// Assert
Assert.AreEqual(1, bus.SentMessages.Count);
```

### NimBusTestFixture

```csharp
var fixture = new NimBusTestFixture();
fixture.RegisterHandler<OrderPlaced>(() => new OrderPlacedHandler());

await fixture.Publisher.Publish(order);
await fixture.DeliverAll();
```

### DI Registration

```csharp
services.AddNimBusTestTransport(sub =>
    sub.AddHandlersFromAssemblyContaining<OrderPlacedHandler>());  // replaces Service Bus with in-memory
```

See [building-adapters.md#adapter-testing](building-adapters.md#adapter-testing)
for testing an adapter's handlers, failure mapping, retry rules and circuit
breaker.

### Scheduled Messages (testing)

```csharp
var bus = new InMemoryMessageBus();
var seq = await bus.ScheduleMessage(message, DateTimeOffset.UtcNow.AddHours(1));

Assert.AreEqual(1, bus.ScheduledMessages.Count);

await bus.CancelScheduledMessage(seq);
Assert.AreEqual(0, bus.ScheduledMessages.Count);
```

---

## Identity Extension

`NimBus.Extensions.Identity` adds username/password authentication with email verification as an alternative to Entra ID. Opt-in — the core WebApp has no SQL Server dependency without it.

### Registration

```csharp
services.AddNimBusIdentity(options =>
{
    options.ConnectionString = "Server=...";
    options.RequireEmailConfirmation = true;
    options.EnableEntraIdLogin = true;  // show "Sign in with Microsoft" alongside local login
    options.Smtp = new SmtpOptions
    {
        Host = "smtp.example.com",
        Port = 587,
        FromAddress = "nimbus@example.com"
    };
});
```

### Auth modes

| Mode | Condition |
|---|---|
| Entra ID only | No Identity registered (default) |
| Identity only | Identity registered, no AzureAd config |
| Dual | Both configured — login page shows both options |

### Claims mapping

Identity users are automatically mapped to the same claims the WebApp's `EndpointAuthorizationService` checks (`oid`, `name`, `groups`). No changes to authorization logic needed.

---

## Key Source Files

| Component | File |
|---|---|
| IPublisherClient | `src/NimBus.SDK/IPublisherClient.cs` |
| ISubscriberClient | `src/NimBus.SDK/ISubscriberClient.cs` |
| IEventHandler\<T\> | `src/NimBus.SDK/EventHandlers/IEventHandler.cs` |
| IEventHandlerContext | `src/NimBus.SDK/EventHandlers/IEventHandlerContext.cs` |
| IMessagePipelineBehavior | `src/NimBus.Core/Extensions/IMessagePipelineBehavior.cs` |
| IMessageLifecycleObserver | `src/NimBus.Core/Extensions/IMessageLifecycleObserver.cs` |
| RetryPolicy | `src/NimBus.Core/Messages/RetryPolicy.cs` |
| IOutbox | `src/NimBus.Core/Outbox/IOutbox.cs` |
| DI extensions | `src/NimBus.SDK/Extensions/ServiceCollectionExtensions.cs` |
| Built-in middleware | `src/NimBus.Core/Pipeline/` |
