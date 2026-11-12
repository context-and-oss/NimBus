using System.Security.Claims;

namespace NimBus.WebApp.Mcp;

/// <summary>Authentication scheme names used only by the operator MCP endpoint.</summary>
public static class McpAuthenticationSchemes
{
    /// <summary>JWT bearer scheme validating tokens issued for the MCP resource.</summary>
    public const string Bearer = "NimBusMcp";

    /// <summary>
    /// The MCP SDK scheme that serves protected-resource metadata and writes the 401 challenge
    /// pointing clients at it. <see cref="Bearer"/> forwards its challenges here.
    /// </summary>
    public const string Challenge = "NimBusMcpChallenge";
}

/// <summary>Authorization policy names for the operator MCP endpoint.</summary>
public static class McpOperatorPolicies
{
    /// <summary>Caller may use the read-only operator tools.</summary>
    public const string Observe = "NimBusMcpObserve";
}

/// <summary>
/// The Entra permissions that admit a caller to the operator tools. Delegated callers carry
/// the scope in <c>scp</c>; approved workloads carry the app role in <c>roles</c>. These
/// gate MCP access only; endpoint roles still decide what each tool returns.
/// </summary>
public static class McpOperatorPermissions
{
    /// <summary>Delegated scope for the read-only tools.</summary>
    public const string ObserveScope = "nimbus.observe";

    /// <summary>Application role for workloads using the read-only tools.</summary>
    public const string ObserveRole = "Nimbus.Observe";

    /// <summary>
    /// Delegated scope for raw payloads. It adds to, and never replaces, the PiiReader grant;
    /// there is deliberately no workload app role for payloads.
    /// </summary>
    public const string PayloadReadScope = "nimbus.payload.read";

    /// <summary>Delegated scope for <c>nimbus_resubmit_message</c> (Spec 035 Phase 2a).</summary>
    public const string ResubmitScope = "nimbus.resubmit";

    /// <summary>Delegated scope for <c>nimbus_skip_message</c>.</summary>
    public const string SkipScope = "nimbus.skip";

    /// <summary>Delegated scope for <c>nimbus_set_message_reported</c>.</summary>
    public const string AnnotateScope = "nimbus.annotate";

    /// <summary>Delegated scope for <c>nimbus_record_classification</c> and the deprecated <c>nimbus_classify_failure</c>.</summary>
    public const string ClassifyScope = "nimbus.classify";

    /// <summary>
    /// Application role for workloads that record failure classifications (Spec 044 §6.6). It
    /// admits <c>nimbus_record_classification</c> only, and only while the Owner allows it; it
    /// changes no message.
    /// </summary>
    public const string ClassifyRole = "Nimbus.Classify";

    /// <summary>
    /// Application role for workloads that resubmit failed messages unattended (Spec 044 Phase 2).
    /// It admits <c>nimbus_prepare_action</c> and <c>nimbus_resubmit_message</c> only, only while
    /// the Owner allows it and the operation journal is available, and within the budget and
    /// per-message cap. There is no role for skip.
    /// </summary>
    public const string ResubmitRole = "Nimbus.Resubmit";

    /// <summary>
    /// Every delegated scope the endpoint understands, in the order the protected-resource
    /// metadata advertises them. <see cref="ClassifyScope"/> and <see cref="ResubmitScope"/> have
    /// workload app roles (<see cref="ClassifyRole"/>, <see cref="ResubmitRole"/>); skip stays with
    /// interactive, delegated callers.
    /// </summary>
    public static readonly IReadOnlyList<string> AllScopes =
        [ObserveScope, PayloadReadScope, ResubmitScope, SkipScope, AnnotateScope, ClassifyScope];

    /// <summary>True when <paramref name="user"/> holds the delegated <paramref name="scope"/>.</summary>
    public static bool HasDelegatedScope(ClaimsPrincipal user, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);
        return HasScope(user, scope);
    }

    /// <summary>True when <paramref name="user"/> holds the observe scope or app role.</summary>
    public static bool CanObserve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return HasScope(user, ObserveScope)
            || user.FindAll("roles").Any(claim => string.Equals(claim.Value, ObserveRole, StringComparison.Ordinal));
    }

    /// <summary>True when <paramref name="user"/> is a workload (no <c>scp</c>) holding <paramref name="role"/>.</summary>
    public static bool IsWorkloadWithRole(ClaimsPrincipal user, string role)
    {
        ArgumentNullException.ThrowIfNull(user);
        return !user.HasClaim(claim => claim.Type == "scp")
            && user.FindAll("roles").Any(claim => string.Equals(claim.Value, role, StringComparison.Ordinal));
    }

    /// <summary>True when <paramref name="user"/> holds the delegated payload scope.</summary>
    public static bool HasPayloadScope(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return HasScope(user, PayloadReadScope);
    }

    private static bool HasScope(ClaimsPrincipal user, string scope)
        => user.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
