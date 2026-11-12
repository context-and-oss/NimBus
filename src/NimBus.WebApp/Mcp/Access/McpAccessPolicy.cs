using NimBus.MessageStore.States;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>Why the MCP access policy refused a call. The names are recorded in the audit log.</summary>
public enum McpRefusalReason
{
    /// <summary>The caller is not on the people allowlist.</summary>
    Person,

    /// <summary>The client application is not approved.</summary>
    Client,

    /// <summary>App-only tokens are not accepted.</summary>
    Workload,

    /// <summary>The tool or capability is switched off, or the client may not change messages.</summary>
    Tool,

    /// <summary>Changes are not allowed on the endpoint.</summary>
    Endpoint,

    /// <summary>The caller's token lacks the delegated scope.</summary>
    Scope,
}

/// <summary>
/// The MCP access policy one instance enforces (Spec 037): the site Owner's saved settings,
/// narrowed by the deployment. Immutable; the provider swaps whole snapshots.
/// </summary>
public sealed class McpAccessPolicy
{
    private readonly HashSet<string> _hidden;
    private readonly HashSet<string> _changeOn;
    private readonly HashSet<string> _people;
    private readonly Dictionary<string, McpApprovedClient> _clients;

    private McpAccessPolicy(McpAccessSettings settings, int? requestLimit, int? mutationLimit)
    {
        Settings = settings;
        RequestLimit = requestLimit;
        MutationLimit = mutationLimit;
        _hidden = settings.Endpoints.Visibility == McpEndpointVisibility.AllExcept
            ? new HashSet<string>(settings.Endpoints.Hidden, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _changeOn = new HashSet<string>(settings.Endpoints.ChangeOn, StringComparer.OrdinalIgnoreCase);
        _people = new HashSet<string>(settings.People.Principals.Select(p => p.Principal.Trim()), StringComparer.OrdinalIgnoreCase);
        _clients = settings.Clients.Approved
            .GroupBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The saved settings, or the defaults when nothing was saved. Do not modify.</summary>
    public McpAccessSettings Settings { get; }

    /// <summary>Whether <c>/mcp</c> serves calls.</summary>
    public bool Enabled => Settings.Enabled;

    /// <summary>The effective MCP request limit per window, or null when rate limiting is off.</summary>
    public int? RequestLimit { get; }

    /// <summary>The effective message-change limit per window, or null when rate limiting is off.</summary>
    public int? MutationLimit { get; }

    /// <summary>Whether raw payloads may be returned.</summary>
    public bool AllowsPayloads => Settings.Capabilities.Payloads;

    /// <summary>
    /// Whether workloads with the <c>Nimbus.Classify</c> app role may record classifications: the
    /// Owner allowed it and workload tokens are accepted (Spec 044 §6.6).
    /// </summary>
    public bool AllowsWorkloadClassification => Settings.AllowWorkloads && Settings.AllowWorkloadClassification;

    /// <summary>
    /// Whether workloads with the <c>Nimbus.Resubmit</c> app role may resubmit: the Owner allowed
    /// it and workload tokens are accepted (Spec 044 Phase 2). The journal must also be available;
    /// <c>OperatorActionAccess</c> checks that.
    /// </summary>
    public bool AllowsWorkloadResubmit => Settings.AllowWorkloads && Settings.AllowWorkloadResubmit;

    /// <summary>
    /// Builds the policy from <paramref name="settings"/>, with each limit lowered to the
    /// deployment's value. When rate limiting is off the saved limits are ignored.
    /// </summary>
    public static McpAccessPolicy From(McpAccessSettings settings, RateLimitOptions rateLimits)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(rateLimits);

        var copy = settings.Clone();
        if (!rateLimits.Enabled)
            return new McpAccessPolicy(copy, null, null);

        return new McpAccessPolicy(
            copy,
            Lower(copy.Limits.RequestsPerWindow, rateLimits.Mcp.PermitLimit),
            Lower(copy.Limits.MutationsPerWindow, rateLimits.McpMutations.PermitLimit));
    }

    /// <summary>Whether the capability behind <paramref name="action"/> is switched on.</summary>
    public bool AllowsAction(OperatorAction action) => action switch
    {
        OperatorAction.Resubmit => Settings.Capabilities.Resubmit,
        OperatorAction.Skip => Settings.Capabilities.Skip,
        OperatorAction.Report => Settings.Capabilities.Report,
        OperatorAction.Classify => Settings.Capabilities.Classify,
        _ => false,
    };

    /// <summary>
    /// Checks the people, client and workload rules for <paramref name="caller"/>; null when
    /// the caller is admitted. Local development is always admitted.
    /// </summary>
    public McpRefusalReason? CheckCaller(McpCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.LocalDevelopment)
            return null;

        if (caller.IsWorkload && !Settings.AllowWorkloads)
            return McpRefusalReason.Workload;

        if (Settings.Clients.Mode == McpClientMode.Approved
            && (caller.ClientId is null || !_clients.ContainsKey(caller.ClientId)))
        {
            return McpRefusalReason.Client;
        }

        // A workload has no person; the client and workload rules cover it.
        if (!caller.IsWorkload
            && Settings.People.Mode == McpPeopleMode.Listed
            && !caller.Identifiers.Any(_people.Contains))
        {
            return McpRefusalReason.Person;
        }

        return null;
    }

    /// <summary>Whether <paramref name="caller"/>'s client may use the tools that change messages.</summary>
    public bool ClientMayChange(McpCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.LocalDevelopment || Settings.Clients.Mode == McpClientMode.Any)
            return true;

        return caller.ClientId is not null && _clients.TryGetValue(caller.ClientId, out var client) && client.MayChange;
    }

    /// <summary>Whether <paramref name="endpointId"/> is hidden from agents.</summary>
    public bool IsHidden(string? endpointId) => endpointId is not null && _hidden.Contains(endpointId);

    /// <summary>Whether agents may change messages on <paramref name="endpointId"/>.</summary>
    public bool MayChangeOn(string endpointId)
        => !IsHidden(endpointId)
           && (Settings.Endpoints.Changes == McpChangeScope.AllVisible || _changeOn.Contains(endpointId));

    private static int Lower(int? saved, int deployment)
        => saved is { } value && value >= 1 ? Math.Min(value, deployment) : deployment;
}
