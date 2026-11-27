using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox;

/// <summary>
/// Dispatches pending outbox messages to the real message sender.
/// This is the core polling logic; wrap in a hosted service for background execution.
/// </summary>
public partial class OutboxDispatcher
{
    private static readonly TimeSpan DefaultCompensatingCheckpointTimeout = TimeSpan.FromSeconds(5);
    private readonly IOutbox _outbox;
    private readonly ISender _sender;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly TimeSpan _compensatingCheckpointTimeout;
    private readonly OutboxDispatcherOptions _options;
    private readonly string _dispatcherId;
    private int _reportedUnclaimedStore;

    /// <summary>
    /// Initializes an outbox dispatcher with the default <see cref="OutboxDispatcherOptions"/>.
    /// </summary>
    /// <param name="outbox">The transactional outbox store.</param>
    /// <param name="sender">The sender used to dispatch stored messages.</param>
    /// <param name="logger">Optional dispatcher logger.</param>
    public OutboxDispatcher(IOutbox outbox, ISender sender, ILogger<OutboxDispatcher>? logger = null)
        : this(outbox, sender, DefaultCompensatingCheckpointTimeout, logger)
    {
    }

    /// <summary>
    /// Initializes an outbox dispatcher with a bounded timeout for the best-effort
    /// checkpoint performed after cooperative cancellation.
    /// </summary>
    /// <param name="outbox">The transactional outbox store.</param>
    /// <param name="sender">The sender used to dispatch stored messages.</param>
    /// <param name="compensatingCheckpointTimeout">Maximum time to wait for the cancellation checkpoint.</param>
    /// <param name="logger">Optional dispatcher logger.</param>
    public OutboxDispatcher(
        IOutbox outbox,
        ISender sender,
        TimeSpan compensatingCheckpointTimeout,
        ILogger<OutboxDispatcher>? logger = null)
        : this(outbox, sender, new OutboxDispatcherOptions(), compensatingCheckpointTimeout, logger)
    {
    }

    /// <summary>
    /// Initializes an outbox dispatcher with explicit retry, parking and claiming settings.
    /// </summary>
    /// <param name="outbox">The transactional outbox store.</param>
    /// <param name="sender">The sender used to dispatch stored messages.</param>
    /// <param name="options">Retry, parking and claiming settings.</param>
    /// <param name="logger">Optional dispatcher logger.</param>
    public OutboxDispatcher(
        IOutbox outbox,
        ISender sender,
        OutboxDispatcherOptions options,
        ILogger<OutboxDispatcher>? logger = null)
        : this(outbox, sender, options, DefaultCompensatingCheckpointTimeout, logger)
    {
    }

    /// <summary>
    /// Initializes an outbox dispatcher with explicit retry, parking and claiming settings and a
    /// bounded timeout for the best-effort checkpoint performed after cooperative cancellation.
    /// </summary>
    /// <param name="outbox">The transactional outbox store.</param>
    /// <param name="sender">The sender used to dispatch stored messages.</param>
    /// <param name="options">Retry, parking and claiming settings.</param>
    /// <param name="compensatingCheckpointTimeout">Maximum time to wait for the cancellation checkpoint.</param>
    /// <param name="logger">Optional dispatcher logger.</param>
    public OutboxDispatcher(
        IOutbox outbox,
        ISender sender,
        OutboxDispatcherOptions options,
        TimeSpan compensatingCheckpointTimeout,
        ILogger<OutboxDispatcher>? logger = null)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(compensatingCheckpointTimeout, TimeSpan.Zero);

        _options = options;
        _dispatcherId = options.ResolveDispatcherId();
        _compensatingCheckpointTimeout = compensatingCheckpointTimeout;
        _logger = logger ?? NullLogger<OutboxDispatcher>.Instance;
    }

    /// <summary>
    /// The lease owner this dispatcher records when it claims rows from an
    /// <see cref="IOutboxClaiming"/> store.
    /// </summary>
    public string DispatcherId => _dispatcherId;

    /// <summary>
    /// Dispatches a batch of pending outbox messages.
    /// </summary>
    /// <param name="batchSize">Maximum number of messages to dispatch in one batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of messages dispatched.</returns>
    /// <remarks>
    /// When the outbox implements <see cref="IOutboxClaiming"/>, rows are leased to this
    /// dispatcher so several replicas can run side by side, a failed row is retried with
    /// exponential backoff and parked after <see cref="OutboxDispatcherOptions.MaxAttempts"/>,
    /// and a session blocked by a failed row does not take up room in later batches. Otherwise
    /// the dispatcher reads <see cref="IOutbox.GetPendingAsync"/> and retries a failed row on
    /// every poll; only one dispatcher instance may run against such an outbox.
    /// </remarks>
    public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        if (_outbox is IOutboxClaiming claiming)
            return await DispatchClaimedAsync(claiming, batchSize, cancellationToken);

        if (Interlocked.Exchange(ref _reportedUnclaimedStore, 1) == 0)
            LogOutboxStoreDoesNotSupportClaiming(_logger, _outbox.GetType().FullName);

        var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);
        if (pending.Count == 0)
            return 0;

        LogOutboxDispatchPollFoundPendingMessage(_logger, pending.Count);

        var dispatched = new List<string>();
        // NimBus's ordering guarantee is FIFO per session (ADR-001), not global.
        // Halt only the failing session within this poll. Rows are oldest-first,
        // so once a session's row fails, later rows for that same session stay
        // behind it and are retried next interval. Without IOutboxClaiming the
        // store cannot leave a blocked session out of the next batch, so a
        // session whose rows fill the whole batch still stalls the dispatcher.
        var failedSessions = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var outboxMessage in pending)
            {
                var sessionId = outboxMessage.SessionId;
                var hasSession = !string.IsNullOrEmpty(sessionId);

                if (hasSession && failedSessions.Contains(sessionId!))
                {
                    // A prior row on this session failed this poll; keep it strictly
                    // ordered behind its stuck row instead of dispatching out of order.
                    continue;
                }

                var failure = await DispatchOneAsync(outboxMessage, cancellationToken);
                if (failure is not null)
                {
                    LogOutboxDispatchFailedForMessage(_logger, failure.Exception, outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
                    // Block only this session; session-less rows fail independently.
                    // The failed message will be retried on the next poll.
                    if (hasSession)
                        failedSessions.Add(sessionId!);
                    continue;
                }

                dispatched.Add(outboxMessage.Id);
            }

            if (dispatched.Count > 0)
            {
                await _outbox.MarkAsDispatchedAsync(dispatched, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CompensateAfterCancellationAsync(dispatched, claiming: null, release: null);
            throw;
        }

        return dispatched.Count;
    }

    private async Task<int> DispatchClaimedAsync(IOutboxClaiming claiming, int batchSize, CancellationToken cancellationToken)
    {
        var claimStarted = Stopwatch.GetTimestamp();
        var claimed = await claiming.ClaimPendingAsync(batchSize, _dispatcherId, _options.LeaseDuration, cancellationToken);
        if (claimed.Count == 0)
            return 0;

        LogOutboxDispatchPollFoundPendingMessage(_logger, claimed.Count);

        // Stop starting sends once half of the lease is gone, so the lease cannot expire (and
        // another replica claim the rows) while this dispatcher is still sending them.
        var sendBudget = _options.LeaseDuration / 2;
        var dispatched = new List<string>();
        var failures = new List<(OutboxMessage Message, DispatchFailure Failure)>();
        var release = new List<string>();
        var budgetExhausted = 0;
        // FIFO per session (ADR-001): once a session's row fails, its later rows in this batch
        // are released unsent. The store keeps the whole session out of later batches while the
        // failed row waits for its retry or is parked, so it cannot crowd out other sessions.
        var failedSessions = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var outboxMessage in claimed)
            {
                var sessionId = outboxMessage.SessionId;
                var hasSession = !string.IsNullOrEmpty(sessionId);

                if (hasSession && failedSessions.Contains(sessionId!))
                {
                    release.Add(outboxMessage.Id);
                    continue;
                }

                if (Stopwatch.GetElapsedTime(claimStarted) >= sendBudget)
                {
                    release.Add(outboxMessage.Id);
                    budgetExhausted++;
                    continue;
                }

                var failure = await DispatchOneAsync(outboxMessage, cancellationToken);
                if (failure is not null)
                {
                    failures.Add((outboxMessage, failure));
                    if (hasSession)
                        failedSessions.Add(sessionId!);
                    continue;
                }

                dispatched.Add(outboxMessage.Id);
            }

            if (dispatched.Count > 0)
            {
                await _outbox.MarkAsDispatchedAsync(dispatched, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Release every claimed row that was not sent (failed rows included: a cancelled poll
            // does not count an attempt), so another replica can take them over at once.
            var sent = new HashSet<string>(dispatched, StringComparer.Ordinal);
            var unsent = new List<string>();
            foreach (var outboxMessage in claimed)
            {
                if (!sent.Contains(outboxMessage.Id))
                    unsent.Add(outboxMessage.Id);
            }

            await CompensateAfterCancellationAsync(dispatched, claiming, unsent);
            throw;
        }

        if (budgetExhausted > 0)
            LogOutboxLeaseBudgetExhausted(_logger, sendBudget, budgetExhausted);

        // Checkpointing sent rows came first; failure records and releases are best-effort,
        // because a lease that is not cleared simply expires and the row is claimed again.
        foreach (var (outboxMessage, failure) in failures)
        {
            await RecordFailureAsync(claiming, outboxMessage, failure, cancellationToken);
        }

        if (release.Count > 0)
        {
            try
            {
                await claiming.ReleaseClaimsAsync(_dispatcherId, release, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogCouldNotReleaseOutboxClaims(_logger, ex, release.Count);
            }
        }

        return dispatched.Count;
    }

    private async Task RecordFailureAsync(
        IOutboxClaiming claiming,
        OutboxMessage outboxMessage,
        DispatchFailure failure,
        CancellationToken cancellationToken)
    {
        var attempt = outboxMessage.AttemptCount + 1;
        var park = failure.IsPoison || attempt >= _options.MaxAttempts;
        var retryDelay = park ? TimeSpan.Zero : _options.GetRetryDelay(attempt);
        var errorType = failure.Exception.GetType().FullName;
        var error = $"{errorType}: {failure.Exception.Message}";

        if (park)
        {
            LogOutboxMessageParked(_logger, failure.Exception, outboxMessage.Id, attempt, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
        }
        else
        {
            LogOutboxDispatchAttemptFailed(_logger, failure.Exception, attempt, _options.MaxAttempts, outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId, retryDelay);
        }

        try
        {
            await claiming.RecordDispatchFailureAsync(outboxMessage.Id, _dispatcherId, error, retryDelay, park, cancellationToken);
            if (park)
            {
                NimBusMeters.OutboxParked.Add(1, BuildDispatchTags(outboxMessage.To, "parked", errorType));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCouldNotRecordOutboxDispatchFailure(_logger, ex, outboxMessage.Id);
        }
    }

    private async Task CompensateAfterCancellationAsync(
        List<string> dispatched,
        IOutboxClaiming? claiming,
        List<string>? release)
    {
        // Sending and checkpointing are separate operations. Complete the
        // idempotent checkpoint without the canceled polling token, whether
        // cancellation interrupted sending or a partially-applied checkpoint.
        if (dispatched.Count > 0)
        {
            Task? checkpointTask = null;
            try
            {
                checkpointTask = _outbox.MarkAsDispatchedAsync(dispatched, CancellationToken.None);
                await checkpointTask.WaitAsync(_compensatingCheckpointTimeout, CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (ex is TimeoutException && checkpointTask is not null)
                {
                    _ = ObserveCompensatingCheckpointAsync(checkpointTask, dispatched.Count);
                }

                // The caller's cancellation remains the primary outcome. The
                // pending rows may be sent again under the outbox's at-least-once
                // delivery contract, but shutdown must not be reported as a
                // dispatch failure because bookkeeping also became unavailable.
                LogCouldNotCheckpointOutboxMessage(_logger, ex, dispatched.Count);
            }
        }

        if (claiming is not null && release is { Count: > 0 })
        {
            try
            {
                await claiming.ReleaseClaimsAsync(_dispatcherId, release, CancellationToken.None)
                    .WaitAsync(_compensatingCheckpointTimeout, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Unreleased leases expire on their own; shutdown only delays them.
                LogCouldNotReleaseOutboxClaims(_logger, ex, release.Count);
            }
        }
    }

    private async Task ObserveCompensatingCheckpointAsync(Task checkpointTask, int dispatchedCount)
    {
        try
        {
            await checkpointTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogOutboxCheckpointCompletedWithAnError(_logger, ex, dispatchedCount);
        }
    }

    private sealed record DispatchFailure(Exception Exception, bool IsPoison);

    private async Task<DispatchFailure?> DispatchOneAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken)
    {
        // Detach from the polling-loop activity so the dispatch span is a root that
        // links (rather than nests under) the original publish context.
        var savedCurrent = Activity.Current;
        Activity.Current = null;

        var startTimestamp = Stopwatch.GetTimestamp();
        Message? message = null;
        // Endpoint comes from the persisted column so dispatch metrics are
        // tagged even when payload deserialization fails on a malformed row.
        var endpoint = outboxMessage.To;
        Activity? activity = null;
        var payloadUnreadable = false;

        try
        {
            ActivityLink? link = TryBuildLink(outboxMessage.TraceParent, outboxMessage.TraceState);
            // FR-015: messaging spans use "{operation.type} {destination.name}".
            var spanName = string.IsNullOrEmpty(endpoint) ? "publish" : $"publish {endpoint}";
            activity = NimBusActivitySources.Outbox.StartActivity(
                spanName,
                ActivityKind.Producer,
                parentContext: default,
                tags: null,
                links: link.HasValue ? new[] { link.Value } : null);

            if (activity is not null)
            {
                if (!link.HasValue)
                    activity.AddEvent(new ActivityEvent("nimbus.outbox.orphan_row"));
                // FR-020: messaging spans MUST set messaging.system /
                // messaging.operation.type / messaging.destination.name.
                // System is unknown at this layer (NimBus.Core is transport-
                // agnostic) — set what we know; transport wrappers may add it.
                activity.SetTag(MessagingAttributes.OperationType, "publish");
                if (!string.IsNullOrEmpty(endpoint))
                {
                    activity.SetTag(MessagingAttributes.DestinationName, endpoint);
                    activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
                }
                if (!string.IsNullOrEmpty(outboxMessage.EventTypeId))
                    activity.SetTag(MessagingAttributes.NimBusEventType, outboxMessage.EventTypeId);
                if (!string.IsNullOrEmpty(outboxMessage.MessageId))
                    activity.SetTag(MessagingAttributes.MessageId, outboxMessage.MessageId);
                if (!string.IsNullOrEmpty(outboxMessage.CorrelationId))
                    activity.SetTag(MessagingAttributes.MessageConversationId, outboxMessage.CorrelationId);
            }

            try
            {
                message = JsonConvert.DeserializeObject<Message>(
                    outboxMessage.Payload,
                    Constants.CreateSafeJsonSettings())
                    ?? throw new JsonSerializationException("The outbox payload deserialized to null.");
            }
            catch (JsonException)
            {
                // A payload that cannot be read fails the same way on every attempt.
                payloadUnreadable = true;
                throw;
            }

            if (outboxMessage.ScheduledEnqueueTimeUtc.HasValue)
            {
                await _sender.ScheduleMessage(message, new DateTimeOffset(outboxMessage.ScheduledEnqueueTimeUtc.Value, TimeSpan.Zero), cancellationToken);
            }
            else
            {
                await _sender.Send(message, outboxMessage.EnqueueDelayMinutes, cancellationToken);
            }

            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var tags = BuildDispatchTags(endpoint, "dispatched", errorType: null);
            NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
            NimBusMeters.OutboxDispatched.Add(1, tags);
            activity?.SetStatus(ActivityStatusCode.Ok);

            LogOutboxDispatchedMessage(_logger, outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A stopped polling loop is not a failed outbox dispatch. Propagate
            // cancellation without failure metrics/logging so the row remains
            // pending for the next active dispatcher.
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var errorType = ex.GetType().FullName;
            var tags = BuildDispatchTags(endpoint, "failed", errorType);
            NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
            NimBusMeters.OutboxDispatched.Add(1, tags);
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.SetTag(MessagingAttributes.ErrorType, errorType);
                activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                {
                    { "exception.type", errorType },
                    { "exception.message", ex.Message },
                    { "exception.stacktrace", ex.ToString() },
                }));
            }

            return new DispatchFailure(ex, payloadUnreadable);
        }
        finally
        {
            activity?.Dispose();
            Activity.Current = savedCurrent;
        }
    }

    private static ActivityLink? TryBuildLink(string? traceParent, string? traceState)
    {
        var context = W3CMessagePropagator.TryParse(traceParent, traceState);
        return context == default ? (ActivityLink?)null : new ActivityLink(context);
    }

    private static KeyValuePair<string, object?>[] BuildDispatchTags(string? endpoint, string outcome, string? errorType)
    {
        var tags = new List<KeyValuePair<string, object?>>(3)
        {
            new(MessagingAttributes.NimBusOutcome, outcome),
        };
        if (!string.IsNullOrEmpty(endpoint))
            tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
        if (!string.IsNullOrEmpty(errorType))
            tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType));
        return tags.ToArray();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox dispatch poll found {PendingCount} pending message(s)")]
    private static partial void LogOutboxDispatchPollFoundPendingMessage(ILogger logger, int pendingCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not checkpoint {DispatchedCount} outbox message(s) sent before cancellation")]
    private static partial void LogCouldNotCheckpointOutboxMessage(ILogger logger, Exception exception, int dispatchedCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox checkpoint completed with an error after shutdown stopped waiting for {DispatchedCount} message(s)")]
    private static partial void LogOutboxCheckpointCompletedWithAnError(ILogger logger, Exception exception, int dispatchedCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbox dispatched message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId})")]
    private static partial void LogOutboxDispatchedMessage(ILogger logger, string outboxId, string? eventTypeId, string? sessionId, string? messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatch failed for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId}). Halting this poll; will retry next interval.")]
    private static partial void LogOutboxDispatchFailedForMessage(ILogger logger, Exception exception, string outboxId, string? eventTypeId, string? sessionId, string? messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox store {OutboxType} does not implement IOutboxClaiming: failed rows are retried every poll without backoff or parking, and only one dispatcher instance may run against it.")]
    private static partial void LogOutboxStoreDoesNotSupportClaiming(ILogger logger, string? outboxType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatch attempt {Attempt} of {MaxAttempts} failed for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId}); retrying in {RetryDelay}. Later messages of the session wait behind it.")]
    private static partial void LogOutboxDispatchAttemptFailed(ILogger logger, Exception exception, int attempt, int maxAttempts, string outboxId, string? eventTypeId, string? sessionId, string? messageId, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {OutboxId} parked after {Attempt} failed attempt(s) (event {EventTypeId}, session {SessionId}, messageId {MessageId}). It is no longer dispatched, and later messages of its session wait until it is requeued.")]
    private static partial void LogOutboxMessageParked(ILogger logger, Exception exception, string outboxId, int attempt, string? eventTypeId, string? sessionId, string? messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the failed dispatch of outbox message {OutboxId}; it is retried once its lease expires.")]
    private static partial void LogCouldNotRecordOutboxDispatchFailure(ILogger logger, Exception exception, string outboxId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release {Count} claimed outbox message(s); they become available when their lease expires.")]
    private static partial void LogCouldNotReleaseOutboxClaims(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatcher used half of its lease ({SendBudget}) and released {Count} unsent message(s); consider a smaller batch size or a longer lease.")]
    private static partial void LogOutboxLeaseBudgetExhausted(ILogger logger, TimeSpan sendBudget, int count);

}
