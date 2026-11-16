#pragma warning disable CA1707, CA2007
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Extensions.IntegrationIntelligence.Controllers;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class ActivationDiagnosticsTests
{
    [TestMethod]
    public void Missing_Host_Adapter_Disables_All_Intelligence_Routes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIntegrationIntelligenceStorageSettings, TestStorageSettings>();
        services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceReadController).Assembly);
        services.AddNimBusIntegrationIntelligence(Configuration(true, "0.75"));

        using var provider = services.BuildServiceProvider();
        var activation = provider.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsFalse(activation.Readable);
        var routes = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .Select(action => action.AttributeRouteInfo?.Template ?? string.Empty);
        Assert.IsFalse(routes.Any(route => route.Contains("integration-intelligence", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Invalid_Enabled_Configuration_Emits_One_Sanitized_Warning()
    {
        var logs = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CaptureLoggerProvider(logs)));
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton<IIntegrationIntelligenceStorageSettings, TestStorageSettings>();
        services.AddSingleton<IIntegrationIntelligenceHost, TestIntelligenceHost>();
        services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceReadController).Assembly);
        services.AddNimBusIntegrationIntelligence(Configuration(true, "not-a-number", "secret-key"));

        using var provider = services.BuildServiceProvider();
        var warnings = provider.GetServices<IHostedService>().Where(service => service.GetType().Name == "InvalidConfigurationWarning").ToArray();
        Assert.HasCount(1, warnings);
        await warnings[0].StartAsync(default);
        Assert.HasCount(1, logs);
        Assert.DoesNotContain("secret-key", logs[0]);
    }

    [TestMethod]
    public void Provider_None_Without_A_Key_Is_Recordable_Without_A_Warning()
    {
        // Spec 044 §6.7: recording needs storage and valid evidence and thresholds, not a provider.
        using var provider = Build(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "true",
            ["NimBus:IntegrationIntelligence:FailureClassification:Provider"] = "None",
        });

        var activation = provider.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsTrue(activation.Readable);
        Assert.IsTrue(activation.Recordable);
        Assert.IsEmpty(provider.GetServices<IHostedService>().Where(s => s.GetType().Name == "InvalidConfigurationWarning"));
        using var scope = provider.CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetService<FailureClassificationRecorder>());
        var routes = Routes(provider);
        Assert.IsTrue(routes.Any(route => route.Contains("classification", StringComparison.Ordinal)), "Saved results stay readable.");
    }

    [TestMethod]
    public void Default_Configuration_Is_Recordable_Without_A_Warning()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "true",
        });

        var activation = provider.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsTrue(activation.Recordable);
        Assert.IsEmpty(provider.GetServices<IHostedService>().Where(s => s.GetType().Name is "InvalidConfigurationWarning" or "RemovedProviderSettingsWarning"));
    }

    [TestMethod]
    public void Invalid_Thresholds_Leave_Saved_Results_Readable_But_Not_Recordable()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "true",
            ["NimBus:IntegrationIntelligence:FailureClassification:Provider"] = "None",
            ["NimBus:IntegrationIntelligence:FailureClassification:Thresholds:RetryLikely"] = "2",
        });

        var activation = provider.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsTrue(activation.Readable);
        Assert.IsFalse(activation.Recordable);
        using var scope = provider.CreateScope();
        Assert.IsNull(scope.ServiceProvider.GetService<FailureClassificationRecorder>());
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IIntegrationIntelligenceStorageSettings, TestStorageSettings>();
        services.AddSingleton<IIntegrationIntelligenceHost, TestIntelligenceHost>();
        services.AddSingleton(System.Reflection.DispatchProxy.Create<NimBus.MessageStore.Abstractions.IMessageTrackingStore, MessageSource>());
        services.AddSingleton<NimBus.Core.Messages.PII.IEventJsonMasker, TestMasker>();
        services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceReadController).Assembly);
        services.AddNimBusIntegrationIntelligence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    private static string[] Routes(IServiceProvider provider)
        => provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .Select(action => action.AttributeRouteInfo?.Template ?? string.Empty).ToArray();

    private static IConfiguration Configuration(bool enabled, string retryLikely, string apiKey = "test-key")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = enabled.ToString(),
            // A removed provider key stays in some deployments; it must never reach a log line.
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = apiKey,
            ["NimBus:IntegrationIntelligence:FailureClassification:Thresholds:RetryLikely"] = retryLikely,
        }).Build();

    private sealed class TestStorageSettings : IIntegrationIntelligenceStorageSettings
    {
        public string Provider => "sqlserver";
        public string? SqlConnectionString => "Server=(local);Database=test";
        public string SqlSchema => "nimbus";
        public Microsoft.Azure.Cosmos.CosmosClient? CosmosClient => null;
        public string? CosmosDatabaseName => null;
    }

    private sealed class CaptureLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(messages);
        public void Dispose() { }

        private sealed class CaptureLogger(List<string> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) messages.Add(formatter(state, exception));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
