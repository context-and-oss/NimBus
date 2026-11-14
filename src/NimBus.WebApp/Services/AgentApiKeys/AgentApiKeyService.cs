using System.Text.RegularExpressions;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>How an agent API key change ended.</summary>
public enum AgentApiKeyChangeStatus
{
    /// <summary>The change was saved.</summary>
    Saved,

    /// <summary>The request failed validation; see the errors.</summary>
    Invalid,

    /// <summary>No key has that id.</summary>
    NotFound,

    /// <summary>The key must be revoked (or have expired) before it can be removed.</summary>
    StillActive,

    /// <summary>Concurrent saves kept winning; nothing was saved.</summary>
    Conflict,
}

/// <summary>The outcome of an agent API key change.</summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Settings">The keys after the change (or as read, when nothing was saved).</param>
/// <param name="Key">The key the change was about, when there is one.</param>
/// <param name="FullKey">The full key; only set when one was just issued.</param>
/// <param name="Errors">Validation errors for <see cref="AgentApiKeyChangeStatus.Invalid"/>.</param>
public sealed record AgentApiKeyChange(
    AgentApiKeyChangeStatus Status,
    AgentApiKeySettings Settings,
    AgentApiKey? Key = null,
    string? FullKey = null,
    IReadOnlyList<string>? Errors = null);

/// <summary>
/// Issues, revokes and removes agent API keys (Spec 039). Each change touches one key, so a
/// revision conflict is resolved by re-reading and re-applying it, a few times at most. A saved
/// change is handed to <see cref="IAgentApiKeyProvider.Accept"/> so this instance applies it at once.
/// </summary>
public sealed partial class AgentApiKeyService
{
    /// <summary>The longest name a key may carry.</summary>
    public const int MaxNameLength = 100;

    private const int MaxAttempts = 3;

    private readonly IEndpointMetadataStore _store;
    private readonly IAgentApiKeyProvider _provider;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public AgentApiKeyService(IEndpointMetadataStore store, IAgentApiKeyProvider provider, TimeProvider time)
    {
        _store = store;
        _provider = provider;
        _time = time;
    }

    /// <summary>The issued keys and when each was last used.</summary>
    public async Task<(AgentApiKeySettings Settings, IReadOnlyDictionary<string, DateTime> LastUsed)> ListAsync()
    {
        var settings = await _store.GetAgentApiKeySettings().ConfigureAwait(false);
        var usage = await _store.GetAgentApiKeyUsage().ConfigureAwait(false);
        var lastUsed = usage
            .GroupBy(u => u.KeyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(u => u.LastUsedAtUtc), StringComparer.Ordinal);
        return (settings, lastUsed);
    }

    /// <summary>The status a key has now.</summary>
    public AgentApiKeyStatus StatusOf(AgentApiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.RevokedAtUtc is not null)
            return AgentApiKeyStatus.Revoked;
        if (key.ExpiresAtUtc is { } expires && _time.GetUtcNow().UtcDateTime >= expires)
            return AgentApiKeyStatus.Expired;
        return AgentApiKeyStatus.Active;
    }

    /// <summary>Issues a key for <paramref name="agentId"/>.</summary>
    public async Task<AgentApiKeyChange> CreateAsync(string? agentId, string? name, DateTime? expiresAtUtc, string? createdBy)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var errors = new List<string>();
        if (string.IsNullOrEmpty(agentId) || !AgentIdPattern().IsMatch(agentId))
            errors.Add("agentId must be 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit.");
        if (name is { Length: > MaxNameLength })
            errors.Add($"name must be at most {MaxNameLength} characters.");
        if (expiresAtUtc is { } expires && expires.ToUniversalTime() <= now)
            errors.Add("expiresAtUtc must be in the future.");
        if (errors.Count > 0)
            return new AgentApiKeyChange(AgentApiKeyChangeStatus.Invalid, await _store.GetAgentApiKeySettings().ConfigureAwait(false), Errors: errors);

        var generated = AgentApiKeyFormat.Generate();
        var key = new AgentApiKey
        {
            KeyId = generated.KeyId,
            AgentId = agentId!,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            SecretHash = generated.SecretHash,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            ExpiresAtUtc = expiresAtUtc?.ToUniversalTime(),
        };

        var change = await SaveAsync(createdBy, settings =>
        {
            settings.Keys.Add(key);
            return (AgentApiKeyChangeStatus.Saved, key);
        }).ConfigureAwait(false);
        return change.Status == AgentApiKeyChangeStatus.Saved ? change with { FullKey = generated.Key } : change;
    }

    /// <summary>Revokes a key. Revoking a revoked key keeps the first revocation.</summary>
    public Task<AgentApiKeyChange> RevokeAsync(string keyId, string? revokedBy)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return SaveAsync(revokedBy, settings =>
        {
            var key = Find(settings, keyId);
            if (key is null)
                return (AgentApiKeyChangeStatus.NotFound, null);
            if (key.RevokedAtUtc is null)
            {
                key.RevokedAtUtc = now;
                key.RevokedBy = revokedBy;
            }
            return (AgentApiKeyChangeStatus.Saved, key);
        });
    }

    /// <summary>Removes a revoked or expired key.</summary>
    public Task<AgentApiKeyChange> RemoveAsync(string keyId, string? removedBy)
        => SaveAsync(removedBy, settings =>
        {
            var key = Find(settings, keyId);
            if (key is null)
                return (AgentApiKeyChangeStatus.NotFound, null);
            if (StatusOf(key) == AgentApiKeyStatus.Active)
                return (AgentApiKeyChangeStatus.StillActive, key);
            settings.Keys.Remove(key);
            return (AgentApiKeyChangeStatus.Saved, key);
        });

    private async Task<AgentApiKeyChange> SaveAsync(
        string? updatedBy,
        Func<AgentApiKeySettings, (AgentApiKeyChangeStatus Status, AgentApiKey? Key)> apply)
    {
        AgentApiKeySettings current = new();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            current = await _store.GetAgentApiKeySettings().ConfigureAwait(false);
            var next = current.Clone();
            var (status, key) = apply(next);
            if (status != AgentApiKeyChangeStatus.Saved)
                return new AgentApiKeyChange(status, current, key);

            next.Revision = Guid.NewGuid().ToString();
            next.UpdatedBy = updatedBy;
            next.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
            if (await _store.TrySetAgentApiKeySettings(next, current.Revision).ConfigureAwait(false))
            {
                _provider.Accept(next);
                return new AgentApiKeyChange(AgentApiKeyChangeStatus.Saved, next, key);
            }
        }

        return new AgentApiKeyChange(AgentApiKeyChangeStatus.Conflict, current);
    }

    private static AgentApiKey? Find(AgentApiKeySettings settings, string keyId)
        => settings.Keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex AgentIdPattern();
}

/// <summary>An agent API key's status.</summary>
public enum AgentApiKeyStatus
{
    /// <summary>The key authenticates.</summary>
    Active,

    /// <summary>An Owner revoked the key.</summary>
    Revoked,

    /// <summary>The key's expiry has passed.</summary>
    Expired,
}
