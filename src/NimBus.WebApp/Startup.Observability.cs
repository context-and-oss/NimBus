using NimBus.WebApp.Services.ApplicationInsights;
using NimBus.OpenTelemetry;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore.SqlServer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure.Identity;
using NimBus.ServiceBus.HealthChecks;
using NimBus.MessageStore.HealthChecks;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NimBus.WebApp;

public partial class Startup
{
    private static readonly string[] ReadyHealthCheckTags = ["ready"];

    private void AddObservability(IServiceCollection services, string storageProvider)
    {
        // Typed HttpClient via IHttpClientFactory — pools the underlying
        // SocketsHttpHandler across calls, hooks into AddHttpClientInstrumentation
        // for OpenTelemetry, and leaves room for a future Polly retry policy.
        // The query API has accepted only Microsoft Entra tokens since API keys were
        // retired on 2026-03-31. DefaultAzureCredential resolves to the site's managed
        // identity when deployed and to the developer's credential locally; a leftover
        // AppInsights:ApiKey setting is ignored.
        services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddHttpClient<IApplicationInsightsService, ApplicationInsightsService>((sp, http) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var appId = cfg.GetValue<string>("AppInsights:ApplicationId");
            if (!string.IsNullOrWhiteSpace(appId))
            {
                http.BaseAddress = new Uri($"https://api.applicationinsights.io/v1/apps/{appId}/");
            }
        })
        .AddHttpMessageHandler(sp => new ApplicationInsightsAuthenticationHandler(sp.GetRequiredService<TokenCredential>()));

        // Telemetry is optional and always has been: the app runs locally and in
        // any environment that simply doesn't configure it. Application Insights
        // 3.x turned a missing connection string from "collect nothing" into a
        // throw from AddApplicationInsightsTelemetry, which takes the whole host
        // down at startup. Gate on the same variable ServiceDefaults uses for
        // Azure Monitor, and that deploy.core.bicep sets, so a configured
        // environment is unaffected and an unconfigured one still boots.
        if (!string.IsNullOrWhiteSpace(Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"])
            || !string.IsNullOrWhiteSpace(Configuration["ApplicationInsights:ConnectionString"]))
        {
            services.AddApplicationInsightsTelemetry();
        }

        services.AddNimBusInstrumentation();
        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddNimBusInstrumentation()
                    .AddMeter(NimBusIntelligenceTelemetry.Name);
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource("Azure.Cosmos.Operation")
                    .AddSource("Azure.Messaging.ServiceBus")
                    .AddSource(NimBusIntelligenceTelemetry.Name)
                    .AddNimBusInstrumentation();
            });

        var useOtlpExporter = !string.IsNullOrWhiteSpace(Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        if (useOtlpExporter)
        {
            services.AddOpenTelemetry().UseOtlpExporter();
        }

        var healthChecks = services.AddHealthChecks()
            .AddServiceBusHealthCheck();
        if (string.Equals(storageProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            healthChecks.AddCheck<SqlServerMessageStoreHealthCheck>(
                "sqlserver-messagestore",
                failureStatus: HealthStatus.Unhealthy,
                tags: ReadyHealthCheckTags);
        }
        else
        {
            healthChecks.AddCosmosDbHealthCheck();
        }
    }
}
