using Microsoft.AspNetCore.Http;
using System;

namespace NimBus.WebApp.Middleware;

/// <summary>
/// Gives server-side code access to the per-request Content-Security-Policy nonce issued by
/// <see cref="SecurityHeadersMiddleware"/>.
/// </summary>
public static class CspNonceHttpContextExtensions
{
    internal const string ItemKey = "NimBus.WebApp.CspNonce";

    /// <summary>
    /// Returns the base64 nonce listed in this response's <c>script-src</c> directive. Add it as
    /// <c>nonce="…"</c> to any inline <c>&lt;script&gt;</c> rendered in the same request.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>The nonce, or <see langword="null"/> when the request carries no CSP header (for example Swagger paths).</returns>
    public static string? GetCspNonce(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out var value) ? value as string : null;
    }
}
