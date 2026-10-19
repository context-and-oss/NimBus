#pragma warning disable CA1707, CA2007
extern alias AppHost;

using Aspire.Hosting.Azure;
using Aspire.Hosting.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class AzureSqlPublishTests
{
    // Regression: the serverless free offer never auto-paused under the demo's constant
    // SQL traffic, used up its monthly allowance in days and paused every database.
    [TestMethod]
    public async Task Publish_puts_every_database_on_the_fixed_price_Basic_tier()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish"]);
        var sql = builder.Resources.OfType<AzureSqlServerResource>().Single();

        var bicep = sql.GetBicepTemplateString();

        foreach (var database in new[] { "crm", "erp", "nimbus" })
        {
            Assert.Contains($"resource {database} 'Microsoft.Sql/servers/databases@", bicep);
        }
        Assert.AreEqual(3, CountOf(bicep, "name: 'Basic'"), bicep);
        Assert.AreEqual(3, CountOf(bicep, "tier: 'Basic'"), bicep);
        Assert.DoesNotContain("useFreeLimit", bicep);
        Assert.DoesNotContain("freeLimitExhaustionBehavior", bicep);
        Assert.Contains("azureADOnlyAuthentication: true", bicep);
    }

    private static int CountOf(string text, string value) => text.Split(value).Length - 1;
}
