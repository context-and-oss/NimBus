#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>Startup validation of <see cref="SimulationOptions"/> (plan Decisions 4, 7 and 11).</summary>
[TestClass]
public sealed class SimulationOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(Action<SimulationOptions> configure)
    {
        var options = new SimulationOptions();
        configure(options);
        return new SimulationOptionsValidator(SimulationTestPlatform.Create()).Validate(null, options);
    }

    [TestMethod]
    public void Defaults_are_valid()
    {
        Assert.IsTrue(Validate(_ => { }).Succeeded);
    }

    [TestMethod]
    public void Owned_consumers_are_valid()
    {
        Assert.IsTrue(Validate(o => o.OwnedEndpoints = new() { SimulationTestPlatform.Billing, SimulationTestPlatform.Warehouse }).Succeeded);
    }

    [TestMethod]
    [DataRow("prod")]
    [DataRow("Production")]
    public void A_production_name_in_the_allowlist_fails(string name)
    {
        var result = Validate(o => o.AllowedEnvironments.Add(name));

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, name, StringComparison.Ordinal);
        StringAssert.Contains(result.FailureMessage, "production", StringComparison.Ordinal);
    }

    [TestMethod]
    public void An_unknown_owned_endpoint_fails()
    {
        var result = Validate(o => o.OwnedEndpoints = new() { "NoSuchEndpoint" });

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "NoSuchEndpoint", StringComparison.Ordinal);
    }

    [TestMethod]
    public void A_producer_only_endpoint_cannot_be_owned()
    {
        var result = Validate(o => o.OwnedEndpoints = new() { SimulationTestPlatform.Storefront });

        Assert.IsTrue(result.Failed, "Storefront only produces; there is no subscription for the simulator to own.");
    }

    [TestMethod]
    [DataRow(nameof(SimulationOptions.AutoStopMinutes), 0)]
    [DataRow(nameof(SimulationOptions.AutoStopMinutes), 241)]
    [DataRow(nameof(SimulationOptions.MaxRateCeilingPerMinute), 0)]
    [DataRow(nameof(SimulationOptions.MaxRateCeilingPerMinute), 6001)]
    [DataRow(nameof(SimulationOptions.RateCeilingPerMinute), 0)]
    [DataRow(nameof(SimulationOptions.RateCeilingPerMinute), 1201)]
    [DataRow(nameof(SimulationOptions.SessionPoolSize), 0)]
    [DataRow(nameof(SimulationOptions.SessionPoolSize), 1001)]
    [DataRow(nameof(SimulationOptions.DrainSeconds), -1)]
    [DataRow(nameof(SimulationOptions.DrainSeconds), 121)]
    [DataRow(nameof(SimulationOptions.PauseDeadlineSeconds), 0)]
    [DataRow(nameof(SimulationOptions.StopDeadlineSeconds), 0)]
    [DataRow(nameof(SimulationOptions.StopDeadlineSeconds), 121)]
    public void Each_out_of_range_option_fails(string property, int value)
    {
        var result = Validate(o => typeof(SimulationOptions).GetProperty(property)!.SetValue(o, value));

        Assert.IsTrue(result.Failed, $"{property}={value} must fail validation.");
        StringAssert.Contains(result.FailureMessage, property, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Blank_session_prefix_fails()
    {
        Assert.IsTrue(Validate(o => o.SessionPrefix = " ").Failed);
    }

    [TestMethod]
    public void Every_violation_is_reported()
    {
        var result = Validate(o =>
        {
            o.AutoStopMinutes = 0;
            o.SessionPoolSize = 0;
            o.AllowedEnvironments.Add("live");
        });

        Assert.AreEqual(3, result.Failures!.Count());
    }

    [TestMethod]
    public void Configured_allowed_environments_add_to_the_defaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("NimBus:Simulation:AllowedEnvironments:0", "test") })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IPlatform>(SimulationTestPlatform.Create());
        services.AddNimBusSimulation(configuration);
        using var provider = services.BuildServiceProvider();

        var allowed = provider.GetRequiredService<IOptions<SimulationOptions>>().Value.AllowedEnvironments;

        CollectionAssert.IsSubsetOf(new[] { "dev", "development", "test" }, allowed, "docs/webapp-simulate.md documents that the list appends.");
    }

    [TestMethod]
    public void ValidateOnStart_rejects_a_production_name_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new System.Collections.Generic.KeyValuePair<string, string?>("NimBus:Simulation:AllowedEnvironments:2", "prod") })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IPlatform>(SimulationTestPlatform.Create());
        services.AddNimBusSimulation(configuration);
        using var provider = services.BuildServiceProvider();

        var error = Assert.ThrowsExactly<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SimulationOptions>>().Value);
        StringAssert.Contains(error.Message, "prod", StringComparison.Ordinal);
    }
}
