using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

/// <summary>
/// The agent API keys a site Owner has issued (Spec 039). A single record: one row on SQL
/// Server, one document with a fixed id on Cosmos. A store that has never been written returns
/// an empty list with a null <see cref="Revision"/>.
/// </summary>
public class AgentApiKeySettings
{
    /// <summary>Fixed id of the singleton record.</summary>
    public const string SingletonId = "AgentApiKeys";

    /// <summary>Record id. Always <see cref="SingletonId"/>.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = SingletonId;

    /// <summary>A new GUID on every save; null when nothing was ever saved.</summary>
    public string? Revision { get; set; }

    /// <summary>Who saved this revision. Display only.</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>When this revision was saved. Display only.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Every issued key, revoked ones included until an Owner removes them.</summary>
    public List<AgentApiKey> Keys { get; set; } = new();

    /// <summary>A deep copy.</summary>
    public AgentApiKeySettings Clone()
        => JsonConvert.DeserializeObject<AgentApiKeySettings>(JsonConvert.SerializeObject(this))!;
}

/// <summary>One issued agent API key. The secret itself is never stored, only its hash.</summary>
public class AgentApiKey
{
    /// <summary>Public lookup id; the <c>nbak_&lt;KeyId&gt;</c> prefix of the key.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The agent the key authenticates as.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Owner-chosen label. Display only.</summary>
    public string? Name { get; set; }

    /// <summary>Base64 SHA-256 of the key's secret part.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>When the key was issued.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Who issued the key. Display only.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When the key stops working; null for no expiry.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>When the key was revoked; null while it is active.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Who revoked the key. Display only.</summary>
    public string? RevokedBy { get; set; }

    /// <summary>
    /// Where the key authenticates, fixed when it is issued (Spec 052). A key stored before uses
    /// existed reads as <see cref="AgentApiKeyUse.AgentApi"/>.
    /// </summary>
    public AgentApiKeyUse Use { get; set; } = AgentApiKeyUse.AgentApi;

    /// <summary>What an <see cref="AgentApiKeyUse.Mcp"/> key may do; null for an Agent API key.</summary>
    public AgentApiKeyMcpGrant? Mcp { get; set; }

    /// <summary>The <see cref="KeyId"/> of the key this one rotated, when it was issued by Rotate.</summary>
    public string? Replaces { get; set; }
}

/// <summary>Where an agent API key authenticates (Spec 052 §6.3).</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum AgentApiKeyUse
{
    /// <summary>The agent API, <c>/api/agent/*</c> (Spec 039). Every key issued before Spec 052.</summary>
    AgentApi,

    /// <summary>The operator MCP endpoint, <c>/mcp</c>, as an unattended workload.</summary>
    Mcp,
}

/// <summary>
/// The most an MCP key may do (Spec 052 §6.3). Observe is implied. Each permission maps to a
/// workload app role and still needs the Owner's switch in the MCP access policy.
/// </summary>
public class AgentApiKeyMcpGrant
{
    /// <summary>Record failure classifications, like <c>Nimbus.Workload.Classify</c>.</summary>
    public bool Classify { get; set; }

    /// <summary>Mark messages reported, like <c>Nimbus.Workload.Annotate</c>.</summary>
    public bool Annotate { get; set; }

    /// <summary>Resubmit failed messages unattended, like <c>Nimbus.Workload.Resubmit</c>.</summary>
    public bool Resubmit { get; set; }

    /// <summary>MCP requests per window for this key, below the deployment's and the Owner's limit; null for theirs.</summary>
    public int? RequestsPerWindow { get; set; }
}

/// <summary>When an agent API key last authenticated a request. Kept apart from
/// <see cref="AgentApiKeySettings"/> so recording use never conflicts with an Owner's edit.</summary>
public class AgentApiKeyUsage
{
    /// <summary>The key's <see cref="AgentApiKey.KeyId"/>.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The most recently recorded use.</summary>
    public DateTime LastUsedAtUtc { get; set; }
}
