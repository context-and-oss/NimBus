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
                configuration.GetSection(SqlServerMessageStoreOptions.SectionName).Bind(options);

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
                "SqlServerMessageStoreOptions.Schema is required.")
            .Validate(o => o.RetentionInterval > TimeSpan.Zero && o.RetentionInterval <= SqlServerMessageStoreOptions.MaxRetentionInterval,
                "SqlServerMessageStoreOptions.RetentionInterval must be greater than zero and at most 49 days.");

        // IValidateOptions rather than Validate(predicate, message): the failure must name the offending value.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<SqlServerMessageStoreOptions>, SqlServerMessageStoreOptionsValidator>());
        services.TryAddSingleton<SqlServerSchemaReadiness>();

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

        services.AddSingleton<IStorageProviderRegistration>(_ => new SqlServerStorageProviderRegistration());
        services.AddSingleton<IStorageProviderCapabilities>(_ => new SqlServerStorageProviderCapabilities());

        services.AddSingleton<SqlServerMessageStoreHealthCheck>();
        services.AddSingleton<IHostedService, SqlServerSchemaInitializer>();

        return builder;
    }

    /// <summary>
    /// Registers the SQL Server retention job: a hosted service that, once per
    /// <see cref="SqlServerMessageStoreOptions.RetentionInterval"/>, deletes <c>Messages</c> older
    /// than 90 days, <c>MessageAudits</c> older than 365 days, archived or removed
    /// <c>UnresolvedEvents</c> older than 30 days, and — when
    /// <see cref="SqlServerMessageStoreOptions.UnresolvedRetentionDays"/> is set — other
    /// <c>UnresolvedEvents</c> older than that. Call it from exactly one kind of host (the WebApp)
    /// after <see cref="AddSqlServerMessageStore(INimBusBuilder)"/>; calling it twice registers one job.
    /// </summary>
    public static INimBusBuilder AddSqlServerMessageStoreRetention(this INimBusBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<SqlServerSchemaReadiness>();
        builder.Services.AddHostedService<SqlServerRetentionService>();
        return builder;
    }
}

internal sealed class SqlServerMessageStoreOptionsValidator : IValidateOptions<SqlServerMessageStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, SqlServerMessageStoreOptions options)
        => options.IsUnresolvedRetentionDaysValid
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                SqlServerMessageStoreOptions.DescribeInvalidUnresolvedRetentionDays(options.UnresolvedRetentionDays));
}

internal sealed class SqlServerStorageProviderRegistration : IStorageProviderRegistration
{
    public string ProviderName => "SQL Server";
}

internal sealed class SqlServerStorageProviderCapabilities : IStorageProviderCapabilities
{
    public bool SupportsCrossAccountCopy => false;
}
