using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;

namespace NimBus.Resolver.Services;

public class ResolverService : IMessageHandler
{
    private readonly IMessageTrackingStore _store;
    private readonly IMessageStateChangeNotifier _notifier;
    private readonly ILogger? _logger;
    private readonly IEndpointMetadataStore? _metadataStore;
    private readonly IServiceHealthStore? _serviceHealthStore;

    private const string CosmosThrottleDeadLetterReason = "CosmosDbThrottled";
    private const int BaseDelaySeconds = 5;
    private const int MaxDelaySeconds = 300; // 5 minutes
    private const string TransientDeadLetterReason = "Max throttle retries exceeded";

    // Backoff for a message the store could not persist: 5 s doubling to 5 min, the
    // same schedule NimBus.Core.RetryPolicy offers to handlers. Attempt 0 = 5 s.
    private static readonly RetryPolicy StoreBackoff = new()
    {
        Strategy = BackoffStrategy.Exponential,
        BaseDelay = TimeSpan.FromSeconds(BaseDelaySeconds),
        MaxDelay = TimeSpan.FromSeconds(MaxDelaySeconds),
    };

    private static readonly Dictionary<MessageType, ResolutionStatus> MessageTypeToStatusMap = new()
    {
        [MessageType.EventRequest] = ResolutionStatus.Pending,
        [MessageType.ResubmissionRequest] = ResolutionStatus.Pending,
        [MessageType.RetryRequest] = ResolutionStatus.Pending,
        [MessageType.SkipRequest] = ResolutionStatus.Pending,
        [MessageType.ContinuationRequest] = ResolutionStatus.Pending,
        // PendingHandoff control flow. The response from the subscriber records
        // the audit row as Pending+Handoff; the two settlement requests are
        // projected as plain Pending rows (sub-status cleared) that flip when
        // their resulting ResolutionResponse / ErrorResponse arrive. The agent
        // zone's non-claiming receive and the WebApp's settle guard rely on that
        // projection to stop handing out a just-settled event (ADR-012, 2026-09-15 note).
        [MessageType.PendingHandoffResponse] = ResolutionStatus.Pending,
        [MessageType.HandoffCompletedRequest] = ResolutionStatus.Pending,
        [MessageType.HandoffFailedRequest] = ResolutionStatus.Pending,
        [MessageType.ErrorResponse] = ResolutionStatus.Failed,
        [MessageType.ResolutionResponse] = ResolutionStatus.Completed,
        [MessageType.DeferralResponse] = ResolutionStatus.Deferred,
        [MessageType.SkipResponse] = ResolutionStatus.Skipped,
        [MessageType.UnsupportedResponse] = ResolutionStatus.Unsupported,
    };

    /// <summary>
    /// Creates the Resolver message handler.
    /// </summary>
    /// <param name="store">Tracking store the audit trail and endpoint state are written to.</param>
    /// <param name="notifier">Write-path state-change notifier; a no-op notifier is used when omitted.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="metadataStore">
    /// Optional heartbeat store. When absent the platform heartbeat degrades gracefully:
    /// heartbeat traffic is still diverted away from the audit trail and completed, just
    /// not recorded.
    /// </param>
    /// <param name="serviceHealthStore">
    /// Optional service-liveness store backing the Resolver's own probe. Same graceful
    /// degradation as <paramref name="metadataStore"/> when absent.
    /// </param>
    public ResolverService(
        IMessageTrackingStore store,
        IMessageStateChangeNotifier? notifier = null,
        ILogger<ResolverService>? logger = null,
        IEndpointMetadataStore? metadataStore = null,
        IServiceHealthStore? serviceHealthStore = null)
    {
        _store = store;
        _notifier = notifier ?? new NoopMessageStateChangeNotifier();
        _logger = logger;
        _metadataStore = metadataStore;
        _serviceHealthStore = serviceHealthStore;
    }

    public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        _logger?.LogTrace("Resolver: Handle {EventTypeId} EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            messageContext.MessageContent.EventContent?.EventTypeId, messageContext.EventId, messageContext.MessageId, messageContext.SessionId);

        // The consumer span is owned by the transport boundary
        // (ServiceBusAdapter → NimBusConsumerInstrumentation). When invoked
        // through ServiceBusAdapter (the Azure Function `Functions.cs` path),
        // Activity.Current is the consumer span and the resolver's downstream
        // RecordOutcome / RecordAudit spans nest under it automatically. When
        // invoked directly (tests / non-adapter hosts) those resolver-side
        // spans become roots — that's expected, the resolver doesn't fabricate
        // a transport span when there isn't one.
        try
        {
            // Platform heartbeat traffic is diverted before anything touches the
            // tracking store: it is infrastructure chatter, not integration events,
            // so it must never appear on the Events / Flow / Monitor pages nor in
            // the latency aggregates.
            if (IsHeartbeat(messageContext))
            {
                await HandleHeartbeatMessage(messageContext, cancellationToken);
                return;
            }

            MessageEntity messageEntity = CreateMessageEntity(messageContext);

            await _store.StoreMessage(messageEntity);

            var (status, applied) = await UpdateState(messageEntity);

            // Written only after the event writes succeed: the audit is a plain insert, so
            // writing it first would repeat it on every copy a store failure reschedules.
            if (messageEntity.MessageType == MessageType.RetryRequest)
            {
                var messageAudit = new MessageAuditEntity() { AuditorName = Constants.ManagerId, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Retry };
                await InstrumentAuditWrite(messageContext, messageAudit);
            }

            if (applied)
            {
                _logger?.LogInformation("Resolver: Updated Endpoint EndpointId:{EndpointId}, Status:{Status}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageEntity.EndpointId, status, messageEntity.EventId, messageContext.MessageId, messageEntity.SessionId);

                await NotifyEndpointStateChanged(messageEntity.EndpointId, cancellationToken);
            }
            else
            {
                await HandleStaleOutcome(messageContext, messageEntity, cancellationToken);
            }

            // Completed either way: the history document is already stored, so the Flow tab
            // keeps showing the late copy after the response — the forensic trail that made
            // the Spec 030 incident diagnosable.
            await messageContext.Complete(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown is not a resolver failure. Leave the message unsettled
            // so the transport can stop cooperatively and redeliver it later.
            throw;
        }
        catch (RequestLimitException ex)
        {
            await HandleCosmosThrottle(messageContext, ex, cancellationToken);
        }
        catch (StorageProviderTransientException ex)
        {
            await HandleThrottling(messageContext, ex.RetryAfter, cancellationToken);
        }
        catch (TransientException transientException)
        {
            _logger?.LogError(transientException, "Resolver: Transient exception EventId:{EventId}", messageContext.EventId);
            await messageContext.Abandon(transientException);
        }
        catch (Exception unexpectedException)
        {
            _logger?.LogError(unexpectedException, "Resolver: Failed to handle message, add to DeadLetter. EventId:{EventId}", messageContext.EventId);
            await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);
        }
    }

    private Task HandleCosmosThrottle(
        IMessageContext messageContext,
        RequestLimitException exception,
        CancellationToken cancellationToken) =>
        HandleStoreFailure(messageContext, exception, exception.RetryAfter, StoreRetryReason.Throttled, CosmosThrottleDeadLetterReason, cancellationToken);

    private Task HandleThrottling(IMessageContext messageContext, TimeSpan? retryAfter, CancellationToken cancellationToken) =>
        HandleStoreFailure(messageContext, exception: null, retryAfter, StoreRetryReason.Transient, TransientDeadLetterReason, cancellationToken);

    // One settlement path for every message the store could not persist, whether Cosmos
    // rate-limited it (throttled) or the provider failed transiently. The logical attempt
    // counts scheduled re-sends plus broker deliveries, so both reasons share one delivery
    // budget; the delay is the larger of the exponential backoff and the store's RetryAfter
    // hint. Each outcome is counted once, after its settlement call succeeded, so a failed
    // settlement is never reported as done.
    private async Task HandleStoreFailure(
        IMessageContext messageContext,
        Exception? exception,
        TimeSpan? retryAfter,
        string reason,
        string deadLetterReason,
        CancellationToken cancellationToken)
    {
        var deliveryCount = messageContext is IMessageDeliveryContext deliveryContext
            ? Math.Max(1, deliveryContext.DeliveryCount)
            : 1;
        var logicalAttempt = messageContext.ThrottleRetryCount + deliveryCount;
        var (endpointId, _) = DetermineEndpoint(messageContext);

        _logger?.LogWarning(
            exception,
            "Resolver: Store write failed ({Reason}). EventId:{EventId}, SessionId:{SessionId}, LogicalAttempt:{LogicalAttempt}/{MaxAttempts}, DeliveryCount:{DeliveryCount}",
            reason,
            messageContext.EventId,
            messageContext.SessionId,
            logicalAttempt,
            Constants.ServiceBusMaxDeliveryCount,
            deliveryCount);

        if (logicalAttempt >= Constants.ServiceBusMaxDeliveryCount)
        {
            _logger?.LogError(
                "Resolver: Store failures ({Reason}) exhausted the delivery budget ({MaxAttempts}). Dead-lettering. EventId:{EventId}, SessionId:{SessionId}",
                reason,
                Constants.ServiceBusMaxDeliveryCount,
                messageContext.EventId,
                messageContext.SessionId);
            await messageContext.DeadLetter(deadLetterReason, null, cancellationToken);
            RecordStoreRetry(endpointId, reason, RetryAction.DeadLettered);
            return;
        }

        // Heartbeats retry in place, like their transient failures do: a rescheduled copy lands
        // minutes later and would overwrite a newer heartbeat (or report the backoff as a probe's
        // round trip), and the next heartbeat supersedes this one anyway. Abandon leaves the
        // message unsettled, so the session redelivers it and DeliveryCount carries the budget.
        if (IsHeartbeat(messageContext))
        {
            _logger?.LogInformation(exception, "Resolver: Heartbeat store write failed ({Reason}); leaving it for the session to redeliver. EventId:{EventId}, SessionId:{SessionId}",
                reason, messageContext.EventId, messageContext.SessionId);
            var heartbeatFailure = $"Heartbeat store write failed ({reason}).";
            await messageContext.Abandon(exception is null
                ? new TransientException(heartbeatFailure)
                : new TransientException(heartbeatFailure, exception));
            RecordStoreRetry(endpointId, reason, RetryAction.Abandoned);
            return;
        }

        // Honor a provider hint only when it is longer than the calculated backoff.
        // Providers such as SQL Server may not supply one.
        var backoff = StoreBackoff.GetDelay(logicalAttempt - 1);
        var useProviderRetryAfter = retryAfter.HasValue && retryAfter.Value > backoff;
        var delay = useProviderRetryAfter ? retryAfter.GetValueOrDefault() : backoff;

        _logger?.LogInformation(
            "Resolver: Store write failed ({Reason}). Scheduling redelivery in {DelaySeconds}s ({DelaySource}). EventId:{EventId}, SessionId:{SessionId}, LogicalAttempt:{LogicalAttempt}/{MaxAttempts}",
            reason,
            delay.TotalSeconds,
            useProviderRetryAfter ? DelaySource.Provider : DelaySource.Backoff,
            messageContext.EventId,
            messageContext.SessionId,
            logicalAttempt,
            Constants.ServiceBusMaxDeliveryCount);

        try
        {
            await messageContext.ScheduleRedelivery(delay, logicalAttempt, cancellationToken);
        }
        catch (TransientException ex)
        {
            _logger?.LogInformation(ex, "Resolver: Failed to schedule redelivery. Abandoning for retry. EventId:{EventId}, SessionId:{SessionId}",
                messageContext.EventId, messageContext.SessionId);
            await messageContext.Abandon(ex);
            RecordStoreRetry(endpointId, reason, RetryAction.Abandoned);
            return;
        }

        RecordStoreRetry(endpointId, reason, RetryAction.Rescheduled);
        RecordRetryDelay(endpointId, delay, useProviderRetryAfter);
    }

    // The store refused the write because the audit row already holds a later outcome
    // (Spec 030): a copy delayed by auto-forward lag, by ScheduleRedelivery, or replayed
    // from the dead-letter queue. Nothing is retried — the row is already correct — but the
    // copy is attributed, because a silent drop is indistinguishable from a lost message.
    private async Task HandleStaleOutcome(
        IMessageContext messageContext,
        MessageEntity messageEntity,
        CancellationToken cancellationToken)
    {
        var deliveryCount = messageContext is IMessageDeliveryContext deliveryContext
            ? Math.Max(1, deliveryContext.DeliveryCount)
            : 1;

        // ThrottleRetryCount > 0 attributes the copy to ScheduleRedelivery; a zero count with
        // DeliveryCount 1 to fan-out lag or a dead-letter replay; DeliveryCount > 1 to a
        // broker redelivery.
        _logger?.LogWarning(
            "Resolver: Ignored stale {MessageType}; audit row already reflects a later state. EndpointId:{EndpointId}, EventId:{EventId}, SessionId:{SessionId}, MessageId:{MessageId}, EnqueuedTimeUtc:{EnqueuedTimeUtc}, ThrottleRetryCount:{ThrottleRetryCount}, DeliveryCount:{DeliveryCount}",
            messageEntity.MessageType,
            messageEntity.EndpointId,
            messageEntity.EventId,
            messageEntity.SessionId,
            messageContext.MessageId,
            messageEntity.EnqueuedTimeUtc,
            messageContext.ThrottleRetryCount,
            deliveryCount);

        // Best effort. The audit shows in the WebApp audit listing with no UI change and
        // gives SQL deployments an attribution the store itself cannot log; failing to write
        // it must not turn a harmless stale copy into a redelivery.
        try
        {
            var audit = new MessageAuditEntity
            {
                AuditorName = Constants.ResolverId,
                AuditTimestamp = DateTime.UtcNow,
                AuditType = MessageAuditType.Comment,
                EventId = messageEntity.EventId,
                EndpointId = messageEntity.EndpointId,
                Data = $"Ignored stale {messageEntity.MessageType} {messageContext.MessageId}: the audit row already holds a later outcome.",
            };
            await InstrumentAuditWrite(messageContext, audit, messageEntity.EndpointId, messageEntity.EventTypeId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception auditException)
        {
            _logger?.LogWarning(auditException,
                "Resolver: Failed to record the stale-copy audit (non-fatal). EventId:{EventId}, MessageId:{MessageId}",
                messageEntity.EventId, messageContext.MessageId);
        }
    }

    // Fire the state-change notification (provider-neutral). Webhook is no longer
    // the only way for the WebApp to learn about updates; this works for any
    // storage provider including SQL Server which has no Change Feed.
    private async Task NotifyEndpointStateChanged(string endpointId, CancellationToken cancellationToken)
    {
        try { await _notifier.NotifyEndpointStateChangedAsync(endpointId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception notifyEx) { _logger?.LogWarning(notifyEx, "Resolver: state-change notification failed (non-fatal)"); }
    }

    private static void RecordStoreRetry(string? endpoint, string reason, string action) =>
        NimBusMeters.ResolverStoreRetry.Add(1, BuildRetryTags(endpoint,
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreReason, reason),
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusRetryAction, action)));

    // The delay that was actually applied (max of the Resolver's backoff and the store's
    // RetryAfter hint), i.e. how far the copy was pushed behind its session.
    private static void RecordRetryDelay(string? endpoint, TimeSpan delay, bool fromProvider) =>
        NimBusMeters.ResolverRetryDelay.Record(delay.TotalSeconds, BuildRetryTags(endpoint,
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusDelaySource, fromProvider ? DelaySource.Provider : DelaySource.Backoff)));

    // Same optional-endpoint shape as BuildOutcomeTags so operators can split the
    // retry series per endpoint like every other Resolver instrument.
    private static KeyValuePair<string, object?>[] BuildRetryTags(string? endpoint, params KeyValuePair<string, object?>[] tags)
    {
        if (string.IsNullOrEmpty(endpoint))
            return tags;

        var withEndpoint = new KeyValuePair<string, object?>[tags.Length + 1];
        tags.CopyTo(withEndpoint, 0);
        withEndpoint[tags.Length] = new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint);
        return withEndpoint;
    }

    /// <summary>
    /// Routes one heartbeat message. Endpoint answers update the heartbeat store; the
    /// Resolver's own liveness probe settles itself; the copies of endpoint requests
    /// that the Resolver's subscription also receives are dropped.
    /// </summary>
    /// <remarks>
    /// Generic transient storage failures remain unsettled and may eventually be
    /// broker-dead-lettered as MaxDeliveryCountExceeded. Cosmos DB throttling is also
    /// retried in place (never rescheduled: a late copy could overwrite a newer heartbeat),
    /// within the delivery budget, then dead-lettered with the stable CosmosDbThrottled reason.
    /// </remarks>
    private async Task HandleHeartbeatMessage(IMessageContext messageContext, CancellationToken cancellationToken)
    {
        if (messageContext.MessageType == MessageType.EventRequest)
        {
            // A heartbeat EventRequest addressed to the Resolver itself is the
            // WebApp's liveness probe; every other one is the copy of an
            // endpoint request the Resolver subscription also receives.
            if (Constants.ResolverId.Equals(messageContext.To, StringComparison.OrdinalIgnoreCase))
            {
                await HandleSelfProbe(messageContext, cancellationToken);
                return;
            }

            _logger?.LogTrace("Resolver: Dropped Heartbeat request copy. EventId:{EventId}, MessageId:{MessageId}",
                messageContext.EventId, messageContext.MessageId);
            await messageContext.Complete(cancellationToken);
            return;
        }

        var endpointId = GetHeartbeatEndpointId(messageContext);
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            _logger?.LogWarning("Resolver: Heartbeat response without endpoint. EventId:{EventId}, MessageId:{MessageId}",
                messageContext.EventId, messageContext.MessageId);
            await messageContext.Complete(cancellationToken);
            return;
        }

        if (_metadataStore is null)
        {
            _logger?.LogWarning("Resolver: heartbeat store not configured; completing without recording. EndpointId:{EndpointId}, EventId:{EventId}",
                endpointId, messageContext.EventId);
            await messageContext.Complete(cancellationToken);
            return;
        }

        var heartbeat = CreateHeartbeat(messageContext);

        try
        {
            await _metadataStore.SetHeartbeat(heartbeat, endpointId);
        }
        catch (RequestLimitException)
        {
            throw;
        }
        catch (StorageProviderTransientException ex)
        {
            // Return without settling: the session redelivers the message. Heartbeats
            // deliberately skip the scheduled-redelivery path used for event traffic —
            // the next probe supersedes this one anyway.
            _logger?.LogInformation(ex, "Resolver: Storage temporarily unavailable, will reprocess heartbeat. EndpointId:{EndpointId}, EventId:{EventId}",
                endpointId, messageContext.EventId);
            return;
        }

        _logger?.LogInformation("Resolver: Updated Heartbeat EndpointId:{EndpointId}, Status:{Status}, EventId:{EventId}, MessageId:{MessageId}",
            endpointId, heartbeat.EndpointHeartbeatStatus, messageContext.EventId, messageContext.MessageId);

        try { await _notifier.NotifyHeartbeatChangedAsync(endpointId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception notifyEx) { _logger?.LogWarning(notifyEx, "Resolver: heartbeat notification failed (non-fatal)"); }

        await messageContext.Complete(cancellationToken);
    }

    /// <summary>
    /// Answers the WebApp's Resolver liveness probe. Reaching this point already
    /// proves what the probe asks: the host is up, it is draining its Service Bus
    /// session subscription, and — once the write below succeeds — it can reach
    /// the message store. So the probe settles itself here rather than sending a
    /// response back over the bus.
    /// </summary>
    private async Task HandleSelfProbe(IMessageContext messageContext, CancellationToken cancellationToken)
    {
        if (_serviceHealthStore is null)
        {
            _logger?.LogWarning("Resolver: service health store not configured; completing liveness probe without recording. MessageId:{MessageId}",
                messageContext.MessageId);
            await messageContext.Complete(cancellationToken);
            return;
        }

        var content = DeserializeHeartbeat(messageContext);
        var now = DateTime.UtcNow;
        var sentAt = TimestampOrDefault(content.ForwardSendTime, messageContext.EnqueuedTimeUtc);

        var health = new ServiceHealth
        {
            ServiceId = Constants.ResolverId,
            Status = HeartbeatStatus.On,
            Version = GetResolverVersion(),
            LastSeenUtc = now,
            RoundTripMs = sentAt == default ? null : (long?)Math.Max(0, (now - sentAt).TotalMilliseconds),
        };

        try
        {
            await _serviceHealthStore.SetServiceHealth(health);
        }
        catch (RequestLimitException)
        {
            throw;
        }
        catch (StorageProviderTransientException ex)
        {
            _logger?.LogInformation(ex, "Resolver: Storage temporarily unavailable, will reprocess liveness probe. EventId:{EventId}",
                messageContext.EventId);
            return;
        }

        _logger?.LogInformation("Resolver: Answered liveness probe. RoundTripMs:{RoundTripMs}, Version:{Version}, MessageId:{MessageId}",
            health.RoundTripMs, health.Version, messageContext.MessageId);

        try { await _notifier.NotifyServiceHealthChangedAsync(Constants.ResolverId, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception notifyEx) { _logger?.LogWarning(notifyEx, "Resolver: service health notification failed (non-fatal)"); }

        await messageContext.Complete(cancellationToken);
    }

    private static string GetResolverVersion()
    {
        var assembly = typeof(ResolverService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the '+<sha>' source-revision suffix the .NET SDK appends to the
            // informational version; the release identity is the bare package version.
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static bool IsHeartbeat(IReceivedMessage message)
    {
        var eventTypeId = message.EventTypeId
            ?? message.MessageContent?.EventContent?.EventTypeId;
        return eventTypeId?.Equals(CoreHeartbeat.EventTypeId, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Attributes a heartbeat answer to an endpoint. The endpoint the payload names wins
    /// over the message's <c>From</c> — <c>ResponseService.CreateResponse</c> is static
    /// and does not stamp <c>From</c>, so a response from an older or hand-rolled
    /// emitter can arrive with it blank while the payload always carries the endpoint
    /// the SDK answered for.
    /// </summary>
    private static string? GetHeartbeatEndpointId(IReceivedMessage message)
    {
        var content = DeserializeHeartbeat(message);
        return !string.IsNullOrWhiteSpace(content.Endpoint)
            ? content.Endpoint
            : message.From;
    }

    private static Heartbeat CreateHeartbeat(IReceivedMessage message)
    {
        var content = DeserializeHeartbeat(message);
        var now = DateTime.UtcNow;
        return new Heartbeat
        {
            MessageId = !string.IsNullOrWhiteSpace(message.CorrelationId)
                ? message.CorrelationId
                : message.MessageId,
            StartTime = TimestampOrDefault(content.ForwardSendTime, TimestampOrDefault(message.EnqueuedTimeUtc, now)),
            ReceivedTime = TimestampOrDefault(content.ForwardReceivedTime, now),
            EndTime = now,
            SdkVersion = content.SdkVersion ?? string.Empty,
            EndpointHeartbeatStatus = message.MessageType switch
            {
                MessageType.ResolutionResponse => HeartbeatStatus.On,
                MessageType.UnsupportedResponse => HeartbeatStatus.Unsupported,
                MessageType.ErrorResponse => HeartbeatStatus.Off,
                MessageType.DeferralResponse => HeartbeatStatus.Off,
                _ => HeartbeatStatus.Unknown,
            },
        };
    }

    private static CoreHeartbeat DeserializeHeartbeat(IReceivedMessage message)
    {
        var json = message.MessageContent?.EventContent?.EventJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            return new CoreHeartbeat();
        }

        try
        {
            return JsonConvert.DeserializeObject<CoreHeartbeat>(json) ?? new CoreHeartbeat();
        }
        catch (JsonException)
        {
            // A malformed probe payload still proves the endpoint answered; fall back
            // to the empty shape so attribution and timings degrade rather than fail.
            return new CoreHeartbeat();
        }
    }

    private static DateTime TimestampOrDefault(DateTime value, DateTime fallback) =>
        value == default ? fallback : value;

    private MessageEntity CreateMessageEntity(IReceivedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var (endpointId, endpointRole) = DetermineEndpoint(message);

        return new MessageEntity
        {
            EventId = message.EventId,
            MessageId = message.MessageId,
            OriginatingMessageId = message.OriginatingMessageId,
            ParentMessageId = message.ParentMessageId,
            // A request is attributed by To, so From is only recorded. A request put on an
            // endpoint topic by hand (e.g. resubmitted from a dead-letter queue with a broker
            // tool) bypasses the forward rule that stamps From; record its audit copy under
            // the originating endpoint instead of dead-lettering it (never null: stored rows
            // have always had a From). A response is attributed BY From, so there it stays
            // required.
            From = IsRequest(message.MessageType)
                ? message.GetFromOrDefault() ?? message.OriginatingFrom ?? Constants.Self
                : message.From,
            To = message.To,
            OriginatingFrom = message.OriginatingFrom,
            SessionId = message.SessionId,
            CorrelationId = message.CorrelationId,
            EnqueuedTimeUtc = message.EnqueuedTimeUtc,
            MessageContent = message.MessageContent,
            MessageType = message.MessageType,
            EndpointId = endpointId!,
            EndpointRole = endpointRole,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription,
            DeadLetterReason = message.DeadLetterReason,
            EventTypeId = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId!,
            // Per-message timings carried on the response message by the
            // subscriber. Null on EventRequest / original publishes.
            QueueTimeMs = message.QueueTimeMs,
            ProcessingTimeMs = message.ProcessingTimeMs,
            // PendingHandoff metadata. Sub-status is set only on the
            // PendingHandoffResponse audit row (the original Pending+Handoff
            // entry); the Manager-issued HandoffCompleted/HandoffFailed
            // requests are recorded as plain Pending so the subsequent
            // ResolutionResponse / ErrorResponse can flip the original.
            HandoffReason = message.HandoffReason,
            ExternalJobId = message.ExternalJobId,
            ExpectedBy = message.ExpectedBy,
            PendingSubStatus = message.MessageType == MessageType.PendingHandoffResponse ? "Handoff" : null,
            // CloudEvents identity carried on the response from a CloudEvents-consuming
            // subscriber; null for native messages.
            CloudEventId = message.CloudEventId,
            CloudEventSource = message.CloudEventSource,
            CloudEventType = message.CloudEventType,
            CloudEventSubject = message.CloudEventSubject,
        };
    }

    /// <summary>
    /// Determines the endpoint ID and role based on message properties.
    /// </summary>
    /// <remarks>
    /// A request (see <see cref="IsRequest"/>) is addressed to the subscriber, so its endpoint
    /// is <c>To</c>; anything else is a response from the subscriber, so its endpoint is
    /// <c>From</c>. The role is always <see cref="EndpointRole.Subscriber"/>.
    /// </remarks>
    internal (string? endpointId, EndpointRole role) DetermineEndpoint(IReceivedMessage message) =>
        (IsRequest(message.MessageType) ? message.To : message.From, EndpointRole.Subscriber);

    private static bool IsRequest(MessageType messageType) =>
        messageType == MessageType.EventRequest ||
        messageType == MessageType.ContinuationRequest ||
        messageType == MessageType.RetryRequest ||
        messageType == MessageType.ResubmissionRequest ||
        messageType == MessageType.SkipRequest ||
        messageType == MessageType.HandoffCompletedRequest ||
        messageType == MessageType.HandoffFailedRequest;

    private UnresolvedEvent CreateUnresolvedEvent(MessageEntity message, long? processingTimeMsOverride = null)
    {
        return new UnresolvedEvent
        {
            UpdatedAt = DateTime.UtcNow,
            EnqueuedTimeUtc = message.EnqueuedTimeUtc,

            EventId = message.EventId,
            SessionId = message.SessionId,
            CorrelationId = message.CorrelationId,

            ResolutionStatus = GetResultingStatus(message),
            EndpointRole = message.EndpointRole,
            EndpointId = message.EndpointId,
            RetryCount = message.RetryCount,
            RetryLimit = message.RetryLimit,
            MessageType = message.MessageType,
            DeadLetterReason = message.DeadLetterReason,
            DeadLetterErrorDescription = message.DeadLetterErrorDescription,

            LastMessageId = message.MessageId,
            OriginatingMessageId = message.OriginatingMessageId,
            ParentMessageId = message.ParentMessageId,
            Reason = message.MessageType == MessageType.SkipResponse
                ? message.MessageContent?.ErrorContent?.ErrorText
                : message.DeadLetterErrorDescription,
            OriginatingFrom = message.OriginatingFrom,

            EventTypeId = message.EventTypeId,
            To = message.To,
            From = message.From,
            MessageContent = message.MessageContent!,
            QueueTimeMs = message.QueueTimeMs,
            // For terminal settlement of an event that went through async
            // handoff, override the per-hop handler duration with the
            // wall-clock span from the original EventRequest. The raw value
            // remains on the per-message MessageEntity row for auditing.
            ProcessingTimeMs = processingTimeMsOverride ?? message.ProcessingTimeMs,
            // PendingHandoff metadata: copy through from the projected
            // MessageEntity so the UnresolvedEvent (i.e. the audit row
            // surfaced by the WebApp) carries them too.
            PendingSubStatus = message.PendingSubStatus,
            HandoffReason = message.HandoffReason,
            ExternalJobId = message.ExternalJobId,
            ExpectedBy = message.ExpectedBy,
            // Surface CloudEvents identity on the tracking record (null for native).
            CloudEventId = message.CloudEventId,
            CloudEventSource = message.CloudEventSource,
            CloudEventType = message.CloudEventType,
            CloudEventSubject = message.CloudEventSubject,
        };
    }

    // Returns the wall-clock duration since the original EventRequest's
    // EnqueuedTimeUtc when settling a terminal response (Resolution/Error)
    // for an event that previously emitted a PendingHandoffResponse.
    // Returns null otherwise so the regular per-hop ProcessingTimeMs wins.
    private async Task<long?> ComputeHandoffWallClockMsIfTerminal(MessageEntity message)
    {
        if (message.MessageType != MessageType.ResolutionResponse &&
            message.MessageType != MessageType.ErrorResponse)
        {
            return null;
        }

        var history = (await _store.GetEventHistory(message.EventId)).ToList();
        if (!history.Any(m => m.MessageType == MessageType.PendingHandoffResponse))
        {
            return null;
        }

        var eventRequest = history.FirstOrDefault(m => m.MessageType == MessageType.EventRequest);
        if (eventRequest is null)
        {
            return null;
        }

        return (long)Math.Max(0, (DateTime.UtcNow - eventRequest.EnqueuedTimeUtc).TotalMilliseconds);
    }

    /// <summary>
    /// Projects the message onto the audit row. <c>Applied</c> is the store's answer
    /// (Spec 030): false means <c>StaleWriteGuard</c> refused the write because the row
    /// already holds a later outcome. A status with no upload handler writes nothing and
    /// counts as applied, exactly as before.
    /// </summary>
    private async Task<(ResolutionStatus Status, bool Applied)> UpdateState(MessageEntity message)
    {
        ResolutionStatus status = GetResultingStatus(message);
        long? wallClockMs = await ComputeHandoffWallClockMsIfTerminal(message);
        UnresolvedEvent unresolvedEvent = CreateUnresolvedEvent(message, wallClockMs);

        var statusHandlers = new Dictionary<ResolutionStatus, Func<Task<bool>>>
        {
            [ResolutionStatus.Completed] = () => _store.UploadCompletedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            [ResolutionStatus.Skipped] = () => _store.UploadSkippedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            [ResolutionStatus.Failed] = () => _store.UploadFailedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            [ResolutionStatus.Deferred] = () => _store.UploadDeferredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            [ResolutionStatus.Pending] = () => _store.UploadPendingMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            [ResolutionStatus.DeadLettered] = () => _store.UploadDeadletteredMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
            [ResolutionStatus.Unsupported] = () => _store.UploadUnsupportedMessage(message.EventId, message.SessionId, message.EndpointId, unresolvedEvent),
        };

        if (statusHandlers.TryGetValue(status, out var handler))
        {
            return (status, await InstrumentOutcomeWrite(message.EndpointId, status, handler));
        }

        return (status, true);
    }

    private async Task InstrumentAuditWrite(
        IReceivedMessage message,
        MessageAuditEntity audit,
        string? endpointId = null,
        string? eventTypeId = null)
    {
        var auditType = audit.AuditType.ToString().ToLowerInvariant();
        // RetryRequest is a request type, so DetermineEndpoint resolves to message.To.
        // We use that directly to avoid recomputing; a caller that already knows the
        // endpoint (the stale-copy audit) passes it explicitly.
        var endpoint = endpointId ?? message.To;
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = NimBusActivitySources.Resolver.StartActivity(
            "NimBus.Resolver.RecordAudit", ActivityKind.Internal);
        if (activity is not null)
        {
            if (!string.IsNullOrEmpty(endpoint))
                activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
            activity.SetTag(MessagingAttributes.NimBusAuditType, auditType);
        }

        string? errorType = null;
        try
        {
            await _store.StoreMessageAudit(message.EventId, audit, endpointId, eventTypeId);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().FullName;
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.SetTag(MessagingAttributes.ErrorType, errorType);
            }
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var tags = BuildAuditTags(endpoint, auditType, errorType);
            NimBusMeters.ResolverWriteDuration.Record(elapsed, tags);
            NimBusMeters.ResolverAuditWritten.Add(1, tags);
        }
    }

    private async Task<bool> InstrumentOutcomeWrite(string endpointId, ResolutionStatus status, Func<Task<bool>> handler)
    {
        var outcome = status.ToString().ToLowerInvariant();
        var startTimestamp = Stopwatch.GetTimestamp();
        using var activity = NimBusActivitySources.Resolver.StartActivity(
            "NimBus.Resolver.RecordOutcome", ActivityKind.Internal);
        if (activity is not null)
        {
            if (!string.IsNullOrEmpty(endpointId))
                activity.SetTag(MessagingAttributes.NimBusEndpoint, endpointId);
            activity.SetTag(MessagingAttributes.NimBusOutcome, outcome);
        }

        string? errorType = null;
        var applied = false;
        try
        {
            applied = await handler();
            activity?.SetTag(MessagingAttributes.NimBusOutcomeApplied, applied);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return applied;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().FullName;
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.SetTag(MessagingAttributes.ErrorType, errorType);
            }
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var tags = BuildOutcomeTags(endpointId, outcome, errorType);
            NimBusMeters.ResolverWriteDuration.Record(elapsed, tags);

            // A refusal is not a write: it is a successful call that deliberately stored
            // nothing, so counting it under outcome_written would report a stale copy as a
            // fresh outcome. A *failed* write keeps counting there, carrying its error_type
            // tag — that pairing is how the write error rate is read, and moving failures to
            // outcome_ignored would hide them behind a counter named for stale copies.
            // Dashboards that used outcome_written as throughput should sum
            // outcome_written + outcome_ignored.
            if (applied || errorType is not null)
            {
                NimBusMeters.ResolverOutcomeWritten.Add(1, tags);
            }
            else
            {
                NimBusMeters.ResolverOutcomeIgnored.Add(1, tags);
            }
        }
    }

    private static KeyValuePair<string, object?>[] BuildOutcomeTags(string? endpoint, string outcome, string? errorType)
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

    private static KeyValuePair<string, object?>[] BuildAuditTags(string? endpoint, string auditType, string? errorType)
    {
        var tags = new List<KeyValuePair<string, object?>>(3)
        {
            new(MessagingAttributes.NimBusAuditType, auditType),
        };
        if (!string.IsNullOrEmpty(endpoint))
            tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
        if (!string.IsNullOrEmpty(errorType))
            tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType));
        return tags.ToArray();
    }

    private ResolutionStatus GetResultingStatus(MessageEntity message)
    {
        if (message.DeadLetterErrorDescription != null)
        {
            return ResolutionStatus.DeadLettered;
        }

        if (MessageTypeToStatusMap.TryGetValue(message.MessageType, out var status))
        {
            return status;
        }

        throw new ArgumentException($"Unexpected {nameof(MessageType)} {message.MessageType}", nameof(message));
    }

}
