#pragma warning disable CA1707, CA2007

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Middleware;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Tests covering how the WebApp serves the built SPA bundle after the removal of the
/// deprecated <c>Microsoft.AspNetCore.SpaServices.Extensions</c> package (GH#89):
/// plain <c>UseStaticFiles</c> over <c>ClientApp/build/public</c> plus the
/// <c>MapFallbackToFile("index.html")</c> endpoint for deep-link client-side routes.
///
/// Following the suite's established approach (see <see cref="ResponseCompressionTests"/>),
/// the fixture mirrors the exact static-file / fallback wiring from
/// `src/NimBus.WebApp/Startup.cs` in a minimal in-process host (the production Startup
/// needs Azure Service Bus + storage configuration at boot), and a companion
/// source-scraping test pins the real Startup.cs to that wiring so the mirror cannot
/// silently drift from production.
/// </summary>
[TestClass]
public class SpaFallbackTests
{
    private const string IndexSentinel = "<!doctype html><html><body>spa-index-sentinel</body></html>";
    private const string HashedAssetBody = "console.log('hashed-asset-sentinel');";
    private const string AuthenticatedHeader = "X-Test-Authenticated";
    private const string LoginPath = "/account/login";

    private static string _spaRoot = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext _)
    {
        // Stage a stand-in for ClientApp/build/public: index.html at the root plus a
        // Vite-style content-hashed asset under assets/.
        _spaRoot = Path.Combine(Path.GetTempPath(), "nimbus-spa-fallback-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_spaRoot, "assets"));
        File.WriteAllText(Path.Combine(_spaRoot, "index.html"), IndexSentinel);
        File.WriteAllText(Path.Combine(_spaRoot, "assets", "index-abc123.js"), HashedAssetBody);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        try
        {
            Directory.Delete(_spaRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private static IHost BuildTestHost()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services => services.AddRouting());
                web.Configure(app =>
                {
                    // Stand-in for UseAuthentication: an authenticated principal when the
                    // test opts in via header, the anonymous default otherwise.
                    app.Use(async (ctx, next) =>
                    {
                        if (ctx.Request.Headers.ContainsKey(AuthenticatedHeader))
                        {
                            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                                new[] { new Claim(ClaimTypes.Name, "test-user") }, "TestAuth"));
                        }

                        await next();
                    });

                    app.UseRouting();

                    // === Mirror src/NimBus.WebApp/Startup.cs SPA static-file serving ===
                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(_spaRoot),
                        ContentTypeProvider = new PrecompressedContentTypeProvider(),
                        OnPrepareResponse = ctx =>
                        {
                            var path = ctx.Context.Request.Path.Value ?? string.Empty;
                            var headers = ctx.Context.Response.GetTypedHeaders();
                            if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                            {
                                headers.CacheControl = new CacheControlHeaderValue
                                {
                                    Public = true,
                                    MaxAge = TimeSpan.FromDays(365),
                                    Extensions = { new NameValueHeaderValue("immutable") },
                                };
                            }
                            else
                            {
                                headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                            }
                        },
                    });

                    // === Mirror src/NimBus.WebApp/Startup.cs deep-link fallback endpoint ===
                    app.UseEndpoints(endpoints =>
                    {
                        var fallbackOptions = new StaticFileOptions
                        {
                            FileProvider = new PhysicalFileProvider(_spaRoot),
                            OnPrepareResponse = ctx =>
                            {
                                ctx.Context.Response.GetTypedHeaders().CacheControl =
                                    new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };

                                if (!ctx.Context.User.Identity!.IsAuthenticated)
                                {
                                    ctx.Context.Response.Redirect(LoginPath);
                                }
                            },
                        };

                        var monitorShellOptions = new StaticFileOptions
                        {
                            FileProvider = new PhysicalFileProvider(_spaRoot),
                            OnPrepareResponse = ctx => ctx.Context.Response.GetTypedHeaders().CacheControl =
                                new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true },
                        };

                        endpoints.MapFallbackToFile("Monitor", "index.html", monitorShellOptions);
                        endpoints.MapFallbackToFile("index.html", fallbackOptions);
                    });
                });
            });

        return builder.Start();
    }

    private static async Task<HttpResponseMessage> GetAsync(IHost host, string path, bool authenticated)
    {
        var client = host.GetTestServer().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authenticated)
        {
            request.Headers.TryAddWithoutValidation(AuthenticatedHeader, "1");
        }

        return await client.SendAsync(request);
    }

    /// <summary>The built bundle's hashed assets are served from the SPA root by the
    /// static-file middleware, with the immutable cache policy.</summary>
    [TestMethod]
    public async Task HashedAsset_IsServedFromSpaRoot_WithImmutableCacheHeaders()
    {
        using var host = BuildTestHost();
        using var response = await GetAsync(host, "/assets/index-abc123.js", authenticated: false);

        response.EnsureSuccessStatusCode();
        Assert.AreEqual(HashedAssetBody, await response.Content.ReadAsStringAsync());

        var cacheControl = response.Headers.CacheControl;
        Assert.IsNotNull(cacheControl, "Expected a Cache-Control header on the hashed asset response.");
        Assert.IsTrue(cacheControl.Public, "Hashed assets must be publicly cacheable.");
        Assert.AreEqual(TimeSpan.FromDays(365), cacheControl.MaxAge);
        Assert.IsTrue(
            cacheControl.Extensions.Any(e => e.Name == "immutable"),
            "Hashed assets must carry the `immutable` cache extension.");
    }

    /// <summary>A deep-link client-side route (no matching file or endpoint) falls back
    /// to index.html for an authenticated user, with no-cache headers so a deploy is
    /// always picked up.</summary>
    [TestMethod]
    public async Task DeepLink_Authenticated_FallsBackToIndexHtml()
    {
        using var host = BuildTestHost();
        using var response = await GetAsync(host, "/endpoints/some-endpoint/messages", authenticated: true);

        response.EnsureSuccessStatusCode();
        Assert.AreEqual(IndexSentinel, await response.Content.ReadAsStringAsync());

        var cacheControl = response.Headers.CacheControl;
        Assert.IsNotNull(cacheControl, "Expected a Cache-Control header on the fallback response.");
        Assert.IsTrue(cacheControl.NoCache && cacheControl.NoStore, "index.html must never be cached.");
    }

    /// <summary>An anonymous deep link is redirected to the login page instead of
    /// receiving the SPA shell.</summary>
    [TestMethod]
    public async Task DeepLink_Anonymous_RedirectsToLogin()
    {
        using var host = BuildTestHost();
        using var response = await GetAsync(host, "/endpoints/some-endpoint/messages", authenticated: false);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual(LoginPath, response.Headers.Location?.OriginalString);
    }

    /// <summary>The Monitor wall's shell loads without a session (a dashboard token travels in
    /// the URL fragment, which the server never sees), in any letter case, and is never cached.</summary>
    [TestMethod]
    [DataRow("/Monitor")]
    [DataRow("/monitor")]
    public async Task Monitor_Anonymous_GetsTheShell(string path)
    {
        using var host = BuildTestHost();
        using var response = await GetAsync(host, path, authenticated: false);

        response.EnsureSuccessStatusCode();
        Assert.AreEqual(IndexSentinel, await response.Content.ReadAsStringAsync());
        var cacheControl = response.Headers.CacheControl;
        Assert.IsTrue(cacheControl!.NoCache && cacheControl.NoStore, "the shell must never be cached.");
    }

    /// <summary>Only the exact Monitor route is open: anything below it, and every other route,
    /// still redirects an anonymous browser to the login page.</summary>
    [TestMethod]
    [DataRow("/Monitor/x")]
    [DataRow("/Endpoints")]
    [DataRow("/")]
    public async Task Other_routes_Anonymous_StillRedirectToLogin(string path)
    {
        using var host = BuildTestHost();
        using var response = await GetAsync(host, path, authenticated: false);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, path);
        Assert.AreEqual(LoginPath, response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// Pins the real `Startup.cs` to the wiring this fixture mirrors: SPA serving goes
    /// through plain `app.UseStaticFiles` plus `MapFallbackToFile("index.html", ...)`,
    /// and the deprecated SpaServices.Extensions surface (`AddSpaStaticFiles` /
    /// `UseSpaStaticFiles`) and preview-only `AddRazorRuntimeCompilation` stay removed
    /// (GH#89).
    /// </summary>
    [TestMethod]
    public void Startup_ServesSpaViaUseStaticFiles_WithoutSpaServicesOrRuntimeCompilation()
    {
        // Startup is split across partial files (Startup.*.cs); check them all.
        string startupPath = LocateStartupSource();
        string source = string.Concat(Directory
            .GetFiles(Path.GetDirectoryName(startupPath)!, "Startup*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));

        Assert.IsTrue(
            source.Contains("app.UseStaticFiles", StringComparison.Ordinal),
            "Startup.cs must serve the SPA bundle via `app.UseStaticFiles(...)`.");
        Assert.IsTrue(
            source.Contains("endpoints.MapFallbackToFile(\"index.html\"", StringComparison.Ordinal),
            "Startup.cs must keep the deep-link fallback to index.html.");
        Assert.IsTrue(
            source.Contains("endpoints.MapFallbackToFile(\"Monitor\", \"index.html\", monitorShellOptions)", StringComparison.Ordinal),
            "Startup.cs must keep the Monitor shell's own fallback, which serves it without a session.");
        Assert.IsFalse(
            source.Contains("SpaStaticFiles", StringComparison.Ordinal),
            "Startup.cs must not use the deprecated SpaServices.Extensions surface (AddSpaStaticFiles/UseSpaStaticFiles) — GH#89.");
        Assert.IsFalse(
            source.Contains("AddRazorRuntimeCompilation", StringComparison.Ordinal),
            "Startup.cs must not call AddRazorRuntimeCompilation — all Razor surfaces are build-time compiled (GH#89).");
    }

    private static string LocateStartupSource()
    {
        string? dir = Path.GetDirectoryName(typeof(SpaFallbackTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "src", "NimBus.WebApp", "Startup.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(
            "Could not locate src/NimBus.WebApp/Startup.cs by walking up from the test assembly directory.");
    }
}
