using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Providers;
using NimBus.Extensions.IntegrationIntelligence.Storage;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Registers the optional integration-intelligence feature.</summary>
public static class IntegrationIntelligenceRegistration
{
    /// <summary>
    /// Reads and validates a contained configuration snapshot, prunes feature
    /// controllers when disabled or unready, and registers execution services only
    /// when the provider configuration is valid.
    /// </summary>
    public static IServiceCollection AddNimBusIntegrationIntelligence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IntegrationIntelligenceOptions? parent = null;
        FailureClassificationOptions options;
        var bindingErrors = new List<string>();
        try
        {
            var section = configuration.GetSection("NimBus:IntegrationIntelligence");
            if (!section.GetValue<bool>("Enabled") || !section.GetValue("FailureClassification:Enabled", true))
            {
                services.AddControllers().ConfigureApplicationPartManager(manager =>
                    manager.FeatureProviders.Add(new IntegrationIntelligenceFeatureProvider(false, false, false, false)));
                return services;
            }
            parent = section.Get<IntegrationIntelligenceOptions>();
            options = parent?.FailureClassification ?? new();
            if (parent is not null)
            {
                options.IntelligenceEnabled = parent.Enabled;
                options.Enabled = parent.Enabled && options.Enabled;
                options.ApplyNestedSettings();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            options = new FailureClassificationOptions();
            var parentEnabled = string.Equals(configuration["NimBus:IntegrationIntelligence:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
            options.Enabled = parentEnabled;
            options.IntelligenceEnabled = parentEnabled;
            bindingErrors.Add("Classification configuration could not be bound.");
        }
        var evidenceErrors = bindingErrors.Concat(options.ValidateEvidence()).ToArray();
        var providerErrors = options.ValidateProvider();
        var validationErrors = evidenceErrors.Concat(providerErrors).ToArray();
        var hasStorage = services.Any(d => d.ServiceType == typeof(IIntegrationIntelligenceStorageSettings) || d.ServiceType == typeof(IFailureClassificationStore));
        var hasHostAdapter = services.Any(d => d.ServiceType == typeof(IIntegrationIntelligenceHost));
        // Spec 043: saved classifications stay readable when only provider, threshold or evidence
        // settings are invalid (for example no API key). A binding failure still fails closed.
        // Spec 044 §6.7: recording needs valid evidence and thresholds but no provider; provider
        // analysis (Ready) additionally needs a valid TypeSafe configuration.
        var readable = options.Enabled && bindingErrors.Count == 0 && hasStorage && hasHostAdapter;
        var recordable = readable && evidenceErrors.Length == 0;
        var ready = recordable && !options.ProviderDisabled && providerErrors.Count == 0;
        services.AddSingleton(options);
        services.AddSingleton(new IntegrationIntelligenceActivation(options.Enabled, ready, validationErrors) { Readable = readable, Recordable = recordable });
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new IntegrationIntelligenceFeatureProvider(options.Enabled, readable, ready, hasHostAdapter)));

        if (options.Enabled && (validationErrors.Length > 0 || !hasStorage || !hasHostAdapter))
        {
            var diagnostics = validationErrors
                .Concat(hasStorage ? [] : ["Durable classification storage is not configured."])
                .Concat(hasHostAdapter ? [] : ["Host authorization and audit adapters are not configured."])
                .ToArray();
            services.AddSingleton<IHostedService>(sp => new InvalidConfigurationWarning(
                diagnostics, sp.GetRequiredService<ILoggerFactory>()));
        }

        if (!options.Enabled)
        {
            return services;
        }

        services.AddScoped<IntegrationIntelligenceStatusService>();
        if (!readable)
        {
            return services;
        }

        // Read-only mode and above: durable storage, its schema and retention, and saved-result reads.
        services.TryAddSingleton<IFailureClassificationStore>(CreateStore);
        services.AddHostedService<SqlClassificationSchemaInitializer>();
        services.AddHostedService<ClassificationRetentionWorker>();
        services.AddScoped(sp => new FailureClassificationReader(
            sp.GetRequiredService<IIntegrationIntelligenceHost>(),
            sp.GetRequiredService<IFailureClassificationStore>(),
            sp.GetRequiredService<NimBus.MessageStore.Abstractions.IMessageTrackingStore>(),
            sp.GetService<ILogger<FailureClassificationReader>>()));
        if (!recordable)
        {
            return services;
        }

        // Recordable mode and above: evidence for agents, and recording without a provider.
        services.AddSingleton(_ => new IntelligenceDataRedactor(options));
        services.AddScoped(sp => new FailureEvidenceBuilder(
            sp.GetRequiredService<NimBus.MessageStore.Abstractions.IMessageTrackingStore>(),
            sp.GetRequiredService<NimBus.Core.Messages.PII.IEventJsonMasker>(),
            sp.GetService<NimBus.Core.Messages.PII.IEventJsonRedactor>(),
            sp.GetRequiredService<IntelligenceDataRedactor>(), options));
        services.AddScoped(sp => new FailureClassificationRecorder(
            options,
            sp.GetRequiredService<IIntegrationIntelligenceHost>(),
            sp.GetRequiredService<IFailureClassificationStore>(),
            sp.GetRequiredService<FailureEvidenceBuilder>(),
            sp.GetRequiredService<NimBus.MessageStore.Abstractions.IMessageTrackingStore>(),
            sp.GetService<TimeProvider>()));
        if (!ready)
        {
            return services;
        }

        services.AddHttpClient("NimBus.TypeSafe", client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IFailureIntelligenceProvider, TypeSafeFailureIntelligenceProvider>();
        services.AddScoped<FailureClassificationService>();
        return services;
    }

    private static IFailureClassificationStore CreateStore(IServiceProvider sp)
    {
        var storage = sp.GetService<IIntegrationIntelligenceStorageSettings>();
        if (storage is null) return new UnavailableClassificationStore();
        if (string.Equals(storage.Provider, "sqlserver", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(storage.SqlConnectionString) ? new UnavailableClassificationStore() : new SqlClassificationStore(storage.SqlConnectionString);
        }
        if (string.Equals(storage.Provider, "cosmos", StringComparison.OrdinalIgnoreCase)
            && storage.CosmosClient is not null
            && !string.IsNullOrWhiteSpace(storage.CosmosDatabaseName))
        {
            return new CosmosClassificationStore(storage.CosmosClient, storage.CosmosDatabaseName);
        }
        return new UnavailableClassificationStore();
    }

    /// <summary>
    /// Off: no intelligence controllers. Enabled but unreadable: status only. Read-only: status and
    /// the classification GET routes. Ready: everything, including POST analyze.
    /// </summary>
    private sealed class IntegrationIntelligenceFeatureProvider(bool enabled, bool readable, bool ready, bool hasHostAdapter) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            for (var index = feature.Controllers.Count - 1; index >= 0; index--)
            {
                var type = feature.Controllers[index].AsType();
                var keep = type == typeof(IntegrationIntelligenceStatusController) ? enabled && hasHostAdapter
                    : type == typeof(IntegrationIntelligenceReadController) ? readable
                    : type == typeof(IntegrationIntelligenceController) ? ready
                    : true;
                if (!keep) feature.Controllers.RemoveAt(index);
            }
        }
    }
}

internal sealed class SqlClassificationSchemaInitializer : IHostedService
{
    private static readonly Action<ILogger, Exception?> SchemaInitializationLog =
        LoggerMessage.Define(LogLevel.Error, new EventId(1, "SchemaInitializationFailed"), "Integration Intelligence SQL schema could not be initialized; classification remains unavailable until storage is fixed.");
    private readonly IServiceProvider _services;
    private readonly ILogger<SqlClassificationSchemaInitializer> _logger;

    public SqlClassificationSchemaInitializer(
        IServiceProvider services,
        ILogger<SqlClassificationSchemaInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_services.GetRequiredService<IFailureClassificationStore>() is not SqlClassificationStore sqlStore) return;
            await sqlStore.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SchemaInitializationLog(_logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class InvalidConfigurationWarning(IReadOnlyList<string> diagnostics, ILoggerFactory loggerFactory) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogWarning =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, "InvalidConfiguration"),
            "Integration Intelligence analysis is unavailable because enabled configuration is invalid or incomplete; saved classifications stay readable when storage is configured: {Diagnostics}");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Validation messages contain field names and fixed bounds only. Secret
        // values are never copied into this diagnostic.
        LogWarning(loggerFactory.CreateLogger("NimBus.IntegrationIntelligence"), string.Join(" ", diagnostics), null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Activation snapshot used by status and tests.</summary>
public sealed record IntegrationIntelligenceActivation(bool Enabled, bool Ready, IReadOnlyList<string> ValidationErrors)
{
    /// <summary>
    /// True when saved classifications can be read: enabled, bound, with storage and a host adapter.
    /// Ready implies readable; read-only mode is readable without being ready (spec 043).
    /// </summary>
    public bool Readable { get; init; }

    /// <summary>
    /// True when agents can read evidence and record classifications over MCP: readable, with
    /// valid evidence and threshold settings. No provider is needed. Ready implies recordable
    /// (Spec 044 §6.7).
    /// </summary>
    public bool Recordable { get => _recordable || Ready; init => _recordable = value; }

    private readonly bool _recordable;
}

/// <summary>
/// Stable status response data. <paramref name="Status"/> is <c>Ready</c> (provider analysis
/// available), <c>Recordable</c> (agents record over MCP; no provider) or
/// <c>ProviderNotConfigured</c> (saved results only).
/// </summary>
public sealed record IntegrationIntelligenceStatus(
    string Status,
    string? Provider,
    string? Model,
    int ContractVersion,
    string EndpointId,
    bool CanAnalyze)
{
    /// <summary>Whether agents can record classifications for this endpoint over MCP.</summary>
    public bool CanRecord { get; init; }
}

/// <summary>Returns feature status without resolving execution dependencies.</summary>
public sealed class IntegrationIntelligenceStatusService
{
    private readonly FailureClassificationOptions _options;
    private readonly IntegrationIntelligenceActivation _activation;
    private readonly IIntegrationIntelligenceHost _host;

    /// <summary>Creates the status service.</summary>
    public IntegrationIntelligenceStatusService(FailureClassificationOptions options, IntegrationIntelligenceActivation activation, IIntegrationIntelligenceHost host)
    {
        _options = options;
        _activation = activation;
        _host = host;
    }

    /// <summary>Gets status for a verified endpoint.</summary>
    public async Task<IntegrationIntelligenceStatus?> GetAsync(string endpointId, CancellationToken cancellationToken)
    {
        if (!await _host.EndpointExistsAsync(endpointId, cancellationToken).ConfigureAwait(false)) return null;
        if (!await _host.HasReaderAsync(endpointId, cancellationToken).ConfigureAwait(false))
            throw new ClassificationServiceException("Forbidden", 403);
        var allowed = _options.AllowedEndpoints.Length == 0
            || _options.AllowedEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase);
        var canAnalyze = _activation.Ready && allowed && await _host.HasContributorAsync(endpointId, cancellationToken).ConfigureAwait(false);
#pragma warning disable CS0618 // The deprecated provider's model is reported while it is Ready (Spec 044 §6.10).
        var model = _activation.Ready ? _options.Model : null;
#pragma warning restore CS0618
        return new IntegrationIntelligenceStatus(
            _activation.Ready ? "Ready" : _activation.Recordable ? "Recordable" : "ProviderNotConfigured",
            _activation.Ready ? _options.Provider : null,
            model,
            1,
            endpointId,
            canAnalyze)
        {
            CanRecord = _activation.Recordable && allowed,
        };
    }
}
