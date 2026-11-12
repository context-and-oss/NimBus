using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>
/// The dashboard tokens a site Owner has issued: read-only credentials for the WebApp's Monitor
/// wall display. A single record: one row on SQL Server, one document with a fixed id on Cosmos.
/// A store that has never been written returns an empty list with a null <see cref="Revision"/>.
/// </summary>
public class DashboardTokenSettings
{
    /// <summary>Fixed id of the singleton record.</summary>
    public const string SingletonId = "DashboardTokens";

    /// <summary>Record id. Always <see cref="SingletonId"/>.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = SingletonId;

    /// <summary>A new GUID on every save; null when nothing was ever saved.</summary>
    public string? Revision { get; set; }

    /// <summary>Who saved this revision. Display only.</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>When this revision was saved. Display only.</summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Every issued token, revoked ones included until an Owner removes them.</summary>
    public List<DashboardToken> Tokens { get; set; } = new();

    /// <summary>A deep copy.</summary>
    public DashboardTokenSettings Clone()
        => JsonConvert.DeserializeObject<DashboardTokenSettings>(JsonConvert.SerializeObject(this))!;
}

/// <summary>One issued dashboard token. The secret itself is never stored, only its hash.</summary>
public class DashboardToken
{
    /// <summary>Public lookup id; the <c>nbdt_&lt;TokenId&gt;</c> prefix of the token.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>Owner-chosen label, for example the wall it is used on. Display only.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base64 SHA-256 of the token's secret part.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>When the token was issued.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Who issued the token. Display only.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When the token stops working; null for no expiry.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>When the token was revoked; null while it is active.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Who revoked the token. Display only.</summary>
    public string? RevokedBy { get; set; }
}

/// <summary>When a dashboard token last authenticated a request. Kept apart from
/// <see cref="DashboardTokenSettings"/> so recording use never conflicts with an Owner's edit.</summary>
public class DashboardTokenUsage
{
    /// <summary>The token's <see cref="DashboardToken.TokenId"/>.</summary>
    public string TokenId { get; set; } = string.Empty;

    /// <summary>The most recently recorded use.</summary>
    public DateTime LastUsedAtUtc { get; set; }
}
