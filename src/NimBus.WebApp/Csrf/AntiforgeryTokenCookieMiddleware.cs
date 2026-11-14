using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace NimBus.WebApp.Csrf;

/// <summary>
/// Hands the SPA its antiforgery request token in a readable cookie (#296), so the shared API
/// client can send it as <c>X-NimBus-CSRF</c> on writes: the cookie-to-header pattern. The token is
/// bound to the signed-in user, so this runs after authorization.
/// </summary>
/// <remarks>
/// The cookie is issued on every <c>GET /api/…</c> from a browser session, not only with
/// <c>index.html</c>. That covers the Vite dev server, which serves the shell itself, and it follows
/// a change of user without a reload. <see cref="IAntiforgery.GetAndStoreTokens"/> also sets the
/// HttpOnly antiforgery cookie the token pairs with, and marks the response no-cache, which the API
/// already is.
/// </remarks>
internal sealed class AntiforgeryTokenCookieMiddleware
{
    /// <summary>The readable cookie that carries the antiforgery request token to the SPA.</summary>
    public const string CookieName = "NimBus.CSRF";

    private readonly RequestDelegate _next;
    private readonly IAntiforgery _antiforgery;
    private readonly SignInCookies _signInCookies;

    public AntiforgeryTokenCookieMiddleware(RequestDelegate next, IAntiforgery antiforgery, SignInCookies signInCookies)
    {
        _next = next;
        _antiforgery = antiforgery;
        _signInCookies = signInCookies;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && context.User.Identity?.IsAuthenticated == true
            && await _signInCookies.ArePresentAsync(context))
        {
            var tokens = _antiforgery.GetAndStoreTokens(context);
            context.Response.Cookies.Append(CookieName, tokens.RequestToken!, new CookieOptions
            {
                // Read by the SPA's script, so not HttpOnly. Strict: it is never needed cross-site.
                HttpOnly = false,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
            });
        }

        await _next(context);
    }
}
