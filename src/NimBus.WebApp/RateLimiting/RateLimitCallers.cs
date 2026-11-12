using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using NimBus.WebApp.Services.AgentApiKeys;
using NimBus.WebApp.Services.DashboardTokens;

namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// Names the caller of a request for the rate limiter when that caller authenticates only inside
/// MVC. Agent keys and dashboard tokens are never default schemes: their controllers' policies
/// authenticate them during filter execution, after <c>UseRateLimiter</c> has picked a partition.
/// Without this, every agent and wall display behind one address would share that address's
/// anonymous budget, which anonymous traffic from the same address could exhaust.
/// </summary>
public static class RateLimitCallers
{
    private static readonly object CallerKey = new();

    private static readonly (string Header, string Scheme, string Claim, string Prefix)[] Credentials =
    [
        (AgentApiKeyDefaults.HeaderName, AgentApiKeyDefaults.SchemeName, AgentApiKeyDefaults.KeyIdClaim, "agent-key:"),
        (DashboardTokenDefaults.HeaderName, DashboardTokenDefaults.SchemeName, DashboardTokenDefaults.TokenIdClaim, "dashboard-token:"),
    ];

    /// <summary>
    /// For a request with no signed-in user that carries an agent key or a dashboard token, verifies
    /// it with its own scheme and records the key or token id as the rate-limit caller. Only a
    /// verified credential counts, so a forged id cannot draw on a real key's budget. The scheme
    /// caches its result for the request, so MVC's later authentication does not repeat the work.
    /// Register it after <c>UseAuthentication</c> and right before <c>UseRateLimiter</c>.
    /// </summary>
    public static IApplicationBuilder UseNimBusRateLimitCallers(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                foreach (var (header, scheme, claim, prefix) in Credentials)
                {
                    if (!context.Request.Headers.ContainsKey(header))
                        continue;

                    var result = await context.AuthenticateAsync(scheme).ConfigureAwait(false);
                    if (result.Succeeded && result.Principal.FindFirstValue(claim) is { Length: > 0 } id)
                        context.Items[CallerKey] = prefix + id;
                    break;
                }
            }

            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>The verified key or token caller recorded for this request, if any.</summary>
    internal static string? Resolve(HttpContext context) => context.Items.TryGetValue(CallerKey, out var caller) ? caller as string : null;
}
