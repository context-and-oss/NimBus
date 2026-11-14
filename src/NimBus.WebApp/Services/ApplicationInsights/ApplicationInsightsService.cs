using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web;

namespace NimBus.WebApp.Services.ApplicationInsights;

public class ApplicationInsightsService : IApplicationInsightsService
{
    // HttpClient is owned by IHttpClientFactory — the typed-client
    // registration in Startup.cs sets BaseAddress and adds the Entra
    // bearer-token handler. Don't dispose it here; the factory recycles
    // handlers for us.
    private readonly HttpClient client;

    public ApplicationInsightsService(HttpClient client)
    {
        this.client = client;
    }

    public async Task<IEnumerable<LogEntry>> GetLogs(string messageId, SeverityLevel minimumLevel)
    {
        return await GetLogs(new Filter()
        {
            EventId = messageId,
            MinimumLogLevel = minimumLevel,
        });
    }

    public async Task<IEnumerable<LogEntry>> GetLogs(Filter filter)
    {
        var query = "traces " +
                " | where itemType == 'trace' " +
                (filter.Before == null ? "" : $"and timestamp <= datetime({filter.Before.Value.ToString("u")}) ") +
                (filter.After == null ? "" : $"and timestamp >= datetime({filter.After.Value.ToString("u")}) ") +
                //(filter.LogSource == null ? "" : $"and tostring(customDimensions['LogSource']) == '{filter.LogSource}' ") +
                //(filter.EventType == null ? "" : $"and tostring(customDimensions['EventType']) == '{filter.EventType}' ") +
                //(filter.CorrelationId == null ? "" : $"and tostring(customDimensions['CorrelationId']) == '{filter.CorrelationId}' ") +
                (string.IsNullOrEmpty(filter.EventId) ? "" : $"and tostring(customDimensions['NimBus.EventId']) == {KqlStringLiteral.Format(filter.EventId)} ") +
                //(filter.PublishedBy == null ? "" : $"and tostring(customDimensions['PublishedBy']) == '{filter.PublishedBy.ToString()}' ") +
                (filter.MinimumLogLevel == null ? "" : $"and severityLevel >= {(int)filter.MinimumLogLevel} ") +
                " | top 1000 by timestamp desc";
        var req = $"query?query={HttpUtility.UrlEncode(query)}";

        using var response = await client.GetAsync(req);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadAsStringAsync();
        var obj = JsonConvert.DeserializeObject<AppInsightsResultRaw>(result)
            ?? throw new InvalidOperationException("Application Insights returned an empty response.");

        return new LogTraceCollection(obj).GetLogEntries();
    }

    public async Task<IEnumerable<LatencyMetric>> GetLatencyMetrics(TimeSpan period)
    {
        var periodKql = period.TotalHours switch
        {
            <= 1 => "1h",
            <= 12 => "12h",
            <= 24 => "1d",
            <= 72 => "3d",
            <= 168 => "7d",
            _ => "30d"
        };

        // One union query covering all three histograms — keeps per-row tags
        // (destination, eventType) aligned across queue / processing / e2e
        // and lets KQL compute percentiles for each in a single round-trip.
        var query =
            "customMetrics" +
            $" | where name in ('nimbus.message.queue_wait', 'nimbus.message.process.duration', 'nimbus.message.e2e_latency')" +
            $" | where timestamp >= ago({periodKql})" +
            " | extend eventType = tostring(customDimensions['nimbus.event_type'])," +
            "          destination = tostring(customDimensions['messaging.destination.name'])" +
            " | summarize" +
            "     count_ = count()," +
            "     avg_ = avg(value)," +
            "     p50 = percentile(value, 50)," +
            "     p95 = percentile(value, 95)," +
            "     p99 = percentile(value, 99)," +
            "     max_ = max(value)" +
            "   by name, destination, eventType" +
            " | order by destination asc, eventType asc, name asc";

        var req = $"query?query={HttpUtility.UrlEncode(query)}";
        using var response = await client.GetAsync(req);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync();
        var obj = JsonConvert.DeserializeObject<AppInsightsResultRaw>(result);

        var table = obj?.Tables?.FirstOrDefault();
        if (table == null) return new List<LatencyMetric>();

        var colMap = new Dictionary<string, int>();
        for (var i = 0; i < table.Columns.Length; i++)
            colMap[table.Columns[i].Name] = i;

        // Group the three histogram rows back into a single LatencyMetric per
        // (destination, eventType). Missing histograms (e.g. processing time
        // not recorded for a flow that aborts before the pipeline) leave the
        // corresponding stats at zero/null.
        var grouped = new Dictionary<(string Destination, string EventType), LatencyMetric>();

        foreach (var row in table.Rows)
        {
            var name = colMap.TryGetValue("name", out var nameIdx) ? row[nameIdx] : "";
            var destination = colMap.TryGetValue("destination", out var destinationIdx) ? row[destinationIdx] : "";
            var eventType = colMap.TryGetValue("eventType", out var eventTypeIdx) ? row[eventTypeIdx] : "";

            var key = (destination, eventType);
            if (!grouped.TryGetValue(key, out var metric))
            {
                metric = new LatencyMetric { Destination = destination, EventType = eventType };
                grouped[key] = metric;
            }

            var stats = ParseStats(row, colMap);
            switch (name)
            {
                case "nimbus.message.queue_wait":
                    metric.Queue = stats;
                    break;
                case "nimbus.message.process.duration":
                    metric.Processing = stats;
                    break;
                case "nimbus.message.e2e_latency":
                    metric.E2E = stats;
                    break;
            }
        }

        return grouped.Values.ToList();
    }

    private static LatencyStats ParseStats(string[] row, Dictionary<string, int> colMap)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new LatencyStats
        {
            Count = colMap.TryGetValue("count_", out var countIdx) && int.TryParse(row[countIdx], out var c) ? c : 0,
            AvgMs = colMap.TryGetValue("avg_", out var avgIdx) && double.TryParse(row[avgIdx], System.Globalization.NumberStyles.Any, inv, out var a) ? a : 0,
            P50Ms = colMap.TryGetValue("p50", out var p50Idx) && double.TryParse(row[p50Idx], System.Globalization.NumberStyles.Any, inv, out var p50) ? p50 : 0,
            P95Ms = colMap.TryGetValue("p95", out var p95Idx) && double.TryParse(row[p95Idx], System.Globalization.NumberStyles.Any, inv, out var p95) ? p95 : 0,
            P99Ms = colMap.TryGetValue("p99", out var p99Idx) && double.TryParse(row[p99Idx], System.Globalization.NumberStyles.Any, inv, out var p99) ? p99 : 0,
            MaxMs = colMap.TryGetValue("max_", out var maxIdx) && double.TryParse(row[maxIdx], System.Globalization.NumberStyles.Any, inv, out var m) ? m : 0,
        };
    }
}

public interface IApplicationInsightsService
{
    Task<IEnumerable<LogEntry>> GetLogs(string messageId, SeverityLevel minimumLevel = SeverityLevel.Information);
    Task<IEnumerable<LogEntry>> GetLogs(Filter filter);
    Task<IEnumerable<LatencyMetric>> GetLatencyMetrics(TimeSpan period);
}

public class LatencyMetric
{
    public string Destination { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public LatencyStats? Queue { get; set; }
    public LatencyStats? Processing { get; set; }
    public LatencyStats? E2E { get; set; }
}

public class LatencyStats
{
    public int Count { get; set; }
    public double AvgMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double MaxMs { get; set; }
}
