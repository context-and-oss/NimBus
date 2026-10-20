using Microsoft.ApplicationInsights.DataContracts;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services.ApplicationInsights;

public class Filter
{
    public string? EventId { get; set; }
    public string? CorrelationId { get; set; }
    public Source? LogSource { get; set; }
    public Source? PublishedBy { get; set; }
    public string? EventType { get; set; }
    public DateTime? After { get; set; }
    public DateTime? Before { get; set; }
    public SeverityLevel? MinimumLogLevel { get; set; }
}
