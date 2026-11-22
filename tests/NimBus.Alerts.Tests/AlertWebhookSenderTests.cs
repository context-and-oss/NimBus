#pragma warning disable CA1707, CA1515, CA2007
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.Alerts;

namespace NimBus.Alerts.Tests;

/// <summary>
/// The request an alert webhook receives: ids only, signed with the subscription's derived
/// secret, with a bearer token only under a URL prefix an administrator listed.
/// </summary>
[TestClass]
public class AlertWebhookSenderTests
{
    private const string SigningKey = "0123456789abcdef0123456789abcdef";
    private const string SubscriptionId = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static AlertWebhookOptions Options(Action<AlertWebhookOptions>? configure = null)
    {
        var options = new AlertWebhookOptions { SigningKey = SigningKey };
        configure?.Invoke(options);
        return options;
    }

    private static AlertWebhookSender CreateSender(
        CapturingHandler handler, AlertWebhookOptions? options = null, TokenCredential? credential = null)
        => new(
            options ?? Options(),
            new HttpClient(handler),
            credential,
            new ManualTimeProvider(Now));

    private static AlertWebhook Webhook(string url = "https://agent.example.com/hooks/nimbus") => new(
        SubscriptionId,
        new Uri(url),
        new EndpointAlertContext
        {
            EndpointId = "ErpEndpoint",
            EventId = "event-1",
            EventTypeId = "CrmAccountCreated",
            SessionId = "session-1",
            Status = ResolutionStatus.DeadLettered,
            ErrorText = "Customer 4711 has no billing address",
            OccurredAtUtc = new DateTimeOffset(2026, 11, 16, 11, 59, 30, 250, TimeSpan.Zero),
        });

    [TestMethod]
    public async Task The_request_is_a_json_post_to_the_subscription_url()
    {
        var handler = new CapturingHandler();

        await CreateSender(handler).SendAsync(Webhook());

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("https://agent.example.com/hooks/nimbus", handler.RequestUri!.AbsoluteUri);
        Assert.AreEqual("application/json", handler.ContentType);
    }

    [TestMethod]
    public async Task The_body_carries_ids_and_no_error_text()
    {
        var handler = new CapturingHandler();

        await CreateSender(handler).SendAsync(Webhook());

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.AreEqual(1, root.GetProperty("version").GetInt32());
        Assert.AreEqual("ErpEndpoint", root.GetProperty("endpointId").GetString());
        Assert.AreEqual("event-1", root.GetProperty("eventId").GetString());
        Assert.AreEqual("session-1", root.GetProperty("sessionId").GetString());
        Assert.AreEqual("CrmAccountCreated", root.GetProperty("eventTypeId").GetString());
        Assert.AreEqual("DeadLettered", root.GetProperty("status").GetString());
        Assert.AreEqual("2026-11-16T11:59:30.250Z", root.GetProperty("occurredAtUtc").GetString());
        Assert.AreEqual(7, root.EnumerateObject().Count(), "nothing else is sent");
        Assert.IsFalse(handler.Body!.Contains("billing address", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task The_signature_verifies_with_the_subscriptions_derived_secret()
    {
        var handler = new CapturingHandler();

        await CreateSender(handler).SendAsync(Webhook());

        var timestamp = handler.Header(EndpointAlertWebhook.TimestampHeader);
        Assert.AreEqual(Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), timestamp);
        var secret = EndpointAlertWebhook.DeriveSigningSecret(SigningKey, SubscriptionId);
        Assert.AreEqual(
            EndpointAlertWebhook.Sign(secret, Now.ToUnixTimeSeconds(), handler.Body!),
            handler.Header(EndpointAlertWebhook.SignatureHeader));
    }

    [TestMethod]
    public async Task The_request_names_its_subscription_and_a_new_delivery_id()
    {
        var handler = new CapturingHandler();
        var sender = CreateSender(handler);

        await sender.SendAsync(Webhook());
        var first = handler.Header(EndpointAlertWebhook.DeliveryHeader);
        await sender.SendAsync(Webhook());

        Assert.AreEqual(SubscriptionId, handler.Header(EndpointAlertWebhook.SubscriptionHeader));
        Assert.IsTrue(Guid.TryParse(first, out _));
        Assert.AreNotEqual(first, handler.Header(EndpointAlertWebhook.DeliveryHeader));
    }

    [TestMethod]
    public async Task Without_a_listed_prefix_no_token_is_sent()
    {
        var handler = new CapturingHandler();
        var credential = new RecordingCredential();

        await CreateSender(handler, credential: credential).SendAsync(Webhook());

        Assert.IsNull(handler.Authorization);
        Assert.AreEqual(0, credential.Scopes.Count);
    }

    [TestMethod]
    [DataRow("https://agent.example.com/api/triggers/abc")]
    [DataRow("https://AGENT.example.com/api/triggers/abc?x=1")]
    [DataRow("https://agent.example.com/api/triggers")]
    public async Task A_url_under_a_listed_prefix_gets_a_token_for_its_audience(string url)
    {
        var handler = new CapturingHandler();
        var credential = new RecordingCredential();
        var options = Options(o => o.BearerTokens.Add(new AlertWebhookBearerToken
        {
            UrlPrefix = "https://agent.example.com/api/triggers",
            Audience = "https://azuresre.dev",
        }));

        await CreateSender(handler, options, credential).SendAsync(Webhook(url));

        Assert.AreEqual("Bearer token-for-https://azuresre.dev/.default", handler.Authorization);
        Assert.IsNotNull(handler.Header(EndpointAlertWebhook.SignatureHeader), "the signature is still sent");
    }

    [TestMethod]
    [DataRow("https://agent.example.com/api/triggersevil/abc", DisplayName = "a longer path segment")]
    [DataRow("https://agent.example.com/other", DisplayName = "another path")]
    [DataRow("https://agent.example.com:8443/api/triggers/abc", DisplayName = "another port")]
    [DataRow("https://evil.example.com/api/triggers/abc", DisplayName = "another host")]
    [DataRow("https://agent.example.com.evil.example/api/triggers/abc", DisplayName = "a host that only starts the same")]
    [DataRow("https://agent.example.com/api/triggers/%2e%2e/admin", DisplayName = "an encoded dot segment")]
    [DataRow("https://agent.example.com/api/triggers/..%2fadmin", DisplayName = "an encoded slash")]
    public async Task A_url_outside_the_listed_prefix_gets_no_token(string url)
    {
        var handler = new CapturingHandler();
        var credential = new RecordingCredential();
        var options = Options(o => o.BearerTokens.Add(new AlertWebhookBearerToken
        {
            UrlPrefix = "https://agent.example.com/api/triggers",
            Audience = "https://azuresre.dev",
        }));

        await CreateSender(handler, options, credential).SendAsync(Webhook(url));

        Assert.IsNull(handler.Authorization);
        Assert.AreEqual(0, credential.Scopes.Count);
    }

    [TestMethod]
    public async Task A_token_is_never_sent_over_http()
    {
        var handler = new CapturingHandler();
        var credential = new RecordingCredential();
        var options = Options(o =>
        {
            o.AllowInsecureHttp = true;
            o.BearerTokens.Add(new AlertWebhookBearerToken { UrlPrefix = "https://agent.example.com/", Audience = "api://agent" });
        });

        await CreateSender(handler, options, credential).SendAsync(Webhook("http://agent.example.com/api/triggers/abc"));

        Assert.IsNull(handler.Authorization);
    }

    [TestMethod]
    public async Task A_token_is_reused_until_it_is_about_to_expire()
    {
        var handler = new CapturingHandler();
        var credential = new RecordingCredential();
        var options = Options(o => o.BearerTokens.Add(new AlertWebhookBearerToken
        {
            UrlPrefix = "https://agent.example.com/",
            Audience = "api://agent",
        }));
        var sender = CreateSender(handler, options, credential);

        await sender.SendAsync(Webhook());
        await sender.SendAsync(Webhook());

        Assert.AreEqual(1, credential.Scopes.Count);
        Assert.AreEqual("api://agent/.default", credential.Scopes[0]);
    }

    [TestMethod]
    public async Task A_token_that_cannot_be_acquired_fails_the_delivery()
    {
        var handler = new CapturingHandler();
        var credential = new RecordingCredential { Failure = new InvalidOperationException("no identity") };
        var options = Options(o => o.BearerTokens.Add(new AlertWebhookBearerToken
        {
            UrlPrefix = "https://agent.example.com/",
            Audience = "api://agent",
        }));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateSender(handler, options, credential).SendAsync(Webhook()));

        Assert.IsFalse(handler.Sent, "the request must not go out without its token");
    }

    [TestMethod]
    [DataRow(HttpStatusCode.OK)]
    [DataRow(HttpStatusCode.Accepted)]
    [DataRow(HttpStatusCode.NoContent)]
    public async Task Any_2xx_is_a_delivery(HttpStatusCode status)
        => await CreateSender(new CapturingHandler(status)).SendAsync(Webhook());

    [TestMethod]
    [DataRow(HttpStatusCode.Found)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task Any_other_status_fails_and_names_only_the_host(HttpStatusCode status)
    {
        var sender = CreateSender(new CapturingHandler(status));

        var exception = await Assert.ThrowsExactlyAsync<AlertWebhookDeliveryException>(
            () => sender.SendAsync(Webhook("https://agent.example.com/hooks/secret-path?code=secret-code")));

        StringAssert.Contains(exception.Message, "agent.example.com", StringComparison.Ordinal);
        Assert.IsFalse(exception.Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task A_receiver_that_does_not_answer_in_time_fails_the_delivery()
    {
        var handler = new CapturingHandler { Delay = TimeSpan.FromSeconds(30) };
        var sender = CreateSender(handler, Options(o => o.Timeout = TimeSpan.FromMilliseconds(50)));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => sender.SendAsync(Webhook()));
    }

    [TestMethod]
    public async Task A_cancelled_host_is_not_reported_as_a_timeout()
    {
        var handler = new CapturingHandler { Delay = TimeSpan.FromSeconds(30) };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateSender(handler).SendAsync(Webhook(), cancellation.Token));
    }

    [TestMethod]
    [DataRow("https://agent.example.com/hook", false, true)]
    [DataRow("http://agent.example.com/hook", false, false)]
    [DataRow("http://agent.example.com/hook", true, true)]
    [DataRow("agent.example.com/hook", true, false)]
    [DataRow(null, true, false)]
    public void CanSendTo_follows_the_url_rules(string? url, bool allowHttp, bool expected)
    {
        var sender = CreateSender(new CapturingHandler(), Options(o => o.AllowInsecureHttp = allowHttp));

        Assert.AreEqual(expected, sender.CanSendTo(url));
    }

    // ── Where the Resolver may connect ─────────────────────────────────────────────────

    [TestMethod]
    [DataRow("8.8.8.8", true)]
    [DataRow("20.50.2.1", true)]
    [DataRow("2a01:111:f100::1", true)]
    [DataRow("0.0.0.0", false)]
    [DataRow("10.1.2.3", false)]
    [DataRow("100.64.0.1", false)]
    [DataRow("127.0.0.1", false)]
    [DataRow("169.254.169.254", false)]
    [DataRow("172.16.0.1", false)]
    [DataRow("172.31.255.255", false)]
    [DataRow("172.32.0.1", true)]
    [DataRow("192.168.1.1", false)]
    [DataRow("198.18.0.1", false)]
    [DataRow("224.0.0.1", false)]
    [DataRow("255.255.255.255", false)]
    [DataRow("::1", false)]
    [DataRow("::", false)]
    [DataRow("fe80::1", false)]
    [DataRow("fc00::1", false)]
    [DataRow("fd12:3456::1", false)]
    [DataRow("ff02::1", false)]
    [DataRow("::ffff:10.0.0.1", false)]
    [DataRow("::ffff:8.8.8.8", true)]
    public void Only_public_addresses_pass_the_connect_guard(string address, bool expected)
        => Assert.AreEqual(expected, AlertWebhookConnectGuard.IsPublic(IPAddress.Parse(address)));

    [TestMethod]
    public async Task A_loopback_receiver_is_refused_unless_private_networks_are_allowed()
    {
        using var receiver = new LoopbackReceiver();
        var url = $"http://127.0.0.1:{receiver.Port}/hook";

        var guarded = Options(o => o.AllowInsecureHttp = true);
        using (var sender = new AlertWebhookSender(guarded, AlertWebhookSender.CreateHttpClient(guarded), null, TimeProvider.System))
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Webhook(url)));
            Assert.IsInstanceOfType<AlertWebhookBlockedException>(exception.InnerException ?? exception);
        }

        Assert.AreEqual(0, receiver.Requests, "the guard must refuse before connecting");

        var open = Options(o =>
        {
            o.AllowInsecureHttp = true;
            o.AllowPrivateNetworks = true;
        });
        using (var sender = new AlertWebhookSender(open, AlertWebhookSender.CreateHttpClient(open), null, TimeProvider.System))
        {
            await sender.SendAsync(Webhook(url));
        }

        Assert.AreEqual(1, receiver.Requests);
    }

    [TestMethod]
    public async Task A_redirect_is_a_failed_delivery_and_is_not_followed()
    {
        using var receiver = new LoopbackReceiver("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var options = Options(o =>
        {
            o.AllowInsecureHttp = true;
            o.AllowPrivateNetworks = true;
        });
        using var sender = new AlertWebhookSender(options, AlertWebhookSender.CreateHttpClient(options), null, TimeProvider.System);

        await Assert.ThrowsExactlyAsync<AlertWebhookDeliveryException>(
            () => sender.SendAsync(Webhook($"http://127.0.0.1:{receiver.Port}/hook")));

        Assert.AreEqual(1, receiver.Requests);
    }

    // ── Settings ───────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Webhooks_are_off_until_a_signing_key_is_set()
    {
        Assert.IsFalse(new AlertWebhookOptions().IsConfigured);
        Assert.IsFalse(new AlertWebhookOptions { SigningKey = "  " }.IsConfigured);
        Assert.IsTrue(Options().IsConfigured);
        Assert.AreEqual(0, Options().Validate().Count);
    }

    [TestMethod]
    public void A_short_signing_key_is_invalid()
        => Assert.AreEqual(1, new AlertWebhookOptions { SigningKey = "too-short" }.Validate().Count);

    [TestMethod]
    public void A_non_positive_timeout_is_invalid()
        => Assert.AreEqual(1, Options(o => o.Timeout = TimeSpan.Zero).Validate().Count);

    [TestMethod]
    [DataRow("https://agent.example.com/api/", "https://azuresre.dev", true)]
    [DataRow("https://agent.example.com", "59f0a04a-b322-4310-adc9-39ac41e9631e", true)]
    [DataRow("http://agent.example.com/api/", "https://azuresre.dev", false, DisplayName = "an http prefix")]
    [DataRow("agent.example.com", "https://azuresre.dev", false, DisplayName = "a host instead of a URL")]
    [DataRow("https://agent.example.com/api/?code=1", "https://azuresre.dev", false, DisplayName = "a prefix with a query")]
    [DataRow("https://user@agent.example.com/api/", "https://azuresre.dev", false, DisplayName = "a prefix with user info")]
    [DataRow("", "https://azuresre.dev", false, DisplayName = "an empty prefix")]
    [DataRow(null, null, false, DisplayName = "an empty entry")]
    [DataRow("https://agent.example.com/api/", "", false, DisplayName = "an empty audience")]
    [DataRow("https://agent.example.com/api/", "two words", false, DisplayName = "an audience with a space")]
    public void A_token_entry_needs_an_https_prefix_and_an_audience(string? prefix, string? audience, bool valid)
    {
        var options = Options(o => o.BearerTokens.Add(new AlertWebhookBearerToken { UrlPrefix = prefix, Audience = audience }));

        Assert.AreEqual(valid, options.Validate().Count == 0);
    }

    internal sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

        public bool Sent { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public string? Authorization { get; private set; }
        public TimeSpan Delay { get; set; }

        public string? Header(string name) => _headers.GetValueOrDefault(name);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            Sent = true;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Authorization = request.Headers.Authorization?.ToString();
            _headers.Clear();
            foreach (var header in request.Headers)
            {
                _headers[header.Key] = string.Join(",", header.Value);
            }

            return new HttpResponseMessage(status);
        }
    }

    internal sealed class RecordingCredential : TokenCredential
    {
        public List<string> Scopes { get; } = new();

        public Exception? Failure { get; set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return ValueTask.FromException<AccessToken>(Failure);
            }

            var scope = requestContext.Scopes.Single();
            Scopes.Add(scope);
            return ValueTask.FromResult(new AccessToken("token-for-" + scope, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    /// <summary>A one-response HTTP server on loopback, for the tests that need a real connection.</summary>
    private sealed class LoopbackReceiver : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly byte[] _response;
        private int _requests;

        public LoopbackReceiver(string response = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
        {
            _response = Encoding.ASCII.GetBytes(response);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    Interlocked.Increment(ref _requests);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var read = new StringBuilder();
                    // Read the headers and the (small) body before answering, so the client never
                    // sees a reset while it is still writing.
                    while (!HasWholeRequest(read.ToString()))
                    {
                        var count = await stream.ReadAsync(buffer, _stop.Token);
                        if (count == 0)
                        {
                            break;
                        }

                        read.Append(Encoding.ASCII.GetString(buffer, 0, count));
                    }

                    await stream.WriteAsync(_response, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
            {
                // The test is over.
            }
        }

        private static bool HasWholeRequest(string request)
        {
            var headerEnd = request.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                return false;
            }

            const string lengthHeader = "Content-Length: ";
            var lengthAt = request.IndexOf(lengthHeader, StringComparison.OrdinalIgnoreCase);
            if (lengthAt < 0 || lengthAt > headerEnd)
            {
                return true;
            }

            var lengthEnd = request.IndexOf("\r\n", lengthAt, StringComparison.Ordinal);
            var length = int.Parse(request.AsSpan(lengthAt + lengthHeader.Length, lengthEnd - lengthAt - lengthHeader.Length), System.Globalization.CultureInfo.InvariantCulture);
            return request.Length - (headerEnd + 4) >= length;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
