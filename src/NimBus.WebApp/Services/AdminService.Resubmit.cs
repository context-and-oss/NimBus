using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.WebApp.ManagementApi;
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.WebApp.Services;

// Resubmit / dead-letter recovery / deferred reprocessing — operations that
// move messages out of a Failed / DeadLettered state and back into the
// processing pipeline. Read-only previews live next to their executing twins.
public partial class AdminService
{
    public async Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-AgeThresholdMinutes);
        int totalFailed = 0;
        int eligible = 0;
        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { MessageStore.ResolutionStatus.Failed.ToString() }
                },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                totalFailed++;
                if (ev.UpdatedAt < cutoff)
                    eligible++;
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkResubmitPreview
        {
            TotalFailedCount = totalFailed,
            EligibleCount = eligible,
            AgeThresholdMinutes = AgeThresholdMinutes
        };
    }

    public async Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-AgeThresholdMinutes);
        int processed = 0;
        int succeeded = 0;
        int failed = 0;
        var errors = new ConcurrentBag<string>();
        string? continuationToken = string.Empty;

        // Per-event work (GetFailedMessage + Resubmit + ArchiveFailedEvent) is
        // independent across distinct EventIds within a page. Parallelize with
        // a small concurrency cap so Cosmos + Service Bus don't see a thundering
        // herd. Pages still drain serially via the continuation token.
        const int ResubmitConcurrency = 5;
        using var gate = new SemaphoreSlim(ResubmitConcurrency);

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { MessageStore.ResolutionStatus.Failed.ToString() }
                },
                continuationToken, PageSize);

            var eligible = response.Events.Where(ev => ev.UpdatedAt < cutoff).ToList();
            Interlocked.Add(ref processed, eligible.Count);

            await Task.WhenAll(eligible.Select(async ev =>
            {
                await gate.WaitAsync();
                try
                {
                    var failedMessage = await _messageStore.GetFailedMessage(ev.EventId, endpointId);
                    if (failedMessage == null)
                    {
                        LogFailedMessageNotFound(ev.EventId, endpointId);
                        Interlocked.Increment(ref failed);
                        errors.Add($"{ev.EventId}: failed message not found");
                        return;
                    }

                    var eventTypeId = failedMessage.EventTypeId;
                    var eventJson = failedMessage.MessageContent?.EventContent?.EventJson;

                    if (string.IsNullOrEmpty(eventJson))
                    {
                        LogNoEventContent(ev.EventId);
                        Interlocked.Increment(ref failed);
                        errors.Add($"{ev.EventId}: no event content");
                        return;
                    }

                    await _managerClient.Resubmit(failedMessage, endpointId, eventTypeId, eventJson);
                    await _messageStore.ArchiveFailedEvent(ev.EventId, ev.SessionId!, endpointId);
                    Interlocked.Increment(ref succeeded);
                }
                catch (Exception ex)
                {
                    LogResubmitFailed(ex, ev.EventId);
                    Interlocked.Increment(ref failed);
                    errors.Add($"{ev.EventId}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                }
            }));

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkOperationResult
        {
            Processed = processed,
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors.ToList()
        };
    }

    public async Task<int> GetDeadLetteredCountAsync(string endpointId)
    {
        var stateCount = await _messageStore.DownloadEndpointStateCount(endpointId);
        return stateCount.DeadletterCount;
    }

    // Messages in the dead-letter queue of the endpoint's own subscription, or null when Service Bus
    // can't be read: the preview then still shows the stored count.
    public async Task<long?> GetDeadLetterQueueCountAsync(string endpointId)
    {
        try
        {
            var runtime = await _sbAdmin.GetSubscriptionRuntimePropertiesAsync(endpointId, endpointId);
            return runtime.Value.DeadLetterMessageCount;
        }
        catch (Exception ex)
        {
            LogDeadLetterQueueCountFailed(ex, endpointId);
            return null;
        }
    }

    // A DeadLettered row stands for a message the endpoint dead-lettered on its own subscription.
    // Deleting the row alone left the message in that subscription's dead-letter queue, which
    // Service Bus never empties on its own (#286). Drain the queue first, then delete the rows: if
    // the drain fails the rows stay, so a rerun still finds both. The drain takes the whole queue,
    // including messages with no row (dead-lettered by Service Bus itself, or left behind by the
    // old store-only delete).
    public async Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId)
    {
        int processed = 0;
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();

        try
        {
            var receiver = _sbClient.CreateReceiver(endpointId, endpointId,
                new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            await using (receiver)
            {
                var removed = await SubscriptionAdminService.DrainAsync(receiver);
                processed += removed;
                succeeded += removed;
            }
        }
        catch (Exception ex)
        {
            LogDrainDeadLetterQueueFailed(ex, endpointId);
            errors.Add($"The dead-letter queue of {endpointId}/{endpointId} could not be drained: {ex.Message} " +
                "The stored dead-lettered events were kept, so this can be run again.");
            return new BulkOperationResult { Processed = processed + 1, Succeeded = succeeded, Failed = 1, Errors = errors };
        }

        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { MessageStore.ResolutionStatus.DeadLettered.ToString() }
                },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                processed++;

                try
                {
                    bool removed = await _messageStore.RemoveMessage(ev.EventId, ev.SessionId!, endpointId);
                    if (removed)
                        succeeded++;
                    else
                    {
                        failed++;
                        errors.Add($"{ev.EventId}: remove returned false");
                    }
                }
                catch (Exception ex)
                {
                    LogDeleteDeadLetteredFailed(ex, ev.EventId);
                    failed++;
                    errors.Add($"{ev.EventId}: {ex.Message}");
                }
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkOperationResult
        {
            Processed = processed,
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors
        };
    }

    public async Task<DeferredReprocessResult> ReprocessDeferredAsync(string endpointId, string sessionId)
    {
        var result = new DeferredReprocessResult
        {
            SessionId = sessionId,
            Errors = new List<string>()
        };

        // Step 1: Clear session state
        try
        {
            var receiver = await _sbClient.AcceptSessionAsync(endpointId, endpointId, sessionId);
            await using (receiver)
            {
                // Reset the block, keeping the replay position: events that arrive before
                // the replays this reprocess requests must still park behind them.
                await ClearSessionState(receiver, state => state.ClearForReprocess());
                result.SessionStateCleared = true;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            result.SessionStateCleared = true; // No active session is fine
        }
        catch (Exception ex)
        {
            LogClearSessionStateFailedForEndpoint(ex, sessionId, endpointId);
            result.Errors.Add($"Clear session state: {ex.Message}");
        }

        // Step 2: Send ProcessDeferredRequest to DeferredProcessor subscription
        try
        {
            var message = NimBus.ServiceBus.MessageHelper.ToServiceBusMessage(new NimBus.Core.Messages.Message
            {
                To = CoreConstants.DeferredProcessorId,
                SessionId = sessionId,
                MessageType = NimBus.Core.Messages.MessageType.ProcessDeferredRequest,
                MessageContent = new NimBus.Core.Messages.MessageContent(),
                CorrelationId = Guid.NewGuid().ToString(),
            });

            await using var sender = _sbClient.CreateSender(endpointId);
            await sender.SendMessageAsync(message);
            result.ProcessRequestSent = true;
        }
        catch (Exception ex)
        {
            LogSendProcessDeferredRequestFailed(ex, sessionId, endpointId);
            result.Errors.Add($"Send ProcessDeferredRequest: {ex.Message}");
        }

        return result;
    }
}
