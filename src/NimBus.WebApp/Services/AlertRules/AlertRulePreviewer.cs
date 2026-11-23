using System.Collections.Generic;
using NimBus.Core.Messages;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AlertRules;

/// <summary>
/// Previews an alert rule before it is saved (Spec 053 Phase 3): its value now, and for the
/// conditions message history replays exactly — failure occurrences and silence — when it would
/// have fired in the last 24 hours. The replay runs the evaluator's state machine minute by minute,
/// so the periods honour the resolve time as the real evaluator would.
/// </summary>
internal sealed class AlertRulePreviewer
{
    /// <summary>How far back the replay goes.</summary>
    public const int ReplayMinutes = 1440;

    /// <summary>The most points the replay's series carries for a chart.</summary>
    public const int MaxChartPoints = 288;

    private readonly AlertRuleConditionReader _conditions;

    /// <summary>Creates the previewer.</summary>
    /// <param name="conditions">The condition reader the evaluator uses.</param>
    public AlertRulePreviewer(AlertRuleConditionReader conditions)
        => _conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));

    /// <summary>Previews <paramref name="condition"/> on <paramref name="endpointId"/> at <paramref name="now"/>.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="condition">The draft condition, already validated.</param>
    /// <param name="resolveAfterMinutes">The draft's resolve time.</param>
    /// <param name="now">The time the preview is for.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<AlertRulePreviewResult> PreviewAsync(
        string endpointId, AlertRuleCondition condition, int resolveAfterMinutes, DateTime now, CancellationToken cancellationToken)
    {
        var rule = new AlertRule
        {
            RuleId = "preview",
            EndpointId = endpointId,
            Name = "preview",
            Enabled = true,
            Condition = condition,
            ResolveAfterMinutes = resolveAfterMinutes,
        };

        var read = await _conditions.ReadAsync(rule, now, cancellationToken).ConfigureAwait(false);
        var (replay, reason) = await ReplayAsync(rule, now).ConfigureAwait(false);
        return new AlertRulePreviewResult(read.Reading?.Value, read.Reading?.Holds, read.SkipReason, replay, reason);
    }

    private async Task<(AlertRuleReplay? Replay, string? UnsupportedReason)> ReplayAsync(AlertRule rule, DateTime now)
    {
        var condition = rule.Condition;
        if (condition.Kind is not (AlertRuleConditionKinds.FailureOccurrences or AlertRuleConditionKinds.Silence))
            return (null, AlertRuleReplayUnsupported.NoHistory);
        if (condition.Categories is { Count: > 0 })
            return (null, AlertRuleReplayUnsupported.Categories);
        if (_conditions.Signals is not { } signals)
            return (null, AlertRuleReplayUnsupported.KindUnavailable);

        var end = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var start = end.AddMinutes(-ReplayMinutes);
        var occurrences = condition.Kind == AlertRuleConditionKinds.FailureOccurrences;
        var lookback = (occurrences ? condition.WindowMinutes : condition.Minutes) ?? 0;
        var counts = await signals.CountMessagesPerMinute(
            rule.EndpointId,
            occurrences ? MessageType.ErrorResponse : MessageType.ResolutionResponse,
            start.AddMinutes(-lookback),
            end,
            occurrences && condition.EventTypes.Count > 0 ? condition.EventTypes : null,
            occurrences && !string.IsNullOrWhiteSpace(condition.ErrorTextContains) ? condition.ErrorTextContains : null).ConfigureAwait(false);

        // perMinute[i] is the count of minute start − lookback + i.
        var perMinute = new int[lookback + ReplayMinutes];
        foreach (var bucket in counts)
        {
            var index = (int)((bucket.MinuteUtc - start.AddMinutes(-lookback)).Ticks / TimeSpan.TicksPerMinute);
            if (index >= 0 && index < perMinute.Length)
                perMinute[index] += bucket.Count;
        }

        var state = new AlertRuleState { Status = AlertRuleStatus.Ok };
        var series = new List<AlertRulePreviewPoint>(ReplayMinutes + 1);
        var periods = new List<AlertRulePreviewPeriod>();
        DateTime? openedAt = null;
        var windowSum = perMinute.Take(lookback).Sum();
        int? lastCompletion = null;
        for (var i = 0; i < lookback; i++)
            if (perMinute[i] > 0) lastCompletion = i;

        for (var k = 0; k <= ReplayMinutes; k++)
        {
            // At minute k the window [t − lookback, t) is perMinute[k .. k + lookback).
            if (k > 0)
            {
                windowSum += perMinute[k + lookback - 1] - perMinute[k - 1];
                if (perMinute[k + lookback - 1] > 0) lastCompletion = k + lookback - 1;
            }

            var at = start.AddMinutes(k);
            ConditionReading reading;
            int shown;
            if (occurrences)
            {
                shown = windowSum;
                reading = new ConditionReading(windowSum >= condition.Threshold, Math.Min(windowSum, condition.Threshold), VolatileValue: false);
            }
            else
            {
                // Minutes since the last completed message, as far back as the read reaches.
                var silentFor = lastCompletion is { } last ? k + lookback - 1 - last : k + lookback;
                shown = silentFor;
                var holds = lastCompletion is null || lastCompletion < k;
                reading = new ConditionReading(holds, holds ? lookback : 0, VolatileValue: false);
            }

            series.Add(new AlertRulePreviewPoint(at, shown));
            var before = state.Status;
            AlertRuleEvaluator.Apply(rule, state, reading, at, gap: false, simulate: true);
            if (before != state.Status)
            {
                if (state.Status == AlertRuleStatus.Firing)
                {
                    openedAt = at;
                }
                else
                {
                    periods.Add(new AlertRulePreviewPeriod(openedAt!.Value, at));
                    openedAt = null;
                }
            }
        }

        if (openedAt is { } open)
            periods.Add(new AlertRulePreviewPeriod(open, null));

        return (new AlertRuleReplay(start, end, Thin(series), periods), null);
    }

    /// <summary>At most <see cref="MaxChartPoints"/> points: each the highest value of its stretch.</summary>
    private static IReadOnlyList<AlertRulePreviewPoint> Thin(List<AlertRulePreviewPoint> series)
    {
        var step = (series.Count + MaxChartPoints - 1) / MaxChartPoints;
        if (step <= 1)
            return series;
        return series
            .Chunk(step)
            .Select(chunk => new AlertRulePreviewPoint(chunk[0].AtUtc, chunk.Max(p => p.Value)))
            .ToList();
    }
}

/// <summary>What a preview found.</summary>
/// <param name="ValueNow">The condition's value now, or null when it can't be read.</param>
/// <param name="HoldsNow">Whether the rule would fire now, or null when it can't be read.</param>
/// <param name="NowUnavailableReason">Why the value now can't be read, one of <see cref="AlertRuleSkipReasons"/>.</param>
/// <param name="Replay">The last 24 hours replayed, or null.</param>
/// <param name="ReplayUnsupportedReason">Why there is no replay, one of <see cref="AlertRuleReplayUnsupported"/>.</param>
internal sealed record AlertRulePreviewResult(int? ValueNow, bool? HoldsNow, string? NowUnavailableReason, AlertRuleReplay? Replay, string? ReplayUnsupportedReason);

/// <summary>The last 24 hours of a rule, replayed.</summary>
/// <param name="FromUtc">The replay's first minute.</param>
/// <param name="ToUtc">The replay's last minute (the minute of the preview).</param>
/// <param name="Series">The condition's value over time (a count, or minutes silent), at most <see cref="AlertRulePreviewer.MaxChartPoints"/> points.</param>
/// <param name="Periods">When the rule would have been firing; an open period has no end.</param>
internal sealed record AlertRuleReplay(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<AlertRulePreviewPoint> Series, IReadOnlyList<AlertRulePreviewPeriod> Periods);

/// <summary>One point of a replay's series.</summary>
internal sealed record AlertRulePreviewPoint(DateTime AtUtc, int Value);

/// <summary>A stretch of a replay during which the rule would have been firing.</summary>
internal sealed record AlertRulePreviewPeriod(DateTime FromUtc, DateTime? ToUtc);

/// <summary>Why a preview has no replay.</summary>
internal static class AlertRuleReplayUnsupported
{
    /// <summary>Backlog and blocked-session values can't be rebuilt from history: deletes leave no trace, and there is no past deferred count.</summary>
    public const string NoHistory = "no_history";

    /// <summary>Classifications arrive after the failure, so a past count by category would be today's view.</summary>
    public const string Categories = "categories";

    /// <summary>The storage provider has no alert signal reads.</summary>
    public const string KindUnavailable = "kind_unavailable";
}
