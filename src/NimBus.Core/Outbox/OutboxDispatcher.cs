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
public class OutboxDispatcher
{
    private static readonly TimeSpan DefaultCompensatingCheckpointTimeout = TimeSpan.FromSeconds(5);
    private readonly IOutbox _outbox;
    private readonly ISender _sender;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly TimeSpan _compensatingCheckpointTimeout;

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
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        if (compensatingCheckpointTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(compensatingCheckpointTimeout));

        _compensatingCheckpointTimeout = compensatingCheckpointTimeout;
        _logger = logger ?? NullLogger<OutboxDispatcher>.Instance;
    }

    /// <summary>
    /// Dispatches a batch of pending outbox messages.
    /// </summary>
    /// <param name="batchSize">Maximum number of messages to dispatch in one batch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of messages dispatched.</returns>
    public async Task<int> DispatchPendingAsync(int batchSize = 100, CancellationToken cancellationToken = default)
    {
        var pending = await _outbox.GetPendingAsync(batchSize, cancellationToken);
        if (pending.Count == 0)
            return 0;

        _logger.LogDebug("Outbox dispatch poll found {PendingCount} pending message(s)", pending.Count);

        var dispatched = new List<string>();
        // NimBus's ordering guarantee is FIFO per session (ADR-001), not global.
        // Halt only the failing session so one poison row cannot stall every
        // other session's outbound messages. Rows are oldest-first, so once a
        // session's row fails, later rows for that same session stay parked
        // behind it this poll and are retried next interval.
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

                if (!await DispatchOneAsync(outboxMessage, cancellationToken))
                {
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
                    _logger.LogWarning(
                        ex,
                        "Could not checkpoint {DispatchedCount} outbox message(s) sent before cancellation",
                        dispatched.Count);
                }
            }

            throw;
        }

        return dispatched.Count;
    }

    private async Task ObserveCompensatingCheckpointAsync(Task checkpointTask, int dispatchedCount)
    {
        try
        {
            await checkpointTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Outbox checkpoint completed with an error after shutdown stopped waiting for {DispatchedCount} message(s)",
                dispatchedCount);
        }
    }

    private async Task<bool> DispatchOneAsync(OutboxMessage outboxMessage, CancellationToken cancellationToken)
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

            message = JsonConvert.DeserializeObject<Message>(
                outboxMessage.Payload,
                Constants.CreateSafeJsonSettings())!;

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

            _logger.LogDebug(
                "Outbox dispatched message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId})",
                outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
            return true;
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

            _logger.LogError(
                ex,
                "Outbox dispatch failed for message {OutboxId} (event {EventTypeId}, session {SessionId}, messageId {MessageId}). Halting this poll; will retry next interval.",
                outboxMessage.Id, outboxMessage.EventTypeId, outboxMessage.SessionId, outboxMessage.MessageId);
            return false;
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
}
