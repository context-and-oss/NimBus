using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    public async Task StoreMessage(MessageEntity message)
    {
        var container = await _getMessagesContainer();
        var doc = new MessageDocument
        {
            Id = message.MessageId,
            EventId = message.EventId,
            EndpointId = message.EndpointId,
            Message = message,
            TimeToLive = 60 * 60 * 24 * 90 // 90-day TTL
        };

        try
        {
            await container.UpsertItemAsync(doc, new PartitionKey(doc.EventId), SuppressContentOnWrite);
        }
        catch (CosmosException e)
        {
            LogCosmosStoreMessageError(_logger, e, message.EventId, message.MessageId);
            throw;
        }
    }

    public async Task RemoveStoredMessage(string eventId, string messageId)
    {
        var container = await _getMessagesContainer();
        try
        {
            await container.DeleteItemAsync<MessageDocument>(messageId, new PartitionKey(eventId));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted, ignore
        }
    }

    public async Task<int> PurgeStoredMessages(string endpointId)
    {
        var container = await _getMessagesContainer();
        // The messages container is partitioned by eventId, so this is a cross-partition
        // query; project only the keys each delete needs, not the message payloads.
        var query = new QueryDefinition(
                "SELECT c.id, c.eventId FROM c WHERE LOWER(c.endpointId) = LOWER(@endpointId)")
            .WithParameter("@endpointId", endpointId);
        var iterator = container.GetItemQueryIterator<MessageKeyProjection>(query);

        var deleted = 0;
        var deleteOptions = new ParallelOptions { MaxDegreeOfParallelism = 8 };
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            await Parallel.ForEachAsync(page, deleteOptions, async (item, _) =>
            {
                try
                {
                    await container.DeleteItemAsync<MessageDocument>(item.Id, new PartitionKey(item.EventId));
                    Interlocked.Increment(ref deleted);
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    // Expired by TTL or deleted concurrently — nothing left to remove.
                }
            });
        }

        LogCosmosPurgeDeletedStoredMessages(_logger, deleted, endpointId);
        return deleted;
    }

    private sealed class MessageKeyProjection
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("eventId")] public string EventId { get; set; } = string.Empty;
    }

    public async Task<MessageEntity?> GetMessage(string eventId, string messageId)
    {
        var container = await _getMessagesContainer();
        try
        {
            var response = await container.ReadItemAsync<MessageDocument>(messageId, new PartitionKey(eventId));
            return response.Resource?.Message;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
    {
        var container = await _getMessagesContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId")
            .WithParameter("@eventId", eventId);
        // Bound page size so an event with a long history streams in pages
        // instead of one oversized response (documents carry the full EventJson
        // payload). The loop still drains every match.
        var result = container.GetItemQueryIterator<MessageDocument>(query, null,
            new QueryRequestOptions { MaxItemCount = 100 });
        var messages = new List<MessageEntity>();

        while (result.HasMoreResults)
        {
            var feed = await CosmosExceptionTranslation.TranslateTransientAsync(
                () => result.ReadNextAsync(),
                _logger);
            foreach (var doc in feed)
            {
                messages.Add(doc.Message);
            }
        }

        return messages;
    }

    public async Task<MessageEntity?> GetLatestEventRequestMessage(string eventId)
    {
        var container = await _getMessagesContainer();
        // Single-partition (the messages container is partitioned by /eventId) TOP 1:
        // fetch only the newest message that carries event content instead of pulling
        // the whole history and filtering in memory on every event-detail page load.
        var query = new QueryDefinition(
                "SELECT TOP 1 * FROM c WHERE c.eventId = @eventId " +
                "AND c.message.MessageType IN ('EventRequest', 'ResubmissionRequest') " +
                "AND IS_DEFINED(c.message.MessageContent.EventContent.EventJson) " +
                "AND c.message.MessageContent.EventContent.EventJson != null " +
                "AND c.message.MessageContent.EventContent.EventJson != '' " +
                "ORDER BY c.message.EnqueuedTimeUtc DESC")
            .WithParameter("@eventId", eventId);
        var result = container.GetItemQueryIterator<MessageDocument>(query, null,
            new QueryRequestOptions { MaxItemCount = 1 });
        if (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            return feed.FirstOrDefault()?.Message;
        }

        return null;
    }

    public async Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId)
    {
        var messages = await GetMessagesByEventAndEndpoint(eventId, endpointId);

        return messages
            .Where(me => me.MessageContent?.ErrorContent != null)
            .OrderBy(me => me.EnqueuedTimeUtc)
            .LastOrDefault();
    }

    public async Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId)
    {
        var messages = await GetMessagesByEventAndEndpoint(eventId, endpointId);

        return messages
            .OrderBy(me => me.EnqueuedTimeUtc)
            .LastOrDefault();
    }

    private async Task<List<MessageEntity>> GetMessagesByEventAndEndpoint(string eventId, string endpointId)
    {
        var container = await _getMessagesContainer();
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.eventId = @eventId AND LOWER(c.endpointId) = LOWER(@endpointId)")
            .WithParameter("@eventId", eventId)
            .WithParameter("@endpointId", endpointId);
        var result = container.GetItemQueryIterator<MessageDocument>(query);
        var messages = new List<MessageEntity>();

        while (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            foreach (var doc in feed)
            {
                messages.Add(doc.Message);
            }
        }

        return messages;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS STORE-MESSAGE-ERROR: EventId: {EventId}, MessageId: {MessageId}")]
    private static partial void LogCosmosStoreMessageError(ILogger logger, Exception exception, string eventId, string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "COSMOS PURGE: Deleted {Count} stored messages on endpoint {EndpointId}")]
    private static partial void LogCosmosPurgeDeletedStoredMessages(ILogger logger, int count, string endpointId);
}
