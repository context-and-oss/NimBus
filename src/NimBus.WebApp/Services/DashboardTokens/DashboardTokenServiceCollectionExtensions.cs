using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services.DashboardTokens;

/// <summary>Registers Monitor dashboard token authentication.</summary>
public static class DashboardTokenServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="DashboardTokenDefaults.SchemeName"/> scheme and puts the
    /// <see cref="DashboardTokenDefaults.PolicyName"/> policy on the Monitor feed. The scheme is
    /// never a default: every other endpoint keeps authenticating with the default scheme only, so
    /// a token admits nothing but <c>/api/monitor/feed/*</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddNimBusDashboardTokens(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDashboardTokenProvider, DashboardTokenProvider>();
        services.TryAddSingleton<IDashboardTokenUsageRecorder, DashboardTokenUsageRecorder>();
        services.TryAddScoped<DashboardTokenService>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DashboardTokenAuthenticationHandler>(DashboardTokenDefaults.SchemeName, null);

        // The policy names the scheme that authenticates people everywhere else, next to the token
        // scheme: MVC authenticates an action only with the schemes its policy names, so leaving
        // it out would lock signed-in people out of the feed. Resolved from the final
        // authentication options because the sign-in modes set it differently (see
        // AddNimBusAgentApiKeys).
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<AuthenticationOptions>>((authorization, authentication) =>
            {
                var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
                var signInScheme = authentication.Value.DefaultAuthenticateScheme ?? authentication.Value.DefaultScheme;
                if (!string.IsNullOrEmpty(signInScheme))
                    policy.AddAuthenticationSchemes(signInScheme);
                policy.AddAuthenticationSchemes(DashboardTokenDefaults.SchemeName);
                authorization.AddPolicy(DashboardTokenDefaults.PolicyName, policy.Build());
            });

        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Conventions.Add(new MonitorFeedAuthorizationConvention()));

        return services;
    }
}

/// <summary>
/// Puts the <see cref="DashboardTokenDefaults.PolicyName"/> policy on the NSwag-generated
/// <see cref="MonitorFeedApiController"/>, which cannot carry attributes. MVC combines it with the
/// global "authenticated user" filter.
/// </summary>
internal sealed class MonitorFeedAuthorizationConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        if (controller.ControllerType != typeof(MonitorFeedApiController))
            return;

        controller.Filters.Add(new AuthorizeFilter(DashboardTokenDefaults.PolicyName));
    }
}
