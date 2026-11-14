namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// Rate-limiting configuration, bound from the <c>RateLimiting</c> configuration
/// section. Every value carries the shipped default as a property initialiser,
/// so configuration is optional and the defaults live in exactly one place.
/// See <c>docs/rate-limiting.md</c> for the rationale behind each number.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>
    /// Kill switch. When false the policies are still <em>registered</em> (so
    /// <c>UseRateLimiter()</c> cannot throw at startup) but no endpoint gets any
    /// rate-limiting metadata attached, so nothing is throttled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the <c>X-Forwarded-For</c> header may be used to derive the
    /// client address for the login policy. False by default — an untrusted
    /// forwarded address must never be able to move a caller's partition.
    /// The Bicep template sets this true for App Service, which always
    /// terminates at its front end.
    /// </summary>
    public bool TrustForwardedForHeader { get; set; }

    /// <summary>
    /// Fixed-window limits applied per caller to every <c>/api</c> request, on top of the named
    /// policies below: a ceiling for the routes no policy covers. Default 600 per 60 seconds.
    /// </summary>
    public WindowLimits Global { get; set; } = new() { PermitLimit = 600, WindowSeconds = 60 };

    /// <summary>Concurrency limits for <c>GET /api/agent/receive</c>.</summary>
    public AgentReceiveLimits AgentReceive { get; set; } = new();

    /// <summary>Fixed-window limits for <c>/api/admin/*</c>.</summary>
    public WindowLimits Admin { get; set; } = new() { PermitLimit = 60, WindowSeconds = 60 };

    /// <summary>Fixed-window limits for the message and audit search endpoints.</summary>
    public WindowLimits Search { get; set; } = new() { PermitLimit = 120, WindowSeconds = 60 };

    /// <summary>Fixed-window limits for <c>POST /account/login</c>.</summary>
    public LoginLimits Login { get; set; } = new();

    /// <summary>Fixed-window limits for provider-backed intelligence requests.</summary>
    public WindowLimits Intelligence { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    /// <summary>Fixed-window limits for the operator MCP endpoint (Spec 035).</summary>
    public WindowLimits Mcp { get; set; } = new() { PermitLimit = 60, WindowSeconds = 60 };

    /// <summary>
    /// Fixed-window limits for operator MCP tools that change a message (resubmit, skip,
    /// report, classify), per tenant, client application and user. Counted inside the tools,
    /// on top of <see cref="Mcp"/>, because every tool call shares the one <c>/mcp</c> route.
    /// </summary>
    public WindowLimits McpMutations { get; set; } = new() { PermitLimit = 5, WindowSeconds = 60 };

    /// <summary>Permit count and queue depth for a concurrency limiter.</summary>
    public sealed class AgentReceiveLimits
    {
        /// <summary>Simultaneously executing requests. Default 20.</summary>
        public int PermitLimit { get; set; } = 20;

        /// <summary>Requests queued behind the permits. Default 5; set 0 for a hard cap.</summary>
        public int QueueLimit { get; set; } = 5;
    }

    /// <summary>Permit count and window length for a fixed-window limiter.</summary>
    public class WindowLimits
    {
        /// <summary>Requests admitted per window, per partition.</summary>
        public int PermitLimit { get; set; }

        /// <summary>Window length in seconds.</summary>
        public int WindowSeconds { get; set; }
    }

    /// <summary>Login window limits plus the IPv6 bucketing knob.</summary>
    public sealed class LoginLimits : WindowLimits
    {
        /// <summary>Creates the shipped login defaults: 50 requests per 300 seconds.</summary>
        public LoginLimits()
        {
            PermitLimit = 50;
            WindowSeconds = 300;
        }

        /// <summary>
        /// How many bits of an IPv6 client address form the partition key.
        /// 128 (the default) means the full address — per-client-IP. An operator
        /// facing an attacker who rotates addresses inside a routed /64 can set
        /// 64 to bucket by prefix, at the cost of merging a site's IPv6 users.
        /// Ignored for IPv4.
        /// </summary>
        public int IPv6PrefixBits { get; set; } = 128;
    }
}
