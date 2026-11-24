#pragma warning disable CA1707, CA2007
extern alias ErpApi;

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CountryCodes = ErpApi::Erp.Api.Validation.CountryCodes;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// ERP accepts a customer from CRM only with an ISO 3166-1 alpha-2 country. The rejection is the
/// demo's data-quality failure, so its text must quote the value CRM sent: whoever reads the
/// failure compares it with the message's payload.
/// </summary>
[TestClass]
public sealed class ErpCountryCodeTests
{
    [TestMethod]
    [DataRow("DK")]
    [DataRow("GB")]
    [DataRow("US")]
    [DataRow("FI")]
    public void Problem_IsNullForAnIsoCode(string code)
    {
        Assert.IsNull(CountryCodes.Problem(code));
    }

    [TestMethod]
    [DataRow("UK")]
    [DataRow("XX")]
    [DataRow("dk")]
    [DataRow("D")]
    [DataRow("DNK")]
    public void Problem_QuotesTheValueThatWasSent(string code)
    {
        var problem = CountryCodes.Problem(code);

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, $"'{code}'", StringComparison.Ordinal);
        StringAssert.Contains(problem, "ISO 3166-1 alpha-2", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Problem_ReportsAMissingCountry(string? code)
    {
        Assert.IsNotNull(CountryCodes.Problem(code));
    }

    [TestMethod]
    public void Problem_NamesTheCodeForTheUnitedKingdom()
    {
        StringAssert.Contains(CountryCodes.Problem("UK"), "'GB'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void TheList_HoldsEveryAssignedCode()
    {
        Assert.AreEqual(249, CountryCodes.Iso3166Alpha2.Count);
    }
}
