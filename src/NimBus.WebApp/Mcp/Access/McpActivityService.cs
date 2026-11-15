using Newtonsoft.Json.Linq;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>One audited MCP event.</summary>
/// <param name="AtUtc">When it happened.</param>
/// <param name="Kind">action, refused or settings.</param>
/// <param name="Type">The audit type.</param>
/// <param name="EndpointId">The endpoint, when there is one.</param>
/// <param name="EventId">The message's event id, when there is one.</param>
/// <param name="Auditor">Who.</param>
/// <param name="ClientId">The client application, when known.</param>
/// <param name="Reason">The operator's reason, or the refusal reason.</param>
/// <param name="Detail">The tool, ticket or change summary.</param>
public sealed record McpActivityItem(
    DateTime AtUtc, string Kind, MessageAuditType Type, string? EndpointId, string? EventId,
    string? Auditor, string? ClientId, string? Reason, string? Detail);

/// <summary>A client the policy refused, with how often.</summary>
/// <param name="ClientId">The client application id.</param>
/// <param name="Calls">Refusal rows; deduplicated, so a lower bound on calls.</param>
/// <param name="LastAuditor">Who used it last.</param>
/// <param name="LastAtUtc">When.</param>
public sealed record McpRefusedClient(string ClientId, int Calls, string? LastAuditor, DateTime LastAtUtc);

/// <summary>Recent agent activity over MCP.</summary>
/// <param name="Hours">The period covered.</param>
/// <param name="Items">Every row found, newest first.</param>
/// <param name="Capped">True when a query hit its row cap.</param>
public sealed record McpActivity(int Hours, IReadOnlyList<McpActivityItem> Items, bool Capped)
{
    /// <summary>Accepted actions.</summary>
    public IEnumerable<McpActivityItem> Actions => Items.Where(i => i.Kind == McpActivityService.ActionKind);

    /// <summary>Refusals.</summary>
    public IEnumerable<McpActivityItem> Refusals => Items.Where(i => i.Kind == McpActivityService.RefusedKind);

    /// <summary>Clients refused because they are not approved.</summary>
    public IReadOnlyList<McpRefusedClient> RefusedClients => Refusals
        .Where(i => i.Reason == "client" && !string.IsNullOrEmpty(i.ClientId))
        .GroupBy(i => i.ClientId!, StringComparer.OrdinalIgnoreCase)
        .Select(g => new McpRefusedClient(g.Key, g.Count(), g.First().Auditor, g.First().AtUtc))
        .OrderByDescending(c => c.LastAtUtc)
        .ToList();
}

/// <summary>
/// Builds the MCP access settings' activity summary from the audit log (Spec 037 §5.8): rows written
/// with <c>channel: "Mcp"</c>, refusals and settings changes. Classification requests made over
/// MCP are audited by the classification service without a channel, so they are not counted.
/// </summary>
public sealed class McpActivityService
{
    /// <summary>Most rows read per audit type.</summary>
    public const int RowCap = 500;

    internal const string ActionKind = "action";
    internal const string RefusedKind = "refused";
    internal const string SettingsKind = "settings";

    private static readonly MessageAuditType[] Types =
    [
        MessageAuditType.Resubmit,
        MessageAuditType.ResubmitWithChanges,
        MessageAuditType.Skip,
        MessageAuditType.ReportEvent,
        MessageAuditType.CompleteHandoff,
        MessageAuditType.FailHandoff,
        MessageAuditType.ReprocessDeferred,
        MessageAuditType.McpAccessRefused,
        MessageAuditType.UpdateMcpSettings,
    ];

    private readonly IMessageTrackingStore _store;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public McpActivityService(IMessageTrackingStore store, TimeProvider time)
    {
        _store = store;
        _time = time;
    }

    /// <summary>The activity in the last <paramref name="hours"/>.</summary>
    public async Task<McpActivity> GetAsync(int hours)
    {
        var from = _time.GetUtcNow().UtcDateTime.AddHours(-hours);
        var items = new List<McpActivityItem>();
        var capped = false;

        foreach (var type in Types)
        {
            var result = await _store.SearchAudits(new AuditFilter { AuditType = type, CreatedAtFrom = from }, null, RowCap).ConfigureAwait(false);
            var rows = result.Audits.ToList();
            capped |= rows.Count >= RowCap || result.ContinuationToken is not null;
            items.AddRange(rows.Select(Project).OfType<McpActivityItem>());
        }

        return new McpActivity(hours, items.OrderByDescending(i => i.AtUtc).ToList(), capped);
    }

    private static McpActivityItem? Project(AuditSearchItem row)
    {
        var audit = row.Audit;
        var data = Parse(audit.Data);
        var at = audit.AuditTimestamp == default ? row.CreatedAt : audit.AuditTimestamp;
        var endpoint = audit.EndpointId ?? row.EndpointId;
        var eventId = audit.EventId ?? row.EventId;

        if (audit.AuditType == MessageAuditType.UpdateMcpSettings)
        {
            if (audit.AccessDenied)
                return null;
            var turnOff = data?.Value<bool?>("turnOff") == true;
            var count = (data?["changes"] as JArray)?.Count ?? 0;
            var detail = turnOff ? "Turned MCP access off" : $"{count} change{(count == 1 ? string.Empty : "s")} (revision {data?.Value<string>("revision")?[..8]})";
            return new McpActivityItem(at, SettingsKind, audit.AuditType, null, null, audit.AuditorName, null, null, detail);
        }

        if (!string.Equals(data?.Value<string>("channel"), "Mcp", StringComparison.Ordinal))
            return null;

        var clientId = data?.Value<string>("clientId");
        if (audit.AuditType == MessageAuditType.McpAccessRefused)
        {
            return new McpActivityItem(at, RefusedKind, audit.AuditType, endpoint, eventId, audit.AuditorName, clientId,
                data?.Value<string>("reason"), data?.Value<string>("tool"));
        }

        return audit.AccessDenied
            ? new McpActivityItem(at, RefusedKind, audit.AuditType, endpoint, eventId, audit.AuditorName, clientId, "role", Verb(audit.AuditType))
            : new McpActivityItem(at, ActionKind, audit.AuditType, endpoint, eventId, audit.AuditorName, clientId,
                data?.Value<string>("reason"), data?.Value<string>("ticketId"));
    }

    private static string Verb(MessageAuditType type) => type switch
    {
        MessageAuditType.Resubmit => "resubmit",
        MessageAuditType.Skip => "skip",
        MessageAuditType.ReportEvent => "report",
        MessageAuditType.ResubmitWithChanges => "resubmit_with_changes",
        MessageAuditType.CompleteHandoff or MessageAuditType.FailHandoff => "settle_handoff",
        MessageAuditType.ReprocessDeferred => "reprocess_deferred",
        _ => type.ToString(),
    };

    private static JObject? Parse(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;
        try
        {
            return JToken.Parse(data) as JObject;
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            // The audit writer truncates long Data, so a row may hold cut JSON.
            return null;
        }
    }
}
