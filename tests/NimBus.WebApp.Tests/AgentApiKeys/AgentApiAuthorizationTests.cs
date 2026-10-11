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
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.AgentApiKeys;

/// <summary>
/// A key admits the agent API and nothing else, while a signed-in person keeps the agent API.
/// Mirrors production: a default sign-in scheme, the global "authenticated user" filter, and
/// <see cref="AgentApiKeyServiceCollectionExtensions.AddNimBusAgentApiKeys"/>.
/// </summary>
[TestClass]
public class AgentApiAuthorizationTests
{
    private const string SignInScheme = "TestSignIn";
    private const string SignInHeader = "X-Test-Sign-In";

    private static IHost _host = null!;
    private static HttpClient _client = null!;
    private static string _key = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var store = new InMemoryMessageStore();
        var generated = AgentApiKeyFormat.Generate();
        _key = generated.Key;
        Assert.IsTrue(await store.TrySetAgentApiKeySettings(new AgentApiKeySettings
        {
            Revision = "r1",
            Keys = { new AgentApiKey { KeyId = generated.KeyId, AgentId = "enrichment-agent", SecretHash = generated.SecretHash, CreatedAtUtc = DateTime.UtcNow } },
        }, null));

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddSingleton<IEndpointMetadataStore>(store);
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IApplicationApiController, StubApplicationApi>();
                    services.AddSingleton<IAgentApiController, StubAgentApi>();

                    services
                        .AddAuthentication(SignInScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderSignInHandler>(SignInScheme, _ => { });
                    services.AddNimBusAgentApiKeys();

                    services.AddControllers(options =>
                    {
                        options.Filters.Add(new AuthorizeFilter(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()));
                    })
                    .AddApplicationPart(typeof(AgentApiController).Assembly);
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
    public async Task A_key_reaches_the_agent_api_as_its_agent()
    {
        using var response = await SendAsync("/api/agent/catalog", key: _key);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("agent:enrichment-agent", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task A_key_does_not_reach_any_other_api()
    {
        using var response = await SendAsync("/api/me", key: _key);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task A_signed_in_person_still_reaches_the_agent_api()
    {
        using var response = await SendAsync("/api/agent/catalog", signedIn: true);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Test User", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task A_signed_in_person_still_reaches_other_apis()
    {
        using var response = await SendAsync("/api/me", signedIn: true);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task No_key_and_no_sign_in_is_refused()
    {
        using var response = await SendAsync("/api/agent/catalog");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task An_invalid_key_is_refused()
    {
        using var response = await SendAsync("/api/agent/catalog", key: AgentApiKeyFormat.Generate().Key);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(string path, string? key = null, bool signedIn = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null) request.Headers.Add(AgentApiKeyDefaults.HeaderName, key);
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

    /// <summary>Answers the catalog with the caller's name, so a test can see who got through.</summary>
    private sealed class StubAgentApi(IHttpContextAccessor accessor) : IAgentApiController
    {
        public Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync()
            => Task.FromResult<ActionResult<AgentCatalog>>(new ContentResult { Content = accessor.HttpContext!.User.Identity?.Name });

        public Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body) => throw new NotSupportedException();
        public Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body) => throw new NotSupportedException();
        public Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds) => throw new NotSupportedException();
        public Task<IActionResult> PostAgentPublishAsync(AgentPublishRequest body) => throw new NotSupportedException();
        public Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body) => throw new NotSupportedException();
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
