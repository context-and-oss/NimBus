#pragma warning disable CA1707, CA2007
extern alias CrmApi;
extern alias ErpApi;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CrmDbContext = CrmApi::Crm.Api.CrmDbContext;
using ErpDbContext = ErpApi::Erp.Api.ErpDbContext;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// Regression: with 21,000 contacts on the deployed demo a page of 100 still took over a
/// second, because every request sorted the whole table. Each paged list needs an index on
/// its sort order, in the model for a new database and in the start-up upgrade script for an
/// existing one.
/// </summary>
[TestClass]
public sealed class ListSortIndexTests
{
    [TestMethod]
    [DataRow("Accounts")]
    [DataRow("Contacts")]
    public void CrmList_HasAnIndexOnItsSortOrder(string table)
    {
        using var db = new CrmDbContext(new DbContextOptionsBuilder<CrmDbContext>().UseSqlServer("Server=unused;Database=unused").Options);

        AssertSortIndex(db.GetService<IDesignTimeModel>().Model, table, CrmDbContext.ListIndexUpgradeSql);
    }

    [TestMethod]
    [DataRow("Customers")]
    [DataRow("Contacts")]
    public void ErpList_HasAnIndexOnItsSortOrder(string table)
    {
        using var db = new ErpDbContext(new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer("Server=unused;Database=unused").Options);

        AssertSortIndex(db.GetService<IDesignTimeModel>().Model, table, ErpDbContext.ListIndexUpgradeSql);
    }

    private static void AssertSortIndex(IModel model, string table, string upgradeSql)
    {
        var entity = model.GetEntityTypes().Single(e => e.GetTableName() == table);
        var index = entity.GetIndexes().SingleOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(["CreatedAt", "Id"]));

        Assert.IsNotNull(index, $"{table} needs an index on (CreatedAt, Id), the order its list is paged in.");
        CollectionAssert.AreEqual(new[] { true, false }, index.IsDescending?.ToList(), "Newest first, then Id.");

        // The model only reaches a database that is created from it. An existing database gets
        // the index from the upgrade script, which must create the same index under the same name.
        var name = index.GetDatabaseName();
        Assert.AreEqual($"IX_{table}_CreatedAt_Id", name);
        StringAssert.Contains(upgradeSql, $"CREATE INDEX {name} ON dbo.{table} (CreatedAt DESC, Id)", StringComparison.Ordinal);
        StringAssert.Contains(upgradeSql, $"name = '{name}'", StringComparison.Ordinal);
    }
}
