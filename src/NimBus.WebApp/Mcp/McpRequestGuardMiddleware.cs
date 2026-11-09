using System.Net;
using NimBus.WebApp.Mcp.Access;

namespace NimBus.WebApp.Mcp;

/// <summary>
/// Transport checks for the operator MCP endpoint that run before authentication.
/// <list type="bullet">
/// <item>A browser <c>Origin</c> must be allowed; the MCP transport requires this to stop DNS
/// rebinding. Requests without the header (non-browser clients) are unaffected.</item>
/// <item>In local development, where nothing authenticates the caller, only loopback
/// connections are accepted.</item>
/// <item>The site Owner's MCP access policy (Spec 037) must be loaded and must have the
/// endpoint switched on; otherwise every call gets 503.</item>
/// </list>
/// </summary>
public sealed partial class McpRequestGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly McpOperatorRuntime _runtime;
    private readonly IMcpAccessPolicyProvider _policies;
    private readonly ILogger<McpRequestGuardMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    public McpRequestGuardMiddleware(RequestDelegate next, McpOperatorRuntime runtime, IMcpAccessPolicyProvider policies, ILogger<McpRequestGuardMiddleware> logger)
    {
        _next = next;
        _runtime = runtime;
        _policies = policies;
        _logger = logger;
    }

    /// <summary>
    /// Rejects disallowed MCP requests with 403, refuses every call with 503 while the access
    /// policy is unavailable or turned off, and passes everything else on.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpOperatorOptions.Path, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var local = _runtime.Mode == McpAuthenticationMode.LocalDevelopment;

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsAllowedOrigin(origin, local))
        {
            LogRejectedAnMcpRequestFromOrigin(_logger, origin);
            await RejectAsync(context, "Origin not allowed.").ConfigureAwait(false);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (local && remote is not null && !IsLoopback(remote))
        {
            LogRejectedANonLoopbackMcpRequest(_logger, remote);
            await RejectAsync(context, "The local development MCP endpoint accepts loopback connections only.").ConfigureAwait(false);
            return;
        }

        var policy = await _policies.GetAsync().ConfigureAwait(false);
        if (policy is null)
        {
            await UnavailableAsync(context, "[Unavailable] The MCP access policy could not be loaded.").ConfigureAwait(false);
            return;
        }

        if (!policy.Enabled)
        {
            await UnavailableAsync(context, "[Disabled] MCP access is turned off by an administrator.").ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool IsAllowedOrigin(string origin, bool local)
    {
        var normalized = origin.TrimEnd('/');
        if (_runtime.Options.AllowedOrigins.Any(allowed => string.Equals(allowed.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        return local
            && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static Task UnavailableAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(message);
    }

    private static Task RejectAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(message);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an MCP request from origin {Origin}")]
    private static partial void LogRejectedAnMcpRequestFromOrigin(ILogger logger, string origin);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a non-loopback MCP request from {RemoteAddress} in local development mode")]
    private static partial void LogRejectedANonLoopbackMcpRequest(ILogger logger, IPAddress remoteAddress);
}
