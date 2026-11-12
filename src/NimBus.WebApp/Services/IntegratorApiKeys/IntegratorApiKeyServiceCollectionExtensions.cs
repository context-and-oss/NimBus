using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.WebApp.Problems;

namespace NimBus.WebApp.Services.IntegratorApiKeys;

/// <summary>Registers integrator API key authentication (Spec 045).</summary>
public static class IntegratorApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IntegratorApiKeyDefaults.SchemeName"/> scheme and its services. The
    /// scheme is never a default: <see cref="UseNimBusIntegratorApiKeys"/> runs it on REST API
    /// requests, so a person's sign-in keeps working exactly as before.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddNimBusIntegratorApiKeys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIntegratorApiKeyProvider, IntegratorApiKeyProvider>();
        services.TryAddSingleton<IIntegratorApiKeyUsageRecorder, IntegratorApiKeyUsageRecorder>();
        services.TryAddScoped<IntegratorApiKeyService>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, IntegratorApiKeyAuthenticationHandler>(IntegratorApiKeyDefaults.SchemeName, null);

        return services;
    }

    /// <summary>
    /// Authenticates the integrator API key in <see cref="IntegratorApiKeyDefaults.HeaderName"/> on
    /// <c>/api</c> requests and adds its identity to the request's user, so the global "authenticated
    /// user" filter admits it and <see cref="EndpointAuthorizationService"/> grants its role.
    /// Register it right after <c>UseAuthentication</c>.
    /// <list type="bullet">
    /// <item>Only <c>/api</c>: the SignalR hub, <c>/mcp</c> and the SPA never see a key.</item>
    /// <item>The agent API and the Monitor feed refuse it (see the handler): they have their own
    /// credentials, and their policies authenticate only with the schemes they name.</item>
    /// <item>A key that fails answers 401, even when the request also carries a sign-in: a caller
    /// that sends a key means the key.</item>
    /// </list>
    /// </summary>
    public static IApplicationBuilder UseNimBusIntegratorApiKeys(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (!ApiProblems.IsApiPath(context.Request.Path)
                || !context.Request.Headers.ContainsKey(IntegratorApiKeyDefaults.HeaderName)
                || IntegratorApiKeyAuthenticationHandler.IsExcludedSurface(context))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var result = await context.AuthenticateAsync(IntegratorApiKeyDefaults.SchemeName).ConfigureAwait(false);
            if (!result.Succeeded || result.Principal?.Identity is not System.Security.Claims.ClaimsIdentity keyIdentity)
            {
                await context.ChallengeAsync(IntegratorApiKeyDefaults.SchemeName).ConfigureAwait(false);
                return;
            }

            // The key's identity goes first: ClaimsPrincipal.Identity and FindFirst read identities
            // in order, so the audit actor, the current user name and the rate-limit partition all
            // name the key. A sign-in identity stays behind it, so the cookie antiforgery check
            // still applies to a browser session that also sends a key.
            var identities = new List<System.Security.Claims.ClaimsIdentity> { keyIdentity };
            if (context.User.Identity?.IsAuthenticated == true)
                identities.AddRange(context.User.Identities);
            context.User = new System.Security.Claims.ClaimsPrincipal(identities);

            await next(context).ConfigureAwait(false);
        });
    }
}
