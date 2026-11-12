using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.DashboardTokens;

/// <summary>How a dashboard token change ended.</summary>
public enum DashboardTokenChangeStatus
{
    /// <summary>The change was saved.</summary>
    Saved,

    /// <summary>The request failed validation; see the errors.</summary>
    Invalid,

    /// <summary>No token has that id.</summary>
    NotFound,

    /// <summary>The token must be revoked (or have expired) before it can be removed.</summary>
    StillActive,

    /// <summary>Concurrent saves kept winning; nothing was saved.</summary>
    Conflict,

    /// <summary>The storage provider cannot store dashboard tokens.</summary>
    Unavailable,
}

/// <summary>The outcome of a dashboard token change.</summary>
/// <param name="Status">How the change ended.</param>
/// <param name="Settings">The tokens after the change (or as read, when nothing was saved).</param>
/// <param name="Token">The token the change was about, when there is one.</param>
/// <param name="FullToken">The full token; only set when one was just issued.</param>
/// <param name="Errors">Validation errors for <see cref="DashboardTokenChangeStatus.Invalid"/>.</param>
public sealed record DashboardTokenChange(
    DashboardTokenChangeStatus Status,
    DashboardTokenSettings Settings,
    DashboardToken? Token = null,
    string? FullToken = null,
    IReadOnlyList<string>? Errors = null);

/// <summary>A dashboard token's status.</summary>
public enum DashboardTokenStatus
{
    /// <summary>The token authenticates.</summary>
    Active,

    /// <summary>An Owner revoked the token.</summary>
    Revoked,

    /// <summary>The token's expiry has passed.</summary>
    Expired,
}

/// <summary>
/// Issues, revokes and removes Monitor dashboard tokens. Each change touches one token, so a
/// revision conflict is resolved by re-reading and re-applying it, a few times at most. A saved
/// change is handed to <see cref="IDashboardTokenProvider.Accept"/> so this instance applies it at once.
/// </summary>
public sealed class DashboardTokenService
{
    /// <summary>The longest name a token may carry.</summary>
    public const int MaxNameLength = 100;

    private const int MaxAttempts = 3;

    private readonly IDashboardTokenStore? _store;
    private readonly IDashboardTokenProvider _provider;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public DashboardTokenService(IDashboardTokenProvider provider, TimeProvider time, IDashboardTokenStore? store = null)
    {
        _store = store;
        _provider = provider;
        _time = time;
    }

    /// <summary>Whether the storage provider can store dashboard tokens.</summary>
    public bool IsAvailable => _store is not null;

    /// <summary>The issued tokens and when each was last used.</summary>
    public async Task<(DashboardTokenSettings Settings, IReadOnlyDictionary<string, DateTime> LastUsed)> ListAsync()
    {
        var store = Store();
        var settings = await store.GetDashboardTokenSettings().ConfigureAwait(false);
        var usage = await store.GetDashboardTokenUsage().ConfigureAwait(false);
        var lastUsed = usage
            .GroupBy(u => u.TokenId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(u => u.LastUsedAtUtc), StringComparer.Ordinal);
        return (settings, lastUsed);
    }

    /// <summary>The status a token has now.</summary>
    public DashboardTokenStatus StatusOf(DashboardToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.RevokedAtUtc is not null)
            return DashboardTokenStatus.Revoked;
        if (token.ExpiresAtUtc is { } expires && _time.GetUtcNow().UtcDateTime >= expires)
            return DashboardTokenStatus.Expired;
        return DashboardTokenStatus.Active;
    }

    /// <summary>Issues a token.</summary>
    public async Task<DashboardTokenChange> CreateAsync(string? name, DateTime? expiresAtUtc, string? createdBy)
    {
        if (_store is null)
            return Unavailable();

        var now = _time.GetUtcNow().UtcDateTime;
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
            errors.Add("name is required.");
        else if (name.Trim().Length > MaxNameLength)
            errors.Add($"name must be at most {MaxNameLength} characters.");
        if (expiresAtUtc is { } expires && expires.ToUniversalTime() <= now)
            errors.Add("expiresAtUtc must be in the future.");
        if (errors.Count > 0)
            return new DashboardTokenChange(DashboardTokenChangeStatus.Invalid, await _store.GetDashboardTokenSettings().ConfigureAwait(false), Errors: errors);

        var generated = SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix);
        var token = new DashboardToken
        {
            TokenId = generated.Id,
            Name = name!.Trim(),
            SecretHash = generated.SecretHash,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            ExpiresAtUtc = expiresAtUtc?.ToUniversalTime(),
        };

        var change = await SaveAsync(createdBy, settings =>
        {
            settings.Tokens.Add(token);
            return (DashboardTokenChangeStatus.Saved, token);
        }).ConfigureAwait(false);
        return change.Status == DashboardTokenChangeStatus.Saved ? change with { FullToken = generated.Value } : change;
    }

    /// <summary>Revokes a token. Revoking a revoked token keeps the first revocation.</summary>
    public Task<DashboardTokenChange> RevokeAsync(string tokenId, string? revokedBy)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return SaveAsync(revokedBy, settings =>
        {
            var token = Find(settings, tokenId);
            if (token is null)
                return (DashboardTokenChangeStatus.NotFound, null);
            if (token.RevokedAtUtc is null)
            {
                token.RevokedAtUtc = now;
                token.RevokedBy = revokedBy;
            }
            return (DashboardTokenChangeStatus.Saved, token);
        });
    }

    /// <summary>Removes a revoked or expired token.</summary>
    public Task<DashboardTokenChange> RemoveAsync(string tokenId, string? removedBy)
        => SaveAsync(removedBy, settings =>
        {
            var token = Find(settings, tokenId);
            if (token is null)
                return (DashboardTokenChangeStatus.NotFound, null);
            if (StatusOf(token) == DashboardTokenStatus.Active)
                return (DashboardTokenChangeStatus.StillActive, token);
            settings.Tokens.Remove(token);
            return (DashboardTokenChangeStatus.Saved, token);
        });

    private async Task<DashboardTokenChange> SaveAsync(
        string? updatedBy,
        Func<DashboardTokenSettings, (DashboardTokenChangeStatus Status, DashboardToken? Token)> apply)
    {
        if (_store is null)
            return Unavailable();

        DashboardTokenSettings current = new();
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            current = await _store.GetDashboardTokenSettings().ConfigureAwait(false);
            var next = current.Clone();
            var (status, token) = apply(next);
            if (status != DashboardTokenChangeStatus.Saved)
                return new DashboardTokenChange(status, current, token);

            next.Revision = Guid.NewGuid().ToString();
            next.UpdatedBy = updatedBy;
            next.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
            if (await _store.TrySetDashboardTokenSettings(next, current.Revision).ConfigureAwait(false))
            {
                _provider.Accept(next);
                return new DashboardTokenChange(DashboardTokenChangeStatus.Saved, next, token);
            }
        }

        return new DashboardTokenChange(DashboardTokenChangeStatus.Conflict, current);
    }

    private IDashboardTokenStore Store()
        => _store ?? throw new InvalidOperationException("The storage provider cannot store dashboard tokens.");

    private static DashboardTokenChange Unavailable() => new(DashboardTokenChangeStatus.Unavailable, new DashboardTokenSettings());

    private static DashboardToken? Find(DashboardTokenSettings settings, string tokenId)
        => settings.Tokens.FirstOrDefault(t => string.Equals(t.TokenId, tokenId, StringComparison.Ordinal));
}
