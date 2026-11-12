#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Resolver;

/// <summary>
/// Resolver write-path notifier (spec 020). After the Resolver persists a
/// state transition it calls <see cref="NotifyEndpointStateChangedAsync"/>;
/// this implementation POSTs the endpoint id to the management WebApp's
/// storage-hook webhook — the same route the Cosmos Change Feed → Event Grid
/// path uses (<c>api/storagehook/cosmos/{endpointId}</c>). The WebApp then
/// downloads the authoritative counts and broadcasts an <c>endpointupdate</c>
/// over GridEventsHub, so the live Flow / Monitor pages update for storage
/// providers that have no Change Feed (e.g. SQL Server).
///
/// Best-effort by design: a failed push only delays a visual update (the
/// pages reconcile via polling) and never affects message processing, so all
/// transport errors are swallowed. Registered only when a WebApp hook URL is
/// configured; otherwise the Resolver keeps the no-op notifier and existing
/// deployments are unaffected (spec NFR-004).
/// </summary>
public sealed partial class HttpEndpointStateChangeNotifier : IMessageStateChangeNotifier, IDisposable
{
    // Mirrors StorageHookImplementation.WebhookKeyHeaderName on the WebApp
    // side. Kept as a literal so the Resolver host need not reference the
    // WebApp project.
    private const string WebhookKeyHeaderName = "X-Webhook-Key";

    private readonly HttpClient _httpClient;
    private readonly string? _webhookKey;
    private readonly ILogger<HttpEndpointStateChangeNotifier> _logger;

    /// <param name="httpClient">
    /// Client whose <see cref="HttpClient.BaseAddress"/> is the WebApp origin;
    /// the notifier owns it and disposes it on shutdown.
    /// </param>
    /// <param name="webhookKey">
    /// Shared <c>EventGrid:WebhookKey</c>. Sent as <c>X-Webhook-Key</c> when
    /// non-empty; omitted otherwise (the WebApp allows anonymous storage-hook
    /// calls in Development).
    /// </param>
    public HttpEndpointStateChangeNotifier(
        HttpClient httpClient,
        string? webhookKey = null,
        ILogger<HttpEndpointStateChangeNotifier>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _webhookKey = string.IsNullOrEmpty(webhookKey) ? null : webhookKey;
        _logger = logger ?? NullLogger<HttpEndpointStateChangeNotifier>.Instance;
    }

    public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => PostStorageHookAsync("cosmos", endpointId, cancellationToken);

    /// <summary>
    /// Pushes an endpoint's heartbeat change to <c>api/storagehook/heartbeat/{endpointId}</c>
    /// so the WebApp broadcasts a <c>heartbeatupdate</c> to the Heartbeat page and Settings → Heartbeat probing.
    /// </summary>
    /// <param name="endpointId">The endpoint whose heartbeat state changed.</param>
    /// <param name="cancellationToken">Cancels the notification.</param>
    public Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => PostStorageHookAsync("heartbeat", endpointId, cancellationToken);

    /// <summary>
    /// Pushes a platform service's liveness change to
    /// <c>api/storagehook/servicehealth/{serviceId}</c>. Only the Resolver reports
    /// through this route today.
    /// </summary>
    /// <param name="serviceId">The platform service whose liveness changed.</param>
    /// <param name="cancellationToken">Cancels the notification.</param>
    public Task NotifyServiceHealthChangedAsync(string serviceId, CancellationToken cancellationToken = default)
        => PostStorageHookAsync("servicehealth", serviceId, cancellationToken);

    private async Task PostStorageHookAsync(string route, string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id))
            return;

        // Relative path resolves against HttpClient.BaseAddress (the WebApp origin).
        var requestUri = $"api/storagehook/{route}/{Uri.EscapeDataString(id)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            if (_webhookKey is not null)
                request.Headers.TryAddWithoutValidation(WebhookKeyHeaderName, _webhookKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFlowNotifierWebAppStorageHookReturned(_logger, route, (int)response.StatusCode, id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Non-fatal: realtime push is best-effort; the pages reconcile via polling.
            LogFlowNotifierFailedToPostUpdate(_logger, ex, route, id);
        }
    }

    public void Dispose() => _httpClient.Dispose();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Flow notifier: WebApp storage-hook {Route} returned {StatusCode} for {Id}.")]
    private static partial void LogFlowNotifierWebAppStorageHookReturned(ILogger logger, string route, int statusCode, string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Flow notifier: failed to post {Route} update for {Id}.")]
    private static partial void LogFlowNotifierFailedToPostUpdate(ILogger logger, Exception exception, string route, string id);
}
