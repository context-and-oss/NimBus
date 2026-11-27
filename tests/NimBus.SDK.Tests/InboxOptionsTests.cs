#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class InboxOptionsTests
{
    [TestMethod]
    public void CleanupTimeBudget_defaults_to_five_minutes()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(5), new InboxOptions().CleanupTimeBudget);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void CleanupTimeBudget_rejects_non_positive_values(int seconds)
    {
        var options = new InboxOptions();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => options.CleanupTimeBudget = TimeSpan.FromSeconds(seconds));
    }

    [TestMethod]
    public void CleanupTimeBudget_round_trips_a_positive_value()
    {
        var options = new InboxOptions { CleanupTimeBudget = TimeSpan.FromMinutes(90) };

        Assert.AreEqual(TimeSpan.FromMinutes(90), options.CleanupTimeBudget);
    }
}
