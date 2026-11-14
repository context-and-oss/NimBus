using System.Collections.Generic;

namespace NimBus.WebApp.Services;

/// <summary>
/// The access ladder (spec 026, mirroring DIS): each level implies everything
/// below it. <c>PiiReader</c> is deliberately NOT part of this ladder —
/// it is an orthogonal, separately-granted capability.
/// </summary>
public enum AccessRole
{
    None = 0,
    Reader = 1,
    Contributor = 2,
    Owner = 3,
}

/// <summary>
/// The current principal's resolved access: identity keys, effective site role
/// (store grants unioned with claim-based compat grants), per-endpoint roles,
/// and the PII-reveal capability. Resolved once per request.
/// </summary>
public sealed class CurrentUserAccess
{
    /// <summary>Primary email resolved from claims, lowercase; null when the principal carries none.</summary>
    public string? Email { get; init; }

    /// <summary>Entra object id (oid claim); null for non-Entra principals.</summary>
    public string? ObjectId { get; init; }

    /// <summary>Effective site-wide role (max of store grant and compat grant).</summary>
    public AccessRole SiteRole { get; init; }

    /// <summary>Whether the principal may view raw event payloads.</summary>
    public bool IsPiiReader { get; init; }

    /// <summary>
    /// Effective per-endpoint roles from endpoint ACLs and code-defined
    /// RoleAssignments. Endpoints without an explicit grant are absent — the
    /// caller unions with <see cref="SiteRole"/> for the effective answer.
    /// </summary>
    public IReadOnlyDictionary<string, AccessRole> EndpointRoles { get; init; }
        = new Dictionary<string, AccessRole>();
}
