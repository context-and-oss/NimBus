using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore : IMessageTrackingStore
{
    private const string PendingStatus = "Pending";
    private const string FailedStatus = "Failed";
    private const string DeferredStatus = "Deferred";
    private const string DLQStatus = "DeadLettered";
    private const string UnsupportedStatus = "Unsupported";
    private const string CompletedStatus = "Completed";
    private const string SkippedStatus = "Skipped";
    private const string PublisherRole = "Publisher";
    private static readonly ItemRequestOptions SuppressContentOnWrite = new() { EnableContentResponseOnWrite = false };

    private readonly Func<string, Task<ICosmosContainerAdapter>> _getEndpointContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getMessagesContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getAuditsContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getEventReportsContainer;
    private readonly Action<string> _removeEndpointContainerCache;
    private readonly ILogger? _logger;
    private readonly int _unresolvedTtlSeconds;

    public CosmosDbMessageTrackingStore(
        Func<string, Task<ICosmosContainerAdapter>> getEndpointContainer,
        Func<Task<ICosmosContainerAdapter>> getMessagesContainer,
        Func<Task<ICosmosContainerAdapter>> getAuditsContainer,
        Func<Task<ICosmosContainerAdapter>> getEventReportsContainer,
        Action<string> removeEndpointContainerCache,
        ILogger? logger,
        int unresolvedTtlSeconds)
    {
        _getEndpointContainer = getEndpointContainer;
        _getMessagesContainer = getMessagesContainer;
        _getAuditsContainer = getAuditsContainer;
        _getEventReportsContainer = getEventReportsContainer;
        _removeEndpointContainerCache = removeEndpointContainerCache;
        _logger = logger;
        _unresolvedTtlSeconds = unresolvedTtlSeconds;
    }

    private static string CompositeEventId((string EventId, string? SessionId, string Status) row)
        => $"{row.EventId}_{row.SessionId ?? string.Empty}";

    private static string CompositeEventId(UnresolvedEvent @event)
        => $"{@event.EventId}_{@event.SessionId ?? string.Empty}";

    private sealed class IdProjection
    {
        [JsonProperty("id")] public string Id { get; set; } = null!;
    }

    private sealed class BlockedEventProjection
    {
        [JsonProperty("status")] public string Status { get; set; } = null!;
        [JsonProperty("eventId")] public string EventId { get; set; } = null!;
        [JsonProperty("originatingMessageId")] public string? OriginatingMessageId { get; set; }
        [JsonProperty("lastMessageId")] public string? LastMessageId { get; set; }
    }

    private sealed class StatusQueryResult
    {
        public int EventCount { get; set; }
        [JsonProperty(PropertyName = "Status")] public string Status { get; set; } = null!;
        public DateTime? OldestUpdatedAt { get; set; }
    }

    private sealed class SessionCountQueryResult
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; } = null!;
        [JsonProperty(PropertyName = "Status")] public string Status { get; set; } = null!;
    }

    private sealed class BatchSessionQueryResult
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; } = null!;
        [JsonProperty(PropertyName = "status")] public string Status { get; set; } = null!;
        [JsonProperty(PropertyName = "sessionId")] public string SessionId { get; set; } = null!;
    }

    private sealed class EventDbo
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; } = null!;
        [JsonProperty(PropertyName = "status")] public string Status { get; set; } = null!;
        [JsonProperty(PropertyName = "eventType")] public string? EventType { get; set; }
        [JsonProperty(PropertyName = "sessionId")] public string SessionId { get; set; } = null!;
        [JsonProperty(PropertyName = "event")] public UnresolvedEvent Event { get; set; } = null!;
        [JsonProperty(PropertyName = "deleted")] public bool? Deleted { get; set; }
        [JsonProperty(PropertyName = "ttl", NullValueHandling = NullValueHandling.Ignore)] public int? TimeToLive { get; set; }
    }
}
