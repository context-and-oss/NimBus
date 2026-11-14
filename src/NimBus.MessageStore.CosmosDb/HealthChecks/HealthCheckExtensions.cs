using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.MessageStore.HealthChecks;

public static class HealthCheckExtensions
{
    private static readonly string[] ReadyTags = ["ready"];

    public static IHealthChecksBuilder AddCosmosDbHealthCheck(this IHealthChecksBuilder builder)
    {
        // The singleton registration is load-bearing: without it AddCheck<T>
        // activates a fresh instance per probe and the 30s result cache never hits.
        builder.Services.AddSingleton<CosmosDbHealthCheck>();
        return builder.AddCheck<CosmosDbHealthCheck>(
            "cosmosdb",
            failureStatus: HealthStatus.Unhealthy,
            tags: ReadyTags);
    }
}
