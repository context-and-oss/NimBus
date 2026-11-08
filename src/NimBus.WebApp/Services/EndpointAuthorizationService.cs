using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using NimBus.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Resolves the current principal's roles once per request from the cached ACL
/// snapshot, unioned with claim-based compat grants. Registered scoped.
/// </summary>
public partial class EndpointAuthorizationService : IEndpointAuthorizationService
{
    /// <summary>
    /// The internal platform-admin marker claim value. Materialized as a
    /// <c>groups</c> claim by <see cref="LocalDevAuthHandler"/> (local dev),
    /// <see cref="EntraAdminClaimsTransformation"/> (Entra object-id config), and
    /// the Identity role mapping — all of which therefore grant site Owner.
    /// </summary>
    public const string AdminMarkerClaimValue = "EIP_Management";

    /// <summary>Role name honored from token claims for PII reveal (Entra app-role parity).</summary>
    public const string PiiReaderRoleName = "PiiReader";

    private const string OidClaimType = "oid";
    private const string OidLongClaimType = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPlatform _platform;
    private readonly ILogger<EndpointAuthorizationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IAccessControlSnapshotProvider _snapshotProvider;

    // Per-request memo (the service is scoped). Assignment races are benign —
    // both tasks resolve to the same answer for the same principal.
    private Task<CurrentUserAccess>? _resolved;

    public EndpointAuthorizationService(
        IHttpContextAccessor httpContextAccessor,
        IPlatform platform,
        ILogger<EndpointAuthorizationService> logger,
        IConfiguration configuration,
        IAccessControlSnapshotProvider snapshotProvider)
    {
        _httpContextAccessor = httpContextAccessor;
        _platform = platform;
        _logger = logger;
        _configuration = configuration;
        _snapshotProvider = snapshotProvider;
    }

    /// <inheritdoc/>
    public async Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
    {
        if (required == AccessRole.None)
            return true;

        return Satisfies(await GetCurrentUserAccessAsync(), required, endpointId);
    }

    /// <inheritdoc/>
    public async Task<bool> HasRoleFreshAsync(AccessRole required, string endpointId)
    {
        if (required == AccessRole.None)
            return true;

        // Dropping the cached snapshot makes the next read reload from the store; if that load
        // fails, an invalidated generation resolves to the empty snapshot, so store grants fail
        // closed. The per-request memo is bypassed for the same reason.
        _snapshotProvider.Invalidate();
        return Satisfies(await ResolveAsync(), required, endpointId);
    }

    private bool Satisfies(CurrentUserAccess access, AccessRole required, string? endpointId)
    {
        var effective = access.SiteRole;
        if (endpointId != null
            && access.EndpointRoles.TryGetValue(endpointId, out var endpointRole)
            && endpointRole > effective)
        {
            effective = endpointRole;
        }

        if (effective >= required)
            return true;

        LogAuthorizationDeniedRequired(_logger, required, endpointId ?? "(site)", access.SiteRole);
        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> CanReadPiiAsync() => (await GetCurrentUserAccessAsync()).IsPiiReader;

    /// <inheritdoc/>
    public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => _resolved ??= ResolveAsync();

    private async Task<CurrentUserAccess> ResolveAsync()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            LogAuthorizationCheckFailedNoAuthenticatedUser(_logger);
            return new CurrentUserAccess();
        }

        var emails = ResolveEmails(user);
        var objectId = user.FindFirst(OidClaimType)?.Value ?? user.FindFirst(OidLongClaimType)?.Value;
        var identifiers = objectId == null ? emails : emails.Append(objectId).ToList();

        // Compat grants come from claims/config alone so a store outage or an
        // empty store can never lock out configured administrators.
        var compatSiteRole = ResolveCompatSiteRole(user);

        var snapshot = await _snapshotProvider.GetSnapshotAsync();
        var storeSiteRole = ResolveRole(snapshot.Site, identifiers);
        var siteRole = Max(compatSiteRole, storeSiteRole);

        var endpointRoles = new Dictionary<string, AccessRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var (endpointId, acl) in snapshot.Endpoints)
        {
            var role = ResolveRole(acl, identifiers);
            if (role != AccessRole.None)
                endpointRoles[endpointId] = role;
        }

        // Code-defined endpoint RoleAssignments (oid match) keep their historical
        // management rights as endpoint Owner.
        if (objectId != null)
        {
            foreach (var endpoint in _platform.Endpoints)
            {
                if (endpoint.RoleAssignments.Any(ra =>
                        ra.PrincipalId.Equals(objectId, StringComparison.OrdinalIgnoreCase)))
                {
                    endpointRoles[endpoint.Id] = AccessRole.Owner;
                }
            }
        }

        // PII reveal: store grant or an Entra-issued PiiReader app role. Never
        // implied by Owner/compat (spec 021 — masked by default everywhere,
        // including local dev). The explicit dev opt-in flag is safe to read
        // unconditionally: Startup fail-fasts when it is set outside Development.
        var isPiiReader = MatchesAny(snapshot.Site?.PiiReaders, identifiers)
            || user.IsInRole(PiiReaderRoleName)
            || _configuration.GetValue<bool>("Authorization:GrantPiiReaderInDevelopment", false);

        return new CurrentUserAccess
        {
            Email = emails.FirstOrDefault(),
            ObjectId = objectId,
            SiteRole = siteRole,
            IsPiiReader = isPiiReader,
            EndpointRoles = endpointRoles,
        };
    }

    private AccessRole ResolveCompatSiteRole(ClaimsPrincipal user)
    {
        if (_configuration.GetValue<bool>("BypassEndpointAuthorization", false))
        {
            LogEndpointAuthorizationBypassed(_logger);
            return AccessRole.Owner;
        }

        // Restrict the match to the "groups" claim type so non-group claims
        // (e.g. scp, preferred_username) whose value happens to contain the
        // marker cannot elevate privileges.
        var claims = user.Identities.FirstOrDefault()?.Claims;
        return claims != null && claims.Any(c => c.Type == "groups" && c.Value == AdminMarkerClaimValue)
            ? AccessRole.Owner
            : AccessRole.None;
    }

    /// <summary>
    /// The identifiers an access-control entry is matched against: the principal's lowercase
    /// email-bearing claims and its Entra object id.
    /// </summary>
    public static IReadOnlyList<string> ResolveIdentifiers(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var emails = ResolveEmails(user);
        var objectId = user.FindFirst(OidClaimType)?.Value ?? user.FindFirst(OidLongClaimType)?.Value;
        return objectId == null ? emails : emails.Append(objectId).ToList();
    }

    private static List<string> ResolveEmails(ClaimsPrincipal user)
    {
        // DIS's email → upn → name ladder, generalized to a set: an ACL entry may
        // have been granted against any of the principal's email-bearing claims.
        var candidateTypes = new[]
        {
            ClaimTypes.Email, "email", ClaimTypes.Upn, "upn", "preferred_username", "name", ClaimTypes.Name,
        };

        return candidateTypes
            .SelectMany(type => user.FindAll(type))
            .Select(c => c.Value.Trim())
            .Where(v => v.Contains('@', StringComparison.Ordinal))
            .Select(v => v.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    private static AccessRole ResolveRole(AccessControlList? acl, IReadOnlyCollection<string> identifiers)
    {
        if (acl == null)
            return AccessRole.None;
        if (MatchesAny(acl.Owners, identifiers)) return AccessRole.Owner;
        if (MatchesAny(acl.Contributors, identifiers)) return AccessRole.Contributor;
        if (MatchesAny(acl.Readers, identifiers)) return AccessRole.Reader;
        return AccessRole.None;
    }

    private static bool MatchesAny(IEnumerable<string>? entries, IReadOnlyCollection<string> identifiers)
        => entries != null && entries.Any(entry =>
            identifiers.Contains(entry.Trim(), StringComparer.OrdinalIgnoreCase));

    private static AccessRole Max(AccessRole a, AccessRole b) => a >= b ? a : b;

    /// <inheritdoc/>
    public string? GetCurrentUserName()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context?.User == null)
        {
            return null;
        }

        // Try to get name from various claim types
        var name = context.User.FindFirst(c => c.Type.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value;

        if (string.IsNullOrEmpty(name))
        {
            name = context.User.FindFirst(ClaimTypes.Name)?.Value;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = context.User.FindFirst("preferred_username")?.Value;
        }

        return name;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Authorization denied: required {Required} on '{EndpointId}', principal has site {SiteRole}")]
    private static partial void LogAuthorizationDeniedRequired(ILogger logger, AccessRole required, string endpointId, AccessRole siteRole);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authorization check failed: no authenticated user principal available")]
    private static partial void LogAuthorizationCheckFailedNoAuthenticatedUser(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Endpoint authorization bypassed - BypassEndpointAuthorization is enabled")]
    private static partial void LogEndpointAuthorizationBypassed(ILogger logger);
}
