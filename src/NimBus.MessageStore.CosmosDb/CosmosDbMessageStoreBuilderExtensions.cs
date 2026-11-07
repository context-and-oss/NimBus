using System;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;
using NimBus.OpenTelemetry;

namespace NimBus.MessageStore;

/// <summary>
/// Provider-aware registration for the Cosmos DB-backed message store. This is the
/// single entry point consumers should call when running NimBus with Cosmos. Registers
/// the four storage contracts, the storage-provider marker (consumed by builder
/// validation), and provider capabilities.
/// </summary>
public static class CosmosDbMessageStoreBuilderExtensions
{
    /// <summary>
    /// Registers the Cosmos DB message store as the active NimBus storage provider.
    /// Reads the connection from configuration: <c>CosmosAccountEndpoint</c>,
    /// connection string named <c>"cosmos"</c>, or <c>CosmosConnection</c>, in that
    /// order. AAD is used when the endpoint does not contain <c>AccountKey=</c>.
    /// </summary>
    public static INimBusBuilder AddCosmosDbMessageStore(this INimBusBuilder builder)
        => builder.AddCosmosDbMessageStore(_ => { });

    /// <summary>
    /// Registers the Cosmos DB message store with explicit options configuration.
    /// Configuration in <c>NimBus:Cosmos</c> binds first; <paramref name="configure"/>
    /// runs after it, so code wins over configuration.
    /// </summary>
    public static INimBusBuilder AddCosmosDbMessageStore(
        this INimBusBuilder builder,
        Action<CosmosDbMessageStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var services = builder.Services;
        AddStoreOptions(services, configure);

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return CreateCosmosClient(config);
        });

        services.AddSingleton<INimBusMessageStore>(sp =>
        {
            var cosmosClient = sp.GetRequiredService<CosmosClient>();
            return new CosmosDbClient(
                cosmosClient,
                sp.GetService<ILogger<CosmosDbClient>>(),
                sp.GetRequiredService<IOptions<CosmosDbMessageStoreOptions>>().Value);
        });

        RegisterContracts(services);
        return builder;
    }

    /// <summary>
    /// Registers the Cosmos DB message store using a pre-constructed CosmosClient
    /// (useful for tests and advanced scenarios).
    /// </summary>
    public static INimBusBuilder AddCosmosDbMessageStore(this INimBusBuilder builder, CosmosClient cosmosClient)
        => builder.AddCosmosDbMessageStore(cosmosClient, _ => { });

    /// <summary>
    /// Registers the Cosmos DB message store using a pre-constructed CosmosClient, with
    /// explicit options configuration. Configuration in <c>NimBus:Cosmos</c> binds first
    /// when an <see cref="IConfiguration"/> is registered; <paramref name="configure"/>
    /// runs after it, so code wins over configuration.
    /// </summary>
    public static INimBusBuilder AddCosmosDbMessageStore(
        this INimBusBuilder builder,
        CosmosClient cosmosClient,
        Action<CosmosDbMessageStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(cosmosClient);
        ArgumentNullException.ThrowIfNull(configure);

        var services = builder.Services;
        AddStoreOptions(services, configure);

        services.AddSingleton(cosmosClient);
        services.AddSingleton<INimBusMessageStore>(sp =>
            new CosmosDbClient(
                cosmosClient,
                sp.GetService<ILogger<CosmosDbClient>>(),
                sp.GetRequiredService<IOptions<CosmosDbMessageStoreOptions>>().Value));

        RegisterContracts(services);
        return builder;
    }

    private static void AddStoreOptions(IServiceCollection services, Action<CosmosDbMessageStoreOptions> configure)
    {
        services.AddOptions<CosmosDbMessageStoreOptions>();

        // Bind through an optional IConfiguration: the explicit-CosmosClient overload is
        // documented as usable from a bare ServiceCollection, and Configure<IConfiguration>
        // would turn IConfiguration into a hard requirement — an additive option must not.
        services.AddSingleton<IConfigureOptions<CosmosDbMessageStoreOptions>>(sp =>
            new ConfigureOptions<CosmosDbMessageStoreOptions>(options =>
                sp.GetService<IConfiguration>()
                    ?.GetSection(CosmosDbMessageStoreOptions.SectionName)
                    .Bind(options)));

        // Registered after the bind, so code wins over configuration.
        services.AddOptions<CosmosDbMessageStoreOptions>().Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<CosmosDbMessageStoreOptions>, CosmosDbMessageStoreOptionsValidator>());
        services.AddSingleton<IHostedService, CosmosDbMessageStoreOptionsStartupValidator>();
    }

    private static void RegisterContracts(IServiceCollection services)
    {
        services.AddSingleton<IMessageTrackingStore>(sp =>
            NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
                sp.GetRequiredService<INimBusMessageStore>(),
                StoreProvider.Cosmos,
                sp.GetService<IOptionsMonitor<NimBusOpenTelemetryOptions>>()));
        services.AddSingleton<ISubscriptionStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IEndpointMetadataStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IMetricsStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IEventSchemaStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IAccessControlStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IServiceHealthStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IEndpointAcknowledgementStore>(sp => sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IHeartbeatHistoryStore>(sp =>
            (IHeartbeatHistoryStore)sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<ISubscriptionNotificationStore>(sp =>
            (ISubscriptionNotificationStore)sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IStorageProviderRegistration>(_ => new CosmosDbStorageProviderRegistration());
        services.AddSingleton<IStorageProviderCapabilities>(_ => new CosmosDbStorageProviderCapabilities());
        services.AddSingleton<ICosmosContainerAdmin>(sp =>
        {
            var accountResourceId = sp.GetService<IConfiguration>()?["CosmosAccountResourceId"];
            if (!string.IsNullOrWhiteSpace(accountResourceId))
            {
                return new ArmCosmosContainerAdmin(
                    new ArmClient(new DefaultAzureCredential()),
                    accountResourceId);
            }

            return new CosmosContainerAdmin(sp.GetRequiredService<CosmosClient>());
        });
    }

    internal static CosmosClient CreateCosmosClient(IConfiguration config)
    {
        // Treat empty strings as missing — appsettings.json may declare an empty
        // default like "CosmosAccountEndpoint": "" which would otherwise short-
        // circuit the null-coalescing fallback chain and pass "" to CosmosClient.
        var endpoint = NullIfEmpty(config.GetValue<string>("CosmosAccountEndpoint"));
        var connStr = NullIfEmpty(config.GetConnectionString("cosmos"));
        var connFallback = NullIfEmpty(config.GetValue<string>("CosmosConnection"));

        if (endpoint is not null && !endpoint.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase))
        {
            return new CosmosClient(endpoint, new DefaultAzureCredential());
        }

        var connectionString = endpoint
            ?? connStr
            ?? connFallback
            ?? throw new InvalidOperationException(
                "Cosmos DB configuration is required. Set 'CosmosAccountEndpoint', the 'cosmos' connection string, or 'CosmosConnection'.");
        return new CosmosClient(connectionString);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

internal sealed class CosmosDbStorageProviderRegistration : IStorageProviderRegistration
{
    public string ProviderName => "Cosmos DB";
}

internal sealed class CosmosDbStorageProviderCapabilities : IStorageProviderCapabilities
{
    public bool SupportsCrossAccountCopy => true;
}
