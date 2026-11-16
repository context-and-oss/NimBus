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
using NimBus.Extensions.IntegrationIntelligence.Storage;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Registers the optional integration-intelligence feature.</summary>
public static class IntegrationIntelligenceRegistration
{
    private const string ModuleSection = "NimBus:IntegrationIntelligence:FailureClassification";

    /// <summary>
    /// Reads and validates a contained configuration snapshot, prunes feature controllers when
    /// disabled or unreadable, and registers recording services only when the evidence and
    /// threshold settings are valid.
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
                    manager.FeatureProviders.Add(new IntegrationIntelligenceFeatureProvider(false, false, false)));
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
        var validationErrors = bindingErrors.Concat(options.ValidateEvidence()).ToArray();
        var hasStorage = services.Any(d => d.ServiceType == typeof(IIntegrationIntelligenceStorageSettings) || d.ServiceType == typeof(IFailureClassificationStore));
        var hasHostAdapter = services.Any(d => d.ServiceType == typeof(IIntegrationIntelligenceHost));
        // Spec 043: saved classifications stay readable when only threshold or evidence settings
        // are invalid. A binding failure still fails closed. Spec 044 §6.7: recording needs valid
        // evidence and thresholds.
        var readable = options.Enabled && bindingErrors.Count == 0 && hasStorage && hasHostAdapter;
        var recordable = readable && validationErrors.Length == 0;
        services.AddSingleton(options);
        services.AddSingleton(new IntegrationIntelligenceActivation(options.Enabled, validationErrors) { Readable = readable, Recordable = recordable });
        services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new IntegrationIntelligenceFeatureProvider(options.Enabled, readable, hasHostAdapter)));

        var removedSettings = RemovedProviderSettings(configuration.GetSection(ModuleSection));
        if (options.Enabled && removedSettings.Length > 0)
        {
            services.AddSingleton<IHostedService>(sp => new RemovedProviderSettingsWarning(
                removedSettings, sp.GetRequiredService<ILoggerFactory>()));
        }

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

        // Recordable mode: evidence for agents, and recording.
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
        return services;
    }

    /// <summary>
    /// The settings of the built-in provider removed in v5.0.0 that are still configured: a
    /// <c>Provider</c> other than <c>None</c>, the <c>TypeSafe</c> section, and the flat
    /// provider keys. The binder ignores them; the names (never the values) are logged.
    /// </summary>
    private static string[] RemovedProviderSettings(IConfigurationSection module)
    {
        var removed = new List<string>();
        var provider = module["Provider"];
        if (!string.IsNullOrWhiteSpace(provider) && !string.Equals(provider, "None", StringComparison.OrdinalIgnoreCase))
            removed.Add("Provider");
        removed.AddRange(module.GetSection("TypeSafe").GetChildren()
            .Where(child => child.Value is not null || child.GetChildren().Any())
            .Select(child => "TypeSafe:" + child.Key));
        removed.AddRange(new[] { "ApiKey", "BaseUrl", "Model", "TimeoutSeconds" }.Where(key => module[key] is not null));
        return [.. removed];
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
    /// Off: no intelligence controllers. Enabled but unreadable: status only. Readable: status and
    /// the classification GET routes.
    /// </summary>
    private sealed class IntegrationIntelligenceFeatureProvider(bool enabled, bool readable, bool hasHostAdapter) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            for (var index = feature.Controllers.Count - 1; index >= 0; index--)
            {
                var type = feature.Controllers[index].AsType();
                var keep = type == typeof(IntegrationIntelligenceStatusController) ? enabled && hasHostAdapter
                    : type == typeof(IntegrationIntelligenceReadController) ? readable
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
            "Integration Intelligence recording is unavailable because enabled configuration is invalid or incomplete; saved classifications stay readable when storage is configured: {Diagnostics}");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Validation messages contain field names and fixed bounds only. Secret
        // values are never copied into this diagnostic.
        LogWarning(loggerFactory.CreateLogger("NimBus.IntegrationIntelligence"), string.Join(" ", diagnostics), null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RemovedProviderSettingsWarning(IReadOnlyList<string> keys, ILoggerFactory loggerFactory) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> LogWarning =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "RemovedProviderSettings"),
            "The built-in failure-intelligence provider was removed in NimBus 5.0.0, so these NimBus:IntegrationIntelligence:FailureClassification settings are ignored: {Keys}. Agents record classifications over MCP; remove the settings, and any provider API key, from the deployment.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Key names only; a provider API key value is never logged.
        LogWarning(loggerFactory.CreateLogger("NimBus.IntegrationIntelligence"), string.Join(", ", keys), null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Activation snapshot used by status and tests.</summary>
public sealed record IntegrationIntelligenceActivation(bool Enabled, IReadOnlyList<string> ValidationErrors)
{
    /// <summary>
    /// True when saved classifications can be read: enabled, bound, with storage and a host adapter.
    /// Read-only mode is readable without being recordable (spec 043).
    /// </summary>
    public bool Readable { get; init; }

    /// <summary>
    /// True when agents can read evidence and record classifications over MCP: readable, with
    /// valid evidence and threshold settings (Spec 044 §6.7).
    /// </summary>
    public bool Recordable { get; init; }
}

/// <summary>
/// Stable status response data. <paramref name="Status"/> is <c>Recordable</c> (agents record
/// over MCP) or <c>ReadOnly</c> (saved results only).
/// </summary>
public sealed record IntegrationIntelligenceStatus(
    string Status,
    int ContractVersion,
    string EndpointId)
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
        return new IntegrationIntelligenceStatus(
            _activation.Recordable ? "Recordable" : "ReadOnly",
            1,
            endpointId)
        {
            CanRecord = _activation.Recordable && allowed,
        };
    }
}
