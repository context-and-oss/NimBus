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

    public async Task<DeadLetterQueueCounts?> GetDeadLetterQueueCountsAsync(string endpointId)
    {
        var eventIds = (await GetDeadLetteredEventsAsync(endpointId)).Select(ev => ev.EventId).ToHashSet(StringComparer.Ordinal);
        try
        {
            var runtime = await _sbAdmin.GetSubscriptionRuntimePropertiesAsync(endpointId, endpointId);
            int matching = 0;
            if (eventIds.Count > 0)
            {
                var receiver = CreateDeadLetterReceiver(endpointId);
                await using (receiver)
                {
                    matching = await SubscriptionAdminService.CountMatchingAsync(
                        receiver, message => IsDeadLetterOf(message, eventIds));
                }
            }

            return new DeadLetterQueueCounts(runtime.Value.DeadLetterMessageCount, matching);
        }
        catch (Exception ex)
        {
            LogDeadLetterQueueCountFailed(ex, endpointId);
            return null;
        }
    }

    // A DeadLettered row stands for a message the endpoint dead-lettered on its own subscription.
    // Deleting the row alone left the message in that subscription's dead-letter queue, which
    // Service Bus never empties on its own (#286). Complete the rows' messages first, matched on
    // EventId, then delete the rows: if that fails the rows stay, so a rerun still finds both.
    // A queue message that matches no row is left alone: it may be one NimBus never showed as
    // dead-lettered, and Admin → Subscriptions → Purge dead letters is the action that removes it.
    public async Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId)
    {
        int processed = 0;
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();

        // Read every row before deleting any: each delete moves a row out of the filter.
        var events = await GetDeadLetteredEventsAsync(endpointId);
        if (events.Count == 0)
            return new BulkOperationResult { Processed = 0, Succeeded = 0, Failed = 0, Errors = errors };

        var eventIds = events.Select(ev => ev.EventId).ToHashSet(StringComparer.Ordinal);
        int removedFromQueue = 0;
        try
        {
            var receiver = CreateDeadLetterReceiver(endpointId);
            await using (receiver)
            {
                await SubscriptionAdminService.CompleteMatchingAsync(
                    receiver, message => IsDeadLetterOf(message, eventIds), () => removedFromQueue++);
            }
        }
        catch (Exception ex)
        {
            LogRemoveDeadLettersFailed(ex, endpointId);
            errors.Add($"Removing the dead-lettered messages from the dead-letter queue of {endpointId}/{endpointId} " +
                $"failed after {removedFromQueue}: {ex.Message} The stored dead-lettered events were kept, so this can be run again.");
            return new BulkOperationResult { Processed = removedFromQueue + 1, Succeeded = removedFromQueue, Failed = 1, Errors = errors };
        }

        processed = removedFromQueue;
        succeeded = removedFromQueue;

        foreach (var ev in events)
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

        return new BulkOperationResult
        {
            Processed = processed,
            Succeeded = succeeded,
            Failed = failed,
            Errors = errors
        };
    }

    private async Task<List<MessageStore.UnresolvedEvent>> GetDeadLetteredEventsAsync(string endpointId)
    {
        var events = new List<MessageStore.UnresolvedEvent>();
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
            events.AddRange(response.Events);
            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return events;
    }

    private ServiceBusReceiver CreateDeadLetterReceiver(string endpointId) =>
        _sbClient.CreateReceiver(endpointId, endpointId, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });

    // Every NimBus message carries its event's id as the EventId application property, and
    // dead-lettering keeps the application properties.
    private static bool IsDeadLetterOf(ServiceBusReceivedMessage message, HashSet<string> eventIds) =>
        message.ApplicationProperties.TryGetValue(NimBus.Core.Messages.UserPropertyName.EventId.ToString(), out var value)
        && value is string eventId
        && eventIds.Contains(eventId);

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
