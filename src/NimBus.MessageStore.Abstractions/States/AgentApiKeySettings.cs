using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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
