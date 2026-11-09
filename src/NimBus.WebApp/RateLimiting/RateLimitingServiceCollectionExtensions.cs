using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// Registers the WebApp's four rate-limiting policies. The single production
/// entry point — see <c>docs/rate-limiting.md</c> for the values and their
/// rationale.
/// </summary>
public static partial class RateLimitingServiceCollectionExtensions
{
    private const string SectionName = "RateLimiting";

    /// <summary>
    /// Binds <see cref="RateLimitOptions"/>, registers the four named policies,
    /// and installs the application-model convention that attaches them to the
    /// NSwag-generated controller actions.
    /// </summary>
    public static IServiceCollection AddNimBusRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        services.Configure<RateLimitOptions>(section);

        // Snapshot read once at startup: the partition lambdas below need the
        // values, and retuning a limit requires a restart either way (which an
        // App Service application-setting change triggers).
        var options = section.Get<RateLimitOptions>() ?? new RateLimitOptions();

        // The policies are ALWAYS registered, even when disabled: parameterless
        // UseRateLimiter() resolves IOptions<RateLimiterOptions> and throws at
        // startup when AddRateLimiter never ran. The kill switch lives in the
        // convention, which then attaches no metadata to any endpoint.
        services.AddRateLimiter(limiter =>
        {
            // The framework default is 503, which would fail AC-4/6/7/8.
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = OnRejectedAsync;

            // Concurrency, not a window: this endpoint's cost is duration. The
            // limiter is deliberately UNPARTITIONED — the protected resource is
            // the total number of in-flight long polls, and partitioning by
            // caller would restore the amplification the issue describes, since
            // an attacker controls the key.
            //
            // QueueProcessingOrder.OldestFirst is load-bearing, not incidental:
            // under NewestFirst an arriving request evicts the oldest queued one,
            // so overflow would reject more than one request. Do not flip it.
            limiter.AddConcurrencyLimiter(RateLimitPolicyNames.AgentReceive, concurrency =>
            {
                concurrency.PermitLimit = options.AgentReceive.PermitLimit;
                concurrency.QueueLimit = options.AgentReceive.QueueLimit;
                concurrency.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });

            // Per user, not per IP, so one operator's runaway script cannot
            // throttle a whole team behind a shared egress.
            limiter.AddPolicy(RateLimitPolicyNames.Admin, context =>
                FixedWindow(UserPartitionKey(context, options), options.Admin));

            limiter.AddPolicy(RateLimitPolicyNames.Search, context =>
                FixedWindow(UserPartitionKey(context, options), options.Search));

            // Per client IP — the gap the per-account Identity lockout cannot
            // cover, which is one password tried once each across many accounts.
            limiter.AddPolicy(RateLimitPolicyNames.Login, context =>
                FixedWindow("ip:" + ClientIpPartitionKey.Resolve(context, options), options.Login));

            limiter.AddPolicy(RateLimitPolicyNames.Intelligence, context =>
                FixedWindow(UserPartitionKey(context, options), options.Intelligence));

            // Tenant and client application are part of the key, so one agent client
            // cannot exhaust the same operator's budget in another client. Attached to
            // /mcp by MapNimBusOperatorMcp, not by the MVC convention. A site Owner can
            // lower the limit live (Spec 037); the limit is part of the key, so a changed
            // limit gets a new window instead of the cached limiter.
            limiter.AddPolicy(RateLimitPolicyNames.Mcp, context =>
            {
                var permits = EffectiveMcpLimit(context, options);
                return FixedWindow(
                    McpPartitionKey(context, options) + ":" + permits.ToString(CultureInfo.InvariantCulture),
                    new RateLimitOptions.WindowLimits { PermitLimit = permits, WindowSeconds = options.Mcp.WindowSeconds });
            });

            // GlobalLimiter stays null on purpose: everything not explicitly
            // listed above — the SignalR hub, health probes, static files, the
            // SPA fallback, every other /api route — carries no rate-limiting
            // metadata and is therefore not throttled at all (AC-10).
        });

        services.AddSingleton<IConfigureOptions<MvcOptions>, ConfigureRateLimitConventions>();

        return services;
    }

    private static RateLimitPartition<string> FixedWindow(string partitionKey, RateLimitOptions.WindowLimits limits)
        => RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = TimeSpan.FromSeconds(limits.WindowSeconds),
            QueueLimit = 0,
        });

    private static string UserPartitionKey(HttpContext context, RateLimitOptions options)
        => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
           ?? context.User.Identity?.Name
           ?? "ip:" + ClientIpPartitionKey.Resolve(context, options);

    // The site Owner's lowered MCP limit, or the deployment's. The policy is loaded by the
    // MCP request guard, which runs before the rate limiter.
    private static int EffectiveMcpLimit(HttpContext context, RateLimitOptions options)
        => context.RequestServices.GetService<NimBus.WebApp.Mcp.Access.IMcpAccessPolicyProvider>()?.Current?.RequestLimit
           ?? options.Mcp.PermitLimit;

    // MCP tokens keep raw claim names (MapInboundClaims = false).
    internal static string McpPartitionKey(HttpContext context, RateLimitOptions options)
    {
        var user = context.User;
        var caller = user.FindFirstValue("oid") ?? user.FindFirstValue("sub") ?? UserPartitionKey(context, options);
        var client = user.FindFirstValue("azp") ?? user.FindFirstValue("appid");
        return $"mcp:{user.FindFirstValue("tid")}:{client}:{caller}";
    }

    private static ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "unknown";

        string? retryAfter = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay))
        {
            // Present for the three fixed-window policies; the concurrency
            // limiter has no meaningful retry hint, so it emits no header.
            retryAfter = ((int)Math.Ceiling(delay.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            http.Response.Headers.RetryAfter = retryAfter;
        }

        // Throttling must be diagnosable from the logs. This is telemetry, not a
        // detector: a caller pacing under a fixed window's sustained rate is
        // never rejected and therefore never appears here.
        LogRateLimitRejectedARequest(http.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RateLimitingServiceCollectionExtensions).FullName!), policy, http.Request.Path, retryAfter ?? "n/a");

        // POST /account/login is a browser form surface — a bodyless 429 shows a
        // human nothing at all.
        http.Response.ContentType = "text/plain";
        var body = retryAfter is null
            ? $"Too many requests ({policy}). Please retry shortly."
            : $"Too many requests ({policy}). Please retry in {retryAfter} seconds.";

        return new ValueTask(http.Response.WriteAsync(body, cancellationToken));
    }

    /// <summary>
    /// Supplies <see cref="RateLimitPoliciesConvention"/> through DI rather than
    /// appending to Startup's existing <c>Configure&lt;MvcOptions&gt;</c> lambda —
    /// the custom options arrive by constructor injection, so there is no type
    /// mismatch and no ordering dependency on when they are bound.
    /// </summary>
    internal sealed class ConfigureRateLimitConventions : IConfigureOptions<MvcOptions>
    {
        private readonly IOptions<RateLimitOptions> _options;

        public ConfigureRateLimitConventions(IOptions<RateLimitOptions> options) => _options = options;

        public void Configure(MvcOptions options)
            => options.Conventions.Add(new RateLimitPoliciesConvention(_options.Value));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit '{Policy}' rejected a request to {Path}. Retry-After: {RetryAfter}")]
    private static partial void LogRateLimitRejectedARequest(ILogger logger, string policy, PathString path, string retryAfter);
}
