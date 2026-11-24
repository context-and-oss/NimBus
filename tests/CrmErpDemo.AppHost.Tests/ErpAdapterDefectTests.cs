#pragma warning disable CA1707, CA2007
extern alias ErpAdapter;

using System;
using System.Collections.Generic;
using CrmErpDemo.Contracts.Demo;
using CrmErpDemo.Contracts.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ErrorModeGuard = ErpAdapter::Erp.Adapter.Functions.Handlers.ErrorModeGuard;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The "application defect" reason runs a real mapping step with a real defect instead of
/// throwing an exception whose text describes code that does not exist. The failure therefore
/// depends on the message: it happens for the input the defect mishandles and quotes that input.
/// </summary>
[TestClass]
public sealed class ErpAdapterDefectTests
{
    private static readonly ErpFailureReason Defect = ErpFailureReasons.Find(ErpFailureReasons.ApplicationDefect)!;

    [TestMethod]
    public void Defect_FailsAnAccountInACountryTheMappingForgot_AndQuotesIt()
    {
        var ex = Assert.ThrowsExactly<KeyNotFoundException>(
            () => ErrorModeGuard.Fail(Defect, new CrmAccountCreated { CountryCode = "FI" }, nameof(CrmAccountCreated)));

        StringAssert.Contains(ex.Message, "'FI'", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Defect_FailsAnUpdatedAccountTheSameWay()
    {
        Assert.ThrowsExactly<KeyNotFoundException>(
            () => ErrorModeGuard.Fail(Defect, new CrmAccountUpdated { CountryCode = "FI" }, nameof(CrmAccountUpdated)));
    }

    [TestMethod]
    [DataRow("DK")]
    [DataRow("DE")]
    public void Defect_LetsAnAccountInAMappedCountryThrough(string countryCode)
    {
        ErrorModeGuard.Fail(Defect, new CrmAccountCreated { CountryCode = countryCode }, nameof(CrmAccountCreated));
    }

    [TestMethod]
    public void Defect_LetsMessagesWithoutACountryThrough()
    {
        ErrorModeGuard.Fail(Defect, new CrmContactCreated(), nameof(CrmContactCreated));
        ErrorModeGuard.Fail(Defect, null, "PlaceCustomerOnCreditHold");
    }

    [TestMethod]
    public void EveryOtherReason_ThrowsItsSimulatedFailure()
    {
        var transient = ErpFailureReasons.Find("transient_dependency")!;

        Assert.ThrowsExactly<TimeoutException>(
            () => ErrorModeGuard.Fail(transient, new CrmAccountCreated { CountryCode = "DK" }, nameof(CrmAccountCreated)));
    }
}
