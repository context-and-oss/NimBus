#pragma warning disable CA1707, CA2007
using System;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.DashboardTokens;

namespace NimBus.WebApp.Tests.DashboardTokens;

/// <summary>
/// A dashboard token admits the Monitor feed and nothing else, while a signed-in person keeps the
/// feed. Mirrors production: a default sign-in scheme, the global "authenticated user" filter, and
/// <see cref="DashboardTokenServiceCollectionExtensions.AddNimBusDashboardTokens"/>.
/// </summary>
[TestClass]
public class DashboardTokenAuthorizationTests
{
    private const string SignInScheme = "TestSignIn";
    private const string SignInHeader = "X-Test-Sign-In";

    private static IHost _host = null!;
    private static HttpClient _client = null!;
    private static string _token = null!;
    private static string _revokedToken = null!;
    private static string _expiredToken = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var store = new InMemoryMessageStore();
        var active = SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix);
        var revoked = SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix);
        var expired = SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix);
        _token = active.Value;
        _revokedToken = revoked.Value;
        _expiredToken = expired.Value;
        Assert.IsTrue(await store.TrySetDashboardTokenSettings(new DashboardTokenSettings
        {
            Revision = "r1",
            Tokens =
            {
                new DashboardToken { TokenId = active.Id, Name = "Wall", SecretHash = active.SecretHash, CreatedAtUtc = DateTime.UtcNow },
                new DashboardToken { TokenId = revoked.Id, Name = "Old", SecretHash = revoked.SecretHash, CreatedAtUtc = DateTime.UtcNow, RevokedAtUtc = DateTime.UtcNow },
                new DashboardToken { TokenId = expired.Id, Name = "Gone", SecretHash = expired.SecretHash, CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) },
            },
        }, null));

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<IDashboardTokenStore>(store);
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IApplicationApiController, StubApplicationApi>();
                    services.AddSingleton<IMonitorFeedApiController, StubMonitorFeedApi>();

                    services
                        .AddAuthentication(SignInScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderSignInHandler>(SignInScheme, _ => { });
                    services.AddNimBusDashboardTokens();

                    // With views: the admin controllers carry [AutoValidateAntiforgeryToken].
                    services.AddControllersWithViews(options =>
                    {
                        options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                    })
                    .AddApplicationPart(typeof(MonitorFeedApiController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        _host = await builder.StartAsync();
        _client = _host.GetTestServer().CreateClient();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [TestMethod]
    [DataRow("/api/monitor/feed/status")]
    [DataRow("/api/monitor/feed/metrics")]
    [DataRow("/api/monitor/feed/history")]
    public async Task A_token_reaches_the_feed(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, token: _token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [DataRow("GET", "/api/me")]
    [DataRow("GET", "/api/monitor/acknowledgements")]
    [DataRow("PUT", "/api/monitor/acknowledgements/Billing")]
    [DataRow("DELETE", "/api/monitor/acknowledgements/Billing")]
    [DataRow("GET", "/api/metrics/overview?period=1h")]
    [DataRow("GET", "/api/heartbeat/page")]
    [DataRow("POST", "/api/endpoint/status/count")]
    [DataRow("GET", "/api/admin/dashboard-tokens")]
    [DataRow("POST", "/api/admin/dashboard-tokens")]
    [DataRow("GET", "/api/admin/agent-keys")]
    public async Task A_token_reaches_nothing_else(string method, string path)
    {
        using var response = await SendAsync(new HttpMethod(method), path, token: _token);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {path}");
    }

    [TestMethod]
    public async Task A_signed_in_person_reaches_the_feed()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/monitor/feed/status", signedIn: true);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task No_token_and_no_sign_in_is_refused()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/monitor/feed/status");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Unknown_revoked_expired_and_malformed_tokens_are_refused()
    {
        foreach (var token in new[] { SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix).Value, _revokedToken, _expiredToken, "nbdt_not-a-token" })
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/monitor/feed/status", token: token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    private static Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token = null, bool signedIn = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Add(DashboardTokenDefaults.HeaderName, token);
        if (signedIn) request.Headers.Add(SignInHeader, "1");
        return _client.SendAsync(request);
    }

    private sealed class StubApplicationApi : IApplicationApiController
    {
        public Task<ActionResult<ApplicationStatus>> GetApiAppStatsAsync()
            => Task.FromResult<ActionResult<ApplicationStatus>>(new OkObjectResult(new ApplicationStatus()));

        public Task<ActionResult<UserInfo>> GetMeAsync()
            => Task.FromResult<ActionResult<UserInfo>>(new OkObjectResult(new UserInfo()));
    }

    private sealed class StubMonitorFeedApi : IMonitorFeedApiController
    {
        public Task<ActionResult<MonitorFeedStatus>> GetMonitorFeedStatusAsync()
            => Task.FromResult<ActionResult<MonitorFeedStatus>>(new MonitorFeedStatus());

        public Task<ActionResult<MonitorFeedMetrics>> GetMonitorFeedMetricsAsync()
            => Task.FromResult<ActionResult<MonitorFeedMetrics>>(new MonitorFeedMetrics());

        public Task<ActionResult<MonitorFeedHistory>> GetMonitorFeedHistoryAsync()
            => Task.FromResult<ActionResult<MonitorFeedHistory>>(new MonitorFeedHistory());
    }

    private sealed class HeaderSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(SignInHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Name, "Test User") },
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
