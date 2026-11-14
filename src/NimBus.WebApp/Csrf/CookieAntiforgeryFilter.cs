using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Csrf;

/// <summary>
/// Requires the <c>X-NimBus-CSRF</c> antiforgery token on a write from a browser session (#296):
/// an unsafe method on a request that carries a sign-in cookie, the only kind a cross-site forgery
/// can ride on. Safe methods and header-authenticated requests (Entra bearer tokens, agent API keys)
/// pass through: a browser can't attach their headers cross-site, and the documented external
/// callers carry no token. A refusal is a 400 marked as an antiforgery failure, as from
/// <c>[AutoValidateAntiforgeryToken]</c>.
/// </summary>
internal sealed partial class CookieAntiforgeryFilter : IAsyncAuthorizationFilter, IOrderedFilter
{
    private readonly IAntiforgery _antiforgery;
    private readonly SignInCookies _signInCookies;
    private readonly ILogger<CookieAntiforgeryFilter> _logger;

    public CookieAntiforgeryFilter(IAntiforgery antiforgery, SignInCookies signInCookies, ILogger<CookieAntiforgeryFilter> logger)
    {
        _antiforgery = antiforgery;
        _signInCookies = signInCookies;
        _logger = logger;
    }

    /// <summary>
    /// The order the framework's antiforgery filters use: after the authorization filters, so an
    /// unauthenticated caller still gets 401 or 403 rather than an antiforgery 400.
    /// </summary>
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Result is not null)
            return;

        var http = context.HttpContext;
        if (IsSafe(http.Request.Method) || !await _signInCookies.ArePresentAsync(http))
            return;

        try
        {
            await _antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException exception)
        {
            LogRefused(exception, context.ActionDescriptor.DisplayName ?? "action");
            context.Result = new AntiforgeryRefusedResult();
        }
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    // A 400 that carries the marker MVC uses for antiforgery refusals, as the framework's own does.
    private sealed class AntiforgeryRefusedResult : BadRequestResult, IAntiforgeryValidationFailedResult;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Refused a write to {Action}: the antiforgery token was missing or invalid")]
    private partial void LogRefused(Exception exception, string action);
}
