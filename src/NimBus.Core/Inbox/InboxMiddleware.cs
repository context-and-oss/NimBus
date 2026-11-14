using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.Inbox;

/// <summary>
/// Decorates event dispatch with record-on-success inbox deduplication.
/// </summary>
/// <remarks>
/// This component decorates <see cref="IEventContextHandler"/> rather than
/// <see cref="IMessagePipelineBehavior"/> because the generic message-pipeline terminal publishes
/// the Resolver response and settles the broker message. Inbox recording must complete, or fail,
/// before both operations so a record failure leaves the message available for retry.
/// </remarks>
public sealed partial class InboxMiddleware : IEventContextHandler
{
    /// <summary>The maximum message-id length supported by inbox providers.</summary>
    public const int MaximumMessageIdLength = InboxDuplicateDetector.MaximumMessageIdLength;

    /// <summary>The stable Resolver reason used for duplicate skips.</summary>
    public const string DuplicateReason = "DuplicateDetected";

    private const string RecordOperation = "record";
    private readonly IEventContextHandler _inner;
    private readonly IInboxStore _inboxStore;
    private readonly InboxDuplicateDetector _duplicateDetector;
    private readonly bool _checkHandledUpstream;
    private readonly ILogger<InboxMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxMiddleware"/> class.
    /// </summary>
    /// <param name="inner">The real event context handler.</param>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="lifecycleNotifier">The optional lifecycle notifier.</param>
    /// <param name="logger">The optional structured logger.</param>
    /// <param name="checkHandledUpstream">
    /// When <see langword="true"/>, the pre-dispatch duplicate check is skipped because the
    /// hosting composition already runs it (via the <see cref="InboxDuplicateDetector"/> handed
    /// to <c>StrictMessageHandler</c>) before this decorator is reached, and the middleware only
    /// records successes. This keeps a fresh delivery at exactly one store check and one record.
    /// </param>
    /// <param name="endpointScope">
    /// The configured subscriber endpoint used as the stable deduplication scope. When
    /// <see langword="null"/>, the scope falls back to the message's <c>To</c> address, which
    /// for external CloudEvents is only the mapped event type — pass the endpoint whenever the
    /// composition knows it.
    /// </param>
    public InboxMiddleware(
        IEventContextHandler inner,
        IInboxStore inboxStore,
        MessageLifecycleNotifier? lifecycleNotifier = null,
        ILogger<InboxMiddleware>? logger = null,
        bool checkHandledUpstream = false,
        string? endpointScope = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _logger = logger ?? NullLogger<InboxMiddleware>.Instance;
        _duplicateDetector = new InboxDuplicateDetector(inboxStore, lifecycleNotifier, _logger, endpointScope);
        _checkHandledUpstream = checkHandledUpstream;
    }

    /// <inheritdoc />
    public async Task Handle(
        IMessageContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = _duplicateDetector.GetIdentityOrBypass(context);
        if (identity is null)
        {
            await _inner.Handle(context, cancellationToken);
            return;
        }

        if (!_checkHandledUpstream
            && await _duplicateDetector.IsDuplicateAsync(context, cancellationToken))
        {
            return;
        }

        await _inner.Handle(context, cancellationToken);

        if (context.HandlerOutcome != HandlerOutcome.Default)
        {
            // A pending handoff is not durable yet: the PendingHandoffResponse and the session
            // block still happen downstream, and both must be recreatable when a crash forces
            // redelivery. Recording here would turn that redelivery into a duplicate skip that
            // never re-establishes the pending state, so only plain successes are recorded.
            LogInboxRecordSkippedForHandlerOutcome(_logger, context.HandlerOutcome, identity.Value.EndpointId, context.EventTypeId, identity.Value.MessageId);
            return;
        }

        // A RetryRequest, a replayed parked copy and a resubmission reach the endpoint with a
        // fresh MessageId, so their success is also recorded under the MessageId the source
        // delivered the event with. That one goes first: a failure between the two records
        // must not leave the stand-in's own MessageId recorded without it, or the broker's
        // redelivery of the stand-in would be skipped as a duplicate and never record it.
        //
        // The handler has succeeded, so the records run to the end without observing the
        // caller's token: it is the processor's, cancelled when the receiver stops (for example
        // when the endpoint circuit opens), and an unrecorded success runs the handler again on
        // redelivery. StrictMessageHandler settles the delivery the same way.
        var standsInFor = await GetDeliveryStoodInForAsync(context, identity.Value.MessageId);
        try
        {
            if (standsInFor is not null)
            {
                await _inboxStore.RecordProcessedAsync(
                    identity.Value.EndpointId,
                    standsInFor,
                    CancellationToken.None);
            }

            await _inboxStore.RecordProcessedAsync(
                identity.Value.EndpointId,
                identity.Value.MessageId,
                CancellationToken.None);
        }
        catch (Exception)
        {
            throw _duplicateDetector.CreateStoreException(RecordOperation);
        }
    }

    // A RetryRequest or a parked copy carries the source MessageId. A resubmission is built
    // by the Manager and carries none, but it resolves the event that blocks its session,
    // and the session stored the blocking delivery's source MessageId. That read is best
    // effort: the handler already succeeded, so a failure must not turn into a handler
    // failure; the resubmission then records only its own MessageId, as before.
    private async Task<string?> GetDeliveryStoodInForAsync(
        IMessageContext context,
        string messageId)
    {
        var carried = InboxDuplicateDetector.GetStandInMessageId(context, messageId);
        if (carried is not null || !IsResubmission(context))
            return carried;

        try
        {
            if (!await context.IsSessionBlockedByThis(CancellationToken.None))
                return null;

            var blockedBy = await context.GetBlockedByMessageId(CancellationToken.None);
            return string.IsNullOrWhiteSpace(blockedBy)
                || blockedBy.Length > MaximumMessageIdLength
                || string.Equals(blockedBy, messageId, StringComparison.Ordinal)
                    ? null
                    : blockedBy;
        }
        catch (Exception exception)
        {
            LogCouldNotReadTheDelivery(_logger, exception.GetType().Name, context.GetEndpointIdOrDefault(), context.EventTypeId, messageId);
            return null;
        }
    }

    private static bool IsResubmission(IMessageContext context)
    {
        try { return context.MessageType == MessageType.ResubmissionRequest; }
        catch (InvalidMessageException) { return false; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox record skipped for handler outcome {HandlerOutcome}. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}, MessageId:{MessageId}")]
    private static partial void LogInboxRecordSkippedForHandlerOutcome(ILogger logger, HandlerOutcome handlerOutcome, string endpointId, string eventTypeId, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the delivery a resubmission resolves ({ExceptionType}); only its own MessageId is recorded. EndpointId:{EndpointId}, EventTypeId:{EventTypeId}, MessageId:{MessageId}")]
    private static partial void LogCouldNotReadTheDelivery(ILogger logger, string exceptionType, string? endpointId, string eventTypeId, string messageId);
}
