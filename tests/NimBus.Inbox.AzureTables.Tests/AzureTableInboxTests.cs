#pragma warning disable CA1707, CA2007
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;
using NimBus.Testing.Conformance;

namespace NimBus.Inbox.AzureTables.Tests;

[TestClass]
public sealed class AzureTableInboxTests : InboxStoreConformanceTests
{
    private InterleavingTableClient _table = null!;
    private ServiceProvider _services = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        var connection = Environment.GetEnvironmentVariable("NIMBUS_TABLE_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            Assert.Inconclusive("Set NIMBUS_TABLE_TEST_CONNECTION to run the Azure Tables inbox tests.");

        _table = new InterleavingTableClient(connection, $"Inbox{Guid.NewGuid():N}");
        await _table.CreateAsync();
        _services = new ServiceCollection().AddNimBusAzureTableInbox(_table).BuildServiceProvider();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        _services?.Dispose();
        if (_table is not null)
            await _table.DeleteAsync();
    }

    protected override Task<IInboxStore> CreateStoreAsync() =>
        Task.FromResult(_services.GetRequiredKeyedService<IInboxStore>(InboxStore.AzureTable));

    [TestMethod]
    public async Task Missing_table_is_a_store_failure_before_dispatch()
    {
        var store = await CreateStoreAsync();
        await _table.DeleteAsync();
        _table = null!;

        await Assert.ThrowsExactlyAsync<RequestFailedException>(() => store.HasProcessedAsync("adapter", "message"));
    }

    [TestMethod]
    public async Task Overlapping_purges_remove_each_receipt_once_and_allow_it_to_be_recorded_again()
    {
        var store = await CreateStoreAsync();
        const string endpoint = "adapter/1?#";
        const string message = "customer/123#v2?";
        await store.RecordProcessedAsync(endpoint, message);
        var cutoff = DateTimeOffset.UtcNow.AddTicks(1);
        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.PurgeExpiredAsync(endpoint, cutoff)));
        Assert.AreEqual(1, counts.Sum());
        await store.RecordProcessedAsync(endpoint, message);
        Assert.AreEqual(0, await store.PurgeExpiredAsync(endpoint, cutoff));
        Assert.IsTrue(await store.HasProcessedAsync(endpoint, message));
    }

    [TestMethod]
    public async Task Stale_cleanup_preserves_a_receipt_recreated_between_query_and_delete()
    {
        var store = await CreateStoreAsync();
        await store.RecordProcessedAsync("endpoint", "message");
        var cutoff = DateTimeOffset.UtcNow;
        _table.BeforeNextTransaction = async () =>
        {
            Assert.AreEqual(1, await store.PurgeExpiredAsync("endpoint", cutoff));
            await store.RecordProcessedAsync("endpoint", "message");
        };

        Assert.AreEqual(0, await store.PurgeExpiredAsync("endpoint", cutoff));
        Assert.IsTrue(await store.HasProcessedAsync("endpoint", "message"));
        Assert.AreEqual(1, await store.PurgeExpiredAsync("endpoint", DateTimeOffset.MaxValue));
    }

    [TestMethod]
    public async Task Cleanup_leaves_excess_expired_receipts_for_the_next_pass()
    {
        var store = await CreateStoreAsync();
        foreach (var batch in Enumerable.Range(0, 1001).Chunk(20))
        {
            await Task.WhenAll(batch.Select(id => store.RecordProcessedAsync("busy", FormattableString.Invariant($"message-{id}"))));
        }

        Assert.AreEqual(1000, await store.PurgeExpiredAsync("busy", DateTimeOffset.MaxValue));
        Assert.AreEqual(1, await store.PurgeExpiredAsync("busy", DateTimeOffset.MaxValue));
        Assert.AreEqual(0, await store.PurgeExpiredAsync("busy", DateTimeOffset.MaxValue));
    }

    private sealed class InterleavingTableClient(string connection, string name) : TableClient(connection, name)
    {
        public Func<Task>? BeforeNextTransaction { get; set; }

        public override async Task<Response<IReadOnlyList<Response>>> SubmitTransactionAsync(
            IEnumerable<TableTransactionAction> transactionActions, CancellationToken cancellationToken = default)
        {
            var callback = BeforeNextTransaction;
            BeforeNextTransaction = null;
            if (callback is not null)
                await callback();
            return await base.SubmitTransactionAsync(transactionActions, cancellationToken);
        }
    }
}
