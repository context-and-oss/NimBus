# Getting Started with NimBus

This guide walks you through creating your first publisher and subscriber, running them with Aspire, and observing the message flow in the management WebApp.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Node.js 22+ for the management frontend
- A running Docker-compatible container runtime for SQL Server and local storage resources
- Optional: an Azure Service Bus namespace (Standard or Premium) instead of the local emulator; Cosmos DB instead of SQL Server

For a ready-to-run integration, start with the [CRM/ERP sample](../samples/CrmErpDemo/README.md#running-locally--sql-server-default). The [Dynamics 365 Sales ↔ Business Central demo](../samples/DynamicsBcDemo/README.md) shows a CRM and an ERP that each own part of the customer lifecycle.

## 1. Define an Event

Events are simple C# classes that extend `Event`. Use `[Description]` and `[Required]` attributes for documentation and schema generation.

```csharp
using NimBus.Core.Events;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

[Description("Published when a customer places a new order.")]
public class OrderPlaced : Event
{
    [Required]
    [Description("The unique order identifier.")]
    public Guid OrderId { get; set; }

    [Required]
    [Description("The customer that placed the order.")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("The ISO currency code for the order total.")]
    public string CurrencyCode { get; set; }

    [Range(0.01, 1000000)]
    [Description("The total amount of the order.")]
    public decimal TotalAmount { get; set; }

    public override string GetSessionId() => OrderId.ToString();
}
```

**Tip:** You can use the `[SessionKey]` attribute instead of overriding `GetSessionId()`:

```csharp
[SessionKey(nameof(OrderId))]
public class OrderPlaced : Event
{
    public Guid OrderId { get; set; }
    // ... no GetSessionId() override needed
}
```

`GetSessionId()` (or `[SessionKey]`) provides the default session ID for message ordering — all messages with the same session ID are processed in FIFO order. The publisher can also override the session ID explicitly at publish time for advanced ordering scenarios (see [SDK API Reference](sdk-api-reference.md#sessionkey-attribute)).

## 2. Create a Publisher

A publisher is a web app or worker service that sends events to Azure Service Bus.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// Register the publisher for the StorefrontEndpoint topic
builder.Services.AddNimBusPublisher("StorefrontEndpoint");

var app = builder.Build();

app.MapPost("/publish/order", async (IPublisherClient publisher) =>
{
    var order = new OrderPlaced
    {
        OrderId = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        CurrencyCode = "EUR",
        TotalAmount = 42.50m,
    };

    await publisher.Publish(order);

    return Results.Ok(new { order.OrderId, Status = "Published" });
});

app.Run();
```

`AddNimBusPublisher("StorefrontEndpoint")` registers `IPublisherClient` in DI. The endpoint name determines which Service Bus topic receives the messages.

## 3. Create a Subscriber

A subscriber is a worker service that handles events.

### Define a handler

```csharp
using NimBus.SDK.EventHandlers;

public class OrderPlacedHandler : IEventHandler<OrderPlaced>
{
    private readonly ILogger<OrderPlacedHandler> _logger;

    public OrderPlacedHandler(ILogger<OrderPlacedHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(OrderPlaced message, IEventHandlerContext context, CancellationToken ct)
    {
        _logger.LogInformation("Order {OrderId} received, amount {Amount} {Currency}",
            message.OrderId, message.TotalAmount, message.CurrencyCode);
        return Task.CompletedTask;
    }
}
```

Handlers use standard DI — inject any service via the constructor. No NimBus-specific logger needed.

### Wire up the subscriber

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

// Register middleware pipeline
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();
    nimbus.AddPipelineBehavior<ValidationMiddleware>();
});

// Register the subscriber and handler
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<OrderPlacedHandler>();
});

// Start listening on the Service Bus subscription
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "BillingEndpoint";
    opts.SubscriptionName = "BillingEndpoint";
});

var host = builder.Build();
host.Run();
```

## 4. Add Middleware (Optional)

NimBus includes two built-in middleware behaviors. Register them via `AddNimBus()` before `AddNimBusSubscriber()`:

```csharp
builder.Services.AddNimBus(nimbus =>
{
    nimbus.AddPipelineBehavior<LoggingMiddleware>();     // Logs timing + metadata
    nimbus.AddPipelineBehavior<ValidationMiddleware>();  // Dead-letters invalid messages
});
```

Consumer-side OpenTelemetry counters and the `NimBus.Process` span are emitted by the transport adapter automatically — no behavior to register. See [Pipeline Middleware](pipeline-middleware.md) for the contract and for writing custom middleware.

## 5. Configure Retry Policies (Optional)

Add automatic retry with configurable backoff:

```csharp
builder.Services.AddNimBusSubscriber("BillingEndpoint", sub =>
{
    sub.AddHandler<OrderPlaced, OrderPlacedHandler>();
    sub.ConfigureRetryPolicies(policies =>
    {
        policies.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromMinutes(5)
        });
    });
});
```

## 6. Provision Service Bus Topology

Before running, create the topics, subscriptions, and routing rules:

```bash
# Using the CLI
nb topology apply --solution-id nimbus --environment dev --resource-group rg-nimbus-dev

# Or export the config
nb topology export -o platform-config.json
```

With Aspire, the provisioner runs automatically as part of the AppHost.

## 7. Run with Aspire

### Start locally with the emulator and SQL Server

From the repository root, enable the emulator (the main AppHost requires this opt-in; the CRM/ERP sample enables it by default):

```bash
dotnet run --project src/NimBus.AppHost -- --UseEmulator true
```

SQL Server runs in a local container by default. To use Azure Service Bus instead, omit `--UseEmulator true` and set `ConnectionStrings:servicebus` with `dotnet user-secrets --project src/NimBus.AppHost set`. To select Cosmos DB, configure `ConnectionStrings:cosmos` and pass `--NIMBUS_STORAGE_PROVIDER cosmos`. Environment variables of the same names take precedence over arguments.

This launches:
- **Aspire Dashboard** — Logs, traces, metrics at `https://localhost:17xxx`
- **provisioner** — Creates Service Bus topology (runs once)
- **resolver** — Tracks message state in the selected store (SQL Server by default)
- **webapp** — Management UI
- **publisher** — Sample HTTP API
- **subscriber** — Sample event handler with middleware

### Publish a test message

Find the publisher URL in the Aspire Dashboard, then:

```bash
curl -X POST https://localhost:<port>/publish/order
```

## 8. View in the WebApp

Open the WebApp URL from the Aspire Dashboard:

1. **Endpoints** page — Shows StorefrontEndpoint, BillingEndpoint, WarehouseEndpoint with message counts
2. Click an endpoint → **Endpoint Details** with failed/deferred/pending events
3. Click an event → **Event Details** with:
   - **Message** tab — Identifiers, status, action buttons (Resubmit, Skip, Delete)
   - **Flow** tab — Visual timeline of the complete message lifecycle
   - **Blocked** tab — Events blocked by this session

> **Response compression.** The WebApp runs ASP.NET Core's in-process response-compression
> middleware (Brotli first, Gzip fallback, both at `CompressionLevel.Optimal`) so that the
> SPA bundle, CSS, JSON API payloads, and SVG icons are negotiated down to ~20-30 % of
> their raw size for browsers that advertise `Accept-Encoding`. Under Aspire there is no
> reverse proxy in front of the WebApp — this middleware closes the gap. If you deploy
> behind App Service, Application Gateway, or any other layer that already compresses, do
> nothing: the middleware honours an existing `Content-Encoding` header on its way out and
> does not double-encode, so the outer compression takes precedence transparently.

## 9. Test Error Handling

Publish a message that will fail:

```bash
curl -X POST https://localhost:<port>/publish/order-failed
```

This sends an `OrderPlaced` with `SimulateFailure = true`. The subscriber throws an exception, which:
1. Sends an `ErrorResponse` to the Resolver (status → Failed)
2. Blocks the session (subsequent messages are deferred)
3. Appears in the WebApp with the error text and stack trace

You can then **Resubmit** or **Skip** the failed message from the Event Details page.

## Next Steps

- [Onboarding a Customer Project](onboarding.md) — From prerequisites to production for a delivery team
- [Azure Functions Hosting](azure-functions-hosting.md) — Production hosting with Service Bus session triggers
- [Message Flows](message-flows.md) — All 10 message flow patterns
- [Application-Level Orchestration](orchestration.md) — Durable multi-step workflows, timeouts, and compensation
- [Deferred Messages](deferred-messages.md) — Session blocking and deferral mechanics
- [Pipeline Middleware](pipeline-middleware.md) — Custom middleware and lifecycle observers
- [CLI Reference](cli.md) — All `nb` commands
- [Architecture](architecture.md) — System design and component overview
- [SDK API Reference](sdk-api-reference.md) — Complete interface reference
