using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.Csrf;

/// <summary>
/// Tells whether a request carries a cookie of a registered cookie authentication scheme: the
/// ambient credential a cross-site request forgery rides on. Bearer, agent-key and other
/// header-authenticated requests carry none, and a browser can't attach their headers cross-site.
/// </summary>
/// <remarks>
/// The names come from each scheme's options, so ASP.NET Identity's <c>NimBus.Identity</c> and
/// Microsoft.Identity.Web's <c>.AspNetCore.Cookies</c> are covered without hard-coding. A chunked
/// cookie keeps its base name (it holds the chunk count), so the lookup finds it too.
/// </remarks>
internal sealed class SignInCookies
{
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly IOptionsMonitor<CookieAuthenticationOptions> _cookieOptions;

    public SignInCookies(IAuthenticationSchemeProvider schemes, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
    {
        _schemes = schemes;
        _cookieOptions = cookieOptions;
    }

    /// <summary>Whether <paramref name="context"/>'s request carries a sign-in cookie.</summary>
    public async Task<bool> ArePresentAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Cookies.Count == 0)
            return false;

        foreach (var scheme in await _schemes.GetAllSchemesAsync())
        {
            if (!typeof(CookieAuthenticationHandler).IsAssignableFrom(scheme.HandlerType))
                continue;

            var name = _cookieOptions.Get(scheme.Name).Cookie.Name;
            if (!string.IsNullOrEmpty(name) && context.Request.Cookies.ContainsKey(name))
                return true;
        }

        return false;
    }
}
