using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>Pure arithmetic that folds retained heartbeat probes into durable history.</summary>
public static class HeartbeatHistoryFolder
{
    /// <summary>Folds settled probes that have not passed the durable watermark.</summary>
    public static HeartbeatFoldResult Fold(
        string endpointId,
        IEnumerable<Heartbeat>? beats,
        IEnumerable<HeartbeatUptimeDay>? existing,
        HeartbeatGap? openGap,
        DateTime historyStartUtc,
        int fallbackIntervalSeconds)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fallbackIntervalSeconds);

        var existingDays = (existing ?? [])
            .Where(day => string.Equals(day.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(day => day.DayUtc.Date)
            .ToDictionary(
                group => group.Key,
                group => CloneDay(group
                    .OrderByDescending(day => string.Equals(day.EndpointId, endpointId, StringComparison.Ordinal))
                    .ThenByDescending(day => day.LastBeatUtc)
                    .ThenBy(day => day.EndpointId, StringComparer.Ordinal)
                    .First()));
        var watermark = existingDays.Count == 0
            ? historyStartUtc
            : existingDays.Values.Max(day => day.LastBeatUtc);
        if (watermark < historyStartUtc)
        {
            watermark = historyStartUtc;
        }

        var ordered = (beats ?? [])
            .OrderBy(beat => beat.StartTime)
            .ThenBy(beat => beat.MessageId, StringComparer.Ordinal)
            .ToList();
        var changedDays = new Dictionary<DateTime, HeartbeatUptimeDay>();
        var changedGaps = new List<HeartbeatGap>();
        var gap = openGap is null ? null : CloneGap(openGap);
        var latestSdkVersion = ordered
            .Where(beat => beat.StartTime <= watermark && !string.IsNullOrWhiteSpace(beat.SdkVersion))
            .Select(beat => beat.SdkVersion)
            .LastOrDefault() ?? string.Empty;

        foreach (var beat in ordered)
        {
            if (beat.StartTime <= watermark || beat.StartTime < historyStartUtc)
            {
                continue;
            }

            if (beat.EndpointHeartbeatStatus == HeartbeatStatus.Pending)
            {
                break;
            }

            var dayUtc = beat.StartTime.Date;
            if (!existingDays.TryGetValue(dayUtc, out var day))
            {
                day = new HeartbeatUptimeDay
                {
                    Id = BuildDayId(endpointId, dayUtc),
                    EndpointId = endpointId,
                    DayUtc = dayUtc,
                };
                existingDays.Add(dayUtc, day);
            }

            var intervalSeconds = beat.IntervalSeconds > 0
                ? beat.IntervalSeconds
                : fallbackIntervalSeconds;
            var secondsUntilMidnight = (int)Math.Max(0, (dayUtc.AddDays(1) - beat.StartTime).TotalSeconds);
            day.Expected++;
            day.ObservedSeconds += Math.Min(intervalSeconds, secondsUntilMidnight);
            day.LastBeatUtc = beat.StartTime;

            if (beat.EndpointHeartbeatStatus == HeartbeatStatus.Off)
            {
                day.Missed++;
                if (gap is null)
                {
                    gap = new HeartbeatGap
                    {
                        Id = BuildGapId(endpointId, beat.StartTime),
                        EndpointId = endpointId,
                        FromUtc = beat.StartTime,
                        SdkVersionBefore = latestSdkVersion,
                        SdkVersionAfter = string.Empty,
                    };
                    changedGaps.Add(gap);
                }

                var runSeconds = (int)Math.Round((beat.StartTime - gap.FromUtc).TotalSeconds) + intervalSeconds;
                day.LongestGapSeconds = Math.Max(day.LongestGapSeconds, runSeconds);
            }
            else
            {
                day.Received++;
                if (gap is not null)
                {
                    gap.ToUtc = beat.StartTime;
                    gap.SdkVersionAfter = beat.SdkVersion ?? string.Empty;
                    var durationSeconds = (int)Math.Max(0, Math.Round((beat.StartTime - gap.FromUtc).TotalSeconds));
                    day.LongestGapSeconds = Math.Max(day.LongestGapSeconds, durationSeconds);
                    if (!changedGaps.Contains(gap))
                    {
                        changedGaps.Add(gap);
                    }

                    gap = null;
                }
            }

            if (!string.IsNullOrWhiteSpace(beat.SdkVersion))
            {
                latestSdkVersion = beat.SdkVersion;
            }

            changedDays[dayUtc] = day;
            watermark = beat.StartTime;
        }

        return new HeartbeatFoldResult
        {
            Days = changedDays.Values.OrderBy(day => day.DayUtc).ToList(),
            Gaps = changedGaps,
        };
    }

    private static string BuildDayId(string endpointId, DateTime dayUtc)
        => $"{endpointId}|{dayUtc:yyyy-MM-dd}";

    private static string BuildGapId(string endpointId, DateTime fromUtc)
        => $"{endpointId}|{fromUtc:O}";

    private static HeartbeatUptimeDay CloneDay(HeartbeatUptimeDay day)
        => new()
        {
            Id = day.Id,
            EndpointId = day.EndpointId,
            DayUtc = day.DayUtc,
            Expected = day.Expected,
            Received = day.Received,
            Missed = day.Missed,
            ObservedSeconds = day.ObservedSeconds,
            LongestGapSeconds = day.LongestGapSeconds,
            LastBeatUtc = day.LastBeatUtc,
            TimeToLiveSeconds = day.TimeToLiveSeconds,
        };

    private static HeartbeatGap CloneGap(HeartbeatGap gap)
        => new()
        {
            Id = gap.Id,
            EndpointId = gap.EndpointId,
            FromUtc = gap.FromUtc,
            ToUtc = gap.ToUtc,
            SdkVersionBefore = gap.SdkVersionBefore,
            SdkVersionAfter = gap.SdkVersionAfter,
            TimeToLiveSeconds = gap.TimeToLiveSeconds,
        };
}
