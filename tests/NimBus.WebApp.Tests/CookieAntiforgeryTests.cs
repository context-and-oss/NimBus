#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Csrf;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #296: a write from a browser session, the only kind a cross-site forgery can ride on, has
/// to carry the <c>X-NimBus-CSRF</c> antiforgery token. Header-authenticated callers (Entra bearer
/// tokens, agent API keys) carry no sign-in cookie and must keep working without one. These run the
/// real <c>DefaultAntiforgery</c>, so a token here is a token in production.
/// </summary>
[TestClass]
public sealed class CookieAntiforgeryTests
{
    private const string HeaderName = "X-NimBus-CSRF";
    private const string EntraCookie = ".AspNetCore.Cookies";
    private const string IdentityCookie = "NimBus.Identity";

    private readonly IServiceProvider _services = CreateServices();

    [TestMethod]
    public async Task A_cookie_write_without_the_token_is_refused()
    {
        var post = Request("POST", Alice, (EntraCookie, "session"));

        Assert.IsInstanceOfType<IAntiforgeryValidationFailedResult>(await AuthorizeAsync(post));
    }

    [TestMethod]
    public async Task A_cookie_write_with_the_token_is_let_through()
    {
        var post = WithToken(Request("POST", Alice, (EntraCookie, "session")), IssueTokens(Alice));

        Assert.IsNull(await AuthorizeAsync(post));
    }

    [TestMethod]
    public async Task An_identity_cookie_write_is_held_to_the_token_too()
    {
        var post = Request("POST", Alice, (IdentityCookie, "session"));

        Assert.IsInstanceOfType<IAntiforgeryValidationFailedResult>(await AuthorizeAsync(post));
    }

    [TestMethod]
    public async Task A_token_issued_to_another_user_is_refused()
    {
        var post = WithToken(Request("POST", Alice, (EntraCookie, "session")), IssueTokens(Bob));

        Assert.IsInstanceOfType<IAntiforgeryValidationFailedResult>(await AuthorizeAsync(post));
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("HEAD")]
    [DataRow("OPTIONS")]
    public async Task A_safe_method_needs_no_token(string method)
    {
        Assert.IsNull(await AuthorizeAsync(Request(method, Alice, (EntraCookie, "session"))));
    }

    // A browser can't attach an Authorization or API-key header to a cross-site request, so a
    // header-authenticated write can't be forged, and the documented bearer callers need no token.
    [TestMethod]
    public async Task A_write_without_a_sign_in_cookie_needs_no_token()
    {
        var bearer = Request("POST", Alice);
        bearer.Request.Headers.Authorization = "Bearer token";

        Assert.IsNull(await AuthorizeAsync(bearer));
    }

    [TestMethod]
    public async Task An_unrelated_cookie_does_not_make_a_request_a_browser_session()
    {
        Assert.IsNull(await AuthorizeAsync(Request("POST", Alice, ("theme", "dark"))));
    }

    [TestMethod]
    public async Task The_token_cookie_is_issued_on_an_api_get_from_a_browser_session_and_validates()
    {
        var get = Request("GET", Alice, (EntraCookie, "session"));
        get.Request.Path = "/api/platform/config";

        await RunMiddlewareAsync(get);

        var issued = SetCookies(get);
        Assert.IsTrue(issued.ContainsKey(AntiforgeryTokenCookieMiddleware.CookieName), "The SPA reads the token from this cookie.");
        var antiforgeryCookie = issued.Single(cookie => cookie.Key.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));

        var post = Request("POST", Alice, (EntraCookie, "session"), (antiforgeryCookie.Key, antiforgeryCookie.Value));
        post.Request.Headers[HeaderName] = issued[AntiforgeryTokenCookieMiddleware.CookieName];
        Assert.IsNull(await AuthorizeAsync(post), "The token the middleware hands out is the one the filter accepts.");
    }

    [TestMethod]
    [DataRow("POST", "/api/platform/config", true, true)]
    [DataRow("GET", "/api/platform/config", false, true)]
    [DataRow("GET", "/api/platform/config", true, false)]
    [DataRow("GET", "/Monitor", true, true)]
    public async Task The_token_cookie_is_not_issued_outside_browser_api_gets(string method, string path, bool signInCookie, bool authenticated)
    {
        var request = signInCookie
            ? Request(method, authenticated ? Alice : Anonymous, (EntraCookie, "session"))
            : Request(method, authenticated ? Alice : Anonymous);
        request.Request.Path = path;

        await RunMiddlewareAsync(request);

        Assert.IsFalse(SetCookies(request).ContainsKey(AntiforgeryTokenCookieMiddleware.CookieName));
    }

    private string AntiforgeryCookieName => _services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;

    private static ClaimsPrincipal Alice => User("alice");

    private static ClaimsPrincipal Bob => User("bob");

    private static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());

    private static ClaimsPrincipal User(string name) =>
        new(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, name + "-id"), new Claim(ClaimTypes.Name, name) },
            authenticationType: "Cookies"));

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAntiforgery(options => options.HeaderName = HeaderName);
        // The two cookie schemes NimBus signs browsers in with: Microsoft.Identity.Web's
        // "Cookies" (default name .AspNetCore.Cookies) and ASP.NET Identity's, named NimBus.Identity.
        services.AddAuthentication()
            .AddCookie("Cookies")
            .AddCookie("Identity.Application", options => options.Cookie.Name = IdentityCookie);
        services.AddNimBusCookieAntiforgery();
        return services.BuildServiceProvider();
    }

    private DefaultHttpContext Request(string method, ClaimsPrincipal user, params (string Name, string Value)[] cookies)
    {
        var context = new DefaultHttpContext { RequestServices = _services, User = user };
        context.Request.Method = method;
        context.Request.Scheme = "https";
        context.Request.Path = "/api/admin/endpoint/crm/delete-all";
        if (cookies.Length > 0)
            context.Request.Headers.Cookie = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        return context;
    }

    private AntiforgeryTokenSet IssueTokens(ClaimsPrincipal user) =>
        _services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(Request("GET", user, (EntraCookie, "session")));

    private DefaultHttpContext WithToken(DefaultHttpContext context, AntiforgeryTokenSet tokens)
    {
        context.Request.Headers.Cookie = $"{context.Request.Headers.Cookie}; {AntiforgeryCookieName}={tokens.CookieToken}";
        context.Request.Headers[HeaderName] = tokens.RequestToken;
        return context;
    }

    private async Task<IActionResult?> AuthorizeAsync(HttpContext context)
    {
        var filter = _services.GetRequiredService<CookieAntiforgeryFilter>();
        var filterContext = new AuthorizationFilterContext(
            new ActionContext(context, new RouteData(), new ActionDescriptor { DisplayName = "AdminApiController.PostAdminDeleteAll" }),
            new List<IFilterMetadata> { filter });
        await filter.OnAuthorizationAsync(filterContext);
        return filterContext.Result;
    }

    private async Task RunMiddlewareAsync(HttpContext context)
    {
        var middleware = ActivatorUtilities.CreateInstance<AntiforgeryTokenCookieMiddleware>(
            _services, (RequestDelegate)(_ => Task.CompletedTask));
        await middleware.InvokeAsync(context);
    }

    private static Dictionary<string, string> SetCookies(HttpContext context) =>
        context.Response.Headers.SetCookie
            .Select(header => header!.Split(';', 2)[0].Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
}
