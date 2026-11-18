using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

/// <summary>
/// One access-control document (spec 026): either the single site-wide ACL or one
/// endpoint-scoped ACL. Role entries are opaque strings — each may be an email
/// address or an Entra object id — persisted verbatim; normalization and matching
/// are the consumer's concern (see the WebApp's authorization service).
/// </summary>
public class AccessControlList
{
    /// <summary>Document id for the site-wide ACL.</summary>
    public const string SiteId = "site";

    /// <summary>Id prefix for endpoint-scoped ACLs ("endpoint:{endpointId}").</summary>
    public const string EndpointIdPrefix = "endpoint:";

    /// <summary>
    /// "site" for the site ACL, "endpoint:{endpointId}" for endpoint ACLs. The prefix
    /// prevents an endpoint literally named "site" from colliding with the site document.
    /// </summary>
    [JsonProperty(PropertyName = "id")] public string Id { get; set; } = SiteId;

    /// <summary>The endpoint this ACL scopes to; null for the site document.</summary>
    public string? EndpointId { get; set; }

    /// <summary>Read-only access (view endpoints, events, metrics).</summary>
    public List<string> Readers { get; set; } = new();

    /// <summary>Operational access (resubmit, skip, handoff, compose).</summary>
    public List<string> Contributors { get; set; } = new();

    /// <summary>Full management access (purge, metadata, role grants).</summary>
    public List<string> Owners { get; set; } = new();

    /// <summary>May view raw event payloads. Meaningful on the site document only.</summary>
    public List<string> PiiReaders { get; set; } = new();

    /// <summary>
    /// Optional display names for entries, such as what an object id is for. Informational
    /// only: a name grants nothing. The WebApp keys them by the entry trimmed and lowercased.
    /// </summary>
    public Dictionary<string, string> Labels { get; set; } = new();

    /// <summary>Last write timestamp, UTC.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Builds the document id for an endpoint-scoped ACL.</summary>
    public static string IdForEndpoint(string endpointId) => EndpointIdPrefix + endpointId;
}
