using NimBus.WebApp.Mcp.Access;
using Api = NimBus.WebApp.ManagementApi;
using Store = NimBus.MessageStore.States;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>Maps the MCP access policy between the generated API contract and the store model.</summary>
internal static class McpAccessApiMapper
{
    public static Api.McpAccessSettings ToApi(Store.McpAccessSettings s) => new()
    {
        Revision = s.Revision,
        UpdatedBy = s.UpdatedBy,
        UpdatedAtUtc = s.UpdatedAtUtc,
        Enabled = s.Enabled,
        Capabilities = new Api.McpCapabilities
        {
            Payloads = s.Capabilities.Payloads,
            Report = s.Capabilities.Report,
            Classify = s.Capabilities.Classify,
            Resubmit = s.Capabilities.Resubmit,
            Skip = s.Capabilities.Skip,
        },
        AllowWorkloads = s.AllowWorkloads,
        AllowWorkloadClassification = s.AllowWorkloadClassification,
        AllowWorkloadResubmit = s.AllowWorkloadResubmit,
        WorkloadResubmitsPerDay = s.WorkloadResubmitsPerDay,
        WorkloadResubmitsPerMessage = s.WorkloadResubmitsPerMessage,
        People = new Api.McpPeople
        {
            Mode = s.People.Mode == Store.McpPeopleMode.Listed ? Api.McpPeopleMode.Listed : Api.McpPeopleMode.All,
            Principals = s.People.Principals.Select(p => new Api.McpPrincipal { Principal = p.Principal, Label = p.Label }).ToList(),
        },
        Clients = new Api.McpClients
        {
            Mode = s.Clients.Mode == Store.McpClientMode.Approved ? Api.McpClientMode.Approved : Api.McpClientMode.Any,
            Approved = s.Clients.Approved.Select(c => new Api.McpApprovedClient { ClientId = c.ClientId, Name = c.Name, MayChange = c.MayChange }).ToList(),
        },
        Endpoints = new Api.McpEndpointRules
        {
            Visibility = s.Endpoints.Visibility == Store.McpEndpointVisibility.AllExcept ? Api.McpEndpointVisibility.AllExcept : Api.McpEndpointVisibility.All,
            Hidden = s.Endpoints.Hidden.ToList(),
            Changes = s.Endpoints.Changes == Store.McpChangeScope.Listed ? Api.McpChangeScope.Listed : Api.McpChangeScope.AllVisible,
            ChangeOn = s.Endpoints.ChangeOn.ToList(),
        },
        Limits = new Api.McpLimits
        {
            RequestsPerWindow = s.Limits.RequestsPerWindow,
            MutationsPerWindow = s.Limits.MutationsPerWindow,
        },
    };

    /// <summary>
    /// The store model for a request body. Read-only fields (revision, who, when) are ignored;
    /// a missing section becomes its defaults and is validated like any other value.
    /// </summary>
    public static Store.McpAccessSettings FromApi(Api.McpAccessSettings a) => new()
    {
        Enabled = a.Enabled,
        Capabilities = a.Capabilities is null
            ? new Store.McpCapabilitySettings()
            : new Store.McpCapabilitySettings
            {
                Payloads = a.Capabilities.Payloads,
                Report = a.Capabilities.Report,
                Classify = a.Capabilities.Classify,
                Resubmit = a.Capabilities.Resubmit,
                Skip = a.Capabilities.Skip,
            },
        AllowWorkloads = a.AllowWorkloads,
        AllowWorkloadClassification = a.AllowWorkloadClassification,
        AllowWorkloadResubmit = a.AllowWorkloadResubmit,
        WorkloadResubmitsPerDay = a.WorkloadResubmitsPerDay ?? Store.McpAccessSettings.DefaultWorkloadResubmitsPerDay,
        WorkloadResubmitsPerMessage = a.WorkloadResubmitsPerMessage ?? Store.McpAccessSettings.DefaultWorkloadResubmitsPerMessage,
        People = new Store.McpPeopleSettings
        {
            Mode = a.People?.Mode == Api.McpPeopleMode.Listed ? Store.McpPeopleMode.Listed : Store.McpPeopleMode.All,
            Principals = (a.People?.Principals ?? []).Select(p => new Store.McpPrincipal { Principal = p?.Principal ?? string.Empty, Label = p?.Label }).ToList(),
        },
        Clients = new Store.McpClientSettings
        {
            Mode = a.Clients?.Mode == Api.McpClientMode.Approved ? Store.McpClientMode.Approved : Store.McpClientMode.Any,
            Approved = (a.Clients?.Approved ?? []).Select(c => new Store.McpApprovedClient
            {
                ClientId = c?.ClientId ?? string.Empty,
                Name = c?.Name ?? string.Empty,
                MayChange = c?.MayChange ?? false,
            }).ToList(),
        },
        Endpoints = new Store.McpEndpointSettings
        {
            Visibility = a.Endpoints?.Visibility == Api.McpEndpointVisibility.AllExcept ? Store.McpEndpointVisibility.AllExcept : Store.McpEndpointVisibility.All,
            Hidden = (a.Endpoints?.Hidden ?? []).ToList(),
            Changes = a.Endpoints?.Changes == Api.McpChangeScope.Listed ? Store.McpChangeScope.Listed : Store.McpChangeScope.AllVisible,
            ChangeOn = (a.Endpoints?.ChangeOn ?? []).ToList(),
        },
        Limits = new Store.McpLimitSettings
        {
            RequestsPerWindow = a.Limits?.RequestsPerWindow,
            MutationsPerWindow = a.Limits?.MutationsPerWindow,
        },
    };

    public static Api.McpActivity ToApi(McpActivity activity) => new()
    {
        Hours = activity.Hours,
        Actions = activity.Actions.Count(),
        Refused = activity.Refusals.Count(),
        Capped = activity.Capped,
        ActionsByType = activity.Actions.GroupBy(i => Camel(i.Type.ToString()))
            .Select(g => new Api.McpActivityCount { Name = g.Key, Count = g.Count() }).OrderByDescending(c => c.Count).ToList(),
        RefusedByReason = activity.Refusals.GroupBy(i => i.Reason ?? "unknown")
            .Select(g => new Api.McpActivityCount { Name = g.Key, Count = g.Count() }).OrderByDescending(c => c.Count).ToList(),
        Items = activity.Items.Take(50).Select(i => new Api.McpActivityItem
        {
            AtUtc = i.AtUtc,
            Kind = i.Kind,
            Type = Camel(i.Type.ToString()),
            EndpointId = i.EndpointId,
            EventId = i.EventId,
            Auditor = i.Auditor,
            ClientId = i.ClientId,
            Reason = i.Reason,
            Detail = i.Detail,
        }).ToList(),
        RefusedClients = activity.RefusedClients.Select(c => new Api.McpRefusedClient
        {
            ClientId = c.ClientId,
            Calls = c.Calls,
            LastAuditor = c.LastAuditor,
            LastAtUtc = c.LastAtUtc,
        }).ToList(),
    };

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
