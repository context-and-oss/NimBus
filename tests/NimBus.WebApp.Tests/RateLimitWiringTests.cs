#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// AC-1/AC-2/AC-3: the rate limiter is registered exactly once, sits in the
/// right place in the pipeline, exposes the four policies by name, and does not
/// leak hand-written attributes into the NSwag-generated controllers.
/// These tests issue no throttled traffic — enforcement lives in
/// <see cref="RateLimitEnforcementTests"/> and <see cref="LoginRateLimitTests"/>.
/// </summary>
[TestClass]
public class RateLimitWiringTests
{
    [TestMethod]
    public void UseRateLimiter_runs_after_UseRouting_and_before_UseEndpoints()
    {
        // Source-order introspection, the same technique ResponseCompressionTests
        // uses for its own middleware-ordering invariant: a future refactor that
        // moves the call must not silently unbind the endpoint policies.
        var source = File.ReadAllText(LocateWebAppFile("Startup.Pipeline.cs"));

        var routing = source.IndexOf("app.UseRouting()", StringComparison.Ordinal);
        var limiter = source.IndexOf("app.UseRateLimiter()", StringComparison.Ordinal);
        var endpoints = source.IndexOf("app.UseEndpoints(", StringComparison.Ordinal);

        Assert.IsTrue(routing > 0, "Startup.cs is missing `app.UseRouting()`.");
        Assert.IsTrue(limiter > 0, "AC-1: Startup.cs is missing `app.UseRateLimiter()`.");
        Assert.IsTrue(endpoints > 0, "Startup.cs is missing `app.UseEndpoints(`.");

        Assert.IsTrue(
            routing < limiter,
            "AC-1: `app.UseRateLimiter()` MUST run after `app.UseRouting()` — before routing there is no endpoint, so no endpoint policy resolves.");
        Assert.IsTrue(
            limiter < endpoints,
            "AC-1: `app.UseRateLimiter()` MUST run before `app.UseEndpoints(...)` — after it the endpoint has already executed.");
    }

    [TestMethod]
    public void Rate_limiting_is_registered_exactly_once()
    {
        // Startup is split across partial files (Startup.*.cs); count across all of them.
        var startupDirectory = Path.GetDirectoryName(LocateWebAppFile("Startup.cs"))!;
        var startup = string.Concat(Directory
            .GetFiles(startupDirectory, "Startup*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));
        Assert.AreEqual(
            1,
            CountOccurrences(startup, "AddNimBusRateLimiting("),
            "AC-1: Startup.cs must call AddNimBusRateLimiting exactly once.");

        // Asserting across the whole project, not just the extension file, is
        // what catches a second AddRateLimiter added elsewhere later.
        var total = EnumerateWebAppSources()
            .Sum(path => CountOccurrences(File.ReadAllText(path), "AddRateLimiter("));

        Assert.AreEqual(
            1,
            total,
            "AC-1: `AddRateLimiter(` must appear exactly once across all non-generated NimBus.WebApp sources.");
    }

    [TestMethod]
    public async Task All_four_policies_are_reachable_by_name()
    {
        // AC-2. A policy name that is not registered makes the rate-limiting
        // middleware throw when the endpoint is invoked, so a 200 on each of the
        // four is proof that each name resolves.
        using var host = await BuildPolicyProbeHostAsync(new Dictionary<string, string?>());
        using var client = host.GetTestServer().CreateClient();

        foreach (var policy in new[]
        {
            RateLimitPolicyNames.AgentReceive,
            RateLimitPolicyNames.Admin,
            RateLimitPolicyNames.Search,
            RateLimitPolicyNames.Login,
        })
        {
            using var response = await client.GetAsync("/probe/" + policy);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Policy '{policy}' did not resolve by name.");
        }

        await host.StopAsync();
    }

    [TestMethod]
    public void A_global_limiter_is_registered_unless_rate_limiting_is_off()
    {
        // The global limiter covers /api only; RateLimitEnforcementTests shows the hub and
        // health probes stay unthrottled under it (AC-10).
        var provider = BuildProvider(new Dictionary<string, string?>());
        var options = provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        Assert.IsNotNull(options.GlobalLimiter, "Every /api route needs a ceiling.");
        var disabled = BuildProvider(new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "false" })
            .GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.IsNull(disabled.GlobalLimiter, "The kill switch turns the global limit off too.");
        Assert.AreEqual(
            (int)HttpStatusCode.TooManyRequests,
            options.RejectionStatusCode,
            "The framework default is 503; AC-4/6/7/8 all require 429.");
    }

    [TestMethod]
    public void Defaults_are_the_documented_values()
    {
        var provider = BuildProvider(new Dictionary<string, string?>());
        var options = provider.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        Assert.IsTrue(options.Enabled);
        Assert.IsFalse(options.TrustForwardedForHeader, "Forwarded headers must be opt-in per deployment topology.");
        Assert.AreEqual(20, options.AgentReceive.PermitLimit);
        Assert.AreEqual(5, options.AgentReceive.QueueLimit);
        Assert.AreEqual(60, options.Admin.PermitLimit);
        Assert.AreEqual(60, options.Admin.WindowSeconds);
        Assert.AreEqual(120, options.Search.PermitLimit);
        Assert.AreEqual(60, options.Search.WindowSeconds);
        Assert.AreEqual(50, options.Login.PermitLimit);
        Assert.AreEqual(300, options.Login.WindowSeconds);
        Assert.AreEqual(128, options.Login.IPv6PrefixBits, "128 = the full address, i.e. per-client-IP as AC-2 requires.");
        Assert.AreEqual(600, options.Global.PermitLimit);
        Assert.AreEqual(60, options.Global.WindowSeconds);
    }

    [TestMethod]
    public void Configuration_overrides_the_defaults()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["RateLimiting:Login:PermitLimit"] = "7",
            ["RateLimiting:TrustForwardedForHeader"] = "true",
        });
        var options = provider.GetRequiredService<IOptions<RateLimitOptions>>().Value;

        Assert.AreEqual(7, options.Login.PermitLimit);
        Assert.IsTrue(options.TrustForwardedForHeader);
        Assert.AreEqual(300, options.Login.WindowSeconds, "An unset key must keep its default.");
    }

    [TestMethod]
    public void Generated_controllers_carry_no_hand_written_rate_limiting()
    {
        // AC-3: ApiContract.g.cs is regenerated by NSwag on every build, so an
        // attribute added there would be erased. The policies are attached by an
        // application-model convention instead.
        var generated = File.ReadAllText(LocateWebAppFile(Path.Combine("Controllers", "ApiContract.g.cs")));

        StringAssert.DoesNotMatch(
            generated,
            new System.Text.RegularExpressions.Regex("EnableRateLimiting"),
            "AC-3: no rate-limiting attribute may be hand-added to the generated controllers.");
        StringAssert.DoesNotMatch(
            generated,
            new System.Text.RegularExpressions.Regex("RateLimiting"),
            "AC-3: the generated file must stay free of rate-limiting edits.");
    }

    [TestMethod]
    public async Task Kill_switch_still_registers_every_policy()
    {
        // Getting this backwards turns "disable the limits" into a crash loop:
        // parameterless UseRateLimiter() resolves IOptions<RateLimiterOptions>
        // and throws at startup if AddRateLimiter never ran. Disabled means
        // "attach no metadata", never "register nothing".
        using var host = await BuildPolicyProbeHostAsync(new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "false",
        });
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.GetAsync("/probe/" + RateLimitPolicyNames.Login);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "Policies must stay registered when the kill switch is off.");

        await host.StopAsync();
    }

    [TestMethod]
    public void Deployment_template_preserves_operator_tuned_limits()
    {
        // App settings are a full replace on deploy. Without the RateLimiting__
        // prefix in the preservation filter an operator's tuned limit is silently
        // restored to the shipped default on the next `nb infra apply`.
        var bicep = File.ReadAllText(LocateRepoFile(Path.Combine("deploy", "bicep", "deploy.webapp.bicep")));

        StringAssert.Contains(
            bicep,
            "RateLimiting__",
            "deploy.webapp.bicep must carry operator-set RateLimiting__* app settings across a redeploy.");
    }

    [TestMethod]
    public void Rest_api_gap_list_no_longer_claims_there_is_no_rate_limiting()
    {
        var doc = File.ReadAllText(LocateRepoFile(Path.Combine("docs", "webapp-rest-api.md")));

        StringAssert.DoesNotMatch(
            doc,
            new System.Text.RegularExpressions.Regex("No `RateLimiter` middleware is currently configured"),
            "docs/webapp-rest-api.md still claims the API has no rate limiting — that went false when this shipped.");
        StringAssert.Contains(doc, "rate-limiting.md", "The gap item should link the new reference page.");
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddNimBusRateLimiting(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Minimal host whose only endpoints are one per policy, each requiring that
    /// policy by name. Proves the names resolve without dragging in controllers.
    /// </summary>
    private static async Task<IHost> BuildPolicyProbeHostAsync(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddNimBusRateLimiting(configuration);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints =>
                    {
                        foreach (var policy in new[]
                        {
                            RateLimitPolicyNames.AgentReceive,
                            RateLimitPolicyNames.Admin,
                            RateLimitPolicyNames.Search,
                            RateLimitPolicyNames.Login,
                        })
                        {
                            endpoints.MapGet("/probe/" + policy, () => "ok").RequireRateLimiting(policy);
                        }
                    });
                });
            })
            .StartAsync();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static IEnumerable<string> EnumerateWebAppSources()
    {
        var root = Path.GetDirectoryName(LocateWebAppFile("Startup.cs"))!;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".g.cs", StringComparison.Ordinal))
            .Where(path => !ContainsSegment(path, "obj") && !ContainsSegment(path, "bin"));
    }

    private static bool ContainsSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static string LocateWebAppFile(string relativePath)
        => LocateRepoFile(Path.Combine("src", "NimBus.WebApp", relativePath));

    private static string LocateRepoFile(string relativePath)
    {
        // Walk up from the test binary directory until the repo-relative path
        // resolves — same approach as ResponseCompressionTests.LocateStartupSource.
        var dir = Path.GetDirectoryName(typeof(RateLimitWiringTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException($"Could not locate {relativePath} by walking up from the test assembly directory.");
    }
}
