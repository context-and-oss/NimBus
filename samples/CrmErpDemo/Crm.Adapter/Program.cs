using Crm.Adapter.Clients;
using CrmErpDemo.Contracts.E2E;
using CrmErpDemo.Contracts.Security;
using Crm.Adapter.Handlers;
using Crm.Adapter.Observability;
using Microsoft.Extensions.Configuration;
using NimBus.Core.CloudEvents;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Pipeline;
using NimBus.Inbox.SqlServer;
using NimBus.SDK.Extensions;

var builder = Host.CreateApplicationBuilder(args);

// This demo adapter stays deliberately minimal: its CRM client retries inside the standard HTTP
// resilience handler, failures surface as plain HttpRequestException, and it has a circuit breaker
// but no NimBus retry rules. The e2e suite is tuned to that timing. For the shape a production
// adapter should follow (typed failures, AddExceptionRule<T>, Retry-After, no hidden HTTP retries),
// see samples/DynamicsBcDemo/BusinessCentral.Adapter and docs/building-adapters.md#resilience.
builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);
builder.AddAzureServiceBusClient("servicebus");

var crmApiBaseUrl = builder.Configuration["services:crm-api:https:0"]
    ?? builder.Configuration["services:crm-api:http:0"]
    ?? builder.Configuration["Crm:ApiBaseUrl"]
    ?? throw new InvalidOperationException("Crm API base URL is required (service discovery or Crm:ApiBaseUrl).");

// In Azure crm-api requires sign-in; the adapter presents the demo service key (Demo__ServiceKey).
builder.Services.AddDemoServiceKey(builder.Configuration, crmApiBaseUrl);

// Circuit-breaker showcase: the outage flag is read fail-open on a dedicated
// short-timeout client (never surfaces TaskCanceledException into the handler
// path), and the simulation handler injects synthetic 503s BELOW the handlers
// so failures take the authentic CrmApiClient path — the circuit recorder is
// the innermost pipeline wrapper and only counts failures thrown inside
// handler execution.
builder.Services.AddHttpClient<IErrorModeClient, ErrorModeClient>(c =>
{
    c.BaseAddress = new Uri(crmApiBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(2);
});
builder.Services.AddTransient<CrmOutageSimulationHandler>();
builder.Services.AddHttpClient<ICrmApiClient, CrmApiClient>(c => c.BaseAddress = new Uri(crmApiBaseUrl))
    .AddHttpMessageHandler<CrmOutageSimulationHandler>();

// Pushes circuit transitions to crm-api so the SPA can show a live indicator
// (the adapter is a worker with no HTTP listener — state flows adapter → api → web).
builder.Services.AddHttpClient<CircuitStateReporterClient>(c =>
{
    c.BaseAddress = new Uri(crmApiBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(2);
});

// Inbox deduplication store (showcase): the crm business database doubles as the
// dedup store — [nimbus].[InboxMessages] mirrors the erp database's outbox table.
// The crm DB is always provisioned by the AppHost regardless of storage provider,
// so the showcase works in both sqlserver and cosmos platform-storage modes.
var crmDbConnectionString = builder.Configuration.GetConnectionString("crm")
    ?? throw new InvalidOperationException("ConnectionStrings:crm is required for the inbox deduplication store.");
builder.Services.AddNimBusSqlServerInbox(crmDbConnectionString);

// Middleware pipeline. Crm.Adapter is a pure subscriber adapter: CRM-originated
// events are published directly by Crm.Api, while the ERP side is the sample
// that demonstrates the transactional outbox pattern.
builder.Services.AddNimBus(n =>
{
    n.AddPipelineBehavior<LoggingMiddleware>();
    n.AddPipelineBehavior<ValidationMiddleware>();
    if (E2eSettings.IsEnabled(builder.Configuration, builder.Environment)) n.AddPipelineBehavior<E2eMiddleware>();
    // Circuit-breaker showcase: report every transition to crm-api so the SPA
    // shows Closed / Open / HalfOpen live. Also an SDK extensibility example —
    // OnCircuitStateChanged is a default-implemented lifecycle hook.
    n.AddLifecycleObserver<CircuitStateReporter>();
});

// Subscriber + receiver for CrmEndpoint. Also publishes acks/errors back through CrmEndpoint sender.
// AutoDetect handles both native NimBus messages and CloudEvents 1.0 messages on the same
// subscription: ERP events now arrive CloudEvents-binary (Erp.Api publishes with
// UseCloudEvents), and the external PartnerPortal submits raw CloudEvents leads.
builder.Services.AddNimBusSubscriber(
    configure: options =>
    {
        options.Endpoint = "CrmEndpoint";
        options.UseCloudEvents(ce => ce.Mode = CompatibilityMode.AutoDetect);
    },
    configureBuilder: sub =>
    {
        sub.AddHandlersFromAssemblyContaining<ErpCustomerCreatedHandler>();

        // Inbox showcase: platform-level dedup on (CrmEndpoint, MessageId).
        // Redelivery of the same broker MessageId is skipped as DuplicateDetected.
        // Operator resubmit allocates a new MessageId and invokes the handler;
        // both adapters rely on idempotent upserts for that replay.
        sub.UseInbox(options =>
        {
            options.DeduplicationStore = InboxStore.SqlServer;
            options.RetentionPeriod = TimeSpan.FromDays(2);
            options.CleanupInterval = TimeSpan.FromMinutes(15);
        });

        // Circuit-breaker showcase (docs/circuit-breaker.md): demo-tuned
        // thresholds so the circuit trips within ~15-30s of flipping the CRM
        // outage toggle at simulator traffic (~1 event/s) and the full
        // open -> half-open probe -> closed loop completes inside a minute.
        // Overridable via the Crm:CircuitBreaker section
        // (Crm__CircuitBreaker__* environment variables).
        var circuitSection = builder.Configuration.GetSection("Crm:CircuitBreaker");
        sub.WithCircuitBreaker(circuit =>
        {
            circuit.MinimumThroughput = circuitSection.GetValue("MinimumThroughput", 5);
            circuit.FailurePercentageThreshold = circuitSection.GetValue("FailurePercentageThreshold", 50d);
            circuit.SamplingWindow = TimeSpan.FromSeconds(circuitSection.GetValue("SamplingWindowSeconds", 60));
            circuit.BreakDuration = TimeSpan.FromSeconds(circuitSection.GetValue("BreakDurationSeconds", 20));
            circuit.HalfOpenProbeCount = circuitSection.GetValue("HalfOpenProbeCount", 2);
        });
    });
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    // SDK default is 8 — well below the Erp.Adapter.Functions side, which
    // allows 200 concurrent sessions (host.json). 32 is a demo-friendly middle
    // ground: more parallelism than the default, with enough headroom that the
    // shared SQL Server isn't fighting itself. Per-session ordering still holds.
    opts.MaxConcurrentSessions = 32;
    if (E2eSettings.IsEnabled(builder.Configuration, builder.Environment))
        opts.MaxAutoLockRenewalDuration = TimeSpan.Zero;
});

// Second receiver: partner ingress. The external PartnerPortal publishes raw
// CloudEvents (no NimBus user.* properties) to the plain PartnerInbound topic;
// its catch-all session subscription is drained here and AutoDetect routes the
// CloudEvent to PartnerLeadSubmittedHandler via its `type` attribute.
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "PartnerInbound";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 8;
});

// Deferred-replay BackgroundService. Worker hosts that own the deferred-processor
// trigger themselves opt in here; Functions hosts skip this and add their own
// [ServiceBusTrigger] function class instead.
builder.Services.AddNimBusDeferredProcessorHostedService("CrmEndpoint");

builder.Services.AddE2eExecution(builder.Configuration, builder.Environment, crmApiBaseUrl);
builder.Build().Run();
