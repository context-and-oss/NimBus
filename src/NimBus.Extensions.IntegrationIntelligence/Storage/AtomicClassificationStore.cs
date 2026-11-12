using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace NimBus.Extensions.IntegrationIntelligence.Storage;

/// <summary>One conditional durable write commits each failure's state transition and result.</summary>
public abstract class AtomicClassificationStore(TimeProvider? clock = null) : IFailureClassificationStore, IClassificationRetentionStore, IFailureClassificationBulkReader
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    protected abstract Task<(ClassificationDocument Document, string? Version)> ReadAsync(string failureId, CancellationToken ct);
    protected abstract Task<bool> TryWriteAsync(ClassificationDocument document, string? version, CancellationToken ct);
    protected abstract IAsyncEnumerable<ClassificationDocument> EnumerateAsync(CancellationToken ct);

    /// <summary>
    /// Reads the stored documents of the listed failures, omitting ids with no stored state, and
    /// throws when storage is unavailable. The default reads one document at a time; durable stores
    /// override it with a single round trip.
    /// </summary>
    protected virtual async Task<IReadOnlyList<ClassificationDocument>> ReadManyAsync(IReadOnlyList<string> failureIds, CancellationToken ct)
    {
        var documents = new List<ClassificationDocument>(failureIds.Count);
        foreach (var id in failureIds)
        {
            var (document, version) = await ReadAsync(id, ct).ConfigureAwait(false);
            if (version is not null) documents.Add(document);
        }
        return documents;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ClassificationSnapshot>> ReadLatestAsync(
        IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failureMessageIds);
        var ids = failureMessageIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return new Dictionary<string, ClassificationSnapshot>(StringComparer.Ordinal);

        var now = _clock.GetUtcNow();
        var snapshots = new Dictionary<string, ClassificationSnapshot>(ids.Length, StringComparer.Ordinal);
        foreach (var document in await ReadManyAsync(ids, cancellationToken).ConfigureAwait(false))
        {
            var operation = Normalize(document.Operations.LastOrDefault(), now);
            snapshots[document.FailureMessageId] = new ClassificationSnapshot(
                document.FailureMessageId,
                document.Deleted ? null : document.Operations.LastOrDefault(o => o.Result is not null)?.Result,
                operation?.Status,
                operation?.ErrorCode,
                document.Deleted);
        }
        return snapshots;
    }

    public async Task<FailureClassification?> GetLatestAsync(string failureMessageId, CancellationToken cancellationToken = default)
        => (await ReadAsync(failureMessageId, cancellationToken).ConfigureAwait(false)).Document.Operations.LastOrDefault(o => o.Result is not null)?.Result;

    public async Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string failureMessageId, CancellationToken cancellationToken = default)
        => (await ReadAsync(failureMessageId, cancellationToken).ConfigureAwait(false)).Document.Operations
            .Where(o => o.Result is not null).Reverse().Select(o => o.Result!).ToArray();

    public async Task<ClassificationOperation?> GetLatestOperationAsync(string failureMessageId, CancellationToken cancellationToken = default)
    {
        var document = (await ReadAsync(failureMessageId, cancellationToken).ConfigureAwait(false)).Document;
        if (document.Deleted) throw new ClassificationServiceException("FailureDeleted", 404);
        return Normalize(document.Operations.LastOrDefault(), _clock.GetUtcNow());
    }

    public Task<ClassificationReservation> ReserveAsync(string failureMessageId, string idempotencyKey, bool force, DateTimeOffset now,
        ClassificationScope? scope = null, CancellationToken cancellationToken = default)
        => MutateAsync(failureMessageId, document =>
        {
            if (document.Deleted) throw new ClassificationServiceException("FailureDeleted", 404);
            if (document.Scope is not null && scope is not null && document.Scope != scope)
                throw new ClassificationServiceException("FailureIdentityConflict", 409);
            document.Scope ??= scope;
            if (document.Requests.TryGetValue(idempotencyKey, out var request))
            {
                if (request.Force != force) throw new ClassificationServiceException("IdempotencyConflict", 409);
                var original = document.Operations.Find(o => o.OperationId == request.OperationId)!;
                return Reservation(Normalize(original, now)!, false);
            }
            var latest = Normalize(document.Operations.LastOrDefault(), now);
            ClassificationReservation answer;
            if (latest is not null && (latest.Status == "Active" || (!force && (latest.Status == "Completed" || latest.ErrorCode == "AnalysisOutcomeUnknown"))))
                answer = Reservation(latest, false);
            else
            {
                var operation = new ClassificationOperation(Guid.NewGuid().ToString("D"), failureMessageId, idempotencyKey,
                    (latest?.Revision ?? 0) + 1, force, "Active", now.AddSeconds(60));
                document.Operations.Add(operation);
                answer = Reservation(operation, true);
            }
            document.Requests.Add(idempotencyKey, new ClassificationRequestRecord(answer.OperationId, force));
            return answer;
        }, cancellationToken);

    public async Task CompleteAsync(string operationId, FailureClassification result, CancellationToken cancellationToken = default)
    {
        await MutateAsync(result.FailureMessageId, document =>
        {
            var index = document.Operations.FindIndex(o => o.OperationId == operationId);
            if (document.Deleted || index < 0) throw LostOwner();
            var operation = document.Operations[index];
            if (operation.Status == "Completed")
            {
                if (JsonConvert.SerializeObject(operation.Result) != JsonConvert.SerializeObject(result)) throw LostOwner();
                return true;
            }
            if (index != document.Operations.Count - 1 || operation.Status != "Active"
                || operation.ExpiresAtUtc <= _clock.GetUtcNow() || operation.Revision != result.Revision) throw LostOwner();
            document.Operations[index] = operation with { Status = "Completed", Result = result };
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(ClassificationReservation reservation, string errorCode, CancellationToken cancellationToken = default)
    {
        await MutateAsync(reservation.FailureMessageId, document =>
        {
            var operation = document.Operations.LastOrDefault();
            if (!document.Deleted && operation is { Status: "Active" } && operation.OperationId == reservation.OperationId)
                document.Operations[^1] = operation with { Status = "Failed", ErrorCode = errorCode };
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ClassificationScope> GetRetentionCandidatesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var document in EnumerateAsync(cancellationToken).ConfigureAwait(false))
            if (!document.Deleted && document.Scope is not null) yield return document.Scope;
    }

    public async Task DeleteFailureAsync(string failureMessageId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(failureMessageId, document =>
        {
            document.Deleted = true;
            document.Scope = null;
            document.Operations.Clear();
            document.Requests.Clear();
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> MutateAsync<T>(string failureId, Func<ClassificationDocument, T> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (document, version) = await ReadAsync(failureId, ct).ConfigureAwait(false);
            var result = change(document);
            // Reject admission before Cosmos' 2 MB limit; never discard history or request identities.
            if (System.Text.Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(document)) > 1_500_000)
                throw new ClassificationServiceException("ClassificationCapacityExceeded", 503);
            if (await TryWriteAsync(document, version, ct).ConfigureAwait(false)) return result;
        }
        throw new ClassificationServiceException("ClassificationStoreBusy", 503);
    }

    private static ClassificationServiceException LostOwner() => new("ReservationLost", 409);
    private static ClassificationOperation? Normalize(ClassificationOperation? operation, DateTimeOffset now)
        => operation is { Status: "Active" } && operation.ExpiresAtUtc <= now
            ? operation with { Status = "Failed", ErrorCode = "AnalysisOutcomeUnknown" } : operation;
    private static ClassificationReservation Reservation(ClassificationOperation operation, bool owner)
        => new(operation.OperationId, operation.FailureMessageId, operation.Revision, owner, operation.Result,
            operation.Status == "Active" && !owner ? "AnalysisInProgress" : operation.ErrorCode);
}

/// <summary>Atomic per-failure state. The operation id is the unguessable ownership token.</summary>
public sealed class ClassificationDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = "state";
    [JsonProperty("failureMessageId")]
    public string FailureMessageId { get; set; } = string.Empty;
    public ClassificationScope? Scope { get; set; }
    public bool Deleted { get; set; }
    public List<ClassificationOperation> Operations { get; set; } = [];
    public Dictionary<string, ClassificationRequestRecord> Requests { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Durable binding of a request key to its original operation and force semantics.</summary>
public sealed record ClassificationRequestRecord(string OperationId, bool Force);
