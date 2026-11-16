#pragma warning disable CA1707, CA2007
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

/// <summary>
/// v5.0.0 removes the built-in provider (Spec 044 §6.10). A deployment that still carries its
/// settings starts in Recordable mode, serves no analyze route and is told the keys are ignored.
/// </summary>
[TestClass]
public sealed class ProviderRemovalTests
{
    private const string Module = "NimBus:IntegrationIntelligence:FailureClassification:";

    [TestMethod]
    public void Legacy_Provider_Settings_Start_Recordable_Without_An_Analyze_Route_Or_Provider_Client()
    {
        using var provider = Build(LegacySettings());

        var activation = provider.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsTrue(activation.Readable);
        Assert.IsTrue(activation.Recordable);
        var posts = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .Where(action => action.EndpointMetadata.OfType<HttpMethodMetadata>().Any(m => m.HttpMethods.Contains("POST")))
            .Select(action => action.AttributeRouteInfo?.Template ?? string.Empty)
            .Where(route => route.Contains("integration-intelligence", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.IsEmpty(posts, "The analyze route is removed.");
        var clients = provider.GetService<IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>();
        Assert.IsTrue(clients is null || clients.Get("NimBus.TypeSafe").HttpClientActions.Count == 0, "No provider client is registered.");
    }

    [TestMethod]
    public async Task Legacy_Provider_Settings_Log_One_Warning_Naming_The_Keys_But_Not_Their_Values()
    {
        var logs = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new CaptureLoggerProvider(logs)));
        using var provider = Build(LegacySettings(), loggerFactory);

        var warnings = provider.GetServices<IHostedService>().Where(s => s.GetType().Name == "RemovedProviderSettingsWarning").ToArray();
        Assert.HasCount(1, warnings);
        await warnings[0].StartAsync(default);
        Assert.HasCount(1, logs);
        Assert.Contains("TypeSafe:ApiKey", logs[0]);
        Assert.Contains("Provider", logs[0]);
        Assert.DoesNotContain("legacy-secret", logs[0]);
        Assert.IsEmpty(provider.GetServices<IHostedService>().Where(s => s.GetType().Name == "InvalidConfigurationWarning"),
            "Removed settings are ignored, not invalid.");
    }

    [TestMethod]
    public void Provider_None_Is_Not_Reported_As_A_Removed_Setting()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "true",
            [Module + "Provider"] = "None",
        });

        Assert.IsEmpty(provider.GetServices<IHostedService>().Where(s => s.GetType().Name == "RemovedProviderSettingsWarning"));
    }

    [TestMethod]
    public async Task Status_Reports_Recording_Without_Provider_Fields()
    {
        using var provider = Build(LegacySettings());
        using var scope = provider.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IntegrationIntelligenceStatusService>().GetAsync("endpoint", default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.AreEqual("Recordable", json.RootElement.GetProperty("status").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("canRecord").GetBoolean());
        foreach (var removed in new[] { "canAnalyze", "provider", "model" })
            Assert.IsFalse(json.RootElement.TryGetProperty(removed, out _), removed);
    }

    [TestMethod]
    public async Task Status_Reads_ReadOnly_When_Only_Saved_Results_Can_Be_Read()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["NimBus:IntegrationIntelligence:Enabled"] = "true",
            [Module + "Thresholds:RetryLikely"] = "2",
        });
        using var scope = provider.CreateScope();

        var status = await scope.ServiceProvider.GetRequiredService<IntegrationIntelligenceStatusService>().GetAsync("endpoint", default);

        Assert.AreEqual("ReadOnly", status!.Status);
        Assert.IsFalse(status.CanRecord);
    }

    private static Dictionary<string, string?> LegacySettings() => new()
    {
        ["NimBus:IntegrationIntelligence:Enabled"] = "true",
        [Module + "Provider"] = "TypeSafe",
        [Module + "TypeSafe:ApiKey"] = "legacy-secret",
        [Module + "TypeSafe:Model"] = "jev-1.13.0",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings, ILoggerFactory? loggerFactory = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (loggerFactory is not null) services.AddSingleton(loggerFactory);
        services.AddSingleton<IIntegrationIntelligenceStorageSettings, SqlStorageSettings>();
        services.AddSingleton<IIntegrationIntelligenceHost, TestIntelligenceHost>();
        services.AddSingleton(System.Reflection.DispatchProxy.Create<NimBus.MessageStore.Abstractions.IMessageTrackingStore, MessageSource>());
        services.AddSingleton<NimBus.Core.Messages.PII.IEventJsonMasker, TestMasker>();
        services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceRegistration).Assembly);
        services.AddNimBusIntegrationIntelligence(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    private sealed class SqlStorageSettings : IIntegrationIntelligenceStorageSettings
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
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) messages.Add(formatter(state, exception));
            }
        }
    }
}
