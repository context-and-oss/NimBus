using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NimBus.WebApp.ManagementApi;
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.WebApp.Services;

// Session purge + subscription purge + bulk delete-by-recipient/status/skip
// operations. These all walk message-store state and/or Service Bus
// subscriptions to remove or mark messages — destructive admin actions that
// the WebApp's Advanced Operations surface exposes to operators.
public partial class AdminService
{
    public async Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId)
    {
        var sessionState = await _messageStore.DownloadEndpointSessionStateCount(endpointId, sessionId);

        // Count Cosmos events for the session
        int cosmosEventCount = 0;
        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter { EndPointId = endpointId, SessionId = sessionId },
                continuationToken, PageSize);

            cosmosEventCount += response.Events.Count();
            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        // Peek Deferred subscription
        int deferredSubscriptionCount = 0;
        try
        {
            var deferredReceiver = await _sbClient.AcceptSessionAsync(
                endpointId, CoreConstants.DeferredSubscriptionName, sessionId);
            await using (deferredReceiver)
            {
                var peeked = await deferredReceiver.PeekMessagesAsync(100);
                deferredSubscriptionCount = peeked.Count;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No session — 0 messages
        }

        return new SessionPurgePreview
        {
            SessionId = sessionId,
            PendingCount = sessionState.PendingEvents.Count(),
            DeferredCount = sessionState.DeferredEvents.Count(),
            DeferredSubscriptionCount = deferredSubscriptionCount,
            CosmosEventCount = cosmosEventCount
        };
    }

    public async Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId)
    {
        var result = new SessionPurgeResult
        {
            SessionId = sessionId,
            Errors = new List<string>()
        };

        ServiceBusSessionReceiver? receiver = null;
        try
        {
            receiver = await _sbClient.AcceptSessionAsync(endpointId, endpointId, sessionId);
        }
        catch (Exception ex)
        {
            LogAcceptSessionFailed(ex, sessionId, endpointId);
            result.Errors.Add($"Could not accept session: {ex.Message}");
        }

        if (receiver != null)
        {
            // Remove active messages
            try
            {
                int activeRemoved = 0;
                int batchCount;
                do
                {
                    var messages = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5));
                    batchCount = messages.Count;
                    foreach (var message in messages)
                    {
                        try
                        {
                            await receiver.CompleteMessageAsync(message);
                            activeRemoved++;
                        }
                        catch (Exception ex)
                        {
                            LogCompleteActiveMessageFailed(ex, message.MessageId);
                            result.Errors.Add($"Active message {message.MessageId}: {ex.Message}");
                        }
                    }
                }
                while (batchCount > 0);

                result.ActiveMessagesRemoved = activeRemoved;
            }
            catch (Exception ex)
            {
                LogRemoveActiveMessagesError(ex, sessionId);
                result.Errors.Add($"Active messages error: {ex.Message}");
            }

            // Remove deferred messages
            try
            {
                int deferredRemoved = 0;
                long? fromSequenceNumber = null;
                int peekCount;
                do
                {
                    var peekedMessages = fromSequenceNumber.HasValue
                        ? await receiver.PeekMessagesAsync(100, fromSequenceNumber.Value)
                        : await receiver.PeekMessagesAsync(100);
                    peekCount = peekedMessages.Count;
                    if (peekCount > 0)
                        fromSequenceNumber = peekedMessages[peekCount - 1].SequenceNumber + 1;
                    foreach (var message in peekedMessages)
                    {
                        if (message.State == ServiceBusMessageState.Deferred)
                        {
                            try
                            {
                                var deferredMessage = await receiver.ReceiveDeferredMessageAsync(message.SequenceNumber);
                                if (deferredMessage != null)
                                {
                                    await receiver.CompleteMessageAsync(deferredMessage);
                                    deferredRemoved++;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogCompleteDeferredMessageFailed(ex, message.SequenceNumber);
                                result.Errors.Add($"Deferred message seq {message.SequenceNumber}: {ex.Message}");
                            }
                        }
                    }
                }
                while (peekCount > 0);

                result.DeferredMessagesRemoved = deferredRemoved;
            }
            catch (Exception ex)
            {
                LogRemoveDeferredMessagesError(ex, sessionId);
                result.Errors.Add($"Deferred messages error: {ex.Message}");
            }

            // Clear session state
            try
            {
                // Keep the deferral sequence, so a replay still in flight isn't mistaken for
                // a message parked after the purge.
                await ClearSessionState(receiver, state => state.ClearAfterPurge());
                result.SessionStateCleared = true;
            }
            catch (Exception ex)
            {
                LogClearSessionStateFailed(ex, sessionId);
                result.Errors.Add($"Clear session state: {ex.Message}");
            }

            await receiver.DisposeAsync();
        }

        // Remove messages from "Deferred" subscription
        try
        {
            var deferredReceiver = await _sbClient.AcceptSessionAsync(
                endpointId, CoreConstants.DeferredSubscriptionName, sessionId);

            await using (deferredReceiver)
            {
                int deferredSubRemoved = 0;
                int deferredBatchCount;
                do
                {
                    var messages = await deferredReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5));
                    deferredBatchCount = messages.Count;
                    foreach (var message in messages)
                    {
                        await deferredReceiver.CompleteMessageAsync(message);
                        deferredSubRemoved++;
                    }
                }
                while (deferredBatchCount > 0);

                result.DeferredSubscriptionMessagesRemoved = deferredSubRemoved;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            // No deferred messages for this session — not an error
            result.DeferredSubscriptionMessagesRemoved = 0;
        }
        catch (Exception ex)
        {
            LogRemoveDeferredSubscriptionError(ex, sessionId);
            result.Errors.Add($"Deferred subscription error: {ex.Message}");
        }

        // Remove Cosmos events for this session
        try
        {
            int cosmosRemoved = 0;
            string? continuationToken = string.Empty;

            do
            {
                var response = await _messageStore.GetEventsByFilter(
                    new MessageStore.EventFilter { EndPointId = endpointId, SessionId = sessionId },
                    continuationToken, PageSize);

                foreach (var ev in response.Events)
                {
                    try
                    {
                        await _messageStore.RemoveMessage(ev.EventId, sessionId, endpointId);
                        cosmosRemoved++;
                    }
                    catch (Exception ex)
                    {
                        LogRemoveCosmosEventFailed(ex, ev.EventId);
                        result.Errors.Add($"Cosmos event {ev.EventId}: {ex.Message}");
                    }
                }

                continuationToken = response.ContinuationToken;
            }
            while (continuationToken != null);

            result.CosmosEventsRemoved = cosmosRemoved;
        }
        catch (Exception ex)
        {
            LogRemoveCosmosEventsError(ex, sessionId);
            result.Errors.Add($"Cosmos removal error: {ex.Message}");
        }

        return result;
    }

    public async Task<bool> DeleteEventAsync(string endpointId, string eventId)
    {
        var ev = await _messageStore.GetEvent(endpointId, eventId);
        if (ev == null)
            return false;

        return await _messageStore.RemoveMessage(ev.EventId, ev.SessionId!, endpointId);
    }

    public async Task<PurgePreview> PurgeSubscriptionPreviewAsync(string endpointId, string subscription, List<string> states, DateTime? before)
    {
        bool purgeActive = states.Count == 0 || states.Contains("active");
        bool purgeDeferred = states.Count == 0 || states.Contains("deferred");

        await using var peekReceiver = _sbClient.CreateReceiver(endpointId, subscription);
        int totalScanned = 0;
        int totalMatching = 0;
        var sessionIds = new HashSet<string>();
        long fromSequenceNumber = 0;

        while (true)
        {
            var peeked = await peekReceiver.PeekMessagesAsync(100, fromSequenceNumber);
            if (peeked.Count == 0) break;

            foreach (var msg in peeked)
            {
                totalScanned++;
                bool stateMatch = (purgeActive && msg.State == ServiceBusMessageState.Active)
                               || (purgeDeferred && msg.State == ServiceBusMessageState.Deferred);

                if (stateMatch && (!before.HasValue || msg.EnqueuedTime.UtcDateTime < before.Value))
                {
                    totalMatching++;
                    sessionIds.Add(msg.SessionId ?? "");
                }
            }

            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        return new PurgePreview { TotalScanned = totalScanned, TotalMatching = totalMatching, SessionCount = sessionIds.Count };
    }

    public async Task<BulkOperationResult> PurgeSubscriptionAsync(string endpointId, string subscription, List<string> states, DateTime? before)
    {
        bool purgeActive = states.Count == 0 || states.Contains("active");
        bool purgeDeferred = states.Count == 0 || states.Contains("deferred");

        // Peek to discover sessions and matching messages
        await using var peekReceiver = _sbClient.CreateReceiver(endpointId, subscription);
        var sessionMessages = new Dictionary<string, List<(long SequenceNumber, ServiceBusMessageState State)>>();
        long fromSequenceNumber = 0;

        while (true)
        {
            var peeked = await peekReceiver.PeekMessagesAsync(100, fromSequenceNumber);
            if (peeked.Count == 0) break;

            foreach (var msg in peeked)
            {
                bool stateMatch = (purgeActive && msg.State == ServiceBusMessageState.Active)
                               || (purgeDeferred && msg.State == ServiceBusMessageState.Deferred);

                if (stateMatch && (!before.HasValue || msg.EnqueuedTime.UtcDateTime < before.Value))
                {
                    var sid = msg.SessionId ?? "";
                    if (!sessionMessages.ContainsKey(sid))
                        sessionMessages[sid] = new();
                    sessionMessages[sid].Add((msg.SequenceNumber, msg.State));
                }
            }

            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();

        foreach (var (sid, messages) in sessionMessages)
        {
            ServiceBusSessionReceiver sessionReceiver;
            try
            {
                sessionReceiver = await _sbClient.AcceptSessionAsync(endpointId, subscription, sid);
            }
            catch (ServiceBusException ex)
            {
                failed += messages.Count;
                errors.Add($"Session '{sid}': {ex.Message}");
                continue;
            }

            try
            {
                // Complete deferred messages
                foreach (var seqNum in messages.Where(m => m.State == ServiceBusMessageState.Deferred).Select(m => m.SequenceNumber))
                {
                    try
                    {
                        var msg = await sessionReceiver.ReceiveDeferredMessageAsync(seqNum);
                        if (msg != null)
                        {
                            await sessionReceiver.CompleteMessageAsync(msg);
                            succeeded++;
                        }
                    }
                    catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessageNotFound) { }
                    catch (Exception ex) { failed++; errors.Add($"Deferred seq {seqNum}: {ex.Message}"); }
                }

                // Complete active messages
                if (messages.Any(m => m.State == ServiceBusMessageState.Active))
                {
                    while (true)
                    {
                        var received = await sessionReceiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(5));
                        if (received.Count == 0) break;

                        bool anyCompleted = false;
                        foreach (var msg in received)
                        {
                            if (!before.HasValue || msg.EnqueuedTime.UtcDateTime < before.Value)
                            {
                                await sessionReceiver.CompleteMessageAsync(msg);
                                succeeded++;
                                anyCompleted = true;
                            }
                            else
                            {
                                await sessionReceiver.AbandonMessageAsync(msg);
                            }
                        }
                        if (!anyCompleted) break;
                    }
                }
            }
            finally
            {
                await sessionReceiver.DisposeAsync();
            }
        }

        return new BulkOperationResult { Processed = succeeded + failed, Succeeded = succeeded, Failed = failed, Errors = errors };
    }

    public async Task<int> DeleteMessagesByToPreviewAsync(string toField)
    {
        EnsureCosmosOnlyOperation(nameof(DeleteMessagesByToPreviewAsync));
        var container = _rawCosmosClient!.GetDatabase(DatabaseId).GetContainer(MessagesContainer);
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.message.To = @to")
            .WithParameter("@to", toField);

        using var iterator = container.GetItemQueryIterator<int>(query);
        int count = 0;
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            count += response.Sum();
        }
        return count;
    }

    public async Task<BulkOperationResult> DeleteMessagesByToAsync(string toField)
    {
        EnsureCosmosOnlyOperation(nameof(DeleteMessagesByToAsync));
        var container = _rawCosmosClient!.GetDatabase(DatabaseId).GetContainer(MessagesContainer);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.message.To = @to")
            .WithParameter("@to", toField);

        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();

        using var iterator = container.GetItemQueryIterator<JObject>(query);
        while (iterator.HasMoreResults)
        {
            var batch = await iterator.ReadNextAsync();
            foreach (var doc in batch)
            {
                var id = doc["id"]?.ToString();
                var eventId = doc["eventId"]?.ToString();
                if (id == null || eventId == null) continue;

                try
                {
                    await container.DeleteItemAsync<JObject>(id, new PartitionKey(eventId));
                    succeeded++;
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{id}: {ex.Message}");
                }
            }
        }

        return new BulkOperationResult { Processed = succeeded + failed, Succeeded = succeeded, Failed = failed, Errors = errors };
    }

    public async Task<int> DeleteByStatusPreviewAsync(string endpointId, List<string> statuses)
    {
        statuses = AdminStatusValidation.NormalizeDeleteStatuses(statuses);
        int count = 0;
        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter { EndPointId = endpointId, ResolutionStatus = statuses },
                continuationToken, PageSize);

            count += response.Events.Count();
            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return count;
    }

    public async Task<BulkOperationResult> DeleteByStatusAsync(string endpointId, List<string> statuses)
    {
        statuses = AdminStatusValidation.NormalizeDeleteStatuses(statuses);
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();
        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter { EndPointId = endpointId, ResolutionStatus = statuses },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                try
                {
                    bool removed = await _messageStore.RemoveMessage(ev.EventId, ev.SessionId!, endpointId);
                    if (removed) succeeded++;
                    else { failed++; errors.Add($"{ev.EventId}: remove returned false"); }
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{ev.EventId}: {ex.Message}");
                }
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkOperationResult { Processed = succeeded + failed, Succeeded = succeeded, Failed = failed, Errors = errors };
    }

    public async Task<int> SkipMessagesPreviewAsync(string endpointId, List<string> statuses, DateTime? before)
    {
        statuses = AdminStatusValidation.NormalizeSkipStatuses(statuses);
        int count = 0;
        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter { EndPointId = endpointId, ResolutionStatus = statuses },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                if (!before.HasValue || ev.UpdatedAt < before.Value)
                    count++;
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return count;
    }

    public async Task<BulkOperationResult> SkipMessagesAsync(string endpointId, List<string> statuses, DateTime? before)
    {
        statuses = AdminStatusValidation.NormalizeSkipStatuses(statuses);
        int succeeded = 0;
        int failed = 0;
        var errors = new List<string>();
        string? continuationToken = string.Empty;

        do
        {
            var response = await _messageStore.GetEventsByFilter(
                new MessageStore.EventFilter { EndPointId = endpointId, ResolutionStatus = statuses },
                continuationToken, PageSize);

            foreach (var ev in response.Events)
            {
                if (before.HasValue && ev.UpdatedAt >= before.Value)
                    continue;

                try
                {
                    bool updated = await _messageStore.UploadSkippedMessage(ev.EventId, ev.SessionId!, endpointId, ev);
                    if (updated) succeeded++;
                    else { failed++; errors.Add($"{ev.EventId}: update returned false"); }
                }
                catch (Exception ex)
                {
                    failed++;
                    errors.Add($"{ev.EventId}: {ex.Message}");
                }
            }

            continuationToken = response.ContinuationToken;
        }
        while (continuationToken != null);

        return new BulkOperationResult { Processed = succeeded + failed, Succeeded = succeeded, Failed = failed, Errors = errors };
    }

    public async Task<BulkOperationResult> DeleteAllEventsAsync(string endpointId)
    {
        var errors = new List<string>();
        var succeeded = 0;

        // Service Bus first: once the backlog is gone nothing still queued can land in
        // storage again after the purge below. A failed step doesn't stop the rest — the
        // operator asked for everything gone, and the result names whatever was left.
        foreach (var subscriptionName in new[] { endpointId, CoreConstants.DeferredSubscriptionName })
        {
            try
            {
                var recreated = await _subscriptionAdmin.RecreateSubscriptionAsync(endpointId, subscriptionName);
                if (recreated.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    errors.Add($"Subscription {endpointId}/{subscriptionName}: {recreated.Message}");
                    errors.AddRange(recreated.Errors ?? Enumerable.Empty<string>());
                }
            }
            catch (Exception ex)
            {
                LogDeleteAllEventsFailed(ex, endpointId);
                errors.Add($"Subscription {endpointId}/{subscriptionName}: {ex.Message}");
            }
        }

        try
        {
            if (await _messageStore.PurgeMessages(endpointId))
                succeeded++;
            else
                errors.Add("Events: nothing was deleted — the endpoint had no stored events, or the store refused the purge (see logs).");
        }
        catch (Exception ex)
        {
            LogDeleteAllEventsFailed(ex, endpointId);
            errors.Add($"Events: {ex.Message}");
        }

        try
        {
            await _messageStore.PurgeStoredMessages(endpointId);
            succeeded++;
        }
        catch (Exception ex)
        {
            LogDeleteAllEventsFailed(ex, endpointId);
            errors.Add($"Message history: {ex.Message}");
        }

        const int steps = 4;
        return new BulkOperationResult
        {
            Processed = steps,
            Succeeded = succeeded,
            Failed = steps - succeeded,
            Errors = errors
        };
    }
}
