using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.Inbox;

/// <summary>
/// Consults the inbox store for previously processed messages and emits the duplicate-skip
/// signals (handler outcome, telemetry, structured log, lifecycle notification).
/// </summary>
/// <remarks>
/// The check is shared by two call sites: <see cref="InboxMiddleware"/> at the handler seam
/// (standalone compositions only), and <c>StrictMessageHandler</c> before its session-state
/// guards — so a redelivered duplicate whose session state has already moved on (for example a
/// successfully handled RetryRequest that unblocked its session) is still surfaced as a
/// duplicate rather than falling into a state-guard branch that hides it. Hosted compositions
/// run the check exclusively in <c>StrictMessageHandler</c> and configure the middleware as
/// record-only, keeping a fresh delivery at one check plus one record.
/// </remarks>
public sealed partial class InboxDuplicateDetector
{
    /// <summary>The maximum message-id length supported by inbox providers.</summary>
    public const int MaximumMessageIdLength = 512;

    /// <summary>The maximum endpoint-id length supported by inbox providers.</summary>
    public const int MaximumEndpointIdLength = 260;

    private const string CheckOperation = "check";
    private readonly IInboxStore _inboxStore;
    private readonly MessageLifecycleNotifier? _lifecycleNotifier;
    private readonly ILogger _logger;
    private readonly string? _endpointScope;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxDuplicateDetector"/> class.
    /// </summary>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="lifecycleNotifier">The optional lifecycle notifier.</param>
    /// <param name="logger">The optional structured logger.</param>
    /// <param name="endpointScope">
    /// The configured subscriber endpoint used as the stable deduplication scope. When
    /// <see langword="null"/>, the scope falls back to the message's <c>To</c> address —
    /// acceptable only for native NimBus messages, because an external CloudEvent carries no
    /// <c>user.To</c> and the transport substitutes the mapped event type, which is neither
    /// unique per endpoint nor stable across endpoints sharing one store.
    /// </param>
    public InboxDuplicateDetector(
        IInboxStore inboxStore,
        MessageLifecycleNotifier? lifecycleNotifier = null,
        ILogger? logger = null,
        string? endpointScope = null)
    {
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _lifecycleNotifier = lifecycleNotifier;
        _logger = logger ?? NullLogger.Instance;
        _endpointScope = string.IsNullOrWhiteSpace(endpointScope) ? null : endpointScope;
    }

    /// <summary>
    /// Determines whether the message was already processed successfully on this endpoint.
    /// When it was, sets <see cref="IMessageContext.HandlerOutcome"/> to
    /// <see cref="HandlerOutcome.DuplicateDetected"/> and emits the duplicate-skip signals.
    /// A message without a usable deduplication identity is never treated as a duplicate.
    /// A replayed parked copy is also a duplicate when the delivery it was parked from
    /// (its <see cref="IMessage.InboxMessageId"/>) was processed. RetryRequests and
    /// resubmissions are checked against their own MessageId only: a retry of an event that
    /// completed another way is answered by the session guard, and a resubmission is a
    /// deliberate new attempt.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns><see langword="true"/> when the message is a previously processed duplicate.</returns>
    /// <exception cref="InboxStoreException">The store check failed; the message must be retried.</exception>
    public async Task<bool> IsDuplicateAsync(
        IMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = GetIdentityOrBypass(context);
        if (identity is null)
            return false;

        var (endpointId, messageId) = identity.Value;
        var parkedFrom = IsEventRequest(context) ? GetStandInMessageId(context, messageId) : null;
        bool hasProcessed;
        try
        {
            hasProcessed = await _inboxStore.HasProcessedAsync(endpointId, messageId, cancellationToken)
                || (parkedFrom is not null
                    && await _inboxStore.HasProcessedAsync(endpointId, parkedFrom, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw CreateStoreException(CheckOperation);
        }

        if (!hasProcessed)
            return false;

        context.HandlerOutcome = HandlerOutcome.DuplicateDetected;
        NimBusMeters.InboxDuplicatesDetected.Add(1);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var eventId = context.GetEventIdOrDefault();
            var sessionId = context.GetSessionIdOrDefault();
            LogInboxDuplicateDetected(_logger, endpointId, context.EventTypeId, eventId, messageId, parkedFrom, sessionId);
        }
        await NotifyDuplicateDetectedBestEffort(context, cancellationToken);
        return true;
    }

    /// <summary>
    /// Gets the MessageId of the delivery a stand-in (a RetryRequest or a parked copy) carries
    /// as its <see cref="IMessage.InboxMessageId"/>, or <see langword="null"/> when it carries
    /// none, carries its own MessageId, or carries one longer than the supported maximum.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <param name="messageId">The message's own deduplication MessageId.</param>
    /// <returns>The carried MessageId, or <see langword="null"/>.</returns>
    internal static string? GetStandInMessageId(IMessageContext context, string messageId)
    {
        var inboxMessageId = context.GetInboxMessageIdOrDefault();
        return inboxMessageId is null
            || inboxMessageId.Length > MaximumMessageIdLength
            || string.Equals(inboxMessageId, messageId, StringComparison.Ordinal)
                ? null
                : inboxMessageId;
    }

    private static bool IsEventRequest(IMessageContext context)
    {
        try { return context.MessageType == MessageType.EventRequest; }
        catch (InvalidMessageException) { return false; }
    }

    /// <summary>
    /// Resolves the (endpoint, message) deduplication identity, or <see langword="null"/> when
    /// the message cannot participate in deduplication. The endpoint side prefers the
    /// configured subscriber endpoint over the message's <c>To</c> address, which for external
    /// CloudEvents is only the mapped event type. Identity is read through the non-throwing
    /// accessors — a real Service Bus context throws on a missing MessageId, and a message
    /// without one must bypass deduplication and still reach the handler.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The deduplication identity, or <see langword="null"/> to bypass the inbox.</returns>
    internal (string EndpointId, string MessageId)? GetIdentityOrBypass(IMessageContext context)
    {
        var messageId = context.GetMessageIdOrDefault();
        var endpointId = _endpointScope ?? context.GetEndpointIdOrDefault();
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(endpointId))
        {
            LogInboxDeduplicationBypassedBecauseMessageId(_logger, endpointId, context.EventTypeId);
            return null;
        }

        if (messageId.Length > MaximumMessageIdLength || endpointId.Length > MaximumEndpointIdLength)
        {
            LogInboxDeduplicationBypassedBecauseMessageIdLength(_logger, messageId.Length, endpointId.Length, MaximumMessageIdLength, MaximumEndpointIdLength, endpointId, context.EventTypeId);
            return null;
        }

        return (endpointId, messageId);
    }

    /// <summary>
    /// Records the store failure signals and returns the sanitized transient exception.
    /// Provider details are intentionally dropped so they cannot leak through settlement paths.
    /// </summary>
    /// <param name="operation">The bounded operation tag (<c>check</c> or <c>record</c>).</param>
    /// <returns>The sanitized exception to throw.</returns>
    internal InboxStoreException CreateStoreException(string operation)
    {
        NimBusMeters.InboxOperationFailed.Add(
            1,
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreOperation, operation));
        LogInboxStoreOperationFailed(_logger, operation);
        return new InboxStoreException();
    }

    private async Task NotifyDuplicateDetectedBestEffort(
        IMessageContext context,
        CancellationToken cancellationToken)
    {
        if (_lifecycleNotifier?.HasObservers != true)
            return;

        try
        {
            await _lifecycleNotifier.NotifyDuplicateDetected(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogADuplicateLifecycleObserverFailed(_logger, context.GetEndpointIdOrDefault(), context.EventTypeId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox duplicate detected. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, InboxMessageId:{InboxMessageId}, SessionId:{SessionId}")]
    private static partial void LogInboxDuplicateDetected(ILogger logger, string endpointId, string eventTypeId, string? eventId, string messageId, string? inboxMessageId, string? sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox deduplication bypassed because MessageId or endpoint identity is missing or blank. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}")]
    private static partial void LogInboxDeduplicationBypassedBecauseMessageId(ILogger logger, string? endpointId, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox deduplication bypassed because MessageId length {MessageIdLength} or endpoint length {EndpointIdLength} exceeds the supported maximum ({MaximumMessageIdLength}/{MaximumEndpointIdLength}). EndpointId:{EndpointId}, EventTypeId:{EventTypeId}")]
    private static partial void LogInboxDeduplicationBypassedBecauseMessageIdLength(ILogger logger, int messageIdLength, int endpointIdLength, int maximumMessageIdLength, int maximumEndpointIdLength, string endpointId, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Inbox store operation {InboxOperation} failed; the message will be retried.")]
    private static partial void LogInboxStoreOperationFailed(ILogger logger, string inboxOperation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A duplicate lifecycle observer failed. Duplicate processing will continue. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}")]
    private static partial void LogADuplicateLifecycleObserverFailed(ILogger logger, string? endpointId, string eventTypeId);
}
