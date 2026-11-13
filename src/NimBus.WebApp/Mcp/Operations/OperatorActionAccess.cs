using NimBus.MessageStore;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>An action the operator MCP tools can take on a message (Spec 035 Phase 2a).</summary>
public enum OperatorAction
{
    /// <summary>Replay the message's latest payload.</summary>
    Resubmit,

    /// <summary>Skip the message, which can release later messages in its session.</summary>
    Skip,

    /// <summary>Set or clear the "reported" marker.</summary>
    Report,

    /// <summary>Request an AI classification of the failure.</summary>
    Classify,
}

/// <summary>
/// Decides whether the current MCP caller may take an <see cref="OperatorAction"/>. An Entra
/// caller needs the action's delegated scope; a workload token, which carries app roles and no
/// <c>scp</c>, qualifies for <see cref="OperatorAction.Classify"/> (recording) with the
/// <c>Nimbus.Classify</c> role and the Owner's workload-recording switch (Spec 044 §6.6), and for
/// <see cref="OperatorAction.Resubmit"/> with the <c>Nimbus.Resubmit</c> role, the Owner's
/// workload-resubmit switch and an available operation journal (Spec 044 Phase 2). Never for
/// skip. Local development has no scopes. The site Owner's
/// MCP access policy (Spec 037) must also allow the action, the caller's client and the
/// endpoint. The endpoint role (Contributor) is checked separately, and again against fresh
/// access-control lists when a command runs.
/// </summary>
public sealed class OperatorActionAccess
{
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly McpOperatorRuntime _runtime;
    private readonly IMcpAccessPolicyProvider _policies;
    private readonly McpAccessRefusals _refusals;
    private readonly OperatorOperationJournal _journal;

    /// <summary>Creates the check for one request.</summary>
    public OperatorActionAccess(
        IEndpointAuthorizationService authorization,
        IHttpContextAccessor httpContextAccessor,
        McpOperatorRuntime runtime,
        IMcpAccessPolicyProvider policies,
        McpAccessRefusals refusals,
        OperatorOperationJournal journal)
    {
        _journal = journal;
        _authorization = authorization;
        _httpContextAccessor = httpContextAccessor;
        _runtime = runtime;
        _policies = policies;
        _refusals = refusals;
    }

    /// <summary>The delegated scope <paramref name="action"/> requires.</summary>
    public static string ScopeOf(OperatorAction action) => action switch
    {
        OperatorAction.Resubmit => McpOperatorPermissions.ResubmitScope,
        OperatorAction.Skip => McpOperatorPermissions.SkipScope,
        OperatorAction.Report => McpOperatorPermissions.AnnotateScope,
        OperatorAction.Classify => McpOperatorPermissions.ClassifyScope,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>The name agents see for <paramref name="action"/>.</summary>
    public static string NameOf(OperatorAction action) => action.ToString().ToLowerInvariant();

    /// <summary>
    /// True when the access policy allows <paramref name="action"/> for the caller's client
    /// and the caller holds the action's scope.
    /// </summary>
    public bool HasScope(OperatorAction action) => PolicyAllows(action) && HoldsScope(action);

    /// <summary>
    /// Throws <c>[PermissionDenied]</c>, and audits the refusal, unless the access policy allows
    /// <paramref name="action"/> for the caller's client and the caller holds the scope.
    /// </summary>
    public async Task RequireScopeAsync(OperatorAction action)
    {
        if (!PolicyAllows(action))
        {
            await RecordAsync(McpRefusalReason.Tool, action, endpointId: null).ConfigureAwait(false);
            throw OperatorToolErrors.PermissionDenied($"{NameOf(action)} is turned off by an administrator.");
        }

        if (!HoldsScope(action))
        {
            await RecordAsync(McpRefusalReason.Scope, action, endpointId: null).ConfigureAwait(false);
            throw OperatorToolErrors.PermissionDenied(action switch
            {
                OperatorAction.Classify => $"classify requires the delegated {ScopeOf(action)} scope, or for a workload the {McpOperatorPermissions.ClassifyRole} app role with workload recording allowed by an administrator.",
                OperatorAction.Resubmit => $"resubmit requires the delegated {ScopeOf(action)} scope, or for a workload the {McpOperatorPermissions.ResubmitRole} app role with unattended resubmits allowed by an administrator.",
                OperatorAction.Report => $"report requires the delegated {ScopeOf(action)} scope, or for a workload the {McpOperatorPermissions.AnnotateRole} app role with workload reporting allowed by an administrator.",
                _ => $"{NameOf(action)} requires the delegated {ScopeOf(action)} scope. Workload (app-only) tokens cannot {NameOf(action)} messages.",
            });
        }
    }

    /// <summary>
    /// Throws <c>[PermissionDenied]</c>, and audits the refusal, unless the caller holds the
    /// delegated scope itself. A workload app role never qualifies: it admits recording only, not
    /// a paid provider analysis (Spec 044 §6.6).
    /// </summary>
    public async Task RequireDelegatedScopeAsync(OperatorAction action)
    {
        await RequireScopeAsync(action).ConfigureAwait(false);
        if (HoldsDelegatedScope(action))
            return;

        await RecordAsync(McpRefusalReason.Scope, action, endpointId: null).ConfigureAwait(false);
        throw OperatorToolErrors.PermissionDenied(
            $"{NameOf(action)} through this tool requires the delegated {ScopeOf(action)} scope. Workload (app-only) tokens can only record classifications.");
    }

    /// <summary>
    /// True when the caller acts as an unattended workload for <paramref name="action"/>: an
    /// app-only token admitted by its app role, not a delegated scope. Budgets and caps apply.
    /// </summary>
    public bool IsWorkloadFor(OperatorAction action) => !HoldsDelegatedScope(action) && HoldsScope(action);

    /// <summary>
    /// True when the caller is an app-only workload token, so no person is present to say yes
    /// (Spec 046 §3.3). Local development is always a person.
    /// </summary>
    public bool IsWorkloadCaller
        => _httpContextAccessor.HttpContext?.User is { } user && McpCaller.From(user, _runtime.Mode).IsWorkload;

    /// <summary>
    /// The Owner's limits on unattended resubmits (Spec 044 Phase 2): resubmits per workload
    /// client in a rolling 24 hours, and unsuccessful workload resubmits per message.
    /// </summary>
    public (int PerDay, int PerMessage) WorkloadResubmitLimits()
    {
        var settings = _policies.Current?.Settings;
        return (settings?.WorkloadResubmitsPerDay ?? NimBus.MessageStore.States.McpAccessSettings.DefaultWorkloadResubmitsPerDay,
            settings?.WorkloadResubmitsPerMessage ?? NimBus.MessageStore.States.McpAccessSettings.DefaultWorkloadResubmitsPerMessage);
    }

    /// <summary>True when the access policy allows changes on <paramref name="endpointId"/>.</summary>
    public bool MayChangeOn(string endpointId) => _policies.Current?.MayChangeOn(endpointId) == true;

    /// <summary>Throws <c>[PermissionDenied]</c>, and audits the refusal, unless changes are allowed on <paramref name="endpointId"/>.</summary>
    public async Task RequireChangeAllowedOnAsync(OperatorAction action, string endpointId)
    {
        if (MayChangeOn(endpointId))
            return;

        await RecordAsync(McpRefusalReason.Endpoint, action, endpointId).ConfigureAwait(false);
        throw OperatorToolErrors.PermissionDenied($"Changes are not allowed on endpoint '{endpointId}' over MCP.");
    }

    /// <summary>
    /// The actions the caller may take on <paramref name="row"/> right now: Contributor on the
    /// endpoint, the action's scope, and a state the action accepts.
    /// </summary>
    public async Task<IReadOnlyList<string>> EligibleActionsAsync(UnresolvedEvent row, string endpointId, bool classificationAvailable)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!MayChangeOn(endpointId) || !await _authorization.HasRoleAsync(AccessRole.Contributor, endpointId).ConfigureAwait(false))
            return [];

        var actions = new List<string>();
        if (OperatorCommandCoordinator.IsActionable(row, OperatorChannel.Mcp))
        {
            if (HasScope(OperatorAction.Resubmit)) actions.Add(NameOf(OperatorAction.Resubmit));
            if (HasScope(OperatorAction.Skip)) actions.Add(NameOf(OperatorAction.Skip));
        }

        if (HasScope(OperatorAction.Report)) actions.Add(NameOf(OperatorAction.Report));
        if (classificationAvailable && HasScope(OperatorAction.Classify)
            && row.ResolutionStatus is ResolutionStatus.Failed or ResolutionStatus.DeadLettered)
        {
            actions.Add(NameOf(OperatorAction.Classify));
        }

        return actions;
    }

    /// <summary>
    /// The actions the caller holds the scope for and may take on at least one endpoint, as
    /// Contributor there or site-wide.
    /// </summary>
    public async Task<IReadOnlyList<string>> PermittedActionsAsync(bool classificationAvailable)
    {
        var access = await _authorization.GetCurrentUserAccessAsync().ConfigureAwait(false);
        var contributorSomewhere = access.SiteRole >= AccessRole.Contributor
            || access.EndpointRoles.Values.Any(role => role >= AccessRole.Contributor);
        if (!contributorSomewhere)
            return [];

        return Enum.GetValues<OperatorAction>()
            .Where(action => action != OperatorAction.Classify || classificationAvailable)
            .Where(HasScope)
            .Select(NameOf)
            .ToList();
    }

    /// <summary>True when the caller holds the scope and Contributor on <paramref name="endpointId"/>.</summary>
    public async Task<bool> MayAsync(OperatorAction action, string endpointId)
        => HasScope(action) && MayChangeOn(endpointId)
           && await _authorization.HasRoleAsync(AccessRole.Contributor, endpointId).ConfigureAwait(false);

    /// <summary>
    /// Checks the action scope, the policy's change scope and Contributor against the current
    /// endpoint ACL.
    /// </summary>
    public async Task<bool> MayFreshAsync(OperatorAction action, string endpointId)
        => HasScope(action) && MayChangeOn(endpointId)
           && await _authorization.HasRoleFreshAsync(AccessRole.Contributor, endpointId).ConfigureAwait(false);

    private bool PolicyAllows(OperatorAction action)
    {
        var policy = _policies.Current;
        return policy is not null
            && policy.AllowsAction(action)
            && policy.ClientMayChange(McpCaller.From(_httpContextAccessor.HttpContext?.User, _runtime.Mode));
    }

    private bool HoldsScope(OperatorAction action)
    {
        if (HoldsDelegatedScope(action))
            return true;

        var user = _httpContextAccessor.HttpContext?.User;
        if (user is null)
            return false;

        return action switch
        {
            // Spec 044 §6.6: an unattended workload may record classifications when it holds the
            // Nimbus.Classify app role and the Owner allows workload recording. It changes no message.
            OperatorAction.Classify => _policies.Current?.AllowsWorkloadClassification == true
                && McpOperatorPermissions.IsWorkloadWithRole(user, McpOperatorPermissions.ClassifyRole),
            // Spec 044 Phase 2: an unattended workload may resubmit with the Nimbus.Resubmit app role
            // while the Owner allows it and the journal can record, budget and cap the command.
            OperatorAction.Resubmit => _policies.Current?.AllowsWorkloadResubmit == true
                && _journal.IsAvailable
                && McpOperatorPermissions.IsWorkloadWithRole(user, McpOperatorPermissions.ResubmitRole),
            // Spec 044 Phase 3: marking reported changes nothing on the bus.
            OperatorAction.Report => _policies.Current?.AllowsWorkloadAnnotation == true
                && McpOperatorPermissions.IsWorkloadWithRole(user, McpOperatorPermissions.AnnotateRole),
            _ => false,
        };
    }

    private bool HoldsDelegatedScope(OperatorAction action)
    {
        if (_runtime.Mode == McpAuthenticationMode.LocalDevelopment)
            return true;

        var user = _httpContextAccessor.HttpContext?.User;
        return user is not null && McpOperatorPermissions.HasDelegatedScope(user, ScopeOf(action));
    }

    /// <summary>Audits a refusal of <paramref name="action"/> as <c>McpAccessRefused</c>, deduplicated per window.</summary>
    public Task RecordRefusalAsync(McpRefusalReason reason, OperatorAction action, string? endpointId)
        => RecordAsync(reason, action, endpointId);

    private Task RecordAsync(McpRefusalReason reason, OperatorAction action, string? endpointId)
    {
        var http = _httpContextAccessor.HttpContext;
        return http is null
            ? Task.CompletedTask
            : _refusals.RecordAsync(http, McpCaller.From(http.User, _runtime.Mode), reason, NameOf(action), endpointId);
    }
}
