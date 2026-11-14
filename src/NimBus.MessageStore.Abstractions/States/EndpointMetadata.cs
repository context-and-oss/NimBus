using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class EndpointMetadata
{
    [JsonProperty(PropertyName = "id")] public string EndpointId { get; set; } = null!;
    public string? EndpointOwner { get; set; }
    public string? EndpointOwnerTeam { get; set; }
    public string? EndpointOwnerEmail { get; set; }

    /// <summary>Heartbeat opt-in; null when the endpoint has never been configured either way.</summary>
    public bool? IsHeartbeatEnabled { get; set; }

    /// <summary>
    /// Rollup of <see cref="Heartbeats"/>: the most recent settled outcome, so an
    /// in-flight Pending probe never masks the last known state.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public HeartbeatStatus? EndpointHeartbeatStatus { get; set; }

    public List<TechnicalContact>? TechnicalContacts { get; set; }

    /// <summary>Recent heartbeat probes for this endpoint, newest last, pruned by the store.</summary>
    public List<Heartbeat>? Heartbeats { get; set; }

    public bool? SubscriptionStatus { get; set; }

    /// <summary>
    /// The business system this endpoint's adapter integrates (for example "Dynamics 365 Sales"),
    /// named in data-quality reports to the source's owner (Spec 044 Phase 3). Owner-edited.
    /// </summary>
    public string? SourceSystem { get; set; }

    /// <summary>
    /// The https address of the adapter's source repository (Spec 044 Phase 3). An agent that
    /// finds an adapter defect opens its issue and draft pull request here; without it adapter
    /// faults go to a person. Never the NimBus repository. Owner-edited.
    /// </summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>
    /// The issue tracker for this endpoint as an https link template containing
    /// <c>{ticketId}</c>, for example <c>https://github.com/org/adapter/issues/{ticketId}</c>.
    /// Overrides the site's <c>TicketLinkTemplate</c> for this endpoint's reported messages
    /// (Spec 044 Phase 3, §14 Q4). Owner-edited.
    /// </summary>
    public string? TicketLinkTemplate { get; set; }
}
