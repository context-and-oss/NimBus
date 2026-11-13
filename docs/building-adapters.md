# Building Adapters

This guide explains how to build a **NimBus adapter**: a process that connects an
external system to the NimBus event bus. An adapter can publish events, subscribe
to events, or do both in the same host.

Use this page when you are building a real integration and need to decide how to
wire hosting, handlers, retries, the outbox, deferred-message replay,
observability and resilience, how to test the adapter, and how to run a local
stack from packages. For the first "hello world" path, start with
[getting-started.md](getting-started.md). For API details, see
[sdk-api-reference.md](sdk-api-reference.md).

## Adapter responsibilities

An adapter usually owns four things:

- **Translation** between external-system models and NimBus event contracts.
- **Publishing** events when its backing system changes.
- **Subscribing** to events published by other endpoints.
- **Reliability boundaries**, including retries, idempotency, and optionally a
  transactional outbox for publish-after-commit scenarios.

Adapters are independent deployables. They use `NimBus.SDK`, Azure Service Bus,
normal .NET dependency injection, and whatever clients they need for the external
system. They do not share in-memory state with the Resolver or WebApp.

Architecture context for Resolver, WebApp, message state, and sessions lives in
[architecture.md](architecture.md). This guide stays focused on adapter code.

## Quick decision guide

### What is the adapter doing?

| Adapter shape | Register |
| --- | --- |
| Publish only | `AddNimBusPublisher("EndpointName")` |
| Subscribe only | `AddNimBus(...)`, `AddNimBusSubscriber(...)`, and a receiver/trigger |
| Publish and subscribe | All of the above |
| Publish after local DB commit | Add `NimBus.Outbox.SqlServer` and the outbox dispatcher |

### How should it be hosted?

| Host | Use when | Sample |
| --- | --- | --- |
| Long-running Worker | Simple local debugging, stateful adapters, in-process outbox dispatcher, explicit control over background services, a circuit breaker that pauses receiving | [`samples/DynamicsBcDemo/D365Sales.Adapter/Program.cs`](../samples/DynamicsBcDemo/D365Sales.Adapter/Program.cs) (production shape), [`samples/CrmErpDemo/Crm.Adapter/Program.cs`](../samples/CrmErpDemo/Crm.Adapter/Program.cs) (minimal) |
| Azure Functions isolated worker | Serverless scaling, bursty workloads, session-trigger based consumption, no always-on process | [`samples/CrmErpDemo/Erp.Adapter.Functions/Program.cs`](../samples/CrmErpDemo/Erp.Adapter.Functions/Program.cs) |

Functions-specific setup is covered in
[azure-functions-hosting.md](azure-functions-hosting.md).

### Do you need an outbox?

| Publish path | Use when | Tradeoff |
| --- | --- | --- |
| Direct publish | The adapter does not need to make local DB writes atomic with event publishing | Lower I/O and simpler wiring |
| SQL Server outbox | The adapter writes local state and must only publish if that write commits | Adds table storage and a dispatcher, but closes the commit-then-crash gap |

The outbox is a publisher-side reliability feature. Subscriber handlers still
need to be idempotent because Service Bus delivery, retries, operator resubmit,
and dispatcher recovery are all at-least-once paths.

## Endpoint and contract checklist

Before wiring `Program.cs`, settle these items:

- **Endpoint name**: the NimBus endpoint/topic name, for example
  `CrmEndpoint` or `ErpEndpoint`.
- **Subscription name**: usually the same as the endpoint for the main
  subscriber.
- **Session key**: every event must produce the session ID that should preserve
  ordering. Use `GetSessionId()` or `[SessionKey]`.
- **Event identity**: publish with a stable `MessageId` when the source system
  already has a natural idempotency key.
- **Topology**: create topics, subscriptions, rules, and deferred subscriptions
  before the adapter runs. The SDK does not auto-provision Service Bus topology.
- **Several targets of one kind**: when one event must reach one of several
  instances (one ERP per country, for example), give each instance its own
  endpoint and route by a payload field. See [Routing keys](routing-keys.md).

Example event:

```csharp
using NimBus.Core.Events;

public sealed class AccountCreated : Event
{
    public required string AccountId { get; init; }
    public required string Name { get; init; }

    public override string GetSessionId() => AccountId;
}
```

## Common bootstrap

Most adapters start by creating a host and registering an Azure
`ServiceBusClient`.

Aspire-managed Worker:

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");
```

Manual registration, common in Functions and non-Aspire hosts:

```csharp
builder.Services.AddSingleton<ServiceBusClient>(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>()["AzureWebJobsServiceBus"]
        ?? throw new InvalidOperationException("AzureWebJobsServiceBus is required.");

    return new ServiceBusClient(connectionString);
});
```

`ServiceBusClient` must be in DI before `AddNimBusPublisher`,
`AddNimBusSubscriber`, or `AddNimBusReceiver` are used.

## Publisher setup

Register the publisher for the endpoint whose topic receives messages:

```csharp
builder.Services.AddNimBusPublisher("CrmEndpoint");
```

This registers:

- `IPublisherClient`, the usual service to inject into application code.
- `ISender`, the lower-level NimBus sender abstraction.
- NimBus OpenTelemetry runtime components for publish instrumentation.

Publishing from application code:

```csharp
public sealed class CrmEventPublisher(IPublisherClient publisher)
{
    public Task AccountCreatedAsync(Account account)
    {
        var evt = new AccountCreated
        {
            AccountId = account.Id,
            Name = account.Name,
        };

        return publisher.Publish(evt);
    }
}
```

If the external system gives you a stable idempotency key, pass it as the
message ID:

```csharp
await publisher.Publish(
    evt,
    sessionId: evt.AccountId,
    correlationId: command.CorrelationId,
    messageId: $"crm-account-created-{account.Id}");
```

`IPublisherClient` supports:

| Method | Use |
| --- | --- |
| `Publish(IEvent)` | Publish one event using the event's session ID and a new correlation ID |
| `Publish(event, sessionId, correlationId)` | Override the session and correlation IDs |
| `Publish(event, sessionId, correlationId, messageId)` | Also provide an explicit idempotency-oriented `MessageId` |
| `PublishFromContext(event, context, messageId, cancellationToken)` | Publish a workflow follow-up with a deterministic ID while preserving inbound session, correlation, and lineage |
| `PublishBatches(IEnumerable<IEvent>, correlationId)` | **Preferred for bulk publish.** Sends any number of events, automatically paged to the Service Bus batch size; each event is serialized exactly once |
| `PublishBatch(IEnumerable<IEvent>, correlationId)` | Send multiple events in one Service Bus batch — the caller must respect transport size limits |
| `GetBatches(List<IEvent>)` | Split a list into transport-sized batches before publishing (legacy pairing with `PublishBatch`; prefer `PublishBatches`) |
| `Request<TRequest,TResponse>(...)` | Request/response over Service Bus sessions; requires a `PublisherClient` created with a `ServiceBusClient` |

`PublisherClient` also has concrete `Schedule(...)` and `CancelScheduled(...)`
methods. Those methods are not on `IPublisherClient`, so inject or create the
concrete client only when scheduled publish is part of the adapter contract.

## Subscriber setup

A subscriber needs three pieces:

- Handler registrations via `AddNimBusSubscriber(...)`.
- Pipeline/lifecycle configuration via `AddNimBus(...)`.
- A receive loop: `AddNimBusReceiver(...)` in a Worker, or a
  `[ServiceBusTrigger]` function in Azure Functions.

### Register the NimBus pipeline

`AddNimBus(...)` is optional. Call it when you want to register pipeline
behaviors or lifecycle observers; skip it entirely for a no-middleware
subscriber:

```csharp
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});
```

Adapters do not register a NimBus message-store provider. Storage providers
(`AddCosmosDbMessageStore`, `AddSqlServerMessageStore`) are for platform hosts
such as the Resolver and WebApp; they are not part of normal adapter wiring.

### Write handlers

Handlers are regular DI-created classes:

```csharp
public sealed class AccountCreatedHandler(ICrmApiClient crm, ILogger<AccountCreatedHandler> log)
    : IEventHandler<AccountCreated>
{
    public async Task Handle(AccountCreated message, IEventHandlerContext context, CancellationToken ct)
    {
        log.LogInformation(
            "Handling {EventType} {EventId} for account {AccountId}",
            context.EventType,
            context.EventId,
            message.AccountId);

        await crm.CreateAccountAsync(message.AccountId, message.Name, ct);
    }
}
```

`IEventHandlerContext` exposes `MessageId`, `EventId`, `EventType`, `SessionId`,
`CorrelationId`, `ParentMessageId`, and `OriginatingMessageId`. Pass it to
`PublishFromContext(...)` for workflow follow-ups; the method requires an
explicit deterministic outgoing message ID and does not rely on ambient state.
The context also exposes `MarkPendingHandoff(...)` for integrations that start
long-running external work and need the message recorded as pending rather than
failed. See [error-handling.md](error-handling.md) and the pending handoff
[ADR](adr/012-pending-handoff.md) for the operational implications.

### Register handlers

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<AccountCreatedHandler>();
});
```

`AddHandlersFromAssemblyContaining<TMarker>()` scans the marker type's assembly
for concrete `IEventHandler<TEvent>` implementations and registers them with the
same transient lifetime as explicit handler registrations. The implemented
interface is the source of truth; handler class names do not need to follow a
specific convention.

Use explicit registration when you want narrow control or need to override a
scanned handler. Put explicit overrides before the scan when the override type
lives in the scanned assembly:

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, SpecialAccountCreatedHandler>();
    sub.AddHandlersFromAssemblyContaining<AccountCreatedHandler>();
});
```

If scanning finds more than one handler for the same event type, NimBus fails
fast with a clear error. Register the intended handler explicitly to resolve the
ambiguity.

`AddNimBusSubscriber` registers:

- `ISubscriberClient`, which dispatches Service Bus messages through NimBus.
- The handler provider and handler registrations.
- The retry-policy provider when configured.
- A default `IDeferredMessageProcessor` for the endpoint's deferred replay path.

## Worker receiver

For a long-running Worker, add the hosted receiver:

```csharp
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 32;
    opts.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5);
});
```

| Option | Default | Notes |
| --- | --- | --- |
| `TopicName` | Required | Service Bus topic to receive from |
| `SubscriptionName` | Required | Subscription within the topic |
| `MaxConcurrentSessions` | `8` | Number of sessions processed concurrently. Increase this for busy endpoints. |
| `MaxAutoLockRenewalDuration` | `5 min` | Maximum lock renewal duration for long-running handlers |
| `SessionIdleTimeout` | `30 s` | How long an idle session is held before the receiver rotates to another |
| `PrefetchCount` | `0` | Messages fetched ahead of processing. Opt-in: a large win for small, fast-handler workloads, but prefetched messages hold their locks, so leave it at `0` for handlers slower than ~1 s |

Per-session ordering is preserved. `MaxConcurrentSessions` only increases
parallelism across different sessions — effective parallelism is
`min(MaxConcurrentSessions, distinct active session IDs)`, so a low-cardinality
session key caps throughput no matter how high this is set. See
[Throughput Tuning](throughput-tuning.md) for recommended values per workload
profile and the other dials (lock duration, prefetch, tier).

## Azure Functions receiver

In Azure Functions, do not call `AddNimBusReceiver`. Register
`AddNimBusSubscriber(...)` in `Program.cs`, then add a Service Bus trigger that
delegates to `ISubscriberClient`:

```csharp
public sealed class CrmEndpointFunction(ISubscriberClient subscriber)
{
    [Function("CrmEndpoint")]
    public Task RunAsync(
        [ServiceBusTrigger(
            "%TopicName%",
            "%SubscriptionName%",
            Connection = "AzureWebJobsServiceBus",
            IsSessionsEnabled = true)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        ServiceBusSessionMessageActions sessionActions,
        CancellationToken ct) =>
        subscriber.Handle(message, messageActions, sessionActions, ct);
}
```

Both pieces are required:

- `AddNimBusSubscriber(...)` configures handlers and the dispatch pipeline.
- `[ServiceBusTrigger]` feeds Service Bus messages into that pipeline.

Missing either one leaves the adapter configured but not consuming messages. See
[azure-functions-hosting.md](azure-functions-hosting.md) for `host.json`,
session settings, local settings, and the deferred processor function.

## Retry and permanent failures

Retry policies are configured per subscriber. Match them on the exception types
your client throws:

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, AccountCreatedHandler>();

    sub.ConfigureRetryPolicies(policies =>
    {
        policies.AddExceptionRule<CrmUnavailableException>(new RetryPolicy
        {
            MaxRetries = 5,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(30),
            MaxDelay = TimeSpan.FromMinutes(10),
            Jitter = JitterMode.Bounded,
        });
    });
});
```

`DefaultRetryPolicyProvider` resolves a policy in this order:

| Registration | Matches |
| --- | --- |
| `AddExceptionRule<TException>(policy, eventTypeIds)` | The handler's exception, or any inner exception, is a `TException` (subclasses too) |
| `AddExceptionRule(text, policy, eventTypeIds)` | The text appears, ignoring case, in `$"{exception.InnerException} {exception}"` |
| `AddEventTypePolicy(eventTypeId, policy)` | Any failure of that event type |
| `SetDefaultPolicy(policy)` | Everything else |

Typed and text exception rules are checked together in registration order, and
the first match wins. Pass event type IDs to scope a rule to those events. When
nothing matches, the message is not retried: it fails and its session stays
blocked until an operator resubmits or skips it.

Prefer typed rules. The text that string rules search includes type names,
messages and stack traces, so a short fragment such as `"429"` can match a line
number, an ID or a response body.

When the exception, or an inner exception, implements `IRetryAfterHint`, the
scheduled retry waits for the longer of the policy delay and the hint, capped at
`MaxDelay`. See [Resilience](#resilience).

Use a permanent failure classifier for exceptions that should bypass retries and
go directly to dead-letter handling:

```csharp
builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandler<AccountCreated, AccountCreatedHandler>();

    sub.ConfigurePermanentFailureClassifier(classifier =>
    {
        classifier.AddPermanentExceptionType<InvalidPayloadException>();
    });
});
```

Once configured, the classifier also dead-letters `FormatException`,
`InvalidCastException`, `ArgumentException`, `NotSupportedException` and any
exception whose type name contains `Validation`, `Serialization` or
`Deserialization`. Name your own exception types with that in mind.

The full exception routing model is documented in
[error-handling.md](error-handling.md).

## Deferred messages

NimBus uses Service Bus sessions to preserve ordering. When a session is blocked
by a failed or pending message, later messages in that session are parked on the
deferred subscription and replayed after the blocking message is resolved.

`AddNimBusSubscriber(...)` registers the default `IDeferredMessageProcessor`, but
the adapter still needs something to drive replay:

- In a Worker, add a hosted service like
  [`Crm.Adapter/Program.cs`](../samples/CrmErpDemo/Crm.Adapter/Program.cs),
  which registers `AddNimBusDeferredProcessorHostedService(...)`.
- In Azure Functions, add a second non-session `[ServiceBusTrigger]` like
  [`ErpDeferredProcessorFunction.cs`](../samples/CrmErpDemo/Erp.Adapter.Functions/Functions/ErpDeferredProcessorFunction.cs).

The mechanics are covered in [deferred-messages.md](deferred-messages.md).

## Transactional outbox

Use the SQL Server outbox when the adapter must publish only after a local SQL
transaction commits:

```csharp
builder.Services.AddNimBusSqlServerOutbox(crmConnectionString);

builder.Services.AddSingleton<OutboxDispatcherSender>(sp =>
{
    var client = sp.GetRequiredService<ServiceBusClient>();
    return new OutboxDispatcherSender(client.CreateSender("CrmEndpoint"));
});

builder.Services.AddNimBusOutboxDispatcher(TimeSpan.FromSeconds(1));
builder.Services.AddNimBusPublisher("CrmEndpoint");
```

When `IOutbox` is registered, `AddNimBusPublisher` writes outgoing messages to
the outbox instead of sending directly. The dispatcher reads committed outbox
rows and sends them to Service Bus.

Create the outbox table on startup. The call is idempotent and also upgrades
tables created by earlier NimBus versions:

```csharp
var outbox = (SqlServerOutbox)host.Services.GetRequiredService<IOutbox>();
await outbox.EnsureTableExistsAsync();
```

Two important notes:

- Register `OutboxDispatcherSender`; the dispatcher needs a real Service Bus
  sender, not the outbox-decorated `ISender`.
- Outbox dispatch is at-least-once. Consumers must tolerate duplicates.

Complete samples are
[`samples/CrmErpDemo/Erp.Api/Program.cs`](../samples/CrmErpDemo/Erp.Api/Program.cs),
which also creates the table on startup, and
[`samples/DynamicsBcDemo/BusinessCentral.Api/Program.cs`](../samples/DynamicsBcDemo/BusinessCentral.Api/Program.cs).
Both host the dispatcher in the API that owns the database, not in an adapter.

## Middleware and observers

NimBus dispatches received messages through `IMessagePipelineBehavior`
implementations before invoking the handler.

Built-in behaviors:

| Behavior | Purpose |
| --- | --- |
| `LoggingMiddleware` | Logs start, completion, failure, elapsed time, and message metadata |
| `ValidationMiddleware` | Dead-letters invalid messages, such as messages missing required event metadata |

Register middleware in the order you want it to wrap the handler:

```csharp
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});
```

Behaviors execute in registration order, outermost first. With the registration
above, the receive path is:

```text
Logging -> Validation -> Handler -> Validation -> Logging
```

For passive observation, use `IMessageLifecycleObserver` instead of middleware.
Observers can emit metrics, alerts, or audit records without changing dispatch
flow. Register observers with `n.AddLifecycleObserver<T>()`.

For custom middleware examples, short-circuiting, and observer details, see
[pipeline-middleware.md](pipeline-middleware.md).

## Observability

`AddNimBusPublisher(...)` registers NimBus instrumentation runtime components
for publisher-side decorators. Subscriber-only hosts can register the same
runtime package explicitly:

```csharp
builder.Services.AddNimBusInstrumentation();
```

To export spans and metrics, the host's OpenTelemetry setup must also add the
NimBus meters and activity sources:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddNimBusInstrumentation())
    .WithTracing(tracing => tracing.AddNimBusInstrumentation());
```

NimBus emits publisher, consumer, outbox, deferred processor, resolver, and store
instrumentation through the `NimBus.OpenTelemetry` package. Consumer spans and
metrics are emitted by the transport adapter; they are not middleware behaviors.

## Resilience

An adapter talks to an external system that throttles, goes down and rejects
data. Decide how each of those reaches NimBus, so that retries, the circuit
breaker and the audit trail all see the same failure.

The worked example is the pair of adapters in
[`samples/DynamicsBcDemo`](../samples/DynamicsBcDemo/README.md):
[`D365Sales.Adapter`](../samples/DynamicsBcDemo/D365Sales.Adapter/Program.cs) and
[`BusinessCentral.Adapter`](../samples/DynamicsBcDemo/BusinessCentral.Adapter/Program.cs)
share the shape described here. The CrmErpDemo adapters stay deliberately
minimal and do not follow it.

### Typed failures at the client boundary

Map every failed call to an exception type inside the client, not in handlers.
Three types cover most systems:

| Failure | Exception | NimBus treatment |
| --- | --- | --- |
| 429 | Throttled, carrying the `Retry-After` delay | Retried; not counted by the circuit breaker |
| 408, 502, 503, 504, other 5xx, timeout, no connection | Unavailable | Retried; counted by the circuit breaker |
| Any other 4xx | Rejected, carrying the status and the vendor's error code | Not retried; the session blocks for an operator |

The Dataverse client in the sample does the mapping in one place
([`DataverseClient.cs`](../samples/DynamicsBcDemo/D365Sales.Adapter/Clients/DataverseClient.cs)):

```csharp
var message = await response.DescribeFailureAsync(operation, ApiName, cancellationToken);
var status = (int)response.StatusCode;
throw status switch
{
    429 => new DataverseThrottledException(message, response.Headers.RetryAfter?.Delta),
    408 or 502 or 503 or 504 => new DataverseUnavailableException(message),
    >= 400 and < 500 => new DataverseRequestRejectedException(message, status, await ReadErrorCodeAsync(response, cancellationToken)),
    _ => new DataverseUnavailableException(message),
};
```

An `HttpRequestException`, or a `TaskCanceledException` the caller did not
request, becomes `DataverseUnavailableException` with the original as the inner
exception. The three types share an abstract base class
([`DataverseExceptions.cs`](../samples/DynamicsBcDemo/D365Sales.Adapter/Clients/DataverseExceptions.cs)).

NimBus records only the exception's type name and message. Your properties are
not stored, and neither is the stack trace unless the subscriber opts in (see
below), so put what an operator needs into the message:
the operation, the HTTP method and path, the status, and a truncated response
body. The sample's messages read like this:

```text
Update opportunity 0bb0…0105 failed: Dataverse API PATCH /api/data/v9.2/opportunities(0bb0…0105) → 404 Not Found. Body: {"error":{"code":"0x80040217",…}}
```

To let operators, and failure-handling agents an Owner allows, locate a defect in
your code, a subscriber can store the stack trace of the exception its handler
threw on the error response
([Spec 044](spec/044-agent-first/spec.md) Phase 3). It is off by default:

```csharp
services.AddNimBusSubscriber(
    options =>
    {
        options.Endpoint = "CrmEndpoint";
        options.IncludeStackTraceInErrorResponses = true;
    },
    sub => sub.AddHandlersFromAssemblyContaining<Program>());
```

Only the frames are stored (the message is already in the error text), cut at
8,000 characters. The WebApp shows the trace to endpoint Readers. An agent reads
it from `nimbus_get_failure_evidence` with `includeStackTrace` only when an Owner
turns on **Settings → Failure intelligence → Agents may see stack traces**; it is
then scrubbed (file paths cut to the file name, values marked sensitive and
credential-shaped text replaced) and never sent to the deprecated provider.

The message is kept in the audit trail and shown in the WebApp. Use record IDs,
not names, email addresses or other personal data.

Do not name these types with `Validation`, `Serialization` or `Deserialization`
in them. A configured `DefaultPermanentFailureClassifier` dead-letters any
exception whose type name contains one of those words, so a transient failure
would never be retried.

### Retry rules

Register one typed rule per transient type. From
[`D365Resilience.cs`](../samples/DynamicsBcDemo/D365Sales.Adapter/Resilience/D365Resilience.cs):

```csharp
policies
    .AddExceptionRule<DataverseThrottledException>(new RetryPolicy
    {
        MaxRetries = options.ThrottledMaxRetries,
        Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(options.ThrottledBaseDelaySeconds),
        MaxDelay = TimeSpan.FromSeconds(options.ThrottledMaxDelaySeconds),
        Jitter = JitterMode.Bounded,
    })
    .AddExceptionRule<DataverseUnavailableException>(new RetryPolicy
    {
        MaxRetries = options.UnavailableMaxRetries,
        Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(options.UnavailableBaseDelaySeconds),
        MaxDelay = TimeSpan.FromSeconds(options.UnavailableMaxDelaySeconds),
        Jitter = JitterMode.Bounded,
    });
```

A typed rule matches the exception or any inner exception, so it still matches
after the pipeline wraps the handler's exception. A text rule such as
`AddExceptionRule("429", ...)` searches type names, messages and stack traces;
a rejected request whose body mentions "429" would be retried.

There is deliberately no default policy. A rejected request is a data problem,
so its message fails, its session blocks, and later messages for the same
session wait on the deferred subscription. An operator fixes the data in the
external system and resubmits, or skips the message. That is usually the right
outcome for a 4xx. Add `SetDefaultPolicy(...)` only when every unclassified
failure is safe to retry.

### Retry-After

Implement `IRetryAfterHint` on the throttled exception:

```csharp
public sealed class DataverseThrottledException(string message, TimeSpan? retryAfter)
    : DataverseException(message), IRetryAfterHint
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
```

The scheduled retry then waits for the longer of the policy delay and
`RetryAfter`, capped at the policy's `MaxDelay`. The hint only stretches a delay.
The retry rule still decides whether there is a retry at all.

`response.Headers.RetryAfter?.Delta` reads the seconds form of the header. If
the system sends an HTTP date instead, compute the delay from
`RetryAfter.Date`.

### Deduplication with the inbox

Service Bus delivery, retries, resubmits and deferred replay are all
at-least-once. Turn on the [consumer inbox](inbox-pattern.md) so a redelivery of
a message that already succeeded is skipped:

```csharp
builder.Services.AddNimBusSqlServerInbox(options =>
{
    options.ConnectionString = nimbusDbConnectionString;
    options.TableName = "D365SalesInboxMessages";
});

builder.Services.AddNimBusSubscriber(
    configure: options => options.Endpoint = "D365SalesEndpoint",
    configureBuilder: sub =>
    {
        sub.AddHandlersFromAssemblyContaining<BcSalesQuoteCreatedHandler>();
        sub.UseInbox(inbox =>
        {
            inbox.DeduplicationStore = InboxStore.SqlServer;
            inbox.RetentionPeriod = TimeSpan.FromDays(2);
            inbox.CleanupInterval = TimeSpan.FromMinutes(15);
        });
    });
```

The inbox key is `(endpoint, MessageId)`, so it only works when publishers send
deterministic message IDs: the same change must always get the same
`MessageId`. See [Publisher setup](#publisher-setup).

Keep the inbox table in a database the integration platform owns, not in the
external system. A SaaS system such as Dataverse or Business Central gives you
no tables of your own, and its writes could not share a transaction with the
inbox record anyway. The inbox narrows the duplicate window; it does not close
it, so handlers still need idempotent writes (see below).

### Circuit breaker

Pause the adapter while the external system is down, and count only outages:

```csharp
circuit.MinimumThroughput = options.CircuitMinimumThroughput;
circuit.FailurePercentageThreshold = options.CircuitFailurePercentageThreshold;
circuit.SamplingWindow = TimeSpan.FromSeconds(options.CircuitSamplingWindowSeconds);
circuit.BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakDurationSeconds);
circuit.HalfOpenProbeCount = options.CircuitHalfOpenProbeCount;
circuit.Exclude<DataverseRequestRejectedException>();
circuit.Exclude<DataverseThrottledException>();
```

Throttling is paced by retries and a rejection is a data problem; neither means
the system is unhealthy.

Only a Worker can pause its receivers. An Azure Functions trigger owns its
receive loop and keeps receiving while the circuit is open (see
[circuit-breaker.md](circuit-breaker.md#azure-functions-limitation)). Host an
adapter that needs the breaker as a Worker, and keep `PrefetchCount = 0` on its
receiver: prefetched messages already count a delivery attempt, so each open
cycle would burn one.

### Retry budget and the message lock

Each delivery attempt holds a Service Bus lock. The receiver renews it up to
`MaxAutoLockRenewalDuration` (5 minutes by default). Everything that happens
inside one attempt has to fit in that window:

- retries inside a vendor SDK client;
- retries and timeouts in `HttpClient` handlers;
- the handler's own calls, each bounded by `HttpClient.Timeout`.

NimBus retries do not count against the lock. A retry is a broker-scheduled
message: the failed delivery completes, and the next attempt arrives later as a
new delivery.

This is why the external-system client should carry no resilience handler.
`builder.AddServiceDefaults()` from `Akaule.NimBus.ServiceDefaults` leaves the
standard HTTP resilience handler off unless you pass
`options => options.UseStandardResilienceHandler = true`. Do not opt in for the
external-system client. Its retries would run inside one NimBus attempt, hidden
from the retry rules, the circuit breaker and the audit trail, and could outlast
the lock. Opting in is fine for hosts whose outbound calls are not part of
message handling. Set a short `HttpClient.Timeout` (the samples use 15 seconds)
and turn vendor SDK retries down or off.

### Calling Entra-protected APIs

Dataverse, Business Central and Finance and Operations take Microsoft Entra
bearer tokens. `Akaule.NimBus.Extensions.Http` adds a cached token to a typed
client:

```csharp
builder.Services.AddSingleton<TokenCredential>(new DefaultAzureCredential());

builder.Services.AddHttpClient<IDataverseClient, DataverseClient>(client =>
    {
        client.BaseAddress = new Uri("https://contoso.crm.dynamics.com");
        client.Timeout = TimeSpan.FromSeconds(15);
    })
    .AddAzureBearerToken("https://contoso.crm.dynamics.com/.default");
```

`AddAzureBearerToken(scopes)` uses the `TokenCredential` registered in DI;
`AddAzureBearerToken(credential, scopes)` takes one directly. The token lives in
an `AzureAccessTokenCache` shared by every handler instance that
`IHttpClientFactory` creates for the registration. It is refreshed at the
token's `RefreshOn` time, or 5 minutes before it expires, and concurrent first
calls share one token request.

| System | Scope |
| --- | --- |
| Dataverse (Dynamics 365 Sales) | `https://{org}.crm.dynamics.com/.default` |
| Business Central | `https://api.businesscentral.dynamics.com/.default` |
| Finance and Operations | `https://{env}.operations.dynamics.com/.default` |

### Change detection, echoes and idempotent writes

On the publishing side, detect changes in one of two ways:

- **Thin notification, then re-read.** A webhook or plug-in sends only the
  record ID. The adapter reads the current record and publishes that.
  Duplicated or out-of-order notifications then publish current data, not
  stale data.
- **Watermark polling.** Query records modified after a stored watermark, in
  modification order, publish them, then advance the watermark.

Either way, derive the `MessageId` from the record and its version, for example
`$"crm-account-{id}-{versionNumber}"`, so a repeated publish is a duplicate the
inbox can drop.

When two adapters sync the same entity in both directions, a write by one
adapter shows up as a change in the other system and comes back. Write with a
dedicated integration identity (an application user) and ignore changes made by
that identity when you detect changes.

On the subscribing side, make writes idempotent. Upsert by an alternate key or
external ID instead of creating records. The Dataverse client upserts accounts
with `PATCH accounts(cs_bccustomerid={bcCustomerId})`, so a replay updates the
same row instead of creating a second one.

## Complete Worker shape

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// No resilience handler on the external-system client: AddServiceDefaults() leaves it
// off, and you should not opt in for this client. Retries belong to NimBus (see Resilience).
builder.Services.AddHttpClient<ICrmApiClient, CrmApiClient>();

builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber("CrmEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<AccountCreatedHandler>();
    sub.ConfigureRetryPolicies(CrmResilience.ConfigureRetries);
    sub.WithCircuitBreaker(CrmResilience.ConfigureCircuitBreaker);
});

builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 32;
    // With a circuit breaker, prefetched messages would burn a delivery attempt per open cycle.
    opts.PrefetchCount = 0;
});

// Replays messages parked behind a blocked session.
builder.Services.AddNimBusDeferredProcessorHostedService("CrmEndpoint");

builder.Services.AddNimBusPublisher("CrmEndpoint");

var host = builder.Build();
host.Run();
```

`CrmResilience` holds the retry rules and breaker settings, shaped like
[`D365Resilience.cs`](../samples/DynamicsBcDemo/D365Sales.Adapter/Resilience/D365Resilience.cs).
Add the outbox when publishing is coupled to a local database transaction, and
the inbox when the adapter has a database for it.

## Adapter testing

Most adapter behaviour can be tested without Service Bus. Most examples here come
from [`tests/DynamicsBcDemo.Tests`](../tests/DynamicsBcDemo.Tests). NimBus's own test
suite is described in [testing.md](testing.md).

### Handlers

Handlers are plain classes. Construct one with a fake client and assert on what
it wrote:

```csharp
var dataverse = new RecordingDataverseClient();

await new BcSalesQuoteCreatedHandler(dataverse).Handle(quote, null!, CancellationToken.None);

var account = dataverse.Single("account", AccountId);
Assert.AreEqual(2, account["cs_masterdataowner"]);
```

Pass a context only when the handler uses it.

### Failure mapping

Give the real client an `HttpClient` over a stub `HttpMessageHandler` and check
which exception each response becomes:

```csharp
var client = ClientReturning(HttpStatusCode.TooManyRequests, body, retryAfterSeconds: 42);

var ex = await Assert.ThrowsExactlyAsync<DataverseThrottledException>(
    () => client.PatchAccountAsync(Guid.NewGuid(), Columns(), CancellationToken.None));

Assert.AreEqual(TimeSpan.FromSeconds(42), ((IRetryAfterHint)ex).RetryAfter);
```

### Retry rules

Ask the provider for a policy the way the pipeline does: through
`GetRetryPolicy(eventTypeId, exception)`, with the handler's exception wrapped in
`EventContextHandlerException`:

```csharp
var policies = new DefaultRetryPolicyProvider();
D365Resilience.ConfigureRetries(policies, new D365ResilienceOptions());

var rejected = policies.GetRetryPolicy(
    "BcSalesQuoteCreated",
    new EventContextHandlerException(
        new DataverseRequestRejectedException("PATCH … → 400. Body: limits 429/503 apply", 400, "0x80040203")));

Assert.IsNull(rejected);
```

Include a rejection whose message mentions the statuses you retry. It proves the
rules match on type, not text.

### Circuit breaker

Drive an `EndpointCircuitBreaker` directly, with a manual `TimeProvider` so the
test controls the sampling window:

```csharp
var options = new CircuitBreakerOptions();
D365Resilience.ConfigureCircuitBreaker(options, new D365ResilienceOptions());

var breaker = new EndpointCircuitBreaker("D365SalesEndpoint", options, clock);
for (var i = 0; i < 10; i++)
    breaker.RecordFailure(new EventContextHandlerException(new DataverseUnavailableException("503")));

Assert.AreEqual(CircuitState.Open, breaker.State);
```

Record throttled and rejected failures on a second breaker and assert it stays
`Closed`.

### Wiring through the in-memory transport

`AddNimBusTestTransport` from `NimBus.Testing` composes the real subscriber
pipeline over an in-memory bus, including retry rules, the inbox and the
circuit breaker:

```csharp
var services = new ServiceCollection();
services.AddSingleton<IDataverseClient>(dataverse);
services.AddNimBusTestTransport(
    sub => sub.AddHandlersFromAssemblyContaining<BcSalesQuoteCreatedHandler>(),
    endpoint: "D365SalesEndpoint");

using var provider = services.BuildServiceProvider();
await provider.GetRequiredService<IPublisherClient>().Publish(quote);
await provider.GetRequiredService<InMemoryMessageBus>()
    .DeliverAll(provider.GetRequiredService<IMessageHandler>());
```

Assert on the fake client's side effects rather than on message disposition
flags.

### The catalog

Check the platform catalog in the same test project:

```csharp
var errors = PlatformValidation.ValidateCommandConsumers(platform);
Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
```

`ValidateCommandConsumers` reports every `Command` type without exactly one
consuming endpoint. Call `TryValidate()` on each event's example payload to keep
the examples shown in the WebApp valid.

## Topology provisioning

Provision Service Bus topology once per environment before messages flow:

- Use the CLI: `nb topology apply` (see [cli.md](cli.md)).
- Use a provisioner program, such as
  [`samples/AspirePubSub/AspirePubSub.Provisioner/Program.cs`](../samples/AspirePubSub/AspirePubSub.Provisioner/Program.cs).

Topology comes from the `IPlatform` definition: endpoint names, subscriptions,
session settings, retry counts, and routing rules.

## Run a local stack from packages

An adapter repository can run the Service Bus emulator, topology provisioning,
the Resolver and the WebApp from NuGet packages, without a NimBus source
checkout. Add `Akaule.NimBus.AspireHosting` and `Aspire.Hosting.SqlServer` to the
AppHost. Reference the contracts project that holds your `IPlatform` without
making it an Aspire resource:

```xml
<ProjectReference Include="..\Acme.Contracts\Acme.Contracts.csproj" IsAspireProjectResource="false" />
```

The AppHost:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var servicebus = builder.AddNimBusServiceBusEmulator("servicebus").ConnectionString;
var nimbusDb = builder.AddSqlServer("sql").AddDatabase("nimbus");

var platformAssembly = typeof(AcmePlatform).Assembly.Location;
var platformType = typeof(AcmePlatform).FullName!;

var topology = builder.AddNimBusTopology("topology", servicebus, platformAssembly, platformType);

var webApp = builder.AddNimBusWebApp("nimbus-ops", platformAssembly, platformType)
    .WithNimBusServiceBus(servicebus)
    .WithNimBusSqlServerStore(nimbusDb)
    .WaitForCompletion(topology);

builder.AddNimBusResolver()
    .WithNimBusServiceBus(servicebus)
    .WithNimBusSqlServerStore(nimbusDb)
    .WithNimBusWebAppNotifications(webApp)
    .WaitForCompletion(topology);

builder.AddProject<Projects.Acme_Adapter>("acme-adapter")
    .WithReference(servicebus)
    .WithReference(nimbusDb) // the adapter's inbox table
    .WaitForCompletion(topology);

builder.Build().Run();
```

`AddNimBusTopology` runs `nb topology apply` once and exits; everything else
waits for it. `WithNimBusWebAppNotifications` lets the WebApp's live pages
update on SQL Server, which has no change feed. Use `WithNimBusCosmosStore` for
a Cosmos DB store.

Each resource is a dotnet tool, run with `dotnet tool exec` in the AppHost
directory, so the AppHost's `nuget.config` decides where packages come from.
Nothing is installed globally.

| Package | Command | Runs |
| --- | --- | --- |
| `Akaule.NimBus.ServiceBusEmulator` | `nimbus-sb-emulator` | The Service Bus emulator |
| `Akaule.NimBus.CommandLine` | `nb` | Topology provisioning |
| `Akaule.NimBus.Resolver.Host` | `nimbus-resolver` | The Resolver, as a plain worker |
| `Akaule.NimBus.WebApp` | `nimbus-webapp` | The WebApp |

The tool version defaults to the version of the hosting package, so the tools
match the NimBus packages the AppHost references. Set `NIMBUS_TOOL_VERSION` in
the AppHost's configuration, or pass `version:` to an `Add...` method, to
override it.

Open the WebApp on its `https` endpoint. Its UI calls the API over HTTPS with
the ASP.NET Core development certificate, which `aspire run` trusts. Pass
`httpsPort:` or `httpPort:` to `AddNimBusWebApp` for fixed ports. The WebApp
runs in Development with the local sign-in bypass
(`EnableLocalDevAuthentication`), so use it for local work only.

Azure deployment does not change: `nb deploy apps` still deploys the
Functions-hosted Resolver and the WebApp from `Akaule.NimBus.Deploy`. See
[deployment.md](deployment.md).

## Production checklist

- Set `MaxConcurrentSessions` deliberately. The Worker default is `8`; raise it
  for high-throughput endpoints (16–32 is the usual first move) or lower it to
  `1` if you need to serialize the endpoint. See
  [Throughput Tuning](throughput-tuning.md).
- Keep handlers idempotent. NimBus gives ordered, at-least-once processing, not
  exactly-once side effects.
- Use a stable `MessageId` when the source event has a natural unique key.
- Prefer the outbox when publishing is coupled to a local database transaction.
- Run the deferred-message replay path for every subscriber endpoint.
- Classify poison payloads as permanent failures so they do not burn retry
  budget.
- Map external-system failures to typed exceptions in the client, and retry
  them with `AddExceptionRule<T>` rules, not text rules.
- Keep hidden HTTP retries off the external-system client: no standard
  resilience handler, a short timeout, and vendor SDK retries turned down. Every
  attempt must fit inside the message lock.
- With a circuit breaker, host the adapter as a Worker and set
  `PrefetchCount = 0`.
- Export NimBus OpenTelemetry meters and sources from every host.
- Provision topology before deployment and keep endpoint names consistent across
  code, config, and platform definitions.

## Common pitfalls

- **Messages stay pending**: the handler is not registered, the receiver/trigger
  is missing, or the trigger's topic/subscription settings point to the wrong
  entity.
- **A Worker processes one message at a time**: `MaxConcurrentSessions` was set
  to `1` (or the endpoint only ever has a single session).
- **Functions app starts but nothing is consumed**: `AddNimBusSubscriber(...)`
  exists, but there is no `[ServiceBusTrigger]` function, or
  `IsSessionsEnabled = true` is missing on the main subscription trigger.
- **Outbox rows are written but never published**: `OutboxDispatcherSender` or
  `AddNimBusOutboxDispatcher(...)` is missing.
- **Outbox dispatcher fails on first startup**: the outbox table has not been
  created with `EnsureTableExistsAsync()`.
- **Every message is dead-lettered by validation**: required event metadata such
  as `EventId` or `EventTypeId` is missing. Fix the publisher side.

## Where to go next

- [getting-started.md](getting-started.md) - first publisher/subscriber path.
- [sdk-api-reference.md](sdk-api-reference.md) - publisher, subscriber, retry,
  outbox, and request/response APIs.
- [orchestration.md](orchestration.md) - application-owned process managers,
  durable state, timeouts, and compensation.
- [azure-functions-hosting.md](azure-functions-hosting.md) - isolated worker
  setup, `host.json`, triggers, and deferred processor function.
- [pipeline-middleware.md](pipeline-middleware.md) - custom middleware and
  lifecycle observers.
- [error-handling.md](error-handling.md) - retry, dead-letter, and failure
  classification behavior.
- [deferred-messages.md](deferred-messages.md) - session blocking and deferred
  replay.
- [circuit-breaker.md](circuit-breaker.md) - breaker options, what counts, and
  the Functions limitation.
- [inbox-pattern.md](inbox-pattern.md) - consumer inbox registration and
  guarantees.
- [testing.md](testing.md) - NimBus's own test suites and instrumentation tests.
