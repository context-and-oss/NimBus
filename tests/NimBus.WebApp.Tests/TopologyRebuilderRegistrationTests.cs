#pragma warning disable CA1707, CA2007

using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.ServiceBus.Provisioning;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The subscription rebuild reconciles a mismatched rule the way <c>nb topology apply</c>
/// does for the same target: in place on a real namespace, delete-then-create on an
/// emulator, whose admin endpoint rejects a PUT for an existing rule. These tests resolve
/// <see cref="ITopologyRebuilder"/> from the real <see cref="Startup"/> registrations.
/// </summary>
[TestClass]
public sealed class TopologyRebuilderRegistrationTests
{
    private const string EmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string RealNamespaceConnectionString =
        "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test";

    [TestMethod]
    [DataRow("ConnectionStrings:servicebus", EmulatorConnectionString, true)]
    [DataRow("AzureWebJobsServiceBus", EmulatorConnectionString, true)]
    [DataRow("ConnectionStrings:servicebus", RealNamespaceConnectionString, false)]
    [DataRow("AzureWebJobsServiceBus", RealNamespaceConnectionString, false)]
    public void Registered_rebuilder_follows_the_configured_service_bus_target(string key, string connectionString, bool expectedEmulator)
    {
        using var host = BuildHost(new Dictionary<string, string?> { [key] = connectionString });

        var rebuilder = host.Services.GetRequiredService<ITopologyRebuilder>();

        var concrete = Assert.IsInstanceOfType<ServiceBusTopologyRebuilder>(rebuilder);
        Assert.AreEqual(expectedEmulator, concrete.IsEmulator);
    }

    [TestMethod]
    public void Servicebus_connection_string_wins_over_AzureWebJobsServiceBus()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:servicebus"] = RealNamespaceConnectionString,
                ["AzureWebJobsServiceBus"] = EmulatorConnectionString,
            })
            .Build();

        Assert.IsFalse(Startup.IsServiceBusEmulator(configuration));
    }

    private static IHost BuildHost(Dictionary<string, string?> serviceBusSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver",
            ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            ["EnableLocalDevAuthentication"] = "true",
        };
        foreach (var (key, value) in serviceBusSettings)
        {
            settings[key] = value;
        }

        return new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                    new Startup(context.Configuration, context.HostingEnvironment).ConfigureServices(services));
                web.Configure(_ => { });
            })
            .Build();
    }
}
