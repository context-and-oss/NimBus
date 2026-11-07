namespace NimBus.Extensions.IntegrationIntelligence.Storage;

/// <summary>Fail-closed sentinel: never substitutes volatile storage for a durable configuration.</summary>
internal sealed class UnavailableClassificationStore : IFailureClassificationStore, IFailureClassificationBulkReader
{
    private static ClassificationServiceException Unavailable() => new("ClassificationStorageNotConfigured", 503);
    public Task<IReadOnlyDictionary<string, ClassificationSnapshot>> ReadLatestAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<FailureClassification?> GetLatestAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ClassificationOperation?> GetLatestOperationAsync(string failureMessageId, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task<ClassificationReservation> ReserveAsync(string failureMessageId, string idempotencyKey, bool force, DateTimeOffset now, ClassificationScope? scope = null, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task CompleteAsync(string operationId, FailureClassification result, CancellationToken cancellationToken = default) => throw Unavailable();
    public Task FailAsync(ClassificationReservation reservation, string errorCode, CancellationToken cancellationToken = default) => throw Unavailable();
}
