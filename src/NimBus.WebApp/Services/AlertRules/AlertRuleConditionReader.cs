using System.Collections.Generic;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AlertRules;

/// <summary>
/// Reads an alert rule's condition (Spec 053 §5.1): whether it holds now and the value a notice
/// reports. Shared by the evaluator and the rule preview, so both read a condition the same way.
/// </summary>
internal sealed class AlertRuleConditionReader
{
    /// <summary>
    /// The most failures a rule with categories looks at in one reading: their classifications are
    /// read in bulk, so a large backlog would otherwise be read in full every minute.
    /// </summary>
    public const int CategoryScanCeiling = 1_000;

    private const int BacklogPageSize = 200;

    private readonly IMessageTrackingStore _events;
    private readonly IAlertSignalStore? _signals;
    private readonly IAlertRuleClassifications? _classifications;

    // The deferred count is an endpoint-wide read (about 50 RU on Cosmos DB for 5,000 events), so
    // blocked-session rules on one endpoint share it until Reset.
    private readonly Dictionary<string, Task<EndpointStateCount>> _stateCounts = new(StringComparer.Ordinal);

    /// <summary>Creates the reader.</summary>
    /// <param name="events">The message store the backlog and the deferred count are read from.</param>
    /// <param name="signals">The alert signal reads, or null when the provider has none.</param>
    /// <param name="classifications">Saved failure classifications, or null when Integration Intelligence can't be read.</param>
    public AlertRuleConditionReader(IMessageTrackingStore events, IAlertSignalStore? signals, IAlertRuleClassifications? classifications)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _signals = signals;
        _classifications = classifications;
    }

    /// <summary>The alert signal reads, or null.</summary>
    public IAlertSignalStore? Signals => _signals;

    /// <summary>Whether category filters can be evaluated.</summary>
    public bool ClassificationAvailable => _classifications is not null;

    /// <summary>Forgets the shared deferred counts; the evaluator calls it at the start of each tick.</summary>
    public void Reset() => _stateCounts.Clear();

    /// <summary>
    /// Reads the condition. The reading is null, with a reason, when it can't be evaluated
    /// reliably: a 0 instead would resolve a firing rule that may still hold.
    /// </summary>
    /// <param name="rule">The rule; only its endpoint and condition are used.</param>
    /// <param name="now">The time the condition is evaluated at.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<ConditionRead> ReadAsync(AlertRule rule, DateTime now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var condition = rule.Condition;
        var categories = condition.Categories ?? [];
        var counted = condition.Kind is AlertRuleConditionKinds.FailureBacklog or AlertRuleConditionKinds.FailureOccurrences;
        if (counted && categories.Count > 0 && _classifications is null)
            return ConditionRead.Skipped(AlertRuleSkipReasons.ClassificationUnavailable);

        switch (condition.Kind)
        {
            case AlertRuleConditionKinds.FailureOccurrences:
            {
                if (_signals is null)
                    return ConditionRead.Skipped(AlertRuleSkipReasons.KindUnavailable);
                var from = now - TimeSpan.FromMinutes(condition.WindowMinutes ?? 0);
                var eventTypes = condition.EventTypes.Count > 0 ? condition.EventTypes : null;
                var errorText = string.IsNullOrWhiteSpace(condition.ErrorTextContains) ? null : condition.ErrorTextContains;
                if (categories.Count == 0)
                {
                    var count = await _signals.CountFailedAttempts(rule.EndpointId, from, now, eventTypes, errorText, condition.Threshold).ConfigureAwait(false);
                    return Counted(count, condition.Threshold);
                }

                var attempts = await _signals.ListFailedAttempts(rule.EndpointId, from, now, eventTypes, errorText, CategoryScanCeiling).ConfigureAwait(false);
                var matches = await CountMatchesAsync(rule.EndpointId, attempts.Items.Select(a => (a.MessageId, a.EventId)).ToList(), categories, condition.Threshold, cancellationToken).ConfigureAwait(false);
                if (matches is null)
                    return ConditionRead.Skipped(AlertRuleSkipReasons.ClassificationUnavailable);
                if (matches < condition.Threshold && attempts.Truncated)
                    return ConditionRead.Skipped(AlertRuleSkipReasons.ClassificationScanLimit);
                return Counted(matches.Value, condition.Threshold);
            }

            case AlertRuleConditionKinds.BlockedSession:
            {
                if (_signals is null)
                    return ConditionRead.Skipped(AlertRuleSkipReasons.KindUnavailable);
                var holds = false;
                var minutesBlocked = 0;
                if (condition.Minutes is { } minutes)
                {
                    var oldest = await _signals.GetOldestBlockingEvent(rule.EndpointId).ConfigureAwait(false);
                    if (oldest is not null)
                    {
                        minutesBlocked = Math.Max(0, (int)(now - oldest.UpdatedAtUtc).TotalMinutes);
                        holds = minutesBlocked >= minutes;
                    }
                }

                if (!holds && condition.DeferredThreshold is { } deferredThreshold)
                {
                    if (!_stateCounts.TryGetValue(rule.EndpointId, out var read))
                    {
                        read = _events.DownloadEndpointStateCount(rule.EndpointId);
                        _stateCounts[rule.EndpointId] = read;
                    }

                    var counts = await read.ConfigureAwait(false);
                    holds = counts.DeferredCount >= deferredThreshold;
                }

                return ConditionRead.Of(new ConditionReading(holds, minutesBlocked, VolatileValue: true));
            }

            case AlertRuleConditionKinds.Silence:
            {
                if (_signals is null)
                    return ConditionRead.Skipped(AlertRuleSkipReasons.KindUnavailable);
                var minutes = condition.Minutes ?? 0;
                var completed = await _signals.HasCompletedMessageSince(rule.EndpointId, now - TimeSpan.FromMinutes(minutes)).ConfigureAwait(false);
                return ConditionRead.Of(new ConditionReading(!completed, completed ? 0 : minutes, VolatileValue: false));
            }

            default:
                return categories.Count == 0
                    ? Counted(await ReadBacklogAsync(rule).ConfigureAwait(false), condition.Threshold)
                    : await ReadClassifiedBacklogAsync(rule, categories, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ConditionRead Counted(int count, int threshold)
        => ConditionRead.Of(new ConditionReading(count >= threshold, Math.Min(count, threshold), VolatileValue: false));

    private static EventFilter BacklogFilter(AlertRuleCondition condition) => new()
    {
        EventTypeId = condition.EventTypes.Count > 0 ? condition.EventTypes.ToList() : null,
        ResolutionStatus = condition.Statuses.Count > 0 ? condition.Statuses.ToList() : null,
        ErrorText = string.IsNullOrWhiteSpace(condition.ErrorTextContains) ? null : condition.ErrorTextContains,
    };

    private async Task<int> ReadBacklogAsync(AlertRule rule)
    {
        // The rule only asks whether the backlog reaches the threshold, so it reads at most that
        // many failures and never follows the continuation token.
        var page = await _events
            .GetFailedEventsAcrossEndpoints(BacklogFilter(rule.Condition), [rule.EndpointId], continuationToken: null, maxItemCount: rule.Condition.Threshold)
            .ConfigureAwait(false);
        return Math.Min(page.Events?.Count() ?? 0, rule.Condition.Threshold);
    }

    /// <summary>
    /// The backlog counting only failures classified into one of the categories: pages the failed
    /// events, reads their last attempts' classifications, and stops at the threshold or the ceiling.
    /// </summary>
    private async Task<ConditionRead> ReadClassifiedBacklogAsync(AlertRule rule, IReadOnlyCollection<string> categories, CancellationToken cancellationToken)
    {
        var threshold = rule.Condition.Threshold;
        var filter = BacklogFilter(rule.Condition);
        string? token = null;
        var scanned = 0;
        var matched = 0;
        do
        {
            var page = await _events
                .GetFailedEventsAcrossEndpoints(filter, [rule.EndpointId], token, Math.Min(BacklogPageSize, CategoryScanCeiling - scanned))
                .ConfigureAwait(false);
            var failures = (page.Events ?? [])
                .Where(e => !string.IsNullOrEmpty(e.LastMessageId))
                .Select(e => (e.LastMessageId!, e.EventId))
                .ToList();
            scanned += page.Events?.Count() ?? 0;
            var matches = await CountMatchesAsync(rule.EndpointId, failures, categories, threshold - matched, cancellationToken).ConfigureAwait(false);
            if (matches is null)
                return ConditionRead.Skipped(AlertRuleSkipReasons.ClassificationUnavailable);
            matched += matches.Value;
            if (matched >= threshold)
                return Counted(matched, threshold);
            token = string.IsNullOrEmpty(page.ContinuationToken) ? null : page.ContinuationToken;
        }
        while (token is not null && scanned < CategoryScanCeiling);

        return token is null
            ? Counted(matched, threshold)
            : ConditionRead.Skipped(AlertRuleSkipReasons.ClassificationScanLimit);
    }

    /// <summary>How many of the failed attempts are classified into one of the categories, up to <paramref name="cap"/>; null when the classifications can't be read.</summary>
    private async Task<int?> CountMatchesAsync(
        string endpointId, IReadOnlyList<(string MessageId, string EventId)> attempts, IReadOnlyCollection<string> categories, int cap, CancellationToken cancellationToken)
    {
        if (attempts.Count == 0)
            return 0;
        var read = await _classifications!.ReadLatestInBulkAsync(attempts.Select(a => a.MessageId).ToList(), cancellationToken).ConfigureAwait(false);
        if (read.Status != ClassificationBulkReadStatus.Available)
            return null;
        var matches = attempts.Count(a => ClassificationMatch.Matches(read.Snapshots.GetValueOrDefault(a.MessageId), endpointId, a.EventId, categories));
        return Math.Min(matches, cap);
    }
}

/// <summary>One reading of a rule's condition.</summary>
/// <param name="Holds">Whether the condition holds now.</param>
/// <param name="Value">The value a notice reports: a count capped at the threshold, or minutes.</param>
/// <param name="VolatileValue">Whether the value changes on its own every minute (a duration).</param>
internal sealed record ConditionReading(bool Holds, int Value, bool VolatileValue);

/// <summary>The result of reading a condition: a reading, or why there is none.</summary>
/// <param name="Reading">The reading, or null when the condition couldn't be evaluated.</param>
/// <param name="SkipReason">Why there is no reading, one of <see cref="AlertRuleSkipReasons"/>.</param>
internal sealed record ConditionRead(ConditionReading? Reading, string? SkipReason)
{
    public static ConditionRead Of(ConditionReading reading) => new(reading, null);

    public static ConditionRead Skipped(string reason) => new(null, reason);
}

/// <summary>Why a rule was not evaluated in a tick (the <c>reason</c> of the skipped meter).</summary>
internal static class AlertRuleSkipReasons
{
    /// <summary>The storage provider can't evaluate the condition's kind.</summary>
    public const string KindUnavailable = "kind_unavailable";

    /// <summary>The rule has categories but classifications can't be read.</summary>
    public const string ClassificationUnavailable = "classification_unavailable";

    /// <summary>More failures than the scan ceiling were left unchecked below the threshold.</summary>
    public const string ClassificationScanLimit = "classification_scan_limit";
}

/// <summary>Reads saved failure classifications in bulk (Integration Intelligence).</summary>
internal interface IAlertRuleClassifications
{
    /// <summary>The latest saved state of each failed attempt, keyed by message id.</summary>
    Task<ClassificationBulkReadResult> ReadLatestInBulkAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken);
}

/// <summary><see cref="IAlertRuleClassifications"/> over the Integration Intelligence reader.</summary>
internal sealed class ReaderAlertRuleClassifications(FailureClassificationReader reader) : IAlertRuleClassifications
{
    public Task<ClassificationBulkReadResult> ReadLatestInBulkAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken)
        => reader.ReadLatestInBulkAsync(failureMessageIds, new ClassificationBulkReadOptions(), cancellationToken);
}
