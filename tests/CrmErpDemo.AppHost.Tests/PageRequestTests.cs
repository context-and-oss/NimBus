#pragma warning disable CA1707, CA2007
using CrmErpDemo.Contracts.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The paging rules both demo APIs share. Regression: the CRM and ERP lists returned whole
/// tables, so a few thousand contacts made the Contacts page take seconds to load.
/// </summary>
[TestClass]
public sealed class PageRequestTests
{
    [TestMethod]
    public void From_WithoutPage_IsUnpaged()
    {
        // The e2e helpers and demo-video scripts search the whole list, so a request that does
        // not ask for a page still gets every row.
        Assert.IsNull(PageRequest.From(page: null, pageSize: null));
        Assert.IsNull(PageRequest.From(page: null, pageSize: 10));
    }

    [TestMethod]
    [DataRow(1, null, 1, 100, 0)]
    [DataRow(3, null, 3, 100, 200)]
    [DataRow(2, 25, 2, 25, 25)]
    [DataRow(1, 100, 1, 100, 0)]
    public void From_ReadsPageAndSize(int page, int? pageSize, int expectedPage, int expectedSize, int expectedSkip)
    {
        var request = PageRequest.From(page, pageSize);

        Assert.IsNotNull(request);
        Assert.AreEqual(expectedPage, request.Value.Page);
        Assert.AreEqual(expectedSize, request.Value.PageSize);
        Assert.AreEqual(expectedSkip, request.Value.Skip);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-4, 1)]
    [DataRow(int.MaxValue, 1_000_000)]
    public void From_KeepsThePageInRange(int page, int expectedPage)
    {
        var request = PageRequest.From(page, pageSize: null)!.Value;

        Assert.AreEqual(expectedPage, request.Page);
        Assert.IsTrue(request.Skip >= 0, "A huge page number must not overflow the offset.");
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-1, 1)]
    [DataRow(101, 100)]
    [DataRow(5000, 100)]
    public void From_KeepsThePageSizeBetweenOneAndOneHundred(int pageSize, int expected)
    {
        Assert.AreEqual(expected, PageRequest.From(1, pageSize)!.Value.PageSize);
    }

    [TestMethod]
    [DataRow(0, 100, 0)]
    [DataRow(1, 100, 1)]
    [DataRow(100, 100, 1)]
    [DataRow(101, 100, 2)]
    [DataRow(2341, 100, 24)]
    public void PageCount_RoundsUp(int total, int pageSize, int expected)
    {
        Assert.AreEqual(expected, PageRequest.PageCount(total, pageSize));
    }
}
