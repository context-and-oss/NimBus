using Microsoft.ApplicationInsights.DataContracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services.ApplicationInsights;

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = null!;
    public SeverityLevel SeverityLevel { get; set; }
    public string? EventType { get; set; }
    public string? To { get; set; }
    public string? From { get; set; }
    public string? EventId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Payload { get; set; }
    public string? SessionId { get; set; }
    public string? MessageType { get; set; }
    public bool IsDeferred { get; set; }
    public string? MessageId { get; set; }
}
