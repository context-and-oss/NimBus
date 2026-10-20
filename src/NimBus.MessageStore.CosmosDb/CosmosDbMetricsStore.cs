using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IMetricsStore"/>: server-side aggregation
/// over the messages container. Carved out of <see cref="CosmosDbClient"/>, which
/// keeps the container cache and exposes it via the injected accessor.
/// </summary>
internal sealed class CosmosDbMetricsStore : IMetricsStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getMessagesContainer;

    public CosmosDbMetricsStore(Func<Task<ICosmosContainerAdapter>> getMessagesContainer)
    {
        _getMessagesContainer = getMessagesContainer;
    }

    [Obsolete("Use GetEndpointMetrics(from, to). This overload will be removed in v5.")]
    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => GetEndpointMetrics(from, DateTime.UtcNow);

    public async Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from, DateTime to)
    {
        var container = await _getMessagesContainer();
        var fromIso = from.ToString("o");
        var toIso = to.ToString("o");

        // The three aggregates are independent — run them concurrently so the
        // endpoint overview costs one round-trip's latency instead of three.
        var publishedTask = RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.message[\"From\"] AS endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            "GROUP BY c.message[\"From\"], c.message.EventTypeId",
            fromIso, toIso);

        var failedTask = RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'ErrorResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso, toIso);

        var handledTask = RunEventTypeCountQuery(container,
            "SELECT COUNT(1) AS count, c.endpointId, c.message.EventTypeId FROM c " +
            "WHERE c.message.MessageType = 'ResolutionResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso, toIso);

        await Task.WhenAll(publishedTask, failedTask, handledTask);

        return new EndpointMetricsResult
        {
            Published = await publishedTask,
            Handled = await handledTask,
            Failed = await failedTask
        };
    }

    [Obsolete("Use GetEndpointLatencyMetrics(from, to). This overload will be removed in v5.")]
    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => GetEndpointLatencyMetrics(from, DateTime.UtcNow);

    public async Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from, DateTime to)
    {
        // Server-side aggregation over all ResolutionResponse / ErrorResponse
        // documents in the period. Two queries (one per timing series) so we
        // can WHERE-out null values cleanly — the GROUP BY keys must align
        // across the two so we can stitch them back together.
        // Picking only outcome documents avoids double-counting (the original
        // EventRequest doesn't carry timings; only the response does).
        var container = await _getMessagesContainer();
        var fromIso = from.ToString("o");
        var toIso = to.ToString("o");
        var outcomeFilter =
            "(c.message.MessageType = 'ResolutionResponse' OR " +
            " c.message.MessageType = 'ErrorResponse' OR " +
            " c.message.MessageType = 'SkipResponse' OR " +
            " c.message.MessageType = 'DeferralResponse' OR " +
            " c.message.MessageType = 'UnsupportedResponse')";

        // Queue-time and processing-time aggregates are independent — run them
        // concurrently and stitch the results below.
        var queueRowsTask = RunLatencyAggregateQuery(container,
            "SELECT c.endpointId, c.message.EventTypeId, " +
            "       COUNT(1) AS count, " +
            "       AVG(c.message.QueueTimeMs) AS avg, " +
            "       MIN(c.message.QueueTimeMs) AS min, " +
            "       MAX(c.message.QueueTimeMs) AS max " +
            "FROM c " +
            $"WHERE {outcomeFilter} " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            "AND IS_DEFINED(c.message.QueueTimeMs) " +
            "AND c.message.QueueTimeMs != null " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso, toIso);

        var processingRowsTask = RunLatencyAggregateQuery(container,
            "SELECT c.endpointId, c.message.EventTypeId, " +
            "       COUNT(1) AS count, " +
            "       AVG(c.message.ProcessingTimeMs) AS avg, " +
            "       MIN(c.message.ProcessingTimeMs) AS min, " +
            "       MAX(c.message.ProcessingTimeMs) AS max " +
            "FROM c " +
            $"WHERE {outcomeFilter} " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            "AND IS_DEFINED(c.message.ProcessingTimeMs) " +
            "AND c.message.ProcessingTimeMs != null " +
            "GROUP BY c.endpointId, c.message.EventTypeId",
            fromIso, toIso);

        await Task.WhenAll(queueRowsTask, processingRowsTask);
        var queueRows = await queueRowsTask;
        var processingRows = await processingRowsTask;

        // Merge by (endpointId, eventTypeId). One side may be missing rows
        // (e.g. processing time not captured pre-fix); leaves that side at
        // its default zeroed aggregate.
        var grouped = new Dictionary<(string Endpoint, string EventType), EndpointLatencyAggregate>();
        foreach (var row in queueRows)
        {
            var key = (row.EndpointId ?? string.Empty, row.EventTypeId ?? string.Empty);
            if (!grouped.TryGetValue(key, out var agg))
            {
                agg = new EndpointLatencyAggregate { EndpointId = key.Item1, EventTypeId = key.Item2 };
                grouped[key] = agg;
            }
            agg.Queue = new LatencyAggregate { Count = row.Count, AvgMs = row.Avg, MinMs = row.Min, MaxMs = row.Max };
        }
        foreach (var row in processingRows)
        {
            var key = (row.EndpointId ?? string.Empty, row.EventTypeId ?? string.Empty);
            if (!grouped.TryGetValue(key, out var agg))
            {
                agg = new EndpointLatencyAggregate { EndpointId = key.Item1, EventTypeId = key.Item2 };
                grouped[key] = agg;
            }
            agg.Processing = new LatencyAggregate { Count = row.Count, AvgMs = row.Avg, MinMs = row.Min, MaxMs = row.Max };
        }

        return new EndpointLatencyMetricsResult { Latencies = grouped.Values.ToList() };
    }

    [Obsolete("Use GetFailedMessageInsights(from, to). This overload will be removed in v5.")]
    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => GetFailedMessageInsights(from, DateTime.UtcNow);

    public async Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to)
    {
        var container = await _getMessagesContainer();
        var fromIso = from.ToString("o");
        var toIso = to.ToString("o");

        var sql = "SELECT c.endpointId, c.message.EventTypeId, " +
                  "c.message.MessageContent.ErrorContent.ErrorText, " +
                  "c.message.EnqueuedTimeUtc, c.message.EventId " +
                  "FROM c " +
                  "WHERE c.message.MessageType = 'ErrorResponse' " +
                  "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to";

        var query = new QueryDefinition(sql).WithParameter("@from", fromIso).WithParameter("@to", toIso);
        // Bound page size so a high-failure window streams in pages instead of
        // materialising one huge response (each row already projects just the
        // fields below, never the full document). The loop still drains every match.
        var iterator = container.GetItemQueryIterator<FailedMessageQueryResult>(query, null,
            new QueryRequestOptions { MaxItemCount = 1000 });
        var results = new List<FailedMessageInfo>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                results.Add(new FailedMessageInfo
                {
                    EndpointId = item.EndpointId,
                    EventTypeId = item.EventTypeId,
                    ErrorText = item.ErrorText,
                    EnqueuedTimeUtc = item.EnqueuedTimeUtc,
                    EventId = item.EventId
                });
            }
        }

        return results;
    }

    [Obsolete("Use GetTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => GetTimeSeriesMetrics(from, DateTime.UtcNow, substringLength, bucketLabel);

    public async Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        var container = await _getMessagesContainer();
        var fromIso = from.ToString("o");
        var toIso = to.ToString("o");

        // The three bucket aggregates are independent — run them concurrently.
        var publishedBucketsTask = RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso, toIso);

        var handledBucketsTask = RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'ResolutionResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso, toIso);

        var failedBucketsTask = RunBucketCountQuery(container,
            $"SELECT COUNT(1) AS count, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'ErrorResponse' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            $"GROUP BY SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})",
            fromIso, toIso);

        await Task.WhenAll(publishedBucketsTask, handledBucketsTask, failedBucketsTask);
        var publishedBuckets = await publishedBucketsTask;
        var handledBuckets = await handledBucketsTask;
        var failedBuckets = await failedBucketsTask;

        var allBucketKeys = GenerateBucketKeys(from, to, substringLength)
            .Concat(publishedBuckets.Keys)
            .Concat(handledBuckets.Keys)
            .Concat(failedBuckets.Keys)
            .Distinct()
            .OrderBy(k => k);

        var dataPoints = allBucketKeys.Select(key => new TimeSeriesBucket
        {
            Timestamp = key,
            Published = publishedBuckets.GetValueOrDefault(key),
            Handled = handledBuckets.GetValueOrDefault(key),
            Failed = failedBuckets.GetValueOrDefault(key)
        }).ToList();

        return new TimeSeriesResult { BucketSize = bucketLabel, DataPoints = dataPoints };
    }

    [Obsolete("Use GetEventTypeTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => GetEventTypeTimeSeriesMetrics(from, DateTime.UtcNow, substringLength, bucketLabel);

    public async Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        var container = await _getMessagesContainer();
        var fromIso = from.ToString("o");
        var toIso = to.ToString("o");

        // Same `SELECT VALUE { agg, non-agg }` restriction as GetEndpointMetrics —
        // use plain column aliases instead. Result rows: { count, eventTypeId, bucket }.
        var sql =
            $"SELECT COUNT(1) AS count, c.message.EventTypeId AS eventTypeId, " +
            $"SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength}) AS bucket " +
            "FROM c WHERE c.message.MessageType = 'EventRequest' " +
            "AND c.message.EnqueuedTimeUtc >= @from AND c.message.EnqueuedTimeUtc < @to " +
            $"GROUP BY c.message.EventTypeId, SUBSTRING(c.message.EnqueuedTimeUtc, 0, {substringLength})";

        var query = new QueryDefinition(sql).WithParameter("@from", fromIso).WithParameter("@to", toIso);
        var iterator = container.GetItemQueryIterator<EventTypeBucketCountRow>(query);

        // Cross-partition GROUP BY can surface partial aggregates per physical
        // partition — accumulate with += rather than assigning.
        var counts = new Dictionary<(string EventTypeId, string Bucket), int>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            foreach (var item in page)
            {
                if (string.IsNullOrEmpty(item.EventTypeId) || string.IsNullOrEmpty(item.Bucket))
                    continue;
                var key = (item.EventTypeId, item.Bucket);
                counts.TryGetValue(key, out var existing);
                counts[key] = existing + item.Count;
            }
        }

        var series = counts
            .GroupBy(kv => kv.Key.EventTypeId)
            .Select(g => new EventTypeSeriesEntry
            {
                EventTypeId = g.Key,
                Total = g.Sum(kv => kv.Value),
                DataPoints = g
                    .OrderBy(kv => kv.Key.Bucket)
                    .Select(kv => new EventTypeSeriesBucket { Timestamp = kv.Key.Bucket, Published = kv.Value })
                    .ToList(),
            })
            .OrderByDescending(s => s.Total)
            .ToList();

        return new EventTypeTimeSeriesResult { BucketSize = bucketLabel, Series = series };
    }

    private static async Task<List<LatencyAggregateRow>> RunLatencyAggregateQuery(ICosmosContainerAdapter container, string sql, string fromIso, string toIso)
    {
        var query = new QueryDefinition(sql).WithParameter("@from", fromIso).WithParameter("@to", toIso);
        var iterator = container.GetItemQueryIterator<LatencyAggregateRow>(query);
        var results = new List<LatencyAggregateRow>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            foreach (var row in page) results.Add(row);
        }
        return results;
    }

    private static async Task<Dictionary<string, int>> RunBucketCountQuery(ICosmosContainerAdapter container, string sql, string fromIso, string toIso)
    {
        var query = new QueryDefinition(sql).WithParameter("@from", fromIso).WithParameter("@to", toIso);
        var iterator = container.GetItemQueryIterator<BucketCountResult>(query);
        var results = new Dictionary<string, int>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                if (item.Bucket != null)
                    results[item.Bucket] = item.Count;
            }
        }

        return results;
    }

    private static List<string> GenerateBucketKeys(DateTime from, DateTime to, int substringLength)
    {
        var keys = new List<string>();
        var current = substringLength switch
        {
            16 => new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, DateTimeKind.Utc),
            13 => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc),
            10 => new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc)
        };

        var step = substringLength switch
        {
            16 => TimeSpan.FromMinutes(1),
            13 => TimeSpan.FromHours(1),
            10 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };

        while (current < to)
        {
            var key = current.ToString("o")[..substringLength];
            keys.Add(key);
            current += step;
        }

        return keys;
    }

    private static async Task<List<EndpointEventTypeCount>> RunEventTypeCountQuery(ICosmosContainerAdapter container, string sql, string fromIso, string toIso)
    {
        var query = new QueryDefinition(sql).WithParameter("@from", fromIso).WithParameter("@to", toIso);
        var iterator = container.GetItemQueryIterator<MetricsEventTypeCountResult>(query);
        var results = new List<EndpointEventTypeCount>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                results.Add(new EndpointEventTypeCount
                {
                    EndpointId = item.EndpointId,
                    EventTypeId = item.EventTypeId,
                    Count = item.Count
                });
            }
        }

        return results;
    }

    private sealed class EventTypeBucketCountRow
    {
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("eventTypeId")] public string? EventTypeId { get; set; }
        [JsonProperty("bucket")] public string? Bucket { get; set; }
    }

    private sealed class LatencyAggregateRow
    {
        [JsonProperty("endpointId")] public string? EndpointId { get; set; }
        [JsonProperty("EventTypeId")] public string? EventTypeId { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
        [JsonProperty("avg")] public double Avg { get; set; }
        [JsonProperty("min")] public double Min { get; set; }
        [JsonProperty("max")] public double Max { get; set; }
    }

    private sealed class FailedMessageQueryResult
    {
        [JsonProperty("endpointId")]
        public string EndpointId { get; set; } = null!;

        [JsonProperty("EventTypeId")]
        public string EventTypeId { get; set; } = null!;

        [JsonProperty("ErrorText")]
        public string? ErrorText { get; set; }

        [JsonProperty("EnqueuedTimeUtc")]
        public DateTime EnqueuedTimeUtc { get; set; }

        [JsonProperty("EventId")]
        public string EventId { get; set; } = null!;
    }

    private sealed class BucketCountResult
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("bucket")]
        public string? Bucket { get; set; }
    }

    private sealed class MetricsEventTypeCountResult
    {
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("endpointId")]
        public string EndpointId { get; set; } = null!;

        [JsonProperty("EventTypeId")]
        public string EventTypeId { get; set; } = null!;
    }
}
