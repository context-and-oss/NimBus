using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    /// <remarks>
    /// <paramref name="logger"/> has no default value on purpose: with one, the existing call
    /// <c>new OutboxDispatcher(outbox, sender, null)</c> would be ambiguous between this
    /// constructor and the logger constructor. Pass <c>null</c> for no logging.
    /// </remarks>
    /// <param name="outbox">The transactional outbox store.</param>
    /// <param name="sender">The sender used to dispatch stored messages.</param>
    /// <param name="options">Retry, parking and claiming settings.</param>
    /// <param name="logger">Dispatcher logger, or <c>null</c>.</param>
    public OutboxDispatcher(
        IOutbox outbox,
        ISender sender,
        OutboxDispatcherOptions options,
        ILogger<OutboxDispatcher>? logger)
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
        // Started before the claim, so local elapsed time never trails the store's lease clock.
        var claimStarted = Stopwatch.GetTimestamp();
        var claimed = await claiming.ClaimPendingAsync(batchSize, _dispatcherId, _options.LeaseDuration, cancellationToken);
        if (claimed.Count == 0)
            return 0;

        LogOutboxDispatchPollFoundPendingMessage(_logger, claimed.Count);

        // Stop starting sends once half of the lease is gone, and cancel a send still running
        // when only the safety margin is left, so this dispatcher is done with a row before its
        // lease can expire and another replica claim it. Each checkpoint is also fenced on the
        // claim, the last line of defence against a paused process or a sender that ignores
        // cancellation.
        var poll = new ClaimedPoll(claiming, claimed, claimStarted, _options.LeaseDuration);
        try
        {
            if (_sender is IBatchingSender batching)
            {
                await DispatchSegmentsAsync(poll, batching, cancellationToken);
            }
            else
            {
                foreach (var outboxMessage in claimed)
                    await DispatchClaimedRowAsync(poll, outboxMessage, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Release every claimed row that was not sent (failed rows included: a cancelled poll
            // does not count an attempt), so another replica can take them over at once. Rows
            // that were sent but whose checkpoint was interrupted are checkpointed instead.
            var toCheckpoint = new List<string>(poll.AwaitingCheckpoint);
            var settled = new HashSet<string>(poll.Dispatched, StringComparer.Ordinal);
            settled.UnionWith(toCheckpoint);
            var unsent = new List<string>();
            foreach (var outboxMessage in claimed)
            {
                if (!settled.Contains(outboxMessage.Id))
                    unsent.Add(outboxMessage.Id);
            }

            await CompensateAfterCancellationAsync(toCheckpoint, claiming, unsent);
            throw;
        }

        if (poll.BudgetExhausted > 0)
            LogOutboxLeaseBudgetExhausted(_logger, poll.SendBudget, poll.BudgetExhausted);

        // Best-effort: a lease that is not released simply expires and the row is claimed again.
        if (poll.Release.Count > 0)
        {
            try
            {
                await claiming.ReleaseClaimsAsync(_dispatcherId, poll.Release, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogCouldNotReleaseOutboxClaims(_logger, ex, poll.Release.Count);
            }
        }

        return poll.Dispatched.Count;
    }

    // Sends one claimed row on its own, the path every row takes when the sender cannot batch.
    private async Task DispatchClaimedRowAsync(ClaimedPoll poll, OutboxMessage outboxMessage, CancellationToken cancellationToken)
    {
        if (poll.IsBlocked(outboxMessage))
        {
            poll.Release.Add(outboxMessage.Id);
            return;
        }

        var elapsed = poll.Elapsed;
        if (elapsed >= poll.SendBudget)
        {
            poll.ReleaseForBudget(outboxMessage);
            return;
        }

        var sendWindow = poll.SendWindow(elapsed);
        DispatchFailure? failure;
        using (var leaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            leaseTimeout.CancelAfter(sendWindow);
            failure = await DispatchOneAsync(outboxMessage, cancellationToken, leaseTimeout.Token);
            if (failure is not null && leaseTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                LogOutboxSendExceededLease(_logger, outboxMessage.Id, outboxMessage.SessionId, sendWindow);
                failure = new DispatchFailure(CreateLeaseTimeout(sendWindow, failure.Exception), IsPoison: false);
            }
        }

        if (failure is not null)
        {
            poll.BlockSession(outboxMessage);
            await RecordFailureAsync(poll.Claiming, outboxMessage, failure, cancellationToken);
            return;
        }

        await CheckpointAsync(poll, [outboxMessage], cancellationToken);
    }

    // Fenced checkpoint of rows that were sent. A row missing from the result means the lease
    // expired and another dispatcher claimed it: it owns the rest of the batch now and sends it in
    // order. Sending more here would duplicate rows and could put a session's rows out of order.
    private async Task CheckpointAsync(ClaimedPoll poll, List<OutboxMessage> sent, CancellationToken cancellationToken)
    {
        var ids = new List<string>(sent.Count);
        foreach (var outboxMessage in sent)
            ids.Add(outboxMessage.Id);

        poll.AwaitingCheckpoint.AddRange(ids);
        var checkpointed = await poll.Claiming.MarkClaimedAsDispatchedAsync(_dispatcherId, ids, cancellationToken);
        poll.AwaitingCheckpoint.Clear();

        var confirmed = new HashSet<string>(checkpointed, StringComparer.Ordinal);
        OutboxMessage? firstLost = null;
        foreach (var outboxMessage in sent)
        {
            if (confirmed.Contains(outboxMessage.Id))
                poll.Dispatched.Add(outboxMessage.Id);
            else
                firstLost ??= outboxMessage;
        }

        if (firstLost is not null && !poll.ClaimLost)
        {
            poll.ClaimLost = true;
            LogOutboxClaimLost(_logger, firstLost.Id, firstLost.SessionId, poll.Elapsed, _options.LeaseDuration);
        }
    }

    // Batched dispatch: each segment is a run of one routing key's rows (in that key's claim
    // order) that share a destination, or a single row that cannot be batched. Segments are sent
    // one after another, in the claim order of their first row, so every session keeps its order.
    private async Task DispatchSegmentsAsync(ClaimedPoll poll, IBatchingSender batching, CancellationToken cancellationToken)
    {
        foreach (var segment in BuildSegments(poll.Claimed))
        {
            if (segment.Messages is null)
            {
                await DispatchClaimedRowAsync(poll, segment.Rows[0], cancellationToken);
                continue;
            }

            var next = 0;
            while (next < segment.Rows.Count)
            {
                var outboxMessage = segment.Rows[next];
                if (poll.IsBlocked(outboxMessage))
                {
                    poll.Release.Add(outboxMessage.Id);
                    next++;
                    continue;
                }

                var elapsed = poll.Elapsed;
                if (elapsed >= poll.SendBudget)
                {
                    poll.ReleaseForBudget(outboxMessage);
                    next++;
                    continue;
                }

                var (resumeAt, sendAlone) = await DispatchBatchAsync(poll, batching, segment, next, elapsed, cancellationToken);
                // A row that could not be added (too large, or the add threw) is sent on its own
                // after its batch was disposed; its outcome is decided by that send.
                if (sendAlone is not null)
                    await DispatchClaimedRowAsync(poll, sendAlone, cancellationToken);
                next = resumeAt;
            }
        }
    }

    // Fills one batch from segment rows starting at start and sends it. Returns the index of the
    // first row left for the next batch, and a row that must be sent on its own.
    private async Task<(int ResumeAt, OutboxMessage? SendAlone)> DispatchBatchAsync(
        ClaimedPoll poll,
        IBatchingSender batching,
        Segment segment,
        int start,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var window = poll.SendWindow(elapsed);
        using var leaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        leaseTimeout.CancelAfter(window);
        var started = new BatchStart(Stopwatch.GetTimestamp(), DateTime.UtcNow);

        ISenderBatch batch;
        try
        {
            batch = await batching.CreateBatchAsync(leaseTimeout.Token);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("The outbox poll was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (leaseTimeout.IsCancellationRequested)
            {
                await FailBatchOnLeaseWindowAsync(poll, [segment.Rows[start]], window, started, ex, cancellationToken);
                return (start + 1, null);
            }

            // Nothing is recorded for the failed creation: the rows are sent one at a time, so
            // only a row that really fails gets an attempt. The next segment tries a batch again.
            LogOutboxBatchCreationFailed(_logger, ex);
            for (var index = start; index < segment.Rows.Count; index++)
                await DispatchClaimedRowAsync(poll, segment.Rows[index], cancellationToken);
            return (segment.Rows.Count, null);
        }

        using (batch)
        {
            var added = new List<OutboxMessage>();
            var next = start;
            while (next < segment.Rows.Count)
            {
                var outboxMessage = segment.Rows[next];
                if (poll.IsBlocked(outboxMessage))
                {
                    poll.Release.Add(outboxMessage.Id);
                    next++;
                    continue;
                }

                // The add is synchronous and cannot be interrupted: check for cancellation and an
                // expired lease window right before and right after it instead.
                cancellationToken.ThrowIfCancellationRequested();
                if (leaseTimeout.IsCancellationRequested)
                {
                    if (added.Count == 0)
                    {
                        await FailBatchOnLeaseWindowAsync(poll, [outboxMessage], window, started, null, cancellationToken);
                        return (next + 1, null);
                    }

                    await FailBatchOnLeaseWindowAsync(poll, added, window, started, null, cancellationToken);
                    return (next, null);
                }

                bool fits;
                Exception? addError = null;
                try
                {
                    fits = batch.TryAdd(segment.Messages![next], outboxMessage.EnqueueDelayMinutes);
                }
                catch (Exception ex)
                {
                    fits = false;
                    addError = ex;
                }

                if (fits)
                    added.Add(outboxMessage);

                cancellationToken.ThrowIfCancellationRequested();
                if (leaseTimeout.IsCancellationRequested)
                {
                    var assigned = new List<OutboxMessage>(added);
                    if (!fits)
                        assigned.Add(outboxMessage);
                    await FailBatchOnLeaseWindowAsync(poll, assigned, window, started, null, cancellationToken);
                    return (next + 1, null);
                }

                if (addError is not null)
                {
                    LogOutboxBatchAddFailed(_logger, addError, outboxMessage.Id, outboxMessage.SessionId);
                    if (added.Count > 0)
                        await SendBatchAsync(poll, batch, added, leaseTimeout, window, started, cancellationToken);
                    return (next + 1, outboxMessage);
                }

                if (fits)
                {
                    next++;
                    continue;
                }

                if (added.Count == 0)
                {
                    // Too large even for an empty batch: the single send reports the broker's
                    // size error as a normal failed attempt.
                    return (next + 1, outboxMessage);
                }

                // The batch is full: send it, and this row starts the next batch.
                await SendBatchAsync(poll, batch, added, leaseTimeout, window, started, cancellationToken);
                return (next, null);
            }

            if (added.Count > 0)
                await SendBatchAsync(poll, batch, added, leaseTimeout, window, started, cancellationToken);
            return (next, null);
        }
    }

    private async Task SendBatchAsync(
        ClaimedPoll poll,
        ISenderBatch batch,
        List<OutboxMessage> added,
        CancellationTokenSource leaseTimeout,
        TimeSpan window,
        BatchStart started,
        CancellationToken cancellationToken)
    {
        if (poll.Elapsed >= poll.SendBudget)
        {
            foreach (var outboxMessage in added)
                poll.ReleaseForBudget(outboxMessage);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (leaseTimeout.IsCancellationRequested)
        {
            await FailBatchOnLeaseWindowAsync(poll, added, window, started, null, cancellationToken);
            return;
        }

        try
        {
            await batch.SendAsync(leaseTimeout.Token);
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            throw new OperationCanceledException("The outbox poll was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (leaseTimeout.IsCancellationRequested)
            {
                await FailBatchOnLeaseWindowAsync(poll, added, window, started, ex, cancellationToken);
                return;
            }

            // Isolate the bad row: resend one at a time through the per-row path, which keeps
            // attempt counts, backoff, parking and session blocking per row.
            LogOutboxBatchSendFailed(_logger, ex, added.Count, added[0].SessionId);
            foreach (var outboxMessage in added)
                await DispatchClaimedRowAsync(poll, outboxMessage, cancellationToken);
            return;
        }

        foreach (var outboxMessage in added)
            RecordRowOutcome(outboxMessage, started, error: null);

        await CheckpointAsync(poll, added, cancellationToken);
    }

    // The lease window cancelled the batch (or expired around an add): blame only its first row,
    // as a timed-out row send is blamed today, and release the rest of the batch unsent.
    private async Task FailBatchOnLeaseWindowAsync(
        ClaimedPoll poll,
        List<OutboxMessage> assigned,
        TimeSpan window,
        BatchStart started,
        Exception? innerException,
        CancellationToken cancellationToken)
    {
        var first = assigned[0];
        LogOutboxSendExceededLease(_logger, first.Id, first.SessionId, window);
        var timeout = CreateLeaseTimeout(window, innerException);
        RecordRowOutcome(first, started, timeout);
        poll.BlockSession(first);
        for (var index = 1; index < assigned.Count; index++)
            poll.Release.Add(assigned[index].Id);

        await RecordFailureAsync(poll.Claiming, first, new DispatchFailure(timeout, IsPoison: false), cancellationToken);
    }

    private static TimeoutException CreateLeaseTimeout(TimeSpan window, Exception? innerException) =>
        new($"The send did not complete within the {window} left on the dispatcher's lease.", innerException);

    private static List<Segment> BuildSegments(IReadOnlyList<OutboxMessage> claimed)
    {
        var segments = new List<Segment>();
        // The open batchable segment of each routing key ("" stands for no session).
        var open = new Dictionary<string, Segment>(StringComparer.Ordinal);
        foreach (var outboxMessage in claimed)
        {
            var message = TryReadPayload(outboxMessage);
            if (message is null || outboxMessage.ScheduledEnqueueTimeUtc.HasValue)
            {
                // An unreadable row fails through the per-row path (as poison), and a scheduled
                // row is sent with ScheduleMessage. Either one ends its key's open segment.
                open.Remove(message?.SessionId ?? outboxMessage.SessionId ?? string.Empty);
                segments.Add(new Segment([outboxMessage], null));
                continue;
            }

            var routingKey = message.SessionId ?? string.Empty;
            if (!open.TryGetValue(routingKey, out var segment) || !string.Equals(segment.To, message.To, StringComparison.Ordinal))
            {
                segment = new Segment([], []) { To = message.To };
                segments.Add(segment);
                open[routingKey] = segment;
            }

            segment.Rows.Add(outboxMessage);
            segment.Messages!.Add(message);
        }

        return segments;
    }

    private static Message? TryReadPayload(OutboxMessage outboxMessage)
    {
        try
        {
            return JsonConvert.DeserializeObject<Message>(outboxMessage.Payload, Constants.CreateSafeJsonSettings());
        }
#pragma warning disable CA1031 // The per-row path reports the failure, with the same classification as today.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private void RecordRowOutcome(OutboxMessage outboxMessage, BatchStart started, Exception? error)
    {
        var savedCurrent = Activity.Current;
        Activity.Current = null;
        Activity? activity = null;
        try
        {
            activity = StartDispatchActivity(outboxMessage);
            activity?.SetStartTime(started.UtcTime);
            var elapsed = Stopwatch.GetElapsedTime(started.Timestamp).TotalMilliseconds;
            if (error is null)
            {
                var tags = BuildDispatchTags(outboxMessage.To, "dispatched", errorType: null);
                NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
                NimBusMeters.OutboxDispatched.Add(1, tags);
                activity?.SetStatus(ActivityStatusCode.Ok);
                LogOutboxDispatchedMessage(_logger, outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
            }
            else
            {
                var errorType = error.GetType().FullName;
                var tags = BuildDispatchTags(outboxMessage.To, "failed", errorType);
                NimBusMeters.OutboxDispatchDuration.Record(elapsed, tags);
                NimBusMeters.OutboxDispatched.Add(1, tags);
                MarkActivityFailed(activity, error, errorType);
            }
        }
        finally
        {
            activity?.Dispose();
            Activity.Current = savedCurrent;
        }
    }

    private readonly record struct BatchStart(long Timestamp, DateTime UtcTime);

    /// <summary>A run of rows sent as batches, or (when <see cref="Messages"/> is null) one row sent alone.</summary>
    private sealed class Segment(List<OutboxMessage> rows, List<Message>? messages)
    {
        public List<OutboxMessage> Rows { get; } = rows;
        public List<Message>? Messages { get; } = messages;
        public string? To { get; init; }
    }

    /// <summary>The state of one claiming poll, shared by the per-row and batch paths.</summary>
    private sealed class ClaimedPoll(IOutboxClaiming claiming, IReadOnlyList<OutboxMessage> claimed, long claimStarted, TimeSpan leaseDuration)
    {
        private readonly TimeSpan _leaseMargin = leaseDuration / 10;
        // FIFO per session (ADR-001): once a session's row fails, its later rows in this batch
        // are released unsent. The store keeps the whole session out of later batches while the
        // failed row waits for its retry or is parked, so it cannot crowd out other sessions.
        private readonly HashSet<string> _failedSessions = new(StringComparer.Ordinal);

        public IOutboxClaiming Claiming { get; } = claiming;
        public IReadOnlyList<OutboxMessage> Claimed { get; } = claimed;
        public TimeSpan SendBudget { get; } = leaseDuration / 2;
        public List<string> Dispatched { get; } = new();
        public List<string> Release { get; } = new();
        public List<string> AwaitingCheckpoint { get; } = new();
        public int BudgetExhausted { get; private set; }
        public bool ClaimLost { get; set; }
        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(claimStarted);

        public TimeSpan SendWindow(TimeSpan elapsed) => leaseDuration - _leaseMargin - elapsed;

        public bool IsBlocked(OutboxMessage outboxMessage) =>
            ClaimLost || (!string.IsNullOrEmpty(outboxMessage.SessionId) && _failedSessions.Contains(outboxMessage.SessionId));

        // Block only this session; session-less rows fail independently.
        public void BlockSession(OutboxMessage outboxMessage)
        {
            if (!string.IsNullOrEmpty(outboxMessage.SessionId))
                _failedSessions.Add(outboxMessage.SessionId);
        }

        public void ReleaseForBudget(OutboxMessage outboxMessage)
        {
            Release.Add(outboxMessage.Id);
            BudgetExhausted++;
        }
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
        // A claiming store checkpoints only rows this dispatcher still holds.
        if (dispatched.Count > 0)
        {
            Task? checkpointTask = null;
            try
            {
                checkpointTask = claiming is null
                    ? _outbox.MarkAsDispatchedAsync(dispatched, CancellationToken.None)
                    : claiming.MarkClaimedAsDispatchedAsync(_dispatcherId, dispatched, CancellationToken.None);
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

    private Task<DispatchFailure?> DispatchOneAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken) =>
        DispatchOneAsync(outboxMessage, cancellationToken, cancellationToken);

    // cancellationToken is the poll's token: its cancellation propagates. sendToken may also
    // fire on a send timeout, which is reported as a failed attempt.
    private async Task<DispatchFailure?> DispatchOneAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken, CancellationToken sendToken)
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
            activity = StartDispatchActivity(outboxMessage);

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
                await _sender.ScheduleMessage(message, new DateTimeOffset(outboxMessage.ScheduledEnqueueTimeUtc.Value, TimeSpan.Zero), sendToken);
            }
            else
            {
                await _sender.Send(message, outboxMessage.EnqueueDelayMinutes, sendToken);
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
            MarkActivityFailed(activity, ex, errorType);

            return new DispatchFailure(ex, payloadUnreadable);
        }
        finally
        {
            activity?.Dispose();
            Activity.Current = savedCurrent;
        }
    }

    private static Activity? StartDispatchActivity(OutboxMessage outboxMessage)
    {
        // Endpoint comes from the persisted column so dispatch spans are
        // tagged even when payload deserialization fails on a malformed row.
        var endpoint = outboxMessage.To;
        ActivityLink? link = TryBuildLink(outboxMessage.TraceParent, outboxMessage.TraceState);
        // FR-015: messaging spans use "{operation.type} {destination.name}".
        var spanName = string.IsNullOrEmpty(endpoint) ? "publish" : $"publish {endpoint}";
        var activity = NimBusActivitySources.Outbox.StartActivity(
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

        return activity;
    }

    private static void MarkActivityFailed(Activity? activity, Exception ex, string? errorType)
    {
        if (activity is null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag(MessagingAttributes.ErrorType, errorType);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            { "exception.type", errorType },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.ToString() },
        }));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox store {OutboxType} does not implement IOutboxClaiming: failed rows are retried every poll without backoff or parking, and only one dispatcher instance may run against it.")]
    private static partial void LogOutboxStoreDoesNotSupportClaiming(ILogger logger, string? outboxType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatch attempt {Attempt} of {MaxAttempts} failed for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId}); retrying in {RetryDelay}. Later messages of the session wait behind it.")]
    private static partial void LogOutboxDispatchAttemptFailed(ILogger logger, Exception exception, int attempt, int maxAttempts, string outboxId, string? eventTypeId, string? sessionId, string? messageId, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {OutboxId} parked after {Attempt} failed attempt(s) (event {EventTypeId}, session {SessionId}, messageId {MessageId}). It is no longer dispatched, and later messages of its session wait until it is requeued.")]
    private static partial void LogOutboxMessageParked(ILogger logger, Exception exception, string outboxId, int attempt, string? eventTypeId, string? sessionId, string? messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the failed dispatch of outbox message {OutboxId}; it is retried once its lease expires.")]
    private static partial void LogCouldNotRecordOutboxDispatchFailure(ILogger logger, Exception exception, string outboxId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release {Count} claimed outbox message(s); they become available when their lease expires.")]
    private static partial void LogCouldNotReleaseOutboxClaims(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatcher lost its claim on message {OutboxId} (session {SessionId}) {Elapsed} after claiming it with a {LeaseDuration} lease: another dispatcher claimed it after the lease expired. The message was sent and may be delivered twice; this dispatcher stops sending the rest of its batch, which the new owner sends in order. Consider a longer lease or a smaller batch size.")]
    private static partial void LogOutboxClaimLost(ILogger logger, string outboxId, string? sessionId, TimeSpan elapsed, TimeSpan leaseDuration);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox send of message {OutboxId} (session {SessionId}) was cancelled after {SendWindow} because the dispatcher's lease was about to expire; it is recorded as a failed attempt and retried. The broker may still have accepted it, so it can be delivered twice.")]
    private static partial void LogOutboxSendExceededLease(ILogger logger, string outboxId, string? sessionId, TimeSpan sendWindow);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox dispatcher used half of its lease ({SendBudget}) and released {Count} unsent message(s); consider a smaller batch size or a longer lease.")]
    private static partial void LogOutboxLeaseBudgetExhausted(ILogger logger, TimeSpan sendBudget, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox batch send of {Count} message(s) (session {SessionId}) failed; sending them one at a time so only a failing message counts an attempt.")]
    private static partial void LogOutboxBatchSendFailed(ILogger logger, Exception exception, int count, string? sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not create an outbox send batch; sending the messages of this run one at a time.")]
    private static partial void LogOutboxBatchCreationFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not add outbox message {OutboxId} (session {SessionId}) to a send batch; sending it on its own.")]
    private static partial void LogOutboxBatchAddFailed(ILogger logger, Exception exception, string outboxId, string? sessionId);

}
