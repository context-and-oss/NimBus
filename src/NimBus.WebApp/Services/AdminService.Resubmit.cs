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

    public async Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId)
    {
        int processed = 0;
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();
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
                var stateBytes = await receiver.GetSessionStateAsync();
                if (stateBytes != null)
                {
                    // Reset blocking state
                    await receiver.SetSessionStateAsync(null);
                }
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
