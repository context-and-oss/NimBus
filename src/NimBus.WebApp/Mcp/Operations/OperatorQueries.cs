using Microsoft.AspNetCore.Mvc;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services.Insights;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>
/// Read operations for the operator tools, served in-process by the same implementations as
/// the REST API. Endpoint Reader checks, PII redaction and audit rows therefore behave exactly
/// as they do for the Web UI. This class is the seam: tools depend on it rather than on the
/// controllers, so shared logic can later move behind it without changing tool contracts.
/// </summary>
public sealed class OperatorQueries
{
    private readonly IEndpointApiController _endpoints;
    private readonly IEventApiController _events;
    private readonly IMonitorApiController _monitor;
    private readonly IMessageApiController _messages;
    private readonly IMetricsApiController _metrics;
    private readonly IFailedInsightsQuery _failedInsights;

    /// <summary>Creates the queries for one request.</summary>
    public OperatorQueries(
        IEndpointApiController endpoints,
        IEventApiController events,
        IMonitorApiController monitor,
        IMessageApiController messages,
        IMetricsApiController metrics,
        IFailedInsightsQuery failedInsights)
    {
        _endpoints = endpoints;
        _events = events;
        _monitor = monitor;
        _messages = messages;
        _metrics = metrics;
        _failedInsights = failedInsights;
    }

    /// <summary>Status counts for every endpoint the caller can read.</summary>
    public async Task<IReadOnlyList<EndpointStatusCount>> GetStatusCountsAsync()
        => Unwrap(await _endpoints.GetEndpointStatusCountAllAsync().ConfigureAwait(false),
            () => OperatorToolErrors.SourceUnavailable("Endpoint status counts are unavailable.")).ToList();

    /// <summary>Status counts for one endpoint.</summary>
    public async Task<EndpointStatusCount> GetStatusCountAsync(string endpointId)
        => Unwrap(await _endpoints.GetEndpointStatusCountIdAsync(endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.EndpointNotFound(endpointId));

    /// <summary>Active Monitor acknowledgements on endpoints the caller can read.</summary>
    public async Task<IReadOnlyList<MonitorAcknowledgement>> GetAcknowledgementsAsync()
        => Unwrap(await _monitor.GetMonitorAcknowledgementsAsync().ConfigureAwait(false),
            () => OperatorToolErrors.SourceUnavailable("Monitor acknowledgements are unavailable.")).ToList();

    /// <summary>One page of tracked messages on an endpoint matching <paramref name="filter"/>.</summary>
    /// <param name="endpointId">Canonical endpoint id.</param>
    /// <param name="filter">Typed filter.</param>
    /// <param name="limit">Page size.</param>
    /// <param name="continuationToken">Store continuation token, or null for the first page.</param>
    /// <param name="notFound">Error for a refused search; defaults to <see cref="OperatorToolErrors.EndpointNotFound"/>.</param>
    public async Task<SearchResponse> SearchAsync(string endpointId, EventFilter filter, int limit, string? continuationToken, Func<Exception>? notFound = null)
    {
        var request = new SearchRequest { EventFilter = filter, MaxSearchItemsCount = limit, ContinuationToken = continuationToken };
        return Unwrap(await _events.PostApiEventEndpointIdGetByFilterAsync(request, endpointId).ConfigureAwait(false),
            notFound ?? (() => OperatorToolErrors.EndpointNotFound(endpointId)));
    }

    /// <summary>The failed and originating messages of one event.</summary>
    public async Task<EventDetails> GetEventDetailsAsync(string endpointId, string eventId)
        => Unwrap(await _events.GetEventDetailsIdAsync(eventId, endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, eventId));

    /// <summary>Processing attempts of one event on an endpoint, newest first.</summary>
    public async Task<IReadOnlyList<Message>> GetEventHistoryAsync(string endpointId, string eventId)
        => Unwrap(await _events.GetEventDetailsHistoryIdAsync(eventId, endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, eventId)).ToList();

    /// <summary>Processing log entries of one event on an endpoint.</summary>
    public async Task<IReadOnlyList<EventLogEntry>> GetEventLogsAsync(string endpointId, string eventId)
        => Unwrap(await _events.GetEventDetailsLogsIdAsync(eventId, endpointId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, eventId)).ToList();

    /// <summary>Pending and deferred events of one session on an endpoint.</summary>
    public async Task<SessionStatus> GetSessionAsync(string endpointId, string sessionId)
        => Unwrap(await _endpoints.GetEndpointSessionIdAsync(endpointId, sessionId).ConfigureAwait(false),
            () => OperatorToolErrors.MessageNotFound(endpointId, sessionId));

    /// <summary>One page of processing messages across all endpoints. Requires site Reader.</summary>
    public async Task<MessageSearchResponse> SearchMessagesAsync(MessageSearchFilter filter, int limit, string? continuationToken)
    {
        var request = new MessageSearchRequest { Filter = filter, MaxItemCount = limit, ContinuationToken = continuationToken };
        return Unwrap(await _messages.PostMessagesSearchAsync(request).ConfigureAwait(false), SiteReaderRequired);
    }

    /// <summary>Published, handled and failed counts per endpoint and event type. Requires site Reader.</summary>
    public async Task<MetricsOverview> GetThroughputAsync(Period period)
        => Unwrap(await _metrics.GetMetricsOverviewAsync(period, null, null).ConfigureAwait(false), SiteReaderRequired);

    /// <summary>Queue and processing latency per endpoint and event type. Requires site Reader.</summary>
    public async Task<LatencyOverview> GetLatencyAsync(Period period)
        => Unwrap(await _metrics.GetMetricsLatencyAsync(period, null, null).ConfigureAwait(false), SiteReaderRequired);

    /// <summary>
    /// Failures grouped by error pattern. Requires site Reader. Built without classification
    /// enrichment (spec 043 §8.4): the tool hides endpoints per caller, and coverage and cause
    /// totals cannot be split per endpoint, so the MCP path never reads classification storage.
    /// </summary>
    public async Task<FailedInsightsOverview> GetFailureInsightsAsync(Period period)
    {
        var result = await _failedInsights.GetOverviewAsync(
            new FailedInsightsRequest(period, null, null, null, Enrich: false, Refresh: false)).ConfigureAwait(false);
        return result.Status == FailedInsightsQueryStatus.Ok ? result.Overview! : throw SiteReaderRequired();
    }

    // Cross-endpoint reads keep the REST site-Reader floor; a refusal is about the caller's
    // role, not about a resource, so it is reported as such.
    private static Exception SiteReaderRequired()
        => OperatorToolErrors.PermissionDenied("Cross-endpoint search and metrics require a site-wide Reader role.");

    // Success returns the value. A 400 is surfaced as an invalid argument; every other
    // failure, including 403, becomes the caller's not-found error, so a tool never reveals
    // whether a resource the caller cannot read exists.
    private static T Unwrap<T>(ActionResult<T> result, Func<Exception> notFound)
    {
        if (result.Value is not null)
            return result.Value;

        return result.Result switch
        {
            ObjectResult { StatusCode: null or >= 200 and < 300, Value: T value } => value,
            BadRequestObjectResult bad => throw OperatorToolErrors.InvalidArgument(bad.Value?.ToString() ?? "The request was rejected."),
            BadRequestResult => throw OperatorToolErrors.InvalidArgument("The request was rejected."),
            _ => throw notFound(),
        };
    }
}
