using System.Diagnostics.CodeAnalysis;
using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace NimBus.Extensions.Http;

/// <summary>
/// Caches one Microsoft Entra access token for a credential and a set of scopes, refreshing it
/// shortly before it expires. Concurrent callers share one refresh.
/// </summary>
/// <remarks>
/// <c>IHttpClientFactory</c> recreates message handlers every few minutes, so the cache lives
/// outside <see cref="AzureBearerTokenHandler"/> and is shared by every handler instance of a
/// registration.
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The cache is shared by every handler instance for the life of the process, and the SemaphoreSlim never allocates a wait handle (AvailableWaitHandle is unused), so there is nothing to release.")]
public sealed class AzureAccessTokenCache
{
    /// <summary>How long before <see cref="AccessToken.ExpiresOn"/> the token is refreshed.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly TokenRequestContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AccessToken? _token;

    /// <summary>
    /// Creates a cache for tokens that <paramref name="credential"/> issues for <paramref name="scopes"/>.
    /// </summary>
    /// <param name="credential">The credential that issues tokens.</param>
    /// <param name="scopes">
    /// The resource scopes, for example <c>https://contoso.crm.dynamics.com/.default</c> (Dataverse),
    /// <c>https://api.businesscentral.dynamics.com/.default</c> (Business Central) or
    /// <c>https://contoso.operations.dynamics.com/.default</c> (Finance and Operations).
    /// </param>
    public AzureAccessTokenCache(TokenCredential credential, params string[] scopes)
        : this(credential, TimeProvider.System, scopes)
    {
    }

    /// <summary>
    /// Creates a cache that reads the current time from <paramref name="timeProvider"/>.
    /// </summary>
    /// <param name="credential">The credential that issues tokens.</param>
    /// <param name="timeProvider">The clock used to decide when the token needs refreshing.</param>
    /// <param name="scopes">The resource scopes.</param>
    public AzureAccessTokenCache(TokenCredential credential, TimeProvider timeProvider, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (scopes is null || scopes.Length == 0 || Array.Exists(scopes, string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty scope is required.", nameof(scopes));
        }

        _credential = credential;
        _timeProvider = timeProvider;
        _context = new TokenRequestContext(scopes);
    }

    /// <summary>
    /// Returns the cached token, or requests a new one when there is none or it is about to expire.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for, or the request of, a token.</param>
    /// <returns>The bearer token value.</returns>
    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_token is { } cached && !NeedsRefresh(cached))
        {
            return cached.Token;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is { } current && !NeedsRefresh(current))
            {
                return current.Token;
            }

            var fresh = await _credential.GetTokenAsync(_context, cancellationToken).ConfigureAwait(false);
            _token = fresh;
            return fresh.Token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool NeedsRefresh(AccessToken token)
    {
        var now = _timeProvider.GetUtcNow();
        if (token.RefreshOn is { } refreshOn && now >= refreshOn)
        {
            return true;
        }

        return now >= token.ExpiresOn - RefreshMargin;
    }
}
