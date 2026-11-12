# NimBus Extensions

NimBus separates **core messaging** from **optional features** through an extension framework. Core provides the fundamental publish/subscribe platform; extensions add capabilities like notifications, ticket integration, or custom monitoring as separate NuGet packages.

This guide covers how to use existing extensions and how to create new ones.

## Core vs extension

When adding a feature to NimBus, ask whether it belongs in core or in an extension:

| Core | Extension |
|------|-----------|
| Required for message publish/subscribe to work | Adds value but the platform works without it |
| Defines contracts other projects depend on | Consumes contracts defined in core |
| Changes rarely once stable | May evolve independently |
| Used by every deployment | Used by some deployments |

**Core projects:** `NimBus.Abstractions`, `NimBus.Core`, `NimBus.ServiceBus`, `NimBus.SDK`, `NimBus` (platform config).

**Optional platform services** (not core but ship with the repo): `NimBus.MessageStore`, `NimBus.Resolver`, `NimBus.Manager`, `NimBus.WebApp`.

**Extensions** (separate NuGet packages): `NimBus.Extensions.Notifications`, `Akaule.NimBus.Extensions.IntegrationIntelligence`, and any future `NimBus.Extensions.*` packages.

### Integration Intelligence

`Akaule.NimBus.Extensions.IntegrationIntelligence` adds the optional WebApp failure-classification API and provider boundary. It is registered with `services.AddNimBusIntegrationIntelligence(configuration)` and stays inactive unless the nested `NimBus:IntegrationIntelligence` configuration is enabled and valid. See [Integration Intelligence](integration-intelligence.md) for configuration, authorization, redaction, storage, and deployment details.

## Using extensions

### The builder API

Extensions are registered through `AddNimBus()`, which provides an `INimBusBuilder` for composing features:

```csharp
using NimBus.Core.Extensions;
using NimBus.Extensions.Notifications;

services.AddNimBus(builder =>
{
    // Optional platform services
    builder.AddCosmosDbMessageStore();
    builder.AddResolver();
    builder.AddManager();

    // Extension packages
    builder.AddNotifications(
        configureOptions: opts =>
        {
            opts.NotifyOnFailure = true;
            opts.NotifyOnDeadLetter = true;
        },
        configureChannels: channels =>
        {
            channels.AddSingleton<INotificationChannel, TeamsNotificationChannel>();
        });

    // Custom pipeline behaviors
    builder.AddPipelineBehavior<AuditLoggingBehavior>();

    // Custom lifecycle observers
    builder.AddLifecycleObserver<MetricsObserver>();
});
```

Without any configuration, `AddNimBus()` registers the pipeline and lifecycle infrastructure with no behaviors or observers — the platform works exactly as before.

### What the builder provides

| Method | Purpose |
|--------|---------|
| `builder.Services` | Access the underlying `IServiceCollection` for direct DI registration |
| `builder.AddPipelineBehavior<T>()` | Register a message pipeline behavior (middleware) |
| `builder.AddLifecycleObserver<T>()` | Register a message lifecycle observer |
| `builder.AddExtension<T>()` | Register an extension by type (calls `new T()` then `Configure`) |
| `builder.AddExtension(instance)` | Register an extension instance |

## Extension hook points

Extensions can hook into NimBus through two mechanisms:

### 1. Pipeline behaviors

Pipeline behaviors wrap message handling like ASP.NET Core middleware. Each behavior receives the message context and a `next` delegate. It can:

- Execute logic before the handler runs
- Execute logic after the handler completes
- Short-circuit the pipeline by not calling `next`
- Catch and handle exceptions from downstream behaviors

Behaviors execute in registration order. The first registered behavior is the outermost wrapper.

```
Request → Behavior 1 → Behavior 2 → Handler → Behavior 2 → Behavior 1 → Response
```

#### Implementing a pipeline behavior

```csharp
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

public class AuditLoggingBehavior : IMessagePipelineBehavior
{
    private readonly IAuditLogger _auditLogger;

    public AuditLoggingBehavior(IAuditLogger auditLogger)
    {
        _auditLogger = auditLogger;
    }

    public async Task Handle(
        IMessageContext context,
        MessagePipelineDelegate next,
        CancellationToken cancellationToken = default)
    {
        // Before: log that we received the message
        await _auditLogger.LogReceived(context.EventId, context.EventTypeId);

        try
        {
            // Call the next behavior (or the terminal handler)
            await next(context, cancellationToken);

            // After: log successful processing
            await _auditLogger.LogCompleted(context.EventId);
        }
        catch (Exception ex)
        {
            // Log the failure, then re-throw to let the normal error handling proceed
            await _auditLogger.LogFailed(context.EventId, ex);
            throw;
        }
    }
}
```

Register it:

```csharp
services.AddNimBus(builder =>
{
    builder.AddPipelineBehavior<AuditLoggingBehavior>();
});
```

> **Tip:** Pipeline behaviors receive their dependencies via constructor injection. Register any required services in `builder.Services` before or alongside the behavior.

### 2. Lifecycle observers

Lifecycle observers receive notifications about message events without modifying the message flow. They are ideal for monitoring, metrics, alerting, and integration with external systems.

All observer methods have default no-op implementations, so you only override the events you care about.

#### Available lifecycle events

| Event | When it fires |
|-------|---------------|
| `OnMessageReceived` | A message is received and about to be processed |
| `OnMessageCompleted` | A message was processed successfully |
| `OnMessageFailed` | A message handler threw an exception |
| `OnMessageDeadLettered` | A message was sent to the dead-letter queue |
| `OnSessionBlocked` | A message arrived for a session blocked by an earlier event and was deferred; the blocking event id is passed |
| `OnDuplicateDetected` | Inbox deduplication skipped an already processed message |
| `OnCircuitStateChanged` | An endpoint circuit changed state; receives a `CircuitStateChangeContext` instead of a `MessageLifecycleContext` |

#### Implementing a lifecycle observer

```csharp
using NimBus.Core.Extensions;

public class TicketCreationObserver : IMessageLifecycleObserver
{
    private readonly ITicketService _ticketService;

    public TicketCreationObserver(ITicketService ticketService)
    {
        _ticketService = ticketService;
    }

    public async Task OnMessageFailed(
        MessageLifecycleContext context,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        await _ticketService.CreateTicket(
            title: $"Message failed: {context.EventTypeId}",
            description: $"Event {context.EventId} failed.\n\n{exception}",
            severity: "High");
    }

    public async Task OnMessageDeadLettered(
        MessageLifecycleContext context,
        string reason,
        Exception exception = null,
        CancellationToken cancellationToken = default)
    {
        await _ticketService.CreateTicket(
            title: $"Message dead-lettered: {context.EventTypeId}",
            description: $"Event {context.EventId} dead-lettered. Reason: {reason}",
            severity: "Critical");
    }
}
```

Register it:

```csharp
services.AddNimBus(builder =>
{
    builder.Services.AddSingleton<ITicketService, JiraTicketService>();
    builder.AddLifecycleObserver<TicketCreationObserver>();
});
```

#### MessageLifecycleContext

The `MessageLifecycleContext` record provides these properties:

| Property | Type | Description |
|----------|------|-------------|
| `MessageId` | `string` | Service Bus message ID |
| `EventId` | `string` | Domain event ID |
| `EventTypeId` | `string` | Event type identifier |
| `CorrelationId` | `string` | Correlation ID for tracing |
| `SessionId` | `string` | Service Bus session ID |
| `MessageType` | `MessageType` | Request/response/error type |
| `EnqueuedTimeUtc` | `DateTimeOffset` | When the message was enqueued |
| `Timestamp` | `DateTimeOffset` | When this lifecycle event occurred |

## Creating an extension package

An extension packages behaviors, observers, and services into a reusable NuGet package.

### 1. Create the project

```
NimBus.Extensions.YourFeature/
├── NimBus.Extensions.YourFeature.csproj
├── YourFeatureExtension.cs         # INimBusExtension implementation
├── YourFeatureOptions.cs           # Configuration options
└── ...                             # Feature-specific code
```

The `.csproj` should reference `NimBus.Core`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\NimBus.Core\NimBus.Core.csproj" />
    <!-- Or as a NuGet reference for external packages:
    <PackageReference Include="NimBus.Core" Version="x.y.z" />
    -->
  </ItemGroup>
</Project>
```

### 2. Implement `INimBusExtension`

The extension class registers its services, behaviors, and observers:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;

public class YourFeatureExtension : INimBusExtension
{
    private readonly Action<YourFeatureOptions> _configure;

    public YourFeatureExtension(Action<YourFeatureOptions> configure = null)
    {
        _configure = configure;
    }

    public void Configure(INimBusBuilder builder)
    {
        // Register options
        var options = new YourFeatureOptions();
        _configure?.Invoke(options);
        builder.Services.AddSingleton(options);

        // Register services
        builder.Services.AddSingleton<IYourService, YourService>();

        // Register pipeline behaviors (if any)
        builder.AddPipelineBehavior<YourPipelineBehavior>();

        // Register lifecycle observers (if any)
        builder.AddLifecycleObserver<YourLifecycleObserver>();
    }
}
```

### 3. Add a builder extension method

Provide a convenient `AddYourFeature()` method:

```csharp
public static class YourFeatureBuilderExtensions
{
    public static INimBusBuilder AddYourFeature(
        this INimBusBuilder builder,
        Action<YourFeatureOptions> configure = null)
    {
        return builder.AddExtension(new YourFeatureExtension(configure));
    }
}
```

Consumers then use:

```csharp
services.AddNimBus(builder =>
{
    builder.AddYourFeature(opts => opts.Enabled = true);
});
```

### 4. Package naming convention

Follow the naming pattern `NimBus.Extensions.{Name}`:

| Package | Description |
|---------|-------------|
| `NimBus.Extensions.Notifications` | Notification channels on failures/dead-letters |
| `NimBus.Extensions.Identity` | Username/password authentication with email verification |
| `NimBus.Extensions.IntegrationIntelligence` | Advisory failure classification for the management WebApp |

## Worked example: Notifications extension

The `NimBus.Extensions.Notifications` package ships with the repository as a reference implementation.

### What it does

- Observes message lifecycle events (failures, dead-letters, session blocks)
- Sends notifications through pluggable channels (`INotificationChannel`)
- Ships production channels — **Webhook**, **Teams**, **Email** (SendGrid/SMTP) — plus per-channel
  severity routing, rate limiting, and deduplication
- Ships with a `ConsoleNotificationChannel` for development
- Configurable via `NotificationOptions` and a fluent channel builder

> See [notifications.md](notifications.md) for the full guide to the production channels, severity
> routing, rate limiting/dedup, and trigger sources (including session blocks).

### Using it

```csharp
using NimBus.Extensions.Notifications;

services.AddNimBus(builder =>
{
    // Default: console output, notify on failures and dead-letters
    builder.AddNotifications();

    // Custom: specific channels and options
    builder.AddNotifications(
        configureOptions: opts =>
        {
            opts.NotifyOnFailure = true;
            opts.NotifyOnDeadLetter = true;
            opts.NotifyOnCompleted = false;
        },
        configureChannels: channels =>
        {
            channels.AddSingleton<INotificationChannel, EmailNotificationChannel>();
            channels.AddSingleton<INotificationChannel, TeamsNotificationChannel>();
        });
});
```

### Adding a custom notification channel

Implement `INotificationChannel`:

```csharp
public class TeamsNotificationChannel : INotificationChannel
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;

    public TeamsNotificationChannel(IConfiguration config)
    {
        _httpClient = new HttpClient();
        _webhookUrl = config["Notifications:TeamsWebhookUrl"];
    }

    public async Task SendAsync(Notification notification, CancellationToken ct = default)
    {
        var payload = new
        {
            title = notification.Title,
            text = notification.Message,
            themeColor = notification.Severity == NotificationSeverity.Critical ? "FF0000" : "FFA500"
        };

        var json = JsonSerializer.Serialize(payload);
        await _httpClient.PostAsync(_webhookUrl, new StringContent(json, Encoding.UTF8, "application/json"), ct);
    }
}
```

### Notification properties

The `Notification` class provides:

| Property | Type | Description |
|----------|------|-------------|
| `Severity` | `NotificationSeverity` | Information, Warning, Error, or Critical |
| `Title` | `string` | Short subject line |
| `Message` | `string` | Detailed message body |
| `EventId` | `string` | The event that triggered this notification |
| `EventTypeId` | `string` | Event type identifier |
| `MessageId` | `string` | Service Bus message ID |
| `CorrelationId` | `string` | Correlation ID for tracing |
| `ErrorDetails` | `string` | Exception details (if applicable) |

## Architecture

The extension framework lives in `NimBus.Core.Extensions` and integrates into the existing message handling pipeline:

```
ServiceBusAdapter
  └── MessageHandler.Handle()
        ├── Pipeline behaviors (if registered)
        │     └── Behavior 1 → Behavior 2 → ... → HandleByMessageType()
        ├── Lifecycle notifications
        │     ├── OnMessageReceived  (before handling)
        │     ├── OnMessageCompleted (after success)
        │     ├── OnMessageFailed    (on exception)
        │     └── OnMessageDeadLettered (on dead-letter)
        └── Normal error handling (unchanged)
```

The framework is fully backward compatible. `MessageHandler` accepts optional `MessagePipeline` and `MessageLifecycleNotifier` parameters; when not provided, it behaves exactly as before.

### Key types

| Type | Role |
|------|------|
| `INimBusExtension` | Contract for extension packages |
| `INimBusBuilder` | Fluent builder for composing extensions |
| `IMessagePipelineBehavior` | Middleware wrapping message handling |
| `MessagePipelineDelegate` | Delegate for the next step in the pipeline |
| `IMessageLifecycleObserver` | Passive observer for message events |
| `MessageLifecycleContext` | Event data passed to observers |
| `MessagePipeline` | Chains behaviors around the terminal handler |
| `MessageLifecycleNotifier` | Broadcasts lifecycle events to observers |
| `NimBusBuilder` | Default `INimBusBuilder` implementation |
| `PipelineBehaviorRegistry` | Ordered list of behavior types |

## Event-type authoring tip — authored `Example` payloads pay off twice

A `public static readonly T Example = new T { … }` field on an `IEvent`
type now feeds **both** the existing "Example payload" tab in the WebApp
**and** the seed for `GET /api/v1/event-types/{eventtypeid}/fake` (used by the
Compose dialog's "Generate fake data" button — see
[`docs/webapp-rest-api.md`](webapp-rest-api.md#server-side-schema-valid-fake-payloads)).
The fake-payload generator deep-clones the example, type-aware randomizes
every leaf, and falls back to the example verbatim when no randomization
satisfies `TryValidate` inside the retry budget. Authoring a good example
therefore both documents the type and guarantees the Compose dialog
produces a submit-clean payload on first try.
