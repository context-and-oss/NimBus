#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NimBus.Adapter.Mcp.Tests.Infrastructure;

namespace NimBus.Adapter.Mcp.Tests;

[TestClass]
public sealed class AdapterMcpRegistrationTests
{
    [TestMethod]
    public async Task A_host_without_tenant_or_audience_fails_at_start()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdapterMcp:Audience"] = TestTokens.AdapterClientId,
        }).Build();
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<FakeAdapterData>();
                services.AddAdapterMcp<FakeAdapterSystem>(configuration);
            })
            .Build();

        var error = await Assert.ThrowsExactlyAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("AdapterMcp:TenantId", error.Message);
    }

    [TestMethod]
    public void The_options_bind_from_the_AdapterMcp_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(TestTokens.Settings())
        {
            ["AdapterMcp:ReadRole"] = "Erp.Read",
            ["AdapterMcp:CallLogCapacity"] = "25",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<FakeAdapterData>();
        services.AddAdapterMcp<FakeAdapterSystem>(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AdapterMcpOptions>>().Value;
        Assert.AreEqual(TestTokens.TenantId, options.TenantId);
        Assert.AreEqual(TestTokens.AdapterClientId, options.Audience);
        Assert.AreEqual(TestTokens.AdapterAppIdUri, options.ApplicationIdUri);
        Assert.AreEqual("Erp.Read", options.ReadRole);
        Assert.AreEqual(25, provider.GetRequiredService<AdapterCallLog>().Capacity);
        Assert.IsInstanceOfType<FakeAdapterSystem>(provider.CreateScope().ServiceProvider.GetRequiredService<IAdapterSystem>());
    }

    [TestMethod]
    public void The_call_log_capacity_holds_when_the_client_is_registered_first()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(TestTokens.Settings())
        {
            ["AdapterMcp:CallLogCapacity"] = "25",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<FakeAdapterData>();
        services.AddHttpClient("erp").AddAdapterCallLog("customer", _ => null);
        services.AddAdapterMcp<FakeAdapterSystem>(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.AreEqual(25, provider.GetRequiredService<AdapterCallLog>().Capacity);
    }
}
