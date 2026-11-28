#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Middleware;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Tests for <see cref="SecurityHeadersMiddleware"/>: the nonce-based script policy, the
/// pinned connect-src built from the Application Insights connection string, the Swagger
/// exemption and the unchanged non-CSP headers.
/// </summary>
[TestClass]
public class SecurityHeadersMiddlewareTests
{
    private const string EnvKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    private const string ConfigKey = "ApplicationInsights:ConnectionString";
    private const string WestEurope =
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;" +
        "IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/;" +
        "LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;" +
        "ApplicationId=11111111-1111-1111-1111-111111111111";

    private static readonly Regex NonceRegex = new("'nonce-([^']+)'", RegexOptions.Compiled);

    private static SecurityHeadersMiddleware CreateMiddleware(
        IDictionary<string, string?>? settings = null, RequestDelegate? next = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        return new SecurityHeadersMiddleware(next ?? (_ => Task.CompletedTask), configuration);
    }

    private static async Task<HttpContext> InvokeAsync(SecurityHeadersMiddleware middleware, string path = "/")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        await middleware.InvokeAsync(context);
        return context;
    }

    private static string Csp(HttpContext context) => context.Response.Headers["Content-Security-Policy"].ToString();

    private static string Directive(string csp, string name) =>
        csp.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Single(d => d.StartsWith(name + " ", StringComparison.Ordinal) || d == name);

    private static string Nonce(string csp) => NonceRegex.Match(csp).Groups[1].Value;

    [TestMethod]
    [DataRow("/")]
    [DataRow("/api/endpoints")]
    [DataRow("/Monitor")]
    public async Task ScriptSrc_IsSelfAndNonceOnly(string path)
    {
        var context = await InvokeAsync(CreateMiddleware(), path);
        var csp = Csp(context);
        var nonce = Nonce(csp);

        Assert.IsFalse(string.IsNullOrEmpty(nonce));
        Assert.AreEqual($"script-src 'self' 'nonce-{nonce}'", Directive(csp, "script-src"));
        Assert.IsFalse(csp.Contains("'unsafe-eval'", StringComparison.Ordinal));
        Assert.IsFalse(Directive(csp, "script-src").Contains("'unsafe-inline'", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FullPolicy_WithoutApplicationInsights_IsExact()
    {
        var context = await InvokeAsync(CreateMiddleware());
        var csp = Csp(context);
        var nonce = Nonce(csp);

        Assert.AreEqual(
            "default-src 'self'; " +
            $"script-src 'self' 'nonce-{nonce}'; " +
            "style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data: https:; " +
            "font-src 'self'; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none';",
            csp);
    }

    [TestMethod]
    public async Task Nonce_IsBase64OfAtLeast16Bytes()
    {
        var context = await InvokeAsync(CreateMiddleware());
        var bytes = Convert.FromBase64String(Nonce(Csp(context)));

        Assert.IsTrue(bytes.Length >= 16, $"Nonce decodes to {bytes.Length} bytes.");
    }

    [TestMethod]
    public async Task Nonce_DiffersPerRequest()
    {
        var middleware = CreateMiddleware();
        var nonces = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 200; i++)
        {
            var context = await InvokeAsync(middleware);
            Assert.IsTrue(nonces.Add(Nonce(Csp(context))), "A nonce was reused across requests.");
        }
    }

    [TestMethod]
    public async Task GetCspNonce_ReturnsHeaderNonce_DuringRequest()
    {
        string? seenByNext = null;
        var middleware = CreateMiddleware(next: ctx =>
        {
            seenByNext = ctx.GetCspNonce();
            return Task.CompletedTask;
        });

        var context = await InvokeAsync(middleware);

        Assert.AreEqual(Nonce(Csp(context)), seenByNext);
        Assert.AreEqual(seenByNext, context.GetCspNonce());
    }

    [TestMethod]
    public async Task GetCspNonce_ReturnsNull_OnExemptPath()
    {
        string? seenByNext = "unset";
        var middleware = CreateMiddleware(next: ctx =>
        {
            seenByNext = ctx.GetCspNonce();
            return Task.CompletedTask;
        });

        await InvokeAsync(middleware, "/swagger/index.html");

        Assert.IsNull(seenByNext);
        Assert.IsNull(new DefaultHttpContext().GetCspNonce());
    }

    [TestMethod]
    [DataRow("/swagger")]
    [DataRow("/Swagger/index.html")]
    [DataRow("/api-docs/v1")]
    [DataRow("/API-DOCS/v1")]
    public async Task ExemptPaths_HaveNoCsp_ButOtherHeaders(string path)
    {
        var context = await InvokeAsync(CreateMiddleware(), path);
        var headers = context.Response.Headers;

        Assert.IsFalse(headers.ContainsKey("Content-Security-Policy"));
        Assert.AreEqual("DENY", headers["X-Frame-Options"].ToString());
        Assert.AreEqual("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.IsTrue(headers.ContainsKey("Permissions-Policy"));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/swagger/index.html")]
    public async Task OtherSecurityHeaders_AreUnchanged(string path)
    {
        var context = await InvokeAsync(CreateMiddleware(), path);
        var headers = context.Response.Headers;

        Assert.AreEqual("DENY", headers["X-Frame-Options"].ToString());
        Assert.AreEqual("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.AreEqual("0", headers["X-XSS-Protection"].ToString());
        Assert.AreEqual("strict-origin-when-cross-origin", headers["Referrer-Policy"].ToString());
        Assert.AreEqual(
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()",
            headers["Permissions-Policy"].ToString());
    }

    [TestMethod]
    public async Task ConnectSrc_EnvironmentKey_ListsIngestionThenLiveOrigins()
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?> { [EnvKey] = WestEurope }));

        Assert.AreEqual(
            "connect-src 'self' https://westeurope-5.in.applicationinsights.azure.com https://westeurope.livediagnostics.monitor.azure.com",
            Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    public async Task ConnectSrc_ConfigKeyOnly_IsUsed()
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?>
        {
            [ConfigKey] = "IngestionEndpoint=https://config.example.com/v2/track",
        }));

        Assert.AreEqual("connect-src 'self' https://config.example.com", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task ConnectSrc_BlankEnvironmentKey_FallsBackToConfigKey(string envValue)
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?>
        {
            [EnvKey] = envValue,
            [ConfigKey] = "IngestionEndpoint=https://config.example.com/",
        }));

        Assert.AreEqual("connect-src 'self' https://config.example.com", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    public async Task ConnectSrc_BothKeys_EnvironmentKeyWins()
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?>
        {
            [EnvKey] = "IngestionEndpoint=https://env.example.com/",
            [ConfigKey] = "IngestionEndpoint=https://config.example.com/",
        }));

        Assert.AreEqual("connect-src 'self' https://env.example.com", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    [DataRow("ingestionendpoint=https://a.example.com/;LIVEENDPOINT=https://b.example.com/")]
    [DataRow(" IngestionEndpoint = https://a.example.com/ ; LiveEndpoint = https://b.example.com/ ")]
    public async Task ConnectSrc_KeysCaseInsensitiveAndTrimmed(string connectionString)
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?> { [EnvKey] = connectionString }));

        Assert.AreEqual("connect-src 'self' https://a.example.com https://b.example.com", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    [DataRow("IngestionEndpoint=http://insecure.example.com/")]
    [DataRow("IngestionEndpoint=/relative/path")]
    [DataRow("IngestionEndpoint=")]
    [DataRow("IngestionEndpoint=https://")]
    [DataRow("IngestionEndpoint=wss://socket.example.com/")]
    [DataRow("not a connection string")]
    [DataRow("=;;=;Unknown=https://unknown.example.com/")]
    [DataRow("EndpointSuffix=applicationinsights.azure.com;InstrumentationKey=abc")]
    [DataRow("InstrumentationKey=abc")]
    public async Task ConnectSrc_InvalidOrMissingEndpoints_ContributeNothing(string connectionString)
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?> { [EnvKey] = connectionString }));

        Assert.AreEqual("connect-src 'self'", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    public async Task ConnectSrc_DuplicateOrigins_ListedOnce()
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?>
        {
            [EnvKey] = "IngestionEndpoint=https://same.example.com/ingest;LiveEndpoint=https://SAME.example.com/live",
        }));

        Assert.AreEqual("connect-src 'self' https://same.example.com", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    public async Task ConnectSrc_NonDefaultPort_IsKept()
    {
        var context = await InvokeAsync(CreateMiddleware(new Dictionary<string, string?>
        {
            [EnvKey] = "IngestionEndpoint=https://ingest.example.com:8443/path?x=1",
        }));

        Assert.AreEqual("connect-src 'self' https://ingest.example.com:8443", Directive(Csp(context), "connect-src"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(WestEurope)]
    [DataRow("IngestionEndpoint=http://insecure.example.com/;LiveEndpoint=/x")]
    public async Task ConnectSrc_NeverContainsSchemeOrWildcardSources(string? connectionString)
    {
        var settings = new Dictionary<string, string?>();
        if (connectionString is not null)
        {
            settings[EnvKey] = connectionString;
        }

        var context = await InvokeAsync(CreateMiddleware(settings));
        var sources = Directive(Csp(context), "connect-src").Split(' ').Skip(1).ToArray();

        foreach (var source in sources)
        {
            Assert.IsFalse(source is "https:" or "wss:" or "ws:" or "http:" or "*", $"Scheme/wildcard source '{source}'.");
            Assert.IsFalse(source.StartsWith("ws://", StringComparison.OrdinalIgnoreCase), source);
            Assert.IsFalse(source.StartsWith("wss://", StringComparison.OrdinalIgnoreCase), source);
            Assert.IsFalse(source.StartsWith("http://", StringComparison.OrdinalIgnoreCase), source);
        }
    }
}
