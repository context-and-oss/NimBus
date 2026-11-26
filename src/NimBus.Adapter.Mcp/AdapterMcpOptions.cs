namespace NimBus.Adapter.Mcp;

/// <summary>
/// The adapter MCP server's settings, bound from the <c>AdapterMcp</c> configuration section. The
/// resource is the deployment's one Entra registration for adapter MCP servers, separate from the
/// NimBus MCP resource (Spec 051 §7).
/// </summary>
public sealed class AdapterMcpOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "AdapterMcp";

    /// <summary>The Entra tenant whose tokens are accepted. Required.</summary>
    public string? TenantId { get; set; }

    /// <summary>The client id of the adapter MCP resource registration. Required.</summary>
    public string? Audience { get; set; }

    /// <summary>The resource's App ID URI; <c>api://{Audience}</c> when not set.</summary>
    public string? ApplicationIdUri { get; set; }

    /// <summary>The app role a caller's token must carry in <c>roles</c>.</summary>
    public string ReadRole { get; set; } = "Adapter.Read";

    /// <summary>The Entra instance, for a sovereign cloud.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>
    /// The salt for <see cref="NimBus.Core.Messages.PII.MaskMode.Hash"/>, as <c>NimBus:PiiHashSalt</c>
    /// is for NimBus. Use the same value to make hashes comparable with the WebApp's.
    /// </summary>
    public string? PiiHashSalt { get; set; }

    /// <summary>How many (entity, key) pairs the call log keeps before it drops the oldest.</summary>
    public int CallLogCapacity { get; set; } = AdapterCallLog.DefaultCapacity;

    /// <summary>The App ID URI the resource is known by, falling back to <c>api://{Audience}</c>.</summary>
    internal string ResolvedApplicationIdUri =>
        string.IsNullOrWhiteSpace(ApplicationIdUri) ? $"api://{Audience}" : ApplicationIdUri;

    /// <summary>The tenant's v2 authority, which is also its v2 token issuer.</summary>
    internal string Authority => $"{Instance.TrimEnd('/')}/{TenantId}/v2.0";
}
