#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Mcp.Tools;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 035 Phase 3: the action tools take the recovery commands, so the WebApp's own
/// registrations must be able to build them. A missing registration would break every tool that
/// changes a message, not only the new ones.
/// </summary>
[TestClass]
public sealed class OperatorRecoveryStartupWiringTests
{
    [TestMethod]
    public void The_WebApp_registrations_build_the_recovery_commands_and_the_action_tools()
    {
        using var host = new HostBuilder()
            .UseEnvironment("Development")
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NimBus:StorageProvider"] = "sqlserver",
                ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
                ["EnableLocalDevAuthentication"] = "true",
                ["NimBus:Mcp:EnableForLocalDevelopment"] = "true",
                ["ConnectionStrings:servicebus"] =
                    "Endpoint=sb://unit-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=test",
            }))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                    new Startup(context.Configuration, context.HostingEnvironment).ConfigureServices(services));
                web.Configure(_ => { });
            })
            .Build();
        using var scope = host.Services.CreateScope();

        var recovery = scope.ServiceProvider.GetRequiredService<IOperatorRecoveryCommands>();
        var tools = ActivatorUtilities.CreateInstance<OperatorActionTools>(scope.ServiceProvider);

        Assert.IsInstanceOfType<OperatorRecoveryCommands>(recovery);
        Assert.IsNotNull(tools);
    }
}
