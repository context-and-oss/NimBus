#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class EndpointCountQueryTests
{
    private static readonly string[] CountedStatuses = ["Pending", "Deferred", "Failed", "DeadLettered", "Unsupported"];

    [TestMethod]
    public async Task Counts_filter_terminal_history_before_aggregation()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var store = new CosmosDbClient(adapter);
        await store.DownloadEndpointStateCount("counts-endpoint");
        var query = adapter.Container("counts-endpoint").Queries.Single();
        var predicate = Regex.Match(query.QueryText, @"c\.status\s+IN\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
        Assert.IsTrue(predicate.Success, "Counting must restrict statuses in the database, before GROUP BY.");
        var parameters = query.GetQueryParameters().ToDictionary(p => p.Name, p => p.Value);
        var statuses = predicate.Groups[1].Value.Split(',')
            .Select(value => value.Trim())
            .Select(value => value.StartsWith('@') ? (string)parameters[value] : value.Trim('\''))
            .ToArray();
        CollectionAssert.AreEquivalent(CountedStatuses, statuses);
        StringAssert.Contains(query.QueryText, "c.deleted", StringComparison.Ordinal);
        StringAssert.Contains(query.QueryText, "GROUP BY c.status", StringComparison.Ordinal);
    }
}
