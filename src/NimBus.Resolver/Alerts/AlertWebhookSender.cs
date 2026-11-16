using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using NimBus.MessageStore.States;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// Posts an endpoint alert to a subscription's webhook. The body carries ids only: the endpoint,
/// the event, its session and type, the status and the time. A receiver that wants the error or
/// the payload reads them through the operator MCP endpoint or the REST API, under its own role.
/// </summary>
/// <remarks>
/// Every request is signed with the subscription's secret (<see cref="EndpointAlertWebhook"/>).
/// A request to a URL under one of <see cref="AlertWebhookOptions.BearerTokens"/> also carries a
/// token for the Resolver's identity. Delivery is one attempt: no retry, and a redirect is a
/// failure. The receiver is expected to sweep on a timer as well.
/// </remarks>
public sealed class AlertWebhookSender : IAlertWebhookSender, IDisposable
{
    private static readonly TimeSpan TokenRefreshMargin = TimeSpan.FromMinutes(5);

    private readonly AlertWebhookOptions _options;
    private readonly HttpClient _httpClient;
    private readonly TokenCredential? _credential;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, AccessToken> _tokens = new(StringComparer.Ordinal);

    /// <summary>Creates a sender.</summary>
    /// <param name="options">The webhook settings; they must have a signing key.</param>
    /// <param name="httpClient">The client to send with, usually from <see cref="CreateHttpClient"/>. The sender owns it.</param>
    /// <param name="credential">Acquires the bearer tokens; required only when a token prefix is listed.</param>
    /// <param name="timeProvider">The clock the requests are stamped with.</param>
    public AlertWebhookSender(AlertWebhookOptions options, HttpClient httpClient, TokenCredential? credential, TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credential = credential;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (!options.IsConfigured)
        {
            throw new ArgumentException($"{AlertWebhookOptions.SectionName}:SigningKey is not set.", nameof(options));
        }
    }

    /// <summary>
    /// Creates the client alert webhooks are sent with: redirects are not followed, and unless
    /// <see cref="AlertWebhookOptions.AllowPrivateNetworks"/> is set it connects directly, to
    /// public addresses only.
    /// </summary>
    /// <param name="options">The webhook settings.</param>
    public static HttpClient CreateHttpClient(AlertWebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        if (!options.AllowPrivateNetworks)
        {
            // A proxy would be the address the guard sees, so the guard only holds for direct
            // connections.
            handler.UseProxy = false;
            handler.ConnectCallback = AlertWebhookConnectGuard.ConnectAsync;
        }

        // The per-delivery timeout is applied in SendAsync, where it can be told from the host
        // shutting down.
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <inheritdoc />
    public bool CanSendTo(string? url) => EndpointAlertWebhook.IsValidUrl(url, _options.AllowInsecureHttp);

    /// <inheritdoc />
    public async Task SendAsync(AlertWebhook webhook, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webhook);

        var body = BuildBody(webhook.Context);
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var secret = EndpointAlertWebhook.DeriveSigningSecret(_options.SigningKey!, webhook.SubscriptionId);

        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.ParseAdd("NimBus-Alerts");
        request.Headers.Add(EndpointAlertWebhook.SubscriptionHeader, webhook.SubscriptionId);
        request.Headers.Add(EndpointAlertWebhook.DeliveryHeader, Guid.NewGuid().ToString());
        request.Headers.Add(EndpointAlertWebhook.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(EndpointAlertWebhook.SignatureHeader, EndpointAlertWebhook.Sign(secret, timestamp, body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            if (_options.BearerTokens.FirstOrDefault(entry => entry.Covers(webhook.Url)) is { } entry)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", await GetTokenAsync(entry.Audience!, timeout.Token).ConfigureAwait(false));
            }

            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AlertWebhookDeliveryException(
                    $"The webhook at {webhook.Url.Host} answered {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The webhook at {webhook.Url.Host} did not answer within {_options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    private static string BuildBody(EndpointAlertContext context)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("endpointId", context.EndpointId);
            writer.WriteString("eventId", context.EventId);
            writer.WriteString("sessionId", context.SessionId);
            writer.WriteString("eventTypeId", context.EventTypeId);
            writer.WriteString("status", context.Status.ToString());
            writer.WriteString(
                "occurredAtUtc",
                context.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private async Task<string> GetTokenAsync(string audience, CancellationToken cancellationToken)
    {
        if (_tokens.TryGetValue(audience, out var cached)
            && cached.ExpiresOn - TokenRefreshMargin > _timeProvider.GetUtcNow())
        {
            return cached.Token;
        }

        if (_credential is null)
        {
            throw new InvalidOperationException(
                $"{AlertWebhookOptions.SectionName}:BearerTokens lists an audience, but the Resolver has no credential to get a token with.");
        }

        var token = await _credential
            .GetTokenAsync(new TokenRequestContext([audience.TrimEnd('/') + "/.default"]), cancellationToken)
            .ConfigureAwait(false);
        _tokens[audience] = token;
        return token.Token;
    }
}
