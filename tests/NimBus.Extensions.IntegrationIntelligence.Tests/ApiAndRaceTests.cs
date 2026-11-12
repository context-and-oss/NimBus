#pragma warning disable CA1707, CA2007, CS0618 // CS0618: these tests cover the deprecated provider (Spec 044 §6.10).
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
    public async Task Api_Enforces_Anonymous_Reader_Contributor_And_Request_Isolation()
    {
        await using var store = await StoreFixture.CreateAsync("memory");
        var source = new ServiceFixture();
        using var host = await CreateHost(store.First, source.Messages, source.Provider);
        using var client = host.GetTestClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/integration-intelligence/status?endpointId=endpoint")).StatusCode);
        client.DefaultRequestHeaders.Add("Test-Role", "none");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/integration-intelligence/status?endpointId=endpoint")).StatusCode);
        SetRole(client, "contributor");
        using var post = await client.SendAsync(Post());
        Assert.AreEqual(HttpStatusCode.OK, post.StatusCode, await post.Content.ReadAsStringAsync());
        SetRole(client, "reader");
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.SendAsync(Post())).StatusCode);
        SetRole(client, "none");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
        Assert.AreEqual(1, source.Provider.Calls);
    }

    [TestMethod]
    [DataRow("sql")]
    [DataRow("cosmos")]
    public async Task Two_Hosts_Pay_Once_And_Do_Not_Change_Core_Message(string provider)
    {
        await using var store = await StoreFixture.CreateAsync(provider);
        var source = new ServiceFixture();
        var original = Newtonsoft.Json.JsonConvert.SerializeObject(source.Source.Message);
        var counter = new BlockingProvider();
        using var first = await CreateHost(store.First, source.Messages, counter);
        using var second = await CreateHost(store.Second, source.Messages, counter);
        using var client1 = first.GetTestClient();
        using var client2 = second.GetTestClient();
        SetRole(client1, "contributor");
        SetRole(client2, "contributor");
        var pending = client1.SendAsync(Post());
        await counter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.AreEqual(HttpStatusCode.Conflict, (await client2.SendAsync(Post())).StatusCode);
            Assert.AreEqual(1, counter.Calls);
        }
        finally { counter.Release.TrySetResult(); }
        Assert.AreEqual(HttpStatusCode.OK, (await pending).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client2.GetAsync(Route)).StatusCode);
        Assert.AreEqual(original, Newtonsoft.Json.JsonConvert.SerializeObject(source.Source.Message));
    }

    private const string Route = "/api/integration-intelligence/failures/event/failure/classification";
    private static HttpRequestMessage Post()
    {
        var message = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(new ClassificationRequest()) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return message;
    }

    private static void SetRole(HttpClient client, string role)
    {
        client.DefaultRequestHeaders.Remove("Test-Role");
        client.DefaultRequestHeaders.Add("Test-Role", role);
    }

    private static Task<IHost> CreateHost(IFailureClassificationStore store, IMessageTrackingStore messages, IFailureIntelligenceProvider provider)
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
                        ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "not-a-live-key",
                    }).Build();
                    services.AddNimBusIntegrationIntelligence(config);
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton(provider);
                    services.AddControllers().AddApplicationPart(typeof(IntegrationIntelligenceController).Assembly);
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

    private sealed class BlockingProvider : IFailureIntelligenceProvider
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "TypeSafe";
        public async Task<FailureIntelligenceProviderResult> ClassifyAsync(FailureClassificationInput input, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new("test", "unknown", 1, new Dictionary<string, double> { ["unknown"] = 1 }, 0, 0, 0, null, null);
        }
    }
}
