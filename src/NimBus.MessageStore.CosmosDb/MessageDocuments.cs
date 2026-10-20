using Newtonsoft.Json;
using System;

namespace NimBus.MessageStore;

public class MessageDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = null!;

    [JsonProperty("eventId")]
    public string EventId { get; set; } = null!;

    [JsonProperty("endpointId")]
    public string EndpointId { get; set; } = null!;

    [JsonProperty("message")]
    public MessageEntity Message { get; set; } = null!;

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeToLive { get; set; }
}

public class AuditDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = null!;

    [JsonProperty("eventId")]
    public string EventId { get; set; } = null!;

    [JsonProperty("endpointId", NullValueHandling = NullValueHandling.Ignore)]
    public string? EndpointId { get; set; }

    [JsonProperty("eventTypeId", NullValueHandling = NullValueHandling.Ignore)]
    public string? EventTypeId { get; set; }

    [JsonProperty("audit")]
    public MessageAuditEntity Audit { get; set; } = null!;

    [JsonProperty("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeToLive { get; set; }
}
