using Azure.Messaging.ServiceBus;
using NimBus.Core.CloudEvents;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;

public class MessageContext : IMessageContext, IMessageDeliveryContext
{
    private readonly IServiceBusMessage _sbMessage;
    private readonly IServiceBusSession _sbSession;
    private readonly bool _isDeferred;
    private readonly CloudEventReadOptions? _cloudEventReadOptions;

    private const int MaxDeadLetterErrorDescriptionLength = 4096;
    private const string TruncationIndicator = "\n...[TRUNCATED]...";
    private const string ThrottleRetryCountProperty = "ThrottleRetryCount";

    public MessageContext(IServiceBusMessage sbMessage, IServiceBusSession sbSession, bool isDeferred = false)
        : this(sbMessage, sbSession, isDeferred, cloudEventReadOptions: null)
    {
    }

    /// <summary>
    /// Creates a message context. When <paramref name="cloudEventReadOptions"/>
    /// is non-null the context detects and normalizes inbound CloudEvents; when
    /// null the context is pure native NimBus (default), so native decoding and
    /// property access are unchanged.
    /// </summary>
    public MessageContext(IServiceBusMessage sbMessage, IServiceBusSession sbSession, bool isDeferred, CloudEventReadOptions? cloudEventReadOptions)
    {
        _sbMessage = sbMessage ?? throw new ArgumentNullException(nameof(sbMessage));
        _sbSession = sbSession ?? throw new ArgumentNullException(nameof(sbSession));

        _isDeferred = isDeferred;
        _cloudEventReadOptions = cloudEventReadOptions;
    }

    public string From => GetUserProperty(UserPropertyName.From);

    public int DeliveryCount => _sbMessage.DeliveryCount;

    public string? OriginatingFrom
    {
        get { try { return GetUserProperty(UserPropertyName.OriginatingFrom); } catch (InvalidMessageException) { return null; } }
    }

    public string To => GetUserProperty(UserPropertyName.To);

    public string EventId => GetUserProperty(UserPropertyName.EventId);

    public string EventTypeId
    {
        get
        {
            try
            {
                var eventTypeId = GetUserProperty(UserPropertyName.EventTypeId);
                if (!string.IsNullOrEmpty(eventTypeId)) return eventTypeId;
            }
            catch (InvalidMessageException)
            {
                // Native messages produced before EventTypeId became an
                // application property still carry it in MessageContent.
            }

            return GetContent()?.EventContent?.EventTypeId!;
        }
    }

    public string? DeadLetterReason
    {
        get { try { return GetUserProperty(UserPropertyName.DeadLetterReason); } catch (InvalidMessageException) { return null; } }
    }
    public string? DeadLetterErrorDescription
    {
        get { try { return GetUserProperty(UserPropertyName.DeadLetterErrorDescription); } catch (InvalidMessageException) { return null; } }
    }

    public string? HandoffReason
    {
        get { try { return GetUserProperty(UserPropertyName.HandoffReason); } catch (InvalidMessageException) { return null; } }
    }

    public string? ExternalJobId
    {
        get { try { return GetUserProperty(UserPropertyName.ExternalJobId); } catch (InvalidMessageException) { return null; } }
    }

    public DateTime? ExpectedBy
    {
        get
        {
            try
            {
                var value = GetUserProperty(UserPropertyName.ExpectedBy);
                if (string.IsNullOrEmpty(value)) return null;
                return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : (DateTime?)null;
            }
            catch (InvalidMessageException) { return null; }
            catch (FormatException) { return null; }
        }
    }

    public string OriginatingMessageId => _sbMessage.GetUserProperty(UserPropertyName.OriginatingMessageId) ?? Constants.Self;

    public string ParentMessageId => _sbMessage.GetUserProperty(UserPropertyName.ParentMessageId) ?? Constants.Self;

    public string? InboxMessageId
    {
        get { try { return GetUserProperty(UserPropertyName.InboxMessageId); } catch (InvalidMessageException) { return null; } }
    }

    public MessageType MessageType => GetMessageType();

    public string MessageId
    {
        get
        {
            if (_sbMessage.MessageId != null) return _sbMessage.MessageId;
            EnsureCloudEventEvaluated();
            // A detected CloudEvent always yields a non-null id: use the CloudEvents
            // `id` when present, otherwise a clear placeholder so an invalid (id-less)
            // CloudEvent can still be dead-lettered without the getter throwing.
            if (_isCloudEvent) return _cloudEvent?.Id ?? CloudEventMissingIdPlaceholder;
            throw new InvalidMessageException($"MessageId is not defined.");
        }
    }

    public string SessionId
    {
        get
        {
            if (_sbMessage.SessionId != null) return _sbMessage.SessionId;
            EnsureCloudEventEvaluated();
            if (_isCloudEvent)
                return _cloudEventReadOptions!.MapSessionId(_cloudEvent!) ?? _cloudEvent?.Id ?? Constants.Self;
            throw new InvalidMessageException($"SessionId is not defined.");
        }
    }

    public string? CorrelationId
    {
        get
        {
            if (_sbMessage.CorrelationId != null) return _sbMessage.CorrelationId;
            EnsureCloudEventEvaluated();
            return _isCloudEvent ? _cloudEventReadOptions!.MapCorrelationId(_cloudEvent!) : null;
        }
    }

    public string? ReplyTo => _sbMessage.ReplyTo;

    public string? ReplyToSessionId => _sbMessage.ReplyToSessionId;

    public MessageContent MessageContent => GetContent() ?? throw new InvalidMessageException($"MessageContent is null.");

    public bool IsDeferred => _isDeferred;

    public DateTime EnqueuedTimeUtc => _sbMessage.EnqueuedTimeUtc;

    public int? RetryCount
    {
        get { try { return Int32.Parse(GetUserProperty(UserPropertyName.RetryCount), CultureInfo.InvariantCulture); } catch (InvalidMessageException) { return null; } catch (FormatException) { return null; } }
    }

    public string? OriginalSessionId
    {
        get { try { return GetUserProperty(UserPropertyName.OriginalSessionId); } catch (InvalidMessageException) { return null; } }
    }

    public int? DeferralSequence
    {
        get
        {
            try
            {
                var value = GetUserProperty(UserPropertyName.DeferralSequence);
                return value != null ? Int32.Parse(value, CultureInfo.InvariantCulture) : null;
            }
            catch (InvalidMessageException) { return null; }
            catch (FormatException) { return null; }
        }
    }

    public int ThrottleRetryCount
    {
        get
        {
            try
            {
                var value = _sbMessage.GetUserProperty(ThrottleRetryCountProperty);
                return value != null ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            }
            catch { return 0; }
        }
    }

    // QueueTimeMs / ProcessingTimeMs are measured during this receive — initial
    // value comes from the inbound message's user properties (when the inbound
    // is itself a response that already carries timings, e.g. on the Resolver
    // side), and the receive pipeline overwrites them with locally-measured
    // values via the setter. Plain backing fields, no SB round-trip.
    private long? _queueTimeMs;
    private long? _processingTimeMs;

    public long? QueueTimeMs
    {
        get => _queueTimeMs ?? TryReadLong(UserPropertyName.QueueTimeMs);
        set => _queueTimeMs = value;
    }

    public long? ProcessingTimeMs
    {
        get => _processingTimeMs ?? TryReadLong(UserPropertyName.ProcessingTimeMs);
        set => _processingTimeMs = value;
    }

    public DateTime? HandlerStartedAtUtc { get; set; }

    public HandlerOutcome HandlerOutcome { get; set; }

    public HandoffMetadata HandoffMetadata { get; set; } = null!;

    public Exception? HandledFailure { get; set; }

    public System.Diagnostics.ActivityContext ParentTraceContext { get; set; }

    private long? TryReadLong(UserPropertyName name)
    {
        try
        {
            var value = _sbMessage.GetUserProperty(name);
            if (value is null) return null;
            return long.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch { return null; }
    }


    /// <summary>
    /// We actually don't do anything when Abandon is called.
    /// The intention of abandoning a message is to make a retry attempt, and if we actually call IMessageSession.AbandonAsync, then the lock will be released and the message will be picked up again immediately.
    /// By doing nothing the message stays unsettled, so the broker redelivers it once the
    /// session lock is released or expires — a session lock, not a per-message one, so the
    /// pause before the retry is however long the session stays held.
    /// </summary>
    public Task Abandon(TransientException exception) => Task.CompletedTask;

    public async Task BlockSession(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        state.BlockedByEventId = EventId;
        state.BlockedByMessageId = this.GetSourceMessageIdOrDefault();
        await UpdateSessionState(state, cancellationToken);
    }

    public async Task UnblockSession(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        state.BlockedByEventId = null;
        state.BlockedByMessageId = null;
        await UpdateSessionState(state, cancellationToken);
    }

    public async Task Complete(CancellationToken cancellationToken = default)
    {
        try
        {
            await _sbSession.CompleteAsync(_sbMessage, cancellationToken);
        }
        catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            throw new TransientException("SessionLockLost exception.", e);
        }
        catch (ServiceBusException e) when (e.IsTransient)
        {
            throw new TransientException("ServiceBus SDK threw transient exception", e);
        }
    }

    private static string? FormatExceptionForDeadLetter(Exception? exception)
    {
        if (exception == null)
            return null;

        string fullText = exception.ToString();

        if (fullText.Length <= MaxDeadLetterErrorDescriptionLength)
            return fullText;

        int availableLength = MaxDeadLetterErrorDescriptionLength - TruncationIndicator.Length;
        int firstNewlineIndex = fullText.IndexOf('\n');

        if (firstNewlineIndex > 0 && firstNewlineIndex < availableLength)
        {
            string truncated = fullText.Substring(0, availableLength);
            int lastNewline = truncated.LastIndexOf('\n');
            if (lastNewline > firstNewlineIndex)
                truncated = truncated.Substring(0, lastNewline);

            return truncated + TruncationIndicator;
        }

        return fullText.Substring(0, availableLength) + TruncationIndicator;
    }

    public async Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Null when no exception is given; IServiceBusSession.DeadLetterAsync still declares it non-null.
            await _sbSession.DeadLetterAsync(_sbMessage, reason, FormatExceptionForDeadLetter(exception)!, cancellationToken);
        }
        catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            throw new TransientException("SessionLockLost exception.", e);
        }
        catch (ServiceBusException e) when (e.IsTransient)
        {
            throw new TransientException("ServiceBus SDK threw transient exception", e);
        }
    }

    public async Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return !string.IsNullOrEmpty(state.BlockedByEventId);
    }

    public async Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return !string.IsNullOrEmpty(state.BlockedByEventId);
    }

    public async Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return !string.IsNullOrEmpty(state.BlockedByEventId) && state.BlockedByEventId.Equals(EventId, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return state.BlockedByEventId!;
    }

    public async Task<string?> GetBlockedByMessageId(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return state.BlockedByMessageId;
    }

    private string GetUserProperty(UserPropertyName userPropertyName)
    {
        var raw = _sbMessage.GetUserProperty(userPropertyName);
        if (raw != null) return raw;

        var cloudEventFallback = CloudEventFallback(userPropertyName);
        if (cloudEventFallback != null) return cloudEventFallback;

        throw new InvalidMessageException($"Message.UserProperties[{userPropertyName}] is not defined.");
    }

    // Synthetic user-property values derived from an inbound CloudEvent so an
    // external CloudEvents producer (which does not stamp NimBus user.* routing
    // properties) can still flow through the native dispatch/response pipeline.
    // Returns null for native messages, so native property access is unchanged.
    private string? CloudEventFallback(UserPropertyName userPropertyName)
    {
        EnsureCloudEventEvaluated();
        if (!_isCloudEvent) return null;

        return userPropertyName switch
        {
            UserPropertyName.From => _cloudEvent?.Source ?? "external",
            UserPropertyName.To => _cloudEventReadOptions!.MapType(_cloudEvent?.Type),
            // Never null for a detected CloudEvent: a producer may omit both the
            // CloudEvents `id` and the native Service Bus MessageId. Such a message
            // is invalid and will be dead-lettered by the validating handler, but
            // the dead-letter path (MessageHandler) reads EventId while logging — a
            // null here would throw InvalidMessageException and crash the processor
            // before the message is dead-lettered (AC12). Fall back to a clear,
            // inspectable placeholder so the dead-letter completes cleanly.
            UserPropertyName.EventId => _cloudEvent?.Id ?? _sbMessage.MessageId ?? CloudEventMissingIdPlaceholder,
            UserPropertyName.MessageType => MessageType.EventRequest.ToString(),
            UserPropertyName.EventTypeId => _cloudEventReadOptions!.MapType(_cloudEvent?.Type),
            UserPropertyName.OriginatingFrom => _cloudEvent?.Source,
            _ => null,
        };
    }

    private MessageType GetMessageType()
    {
        var messageTypeString = GetUserProperty(UserPropertyName.MessageType);

        if (Enum.TryParse(messageTypeString, out MessageType messageType))
            return messageType;

        throw new InvalidMessageException($"Unable to parse MessageType from '{messageTypeString}'.");
    }

    private MessageContent? _content;
    private bool _contentLoaded;

    private CloudEvent? _cloudEvent;
    private bool _isCloudEvent;
    private bool _cloudEventEvaluated;

    // Placeholder identifier used when a detected CloudEvent carries neither a
    // CloudEvents `id` nor a native Service Bus MessageId. Such a message is
    // invalid and will be dead-lettered; the placeholder keeps identity accessors
    // non-null so the dead-letter path never crashes while logging (AC12).
    private const string CloudEventMissingIdPlaceholder = "(cloudevent-missing-id)";

    // Detects (once) whether this inbound message is a CloudEvent. Only runs when
    // CloudEvents read options are configured; native subscribers never evaluate
    // it, so their behavior is unchanged. Detection is intentionally lenient: a
    // message carrying CloudEvents markers but missing required attributes is
    // still flagged as a CloudEvent so the pipeline dead-letters it with a clear
    // reason instead of mis-parsing it as native.
    private void EnsureCloudEventEvaluated()
    {
        if (_cloudEventEvaluated) return;
        _cloudEventEvaluated = true;

        if (_cloudEventReadOptions == null) return;
        if (CloudEventsServiceBusBinding.TryParse(_sbMessage, _cloudEventReadOptions, out var parsed))
        {
            _isCloudEvent = true;
            _cloudEvent = parsed;
        }
    }

    /// <inheritdoc/>
    public CloudEvent? GetCloudEvent()
    {
        EnsureCloudEventEvaluated();
        return _cloudEvent;
    }

    // CloudEvents identity carried through to the Resolver. Two sources: a response
    // message received by the Resolver reads the stamped user.CloudEvent* property;
    // the original inbound CloudEvent (subscriber side) reads the parsed CloudEvent.
    // Returns null for a native message, so tracking of native events is unchanged.
    public string? CloudEventId => CloudEventAttribute(UserPropertyName.CloudEventId, ce => ce.Id);
    public string? CloudEventSource => CloudEventAttribute(UserPropertyName.CloudEventSource, ce => ce.Source);
    public string? CloudEventType => CloudEventAttribute(UserPropertyName.CloudEventType, ce => ce.Type);
    public string? CloudEventSubject => CloudEventAttribute(UserPropertyName.CloudEventSubject, ce => ce.Subject);

    private string? CloudEventAttribute(UserPropertyName property, Func<CloudEvent, string?> fromCloudEvent)
    {
        var stamped = _sbMessage.GetUserProperty(property);
        if (stamped != null) return stamped;
        EnsureCloudEventEvaluated();
        return _cloudEvent != null ? fromCloudEvent(_cloudEvent) : null;
    }

    // MessageContext is constructed per received message and used single-threaded,
    // so the deserialized body is memoized to avoid re-decoding + re-deserializing
    // the entire body on every access (it is read 3-4x on the hot path). A separate
    // "loaded" flag guards null and invalid payloads from re-running each call.
    private MessageContent? GetContent()
    {
        if (!_contentLoaded)
        {
            // Record the attempt before parsing so an invalid body cannot trigger
            // repeated decoding, parsing, and exceptions on failure paths.
            _contentLoaded = true;
            EnsureCloudEventEvaluated();
            if (_isCloudEvent)
            {
                // Normalize the CloudEvent into the internal MessageContent envelope
                // so the existing EventTypeId-keyed dispatch and EventJson handler
                // deserialization work unchanged.
                _content = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = _cloudEventReadOptions!.MapType(_cloudEvent?.Type)!,
                        EventJson = _cloudEvent?.Data,
                    },
                };
            }
            else
            {
                try
                {
                    _content = JsonConvert.DeserializeObject<MessageContent>(
                        Encoding.UTF8.GetString(_sbMessage.Body),
                        Core.Messages.Constants.CreateSafeJsonSettings());
                }
                catch (JsonException)
                {
                    // EventTypeId is read by lifecycle and dead-letter paths. Treat
                    // a foreign or malformed body as missing content so those paths
                    // can reject the message without a property getter throwing.
                    _content = null;
                }
            }
        }

        return _content;
    }


    private async Task UpdateSessionState(SessionState sessionState, CancellationToken cancellationToken = default)
    {
        try
        {
            await _sbSession.SetStateAsync(sessionState, cancellationToken);
        }
        catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            throw new TransientException("SessionLockLost exception.", e);
        }
        catch (ServiceBusException e) when (e.IsTransient)
        {
            throw new TransientException("ServiceBus SDK threw transient exception", e);
        }
    }

    private async Task<SessionState> GetSessionState(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _sbSession.GetStateAsync(cancellationToken);
        }
        catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.SessionLockLost)
        {
            throw new TransientException("SessionLockLost exception.", e);
        }
        catch (ServiceBusException e) when (e.IsTransient)
        {
            throw new TransientException("ServiceBus SDK threw transient exception", e);
        }
    }

    public async Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        int sequence = state.NextDeferralSequence;
        state.NextDeferralSequence++;
        await UpdateSessionState(state, cancellationToken);
        return sequence;
    }

    public async Task IncrementDeferredCount(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        state.DeferredCount++;
        await UpdateSessionState(state, cancellationToken);
    }

    public async Task DecrementDeferredCount(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        if (state.DeferredCount > 0)
        {
            state.DeferredCount--;
            await UpdateSessionState(state, cancellationToken);
        }
    }

    public async Task<int> GetDeferredCount(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return state.DeferredCount;
    }

    public async Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        return state.HasDeferredMessages();
    }

    public async Task ResetDeferredCount(CancellationToken cancellationToken = default)
    {
        SessionState state = await GetSessionState(cancellationToken);
        state.DeferredCount = 0;
        await UpdateSessionState(state, cancellationToken);
    }

    public async Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default)
    {
        // Create new message with same content
        var receivedMessage = _sbMessage.Message;
        var newMessage = new Azure.Messaging.ServiceBus.ServiceBusMessage(_sbMessage.Body)
        {
            SessionId = SessionId,
            CorrelationId = CorrelationId,
            // The copy keeps the original MessageId (Spec 030 §5.7). The scheduled copy lands
            // behind whatever is already queued in the session, so it can arrive after the
            // outcome it belongs to; the store's stale-write guard recognises it as an
            // already-answered message only if it still carries its own id. It also makes the
            // per-message history write idempotent, so a throttle chain leaves one history
            // document instead of one per round.
            //
            // Precondition: the receiving topic must not have RequiresDuplicateDetection
            // enabled, or the broker silently drops the copy inside the detection window.
            // The provisioner never enables it; brownfield namespaces must be checked.
            MessageId = receivedMessage.MessageId,
            // Copy standard Service Bus properties
            ContentType = receivedMessage.ContentType,
            Subject = receivedMessage.Subject,
            To = receivedMessage.To,
            ReplyTo = receivedMessage.ReplyTo,
            ReplyToSessionId = receivedMessage.ReplyToSessionId,
        };

        // Setting SessionId establishes the outgoing partition key. Reassigning a
        // missing inbound PartitionKey would violate the SDK's session invariant.
        if (!string.IsNullOrEmpty(receivedMessage.PartitionKey))
            newMessage.PartitionKey = receivedMessage.PartitionKey;

        if (!string.IsNullOrEmpty(receivedMessage.TransactionPartitionKey))
            newMessage.TransactionPartitionKey = receivedMessage.TransactionPartitionKey;

        if (receivedMessage.TimeToLive > TimeSpan.Zero)
            newMessage.TimeToLive = receivedMessage.TimeToLive;

        // Copy all application properties from original message
        foreach (var prop in receivedMessage.ApplicationProperties)
        {
            newMessage.ApplicationProperties[prop.Key] = prop.Value;
        }

        // Set/update throttle retry count
        newMessage.ApplicationProperties[ThrottleRetryCountProperty] = throttleRetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Schedule for future delivery
        var scheduledTime = DateTimeOffset.UtcNow.Add(delay);
        try
        {
            await _sbSession.SendScheduledMessageAsync(newMessage, scheduledTime, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // SendScheduledMessageAsync requires ServiceBusClient + entityPath which may not be available
            // in all handler configurations (e.g., ServiceBusSessionReceiver path).
            // Fall back to letting the message retry via lock expiration - don't complete it.
            // The message stays unsettled, so the broker redelivers it once the session lock
            // is released or expires. That is a session lock, not a per-message one, and the
            // SDK renews it while the invocation runs, so the retry cadence is governed by
            // session release rather than by any fixed interval.
            throw new Core.Messages.Exceptions.TransientException(
                "Scheduled redelivery not available in current configuration. Message will retry after lock expiration.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The broker refused the copy (busy, entity disabled, quota, missing Send claim). The
            // original is still unsettled, so surface it as transient: the caller then lets the
            // broker redeliver the original instead of the failure escaping its handler and
            // ending as MaxDeliveryCountExceeded. Cancellation still propagates for shutdown.
            throw new Core.Messages.Exceptions.TransientException(
                "Could not schedule the redelivery copy. Message will retry after lock expiration.", ex);
        }

        // Complete original message only after successful scheduling. Completion
        // failures must retain their own semantics rather than masquerading as a
        // scheduling-configuration failure.
        await Complete(cancellationToken);
    }
}
