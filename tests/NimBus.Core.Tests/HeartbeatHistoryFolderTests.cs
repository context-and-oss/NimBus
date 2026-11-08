#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class HeartbeatHistoryFolderTests
{
    private static readonly DateTime Day = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Fold_counts_received_missed_and_observed_seconds()
    {
        var result = HeartbeatHistoryFolder.Fold("orders", [
            Beat(0, HeartbeatStatus.On, 300),
            Beat(5, HeartbeatStatus.Unsupported, 300),
            Beat(10, HeartbeatStatus.Unknown, 60),
            Beat(11, HeartbeatStatus.Off, 60),
        ], [], null, Day.AddDays(-1), 30);

        var row = result.Days.Single();
        Assert.AreEqual(4, row.Expected);
        Assert.AreEqual(3, row.Received);
        Assert.AreEqual(1, row.Missed);
        Assert.AreEqual(720, row.ObservedSeconds);
        Assert.AreEqual(60, row.LongestGapSeconds);
        Assert.AreEqual(1, result.Gaps.Count);
        Assert.IsNull(result.Gaps[0].ToUtc);
    }

    [TestMethod]
    public void Fold_is_idempotent_and_does_not_mutate_existing_rows()
    {
        var existing = new HeartbeatUptimeDay
        {
            EndpointId = "orders",
            DayUtc = Day,
            LastBeatUtc = Day.AddMinutes(5),
            Expected = 2,
            Received = 2,
        };

        var result = HeartbeatHistoryFolder.Fold(
            "orders", [Beat(0, HeartbeatStatus.On), Beat(5, HeartbeatStatus.On)], [existing], null, Day.AddDays(-1), 300);

        Assert.AreEqual(0, result.Days.Count);
        Assert.AreEqual(2, existing.Expected);
    }

    [TestMethod]
    public void Fold_prefers_exact_endpoint_casing_when_history_contains_case_variants()
    {
        var canonical = new HeartbeatUptimeDay
        {
            EndpointId = "Orders",
            DayUtc = Day,
            LastBeatUtc = Day.AddMinutes(5),
            Expected = 2,
            Received = 2,
        };
        var duplicate = new HeartbeatUptimeDay
        {
            EndpointId = "orders",
            DayUtc = Day,
            LastBeatUtc = Day.AddMinutes(10),
            Expected = 99,
            Received = 99,
        };

        var result = HeartbeatHistoryFolder.Fold(
            "Orders", [Beat(15, HeartbeatStatus.On)], [canonical, duplicate], null, Day.AddDays(-1), 300);

        Assert.AreEqual(3, result.Days.Single().Expected);
        Assert.AreEqual(3, result.Days.Single().Received);
    }

    [TestMethod]
    public void Fold_stops_at_pending_and_recovers_when_it_settles()
    {
        var first = HeartbeatHistoryFolder.Fold("orders", [
            Beat(0, HeartbeatStatus.On),
            Beat(5, HeartbeatStatus.Pending),
            Beat(10, HeartbeatStatus.On),
        ], [], null, Day.AddDays(-1), 300);
        Assert.AreEqual(1, first.Days.Single().Received);

        var second = HeartbeatHistoryFolder.Fold("orders", [
            Beat(0, HeartbeatStatus.On),
            Beat(5, HeartbeatStatus.On),
            Beat(10, HeartbeatStatus.On),
        ], first.Days, null, Day.AddDays(-1), 300);

        Assert.AreEqual(3, second.Days.Single().Received);
    }

    [TestMethod]
    public void Fold_preserves_gap_duration_across_one_beat_folds_and_closes_at_recovery()
    {
        var days = new List<HeartbeatUptimeDay>();
        HeartbeatGap? gap = null;
        for (var minute = 0; minute < 60; minute += 5)
        {
            var result = HeartbeatHistoryFolder.Fold(
                "orders", [Beat(minute, HeartbeatStatus.Off)], days, gap, Day.AddDays(-1), 300);
            days = result.Days.ToList();
            gap = (result.Gaps.Count > 0 ? result.Gaps[0] : null) ?? gap;
        }

        Assert.AreEqual(3600, days.Single().LongestGapSeconds);
        Assert.IsNotNull(gap);

        var closed = HeartbeatHistoryFolder.Fold(
            "orders", [Beat(60, HeartbeatStatus.On)], days, gap, Day.AddDays(-1), 300);
        var closedGap = closed.Gaps.Single();
        Assert.AreEqual(3600, (closedGap.ToUtc!.Value - closedGap.FromUtc).TotalSeconds);
    }

    [TestMethod]
    public void Fold_ignores_expired_retained_beats_when_the_watermark_has_expired()
    {
        var cutoff = Day.AddDays(1);
        var result = HeartbeatHistoryFolder.Fold(
            "orders", [Beat(0, HeartbeatStatus.On)], [], null, cutoff, 300);

        Assert.AreEqual(0, result.Days.Count);
    }

    [TestMethod]
    public void Fold_uses_fallback_interval_only_for_legacy_beats()
    {
        var result = HeartbeatHistoryFolder.Fold("orders", [
            Beat(0, HeartbeatStatus.On, 60),
            Beat(1, HeartbeatStatus.On, 0),
        ], [], null, Day.AddDays(-1), 300);

        Assert.AreEqual(360, result.Days.Single().ObservedSeconds);
    }

    [TestMethod]
    public void Fold_assigns_probes_to_utc_days_and_caps_observation_at_midnight()
    {
        var result = HeartbeatHistoryFolder.Fold("orders", [
            Beat(23 * 60 + 59, HeartbeatStatus.On, 300),
            Beat(24 * 60 + 4, HeartbeatStatus.On, 300),
        ], [], null, Day.AddDays(-1), 300);

        Assert.AreEqual(2, result.Days.Count);
        Assert.AreEqual(60, result.Days[0].ObservedSeconds);
        Assert.AreEqual(300, result.Days[1].ObservedSeconds);
    }

    [TestMethod]
    public void Fold_unsupported_response_closes_an_open_gap()
    {
        var result = HeartbeatHistoryFolder.Fold("orders", [
            Beat(0, HeartbeatStatus.Off),
            Beat(5, HeartbeatStatus.Unsupported),
        ], [], null, Day.AddDays(-1), 300);

        Assert.AreEqual(1, result.Days.Single().Missed);
        Assert.AreEqual(1, result.Days.Single().Received);
        Assert.AreEqual(Day.AddMinutes(5), result.Gaps.Single().ToUtc);
    }

    [TestMethod]
    public void Fold_accepts_null_and_empty_inputs()
    {
        var result = HeartbeatHistoryFolder.Fold("orders", null, null, null, Day, 300);

        Assert.AreEqual(0, result.Days.Count);
        Assert.AreEqual(0, result.Gaps.Count);
    }

    private static Heartbeat Beat(int minutes, HeartbeatStatus status, int intervalSeconds = 300)
        => new()
        {
            MessageId = $"beat-{minutes}",
            StartTime = Day.AddMinutes(minutes),
            ReceivedTime = Day.AddMinutes(minutes),
            EndTime = Day.AddMinutes(minutes).AddSeconds(1),
            IntervalSeconds = intervalSeconds,
            EndpointHeartbeatStatus = status,
            SdkVersion = status == HeartbeatStatus.Off ? string.Empty : "1.2.3",
        };
}
