using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace NimBus.WebApp.Middleware;

/// <summary>
/// Middleware to add security-related HTTP headers to all responses.
/// </summary>
public class SecurityHeadersMiddleware
{
    private const string ConnectionStringEnvironmentKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    private const string ConnectionStringConfigKey = "ApplicationInsights:ConnectionString";

    private readonly RequestDelegate _next;
    private readonly string _connectSrc;

    /// <summary>
    /// Creates the middleware. The <c>connect-src</c> directive is computed once here from the
    /// Application Insights connection string and does not change for the process lifetime.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="configuration">Configuration holding the optional Application Insights connection string.</param>
    public SecurityHeadersMiddleware(RequestDelegate next, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _next = next;
        _connectSrc = BuildConnectSrc(configuration);
    }

    /// <summary>
    /// Adds the security headers to the response and invokes the next middleware.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Prevent clickjacking by disallowing embedding in frames
        context.Response.Headers["X-Frame-Options"] = "DENY";

        // Prevent MIME type sniffing
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        // Disable the legacy XSS auditor. Modern guidance (OWASP, Chrome, MDN) is to set 0:
        // the filter is deprecated, removed in current browsers, and known to introduce
        // XS-Leak vulnerabilities in older ones. CSP is the replacement.
        context.Response.Headers["X-XSS-Protection"] = "0";

        // Control referrer information sent with requests
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // Content Security Policy - restrict resource loading
        // Skip CSP for Swagger UI paths to avoid compatibility issues
        var path = context.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/api-docs", StringComparison.OrdinalIgnoreCase))
        {
            // script-src allows same-origin files plus inline script carrying this request's
            // nonce (read it with HttpContext.GetCspNonce()); no 'unsafe-inline' or 'unsafe-eval'.
            // The Vite SPA build has no inline scripts, so it needs only 'self'.
            // connect-src is 'self' (the /api calls and the same-origin /hubs/gridevents SignalR
            // hub, including its WebSocket under CSP Level 3) plus the origins of the
            // IngestionEndpoint/LiveEndpoint in the configured Application Insights connection
            // string (APPLICATIONINSIGHTS_CONNECTION_STRING, else ApplicationInsights:ConnectionString).
            var nonce = CreateNonce();
            context.Items[CspNonceHttpContextExtensions.ItemKey] = nonce;
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; " +
                $"script-src 'self' 'nonce-{nonce}'; " +
                "style-src 'self' 'unsafe-inline'; " +
                "img-src 'self' data: https:; " +
                "font-src 'self'; " +
                $"{_connectSrc}; " +
                "frame-ancestors 'none';";
        }

        // Permissions Policy - restrict browser features
        context.Response.Headers["Permissions-Policy"] =
            "accelerometer=(), " +
            "camera=(), " +
            "geolocation=(), " +
            "gyroscope=(), " +
            "magnetometer=(), " +
            "microphone=(), " +
            "payment=(), " +
            "usb=()";

        await _next(context);
    }

    private static string CreateNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    private static string BuildConnectSrc(IConfiguration configuration)
    {
        var connectionString = configuration[ConnectionStringEnvironmentKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration[ConnectionStringConfigKey];
        }

        var directive = "connect-src 'self'";
        foreach (var origin in GetApplicationInsightsOrigins(connectionString))
        {
            directive += " " + origin;
        }

        return directive;
    }

    private static List<string> GetApplicationInsightsOrigins(string? connectionString)
    {
        var origins = new List<string>();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return origins;
        }

        string? ingestion = null;
        string? live = null;
        foreach (var pair in connectionString.Split(';'))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = pair[..separator].Trim();
            var value = pair[(separator + 1)..].Trim();
            if (key.Equals("IngestionEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                ingestion = value;
            }
            else if (key.Equals("LiveEndpoint", StringComparison.OrdinalIgnoreCase))
            {
                live = value;
            }
        }

        foreach (var endpoint in new[] { ingestion, live })
        {
            if (TryGetHttpsOrigin(endpoint, out var origin) &&
                !origins.Exists(o => o.Equals(origin, StringComparison.OrdinalIgnoreCase)))
            {
                origins.Add(origin);
            }
        }

        return origins;
    }

    private static bool TryGetHttpsOrigin(string? endpoint, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrEmpty(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
