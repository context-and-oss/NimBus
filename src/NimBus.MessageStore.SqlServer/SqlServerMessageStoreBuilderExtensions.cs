using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

                var interval = configuration.GetValue<TimeSpan?>(SqlServerMessageStoreOptions.RetentionIntervalKey);
                if (interval is not null)
                    options.RetentionInterval = interval.Value;
            })
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionString),
                "SqlServerMessageStoreOptions.ConnectionString is required (set 'SqlConnection', the 'sqlserver' connection string, or 'SqlServerConnection').")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Schema),
                "SqlServerMessageStoreOptions.Schema is required.")
            .Validate(o => o.RetentionInterval > TimeSpan.Zero && o.RetentionInterval <= SqlServerMessageStoreOptions.MaxRetentionInterval,
                $"SqlServerMessageStoreOptions.RetentionInterval ('{SqlServerMessageStoreOptions.RetentionIntervalKey}') must be greater than zero and at most 49 days.");

        services.TryAddSingleton<SqlServerSchemaReadiness>();

        // Retention periods (Spec 050): SQL Server keeps everything unless a period is pinned in
        // host configuration (NimBus:Retention:*). Read once; an invalid pin fails the host that
        // resolves it, which is the one running the cleanup.
        services.TryAddSingleton(sp =>
        {
            var configuration = sp.GetService<IConfiguration>();
            return new RetentionPolicy(
                RetentionPeriods.AllUnlimited,
                key => configuration?[$"{RetentionPolicy.SectionName}:{key}"]);
        });

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
        services.AddSingleton<IMonitorMetricsStore>(sp =>
            (IMonitorMetricsStore)sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IDashboardTokenStore>(sp =>
            (IDashboardTokenStore)sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IOperatorOperationStore>(sp =>
            (IOperatorOperationStore)sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IAlertRuleStore>(sp =>
            (IAlertRuleStore)sp.GetRequiredService<INimBusMessageStore>());
        services.AddSingleton<IAlertSignalStore>(sp =>
            (IAlertSignalStore)sp.GetRequiredService<INimBusMessageStore>());

        services.AddSingleton<IStorageProviderRegistration>(_ => new SqlServerStorageProviderRegistration());
        services.AddSingleton<IStorageProviderCapabilities>(_ => new SqlServerStorageProviderCapabilities());

        services.AddSingleton<SqlServerMessageStoreHealthCheck>();
        services.AddSingleton<IHostedService, SqlServerSchemaInitializer>();

        return builder;
    }

    /// <summary>
    /// Registers the SQL Server retention cleanup (Spec 050): a hosted service that, once per
    /// <see cref="SqlServerMessageStoreOptions.RetentionInterval"/>, deletes the rows whose
    /// retention period ran out, with the periods of <see cref="RetentionPolicy"/>. On SQL Server
    /// every period is unlimited until it is pinned in <c>NimBus:Retention:*</c>, so by default
    /// the cleanup deletes nothing. Call it from one kind of host only (the WebApp), after
    /// <see cref="AddSqlServerMessageStore(INimBusBuilder)"/>; calling it twice registers one cleanup.
    /// </summary>
    public static INimBusBuilder AddSqlServerMessageStoreRetention(this INimBusBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<SqlServerSchemaReadiness>();
        // TryAddEnumerable by implementation type: a second call adds nothing.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerRetentionService>());
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
