#pragma warning disable CA1707, CA2007
extern alias ErpApi;

using CrmErpDemo.Contracts.Demo;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ErrorModeState = ErpApi::Erp.Api.ErrorModeState;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The erp-web error mode can fail only a share of the messages. The rate defaults to 100% so
/// callers that send only <c>{ enabled }</c> (e2e helpers, the demo film) keep failing every message.
/// </summary>
[TestClass]
public sealed class ErpErrorModeStateTests
{
    [TestMethod]
    public void FailureRate_DefaultsToEveryMessage()
    {
        var state = new ErrorModeState();
        Assert.AreEqual(1.0, state.Snapshot().FailureRate);
    }

    [TestMethod]
    public void Set_WithARate_StoresIt()
    {
        var state = new ErrorModeState();

        var after = state.Set(enabled: false, reason: null, failureRate: 0.25);

        Assert.AreEqual(0.25, after.FailureRate);
        Assert.AreEqual(0.25, state.Snapshot().FailureRate);
    }

    [TestMethod]
    public void Set_WithoutARate_KeepsTheCurrentOne()
    {
        var state = new ErrorModeState();
        state.Set(enabled: true, reason: ErpFailureReasons.Random, failureRate: 0.4);

        var off = state.Set(enabled: false);
        Assert.AreEqual(0.4, off.FailureRate);
        Assert.AreEqual(ErpFailureReasons.Random, off.Reason);

        var on = state.Set(enabled: true, reason: "business_rule");
        Assert.AreEqual(0.4, on.FailureRate);
        Assert.AreEqual("business_rule", on.Reason);
    }
}
