using System.Security.Claims;
using NimBus.WebApp.Services.AgentApiKeys;

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

    /// <summary>
    /// The policy scheme the MCP policy names in Entra mode (Spec 052 §6.2). It forwards a request
    /// that carries an MCP agent key to the key scheme, when the deployment allows keys, and
    /// everything else to <see cref="Bearer"/>; so one request is authenticated by exactly one scheme.
    /// </summary>
    public const string Auth = "NimBusMcpAuth";
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
    public const string ObserveRole = "Nimbus.Workload.Observe";

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
    /// Delegated scope for <c>nimbus_settle_handoff</c> (Spec 035 Phase 3). The Phase 3 scopes
    /// have no workload app role: these tools stay with a person's agent.
    /// </summary>
    public const string SettleScope = "nimbus.settle";

    /// <summary>Delegated scope for <c>nimbus_reprocess_deferred</c> and <c>nimbus_skip_deferred_tracking</c> (Spec 035 Phase 3).</summary>
    public const string RecoverScope = "nimbus.recover";

    /// <summary>
    /// Delegated scope for <c>nimbus_resubmit_with_changes</c> (Spec 035 Phase 3). It adds to, and
    /// never replaces, the Contributor and PiiReader grants.
    /// </summary>
    public const string EditScope = "nimbus.edit";

    /// <summary>
    /// Application role for workloads that record failure classifications (Spec 044 §6.6). It
    /// admits <c>nimbus_record_classification</c> only, and only while the Owner allows it; it
    /// changes no message.
    /// </summary>
    public const string ClassifyRole = "Nimbus.Workload.Classify";

    /// <summary>
    /// Application role for workloads that resubmit failed messages unattended (Spec 044 Phase 2).
    /// It admits <c>nimbus_prepare_action</c> and <c>nimbus_resubmit_message</c> only, only while
    /// the Owner allows it and the operation journal is available, and within the budget and
    /// per-message cap. There is no role for skip.
    /// </summary>
    public const string ResubmitRole = "Nimbus.Workload.Resubmit";

    /// <summary>
    /// Application role for workloads that mark messages reported with a ticket id (Spec 044
    /// Phase 3). It admits <c>nimbus_set_message_reported</c> only, while the Owner allows it.
    /// </summary>
    public const string AnnotateRole = "Nimbus.Workload.Annotate";

    /// <summary>
    /// Every delegated scope the endpoint understands, in the order the protected-resource
    /// metadata advertises them. <see cref="ClassifyScope"/> and <see cref="ResubmitScope"/> have
    /// workload app roles (<see cref="ClassifyRole"/>, <see cref="ResubmitRole"/>); skip and the
    /// Phase 3 scopes (settle, recover, edit) stay with interactive, delegated callers.
    /// </summary>
    public static readonly IReadOnlyList<string> AllScopes =
        [ObserveScope, PayloadReadScope, ResubmitScope, SkipScope, AnnotateScope, ClassifyScope, SettleScope, RecoverScope, EditScope];

    /// <summary>
    /// Every workload app role the endpoint understands. Entra requires app-role and scope values
    /// on one app registration to be unique ignoring case, so no role may equal a scope in
    /// <see cref="AllScopes"/> (#407).
    /// </summary>
    public static readonly IReadOnlyList<string> AllRoles = [ObserveRole, ClassifyRole, ResubmitRole, AnnotateRole];

    // The role names before #407. Entra refuses them next to the scopes they shadow, so no tenant
    // can issue them for the MCP resource; they stay accepted so existing tokens keep working.
    private static readonly Dictionary<string, string> LegacyRoles = new(StringComparer.Ordinal)
    {
        [ObserveRole] = "Nimbus.Observe",
        [ClassifyRole] = "Nimbus.Classify",
        [ResubmitRole] = "Nimbus.Resubmit",
        [AnnotateRole] = "Nimbus.Annotate",
    };

    /// <summary>True when <paramref name="user"/> holds the delegated <paramref name="scope"/>.</summary>
    public static bool HasDelegatedScope(ClaimsPrincipal user, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);
        return HasScope(user, scope);
    }

    /// <summary>
    /// True when <paramref name="user"/> holds the observe scope or app role, or is an MCP agent key
    /// (Spec 052), which always holds observe.
    /// </summary>
    public static bool CanObserve(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return HasScope(user, ObserveScope) || HasRole(user, ObserveRole)
            || McpAgentKeyDefaults.Read(user)?.Holds(McpAgentKeyDefaults.ObservePermission) == true;
    }

    /// <summary>
    /// True when <paramref name="user"/> is a workload (no <c>scp</c>) holding <paramref name="role"/>:
    /// an Entra app role, or for an MCP agent key the matching permission of its grant (Spec 052 §6.4).
    /// </summary>
    public static bool IsWorkloadWithRole(ClaimsPrincipal user, string role)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (McpAgentKeyDefaults.Read(user) is { } key)
            return KeyPermissionOf(role) is { } permission && key.Holds(permission);

        return !user.HasClaim(claim => claim.Type == "scp") && HasRole(user, role);
    }

    // The key permission that stands for a workload app role. NimBus never mints roles claims.
    private static string? KeyPermissionOf(string role) => role switch
    {
        ObserveRole => McpAgentKeyDefaults.ObservePermission,
        ClassifyRole => McpAgentKeyDefaults.ClassifyPermission,
        AnnotateRole => McpAgentKeyDefaults.AnnotatePermission,
        ResubmitRole => McpAgentKeyDefaults.ResubmitPermission,
        _ => null,
    };

    /// <summary>True when <paramref name="user"/> holds the delegated payload scope.</summary>
    public static bool HasPayloadScope(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return HasScope(user, PayloadReadScope);
    }

    private static bool HasRole(ClaimsPrincipal user, string role)
    {
        var legacy = LegacyRoles.GetValueOrDefault(role);
        return user.FindAll("roles").Any(claim =>
            string.Equals(claim.Value, role, StringComparison.Ordinal)
            || (legacy is not null && string.Equals(claim.Value, legacy, StringComparison.Ordinal)));
    }

    private static bool HasScope(ClaimsPrincipal user, string scope)
        => user.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
