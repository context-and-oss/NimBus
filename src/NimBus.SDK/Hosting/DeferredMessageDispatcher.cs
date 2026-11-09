using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Settlement intent returned by <see cref="DeferredMessageDispatcher.ProcessAsync"/>.
/// The caller (Worker BackgroundService or Functions trigger) maps this to
/// its native settlement API (<c>ProcessMessageEventArgs.CompleteMessageAsync</c>
/// / <c>ServiceBusMessageActions.CompleteMessageAsync</c>).
/// </summary>
public enum DeferredMessageDispatchAction
{
    /// <summary>The trigger message should be completed.</summary>
    Complete,

    /// <summary>The trigger message should be dead-lettered with the supplied reason.</summary>
    DeadLetter,
}

/// <summary>
/// Result of dispatching a deferred-processor trigger. The dispatcher returns
/// this rather than calling settlement APIs itself so the body has zero
/// coupling to the host SDK in use (Worker vs Azure Functions isolated).
/// </summary>
public readonly record struct DeferredMessageDispatchOutcome(
    DeferredMessageDispatchAction Action,
    string? DeadLetterReason);

/// <summary>
/// Host-agnostic body that turns a non-session trigger message on the
/// <c>deferredprocessor</c> subscription into a call into
/// <see cref="IDeferredMessageProcessor.ProcessDeferredMessagesAsync"/>.
///
/// <para>Both <see cref="DeferredMessageProcessorHostedService"/> (Worker
/// hosting) and the Azure Functions trigger class in
/// <c>samples/CrmErpDemo/Erp.Adapter.Functions</c> call this — there is no
/// other place where SessionId extraction or
/// <see cref="ServiceBusFailureReason.SessionCannotBeLocked"/> handling
/// should live.</para>
/// </summary>
public static class DeferredMessageDispatcher
{
    /// <summary>
    /// Extracts the session id from the trigger message and invokes the
    /// deferred-message processor for it. Returns an outcome the caller
    /// uses to settle the trigger message.
    /// </summary>
    /// <param name="triggerMessage">The non-session trigger that landed on the <c>deferredprocessor</c> subscription. Its <see cref="ServiceBusReceivedMessage.SessionId"/> (preferred) or its <c>"SessionId"</c> application property (tolerance fallback) names the session to drain on the <c>Deferred</c> parking subscription.</param>
    /// <param name="processor">The drain. The dispatcher does not own this — it's resolved from DI by the caller (the Worker BackgroundService) or from the Functions DI container.</param>
    /// <param name="topicName">The endpoint topic the deferred messages must be re-published to. Matches the endpoint name passed to <c>AddNimBusSubscriber</c>.</param>
    /// <param name="cancellationToken">Cancellation propagated from the host. The dispatcher hands it to the processor; on shutdown the processor may throw <see cref="OperationCanceledException"/>, which propagates to the caller (the caller is responsible for distinguishing shutdown from failure).</param>
    public static async Task<DeferredMessageDispatchOutcome> ProcessAsync(
        ServiceBusReceivedMessage triggerMessage,
        IDeferredMessageProcessor processor,
        string topicName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(triggerMessage);
        ArgumentNullException.ThrowIfNull(processor);
        if (string.IsNullOrEmpty(topicName)) throw new ArgumentException("Topic name is required.", nameof(topicName));

        // Precedence: prefer message.SessionId; fall back to a "SessionId"
        // application property only if the broker-level SessionId is absent.
        // The normal path is broker-level: NimBus's wire formatter writes
        // ServiceBusMessage.SessionId from the inbound message and does NOT
        // emit a "SessionId" application property
        // (src/NimBus.ServiceBus/MessageHelper.cs:92). The application-property
        // branch is therefore *tolerance only* — covering hand-published
        // triggers, third-party producers, or future format changes.
        var sessionId = triggerMessage.SessionId;
        if (string.IsNullOrEmpty(sessionId)
            && triggerMessage.ApplicationProperties.TryGetValue("SessionId", out var sid))
        {
            sessionId = sid?.ToString();
        }

        if (string.IsNullOrEmpty(sessionId))
            return new DeferredMessageDispatchOutcome(DeferredMessageDispatchAction.DeadLetter, "No SessionId");

        try
        {
            await processor.ProcessDeferredMessagesAsync(sessionId, topicName, cancellationToken).ConfigureAwait(false);
            return new DeferredMessageDispatchOutcome(DeferredMessageDispatchAction.Complete, null);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No messages parked for this session right now — graceful no-op.
            // The trigger is still completed so we don't redeliver indefinitely.
            return new DeferredMessageDispatchOutcome(DeferredMessageDispatchAction.Complete, null);
        }
    }
}
