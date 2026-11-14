using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.Csrf;

/// <summary>
/// Registers antiforgery validation for cookie-authenticated writes on the generated API
/// controllers, and the middleware that hands the SPA its token (#296).
/// </summary>
internal static class CookieAntiforgeryServiceCollectionExtensions
{
    /// <summary>
    /// Adds the filter, its convention and their services. Expects
    /// <c>AddAntiforgery</c> to have set the <c>X-NimBus-CSRF</c> header name.
    /// </summary>
    public static IServiceCollection AddNimBusCookieAntiforgery(this IServiceCollection services)
    {
        services.TryAddSingleton<SignInCookies>();
        services.TryAddSingleton<CookieAntiforgeryFilter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, ConfigureCookieAntiforgeryConvention>());
        return services;
    }

    /// <summary>
    /// Issues the <see cref="AntiforgeryTokenCookieMiddleware.CookieName"/> cookie. Place it after
    /// <c>UseAuthorization</c>, so the token is bound to the signed-in user.
    /// </summary>
    public static IApplicationBuilder UseNimBusAntiforgeryTokenCookie(this IApplicationBuilder app)
        => app.UseMiddleware<AntiforgeryTokenCookieMiddleware>();

    private sealed class ConfigureCookieAntiforgeryConvention : IConfigureOptions<MvcOptions>
    {
        public void Configure(MvcOptions options) => options.Conventions.Add(new CookieAntiforgeryConvention());
    }
}
