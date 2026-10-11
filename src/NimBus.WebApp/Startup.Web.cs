using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.ResponseCompression;
using NimBus.WebApp.Services.Heartbeat;
using NimBus.Core;
using NimBus.ServiceBus.Provisioning;
using NimBus.WebApp.Services;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.RateLimiting;
using System.Text.Json.Serialization;
using NimBus.Management.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace NimBus.WebApp;

public partial class Startup
{
    // Controllers/JSON, Razor, NSwag, response compression, SignalR.
    private void AddWebPipeline(IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.ModelBinderProviders.Insert(0, new EnumMemberModelBinderProvider());
        }).AddJsonOptions(opts =>
        {
            // Ordered: the EnumMember factory claims every contract enum first,
            // so those serialize as api-spec.yaml declares them. JsonStringEnumConverter
            // still covers enums carrying no EnumMember values.
            opts.JsonSerializerOptions.Converters.Add(new EnumMemberJsonConverterFactory());
            opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // Normalise every outbound DateTime to UTC with a `Z` suffix so the
            // SPA's moment.js parses it as UTC (then renders in browser-local).
            // Without this, Unspecified-Kind DateTimes from Cosmos / SQL get
            // serialised without offset and moment treats them as local time —
            // displaying UTC wall-clocks as if they were local. See
            // Services/UtcDateTimeJsonConverter.cs for the rationale.
            opts.JsonSerializerOptions.Converters.Add(new Services.UtcDateTimeJsonConverter());
            opts.JsonSerializerOptions.Converters.Add(new Services.NullableUtcDateTimeJsonConverter());
        });

        services.AddMvc();

        services.AddSwaggerDocument(s =>
        {
            s.Title = "DIS Management API";
            s.Description = "Enterprise Integration Platform API - For data displayed in the management-webapp.";
        });

        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();

        // Response compression — Brotli first, Gzip second.
        // App Service's default IIS compression doesn't cover `application/javascript`
        // reliably and never serves precompressed `.br` files emitted by the Vite build;
        // the in-process middleware fills both gaps for Aspire / reverse-proxy-less
        // deployments. When an outer layer (App Service, Application Gateway) already
        // compresses, ASP.NET Core's middleware honours the existing `Content-Encoding`
        // header and does not double-encode.
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
            {
                // SPA bundle assets — Vite emits these and they dominate first-load bytes.
                "application/javascript",
                // JSON API responses (endpoints/audits/messages lists run 100-500 KB).
                "application/json",
                // SPA stylesheet bundle (Tailwind compresses ~80 % under Brotli).
                "text/css",
                // Inline icon assets shipped from ClientApp/public.
                "image/svg+xml",
            });
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);

        services.AddSignalR();

        // Request-rate policies for the four highest-cost surfaces (agent
        // receive, admin bulk operations, search, login). Attached by an
        // application-model convention rather than attributes, for the same
        // reason AllowAnonymousActionsConvention exists: the controllers
        // carrying those routes are NSwag-generated and regenerated on every
        // build. See docs/rate-limiting.md.
        services.AddNimBusRateLimiting(Configuration);
    }

    private void AddApiControllers(IServiceCollection services)
    {
        services.AddTransient<IEndpointApiController, EndpointImplementation>();
        services.AddTransient<IStorageHookApiController, StorageHookImplementation>();
        services.AddTransient<IEventApiController, EventImplementation>();
        services.AddTransient<IEventTypeApiController, EventTypeImplementation>();
        services.AddTransient<IApplicationApiController, ApplicationImplementation>();
        services.AddTransient<IMessageApiController, MessageImplementation>();
        services.AddTransient<IFailedApiController, FailedImplementation>();
        services.AddScoped<IAdminService, AdminService>();
        // The emulator caps entity TTLs far below a real namespace, so a rebuild has to
        // ask the descriptor for emulator-safe values when that is what we're pointed at.
        var isEmulator = ServiceBusTopologyProvisioner.IsEmulator(
            Configuration.GetConnectionString("servicebus")
            ?? Configuration.GetValue<string>("AzureWebJobsServiceBus"));
        services.AddScoped<ISubscriptionAdminService>(sp => new SubscriptionAdminService(
            sp.GetRequiredService<IPlatform>(),
            sp.GetRequiredService<IServiceBusManagement>(),
            sp.GetRequiredService<ITopologyRebuilder>(),
            sp.GetRequiredService<ServiceBusClient>(),
            sp.GetRequiredService<ILogger<SubscriptionAdminService>>(),
            isEmulator,
            sp.GetRequiredService<IResolverDeadLetterClient>()));
        services.AddTransient<IAdminApiController, AdminImplementation>();
        services.AddTransient<IMetricsApiController, MetricsImplementation>();
        services.AddTransient<IHeartbeatApiController, HeartbeatImplementation>();
        services.AddTransient<IMonitorApiController, MonitorImplementation>();
        services.AddScoped<IMonitorAcknowledgementService, MonitorAcknowledgementService>();
        services.AddTransient<IAuditApiController, AuditImplementation>();
        services.AddTransient<IAccessControlApiController, AccessControlImplementation>();
        services.AddTransient<IAgentApiController, AgentImplementation>();
        services.AddTransient<ISimulationApiController, SimulationImplementation>();
        // Spec 037: Settings → MCP access. Registered whatever the deployment's MCP mode, so the
        // tab can explain how to set the endpoint up and a policy can be saved in advance.
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<NimBus.WebApp.Mcp.Access.McpAccessSettingsService>();
        services.AddScoped<NimBus.WebApp.Mcp.Access.McpActivityService>();
        services.AddTransient<IMcpAccessApiController, McpAccessImplementation>();
        // Spec 039: Settings → Agent keys. The key scheme itself is registered with the sign-in
        // stack (Startup.Security).
        services.AddScoped<NimBus.WebApp.Services.AgentApiKeys.AgentApiKeyService>();
        services.AddTransient<IAgentKeysApiController, AgentKeysImplementation>();
        services.AddSingleton<IAgentEventPublisher, AgentEventPublisher>();
        services.AddSingleton<IAgentSubscriptionRegistry, AgentSubscriptionRegistry>();

        // Platform heartbeat. Scoped for its narrow storage dependencies; the
        // sender is a singleton because it wraps the singleton ServiceBusClient.
        // The scheduler resolves the service through a scope on each tick.
        services.AddSingleton<IHeartbeatMessageSender, ServiceBusHeartbeatMessageSender>();
        services.AddScoped<IHeartbeatService, HeartbeatService>();
        services.AddHostedService<HeartbeatBackgroundService>();
    }
}
