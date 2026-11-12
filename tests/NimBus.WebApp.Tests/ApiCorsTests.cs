#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Cors;
using NimBus.WebApp.Problems;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The REST API answers cross-origin browser calls only from the origins in
/// <c>Cors:AllowedOrigins</c>, never with credentials, and only under <c>/api</c>. With no
/// origins configured there is no CORS at all, as before.
/// </summary>
[TestClass]
public class ApiCorsTests
{
    private const string Allowed = "https://portal.example.com";

    [TestMethod]
    public async Task An_allowed_origin_gets_a_preflight_answer_without_credentials_ahead_of_sign_in()
    {
        await using var host = await CorsHost.StartAsync(Allowed);
        using var response = await host.Client.SendAsync(Preflight("/api/v1/probe", Allowed, "Authorization"));

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, "A preflight must not be turned into a 401.");
        Assert.AreEqual(Allowed, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Credentials"), "Credentials are never allowed.");
        StringAssert.Contains(response.Headers.GetValues("Access-Control-Allow-Headers").Single(), "Authorization");
        Assert.AreEqual("600", response.Headers.GetValues("Access-Control-Max-Age").Single());
    }

    [TestMethod]
    public async Task An_actual_request_from_an_allowed_origin_exposes_the_api_headers()
    {
        await using var host = await CorsHost.StartAsync(Allowed);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/probe");
        request.Headers.Add("Origin", Allowed);
        request.Headers.Add("X-Test-Auth", "alice");

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(Allowed, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers").Single();
        foreach (var header in ApiCorsExtensions.ExposedHeaders)
            StringAssert.Contains(exposed, header);
    }

    [TestMethod]
    public async Task A_500_problem_keeps_its_cors_headers_so_the_caller_can_read_it()
    {
        await using var host = await CorsHost.StartAsync(Allowed);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/throw");
        request.Headers.Add("Origin", Allowed);

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(Allowed, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [TestMethod]
    public async Task An_unlisted_origin_gets_no_cors_headers()
    {
        await using var host = await CorsHost.StartAsync(Allowed);
        using var response = await host.Client.SendAsync(Preflight("/api/v1/probe", "https://evil.example.net", "Authorization"));

        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task Paths_outside_api_get_no_cors_headers()
    {
        await using var host = await CorsHost.StartAsync(Allowed);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/other");
        request.Headers.Add("Origin", Allowed);

        using var response = await host.Client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task Without_configured_origins_there_is_no_cors()
    {
        await using var host = await CorsHost.StartAsync();
        using var response = await host.Client.SendAsync(Preflight("/api/v1/probe", Allowed, "Authorization"));

        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.AreEqual(0, host.Services.GetServices<Microsoft.AspNetCore.Cors.Infrastructure.ICorsService>().Count(),
            "CORS is not even registered.");
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow(" ")]
    [DataRow("portal.example.com")]
    [DataRow("https://portal.example.com/app")]
    [DataRow("https://portal.example.com/")]
    [DataRow("https://user@portal.example.com")]
    [DataRow("ftp://portal.example.com")]
    [DataRow("http://portal.example.com")]
    public void An_invalid_origin_fails_at_startup(string origin)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => ApiCorsExtensions.ReadOrigins(Config(origin), Environment(Environments.Production)));
        StringAssert.Contains(exception.Message, ApiCorsExtensions.AllowedOriginsKey);
    }

    [TestMethod]
    public void Http_origins_are_allowed_in_development_and_origins_are_normalised()
    {
        var origins = ApiCorsExtensions.ReadOrigins(
            Config("http://localhost:5173", "https://Portal.Example.com", "https://portal.example.com:443"),
            Environment(Environments.Development));

        CollectionAssert.AreEqual(new[] { "http://localhost:5173", "https://portal.example.com" }, origins.ToArray());
    }

    [TestMethod]
    public void The_production_pipeline_applies_cors_between_routing_and_authentication()
    {
        var pipeline = File.ReadAllText(LocateWebAppFile("Startup.Pipeline.cs"));
        var routing = pipeline.IndexOf("app.UseRouting()", StringComparison.Ordinal);
        var cors = pipeline.IndexOf("app.UseNimBusApiCors()", StringComparison.Ordinal);
        var authentication = pipeline.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        Assert.IsTrue(routing > 0 && routing < cors && cors < authentication);

        var services = string.Concat(Directory.GetFiles(Path.GetDirectoryName(LocateWebAppFile("Startup.cs"))!, "Startup*.cs").Select(File.ReadAllText));
        Assert.IsTrue(services.Contains("services.AddNimBusApiCors(Configuration, Env)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void The_deployment_template_keeps_operator_set_origins_across_a_redeploy()
    {
        var bicep = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(LocateWebAppFile("Startup.cs"))))!,
            "deploy", "bicep", "deploy.webapp.bicep"));

        StringAssert.Contains(bicep, "startsWith(setting.key, 'Cors__')",
            "App-settings deployment is a full replace; Cors__AllowedOrigins__* must be carried forward.");
    }

    private static HttpRequestMessage Preflight(string path, string origin, string requestHeaders)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", requestHeaders);
        return request;
    }

    private static IConfiguration Config(params string[] origins)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(origins.Select((o, i) => new KeyValuePair<string, string?>($"{ApiCorsExtensions.AllowedOriginsKey}:{i}", o)))
            .Build();

    private static IHostEnvironment Environment(string name) => new HostingEnvironment { EnvironmentName = name };

    private static string LocateWebAppFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(ApiCorsTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "NimBus.WebApp", relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(relativePath);
    }

    private sealed class CorsHost : IAsyncDisposable
    {
        private readonly IHost _host;

        private CorsHost(IHost host)
        {
            _host = host;
            Client = host.GetTestServer().CreateClient();
        }

        public HttpClient Client { get; }

        public IServiceProvider Services => _host.Services;

        public static async Task<CorsHost> StartAsync(params string[] origins)
        {
            var configuration = Config(origins);
            var host = await new HostBuilder()
                .UseEnvironment(Environments.Production)
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.ConfigureServices((context, services) =>
                    {
                        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
                        services.AddRouting();
                        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuth>("Test", _ => { });
                        services.AddAuthorization();
                        services.AddNimBusApiCors(configuration, context.HostingEnvironment);
                    });
                    web.Configure(app =>
                    {
                        app.UseNimBusApiProblemDetails();
                        app.UseRouting();
                        app.UseNimBusApiCors();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/api/v1/probe", () => "ok").RequireAuthorization();
                            endpoints.MapGet("/other", () => "ok");
                            endpoints.MapGet("/api/v1/throw", (Func<string>)(() => throw new InvalidOperationException("boom")));
                        });
                    });
                })
                .StartAsync();
            return new CorsHost(host);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private sealed class HeaderAuth : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuth(Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder)
            : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Auth", out var user) || string.IsNullOrEmpty(user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
