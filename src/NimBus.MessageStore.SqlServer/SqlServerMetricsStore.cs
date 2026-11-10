using Dapper;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IMetricsStore"/>: server-side aggregation
/// over the Messages table. Carved out of <see cref="SqlServerMessageStore"/>, which
/// supplies connection/quoting/timeout via <see cref="SqlServerStoreContext"/>.
/// </summary>
internal sealed class SqlServerMetricsStore : IMetricsStore
{
    private readonly SqlServerStoreContext _ctx;

    public SqlServerMetricsStore(SqlServerStoreContext ctx)
    {
        _ctx = ctx;
    }

    private string T(string table) => _ctx.Table(table);

    // Explicit DateTime2: SqlClient otherwise binds DateTime as legacy DATETIME (~3.33 ms
    // rounding), which can move a bound past a message enqueued right at it.
    private static DynamicParameters Window(DateTime from, DateTime to)
    {
        var parameters = new DynamicParameters();
        parameters.Add("From", from, DbType.DateTime2);
        parameters.Add("To", to, DbType.DateTime2);
        return parameters;
    }

    [Obsolete("Use GetEndpointMetrics(from, to). This overload will be removed in v5.")]
    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => GetEndpointMetrics(from, DateTime.UtcNow);

    public async Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from, DateTime to)
    {
        var sql = $@"
SELECT
    CASE
        WHEN MessageType = 'EventRequest' AND NULLIF(FromAddress, '') IS NOT NULL THEN FromAddress
        ELSE EndpointId
    END AS EndpointId,
    EventTypeId,
    MessageType,
    COUNT_BIG(*) AS EventCount
FROM {T("Messages")}
WHERE EnqueuedTimeUtc >= @From
  AND EnqueuedTimeUtc < @To
GROUP BY
    CASE
        WHEN MessageType = 'EventRequest' AND NULLIF(FromAddress, '') IS NOT NULL THEN FromAddress
        ELSE EndpointId
    END,
    EventTypeId,
    MessageType";
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string EndpointId, string EventTypeId, string MessageType, long EventCount)>(
            sql,
            Window(from, to),
            commandTimeout: _ctx.CommandTimeout);
        var result = new EndpointMetricsResult();
        foreach (var r in rows)
        {
            var bucket = r.MessageType switch
            {
                "EventRequest" => result.Published,
                "ResolutionResponse" => result.Handled,
                "ErrorResponse" => result.Failed,
                _ => null,
            };
            bucket?.Add(new EndpointEventTypeCount { EndpointId = r.EndpointId, EventTypeId = r.EventTypeId, Count = (int)r.EventCount });
        }
        return result;
    }

    [Obsolete("Use GetEndpointLatencyMetrics(from, to). This overload will be removed in v5.")]
    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => GetEndpointLatencyMetrics(from, DateTime.UtcNow);

    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from, DateTime to)
    {
        // Aggregate COUNT/AVG/MIN/MAX server-side and GROUP BY (endpoint, eventType)
        // so the Resolver hot path never streams every outcome row into memory.
        // COUNT/AVG/MIN/MAX ignore NULLs, replacing the old client-side null filter.
        var sql = $@"
SELECT EndpointId,
       EventTypeId,
       COUNT(QueueTimeMs) AS QueueCount,
       AVG(CAST(QueueTimeMs AS FLOAT)) AS QueueAvg,
       MIN(QueueTimeMs) AS QueueMin,
       MAX(QueueTimeMs) AS QueueMax,
       COUNT(ProcessingTimeMs) AS ProcessingCount,
       AVG(CAST(ProcessingTimeMs AS FLOAT)) AS ProcessingAvg,
       MIN(ProcessingTimeMs) AS ProcessingMin,
       MAX(ProcessingTimeMs) AS ProcessingMax
FROM {T("Messages")}
WHERE EnqueuedTimeUtc >= @From
  AND EnqueuedTimeUtc < @To
  AND MessageType IN ('ResolutionResponse', 'ErrorResponse', 'SkipResponse', 'DeferralResponse', 'UnsupportedResponse')
  AND (QueueTimeMs IS NOT NULL OR ProcessingTimeMs IS NOT NULL)
GROUP BY EndpointId, EventTypeId";

        return GetEndpointLatencyMetricsCore(sql, from, to);
    }

    private async Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetricsCore(string sql, DateTime from, DateTime to)
    {
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string EndpointId, string EventTypeId,
            int QueueCount, double? QueueAvg, long? QueueMin, long? QueueMax,
            int ProcessingCount, double? ProcessingAvg, long? ProcessingMin, long? ProcessingMax)>(
            sql,
            Window(from, to),
            commandTimeout: _ctx.CommandTimeout);

        var latencies = rows
            .Select(r => new EndpointLatencyAggregate
            {
                EndpointId = r.EndpointId,
                EventTypeId = r.EventTypeId,
                Queue = BuildLatency(r.QueueCount, r.QueueAvg, r.QueueMin, r.QueueMax),
                Processing = BuildLatency(r.ProcessingCount, r.ProcessingAvg, r.ProcessingMin, r.ProcessingMax),
            })
            .ToList();

        return new EndpointLatencyMetricsResult { Latencies = latencies };
    }

    // A group whose column is entirely NULL yields COUNT = 0 with NULL avg/min/max;
    // collapse that to the zeroed aggregate the client-side path used to produce.
    private static LatencyAggregate BuildLatency(int count, double? avg, long? min, long? max)
        => count == 0
            ? new LatencyAggregate()
            : new LatencyAggregate
            {
                Count = count,
                AvgMs = avg ?? 0,
                MinMs = min ?? 0,
                MaxMs = max ?? 0,
            };

    [Obsolete("Use GetFailedMessageInsights(from, to). This overload will be removed in v5.")]
    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => GetFailedMessageInsights(from, DateTime.UtcNow);

    public async Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to)
    {
        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<FailedMessageInfo>(
            $@"SELECT
                   EndpointId,
                   EventTypeId,
                   COALESCE(NULLIF(JSON_VALUE(MessageContentJson, '$.ErrorContent.ErrorText'), ''), DeadLetterErrorDescription, '') AS ErrorText,
                   EnqueuedTimeUtc,
                   EventId,
                   MessageId,
                   SessionId
               FROM {T("Messages")}
               WHERE MessageType = 'ErrorResponse'
                 AND EnqueuedTimeUtc >= @From
                 AND EnqueuedTimeUtc < @To",
            Window(from, to), commandTimeout: _ctx.CommandTimeout);
        return rows.ToList();
    }

    [Obsolete("Use GetTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => GetTimeSeriesMetrics(from, DateTime.UtcNow, substringLength, bucketLabel);

    public async Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        // Floor to the bucket boundary server-side and GROUP BY, so we stop
        // streaming every message row. DATEADD(unit, DATEDIFF(unit, 0, ts), 0)
        // is version-agnostic (DATETRUNC is SQL Server 2022+). The unit is a
        // switch-constrained literal, never user input.
        var bucketUnit = substringLength switch
        {
            16 => "minute",
            13 => "hour",
            10 => "day",
            _ => "hour",
        };
        var bucketExpr = $"DATEADD({bucketUnit}, DATEDIFF({bucketUnit}, 0, EnqueuedTimeUtc), 0)";

        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string MessageType, DateTime Bucket, long Count)>(
            $@"SELECT MessageType, {bucketExpr} AS Bucket, COUNT_BIG(*) AS [Count]
               FROM {T("Messages")}
               WHERE EnqueuedTimeUtc >= @From
                 AND EnqueuedTimeUtc < @To
                 AND MessageType IN ('EventRequest', 'ResolutionResponse', 'ErrorResponse')
               GROUP BY MessageType, {bucketExpr}",
            Window(from, to),
            commandTimeout: _ctx.CommandTimeout);

        var buckets = GenerateBucketKeys(from, to, substringLength)
            .ToDictionary(k => k, k => new TimeSeriesBucket { Timestamp = k });

        foreach (var row in rows)
        {
            // The bucket start truncated to substringLength yields the same key
            // the per-row path produced (flooring == string truncation here).
            var key = DateTime.SpecifyKind(row.Bucket, DateTimeKind.Utc).ToString("o")[..substringLength];
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new TimeSeriesBucket { Timestamp = key };
                buckets[key] = bucket;
            }

            switch (row.MessageType)
            {
                case "EventRequest":
                    bucket.Published += (int)row.Count;
                    break;
                case "ResolutionResponse":
                    bucket.Handled += (int)row.Count;
                    break;
                case "ErrorResponse":
                    bucket.Failed += (int)row.Count;
                    break;
            }
        }

        return new TimeSeriesResult
        {
            BucketSize = bucketLabel,
            DataPoints = buckets.Values.OrderBy(b => b.Timestamp).ToList(),
        };
    }

    [Obsolete("Use GetEventTypeTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => GetEventTypeTimeSeriesMetrics(from, DateTime.UtcNow, substringLength, bucketLabel);

    public async Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        // Same version-agnostic flooring as GetTimeSeriesMetrics; the unit is a
        // switch-constrained literal, never user input. Buckets stay sparse (no
        // zero-fill) — the contract shared with the Cosmos backend.
        var bucketUnit = substringLength switch
        {
            16 => "minute",
            13 => "hour",
            10 => "day",
            _ => "hour",
        };
        var bucketExpr = $"DATEADD({bucketUnit}, DATEDIFF({bucketUnit}, 0, EnqueuedTimeUtc), 0)";

        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(string EventTypeId, DateTime Bucket, long Count)>(
            $@"SELECT EventTypeId, {bucketExpr} AS Bucket, COUNT_BIG(*) AS [Count]
               FROM {T("Messages")}
               WHERE EnqueuedTimeUtc >= @From
                 AND EnqueuedTimeUtc < @To
                 AND MessageType = 'EventRequest'
                 AND EventTypeId IS NOT NULL
               GROUP BY EventTypeId, {bucketExpr}",
            Window(from, to),
            commandTimeout: _ctx.CommandTimeout);

        var series = rows
            .Where(r => !string.IsNullOrEmpty(r.EventTypeId))
            .GroupBy(r => r.EventTypeId)
            .Select(g => new EventTypeSeriesEntry
            {
                EventTypeId = g.Key,
                Total = (int)g.Sum(r => r.Count),
                DataPoints = g
                    .OrderBy(r => r.Bucket)
                    .Select(r => new EventTypeSeriesBucket
                    {
                        // Same truncated-ISO key contract as GetTimeSeriesMetrics.
                        Timestamp = DateTime.SpecifyKind(r.Bucket, DateTimeKind.Utc).ToString("o")[..substringLength],
                        Published = (int)r.Count,
                    })
                    .ToList(),
            })
            .OrderByDescending(s => s.Total)
            .ToList();

        return new EventTypeTimeSeriesResult { BucketSize = bucketLabel, Series = series };
    }

    /// <summary>
    /// Queue and processing time per bucket over outcome messages (<c>IMonitorMetricsStore</c>).
    /// Same message types as <see cref="GetEndpointLatencyMetrics(DateTime, DateTime)"/>; buckets
    /// stay sparse because a zero would read as "instant".
    /// </summary>
    public async Task<LatencyTimeSeriesResult> GetLatencyTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        var bucketExpr = $"DATEADD({BucketUnit(substringLength)}, DATEDIFF({BucketUnit(substringLength)}, 0, EnqueuedTimeUtc), 0)";

        await using var conn = await _ctx.Open();
        var rows = await conn.QueryAsync<(DateTime Bucket,
            int QueueCount, double? QueueAvg, long? QueueMin, long? QueueMax,
            int ProcessingCount, double? ProcessingAvg, long? ProcessingMin, long? ProcessingMax)>(
            $@"SELECT {bucketExpr} AS Bucket,
                      COUNT(QueueTimeMs) AS QueueCount,
                      AVG(CAST(QueueTimeMs AS FLOAT)) AS QueueAvg,
                      MIN(QueueTimeMs) AS QueueMin,
                      MAX(QueueTimeMs) AS QueueMax,
                      COUNT(ProcessingTimeMs) AS ProcessingCount,
                      AVG(CAST(ProcessingTimeMs AS FLOAT)) AS ProcessingAvg,
                      MIN(ProcessingTimeMs) AS ProcessingMin,
                      MAX(ProcessingTimeMs) AS ProcessingMax
               FROM {T("Messages")}
               WHERE EnqueuedTimeUtc >= @From
                 AND EnqueuedTimeUtc < @To
                 AND MessageType IN ('ResolutionResponse', 'ErrorResponse', 'SkipResponse', 'DeferralResponse', 'UnsupportedResponse')
                 AND (QueueTimeMs IS NOT NULL OR ProcessingTimeMs IS NOT NULL)
               GROUP BY {bucketExpr}",
            Window(from, to),
            commandTimeout: _ctx.CommandTimeout);

        var dataPoints = rows
            .OrderBy(r => r.Bucket)
            .Select(r => new LatencyTimeSeriesBucket
            {
                Timestamp = DateTime.SpecifyKind(r.Bucket, DateTimeKind.Utc).ToString("o")[..substringLength],
                Queue = BuildLatency(r.QueueCount, r.QueueAvg, r.QueueMin, r.QueueMax),
                Processing = BuildLatency(r.ProcessingCount, r.ProcessingAvg, r.ProcessingMin, r.ProcessingMax),
            })
            .ToList();

        return new LatencyTimeSeriesResult { BucketSize = bucketLabel, DataPoints = dataPoints };
    }

    // A switch-constrained literal for DATEADD/DATEDIFF, never user input.
    private static string BucketUnit(int substringLength) => substringLength switch
    {
        16 => "minute",
        13 => "hour",
        10 => "day",
        _ => "hour",
    };

    private static List<string> GenerateBucketKeys(DateTime from, DateTime to, int substringLength)
    {
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

        var keys = new List<string>();
        while (current < to)
        {
            keys.Add(current.ToString("o")[..substringLength]);
            current += step;
        }

        return keys;
    }
}
