using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Heartbeat;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>Reader-authorized fleet heartbeat history projection.</summary>
public sealed class HeartbeatImplementation : IHeartbeatApiController
{
    private readonly IHeartbeatService _heartbeatService;
    private readonly IHeartbeatHistoryStore? _historyStore;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly TimeProvider _timeProvider;

    public HeartbeatImplementation(
        IHeartbeatService heartbeatService,
        IEnumerable<IHeartbeatHistoryStore> historyStores,
        IEndpointAuthorizationService authorizationService,
        TimeProvider? timeProvider = null)
    {
        _heartbeatService = heartbeatService;
        _historyStore = historyStores.SingleOrDefault();
        _authorizationService = authorizationService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ActionResult<HeartbeatPage>> GetHeartbeatPageAsync(int windowDays)
    {
        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader))
        {
            return new ForbidResult();
        }

        windowDays = Math.Clamp(windowDays, 1, 90);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var fromDayUtc = now.Date.AddDays(-(windowDays - 1));
        var overview = (await _heartbeatService.GetOverviewAsync())
            .Where(row => row.IsHeartbeatEnabled != false)
            .ToList();
        var storedUptimeDays = _historyStore is null
            ? []
            : await _historyStore.GetHeartbeatUptimeDays(fromDayUtc);
        var activeIds = overview.Select(row => row.EndpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uptimeDays = overview
            .SelectMany(row => SelectCanonicalDays(row.EndpointId, storedUptimeDays))
            .ToList();
        var gaps = _historyStore is null
            ? []
            : (await _historyStore.GetHeartbeatGaps(fromDayUtc))
                .Where(gap => activeIds.Contains(gap.EndpointId))
                .ToList();
        var adapters = overview.Select(row => BuildAdapter(row, uptimeDays, fromDayUtc, windowDays, now)).ToList();
        var gapRows = gaps.Select(gap => BuildGap(gap, now)).OrderByDescending(gap => gap.FromUtc).ToList();
        var expected = uptimeDays.Where(day => activeIds.Contains(day.EndpointId)).Sum(day => day.Expected);
        var received = uptimeDays.Where(day => activeIds.Contains(day.EndpointId)).Sum(day => day.Received);
        var todayRows = uptimeDays.Where(day => activeIds.Contains(day.EndpointId) && day.DayUtc.Date == now.Date).ToList();

        return new HeartbeatPage
        {
            WindowDays = windowDays,
            AdaptersReporting = adapters.Count(adapter => adapter.Liveness == "alive"),
            AdaptersTotal = adapters.Count,
            AdaptersNeedingAttention = adapters
                .Where(adapter => adapter.Liveness is "late" or "missing" || adapter.Uptime is < 0.99)
                .Select(adapter => adapter.EndpointId)
                .ToList(),
            FleetUptime = expected == 0 ? null : (double)received / expected,
            MissedBeatsToday = todayRows.Sum(day => day.Missed),
            AdaptersMissingBeatsToday = todayRows.Count(day => day.Missed > 0),
            LongestGap = gapRows.Count == 0 ? null : gapRows.Max(gap => gap.DurationSeconds),
            Adapters = adapters,
            Gaps = gapRows,
        };
    }

    private static HeartbeatAdapterRow BuildAdapter(
        HeartbeatOverviewItem overview,
        IEnumerable<HeartbeatUptimeDay> allDays,
        DateTime fromDayUtc,
        int windowDays,
        DateTime now)
    {
        var rows = SelectCanonicalDays(overview.EndpointId, allDays)
            .ToDictionary(day => day.DayUtc.Date);
        var days = new List<HeartbeatDay>(windowDays);
        for (var offset = 0; offset < windowDays; offset++)
        {
            var dayUtc = fromDayUtc.AddDays(offset);
            if (!rows.TryGetValue(dayUtc, out var row))
            {
                days.Add(new HeartbeatDay { DayUtc = dayUtc, State = "none" });
                continue;
            }

            var observedSpanSeconds = dayUtc == now.Date
                ? Math.Max(1, (now - dayUtc).TotalSeconds)
                : 86400.0;
            var coverage = Math.Clamp(row.ObservedSeconds / observedSpanSeconds, 0, 1);
            days.Add(new HeartbeatDay
            {
                DayUtc = dayUtc,
                State = row.LongestGapSeconds >= 3600
                    ? "gap"
                    : row.Missed > 0 || coverage < 0.9
                        ? "partial"
                        : "full",
                Missed = row.Missed,
                Expected = row.Expected,
                Coverage = coverage,
                LongestGapSeconds = row.LongestGapSeconds,
            });
        }

        var expected = rows.Values.Sum(day => day.Expected);
        var received = rows.Values.Sum(day => day.Received);
        return new HeartbeatAdapterRow
        {
            EndpointId = overview.EndpointId,
            Liveness = ToLiveness(overview.Status),
            Status = overview.Status.ToString(),
            LastBeatUtc = overview.LastStartTime,
            RoundTripMs = overview.RoundTripMs,
            Uptime = expected == 0 ? null : (double)received / expected,
            SdkVersion = string.IsNullOrWhiteSpace(overview.SdkVersion) ? null : overview.SdkVersion,
            Days = days,
        };
    }

    private static IEnumerable<HeartbeatUptimeDay> SelectCanonicalDays(
        string endpointId,
        IEnumerable<HeartbeatUptimeDay> allDays)
        => allDays
            .Where(day => string.Equals(day.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(day => day.DayUtc.Date)
            .Select(group => group
                .OrderByDescending(day => string.Equals(day.EndpointId, endpointId, StringComparison.Ordinal))
                .ThenByDescending(day => day.LastBeatUtc)
                .ThenBy(day => day.EndpointId, StringComparer.Ordinal)
                .First());

    private static HeartbeatGapRow BuildGap(HeartbeatGap gap, DateTime now)
    {
        var end = gap.ToUtc ?? now;
        var duration = Math.Max(0, Math.Round((end - gap.FromUtc).TotalSeconds));
        return new HeartbeatGapRow
        {
            EndpointId = gap.EndpointId,
            FromUtc = gap.FromUtc,
            ToUtc = gap.ToUtc,
            DurationSeconds = (int)Math.Min(int.MaxValue, duration),
            Ongoing = gap.ToUtc is null,
            Cause = gap.ToUtc is null
                ? "stillMissing"
                : !string.IsNullOrWhiteSpace(gap.SdkVersionBefore) &&
                  !string.IsNullOrWhiteSpace(gap.SdkVersionAfter) &&
                  !string.Equals(gap.SdkVersionBefore, gap.SdkVersionAfter, StringComparison.Ordinal)
                    ? "redeployed"
                    : null,
        };
    }

    /// <summary>The liveness the heartbeat page and the Monitor feed report for a settled status.</summary>
    internal static string ToLiveness(HeartbeatStatus status) => status switch
    {
        HeartbeatStatus.On or HeartbeatStatus.Unsupported => "alive",
        HeartbeatStatus.Pending => "late",
        HeartbeatStatus.Off => "missing",
        _ => "notDeployed",
    };
}
