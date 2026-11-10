using System;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Optional queries behind the WebApp's Monitor dashboard. Separate from
/// <see cref="IMetricsStore"/> and <see cref="IMessageTrackingStore"/> so existing implementations
/// of those keep compiling; the WebApp reports the affected numbers as unavailable when no
/// implementation is registered.
/// </summary>
public interface IMonitorMetricsStore
{
    /// <summary>
    /// Queue and processing time per time bucket, across all endpoints, for outcome messages
    /// enqueued in [<paramref name="from"/>, <paramref name="to"/>): resolution, error, skip,
    /// deferral and unsupported responses, as in
    /// <see cref="IMetricsStore.GetEndpointLatencyMetrics(DateTime, DateTime)"/>. Each series
    /// counts only the messages that carry its timing. Buckets are sparse: a bucket with no timed
    /// outcome is absent, because a zero would read as "instant". Bucket keys are the bucket start
    /// as ISO-8601 UTC truncated to <paramref name="substringLength"/>, matching
    /// <see cref="IMetricsStore.GetTimeSeriesMetrics(DateTime, DateTime, int, string)"/>.
    /// </summary>
    /// <param name="from">Window start, inclusive.</param>
    /// <param name="to">Window end, exclusive.</param>
    /// <param name="substringLength">16 for minute buckets, 13 for hour buckets, 10 for day buckets.</param>
    /// <param name="bucketLabel">Returned as <see cref="LatencyTimeSeriesResult.BucketSize"/>.</param>
    /// <returns>The buckets that have at least one timed outcome, in ascending order.</returns>
    Task<LatencyTimeSeriesResult> GetLatencyTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel);

    /// <summary>
    /// The number of distinct, non-empty session ids among the endpoint's non-deleted unresolved
    /// events whose status is Failed or DeadLettered, or Pending with the <c>Handoff</c> sub-status:
    /// the events that can hold a session blocked. An estimate of the sessions blocked on Service
    /// Bus: a message dead-lettered on its first attempt does not block its session but is counted.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <returns>The number of sessions.</returns>
    Task<int> GetBlockedSessionCount(string endpointId);
}
