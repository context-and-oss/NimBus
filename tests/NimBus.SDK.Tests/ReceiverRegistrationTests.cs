#pragma warning disable CA1707, CA2007
using System.Linq;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="NimBus.SDK.Extensions.ServiceCollectionExtensions.AddNimBusReceiver"/> registering
/// one hosted receiver PER CALL. It used to go through AddHostedService, whose
/// TryAddEnumerable dedups on the implementation type — so a second receiver (e.g. one
/// endpoint draining an extra ingress topic like CrmErpDemo's PartnerInbound) was
/// silently dropped and its subscription never drained.
/// </summary>
[TestClass]
public class ReceiverRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void Two_AddNimBusReceiver_calls_register_two_hosted_receivers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddNimBusSubscriber("CrmEndpoint", _ => { });

        services.AddNimBusReceiver(opts =>
        {
            opts.TopicName = "CrmEndpoint";
            opts.SubscriptionName = "CrmEndpoint";
        });
        services.AddNimBusReceiver(opts =>
        {
            opts.TopicName = "PartnerInbound";
            opts.SubscriptionName = "CrmEndpoint";
        });

        using var provider = services.BuildServiceProvider();
        var receivers = provider.GetServices<IHostedService>()
            .OfType<NimBusReceiverHostedService>()
            .ToList();

        Assert.AreEqual(2, receivers.Count,
            "Each AddNimBusReceiver call must yield its own hosted receiver; the second registration used to be silently dropped");
    }
}
