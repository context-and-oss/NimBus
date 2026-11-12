using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.MonitorFeed;

/// <summary>A query window: [From, To).</summary>
/// <param name="From">Inclusive start.</param>
/// <param name="To">Exclusive end.</param>
public readonly record struct FeedWindow(DateTime From, DateTime To);

/// <summary>
/// The Monitor feed's time windows. Series windows start on a bucket boundary and end at the end
/// of the current bucket, so they always hold the same number of buckets and the last one is the
/// current, partial bucket.
/// </summary>
public static class MonitorFeedWindows
{
    /// <summary>Bucket key length for minute buckets (ISO-8601 truncated to minutes).</summary>
    public const int MinuteKeyLength = 16;

    /// <summary>Bucket key length for hour buckets (ISO-8601 truncated to hours).</summary>
    public const int HourKeyLength = 13;

    /// <summary>60 minute buckets, the last one being the current minute.</summary>
    public static FeedWindow LastHourByMinute(DateTime nowUtc)
    {
        var end = FloorToMinute(nowUtc).AddMinutes(1);
        return new FeedWindow(end.AddMinutes(-60), end);
    }

    /// <summary>72 hour buckets, the last one being the current hour.</summary>
    public static FeedWindow LastThreeDaysByHour(DateTime nowUtc)
    {
        var end = FloorToHour(nowUtc).AddHours(1);
        return new FeedWindow(end.AddHours(-72), end);
    }

    /// <summary>Up to 24 hour buckets, the last one being the current hour.</summary>
    public static FeedWindow LastDayByHour(DateTime nowUtc)
    {
        var end = FloorToHour(nowUtc).AddHours(1);
        return new FeedWindow(end.AddHours(-24), end);
    }

    /// <summary>The 60 minutes up to now, for per-endpoint totals.</summary>
    public static FeedWindow TrailingHour(DateTime nowUtc) => new(nowUtc.AddHours(-1), nowUtc);

    /// <summary>The 24 hours up to now, for per-endpoint averages.</summary>
    public static FeedWindow TrailingDay(DateTime nowUtc) => new(nowUtc.AddDays(-1), nowUtc);

    /// <summary>The start of the minute holding <paramref name="value"/>, as UTC.</summary>
    public static DateTime FloorToMinute(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Utc);

    /// <summary>The start of the hour holding <paramref name="value"/>, as UTC.</summary>
    public static DateTime FloorToHour(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Combines aggregates of disjoint populations: the average is weighted by each one's count,
    /// minimum and maximum span all of them. Empty aggregates (count 0) are ignored.
    /// </summary>
    public static LatencyAggregate Combine(IEnumerable<LatencyAggregate> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var count = 0;
        var sum = 0d;
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var part in parts)
        {
            if (part is null || part.Count <= 0)
                continue;
            count += part.Count;
            sum += part.AvgMs * part.Count;
            min = Math.Min(min, part.MinMs);
            max = Math.Max(max, part.MaxMs);
        }

        return count == 0
            ? new LatencyAggregate()
            : new LatencyAggregate { Count = count, AvgMs = sum / count, MinMs = min, MaxMs = max };
    }
}
