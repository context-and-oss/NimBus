using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.Core;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

public interface IAdminService
{
    Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform);
    Task<TopologyAuditResult> AuditTopologyAsync(string endpointName);
    Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName);
    Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId);
    Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId);
    Task<int> GetDeadLetteredCountAsync(string endpointId);
    Task<long?> GetDeadLetterQueueCountAsync(string endpointId);
    Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId);
    Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId);
    Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId);
    Task<bool> DeleteEventAsync(string endpointId, string eventId);

    // Advanced operations
    Task<PurgePreview> PurgeSubscriptionPreviewAsync(string endpointId, string subscription, List<string> states, System.DateTime? before);
    Task<BulkOperationResult> PurgeSubscriptionAsync(string endpointId, string subscription, List<string> states, System.DateTime? before);
    Task<int> DeleteMessagesByToPreviewAsync(string toField);
    Task<BulkOperationResult> DeleteMessagesByToAsync(string toField);
    Task<int> DeleteByStatusPreviewAsync(string endpointId, List<string> statuses);
    Task<BulkOperationResult> DeleteByStatusAsync(string endpointId, List<string> statuses);
    Task<int> SkipMessagesPreviewAsync(string endpointId, List<string> statuses, System.DateTime? before);
    Task<BulkOperationResult> SkipMessagesAsync(string endpointId, List<string> statuses, System.DateTime? before);
    Task<CopyResult> CopyEndpointDataAsync(string endpointId, string targetConnectionString, System.DateTime? from, System.DateTime? to, List<string> statuses, int? batchSize);
    Task<DeferredReprocessResult> ReprocessDeferredAsync(string endpointId, string sessionId);
    /// <summary>
    /// Wipes an endpoint: deletes and re-provisions its own and its Deferred Service Bus
    /// subscriptions (discarding their backlog), purges every stored event, and deletes the
    /// endpoint's stored message history. Each of those four steps counts once in the result.
    /// </summary>
    Task<BulkOperationResult> DeleteAllEventsAsync(string endpointId);
    Task<StalePendingPreview> PreviewStalePendingAsync(string endpointId, System.DateTime? enqueuedBefore, int maxRows);
    Task<StalePendingReconcileResult> ReconcileStalePendingAsync(string endpointId, System.DateTime enqueuedBefore, int? maxRepairs, string auditorName, string? note);
}
