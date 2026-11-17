using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>Loads the immutable Admin override before Startup registers optional controllers.</summary>
public static class IntelligenceSettingsBootstrap
{
    public const string RevisionKey = "NimBus:IntelligenceAdmin:ActiveRevision";
    public const string FailureKey = "NimBus:IntelligenceAdmin:LoadFailed";

    /// <summary>
    /// Runs an isolated, disposable configuration bootstrap, not a second application host.
    /// Reuses storage registration/credential precedence; no hosted services are started.
    /// </summary>
    public static IConfiguration Load(IConfiguration configuration)
    {
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(configuration);
            var provider = Startup.AddStorage(services, configuration);
            using var bootstrap = services.BuildServiceProvider();
            var storage = new WebAppIntegrationIntelligenceStorageSettings(provider, bootstrap);
            var store = IntelligenceSettingsStore.Create(storage);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            return LoadAsync(configuration, store, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Configuration exceptions can contain credentials; never print their text.
            Console.Error.WriteLine("Failure intelligence settings could not be loaded; classification is disabled until a successful restart.");
            return FailClosed(configuration);
        }
    }

    /// <summary>Applies the saved revision over deployment configuration.</summary>
    public static async Task<IConfiguration> LoadAsync(IConfiguration configuration, IIntelligenceSettingsStore store, CancellationToken cancellationToken)
    {
        var saved = await store.ReadAsync(cancellationToken).WaitAsync(cancellationToken);
        if (saved is not null && (saved.Settings.Validate().Count != 0 || !Guid.TryParse(saved.Revision, out _)))
            throw new InvalidOperationException("Stored intelligence settings are invalid.");
        var effective = saved is null ? configuration : saved.Settings.ApplyTo(configuration);
        return new ConfigurationBuilder().AddConfiguration(effective).AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RevisionKey] = saved?.Revision ?? "none", [FailureKey] = "false",
        }).Build();
    }

    public static IConfiguration FailClosed(IConfiguration configuration)
        => new ConfigurationBuilder().AddConfiguration(configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "false", [FailureKey] = "true", [RevisionKey] = "unavailable",
        }).Build();
}
