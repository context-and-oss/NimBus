#pragma warning disable CA1707, CA2007
using System;
using CrmErpDemo.Contracts.Demo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The CRM outage's 503 is recorded by NimBus as failure evidence and read by whoever classifies
/// the failure, a person or an agent. It has to read like a real outage: text that names the demo
/// switch makes the failure look like something only a person can end, so it is never retried.
/// </summary>
[TestClass]
public sealed class CrmOutageResponseTests
{
    [TestMethod]
    [DataRow("simulat")]
    [DataRow("error mode")]
    [DataRow("error-mode")]
    [DataRow("/api/admin")]
    [DataRow("demo")]
    [DataRow("enabled")]
    public void Evidence_DoesNotNameTheDemoSwitch(string cue)
    {
        var evidence = CrmOutageResponse.ReasonPhrase + " " + CrmOutageResponse.Body;
        Assert.IsFalse(evidence.Contains(cue, StringComparison.OrdinalIgnoreCase), $"the outage evidence mentions '{cue}': {evidence}");
    }

    [TestMethod]
    public void Evidence_ReadsAsATemporaryOutageOfTheTarget()
    {
        Assert.AreEqual("Service Unavailable", CrmOutageResponse.ReasonPhrase);
        StringAssert.Contains(CrmOutageResponse.Body, "temporarily unavailable");
        StringAssert.Contains(CrmOutageResponse.Body, "Retry");
        Assert.IsTrue(CrmOutageResponse.RetryAfter > TimeSpan.Zero);
    }
}
