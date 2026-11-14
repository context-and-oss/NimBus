#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AgentApiKeys;
using NimBus.WebApp.Services.DashboardTokens;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Agent keys and dashboard tokens authenticate inside MVC, after the rate limiter has picked a
/// partition. The limiter must still give each verified key or token its own budget rather than
/// one shared per client IP, which anonymous traffic from the same address could exhaust.
/// Built with the real key and token schemes and the production pipeline order.
/// </summary>
[TestClass]
public class RateLimitCallerPartitionTests
{
    private const string SignInScheme = "TestSignIn";

    [TestMethod]
    public async Task Two_agent_keys_behind_one_address_have_their_own_global_budgets()
    {
        await using var host = await Host.StartAsync();
        var first = await host.IssueAgentKeyAsync("agent-a");
        var second = await host.IssueAgentKeyAsync("agent-b");

        for (var i = 0; i < 2; i++)
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/v1/agent/catalog", AgentApiKeyDefaults.HeaderName, first)).StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await host.GetAsync("/api/v1/agent/catalog", AgentApiKeyDefaults.HeaderName, first)).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/v1/agent/catalog", AgentApiKeyDefaults.HeaderName, second)).StatusCode,
            "Another agent's key must not share the exhausted budget.");
    }

    [TestMethod]
    public async Task Anonymous_traffic_from_the_same_address_cannot_exhaust_a_key()
    {
        await using var host = await Host.StartAsync();
        var key = await host.IssueAgentKeyAsync("agent-a");

        for (var i = 0; i < 3; i++)
            await host.GetAsync("/api/v1/agent/catalog", header: null, value: null);

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/v1/agent/catalog", AgentApiKeyDefaults.HeaderName, key)).StatusCode);
    }

    [TestMethod]
    public async Task Two_dashboard_tokens_behind_one_address_have_their_own_global_budgets()
    {
        await using var host = await Host.StartAsync();
        var first = await host.IssueDashboardTokenAsync();
        var second = await host.IssueDashboardTokenAsync();

        for (var i = 0; i < 2; i++)
            Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/v1/monitor/feed/status", DashboardTokenDefaults.HeaderName, first)).StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await host.GetAsync("/api/v1/monitor/feed/status", DashboardTokenDefaults.HeaderName, first)).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/v1/monitor/feed/status", DashboardTokenDefaults.HeaderName, second)).StatusCode);
    }

    [TestMethod]
    public async Task A_forged_key_id_does_not_draw_on_the_real_key_budget()
    {
        await using var host = await Host.StartAsync();
        var key = await host.IssueAgentKeyAsync("agent-a");
        AgentApiKeyFormat.TryParse(key, out var keyId, out _);
        var forged = "nbak_" + keyId + "_" + new string('A', 43);

        for (var i = 0; i < 3; i++)
            await host.GetAsync("/api/v1/agent/catalog", AgentApiKeyDefaults.HeaderName, forged);

        Assert.AreEqual(HttpStatusCode.OK, (await host.GetAsync("/api/v1/agent/catalog", AgentApiKeyDefaults.HeaderName, key)).StatusCode,
            "Only a verified key earns the key's partition.");
    }

    [TestMethod]
    public void The_production_pipeline_resolves_callers_right_before_the_rate_limiter()
    {
        var dir = System.IO.Path.GetDirectoryName(typeof(RateLimitCallerPartitionTests).Assembly.Location);
        string? file = null;
        while (!string.IsNullOrEmpty(dir) && file is null)
        {
            var candidate = System.IO.Path.Combine(dir, "src", "NimBus.WebApp", "Startup.Pipeline.cs");
            if (System.IO.File.Exists(candidate)) file = candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        var source = System.IO.File.ReadAllText(file!);
        var authentication = source.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        var callers = source.IndexOf("app.UseNimBusRateLimitCallers()", StringComparison.Ordinal);
        var limiter = source.IndexOf("app.UseRateLimiter()", StringComparison.Ordinal);
        Assert.IsTrue(authentication > 0 && authentication < callers && callers < limiter);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly InMemoryMessageStore _store;
        private readonly HttpClient _client;
        private readonly List<DashboardToken> _tokens = new();

        private Host(IHost host, InMemoryMessageStore store)
        {
            _host = host;
            _store = store;
            _client = host.GetTestServer().CreateClient();
        }

        public static async Task<Host> StartAsync()
        {
            var store = new InMemoryMessageStore();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["RateLimiting:Global:PermitLimit"] = "2" })
                .Build();
            var host = await new HostBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
                        services.AddSingleton<IEndpointMetadataStore>(store);
                        services.AddSingleton<IDashboardTokenStore>(store);
                        services.AddSingleton<IAgentApiController>(new StubAgentApi());
                        services.AddSingleton<IMonitorFeedApiController>(new StubMonitorFeedApi());
                        services.AddAuthentication(SignInScheme)
                            .AddScheme<AuthenticationSchemeOptions, NoSignIn>(SignInScheme, _ => { });
                        services.AddNimBusAgentApiKeys();
                        services.AddNimBusDashboardTokens();
                        services.AddNimBusRateLimiting(configuration);
                        services.AddControllers(options =>
                                options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())))
                            .AddApplicationPart(typeof(AgentApiController).Assembly);
                    });
                    web.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseNimBusRateLimitCallers();
                        app.UseRateLimiter();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .StartAsync();
            return new Host(host, store);
        }

        public async Task<string> IssueAgentKeyAsync(string agentId)
        {
            var service = new AgentApiKeyService(_store, _host.Services.GetRequiredService<IAgentApiKeyProvider>(), TimeProvider.System);
            var change = await service.CreateAsync(agentId, null, null, "owner");
            return change.FullKey!;
        }

        public async Task<string> IssueDashboardTokenAsync()
        {
            var service = new DashboardTokenService(_host.Services.GetRequiredService<IDashboardTokenProvider>(), TimeProvider.System, _store);
            var change = await service.CreateAsync("Wall " + _tokens.Count, null, "owner");
            _tokens.Add(change.Token!);
            return change.FullToken!;
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string? header, string? value)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (header is not null)
                request.Headers.Add(header, value);
            return await _client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class NoSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class StubAgentApi : IAgentApiController
    {
        public Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync() => Task.FromResult<ActionResult<AgentCatalog>>(new OkObjectResult(new AgentCatalog()));
        public Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body) => throw new NotSupportedException();
        public Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body) => throw new NotSupportedException();
        public Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds) => throw new NotSupportedException();
        public Task<IActionResult> PostAgentPublishAsync(AgentPublishRequest body) => throw new NotSupportedException();
        public Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body) => throw new NotSupportedException();
    }

    private sealed class StubMonitorFeedApi : IMonitorFeedApiController
    {
        public Task<ActionResult<MonitorFeedStatus>> GetMonitorFeedStatusAsync() => Task.FromResult<ActionResult<MonitorFeedStatus>>(new MonitorFeedStatus());
        public Task<ActionResult<MonitorFeedMetrics>> GetMonitorFeedMetricsAsync() => Task.FromResult<ActionResult<MonitorFeedMetrics>>(new MonitorFeedMetrics());
        public Task<ActionResult<MonitorFeedHistory>> GetMonitorFeedHistoryAsync() => Task.FromResult<ActionResult<MonitorFeedHistory>>(new MonitorFeedHistory());
    }
}
