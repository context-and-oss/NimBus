#pragma warning disable CA1707, CA2007

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Agents;
using NimBus.Agents.Internal;

namespace NimBus.Agents.Tests;

[TestClass]
public class AgentServiceCollectionExtensionsTests
{
    private sealed record Ping(string Value);

    private sealed class TestHandler : IAgentHandler<Ping>
    {
        public Task<AgentResult> HandleAsync(AgentContext<Ping> context, CancellationToken cancellationToken) =>
            Task.FromResult(AgentResult.Done());
    }

    [TestMethod]
    public void AddNimBusAgent_MissingAgentId_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o => o.Subscribe("Ping")));
    }

    [TestMethod]
    public void AddNimBusAgent_MissingSubscribe_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o => o.AgentId = "a"));
    }

    [TestMethod]
    public void AddNimBusAgent_WaitSecondsOutOfRange_Throws()
    {
        var services = new ServiceCollection();
        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o =>
            {
                o.AgentId = "a";
                o.Subscribe("Ping");
                o.ReceiveWaitSeconds = 61;
            }));
    }

    [TestMethod]
    public void AddNimBusAgent_Valid_RegistersHandlerGatewayAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNimBusAgent<TestHandler, Ping>(o =>
        {
            o.AgentId = "enrichment-agent";
            o.Subscribe("Ping");
            o.BaseAddress = "http://nimbus-ops.test";
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType(provider.GetRequiredService<IAgentHandler<Ping>>(), typeof(TestHandler));
        Assert.IsNotNull(provider.GetRequiredService<IAgentBusGateway>());
        Assert.IsTrue(
            provider.GetServices<IHostedService>().Any(h => h is AgentLoopWorker<Ping>),
            "The agent loop should be registered as a hosted service.");
    }

    [TestMethod]
    public void AddNimBusAgent_WithApiKey_SendsTheKeyHeader()
    {
        var client = BuildGatewayClient(o => o.ApiKey = "nbak_abcdefghijkl_secret");

        Assert.AreEqual("nbak_abcdefghijkl_secret", client.DefaultRequestHeaders.GetValues("X-NimBus-Agent-Key").Single());
        Assert.AreEqual("enrichment-agent", client.DefaultRequestHeaders.GetValues("X-Agent-Id").Single());
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void AddNimBusAgent_WithoutApiKey_SendsNoKeyHeader(string? apiKey)
    {
        var client = BuildGatewayClient(o => o.ApiKey = apiKey);

        Assert.IsFalse(client.DefaultRequestHeaders.Contains("X-NimBus-Agent-Key"));
    }

    private static HttpClient BuildGatewayClient(Action<AgentOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNimBusAgent<TestHandler, Ping>(o =>
        {
            o.AgentId = "enrichment-agent";
            o.Subscribe("Ping");
            o.BaseAddress = "http://nimbus-ops.test";
            configure(o);
        });
        var provider = services.BuildServiceProvider();
        // The typed client's name is its service type's name.
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAgentBusGateway));
    }

    [TestMethod]
    public void AddNimBusAgent_CalledTwice_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddNimBusAgent<TestHandler, Ping>(o =>
        {
            o.AgentId = "first-agent";
            o.Subscribe("Ping");
            o.BaseAddress = "http://nimbus-ops.test";
        });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddNimBusAgent<TestHandler, Ping>(o =>
            {
                o.AgentId = "second-agent";
                o.Subscribe("Ping");
                o.BaseAddress = "http://other.test";
            }));

        StringAssert.Contains(ex.Message, "first-agent");
        StringAssert.Contains(ex.Message, "second-agent");
    }
}
