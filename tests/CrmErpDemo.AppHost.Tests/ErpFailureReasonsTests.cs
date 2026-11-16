#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using CrmErpDemo.Contracts.Demo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The erp-web error-mode dropdown exists to feed Integration Intelligence distinct,
/// realistic failures. These pin the properties the showcase depends on: every reason maps
/// to an exception, the advertised NimBus disposition matches what the ERP adapter's failure
/// handling actually does, and the recorded message carries a cue for its target category.
/// </summary>
[TestClass]
public sealed class ErpFailureReasonsTests
{
    [TestMethod]
    public void Catalog_HasUniqueIdsAndAKnownDefault()
    {
        var ids = ErpFailureReasons.All.Select(r => r.Id).ToList();
        CollectionAssert.AllItemsAreUnique(ids);
        Assert.IsNotNull(ErpFailureReasons.Find(ErpFailureReasons.Default));
        Assert.AreEqual(ErpFailureReasons.Default, ErpFailureReasons.All[0].Id, "the default must be first so the UI lists it first");
    }

    [TestMethod]
    public void Find_IsCaseInsensitiveAndNullSafe()
    {
        Assert.AreEqual("business_rule", ErpFailureReasons.Find("BUSINESS_RULE")!.Id);
        Assert.IsNull(ErpFailureReasons.Find(null));
        Assert.IsNull(ErpFailureReasons.Find(" "));
        Assert.IsNull(ErpFailureReasons.Find("no_such_reason"));
    }

    [TestMethod]
    public void CreateException_UnknownReason_FallsBackToGenericSoErrorModeStaysOn()
    {
        var ex = ErpFailureReasons.CreateException("no_such_reason", "CrmAccountCreated");
        Assert.IsInstanceOfType<HandlerErrorModeException>(ex);
    }

    [TestMethod]
    public void EveryReason_DispositionMatchesTheErpAdaptersFailureHandling()
    {
        // Erp.Adapter.Functions registers no failure disposition classifier, so StrictMessageHandler
        // falls back to DefaultFailureDispositionClassifier; with no retry policy either, a failure
        // it does not dead-letter is recorded as Failed.
        var classifier = new DefaultFailureDispositionClassifier();
        foreach (var reason in ErpFailureReasons.All)
        {
            var ex = ErpFailureReasons.CreateException(reason.Id, "CrmAccountCreated");
            var expected = classifier.Classify(ex, "CrmAccountCreated", "ErpEndpoint") == FailureDisposition.DeadLetter
                ? "DeadLettered"
                : "Failed";
            Assert.AreEqual(
                expected,
                reason.Disposition,
                $"{reason.Id} advertises {reason.Disposition} but the ERP adapter records {expected} for {ex.GetType().Name}");
        }
    }

    [TestMethod]
    public void EveryReason_ProducesDistinctExceptionTypesWithEvidenceInTheMessage()
    {
        var byType = ErpFailureReasons.All
            .Select(r => ErpFailureReasons.CreateException(r.Id, "CrmContactCreated"))
            .GroupBy(e => e.GetType())
            .ToList();
        Assert.AreEqual(ErpFailureReasons.All.Count, byType.Count, "each reason should surface a different ErrorType in the WebApp");

        // NimBus stores only the message, never a stack trace, so the message is the evidence.
        foreach (var reason in ErpFailureReasons.All.Where(r => r.Id != ErpFailureReasons.Default))
        {
            var message = ErpFailureReasons.CreateException(reason.Id, "CrmContactCreated").Message;
            Assert.IsTrue(message.Length > 80, $"{reason.Id} message is too thin to classify: '{message}'");
        }
    }

    [TestMethod]
    public void Selectable_IsTheCatalogPlusRandomLast()
    {
        CollectionAssert.AreEqual(
            ErpFailureReasons.All.Select(r => r.Id).Append(ErpFailureReasons.Random).ToList(),
            ErpFailureReasons.Selectable.Select(r => r.Id).ToList());
        CollectionAssert.AllItemsAreUnique(ErpFailureReasons.Selectable.Select(r => r.Id).ToList());
        Assert.AreEqual(ErpFailureReasons.Random, ErpFailureReasons.Find("RANDOM")!.Id);
        Assert.IsFalse(ErpFailureReasons.All.Any(r => r.Id == ErpFailureReasons.Random), "All holds only failures that can be thrown");
    }

    [TestMethod]
    [DataRow(0.0, 0)]
    [DataRow(1.0, 1000)]
    public void Decide_RateZeroNeverFailsAndRateOneAlwaysFails(double failureRate, int expectedFailures)
    {
        var rng = new Random(17);
        var failures = Enumerable.Range(0, 1000).Count(_ => ErpFailureReasons.Decide(failureRate, "business_rule", rng) is not null);
        Assert.AreEqual(expectedFailures, failures);
    }

    [TestMethod]
    public void Decide_FailsAboutTheGivenShareOfMessages()
    {
        var rng = new Random(42);
        var failures = Enumerable.Range(0, 10_000).Count(_ => ErpFailureReasons.Decide(0.3, "business_rule", rng) is not null);
        Assert.IsTrue(failures is > 2_500 and < 3_500, $"expected about 30% of 10,000 to fail, got {failures}");
    }

    [TestMethod]
    public void Decide_ConcreteReasonAlwaysThrowsThatReason()
    {
        var rng = new Random(3);
        var picked = Enumerable.Range(0, 200).Select(_ => ErpFailureReasons.Decide(1.0, "contract_schema", rng)!.Id).Distinct().ToList();
        CollectionAssert.AreEqual(new[] { "contract_schema" }, picked);
    }

    [TestMethod]
    public void Decide_RandomPicksEveryConcreteReasonAndNeverRandomItself()
    {
        var rng = new Random(7);
        var picked = Enumerable.Range(0, 2_000).Select(_ => ErpFailureReasons.Decide(1.0, ErpFailureReasons.Random, rng)!.Id).ToHashSet();
        CollectionAssert.AreEquivalent(ErpFailureReasons.All.Select(r => r.Id).ToList(), picked.ToList());
    }

    [TestMethod]
    public void CreateException_Random_ThrowsOneOfTheConcreteFailures()
    {
        var concreteTypes = ErpFailureReasons.All.Select(r => ErpFailureReasons.CreateException(r.Id, "CrmAccountCreated").GetType()).ToHashSet();
        for (var i = 0; i < 50; i++)
        {
            var ex = ErpFailureReasons.CreateException(ErpFailureReasons.Random, "CrmAccountCreated");
            Assert.IsTrue(concreteTypes.Contains(ex.GetType()), $"{ex.GetType().Name} is not a catalog failure");
        }
    }

    [TestMethod]
    public void ContractSchema_QuotesTheInboundEventType()
    {
        var message = ErpFailureReasons.CreateException("contract_schema", "CrmAccountUpdated").Message;
        StringAssert.Contains(message, "CrmAccountUpdated", StringComparison.Ordinal);
    }
}
