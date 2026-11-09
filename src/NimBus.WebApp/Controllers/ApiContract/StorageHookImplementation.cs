using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NimBus.WebApp.Hubs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers;

public partial class StorageHookImplementation : IStorageHookApiController
{
    /// <summary>
    /// Header carrying the webhook key. Preferred over the legacy ?key=
    /// query parameter because query strings end up in proxy and gateway
    /// access logs; Event Grid subscriptions can send it via custom
    /// delivery headers.
    /// </summary>
    public const string WebhookKeyHeaderName = "X-Webhook-Key";

    private readonly IHubContext<GridEventsHub> _hubContext;
    private readonly ILogger<StorageHookImplementation> _logger;
    private readonly IMessageTrackingStore _messageStore;
    private readonly IPlatform _platform;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly string _webhookKey;

    public StorageHookImplementation(
        IHubContext<GridEventsHub> gridEventsHubContext,
        ILogger<StorageHookImplementation> logger,
        IMessageTrackingStore messageStore,
        IPlatform platform,
        IHttpContextAccessor httpContextAccessor,
        IHostEnvironment hostEnvironment,
        IConfiguration configuration)
    {
        _hubContext = gridEventsHubContext;
        _logger = logger;
        _messageStore = messageStore;
        _platform = platform;
        _httpContextAccessor = httpContextAccessor;
        _hostEnvironment = hostEnvironment;
        _webhookKey = configuration.GetValue<string>("EventGrid:WebhookKey") ?? string.Empty;
    }

    // Cosmos deployments reach this through Change Feed → Event Grid; SQL deployments through
    // the Resolver write-path notifier. The route keeps its historical "cosmos" segment so
    // existing Event Grid subscriptions keep working.
    public async Task<IActionResult> StoragehookReceiveAsync(string endpointId)
    {
        if (!ValidateWebhookKey())
            return new UnauthorizedResult();

        var endpointIdValid = EndpointVerificationService.EndpointExists(_platform, endpointId);
        if (endpointIdValid)
        {
            var state = await _messageStore.DownloadEndpointStateCount(endpointId);
            var endpointStatus = Mapper.EndpointStatusCountFromEndpointStateCount(state);
            await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.EndpointUpdate, endpointStatus);
            return new OkResult();
        }

        return new BadRequestResult();
    }

    /// <summary>
    /// Pushes a heartbeat change to connected operators. Called by the Resolver
    /// after it settles a probe, so the Health tab updates without polling.
    /// </summary>
    /// <param name="endpointId">The endpoint whose heartbeat state changed.</param>
    public async Task<IActionResult> PostStoragehookHeartbeatAsync(string endpointId)
    {
        if (!ValidateWebhookKey())
            return new UnauthorizedResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new BadRequestResult();

        await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.HeartbeatUpdate, endpointId);
        return new OkResult();
    }

    /// <summary>
    /// Pushes a platform-service liveness change to connected operators. Called
    /// by the Resolver after it answers a liveness probe.
    /// </summary>
    /// <remarks>
    /// Separate from the heartbeat hook because "Resolver" is not an endpoint,
    /// so that hook's catalog check would reject it. Only the Resolver is
    /// accepted — the route must not become an open broadcast channel.
    /// </remarks>
    /// <param name="serviceId">The platform service whose liveness changed.</param>
    public async Task<IActionResult> PostStoragehookServicehealthAsync(string serviceId)
    {
        if (!ValidateWebhookKey())
            return new UnauthorizedResult();

        if (!string.Equals(serviceId, NimBus.Core.Messages.Constants.ResolverId, StringComparison.OrdinalIgnoreCase))
            return new BadRequestResult();

        await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.ServiceHealthUpdate, serviceId);
        return new OkResult();
    }

    /// <summary>
    /// Validates the webhook key from the X-Webhook-Key header (preferred) or,
    /// for existing Event Grid subscriptions, the legacy ?key= query parameter.
    /// Keys are compared in fixed time so the comparison does not leak key
    /// bytes through response timing. In Development, missing config falls
    /// back to a warning + allow so a local dashboard can run without Event
    /// Grid wiring; in any other environment a missing key fails closed to
    /// avoid leaving an anonymous endpoint open.
    /// </summary>
    private bool ValidateWebhookKey()
    {
        if (string.IsNullOrEmpty(_webhookKey))
        {
            if (_hostEnvironment.IsDevelopment())
            {
                LogEventGridWebhookKeyIsNotConfigured(_logger);
                return true;
            }

            LogEventGridWebhookKeyIsNotConfiguredWebhook2(_logger);
            return false;
        }

        var request = _httpContextAccessor.HttpContext?.Request;
        var requestKey = request?.Headers[WebhookKeyHeaderName].ToString();
        if (string.IsNullOrEmpty(requestKey))
        {
            requestKey = request?.Query["key"].ToString();
        }

        if (string.IsNullOrEmpty(requestKey) || !FixedTimeEquals(requestKey, _webhookKey))
        {
            LogWebhookRequestRejectedInvalidOrMissing(_logger);
            return false;
        }

        return true;
    }

    private static bool FixedTimeEquals(string left, string right)
        => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left),
            System.Text.Encoding.UTF8.GetBytes(right));

    [LoggerMessage(Level = LogLevel.Warning, Message = "EventGrid:WebhookKey is not configured. Webhook endpoints are unprotected. Configure a webhook key before deploying.")]
    private static partial void LogEventGridWebhookKeyIsNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "EventGrid:WebhookKey is not configured. Webhook request rejected — set EventGrid:WebhookKey to enable storage hooks in non-Development environments.")]
    private static partial void LogEventGridWebhookKeyIsNotConfiguredWebhook2(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook request rejected: invalid or missing webhook key")]
    private static partial void LogWebhookRequestRejectedInvalidOrMissing(ILogger logger);
}
