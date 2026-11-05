#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Registration-level behaviour of the retention option: it binds from configuration,
/// code configuration wins over it, an invalid value fails host startup (not the first
/// write), and none of this requires an <see cref="IConfiguration"/> to be present.
/// </summary>
[TestClass]
public sealed class CosmosDbMessageStoreRegistrationTests
{
    [TestMethod]
    public void Retention_binds_from_configuration()
    {
        var options = Resolve(config: new() { ["NimBus:Cosmos:UnresolvedRetentionDays"] = "180" });

        Assert.AreEqual(180, options.UnresolvedRetentionDays);
    }

    [TestMethod]
    public void Retention_defaults_to_unlimited_when_configuration_is_silent()
    {
        Assert.AreEqual(-1, Resolve().UnresolvedRetentionDays);
    }

    [TestMethod]
    public void Code_configuration_wins_over_configuration()
    {
        var options = Resolve(
            config: new() { ["NimBus:Cosmos:UnresolvedRetentionDays"] = "100" },
            configure: o => o.UnresolvedRetentionDays = 200);

        Assert.AreEqual(200, options.UnresolvedRetentionDays);
    }

    [TestMethod]
    public void Code_configuration_observes_the_bound_value_and_runs_once()
    {
        var options = Resolve(
            config: new() { ["NimBus:Cosmos:UnresolvedRetentionDays"] = "100" },
            configure: o => o.UnresolvedRetentionDays -= 10);

        Assert.AreEqual(90, options.UnresolvedRetentionDays);
    }

    [TestMethod]
    public void Store_resolves_from_a_bare_service_collection_with_no_configuration()
    {
        using var provider = BuildProvider(config: null);

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
        Assert.IsNotNull(provider.GetRequiredService<ICosmosContainerAdmin>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<INimBusMessageStore>());
        Assert.AreSame((object)aggregate, (object)provider.GetRequiredService<INimBusMessageStore>());
        Assert.AreEqual(-1, provider.GetRequiredService<IOptions<CosmosDbMessageStoreOptions>>().Value.UnresolvedRetentionDays);
    }

    [TestMethod]
    public void Code_configuration_works_with_no_configuration_present()
    {
        using var provider = BuildProvider(config: null, configure: o => o.UnresolvedRetentionDays = 180);

        Assert.IsNotNull(provider.GetRequiredService<INimBusMessageStore>());
        Assert.AreEqual(180, provider.GetRequiredService<IOptions<CosmosDbMessageStoreOptions>>().Value.UnresolvedRetentionDays);
    }

    [TestMethod]
    public void Control_plane_container_admin_is_selected_when_resource_id_is_configured()
    {
        using var provider = BuildProvider(config: new()
        {
            ["CosmosAccountResourceId"] =
                "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg/providers/Microsoft.DocumentDB/databaseAccounts/account",
        });

        Assert.IsInstanceOfType<ArmCosmosContainerAdmin>(
            provider.GetRequiredService<ICosmosContainerAdmin>());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-2)]
    [DataRow(366)]
    public async Task An_invalid_configured_value_fails_host_startup(int days)
    {
        using var provider = BuildProvider(
            config: new() { ["NimBus:Cosmos:UnresolvedRetentionDays"] = days.ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var startupValidator = provider.GetServices<IHostedService>()
            .Single(s => s.GetType().Name.Contains("CosmosDbMessageStoreOptionsStartupValidator", StringComparison.Ordinal));

        var ex = await Assert.ThrowsExactlyAsync<OptionsValidationException>(
            () => startupValidator.StartAsync(CancellationToken.None));

        StringAssert.Contains(string.Join(" ", ex.Failures), nameof(CosmosDbMessageStoreOptions.UnresolvedRetentionDays));
        StringAssert.Contains(string.Join(" ", ex.Failures), days.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task A_valid_configured_value_starts_cleanly()
    {
        using var provider = BuildProvider(
            config: new() { ["NimBus:Cosmos:UnresolvedRetentionDays"] = "365" });

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public void An_invalid_value_from_code_configuration_is_rejected_too()
    {
        using var provider = BuildProvider(config: null, configure: o => o.UnresolvedRetentionDays = 0);

        Assert.ThrowsExactly<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<CosmosDbMessageStoreOptions>>().Value);
    }

    private static CosmosDbMessageStoreOptions Resolve(
        Dictionary<string, string?>? config = null,
        Action<CosmosDbMessageStoreOptions>? configure = null)
    {
        using var provider = BuildProvider(config, configure);
        return provider.GetRequiredService<IOptions<CosmosDbMessageStoreOptions>>().Value;
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?>? config,
        Action<CosmosDbMessageStoreOptions>? configure = null)
    {
        var services = new ServiceCollection();
        if (config is not null)
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        }

        var builder = new NimBusBuilder(services);

        // The explicit-CosmosClient overload is the one documented as usable without a
        // configuration system, which is exactly what makes it the right seam here.
        using var cosmosClient = new CosmosClient("AccountEndpoint=https://localhost:8081/;AccountKey=Zm9vYmFy");
        if (configure is null)
        {
            builder.AddCosmosDbMessageStore(cosmosClient);
        }
        else
        {
            builder.AddCosmosDbMessageStore(cosmosClient, configure);
        }

        return services.BuildServiceProvider();
    }
}
