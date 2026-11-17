#pragma warning disable CA1707, CA2007
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NimBus.Core.Messages.PII;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class ApiAndRaceTests
{
    [TestMethod]
    public async Task Api_Enforces_Anonymous_Reader_And_Request_Isolation_And_Serves_No_Analyze_Route()
    {
        await using var store = await StoreFixture.CreateAsync("memory");
        var reservation = await store.First.ReserveAsync("failure", Guid.NewGuid().ToString("D"), false, DateTimeOffset.UtcNow,
            new ClassificationScope("failure", "event", "endpoint", null));
        await store.First.CompleteAsync(reservation.OperationId, DurableStoreConformanceTests.Result("failure", reservation.Revision));
        var source = new ServiceFixture();
        using var host = await CreateHost(store.First, source.Messages);
        using var client = host.GetTestClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/integration-intelligence/status?endpointId=endpoint")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Role", "none");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/integration-intelligence/status?endpointId=endpoint")).StatusCode);
        SetRole(client, "reader");
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
        SetRole(client, "contributor");
        using var post = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new { force = false }) };
        post.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(post)).StatusCode, "The analyze route was removed in v5.0.0.");
        SetRole(client, "none");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    private const string Route = "/api/integration-intelligence/failures/event/failure/classification";

    private static void SetRole(HttpClient client, string role)
    {
        client.DefaultRequestHeaders.Remove("Test-Role");
        client.DefaultRequestHeaders.Add("Test-Role", role);
    }

    private static Task<IHost> CreateHost(IFailureClassificationStore store, IMessageTrackingStore messages)
        => new HostBuilder().UseDefaultServiceProvider(options => options.ValidateScopes = true)
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("test", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton(store);
                    services.AddSingleton(messages);
                    services.AddSingleton<IEventJsonMasker, TestMasker>();
                    services.AddSingleton<IEventJsonRedactor, NullEventJsonRedactor>();
                    services.AddScoped<IIntegrationIntelligenceHost>(sp =>
                    {
                        var role = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.User.FindFirstValue(ClaimTypes.Role);
                        return new TestIntelligenceHost { Reader = role is "reader" or "contributor", Contributor = role == "contributor" };
                    });
                    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["NimBus:IntegrationIntelligence:Enabled"] = "true",
                    }).Build();
                    services.AddNimBusIntegrationIntelligence(config);
                    services.RemoveAll<IHostedService>();
                    services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceReadController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            }).StartAsync();

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["Test-Role"].ToString();
            if (role.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "test")));
        }
    }
}
