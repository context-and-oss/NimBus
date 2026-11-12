using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>
/// The integrator API keys a site Owner has issued (Spec 045): credentials for scripts and partner
/// systems that call the WebApp's REST API with a role and an optional endpoint scope. A single
/// record: one row on SQL Server, one document with a fixed id on Cosmos. A store that has never
/// been written returns an empty list with a null <see cref="Revision"/>.
/// </summary>
public class IntegratorApiKeySettings
{
    /// <summary>Fixed id of the singleton record.</summary>
    public const string SingletonId = "IntegratorApiKeys";

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
    public List<IntegratorApiKey> Keys { get; set; } = new();

    /// <summary>A deep copy.</summary>
    public IntegratorApiKeySettings Clone()
        => JsonConvert.DeserializeObject<IntegratorApiKeySettings>(JsonConvert.SerializeObject(this))!;
}

/// <summary>The role an integrator API key grants. Owner is deliberately not available.</summary>
public enum IntegratorApiKeyRole
{
    /// <summary>Read-only access, as a site or endpoint Reader.</summary>
    Reader = 1,

    /// <summary>Read and operator actions (resubmit, skip, settle), as a site or endpoint Contributor.</summary>
    Contributor = 2,
}

/// <summary>One issued integrator API key. The secret itself is never stored, only its hash.</summary>
public class IntegratorApiKey
{
    /// <summary>Public lookup id; the <c>nbik_&lt;KeyId&gt;</c> prefix of the key.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>Owner-chosen label, for example the system that uses the key. Display only.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base64 SHA-256 of the key's secret part.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>The role the key grants.</summary>
    public IntegratorApiKeyRole Role { get; set; } = IntegratorApiKeyRole.Reader;

    /// <summary>
    /// The endpoints the role applies to; null means site-wide. An empty list is never stored.
    /// </summary>
    public List<string>? Endpoints { get; set; }

    /// <summary>Whether the key may read raw event payloads (the PiiReader capability).</summary>
    public bool CanReadPii { get; set; }

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

/// <summary>When an integrator API key last authenticated a request. Kept apart from
/// <see cref="IntegratorApiKeySettings"/> so recording use never conflicts with an Owner's edit.</summary>
public class IntegratorApiKeyUsage
{
    /// <summary>The key's <see cref="IntegratorApiKey.KeyId"/>.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The most recently recorded use.</summary>
    public DateTime LastUsedAtUtc { get; set; }
}
