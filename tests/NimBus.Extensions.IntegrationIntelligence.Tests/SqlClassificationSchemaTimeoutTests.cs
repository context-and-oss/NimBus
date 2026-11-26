#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Extensions.IntegrationIntelligence.Storage;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

/// <summary>
/// The classification store's schema upgrade runs under the same schema-upgrade timeout as the
/// SQL Server message store, not SqlClient's 30-second default.
/// </summary>
[TestClass]
public sealed class SqlClassificationSchemaTimeoutTests
{
    [TestMethod]
    public void The_store_defaults_to_the_message_stores_default()
    {
        Assert.AreEqual(1800, new SqlClassificationStore("Server=(local);Database=test").SchemaUpgradeTimeoutSeconds);
        Assert.AreEqual(1800, ((IIntegrationIntelligenceStorageSettings)new HostWithoutTimeout()).SqlSchemaUpgradeTimeoutSeconds);
    }

    [TestMethod]
    [DataRow(3600)]
    [DataRow(0)]
    public void The_registered_store_takes_the_hosts_timeout(int seconds)
    {
        using var provider = Build(new Settings(seconds));

        var store = provider.GetRequiredService<IFailureClassificationStore>();

        Assert.IsInstanceOfType<SqlClassificationStore>(store);
        Assert.AreEqual(seconds, ((SqlClassificationStore)store).SchemaUpgradeTimeoutSeconds);
    }

    [TestMethod]
    public void A_negative_timeout_is_refused()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SqlClassificationStore("Server=(local);Database=test") { SchemaUpgradeTimeoutSeconds = -1 });
    }

    private static ServiceProvider Build(IIntegrationIntelligenceStorageSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(settings);
        services.AddSingleton<IIntegrationIntelligenceHost, TestIntelligenceHost>();
        services.AddSingleton(System.Reflection.DispatchProxy.Create<NimBus.MessageStore.Abstractions.IMessageTrackingStore, MessageSource>());
        services.AddSingleton<NimBus.Core.Messages.PII.IEventJsonMasker, TestMasker>();
        services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceRegistration).Assembly);
        services.AddNimBusIntegrationIntelligence(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["NimBus:IntegrationIntelligence:Enabled"] = "true" })
            .Build());
        return services.BuildServiceProvider();
    }

    /// <summary>A host written before the timeout existed: it gets the interface's default.</summary>
    private sealed class HostWithoutTimeout : IIntegrationIntelligenceStorageSettings
    {
        public string Provider => "sqlserver";

        public string? SqlConnectionString => "Server=(local);Database=test";

        public string SqlSchema => "nimbus";

        public Microsoft.Azure.Cosmos.CosmosClient? CosmosClient => null;

        public string? CosmosDatabaseName => null;
    }

    private sealed class Settings(int timeoutSeconds) : IIntegrationIntelligenceStorageSettings
    {
        public string Provider => "sqlserver";

        public string? SqlConnectionString => "Server=(local);Database=test";

        public string SqlSchema => "nimbus";

        public Microsoft.Azure.Cosmos.CosmosClient? CosmosClient => null;

        public string? CosmosDatabaseName => null;

        public int SqlSchemaUpgradeTimeoutSeconds => timeoutSeconds;
    }
}
