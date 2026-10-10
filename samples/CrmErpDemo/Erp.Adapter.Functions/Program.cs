using Azure.Identity;
using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.E2E;
using CrmErpDemo.Contracts.Dtos;
using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Erp.Adapter.Functions.HandoffMode;
using Erp.Adapter.Functions.Handlers;
using Erp.Adapter.Functions.Pipeline;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.Extensions.Notifications;
using NimBus.SDK.Extensions;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// This demo adapter stays deliberately minimal: no NimBus retry rules, inbox or circuit breaker (a
// Functions trigger can't be paused by a breaker anyway), and its HTTP clients retry inside the
// standard resilience handler. The e2e suite is tuned to that timing. For the shape a production
// adapter should follow, see samples/DynamicsBcDemo/BusinessCentral.Adapter and
// docs/building-adapters.md#resilience.
builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();

// ServiceBusClient uses the same AzureWebJobsServiceBus connection as the triggers: a
// namespace plus managed identity in Azure, a connection string locally.
builder.Services.AddSingleton<ServiceBusClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    if (cfg["AzureWebJobsServiceBus:fullyQualifiedNamespace"] is { Length: > 0 } fullyQualifiedNamespace)
        return new ServiceBusClient(fullyQualifiedNamespace, new DefaultAzureCredential());
    var connection = cfg["AzureWebJobsServiceBus"]
        ?? throw new InvalidOperationException("AzureWebJobsServiceBus is required.");
    return new ServiceBusClient(connection);
});

static string ResolveErpApiBaseUrl(IConfiguration cfg) =>
    cfg["services:erp-api:https:0"]
        ?? cfg["services:erp-api:http:0"]
        ?? cfg["Erp:ApiBaseUrl"]
        ?? throw new InvalidOperationException("Erp API base URL is required (service discovery or Erp:ApiBaseUrl).");

builder.Services.AddHttpClient<IErpApiClient, ErpApiClient>(c =>
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration)));

builder.Services.AddHttpClient<IServiceModeClient, ServiceModeClient>(c =>
{
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration));
    c.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IHandoffModeClient, HandoffModeClient>(c =>
{
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration));
    c.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IProcessingDelayClient, ProcessingDelayClient>(c =>
{
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration));
    c.Timeout = TimeSpan.FromSeconds(2);
});

builder.Services.AddHttpClient<IHandoffJobRegistration, HandoffJobRegistration>(c =>
    c.BaseAddress = new Uri(ResolveErpApiBaseUrl(builder.Configuration)));

builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
    n.AddPipelineBehavior<ServiceModeMiddleware>();
    // Runs after the service-mode gate: don't delay messages that are being rejected,
    // but hold the rest for the configured time before their handler runs.
    n.AddPipelineBehavior<ProcessingDelayMiddleware>();
    if (E2eSettings.IsEnabled(builder.Configuration, builder.Environment)) n.AddPipelineBehavior<E2eMiddleware>();
});

// The deferred-processor BackgroundService is intentionally NOT registered here —
// ErpDeferredProcessorFunction's [ServiceBusTrigger] owns the trigger subscription
// directly. The shared dispatch body lives in NimBus.SDK.Hosting.DeferredMessageDispatcher.
builder.Services.AddNimBusSubscriber("ErpEndpoint", sub =>
{
    sub.AddHandlersFromAssemblyContaining<CrmAccountCreatedHandler>();

    // Request/reply responder — request handlers are registered explicitly
    // (not assembly-scanned); the reply travels back on CrmEndpoint-reply.
    sub.AddRequestHandler<ErpCreditCheckRequested, ErpCreditCheckResult, ErpCreditCheckRequestedHandler>();
});

// Demo notifications: when an inbound ERP message fails, dead-letters, or blocks its session,
// NimBus fires a webhook to the ERP API, which surfaces it as a live operator alert in Erp.Web.
// Reuses the same erp-api base URL the adapter already calls; AddNimBus above registered the
// MessageLifecycleNotifier this path depends on. A JSON Template keeps the payload stable (and
// exercises the channel's JSON-string escaping for quotes/newlines in error text).
builder.Services.AddNimBusNotifications(channels =>
{
    channels.AddWebhook(opts =>
    {
        opts.Url = ResolveErpApiBaseUrl(builder.Configuration).TrimEnd('/') + "/api/webhooks/notifications";
        opts.MinSeverity = NotificationSeverity.Warning; // passes Error + Critical, skips Info noise
        opts.Template =
            "{\"severity\":\"{Severity}\",\"title\":\"{Title}\",\"message\":\"{Message}\"," +
            "\"eventId\":\"{EventId}\",\"eventTypeId\":\"{EventTypeId}\",\"messageId\":\"{MessageId}\"," +
            "\"correlationId\":\"{CorrelationId}\",\"errorDetails\":\"{ErrorDetails}\"}";
    });
}, options =>
{
    options.NotifyOnFailure = true;      // Error    (service-mode reject, handler throw)
    options.NotifyOnDeadLetter = true;   // Critical (retries exhausted)
    options.NotifyOnSessionBlock = true; // Critical (default-on for this path anyway)
});

builder.Services.AddE2eExecution(builder.Configuration, builder.Environment, ResolveErpApiBaseUrl(builder.Configuration));
builder.Build().Run();
