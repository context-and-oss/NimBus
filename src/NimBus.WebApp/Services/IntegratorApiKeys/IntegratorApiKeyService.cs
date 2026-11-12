using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.IntegratorApiKeys;

/// <summary>How an integrator API key change ended.</summary>
public enum IntegratorApiKeyChangeStatus
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

    /// <summary>The storage provider cannot store integrator API keys.</summary>
    Unavailable,
}

/// <summary>The outcome of an integrator API key change.</summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Settings">The keys after the change (or as read, when nothing was saved).</param>
/// <param name="Key">The key the change was about, when there is one.</param>
/// <param name="FullKey">The full key; only set when one was just issued.</param>
/// <param name="Errors">Validation errors for <see cref="IntegratorApiKeyChangeStatus.Invalid"/>.</param>
public sealed record IntegratorApiKeyChange(
    IntegratorApiKeyChangeStatus Status,
    IntegratorApiKeySettings Settings,
    IntegratorApiKey? Key = null,
    string? FullKey = null,
    IReadOnlyList<string>? Errors = null);

/// <summary>What an Owner asks for when issuing an integrator API key.</summary>
/// <param name="Name">The key's label.</param>
/// <param name="Role">Reader or Contributor.</param>
/// <param name="Endpoints">The endpoints the role applies to; null for every endpoint.</param>
/// <param name="CanReadPii">Whether the key may read raw event payloads.</param>
/// <param name="ExpiresAtUtc">When the key stops working; null for no expiry.</param>
public sealed record IntegratorApiKeyRequest(
    string? Name,
    IntegratorApiKeyRole Role,
    IReadOnlyList<string>? Endpoints,
    bool CanReadPii,
    DateTime? ExpiresAtUtc);

/// <summary>An integrator API key's status.</summary>
public enum IntegratorApiKeyStatus
{
    /// <summary>The key authenticates.</summary>
    Active,

    /// <summary>An Owner revoked the key.</summary>
    Revoked,

    /// <summary>The key's expiry has passed.</summary>
    Expired,
}

/// <summary>
/// Issues, revokes and removes integrator API keys. Each change touches one key, so a
/// revision conflict is resolved by re-reading and re-applying it, a few times at most. A saved
/// change is handed to <see cref="IIntegratorApiKeyProvider.Accept"/> so this instance applies it at once.
/// </summary>
public sealed class IntegratorApiKeyService
{
    /// <summary>The longest name a key may carry.</summary>
    public const int MaxNameLength = 100;

    private const int MaxAttempts = 3;

    private readonly IIntegratorApiKeyStore? _store;
    private readonly IIntegratorApiKeyProvider _provider;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public IntegratorApiKeyService(IIntegratorApiKeyProvider provider, TimeProvider time, IIntegratorApiKeyStore? store = null)
    {
        _store = store;
        _provider = provider;
        _time = time;
    }

    /// <summary>Whether the storage provider can store integrator API keys.</summary>
    public bool IsAvailable => _store is not null;

    /// <summary>The issued keys and when each was last used.</summary>
    public async Task<(IntegratorApiKeySettings Settings, IReadOnlyDictionary<string, DateTime> LastUsed)> ListAsync()
    {
        var store = Store();
        var settings = await store.GetIntegratorApiKeySettings().ConfigureAwait(false);
        var usage = await store.GetIntegratorApiKeyUsage().ConfigureAwait(false);
        var lastUsed = usage
            .GroupBy(u => u.KeyId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(u => u.LastUsedAtUtc), StringComparer.Ordinal);
        return (settings, lastUsed);
    }

    /// <summary>The status a key has now.</summary>
    public IntegratorApiKeyStatus StatusOf(IntegratorApiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.RevokedAtUtc is not null)
            return IntegratorApiKeyStatus.Revoked;
        if (key.ExpiresAtUtc is { } expires && _time.GetUtcNow().UtcDateTime >= expires)
            return IntegratorApiKeyStatus.Expired;
        return IntegratorApiKeyStatus.Active;
    }

    /// <summary>
    /// Issues a key. <paramref name="knownEndpoints"/> are the endpoint ids the platform defines;
    /// a listed endpoint outside them is refused, so a typo can't issue a key that admits nothing.
    /// </summary>
    public async Task<IntegratorApiKeyChange> CreateAsync(IntegratorApiKeyRequest request, IReadOnlyCollection<string> knownEndpoints, string? createdBy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(knownEndpoints);
        if (_store is null)
            return Unavailable();

        var now = _time.GetUtcNow().UtcDateTime;
        var name = request.Name;
        var expiresAtUtc = request.ExpiresAtUtc;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
            errors.Add("name is required.");
        else if (name.Trim().Length > MaxNameLength)
            errors.Add($"name must be at most {MaxNameLength} characters.");
        if (request.Role is not (IntegratorApiKeyRole.Reader or IntegratorApiKeyRole.Contributor))
            errors.Add("role must be reader or contributor.");
        var endpoints = ValidateEndpoints(request.Endpoints, knownEndpoints, errors);
        if (expiresAtUtc is { } expires && expires.ToUniversalTime() <= now)
            errors.Add("expiresAtUtc must be in the future.");
        if (errors.Count > 0)
            return new IntegratorApiKeyChange(IntegratorApiKeyChangeStatus.Invalid, await _store.GetIntegratorApiKeySettings().ConfigureAwait(false), Errors: errors);

        var generated = SecretTokenFormat.Generate(IntegratorApiKeyDefaults.Prefix);
        var key = new IntegratorApiKey
        {
            KeyId = generated.Id,
            Name = name!.Trim(),
            SecretHash = generated.SecretHash,
            CreatedAtUtc = now,
            Role = request.Role,
            Endpoints = endpoints,
            CanReadPii = request.CanReadPii,
            CreatedBy = createdBy,
            ExpiresAtUtc = expiresAtUtc?.ToUniversalTime(),
        };

        var change = await SaveAsync(createdBy, settings =>
        {
            settings.Keys.Add(key);
            return (IntegratorApiKeyChangeStatus.Saved, key);
        }).ConfigureAwait(false);
        return change.Status == IntegratorApiKeyChangeStatus.Saved ? change with { FullKey = generated.Value } : change;
    }

    /// <summary>Revokes a key. Revoking a revoked key keeps the first revocation.</summary>
    public Task<IntegratorApiKeyChange> RevokeAsync(string keyId, string? revokedBy)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return SaveAsync(revokedBy, settings =>
        {
            var key = Find(settings, keyId);
            if (key is null)
                return (IntegratorApiKeyChangeStatus.NotFound, null);
            if (key.RevokedAtUtc is null)
            {
                key.RevokedAtUtc = now;
                key.RevokedBy = revokedBy;
            }
            return (IntegratorApiKeyChangeStatus.Saved, key);
        });
    }

    /// <summary>Removes a revoked or expired key.</summary>
    public Task<IntegratorApiKeyChange> RemoveAsync(string keyId, string? removedBy)
        => SaveAsync(removedBy, settings =>
        {
            var key = Find(settings, keyId);
            if (key is null)
                return (IntegratorApiKeyChangeStatus.NotFound, null);
            if (StatusOf(key) == IntegratorApiKeyStatus.Active)
                return (IntegratorApiKeyChangeStatus.StillActive, key);
            settings.Keys.Remove(key);
            return (IntegratorApiKeyChangeStatus.Saved, key);
        });

    private async Task<IntegratorApiKeyChange> SaveAsync(
        string? updatedBy,
        Func<IntegratorApiKeySettings, (IntegratorApiKeyChangeStatus Status, IntegratorApiKey? Key)> apply)
    {
        if (_store is null)
            return Unavailable();

        IntegratorApiKeySettings current = new();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            current = await _store.GetIntegratorApiKeySettings().ConfigureAwait(false);
            var next = current.Clone();
            var (status, key) = apply(next);
            if (status != IntegratorApiKeyChangeStatus.Saved)
                return new IntegratorApiKeyChange(status, current, key);

            next.Revision = Guid.NewGuid().ToString();
            next.UpdatedBy = updatedBy;
            next.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
            if (await _store.TrySetIntegratorApiKeySettings(next, current.Revision).ConfigureAwait(false))
            {
                _provider.Accept(next);
                return new IntegratorApiKeyChange(IntegratorApiKeyChangeStatus.Saved, next, key);
            }
        }

        return new IntegratorApiKeyChange(IntegratorApiKeyChangeStatus.Conflict, current);
    }

    private IIntegratorApiKeyStore Store()
        => _store ?? throw new InvalidOperationException("The storage provider cannot store integrator API keys.");

    private static IntegratorApiKeyChange Unavailable() => new(IntegratorApiKeyChangeStatus.Unavailable, new IntegratorApiKeySettings());

    // Null (all endpoints) stays null; a list must be non-empty, without blanks or duplicates, and
    // name endpoints the platform defines. Ids keep the platform's spelling.
    private static List<string>? ValidateEndpoints(IReadOnlyList<string>? requested, IReadOnlyCollection<string> known, List<string> errors)
    {
        if (requested is null)
            return null;
        if (requested.Count == 0)
        {
            errors.Add("endpoints must list at least one endpoint; omit it for every endpoint.");
            return null;
        }

        var result = new List<string>();
        foreach (var entry in requested)
        {
            var id = entry?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                errors.Add("endpoints must not contain an empty entry.");
                continue;
            }

            var match = known.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                errors.Add($"endpoint '{id}' is not defined on this platform.");
            else if (result.Contains(match, StringComparer.OrdinalIgnoreCase))
                errors.Add($"endpoint '{id}' is listed twice.");
            else
                result.Add(match);
        }

        return result;
    }

    private static IntegratorApiKey? Find(IntegratorApiKeySettings settings, string keyId)
        => settings.Keys.FirstOrDefault(t => string.Equals(t.KeyId, keyId, StringComparison.Ordinal));
}
