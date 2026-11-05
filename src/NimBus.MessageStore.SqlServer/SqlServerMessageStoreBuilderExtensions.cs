using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;
using NimBus.OpenTelemetry;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Provider-aware registration for the SQL Server-backed message store. This is the
/// single entry point consumers should call when running NimBus with SQL Server.
/// Registers the four storage contracts (via <see cref="INimBusMessageStore"/>),
/// the storage-provider marker (consumed by builder validation), capabilities,
/// the schema initializer hosted service, and a health check.
/// </summary>
public static class SqlServerMessageStoreBuilderExtensions
{
    /// <summary>
    /// Registers the SQL Server message store. Reads connection from configuration
    /// key <c>SqlConnection</c> or connection-string named <c>"sqlserver"</c>.
    /// </summary>
    public static INimBusBuilder AddSqlServerMessageStore(this INimBusBuilder builder)
        => builder.AddSqlServerMessageStore(_ => { });

    /// <summary>
    /// Registers the SQL Server message store with explicit options configuration.
    /// </summary>
    public static INimBusBuilder AddSqlServerMessageStore(
        this INimBusBuilder builder,
        Action<SqlServerMessageStoreOptions> configure)
    {
        var services = builder.Services;

        services.AddOptions<SqlServerMessageStoreOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                if (string.IsNullOrEmpty(options.ConnectionString))
                {
                    options.ConnectionString =
                        configuration.GetValue<string>("SqlConnection")
                        ?? configuration.GetConnectionString("sqlserver")
                        ?? configuration.GetValue<string>("SqlServerConnection")
                        ?? string.Empty;
                }
            })
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "SqlServerMessageStoreOptions.ConnectionString is required (set 'SqlConnection', the 'sqlserver' connection string, or 'SqlServerConnection').")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Schema),
                "SqlServerMessageStoreOptions.Schema is required.");

        services.AddSingleton<INimBusMessageStore, SqlServerMessageStore>();
        services.AddSingleton<IMessageTrackingStore>(sp =>
            NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
                sp.GetRequiredService<INimBusMessageStore>(),
                StoreProvider.SqlServer,
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

        services.AddSingleton<IStorageProviderRegistration>(_ => new SqlServerStorageProviderRegistration());
        services.AddSingleton<IStorageProviderCapabilities>(_ => new SqlServerStorageProviderCapabilities());

        services.AddSingleton<SqlServerMessageStoreHealthCheck>();
        services.AddSingleton<IHostedService, SqlServerSchemaInitializer>();

        return builder;
    }
}

internal sealed class SqlServerStorageProviderRegistration : IStorageProviderRegistration
{
    public string ProviderName => "SQL Server";
}

internal sealed class SqlServerStorageProviderCapabilities : IStorageProviderCapabilities
{
    public bool SupportsCrossAccountCopy => false;
}
