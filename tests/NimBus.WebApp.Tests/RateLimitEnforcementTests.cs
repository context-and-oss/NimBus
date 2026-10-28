#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Constants;
using NimBus.WebApp.Hubs;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// AC-4 to AC-7, AC-10 and AC-11: what actually happens to a request at, under
/// and over each limit. Test-only small limits keep these fast and
/// deterministic; the production values are asserted in
/// <see cref="RateLimitWiringTests.Defaults_are_the_documented_values"/>.
/// <para>
/// This host mirrors production middleware order — UseRateLimiter after
/// UseAuthorization — because the admin and search partitions key on
/// HttpContext.User. It stands up its own SignalR, health checks and stub auth
/// scheme rather than extending HubAuthorizationTestServer, which is internal
/// sealed, has a fixed Configure with no rate limiting, and is shared with the
/// spec-010 hub authorization tests.
/// </para>
/// </summary>
[TestClass]
public class RateLimitEnforcementTests
{
    private const string TestAuthScheme = "TestAuthenticated";
    private const string TestAuthHeader = "X-Test-Auth";
    private const string DefaultUser = "user-a";

    [TestMethod]
    public async Task Receive_rejects_exactly_one_request_beyond_permits_plus_queue()
    {
        // AC-4. Permits 2 + queue 1 = 3 slots; the 4th arrival is rejected.
        await using var host = await Host(new()
        {
            ["RateLimiting:AgentReceive:PermitLimit"] = "2",
            ["RateLimiting:AgentReceive:QueueLimit"] = "1",
        });

        using var client = host.AuthenticatedClient();

        // Fire all four at once and await NOTHING yet: the gate is never released
        // before the assertion below, so no permit is ever returned. The limiter
        // admits or queues the first three arrivals and rejects every later one,
        // which makes the rejection count exactly one under any interleaving —
        // and an admitted request physically cannot finish while the gate is
        // closed, so the first task to complete is necessarily the rejected one.
        var responses = Enumerable.Range(0, 4).Select(_ => client.GetAsync(ReceiveUrl)).ToArray();

        var first = await Task.WhenAny(responses).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await first).StatusCode);
        Assert.AreEqual(1, responses.Count(t => t.IsCompleted), "Only the rejected request may complete while the gate is closed.");

        host.Agent.Release();
        var all = await Task.WhenAll(responses).WaitAsync(TimeSpan.FromSeconds(30));

        // Slot accounting: production reads as 20 executing + 5 queued = 25
        // in-flight slots, with the 26th rejected.
        Assert.AreEqual(3, all.Count(r => r.StatusCode != HttpStatusCode.TooManyRequests), "Permits + queue must all be served.");
        Assert.AreEqual(1, all.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var response in all)
        {
            response.Dispose();
        }
    }

    [TestMethod]
    public async Task Receive_with_no_queue_rejects_everything_beyond_the_permits()
    {
        // Pins the documented escape hatch (QueueLimit = 0 for a hard cap) as
        // real rather than aspirational.
        await using var host = await Host(new()
        {
            ["RateLimiting:AgentReceive:PermitLimit"] = "2",
            ["RateLimiting:AgentReceive:QueueLimit"] = "0",
        });

        using var client = host.AuthenticatedClient();
        var responses = Enumerable.Range(0, 4).Select(_ => client.GetAsync(ReceiveUrl)).ToArray();

        await WaitUntil(() => responses.Count(t => t.IsCompleted) == 2, "two rejections");

        host.Agent.Release();
        var all = await Task.WhenAll(responses).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual(2, all.Count(r => r.StatusCode != HttpStatusCode.TooManyRequests));
        Assert.AreEqual(2, all.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var response in all)
        {
            response.Dispose();
        }
    }

    [TestMethod]
    public async Task Receive_under_the_limit_keeps_its_existing_status_codes()
    {
        // AC-5: the limiter changes no successful-path behaviour.
        await using var host = await Host(new() { ["RateLimiting:AgentReceive:PermitLimit"] = "2" });
        using var client = host.AuthenticatedClient();

        host.Agent.Release();

        host.Agent.ReturnsMessage = true;
        using (var hit = await client.GetAsync(ReceiveUrl))
        {
            Assert.AreEqual(HttpStatusCode.OK, hit.StatusCode);
        }

        host.Agent.ReturnsMessage = false;
        using (var miss = await client.GetAsync(ReceiveUrl))
        {
            Assert.AreEqual(HttpStatusCode.NoContent, miss.StatusCode);
        }
    }

    [TestMethod]
    public async Task Admin_rejects_requests_beyond_the_window_allowance()
    {
        // AC-6, against two different bulk routes.
        await using var host = await Host(new() { ["RateLimiting:Admin:PermitLimit"] = "2" });
        using var client = host.AuthenticatedClient();

        foreach (var route in new[] { "/api/admin/endpoint/ep-1/bulk-resubmit", "/api/admin/endpoint/ep-1/purge" })
        {
            await using var scoped = await Host(new() { ["RateLimiting:Admin:PermitLimit"] = "2" });
            using var scopedClient = scoped.AuthenticatedClient();

            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 3; i++)
            {
                using var response = await scopedClient.PostAsync(route, EmptyJson());
                statuses.Add(response.StatusCode);
            }

            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, statuses[0], $"{route}: the first request is under the allowance.");
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, statuses[1], $"{route}: the second request is under the allowance.");
            Assert.AreEqual(HttpStatusCode.TooManyRequests, statuses[2], $"{route}: the third request exceeds it.");
        }
    }

    [TestMethod]
    public async Task Admin_budgets_are_per_user_not_shared()
    {
        // Without this a single shared bucket would still pass AC-6 while
        // throttling a whole team, and UserPartitionKey would be untested.
        await using var host = await Host(new() { ["RateLimiting:Admin:PermitLimit"] = "2" });

        using var userA = host.AuthenticatedClient("user-a");
        using var userB = host.AuthenticatedClient("user-b");

        for (var i = 0; i < 2; i++)
        {
            using var warmup = await userA.PostAsync("/api/admin/endpoint/ep-1/purge", EmptyJson());
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, warmup.StatusCode);
        }

        using (var exhausted = await userA.PostAsync("/api/admin/endpoint/ep-1/purge", EmptyJson()))
        {
            Assert.AreEqual(HttpStatusCode.TooManyRequests, exhausted.StatusCode, "user-a has spent its allowance.");
        }

        using var other = await userB.PostAsync("/api/admin/endpoint/ep-1/purge", EmptyJson());
        Assert.AreNotEqual(HttpStatusCode.TooManyRequests, other.StatusCode, "user-b must have its own budget.");
    }

    [TestMethod]
    public async Task Search_rejects_requests_beyond_the_window_allowance()
    {
        // AC-7.
        foreach (var route in new[] { "/api/messages/search", "/api/audits/search" })
        {
            await using var host = await Host(new() { ["RateLimiting:Search:PermitLimit"] = "2" });
            using var client = host.AuthenticatedClient();

            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 3; i++)
            {
                using var response = await client.PostAsync(route, EmptyJson());
                statuses.Add(response.StatusCode);
            }

            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, statuses[0], route);
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, statuses[1], route);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, statuses[2], route);
        }
    }

    [TestMethod]
    public async Task Fixed_window_rejections_carry_retry_after_and_a_body()
    {
        await using var host = await Host(new() { ["RateLimiting:Search:PermitLimit"] = "1" });
        using var client = host.AuthenticatedClient();

        using (var allowed = await client.PostAsync("/api/messages/search", EmptyJson()))
        {
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);
        }

        using var rejected = await client.PostAsync("/api/messages/search", EmptyJson());
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.IsNotNull(rejected.Headers.RetryAfter, "A fixed window knows when the caller may retry.");
        Assert.IsTrue((await rejected.Content.ReadAsStringAsync()).Length > 0, "A bodyless 429 shows a human at a browser nothing.");
    }

    [TestMethod]
    public async Task Concurrency_rejections_carry_a_body_but_no_retry_after()
    {
        await using var host = await Host(new()
        {
            ["RateLimiting:AgentReceive:PermitLimit"] = "1",
            ["RateLimiting:AgentReceive:QueueLimit"] = "0",
        });

        using var client = host.AuthenticatedClient();
        var blocked = client.GetAsync(ReceiveUrl);
        await host.Agent.WaitForInFlightAsync(1);

        using var rejected = await client.GetAsync(ReceiveUrl).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.IsNull(rejected.Headers.RetryAfter, "A concurrency limiter has no meaningful retry hint — permits return on completion, not on a clock.");
        Assert.IsTrue((await rejected.Content.ReadAsStringAsync()).Length > 0);

        host.Agent.Release();
        (await blocked).Dispose();
    }

    [TestMethod]
    public async Task Health_and_the_hub_are_untouched_while_receive_permits_are_exhausted()
    {
        // AC-10. Asserting the exact status, not merely "not 429", so the case
        // cannot pass for the 401 reason GridEventsHubAuthorizationTests documents.
        await using var host = await Host(new()
        {
            ["RateLimiting:AgentReceive:PermitLimit"] = "1",
            ["RateLimiting:AgentReceive:QueueLimit"] = "0",
        });

        using var client = host.AuthenticatedClient();
        var blocked = client.GetAsync(ReceiveUrl);
        await host.Agent.WaitForInFlightAsync(1);
        using (var overflow = await client.GetAsync(ReceiveUrl).WaitAsync(TimeSpan.FromSeconds(30)))
        {
            Assert.AreEqual(HttpStatusCode.TooManyRequests, overflow.StatusCode, "Precondition: the receive permits are exhausted.");
        }

        using (var health = await client.GetAsync("/health"))
        {
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "Health probes carry no policy.");
        }

        using (var negotiate = await client.PostAsync($"{AppEndpoints.GridEventHub}/negotiate?negotiateVersion=1", content: null))
        {
            Assert.AreEqual(HttpStatusCode.OK, negotiate.StatusCode, "The hub carries no policy and its client is authenticated.");
        }

        host.Agent.Release();
        (await blocked).Dispose();
    }

    [TestMethod]
    public async Task An_open_hub_connection_consumes_no_receive_permit()
    {
        await using var host = await Host(new()
        {
            ["RateLimiting:AgentReceive:PermitLimit"] = "2",
            ["RateLimiting:AgentReceive:QueueLimit"] = "0",
        });

        var connection = host.BuildHubConnection();
        await connection.StartAsync();
        Assert.AreEqual(HubConnectionState.Connected, connection.State, "The AC-10 case is meaningless unless the hub client really connects.");

        try
        {
            using var client = host.AuthenticatedClient();
            var inFlight = Enumerable.Range(0, 2).Select(_ => client.GetAsync(ReceiveUrl)).ToArray();
            await host.Agent.WaitForInFlightAsync(2);

            using (var overflow = await client.GetAsync(ReceiveUrl).WaitAsync(TimeSpan.FromSeconds(30)))
            {
                Assert.AreEqual(HttpStatusCode.TooManyRequests, overflow.StatusCode);
            }

            host.Agent.Release();
            var all = await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.AreEqual(
                2,
                all.Count(r => r.StatusCode != HttpStatusCode.TooManyRequests),
                "The full permit count must still be available with a hub connection open.");
            foreach (var response in all)
            {
                response.Dispose();
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Scripted_spa_session_produces_no_throttling_under_production_defaults()
    {
        // AC-11, made finite: the §5 scripted sequences. 7 admin requests of 60
        // and 35 search requests of 120, as one authenticated user, inside one
        // 60-second window.
        await using var host = await Host([]);
        using var client = host.AuthenticatedClient();

        for (var i = 0; i < 6; i++)
        {
            using var config = await client.GetAsync("/api/admin/platform-config");
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, config.StatusCode, $"Admin request {i + 1} of 7 was throttled.");
        }

        using (var topology = await client.GetAsync("/api/admin/topology/ep-1"))
        {
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, topology.StatusCode, "Admin request 7 of 7 was throttled.");
        }

        for (var i = 0; i < 30; i++)
        {
            using var search = await client.PostAsync("/api/messages/search", EmptyJson());
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, search.StatusCode, $"Message search {i + 1} of 30 was throttled.");
        }

        for (var i = 0; i < 5; i++)
        {
            using var audits = await client.PostAsync("/api/audits/search", EmptyJson());
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, audits.StatusCode, $"Audit search {i + 1} of 5 was throttled.");
        }
    }

    private const string ReceiveUrl = "/api/agent/receive?eventTypeId=OrderPlaced&waitSeconds=60";

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new AssertFailedException($"Timed out waiting for {what}.");
    }

    private static Task<TestHostFixture> Host(Dictionary<string, string?> settings) => TestHostFixture.CreateAsync(settings);

    private sealed class TestHostFixture : IAsyncDisposable
    {
        private readonly IHost _host;

        private TestHostFixture(IHost host, StubAgentApi agent)
        {
            _host = host;
            Agent = agent;
        }

        public StubAgentApi Agent { get; }

        private TestServer Server => _host.GetTestServer();

        public static async Task<TestHostFixture> CreateAsync(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var agent = new StubAgentApi();

            var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Error));
                        services.AddSingleton<IAgentApiController>(agent);
                        services.AddSingleton<IAdminApiController>(new StubAdminApi());
                        services.AddSingleton<IMessageApiController>(new StubMessageApi());
                        services.AddSingleton<IAuditApiController>(new StubAuditApi());

                        services.AddRouting();
                        services.AddSignalR();
                        services.AddHealthChecks();
                        services
                            .AddAuthentication(TestAuthScheme)
                            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(TestAuthScheme, _ => { });
                        services.AddAuthorization();
                        services.AddNimBusRateLimiting(configuration);
                        services.AddControllers().AddApplicationPart(typeof(ApplicationApiController).Assembly);
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseRateLimiter();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHealthChecks("/health");
                            endpoints.MapHub<GridEventsHub>(AppEndpoints.GridEventHub);
                            endpoints.MapControllers();
                        });
                    });
                })
                .StartAsync();

            return new TestHostFixture(host, agent);
        }

        public HttpClient AuthenticatedClient(string user = DefaultUser)
        {
            var client = Server.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHeader, user);
            return client;
        }

        public HubConnection BuildHubConnection()
        {
            var url = Server.BaseAddress + AppEndpoints.GridEventHub.TrimStart('/');
            return new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                    // TestServer does not negotiate websockets.
                    options.Transports = HttpTransportType.LongPolling;
                    options.Headers[TestAuthHeader] = DefaultUser;
                })
                .Build();
        }

        public async ValueTask DisposeAsync()
        {
            Agent.Release();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    /// <summary>
    /// Holds every receive call until the test releases the gate, so "in flight"
    /// is something the test controls rather than something it races.
    /// </summary>
    internal sealed class StubAgentApi : IAgentApiController
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        public bool ReturnsMessage { get; set; }

        public void Release() => _gate.TrySetResult();

        /// <summary>
        /// Blocks until <paramref name="count"/> requests have actually reached
        /// the handler — i.e. have taken a permit. Without this a probe can
        /// overtake the request it is meant to be queueing behind and take the
        /// permit itself.
        /// </summary>
        public Task WaitForInFlightAsync(int count)
            => WaitUntil(() => Volatile.Read(ref _entered) >= count, $"{count} receive request(s) to hold a permit");

        public async Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds)
        {
            Interlocked.Increment(ref _entered);
            await _gate.Task;
            return ReturnsMessage
                ? new OkObjectResult(new AgentReceivedMessage())
                : new NoContentResult();
        }

        public Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync()
            => Task.FromResult<ActionResult<AgentCatalog>>(new OkObjectResult(new AgentCatalog()));

        public Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body)
            => Task.FromResult<ActionResult<EventTypeInfo>>(new OkObjectResult(new EventTypeInfo()));

        public Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body)
            => Task.FromResult<IActionResult>(new OkResult());

        public Task<IActionResult> PostAgentPublishAsync(AgentPublishRequest body)
            => Task.FromResult<IActionResult>(new OkResult());

        public Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body)
            => Task.FromResult<IActionResult>(new OkResult());
    }

    private sealed class StubMessageApi : IMessageApiController
    {
        public Task<ActionResult<MessageSearchResponse>> PostMessagesSearchAsync(MessageSearchRequest body)
            => Task.FromResult<ActionResult<MessageSearchResponse>>(new OkObjectResult(new MessageSearchResponse()));
    }

    private sealed class StubAuditApi : IAuditApiController
    {
        public Task<ActionResult<AuditSearchResponse>> PostAuditsSearchAsync(AuditSearchRequest body)
            => Task.FromResult<ActionResult<AuditSearchResponse>>(new OkObjectResult(new AuditSearchResponse()));
    }

    private sealed class StubAdminApi : IAdminApiController
    {
        public Task<ActionResult<IEnumerable<CosmosContainerInfo>>> GetAdminCosmosContainersAsync() => throw new NotSupportedException();
        public Task<ActionResult<CosmosContainerDeleteResult>> PostAdminCosmosContainerDeleteAsync(CosmosContainerDeleteRequest body, string containerName) => throw new NotSupportedException();
        private static Task<ActionResult<T>> Ok<T>() => Task.FromResult<ActionResult<T>>(new OkResult());

        private static Task<IActionResult> OkPlain() => Task.FromResult<IActionResult>(new OkResult());

        public Task<ActionResult<PlatformConfig>> GetAdminPlatformConfigAsync() => Ok<PlatformConfig>();

        public Task<IActionResult> GetAdminAsyncapiAsync(string format) => OkPlain();

        public Task<ActionResult<TopologyAuditResult>> GetAdminTopologyAsync(string endpointName) => Ok<TopologyAuditResult>();

        public Task<ActionResult<TopologyCleanupResult>> PostAdminTopologyRemoveDeprecatedAsync(string endpointName) => Ok<TopologyCleanupResult>();

        public Task<ActionResult<IEnumerable<ServiceBusTopicOverview>>> GetAdminServicebusTopicsAsync() => Ok<IEnumerable<ServiceBusTopicOverview>>();
        public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusTopicAsync(string topicName) => Ok<SubscriptionActionResult>();

        public Task<ActionResult<IEnumerable<ServiceBusSubscriptionInfo>>> GetAdminServicebusSubscriptionsAsync(string topicName) => Ok<IEnumerable<ServiceBusSubscriptionInfo>>();

        public Task<ActionResult<DeadLetterOverview>> GetAdminServicebusResolverDeadlettersAsync(string subscriptionName) => Ok<DeadLetterOverview>();

        public Task<ActionResult<BulkOperationResult>> PostAdminServicebusResolverDeadlettersResubmitAsync(DeadLetterResubmitRequest body, string subscriptionName) => Ok<BulkOperationResult>();

        public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionStatusAsync(SubscriptionStatusRequest body, string topicName, string subscriptionName) => Ok<SubscriptionActionResult>();

        public Task<ActionResult<BulkOperationResult>> PostAdminServicebusSubscriptionPurgeAsync(string topicName, string subscriptionName) => Ok<BulkOperationResult>();
        public Task<ActionResult<BulkOperationResult>> PostAdminServicebusSubscriptionDeadlettersPurgeAsync(string topicName, string subscriptionName) => Ok<BulkOperationResult>();

        public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionRecreateAsync(string topicName, string subscriptionName) => Ok<SubscriptionActionResult>();

        public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusSubscriptionAsync(string topicName, string subscriptionName) => Ok<SubscriptionActionResult>();

        public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusSubscriptionRuleAsync(string topicName, string subscriptionName, string ruleName) => Ok<SubscriptionActionResult>();

        public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionRestoreRulesAsync(string topicName, string subscriptionName) => Ok<SubscriptionActionResult>();

        public Task<ActionResult<BulkResubmitPreview>> GetAdminFailedPreviewAsync(string endpointId) => Ok<BulkResubmitPreview>();

        public Task<ActionResult<BulkOperationResult>> PostAdminBulkResubmitAsync(string endpointId) => Ok<BulkOperationResult>();

        public Task<ActionResult<Response2>> GetAdminDeadletteredPreviewAsync(string endpointId) => Ok<Response2>();

        public Task<ActionResult<BulkOperationResult>> PostAdminDeleteDeadletteredAsync(string endpointId) => Ok<BulkOperationResult>();

        public Task<ActionResult<SessionPurgePreview>> GetAdminSessionPreviewAsync(string endpointId, string sessionId) => Ok<SessionPurgePreview>();

        public Task<ActionResult<SessionPurgeResult>> PostAdminSessionPurgeAsync(string endpointId, string sessionId) => Ok<SessionPurgeResult>();

        public Task<IActionResult> DeleteAdminEventAsync(string endpointId, string eventId) => OkPlain();

        public Task<ActionResult<BulkOperationResult>> PostAdminDeleteAllAsync(string endpointId) => Ok<BulkOperationResult>();

        public Task<ActionResult<PurgePreview>> PostAdminPurgePreviewAsync(string endpointId, PurgeRequest body) => Ok<PurgePreview>();

        public Task<ActionResult<BulkOperationResult>> PostAdminPurgeAsync(string endpointId, PurgeRequest body) => Ok<BulkOperationResult>();

        public Task<ActionResult<StalePendingPreview>> PostAdminStalePendingPreviewAsync(string endpointId, StalePendingReconcileRequest body) => Ok<StalePendingPreview>();

        public Task<ActionResult<StalePendingReconcileResult>> PostAdminStalePendingReconcileAsync(string endpointId, StalePendingReconcileRequest body) => Ok<StalePendingReconcileResult>();

        public Task<ActionResult<CountResponse>> PostAdminDeleteByToPreviewAsync(DeleteByToRequest body) => Ok<CountResponse>();

        public Task<ActionResult<BulkOperationResult>> PostAdminDeleteByToAsync(DeleteByToRequest body) => Ok<BulkOperationResult>();

        public Task<ActionResult<CountResponse>> PostAdminDeleteByStatusPreviewAsync(string endpointId, DeleteByStatusRequest body) => Ok<CountResponse>();

        public Task<ActionResult<BulkOperationResult>> PostAdminDeleteByStatusAsync(string endpointId, DeleteByStatusRequest body) => Ok<BulkOperationResult>();

        public Task<ActionResult<CountResponse>> PostAdminSkipPreviewAsync(string endpointId, SkipRequest body) => Ok<CountResponse>();

        public Task<ActionResult<BulkOperationResult>> PostAdminSkipAsync(string endpointId, SkipRequest body) => Ok<BulkOperationResult>();

        public Task<ActionResult<CopyResult>> PostAdminCopyAsync(string endpointId, CopyRequest body) => Ok<CopyResult>();

        public Task<ActionResult<AuditSettings>> GetAdminAuditSettingsAsync() => Ok<AuditSettings>();

        public Task<ActionResult<AuditSettings>> PutAdminAuditSettingsAsync(AuditSettings body) => Ok<AuditSettings>();

        public Task<ActionResult<HeartbeatSettings>> GetAdminHeartbeatSettingsAsync() => Ok<HeartbeatSettings>();

        public Task<ActionResult<HeartbeatSettings>> PutAdminHeartbeatSettingsAsync(HeartbeatSettings body) => Ok<HeartbeatSettings>();

        public Task<ActionResult<CountResponse>> PostAdminHeartbeatSendAsync() => Ok<CountResponse>();

        public Task<ActionResult<IEnumerable<HeartbeatOverviewRow>>> GetAdminHeartbeatOverviewAsync() => Ok<IEnumerable<HeartbeatOverviewRow>>();

        public Task<IActionResult> PutAdminHeartbeatEndpointEnabledAsync(HeartbeatEndpointEnabledRequest body, string endpointId) => OkPlain();

        public Task<ActionResult<IEnumerable<ServiceHealthRow>>> GetAdminHealthServicesAsync() => Ok<IEnumerable<ServiceHealthRow>>();
    }

    /// <summary>
    /// Stub scheme in the shape this project already uses twice. The header
    /// VALUE becomes the NameIdentifier claim, which is what UserPartitionKey
    /// reads — varying it is how the per-user isolation test produces two buckets.
    /// </summary>
    private sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(TestAuthHeader, out var user) || string.IsNullOrEmpty(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.ToString()),
                    new Claim(ClaimTypes.Name, user.ToString()),
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
