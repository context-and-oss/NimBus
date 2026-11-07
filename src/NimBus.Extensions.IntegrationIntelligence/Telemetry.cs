using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Telemetry names for advisory failure classification.</summary>
public static class NimBusIntelligenceTelemetry
{
    /// <summary>Meter and source name.</summary>
    public const string Name = "NimBus.Intelligence";

    /// <summary>Activity source.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    /// <summary>Meter.</summary>
    public static readonly Meter Meter = new(Name);

    /// <summary>Request counter.</summary>
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("nimbus.intelligence.failure_classification.requests");

    /// <summary>Duration histogram in milliseconds.</summary>
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("nimbus.intelligence.failure_classification.duration", "ms");

    /// <summary>Error counter.</summary>
    public static readonly Counter<long> Errors = Meter.CreateCounter<long>("nimbus.intelligence.failure_classification.errors");

    /// <summary>Provider input-token counter.</summary>
    public static readonly Counter<long> InputTokens = Meter.CreateCounter<long>("nimbus.intelligence.failure_classification.input_tokens");

    /// <summary>Records one completed classification request without high-cardinality identifiers.</summary>
    public static void RecordRequest(
        long started,
        System.Diagnostics.Activity? activity,
        string outcome,
        string? provider,
        string? model,
        string? category,
        string? endpointId,
        string? eventTypeId,
        int? inputTokens = null)
    {
        var tags = new TagList();
        AddTag(ref tags, "nimbus.intelligence.provider", provider);
        AddTag(ref tags, "nimbus.intelligence.model", model);
        AddTag(ref tags, "nimbus.intelligence.outcome", outcome);
        AddTag(ref tags, "nimbus.intelligence.category", category);
        AddTag(ref tags, "nimbus.endpoint", endpointId);
        AddTag(ref tags, "nimbus.event_type", eventTypeId);

        activity?.SetTag("nimbus.intelligence.provider", provider);
        activity?.SetTag("nimbus.intelligence.model", model);
        activity?.SetTag("nimbus.intelligence.outcome", outcome);
        activity?.SetTag("nimbus.intelligence.category", category);
        activity?.SetTag("nimbus.endpoint", endpointId);
        activity?.SetTag("nimbus.event_type", eventTypeId);

        Requests.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (outcome is not "ok" and not "cached") Errors.Add(1, tags);
        if (inputTokens is { } tokens) InputTokens.Add(tokens, tags);
    }

    /// <summary>Bulk classification reads by outcome (available, unavailable, unsupported).</summary>
    public static readonly Counter<long> BulkReads = Meter.CreateCounter<long>("nimbus.intelligence.insights.bulk_reads");

    /// <summary>Distinct failure ids requested per bulk read.</summary>
    public static readonly Histogram<long> BulkReadFailures = Meter.CreateHistogram<long>("nimbus.intelligence.insights.bulk_read_failures");

    /// <summary>Bulk read duration in milliseconds.</summary>
    public static readonly Histogram<double> BulkReadDuration = Meter.CreateHistogram<double>("nimbus.intelligence.insights.bulk_read_duration", "ms");

    /// <summary>Insights responses by enrichment status (available, disabled, unavailable, unsupported, limitExceeded).</summary>
    public static readonly Counter<long> InsightsEnrichment = Meter.CreateCounter<long>("nimbus.intelligence.insights.enrichment");

    /// <summary>Saved classifications whose endpoint or event did not match their failure occurrence.</summary>
    public static readonly Counter<long> InsightsIdentityMismatches = Meter.CreateCounter<long>("nimbus.intelligence.insights.identity_mismatches");

    /// <summary>Duplicate failure identities collapsed from a metrics projection.</summary>
    public static readonly Counter<long> InsightsDuplicateIdentities = Meter.CreateCounter<long>("nimbus.intelligence.insights.duplicate_identities");

    /// <summary>Records one bulk read without identifiers.</summary>
    internal static void RecordBulkRead(string outcome, int failures, TimeSpan? elapsed)
    {
        var tags = new TagList { { "nimbus.intelligence.outcome", outcome } };
        BulkReads.Add(1, tags);
        if (failures > 0) BulkReadFailures.Record(failures, tags);
        if (elapsed is { } duration) BulkReadDuration.Record(duration.TotalMilliseconds, tags);
    }

    private static void AddTag(ref TagList tags, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) tags.Add(name, value);
    }
}
