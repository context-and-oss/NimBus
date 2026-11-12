#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Problems;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Every <c>/api</c> error is an RFC 7807 problem (application/problem+json) with a stable
/// schema, whatever produced it: a string or list body, a contract DTO, a bodyless result, the
/// authentication handlers, an antiforgery refusal, an exception or an unmatched route. Paths
/// outside <c>/api</c> keep their old bodies.
/// </summary>
[TestClass]
public class ApiProblemDetailsTests
{
    private const string TestAuthScheme = "Test";
    private const string TestAuthHeader = "X-Test-Auth";

    [TestMethod]
    public async Task A_string_body_becomes_the_problem_detail()
    {
        using var problem = await GetProblemAsync("/api/probe/not-found", HttpStatusCode.NotFound);

        Assert.AreEqual("Event not found", problem.RootElement.GetProperty("detail").GetString());
        Assert.AreEqual("Not Found", problem.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual("https://tools.ietf.org/html/rfc9110#section-15.5.5", problem.RootElement.GetProperty("type").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(problem.RootElement.GetProperty("traceId").GetString()));
    }

    [TestMethod]
    public async Task A_list_body_becomes_errors_and_a_joined_detail()
    {
        using var problem = await GetProblemAsync("/api/probe/bad-list", HttpStatusCode.BadRequest);

        CollectionAssert.AreEqual(new[] { "Name is required.", "Expiry is in the past." },
            problem.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual("Name is required. Expiry is in the past.", problem.RootElement.GetProperty("detail").GetString());
    }

    [TestMethod]
    public async Task A_contract_dto_keeps_its_members_at_the_top_level()
    {
        using var problem = await GetProblemAsync("/api/probe/typed", HttpStatusCode.Conflict);

        Assert.AreEqual("StillActive", problem.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("Revoke the key first.", problem.RootElement.GetProperty("errors")[0].GetString());
        Assert.AreEqual("Revoke the key first.", problem.RootElement.GetProperty("detail").GetString());
        Assert.AreEqual(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.AreEqual("Conflict", problem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task An_existing_problem_is_completed_not_replaced()
    {
        using var problem = await GetProblemAsync("/api/probe/problem", HttpStatusCode.Forbidden);

        Assert.AreEqual("Agent id mismatch", problem.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(403, problem.RootElement.GetProperty("status").GetInt32());
        Assert.IsTrue(problem.RootElement.TryGetProperty("traceId", out _));
    }

    [TestMethod]
    public async Task A_forbid_result_gets_a_problem_body()
    {
        using var problem = await GetProblemAsync("/api/probe/forbid", HttpStatusCode.Forbidden, authenticated: true);

        Assert.AreEqual("Forbidden", problem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task An_unauthenticated_call_gets_a_401_problem()
    {
        using var problem = await GetProblemAsync("/api/probe/protected", HttpStatusCode.Unauthorized);

        Assert.AreEqual("Unauthorized", problem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task An_antiforgery_refusal_carries_its_code()
    {
        using var problem = await GetProblemAsync("/api/probe/antiforgery", HttpStatusCode.BadRequest);

        Assert.AreEqual(ApiProblems.InvalidAntiforgeryTokenCode, problem.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task A_bare_status_code_gets_a_problem_body()
    {
        using var problem = await GetProblemAsync("/api/probe/unavailable", HttpStatusCode.ServiceUnavailable);

        Assert.AreEqual("Service Unavailable", problem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task An_exception_is_a_500_problem_that_hides_its_message_outside_development()
    {
        using var problem = await GetProblemAsync("/api/probe/throw", HttpStatusCode.InternalServerError);

        var detail = problem.RootElement.GetProperty("detail").GetString()!;
        Assert.IsFalse(detail.Contains("connection string", StringComparison.OrdinalIgnoreCase), detail);
    }

    [TestMethod]
    public async Task An_unmatched_api_route_is_a_404_problem_not_the_spa()
    {
        using var problem = await GetProblemAsync("/api/does-not-exist", HttpStatusCode.NotFound);

        Assert.AreEqual("Not Found", problem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task Paths_outside_api_keep_their_bodies()
    {
        await using var host = await ProbeHost.StartAsync();
        using var client = host.CreateClient();

        using var spa = await client.GetAsync("/some/spa/route");
        Assert.AreEqual(HttpStatusCode.OK, spa.StatusCode);
        Assert.AreEqual("spa", await spa.Content.ReadAsStringAsync());

        using var plain = await client.GetAsync("/probe/plain-not-found");
        Assert.AreEqual(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.AreNotEqual(ApiProblems.ContentType, plain.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task A_success_is_left_alone()
    {
        await using var host = await ProbeHost.StartAsync();
        using var client = host.CreateClient();

        using var ok = await client.GetAsync("/api/probe/ok");

        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        Assert.AreEqual("application/json", ok.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public void The_production_pipeline_wires_the_problem_middleware_ahead_of_routing_and_authentication()
    {
        var pipeline = File.ReadAllText(LocateWebAppFile("Startup.Pipeline.cs"));
        var problems = pipeline.IndexOf("app.UseNimBusApiProblemDetails()", StringComparison.Ordinal);
        var routing = pipeline.IndexOf("app.UseRouting()", StringComparison.Ordinal);
        var authentication = pipeline.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        var notFound = pipeline.IndexOf("endpoints.MapNimBusApiNotFound()", StringComparison.Ordinal);
        var spaFallback = pipeline.IndexOf("endpoints.MapFallbackToFile(\"index.html\"", StringComparison.Ordinal);

        Assert.IsTrue(problems > 0, "Startup.Pipeline.cs must call app.UseNimBusApiProblemDetails().");
        Assert.IsTrue(problems < routing && problems < authentication, "The problem middleware must run before routing and authentication.");
        Assert.IsTrue(notFound > 0 && notFound < spaFallback, "Unmatched /api routes must be mapped to 404 next to the SPA fallback.");

        var web = File.ReadAllText(LocateWebAppFile("Startup.Web.cs"));
        Assert.IsTrue(web.Contains("services.AddNimBusApiProblemDetails()", StringComparison.Ordinal),
            "Startup.Web.cs must register the problem result filter.");
    }

    private static async Task<JsonDocument> GetProblemAsync(string path, HttpStatusCode expected, bool authenticated = false)
    {
        await using var host = await ProbeHost.StartAsync();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.ParseAdd("application/json");
        if (authenticated)
            request.Headers.Add(TestAuthHeader, "alice@example.com");

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(expected, response.StatusCode, body);
        Assert.AreEqual(ApiProblems.ContentType, response.Content.Headers.ContentType?.MediaType, body);
        return JsonDocument.Parse(body);
    }

    private static string LocateWebAppFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(ApiProblemDetailsTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "NimBus.WebApp", relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(relativePath);
    }

    private sealed class ProbeHost : IAsyncDisposable
    {
        private readonly IHost _host;

        private ProbeHost(IHost host) => _host = host;

        public static async Task<ProbeHost> StartAsync()
        {
            var host = await new HostBuilder()
                .UseEnvironment(Environments.Production)
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices(services =>
                    {
                        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
                        services.AddRouting();
                        services
                            .AddAuthentication(TestAuthScheme)
                            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(TestAuthScheme, _ => { });
                        services.AddAuthorization();
                        services.AddNimBusApiProblemDetails();
                        services.AddControllers().ConfigureApplicationPartManager(parts =>
                        {
                            parts.ApplicationParts.Clear();
                            parts.ApplicationParts.Add(new AssemblyPart(typeof(ProblemProbeController).Assembly));
                            parts.FeatureProviders.Add(new OnlyProbeController());
                        });
                    });
                    web.Configure(app =>
                    {
                        app.UseNimBusApiProblemDetails();
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                            endpoints.MapNimBusApiNotFound();
                            endpoints.MapFallback(context => context.Response.WriteAsync("spa"));
                        });
                    });
                })
                .StartAsync();
            return new ProbeHost(host);
        }

        public HttpClient CreateClient() => _host.GetTestServer().CreateClient();

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class OnlyProbeController : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            feature.Controllers.Clear();
            feature.Controllers.Add(typeof(ProblemProbeController).GetTypeInfo());
        }
    }

    private sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(TestAuthHeader, out var user) || string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

/// <summary>One action per error shape the WebApp's implementations return.</summary>
public sealed class ProblemProbeController : Controller
{
    [HttpGet("api/probe/ok")]
    public IActionResult Ok200() => new OkObjectResult(new { fine = true });

    [HttpGet("api/probe/not-found")]
    public IActionResult NotFoundString() => new NotFoundObjectResult("Event not found");

    [HttpGet("api/probe/bad-list")]
    public IActionResult BadList() => new BadRequestObjectResult(new List<string> { "Name is required.", "Expiry is in the past." });

    [HttpGet("api/probe/typed")]
    public IActionResult Typed() => new ObjectResult(new ManagementApi.ProblemDetails
    {
        Code = "StillActive",
        Errors = ["Revoke the key first."],
    })
    { StatusCode = StatusCodes.Status409Conflict };

    [HttpGet("api/probe/problem")]
    public IActionResult ExistingProblem() => new ObjectResult(new ProblemDetails { Title = "Agent id mismatch" })
    { StatusCode = StatusCodes.Status403Forbidden };

    [HttpGet("api/probe/forbid")]
    [Authorize]
    public IActionResult ForbidCaller() => new ForbidResult();

    [HttpGet("api/probe/protected")]
    [Authorize]
    public IActionResult Protected() => new OkResult();

    // A GET: the filter maps the refusal result whatever the method, and a probe POST that skips
    // token validation would itself be a CSRF finding.
    [HttpGet("api/probe/antiforgery")]
    public IActionResult Antiforgery() => new AntiforgeryValidationFailedResult();

    [HttpGet("api/probe/unavailable")]
    public IActionResult Unavailable() => new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);

    [HttpGet("api/probe/throw")]
    public IActionResult Throw() => throw new InvalidOperationException("The connection string Server=secret is invalid.");

    [HttpGet("probe/plain-not-found")]
    public IActionResult PlainNotFound() => new NotFoundObjectResult("plain");
}
