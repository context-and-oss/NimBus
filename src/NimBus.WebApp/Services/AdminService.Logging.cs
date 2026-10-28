using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Services;

// Source-generated LoggerMessage delegates for the AdminService class.
// All AdminService log calls funnel through these partial methods so the
// formatters are built once (CA1848) and the CI output stays clean.
// EventIds are stable; appended only at the end when adding new messages.
public partial class AdminService
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed message not found for event {EventId} on {EndpointId}, skipping")]
    private partial void LogFailedMessageNotFound(string eventId, string endpointId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "No event content for event {EventId}, skipping")]
    private partial void LogNoEventContent(string eventId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Failed to resubmit event {EventId}")]
    private partial void LogResubmitFailed(System.Exception ex, string eventId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Failed to delete dead-lettered event {EventId}")]
    private partial void LogDeleteDeadLetteredFailed(System.Exception ex, string eventId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Failed to clear session state for {SessionId} on {EndpointId}")]
    private partial void LogClearSessionStateFailedForEndpoint(System.Exception ex, string sessionId, string endpointId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Failed to send ProcessDeferredRequest for {SessionId} on {EndpointId}")]
    private partial void LogSendProcessDeferredRequestFailed(System.Exception ex, string sessionId, string endpointId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Failed to delete rule {Rule} on subscription {Subscription}")]
    private partial void LogDeleteRuleFailed(System.Exception ex, string rule, string subscription);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Failed to delete subscription {Subscription}")]
    private partial void LogDeleteSubscriptionFailed(System.Exception ex, string subscription);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning,
        Message = "Could not accept SB session {SessionId} on {EndpointId}")]
    private partial void LogAcceptSessionFailed(System.Exception ex, string sessionId, string endpointId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Warning,
        Message = "Failed to complete active message {MessageId}")]
    private partial void LogCompleteActiveMessageFailed(System.Exception ex, string messageId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
        Message = "Error removing active messages from session {SessionId}")]
    private partial void LogRemoveActiveMessagesError(System.Exception ex, string sessionId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning,
        Message = "Failed to complete deferred message {SequenceNumber}")]
    private partial void LogCompleteDeferredMessageFailed(System.Exception ex, long sequenceNumber);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "Error removing deferred messages from session {SessionId}")]
    private partial void LogRemoveDeferredMessagesError(System.Exception ex, string sessionId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning,
        Message = "Failed to clear session state for {SessionId}")]
    private partial void LogClearSessionStateFailed(System.Exception ex, string sessionId);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning,
        Message = "Error removing messages from Deferred subscription for session {SessionId}")]
    private partial void LogRemoveDeferredSubscriptionError(System.Exception ex, string sessionId);

    [LoggerMessage(EventId = 16, Level = LogLevel.Warning,
        Message = "Failed to remove Cosmos event {EventId}")]
    private partial void LogRemoveCosmosEventFailed(System.Exception ex, string eventId);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning,
        Message = "Error removing Cosmos events for session {SessionId}")]
    private partial void LogRemoveCosmosEventsError(System.Exception ex, string sessionId);

    [LoggerMessage(EventId = 18, Level = LogLevel.Warning,
        Message = "Failed to delete all events for {EndpointId}")]
    private partial void LogDeleteAllEventsFailed(System.Exception ex, string endpointId);

    [LoggerMessage(EventId = 19, Level = LogLevel.Warning,
        Message = "Failed to reconcile stale Pending event {EventId} on {EndpointId}")]
    private partial void LogStalePendingRepairFailed(System.Exception ex, string eventId, string endpointId);

    [LoggerMessage(EventId = 20, Level = LogLevel.Error,
        Message = "Repaired stale Pending event {EventId} on {EndpointId} but failed to write its audit row")]
    private partial void LogStalePendingAuditFailed(System.Exception ex, string eventId, string endpointId);

    [LoggerMessage(EventId = 21, Level = LogLevel.Warning,
        Message = "Bulk skip of event {EventId} on {EndpointId} failed")]
    private partial void LogBulkSkipFailed(System.Exception ex, string eventId, string endpointId);
}
