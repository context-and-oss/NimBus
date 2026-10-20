using Microsoft.ApplicationInsights.DataContracts;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services.ApplicationInsights;


public class LogTraceCollection : List<LogTrace>
{
    // public AppInsightsResultRaw AppInsightsResultRaw { get; }

    private Dictionary<string, int> columnIndexes = new Dictionary<string, int>()
    {
        { "timestamp", -1 },
        { "message", -1 },
        { "severityLevel", -1 },
        { "customDimensions", -1 },
    };

    public LogTraceCollection(AppInsightsResultRaw appInsightsResultRaw)
    {
        var table = appInsightsResultRaw.Tables?.FirstOrDefault();
        if (table == null)
        {
            throw new InvalidOperationException("No tables found in Application Insights response.");
        }

        var columns = table.Columns;
        for (var i = 0; i < columns.Length; i++)
        {
            if (columnIndexes.ContainsKey(columns[i].Name))
            {
                columnIndexes[columns[i].Name] = i;
            }
        }

        if (columnIndexes.Any(columnIndex => columnIndex.Value == -1))
            throw new InvalidOperationException("Not all expected columns were found in response.");

        foreach (var row in table.Rows)
        {
            var logTrace = new LogTrace();
            logTrace.Timestamp = DateTime.Parse(row[columnIndexes["timestamp"]]);
            logTrace.Text = row[columnIndexes["message"]];
            logTrace.SeverityLevel = int.Parse(row[columnIndexes["severityLevel"]]);
            logTrace.CustomDimensions = JsonConvert.DeserializeObject<CustomDimensions>(row[columnIndexes["customDimensions"]])!;
            this.Add(logTrace);
        }
    }

    internal IEnumerable<LogEntry> GetLogEntries()
    {
        foreach (var logTrace in this)
        {
            yield return new LogEntry()
            {
                Timestamp = logTrace.Timestamp,
                Text = logTrace.Text,
                SeverityLevel = (SeverityLevel)logTrace.SeverityLevel,
                EventId = logTrace.CustomDimensions.EventId,
                CorrelationId = logTrace.CustomDimensions.CorrelationId,
                EventType = logTrace.CustomDimensions.EventType,
                Payload = logTrace.CustomDimensions.EventJson,
                To = logTrace.CustomDimensions.To,
                From = logTrace.CustomDimensions.From,
                SessionId = logTrace.CustomDimensions.SessionId,
                MessageType = logTrace.CustomDimensions.MessageType,
                IsDeferred = logTrace.CustomDimensions.IsDeferred,
                MessageId = logTrace.CustomDimensions.MessageId
            };
        }
    }
}

public enum Source
{
    Other = 0,
    IntegrationService,
    ErrorService,
    NavisionPublisher,
    NavisionSubscriber,
    FieldServicePublisher,
    FieldServiceSubscriber,
    TestPublisher,
    TestSubscriber,
    MigrationFieldServicePublisher,
    TricomPublisher,
    TricomSubscriber,
    TracetoolPublisher,
    TracetoolSubscriber,
    AdPublisher,
    FieldServiceAdSubscriber,
    PSAAdSubscriber,
}

public class LogTrace
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = null!;
    public int SeverityLevel { get; set; }
    public CustomDimensions CustomDimensions { get; set; } = null!;
}

public class CustomDimensions
{
    [JsonProperty("NimBus.EventId")]
    public string? EventId { get; set; }
    [JsonProperty("NimBus.CorrelationId")]
    public string? CorrelationId { get; set; }
    public string? PublisherName { get; set; }
    public string? PublishedBy { get; set; }
    public string? LogSource { get; set; }
    [JsonProperty("NimBus.EventTypeId")]
    public string? EventType { get; set; }
    public string? Payload { get; set; }
    [JsonProperty("NimBus.From")]
    public string? From { get; set; }
    [JsonProperty("NimBus.To")]
    public string? To { get; set; }
    [JsonProperty("NimBus.SessionId")]
    public string? SessionId { get; set; }
    [JsonProperty("NimBus.MessageType")]
    public string? MessageType { get; set; }
    [JsonProperty("NimBus.IsDeferred")]
    public bool IsDeferred { get; set; }
    [JsonProperty("NimBus.EventJson")]
    public string? EventJson { get; set; }
    [JsonProperty("NimBus.MessageId")]
    public string? MessageId { get; set; }
}

public class AppInsightsResultRaw
{
    public IEnumerable<Table>? Tables { get; set; }


}

public class Table
{
    public Column[] Columns { get; set; } = null!;
    public IEnumerable<string[]> Rows { get; set; } = null!;
}

public class Column
{
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
}
