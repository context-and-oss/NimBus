#pragma warning disable CA1707, CA2007

using System;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Seeds the tracked row an operator command acts on. Resubmit and skip run only while the
/// endpoint's row is visible and its latest message is the one the caller loaded (Spec 035
/// Phase 2a), so tests that drive those paths need the row as well as the message history.
/// </summary>
internal static class OperatorCommandTestRows
{
    /// <summary>
    /// Seeds a Failed row whose latest message is <paramref name="messageId"/>, on the endpoint a
    /// command for that stored message goes to.
    /// </summary>
    public static async Task<UnresolvedEvent> SeedRowForMessageAsync(IMessageTrackingStore store, string eventId, string messageId)
    {
        var message = await store.GetMessage(eventId, messageId)
            ?? throw new InvalidOperationException($"Store message {messageId} before seeding its row.");
        return await SeedFailedRowAsync(store, OperatorCommandCoordinator.TargetEndpoint(message)!, eventId,
            message.SessionId!, messageId);
    }

    public static async Task<UnresolvedEvent> SeedFailedRowAsync(
        IMessageTrackingStore store,
        string endpointId,
        string eventId,
        string sessionId,
        string lastMessageId,
        ResolutionStatus status = ResolutionStatus.Failed)
    {
        var row = new UnresolvedEvent
        {
            EventId = eventId,
            SessionId = sessionId,
            EndpointId = endpointId,
            LastMessageId = lastMessageId,
            MessageType = MessageType.ErrorResponse,
            ResolutionStatus = status,
            UpdatedAt = DateTime.UtcNow,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        };

        var written = status switch
        {
            ResolutionStatus.Failed => await store.UploadFailedMessage(eventId, sessionId, endpointId, row),
            ResolutionStatus.DeadLettered => await store.UploadDeadletteredMessage(eventId, sessionId, endpointId, row),
            ResolutionStatus.Unsupported => await store.UploadUnsupportedMessage(eventId, sessionId, endpointId, row),
            ResolutionStatus.Deferred => await store.UploadDeferredMessage(eventId, sessionId, endpointId, row),
            ResolutionStatus.Pending => await store.UploadPendingMessage(eventId, sessionId, endpointId, row),
            ResolutionStatus.Completed => await store.UploadCompletedMessage(eventId, sessionId, endpointId, row),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
        if (!written)
            throw new InvalidOperationException($"Seeding the {status} row for {eventId} was refused.");

        return (await store.GetEvent(endpointId, eventId))!;
    }
}
