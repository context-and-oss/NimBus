#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerMessageStoreRegistrationTests
{
    [TestMethod]
    public void Store_registration_exposes_all_narrow_storage_contracts()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var builder = new NimBusBuilder(services);
        builder.AddSqlServerMessageStore(options =>
        {
            options.ConnectionString = "Server=localhost;Database=NimBus;User Id=sa;Password=not-a-real-password;TrustServerCertificate=True";
            options.Schema = "dbo";
        });

        using var provider = services.BuildServiceProvider();

        var aggregate = provider.GetRequiredService<INimBusMessageStore>();
        Assert.IsNotNull(provider.GetRequiredService<IMessageTrackingStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<ISubscriptionStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<ISubscriptionNotificationStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IEndpointMetadataStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IMetricsStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IEventSchemaStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IAccessControlStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IServiceHealthStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IEndpointAcknowledgementStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IHeartbeatHistoryStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<IMonitorMetricsStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<INimBusMessageStore>());
    }
}
