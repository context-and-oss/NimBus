#pragma warning disable CA1707, CA2007
extern alias ErpApi;

using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ErpDbContext = ErpApi::Erp.Api.ErpDbContext;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// Regression: ERP customer numbers were <c>C-yyMMdd-</c> plus a random five-digit number, so
/// two customers created on the same day could draw the same number. The unique index on
/// <c>CustomerNumber</c> then failed the insert with a 500 (nightly e2e, 2026-11-16:
/// "Cannot insert duplicate key row ... IX_Customers_CustomerNumber"). Numbers now come from a
/// SQL Server sequence, which never repeats.
/// </summary>
[TestClass]
public sealed class CustomerNumberSequenceTests
{
    [TestMethod]
    public void Model_DeclaresTheCustomerNumberSequence()
    {
        using var db = NewContext();
        var sequence = db.GetService<IDesignTimeModel>().Model.FindSequence(ErpDbContext.CustomerNumberSequence, "dbo");

        Assert.IsNotNull(sequence, "A new database must get the customer-number sequence from the model.");
        Assert.AreEqual(typeof(long), sequence.Type);
        Assert.AreEqual(ErpDbContext.FirstCustomerNumber, sequence.StartValue);
    }

    [TestMethod]
    public void UpgradeSql_CreatesTheSameSequenceOnAnExistingDatabase()
    {
        var sql = ErpDbContext.CustomerNumberSequenceUpgradeSql;

        StringAssert.Contains(sql, $"OBJECT_ID(N'dbo.{ErpDbContext.CustomerNumberSequence}', N'SO') IS NULL", StringComparison.Ordinal);
        StringAssert.Contains(
            sql,
            $"CREATE SEQUENCE dbo.{ErpDbContext.CustomerNumberSequence} AS bigint START WITH {ErpDbContext.FirstCustomerNumber}",
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void FormatCustomerNumber_KeepsTheDatePrefixAndCannotMatchAnOldRandomNumber()
    {
        var number = ErpDbContext.FormatCustomerNumber(new DateTime(2026, 11, 16, 2, 39, 0, DateTimeKind.Utc), ErpDbContext.FirstCustomerNumber);

        Assert.AreEqual("C-261116-100000", number);
        // Numbers issued before the sequence end in five digits (10000-99998). Starting the
        // sequence at six digits keeps new numbers clear of every number already in a database.
        Assert.IsTrue(ErpDbContext.FirstCustomerNumber > 99999);
    }

    [TestMethod]
    public void NoErpCodePathDrawsARandomCustomerNumber()
    {
        var erpApi = Path.Combine(RepoRoot(), "samples", "CrmErpDemo", "Erp.Api");
        var offenders = Directory.EnumerateFiles(erpApi, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("CustomerNumber = $\"C-", StringComparison.Ordinal)
                || File.ReadAllText(f).Contains("? $\"C-", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(erpApi, f))
            .ToList();

        Assert.AreEqual(0, offenders.Count, $"Build customer numbers with ErpDbContext.NextCustomerNumberAsync: {string.Join(", ", offenders)}");
    }

    private static ErpDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer("Server=unused;Database=unused").Options);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "NimBus.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
