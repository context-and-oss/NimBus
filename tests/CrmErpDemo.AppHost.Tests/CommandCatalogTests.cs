#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using CrmErpDemo.Contracts;
using CrmErpDemo.Contracts.Commands;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class CommandCatalogTests
{
    [TestMethod]
    public void RealCatalog_PassesCommandValidation()
    {
        var errors = PlatformValidation.ValidateCommandConsumers(new CrmErpPlatformConfiguration());

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
    }

    [TestMethod]
    public void RealCatalog_CreditHoldCommand_IsConsumedOnlyByErpEndpoint()
    {
        var platform = new CrmErpPlatformConfiguration();
        var commandType = platform.EventTypes.Single(t => t.Id == nameof(PlaceCustomerOnCreditHold));

        var consumers = platform.GetConsumers(commandType).Select(e => e.Id).ToList();

        CollectionAssert.AreEqual(new[] { "ErpEndpoint" }, consumers);
    }

    [TestMethod]
    public void SecondConsumerOfTheCommand_FailsPlatformValidation()
    {
        var platform = new BrokenPlatform();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PlatformValidation.EnsureCommandConsumers(platform));

        StringAssert.Contains(ex.Message, nameof(PlaceCustomerOnCreditHold), StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "2 consumers", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, nameof(RogueSecondConsumer), StringComparison.Ordinal);
    }

    // A throwaway catalog where a second endpoint also consumes the command —
    // exactly the misconfiguration ADR-014's validation exists to catch.
    private sealed class BrokenPlatform : Platform
    {
        public BrokenPlatform()
        {
            foreach (var endpoint in new CrmErpPlatformConfiguration().Endpoints)
            {
                AddEndpoint(endpoint);
            }

            AddEndpoint(new RogueSecondConsumer());
        }
    }

    private sealed class RogueSecondConsumer : Endpoint
    {
        public RogueSecondConsumer() => Consumes<PlaceCustomerOnCreditHold>();
    }
}
