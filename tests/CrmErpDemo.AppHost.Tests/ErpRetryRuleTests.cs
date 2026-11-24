#pragma warning disable CA1707, CA2007
extern alias ErpAdapter;

using System;
using System.Linq;
using CrmErpDemo.Contracts.Demo;
using CrmErpDemo.Contracts.E2E;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using ErpResilience = ErpAdapter::Erp.Adapter.Functions.ErpResilience;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The ERP adapter's one retry rule is the sample's retry showcase. These pin what it promises:
/// the rate-limited failure is retried, and nothing else is, so every other error-mode reason
/// still ends in Failed for an operator (or an operations agent) to recover.
/// </summary>
[TestClass]
public sealed class ErpRetryRuleTests
{
    private const string Throttled = "throttled_dependency";

    private static DefaultRetryPolicyProvider Rules()
    {
        var policies = new DefaultRetryPolicyProvider();
        ErpResilience.ConfigureRetries(policies);
        return policies;
    }

    // The message handler hands the provider the handler's exception wrapped like this.
    private static RetryPolicy? PolicyFor(DefaultRetryPolicyProvider policies, Exception thrown) =>
        policies.GetRetryPolicy("CrmAccountCreated", new EventContextHandlerException(thrown), "ErpEndpoint");

    [TestMethod]
    public void ThrottledReason_IsInTheCatalogAndThrowsTheTypedException()
    {
        Assert.IsNotNull(ErpFailureReasons.All.SingleOrDefault(r => r.Id == Throttled));
        Assert.IsInstanceOfType<ErpPricingThrottledException>(ErpFailureReasons.CreateException(Throttled, "CrmAccountCreated"));
    }

    [TestMethod]
    public void ThrottledFailure_IsRetriedThreeTimes()
    {
        var policy = PolicyFor(Rules(), ErpFailureReasons.CreateException(Throttled, "CrmAccountCreated"));

        Assert.IsNotNull(policy);
        Assert.AreEqual(3, policy.MaxRetries);
    }

    [TestMethod]
    public void ThrottledFailure_CarriesTheRetryAfterItsTextQuotes()
    {
        var ex = ErpFailureReasons.CreateException(Throttled, "CrmAccountCreated");

        var hint = Assert.IsInstanceOfType<IRetryAfterHint>(ex);
        Assert.AreEqual(TimeSpan.FromSeconds(5), hint.RetryAfter);
        StringAssert.Contains(ex.Message, "429", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "Retry-After: 5", StringComparison.Ordinal);
    }

    [TestMethod]
    public void RetryAfter_FitsUnderThePolicysCap()
    {
        // A hint above MaxDelay would be cut to the cap, and the text would then quote a wait
        // NimBus does not honour.
        var ex = (IRetryAfterHint)ErpFailureReasons.CreateException(Throttled, "CrmAccountCreated");
        var policy = PolicyFor(Rules(), (Exception)ex)!;

        Assert.IsNotNull(policy.MaxDelay);
        Assert.IsTrue(ex.RetryAfter <= policy.MaxDelay);
    }

    [TestMethod]
    public void NoOtherReason_IsRetried()
    {
        var policies = Rules();
        foreach (var reason in ErpFailureReasons.All.Where(r => r.Id != Throttled))
        {
            var ex = ErpFailureReasons.CreateException(reason.Id, "CrmAccountCreated");
            Assert.IsNull(PolicyFor(policies, ex), $"{reason.Id} ({ex.GetType().Name}) must not be retried automatically");
        }
    }

    [TestMethod]
    public void E2eRule_SitsBesideTheAdaptersRule()
    {
        // A subscriber's ConfigureRetryPolicies replaces a provider registered in DI, so in the
        // e2e profile the adapter has to add the scripted rule to its own provider.
        var policies = Rules().AddE2eRetryRule();

        Assert.AreEqual(2, PolicyFor(policies, new InvalidOperationException("E2E configured retry"))!.MaxRetries);
        Assert.AreEqual(3, PolicyFor(policies, ErpFailureReasons.CreateException(Throttled, "CrmAccountCreated"))!.MaxRetries);
        Assert.IsNull(PolicyFor(policies, new InvalidOperationException("E2E fail")));
    }
}
