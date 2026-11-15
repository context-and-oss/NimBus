using CrmErpDemo.Contracts.Endpoints;
using CrmErpDemo.Contracts.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Pipeline;
using NimBus.SDK.Extensions;
using RegionalHub.Handlers;

// Regional hub adapter — the routing-keys showcase (Spec 045, docs/routing-keys.md).
//
// One adapter, several instances: the AppHost starts this project once per hub and tells each
// instance which endpoint it is (RegionalHub__Endpoint). CRM account events carry their country
// as the routing key, and each hub's forward rule on the CrmEndpoint topic lets through only the
// countries that hub claims, so the Nordic hub never sees a German account.
//
//   NordicHubEndpoint    DK, SE, NO, FI
//   DachHubEndpoint      DE, AT, CH
//   UnroutedHubEndpoint  every other country, and accounts with none
//
// ErpEndpoint declares no keys and keeps receiving every account.

var builder = Host.CreateApplicationBuilder(args);

var endpointId = builder.Configuration["RegionalHub:Endpoint"];
var hub = RegionalHubs.Find(endpointId)
    ?? throw new InvalidOperationException(
        $"RegionalHub:Endpoint must name the hub this instance serves, one of: " +
        $"{string.Join(", ", RegionalHubs.All.Select(h => h.EndpointId))}. Got '{endpointId}'.");

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);
builder.AddAzureServiceBusClient("servicebus");

builder.Services.AddSingleton(hub);
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
});

builder.Services.AddNimBusSubscriber(hub.EndpointId, sub =>
{
    if (hub.IsUnrouted)
    {
        // The built-in handler of an unmatched consumer: every message it receives has a key
        // no hub claims, or none, and is dead-lettered with a reason naming the key. That turns
        // "an account in a country without a hub" into something an operator sees in nimbus-ops
        // instead of a message Service Bus dropped.
        sub.AddUnmatchedHandler<CrmAccountCreated>();
        sub.AddUnmatchedHandler<CrmAccountUpdated>();
        return;
    }

    sub.AddHandler<CrmAccountCreated, AccountCreatedHandler>();
    sub.AddHandler<CrmAccountUpdated, AccountUpdatedHandler>();

    // The filter on the bus is one layer. This is the second: before a handler runs, the
    // country in the payload must be one this hub claims, and must equal the RoutingKey
    // property. A message that fails is dead-lettered and doesn't block its session.
    sub.EnforceRoutingKeys([.. hub.Countries]);
});
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = hub.EndpointId;
    opts.SubscriptionName = hub.EndpointId;
});
builder.Services.AddNimBusDeferredProcessorHostedService(hub.EndpointId);

builder.Build().Run();
