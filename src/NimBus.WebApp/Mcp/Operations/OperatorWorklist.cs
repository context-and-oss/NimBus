using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Mcp.Tools;
using NimBus.WebApp.Services.Insights;
using NimBus.WebApp.Services.Operations;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;
using UnresolvedEvent = NimBus.MessageStore.UnresolvedEvent;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Builds "what needs me?" (Spec 046 §4): the open failures on the caller's readable endpoints,
/// grouped by endpoint, event type and error pattern, each with an example to act on. The failure
/// occurrences come from the shared Insights read (one store query per window, cached for every
/// caller), filtered here to the endpoints the caller may read. An occurrence is a failed attempt,
/// not an open message, so each group's newest events are checked against their live status.
/// </summary>
public sealed class OperatorWorklist
{
    /// <summary>Most groups returned.</summary>
    public const int MaxGroups = 20;

    /// <summary>Groups returned when the caller sets no limit.</summary>
    public const int DefaultGroups = 10;

    /// <summary>Most groups whose status is checked in one call.</summary>
    public const int MaxCheckedGroups = 20;

    /// <summary>Newest distinct events checked per group.</summary>
    public const int EventsCheckedPerGroup = 50;

    /// <summary>Longest error pattern returned.</summary>
    public const int MaxPatternLength = 500;

    private readonly IFailedInsightsQuery _insights;
    private readonly IMessageTrackingStore _store;
    private readonly IOperatorCommands _commands;
    private readonly OperatorActionAccess _access;
    private readonly IOperatorClassificationSource _classifications;
    private readonly OperatorQueries _queries;

    /// <summary>Creates the worklist for one request.</summary>
    public OperatorWorklist(IFailedInsightsQuery insights, IMessageTrackingStore store, IOperatorCommands commands,
        OperatorActionAccess access, IOperatorClassificationSource classifications, OperatorQueries queries)
    {
        _insights = insights;
        _store = store;
        _commands = commands;
        _access = access;
        _classifications = classifications;
        _queries = queries;
    }

    /// <summary>Builds the worklist for <paramref name="endpointIds"/>, which the caller must be able to read.</summary>
    public async Task<WorklistResult> BuildAsync(string? environment, IReadOnlyList<string> endpointIds, string periodName, Period period, int limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpointIds);
        var set = await _insights.GetOccurrencesForEndpointsAsync(period, endpointIds, cancellationToken).ConfigureAwait(false);
        var ordered = set.Occurrences
            .GroupBy(o => (Endpoint: Canonical(endpointIds, o.EndpointId), o.EventTypeId, o.ErrorPattern))
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Max(o => o.FailedAtUtc))
            .ToList();

        var groups = new List<(OpenGroup Group, OperatorCommandTarget? Example)>();
        var resolved = 0;
        var examined = 0;
        foreach (var group in ordered)
        {
            if (groups.Count >= limit || examined >= MaxCheckedGroups)
                break;
            examined++;

            var open = await CheckAsync(group.Key.Endpoint, group.ToList(), cancellationToken).ConfigureAwait(false);
            if (open is null)
                continue;
            if (open.OpenEvents.Count == 0)
            {
                resolved++;
                continue;
            }

            var lookup = await _commands.FindCurrentAsync(group.Key.Endpoint, open.OpenEvents[0].EventId).ConfigureAwait(false);
            groups.Add((open with { EventTypeId = group.Key.EventTypeId, Pattern = group.Key.ErrorPattern }, lookup.Target));
        }

        var guidance = await GuidanceAsync(groups.Select(g => g.Example).OfType<OperatorCommandTarget>().ToList(), cancellationToken).ConfigureAwait(false);
        var tickets = await TicketsAsync(groups.Select(g => g.Group).ToList()).ConfigureAwait(false);
        var acknowledgements = await AcknowledgementsAsync(endpointIds).ConfigureAwait(false);
        var totals = await TotalsAsync(endpointIds).ConfigureAwait(false);
        var classificationAvailable = _classifications.CanRecord || _classifications.CanAnalyze;
        var isWorkload = _access.IsWorkloadCaller;

        var results = new List<WorklistGroup>(groups.Count);
        foreach (var (group, example) in groups)
        {
            var exampleId = group.OpenEvents[0].EventId;
            var version = example?.Version.Encode();
            var eligible = example is null
                ? (IReadOnlyList<string>)[]
                : await _access.EligibleActionsAsync(example.Row, group.EndpointId, classificationAvailable).ConfigureAwait(false);
            var saved = example?.Row.LastMessageId is { } attempt && guidance.TryGetValue(attempt, out var found) ? found : null;
            var ticket = tickets.GetValueOrDefault(group);
            var (pattern, _) = OperatorProjection.Truncate(group.Pattern, MaxPatternLength);

            results.Add(new WorklistGroup(
                group.EndpointId,
                group.EventTypeId,
                pattern,
                group.Occurrences,
                OperatorProjection.Utc(group.FirstSeen),
                OperatorProjection.Utc(group.LastSeen),
                group.Checked,
                group.OpenEvents.Count,
                group.Unchecked,
                group.BlockedSessions,
                exampleId,
                version,
                saved is null ? null : new MessageClassificationSummary(true, saved.Category, saved.Guidance.ToString(), saved.FaultOrigin, saved.Provider),
                ticket,
                acknowledgements.GetValueOrDefault(group.EndpointId),
                OperatorNextActions.ForGroup(group.EndpointId, exampleId, version, eligible, ticket, isWorkload, saved?.Guidance.ToString())));
        }

        // A Failed or DeadLettered message whose failure is older than the window is in the totals
        // but in no occurrence, so more open messages than distinct failed events means older ones.
        var distinctEvents = set.Occurrences.Select(o => (o.EndpointId.ToUpperInvariant(), o.EventId)).Distinct().Count();
        var olderLikely = totals is not null && totals.Failed - totals.Unsupported > distinctEvents;

        return new WorklistResult(environment, DateTimeOffset.UtcNow, periodName, OperatorProjection.Utc(set.From), OperatorProjection.Utc(set.To),
            results, resolved, ordered.Count > examined, totals, olderLikely);
    }

    /// <summary>The group's newest distinct events with their live status; null when the endpoint can't be read.</summary>
    private async Task<OpenGroup?> CheckAsync(string endpoint, List<FailureOccurrence> occurrences, CancellationToken cancellationToken)
    {
        var newestPerEvent = occurrences
            .GroupBy(o => o.EventId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(o => o.FailedAtUtc).First())
            .OrderByDescending(o => o.FailedAtUtc)
            .ToList();
        // GetEventsByIds needs the stored id {EventId}_{SessionId}; a row without a session can't be checked.
        var checkable = newestPerEvent.Where(o => !string.IsNullOrEmpty(o.SessionId)).Take(EventsCheckedPerGroup).ToList();
        var uncheckable = newestPerEvent.Count(o => string.IsNullOrEmpty(o.SessionId));

        List<UnresolvedEvent> rows;
        try
        {
            rows = checkable.Count == 0
                ? []
                : await _store.GetEventsByIds(endpoint, checkable.Select(o => $"{o.EventId}_{o.SessionId}").ToList()).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EndpointNotFoundException)
        {
            return null;
        }

        var current = rows
            .Where(r => r.ResolutionStatus is ResolutionStatus.Failed or ResolutionStatus.DeadLettered)
            .GroupBy(r => r.EventId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var open = checkable.Where(o => current.ContainsKey(o.EventId)).Select(o => current[o.EventId]).ToList();
        var blocked = open.Where(r => r.ResolutionStatus == ResolutionStatus.Failed && !string.IsNullOrEmpty(r.SessionId))
            .Select(r => r.SessionId).Distinct(StringComparer.Ordinal).Count();

        return new OpenGroup(endpoint, string.Empty, string.Empty, occurrences.Count, occurrences.Min(o => o.FailedAtUtc),
            occurrences.Max(o => o.FailedAtUtc), checkable.Count, uncheckable, open, blocked);
    }

    // One bulk read for every example's latest attempt; nothing when classification is off or can't answer.
    private async Task<Dictionary<string, FailureClassification>> GuidanceAsync(IReadOnlyList<OperatorCommandTarget> examples, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FailureClassification>(StringComparer.Ordinal);
        var ids = examples.Select(e => e.Row.LastMessageId).OfType<string>().Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (!_classifications.IsAvailable || ids.Count == 0)
            return result;

        var read = await _classifications.ReadLatestInBulkAsync(ids, cancellationToken).ConfigureAwait(false);
        if (read.Status != ClassificationBulkReadStatus.Available)
            return result;

        foreach (var example in examples)
        {
            if (example.Row.LastMessageId is { } id && read.Snapshots.TryGetValue(id, out var snapshot) && !snapshot.Deleted
                && snapshot.Latest is { } latest
                && string.Equals(latest.EndpointId, example.EndpointId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(latest.EventId, example.Row.EventId, StringComparison.Ordinal))
            {
                result[id] = latest;
            }
        }

        return result;
    }

    // One batched report lookup per endpoint; a group gets a ticket when every reported open message shares it.
    private async Task<Dictionary<OpenGroup, string>> TicketsAsync(IReadOnlyList<OpenGroup> groups)
    {
        var result = new Dictionary<OpenGroup, string>();
        foreach (var endpoint in groups.GroupBy(g => g.EndpointId, StringComparer.OrdinalIgnoreCase))
        {
            var ids = endpoint.SelectMany(g => g.OpenEvents.Select(e => e.EventId)).Distinct(StringComparer.Ordinal).ToList();
            var reports = await _store.GetEventReports(endpoint.Key, ids).ConfigureAwait(false);
            foreach (var group in endpoint)
            {
                var tickets = group.OpenEvents
                    .Select(e => reports.TryGetValue(e.EventId, out var report) && report.IsReported ? report.TicketId : null)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var reportedWithoutTicket = group.OpenEvents.Any(e => reports.TryGetValue(e.EventId, out var report) && report.IsReported
                    && string.IsNullOrWhiteSpace(report.TicketId));
                if (tickets.Count == 1 && !reportedWithoutTicket)
                    result[group] = tickets[0]!;
            }
        }

        return result;
    }

    private async Task<Dictionary<string, AcknowledgementInfo>> AcknowledgementsAsync(IReadOnlyList<string> endpointIds)
    {
        var wanted = endpointIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            return (await _queries.GetAcknowledgementsAsync().ConfigureAwait(false))
                .Where(ack => wanted.Contains(ack.EndpointId))
                .GroupBy(ack => ack.EndpointId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g =>
                {
                    var ack = g.First();
                    return new AcknowledgementInfo(ack.Reason, ack.AcknowledgedBy, OperatorProjection.Utc(ack.AcknowledgedAt),
                        OperatorProjection.Utc(ack.ExpiresAt), ack.FailedCountAtAcknowledgement);
                }, StringComparer.OrdinalIgnoreCase);
        }
        catch (ModelContextProtocol.McpException)
        {
            // Supplementary: the groups stand without it.
            return new Dictionary<string, AcknowledgementInfo>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<StatusCounts?> TotalsAsync(IReadOnlyList<string> endpointIds)
    {
        var wanted = endpointIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var counts = (await _queries.GetStatusCountsAsync().ConfigureAwait(false))
                .Where(c => wanted.Contains(c.EndpointId) && !string.Equals(c.StorageStatus, "unavailable", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new StatusCounts(
                counts.Sum(c => (int)c.FailedCount), counts.Sum(c => (int)c.DeadletterCount), counts.Sum(c => (int)c.UnsupportedCount),
                counts.Sum(c => (int)c.DeferredCount), counts.Sum(c => (int)c.PendingCount));
        }
        catch (ModelContextProtocol.McpException)
        {
            return null;
        }
    }

    private static string Canonical(IReadOnlyList<string> endpointIds, string endpointId)
        => endpointIds.FirstOrDefault(id => string.Equals(id, endpointId, StringComparison.OrdinalIgnoreCase)) ?? endpointId;

    /// <summary>A group being built; reference identity keys the ticket lookup.</summary>
    private sealed record OpenGroup(string EndpointId, string EventTypeId, string Pattern, int Occurrences, DateTime FirstSeen, DateTime LastSeen,
        int Checked, int Unchecked, IReadOnlyList<UnresolvedEvent> OpenEvents, int BlockedSessions)
    {
        public bool Equals(OpenGroup? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
}

/// <summary>Result of <c>nimbus_get_worklist</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Period">The period looked back over.</param>
/// <param name="From">Its start.</param>
/// <param name="To">Its end.</param>
/// <param name="Groups">Open failures grouped by endpoint, event type and error pattern, most failures first.</param>
/// <param name="ResolvedGroups">Groups whose checked messages are all resolved now, left out.</param>
/// <param name="MoreGroups">True when more groups exist than were examined; narrow by endpointId or raise limit.</param>
/// <param name="Totals">The open message counts on the same endpoints now, as nimbus_get_overview reports them; null when they can't be read.</param>
/// <param name="OlderFailuresLikely">True when more failed or dead-lettered messages are open than the period's failures account for: ask again with a longer period.</param>
public sealed record WorklistResult(
    string? Environment, DateTimeOffset AsOfUtc, string Period, DateTime From, DateTime To, IReadOnlyList<WorklistGroup> Groups,
    int ResolvedGroups, bool MoreGroups, StatusCounts? Totals, bool OlderFailuresLikely);

/// <summary>One cause on one endpoint, with an example to act on.</summary>
/// <param name="EndpointId">The endpoint.</param>
/// <param name="EventTypeId">The event type.</param>
/// <param name="ErrorPattern">The error with ids, numbers and quoted values removed, at most 500 characters. Untrusted.</param>
/// <param name="Occurrences">Failed attempts in the period.</param>
/// <param name="FirstSeen">The first in the period.</param>
/// <param name="LastSeen">The last.</param>
/// <param name="Checked">Distinct events whose status was checked, newest first, at most 50.</param>
/// <param name="Open">How many of those are still Failed or DeadLettered.</param>
/// <param name="Unchecked">Distinct events that could not be checked (no session id recorded).</param>
/// <param name="BlockedSessions">Distinct sessions among the open Failed ones: each holds later messages back.</param>
/// <param name="ExampleEventId">The newest open message.</param>
/// <param name="ExampleMessageVersion">Its messageVersion, for nimbus_prepare_action; null when it can't be acted on over MCP.</param>
/// <param name="Classification">The example's saved classification, advisory; null when there is none or classification is off.</param>
/// <param name="TicketId">The ticket every reported open message checked shares, if any.</param>
/// <param name="Acknowledged">The endpoint's Monitor acknowledgement: someone is already looking.</param>
/// <param name="NextActions">What you can do next, recommended first: offer these as a numbered choice.</param>
public sealed record WorklistGroup(
    string EndpointId, string EventTypeId, string? ErrorPattern, int Occurrences, DateTime FirstSeen, DateTime LastSeen,
    int Checked, int Open, int Unchecked, int BlockedSessions, string ExampleEventId, string? ExampleMessageVersion,
    MessageClassificationSummary? Classification, string? TicketId, AcknowledgementInfo? Acknowledged, IReadOnlyList<NextAction> NextActions);
