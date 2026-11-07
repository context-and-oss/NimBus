#pragma warning disable CA1707, CA2007
using System.Reflection;
using NimBus.Extensions.IntegrationIntelligence.Storage;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

/// <summary>Spec 043: reading saved classifications without a provider, one at a time and in bulk.</summary>
[TestClass]
public sealed class ClassificationReaderTests
{
    [TestMethod]
    public async Task Reader_Returns_The_Saved_Result_With_No_Provider_And_Keeps_Single_Read_Semantics()
    {
        var store = new InMemoryClassificationStore();
        var reservation = await store.ReserveAsync("failure", Guid.NewGuid().ToString("D"), false, DateTimeOffset.UtcNow,
            new ClassificationScope("failure", "event", "endpoint", null));
        await store.CompleteAsync(reservation.OperationId, DurableStoreConformanceTests.Result("failure", reservation.Revision));
        var reader = new FailureClassificationReader(new TestIntelligenceHost(), store, Messages());

        Assert.AreEqual(1, (await reader.GetLatestAsync("event", "failure", default))!.Revision);
        Assert.HasCount(1, await reader.GetHistoryAsync("event", "failure", default));

        // The single-failure GET keeps answering 409 while a newer analysis runs (spec 043 decision 4).
        await store.ReserveAsync("failure", Guid.NewGuid().ToString("D"), true, DateTimeOffset.UtcNow,
            new ClassificationScope("failure", "event", "endpoint", null));
        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => reader.GetLatestAsync("event", "failure", default));
        Assert.AreEqual("AnalysisInProgress", error.Code);
        Assert.AreEqual(409, error.StatusCode);
    }

    [TestMethod]
    public async Task Reader_Denies_A_Caller_Without_Endpoint_Reader()
    {
        var host = new TestIntelligenceHost { Reader = false };
        var reader = new FailureClassificationReader(host, new InMemoryClassificationStore(), Messages());

        var error = await Assert.ThrowsExactlyAsync<ClassificationServiceException>(() => reader.GetLatestAsync("event", "failure", default));
        Assert.AreEqual(403, error.StatusCode);
    }

    [TestMethod]
    public async Task Bulk_Read_Splits_Into_Bounded_Batches_With_Bounded_Concurrency_And_Never_Reads_One_At_A_Time()
    {
        var store = new RecordingBulkStore();
        var reader = new FailureClassificationReader(new TestIntelligenceHost(), store, Messages());
        var ids = Enumerable.Range(0, 1_200).Select(i => $"failure-{i}").ToList();

        var result = await reader.ReadLatestInBulkAsync([.. ids, .. ids.Take(10)], new ClassificationBulkReadOptions(BatchSize: 500, MaxConcurrency: 2), default);

        Assert.AreEqual(ClassificationBulkReadStatus.Available, result.Status);
        CollectionAssert.AreEquivalent(new[] { 500, 500, 200 }, store.BatchSizes.ToArray(), "Duplicates are collapsed before batching.");
        Assert.IsLessThanOrEqualTo(2, store.MaxInFlight);
        Assert.AreEqual(0, store.SingleReads);
    }

    [TestMethod]
    public async Task Bulk_Read_Of_A_Store_Without_Bulk_Support_Is_Unsupported()
    {
        var reader = new FailureClassificationReader(new TestIntelligenceHost(), new SingleReadOnlyStore(), Messages());

        var result = await reader.ReadLatestInBulkAsync(["failure"], new ClassificationBulkReadOptions(), default);

        Assert.AreEqual(ClassificationBulkReadStatus.Unsupported, result.Status);
    }

    [TestMethod]
    public async Task Bulk_Read_Storage_Failure_Is_Unavailable_And_Cancellation_Propagates()
    {
        var store = new RecordingBulkStore { Throw = true };
        var reader = new FailureClassificationReader(new TestIntelligenceHost(), store, Messages());

        var failed = await reader.ReadLatestInBulkAsync(["failure"], new ClassificationBulkReadOptions(), default);
        Assert.AreEqual(ClassificationBulkReadStatus.Unavailable, failed.Status);
        Assert.HasCount(0, failed.Snapshots);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        store.Throw = false;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            reader.ReadLatestInBulkAsync(["failure"], new ClassificationBulkReadOptions(), cancelled.Token));
    }

    [TestMethod]
    [DataRow(0, 4)]
    [DataRow(500, 0)]
    public void Bulk_Read_Options_Reject_Non_Positive_Limits(int batchSize, int concurrency)
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ClassificationBulkReadOptions(batchSize, concurrency));

    private static IMessageTrackingStore Messages() => DispatchProxy.Create<IMessageTrackingStore, MessageSource>();

    private sealed class RecordingBulkStore : IFailureClassificationStore, IFailureClassificationBulkReader
    {
        private readonly object _gate = new();
        private int _inFlight;
        public List<int> BatchSizes { get; } = [];
        public int MaxInFlight { get; private set; }
        public int SingleReads { get; private set; }
        public bool Throw { get; set; }

        public async Task<IReadOnlyDictionary<string, ClassificationSnapshot>> ReadLatestAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Throw) throw new InvalidOperationException("storage down");
            lock (_gate)
            {
                BatchSizes.Add(failureMessageIds.Count);
                MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
            }
            await Task.Delay(20, cancellationToken);
            lock (_gate) _inFlight--;
            return new Dictionary<string, ClassificationSnapshot>();
        }

        public Task<FailureClassification?> GetLatestAsync(string failureMessageId, CancellationToken cancellationToken = default)
        {
            SingleReads++;
            return Task.FromResult<FailureClassification?>(null);
        }

        public Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClassificationOperation?> GetLatestOperationAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClassificationReservation> ReserveAsync(string failureMessageId, string idempotencyKey, bool force, DateTimeOffset now, ClassificationScope? scope = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(string operationId, FailureClassification result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailAsync(ClassificationReservation reservation, string errorCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SingleReadOnlyStore : IFailureClassificationStore
    {
        public Task<FailureClassification?> GetLatestAsync(string failureMessageId, CancellationToken cancellationToken = default) => Task.FromResult<FailureClassification?>(null);
        public Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClassificationOperation?> GetLatestOperationAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClassificationReservation> ReserveAsync(string failureMessageId, string idempotencyKey, bool force, DateTimeOffset now, ClassificationScope? scope = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteAsync(string operationId, FailureClassification result, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailAsync(ClassificationReservation reservation, string errorCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
