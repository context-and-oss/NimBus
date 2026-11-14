using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>
/// Reads saved failure classifications without a provider (spec 043 read-only mode). Registered
/// whenever classification storage is readable, so saved results stay visible to the WebApp, the
/// classification GET routes and the MCP read tool even when no provider is configured.
/// </summary>
public sealed class FailureClassificationReader
{
    // The count only: failure ids stay out of logs, as they stay out of telemetry.
    private static readonly Action<ILogger, int, Exception?> BulkReadUnavailableLog =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(1, "BulkReadUnavailable"),
            "Saved failure classifications could not be read in bulk ({FailureCount} failures); Insights shows them as unavailable.");

    private readonly IIntegrationIntelligenceHost _host;
    private readonly IFailureClassificationStore _store;
    private readonly IMessageTrackingStore _messages;
    private readonly ILogger<FailureClassificationReader>? _logger;

    /// <summary>Creates the reader without a logger; a failed bulk read then shows in telemetry only.</summary>
    public FailureClassificationReader(IIntegrationIntelligenceHost host, IFailureClassificationStore store, IMessageTrackingStore messages)
        : this(host, store, messages, null)
    {
    }

    /// <summary>Creates the reader. <paramref name="logger"/> receives the exception behind a failed bulk read.</summary>
    public FailureClassificationReader(IIntegrationIntelligenceHost host, IFailureClassificationStore store, IMessageTrackingStore messages,
        ILogger<FailureClassificationReader>? logger)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(messages);
        _host = host;
        _store = store;
        _messages = messages;
        _logger = logger;
    }

    /// <summary>
    /// Loads the latest classification of one failed attempt after checking that its source exists
    /// and the caller has Reader on its endpoint. Answers 409 while a newer analysis is running or
    /// its outcome is unknown, and 503 when the latest analysis failed.
    /// </summary>
    public async Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
    {
        var input = await LoadAuthorizedMessageAsync(eventId, messageId, requireContributor: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        var operation = await _store.GetLatestOperationAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
        if (operation is { Status: "Active" }) throw new ClassificationServiceException("AnalysisInProgress", 409);
        if (operation is { Status: "Failed", ErrorCode: "AnalysisOutcomeUnknown" }) throw new ClassificationServiceException("AnalysisOutcomeUnknown", 409);
        if (operation is { Status: "Failed" }) throw new ClassificationServiceException(operation.ErrorCode ?? "AnalysisFailed", 503);
        return await _store.GetLatestAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads all completed revisions of one failed attempt, newest first, after the same checks.</summary>
    public async Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string eventId, string messageId, CancellationToken cancellationToken)
    {
        var input = await LoadAuthorizedMessageAsync(eventId, messageId, requireContributor: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _store.GetHistoryAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the saved state of many failed attempts in bounded batches with bounded concurrency,
    /// never one failure per round trip. It applies no per-endpoint authorization: the caller must
    /// already hold a role that covers every listed failure (Insights requires site Reader).
    /// Storage errors are logged and return <see cref="ClassificationBulkReadStatus.Unavailable"/>
    /// with no snapshots; cancellation propagates.
    /// </summary>
    public async Task<ClassificationBulkReadResult> ReadLatestInBulkAsync(
        IReadOnlyCollection<string> failureMessageIds, ClassificationBulkReadOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failureMessageIds);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (_store is not IFailureClassificationBulkReader bulk)
        {
            NimBusIntelligenceTelemetry.RecordBulkRead("unsupported", 0, null);
            return new ClassificationBulkReadResult(ClassificationBulkReadStatus.Unsupported, Empty);
        }

        var ids = failureMessageIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return new ClassificationBulkReadResult(ClassificationBulkReadStatus.Available, Empty);

        var started = Stopwatch.GetTimestamp();
        var batches = ids.Chunk(options.BatchSize).ToArray();
        var results = new IReadOnlyDictionary<string, ClassificationSnapshot>[batches.Length];
        using var gate = new SemaphoreSlim(options.MaxConcurrency);
        try
        {
            await Task.WhenAll(batches.Select(async (batch, index) =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { results[index] = await bulk.ReadLatestAsync(batch, cancellationToken).ConfigureAwait(false); }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // The caller only learns "unavailable". The reason (a missing table or container, a
            // refused login, a timeout) is recorded here for whoever has to fix it.
            if (_logger is not null) BulkReadUnavailableLog(_logger, ids.Length, exception);
            NimBusIntelligenceTelemetry.RecordBulkRead("unavailable", ids.Length, Stopwatch.GetElapsedTime(started));
            return new ClassificationBulkReadResult(ClassificationBulkReadStatus.Unavailable, Empty);
        }

        var snapshots = new Dictionary<string, ClassificationSnapshot>(ids.Length, StringComparer.Ordinal);
        foreach (var batch in results)
            foreach (var (id, snapshot) in batch) snapshots[id] = snapshot;
        NimBusIntelligenceTelemetry.RecordBulkRead("available", ids.Length, Stopwatch.GetElapsedTime(started));
        return new ClassificationBulkReadResult(ClassificationBulkReadStatus.Available, snapshots);
    }

    /// <summary>Loads one failed attempt and checks that its source exists and the caller may read (or analyze) it.</summary>
    internal async Task<MessageEntity> LoadAuthorizedMessageAsync(string eventId, string messageId, bool requireContributor,
        Action<MessageEntity>? loadedMessage = null, CancellationToken cancellationToken = default)
    {
        var input = await _messages.GetMessage(eventId, messageId).ConfigureAwait(false);
        if (input is null)
        {
            throw new ClassificationServiceException("FailureNotFound", 404);
        }
        loadedMessage?.Invoke(input);

        if (!await _host.EndpointExistsAsync(input.EndpointId, cancellationToken).ConfigureAwait(false)
            || await _messages.GetEvent(input.EndpointId, eventId).ConfigureAwait(false) is null)
        {
            throw new ClassificationServiceException("FailureNotFound", 404);
        }

        var authorized = requireContributor
            ? await _host.HasContributorAsync(input.EndpointId, cancellationToken).ConfigureAwait(false)
            : await _host.HasReaderAsync(input.EndpointId, cancellationToken).ConfigureAwait(false);
        if (!authorized)
        {
            throw new ClassificationServiceException("Forbidden", 403);
        }

        return input;
    }

    private static readonly IReadOnlyDictionary<string, ClassificationSnapshot> Empty =
        new Dictionary<string, ClassificationSnapshot>(StringComparer.Ordinal);
}
