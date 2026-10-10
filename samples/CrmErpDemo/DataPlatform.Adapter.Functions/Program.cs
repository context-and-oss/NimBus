using Azure.Identity;
using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.Events;
using DataPlatform.Adapter.Functions.Handlers;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NimBus.Core.CloudEvents;
using NimBus.SDK.Extensions;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
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

// Register EnrichedContactHandler for DI resolution. The SP-factory below resolves it
// ONCE at ISubscriberClient construction and captures it for the lifetime of the host, so
// Singleton is the honest lifetime (its only dependency, ILogger<T>, is itself a singleton).
builder.Services.AddSingleton<EnrichedContactHandler>();

// AutoDetect is required now that Erp.Api publishes CloudEvents-binary: the body is the
// raw domain JSON (not the native MessageContent envelope), so a native-only subscriber
// would mis-parse ErpCustomerCreated. Native messages are still handled untouched.
builder.Services.AddNimBusSubscriber(
    configure: options =>
    {
        options.Endpoint = "DataPlatformEndpoint";
        options.UseCloudEvents(ce => ce.Mode = CompatibilityMode.AutoDetect);
    },
    configureBuilder: sub =>
    {
        sub.AddHandler<ErpCustomerCreated, ErpCustomerCreatedHandler>();

        // Spec 022 Phase 3 Task D — consume the AI-agent enriched-contact event on this endpoint.
        // The event has no compiled IEvent class; it is identified only by its EventTypeId string.
        // Use the DI-aware overload so EnrichedContactHandler receives its ILogger from the container.
        sub.AddDynamicHandler("crm.contact.enriched.v1", sp => sp.GetRequiredService<EnrichedContactHandler>());
    });

builder.Build().Run();
