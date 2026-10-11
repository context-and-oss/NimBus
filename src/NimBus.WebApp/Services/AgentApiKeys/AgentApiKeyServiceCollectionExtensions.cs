using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>Registers agent API key authentication (Spec 039).</summary>
public static class AgentApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="AgentApiKeyDefaults.SchemeName"/> scheme and puts the
    /// <see cref="AgentApiKeyDefaults.PolicyName"/> policy on the agent API. The scheme is never
    /// a default: every other endpoint keeps authenticating with the default scheme only, so a
    /// key admits nothing but <c>/api/agent/*</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddNimBusAgentApiKeys(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAgentApiKeyProvider, AgentApiKeyProvider>();
        services.TryAddSingleton<IAgentApiKeyUsageRecorder, AgentApiKeyUsageRecorder>();

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AgentApiKeyAuthenticationHandler>(AgentApiKeyDefaults.SchemeName, null);

        // The policy names the scheme that authenticates people everywhere else, next to the key
        // scheme: MVC authenticates an action only with the schemes its policy names, so leaving
        // it out would lock signed-in people out of the agent API. Resolved from the final
        // authentication options because the sign-in modes set it differently (Identity sets the
        // default authenticate scheme explicitly; Microsoft.Identity.Web's web app sets
        // OpenIdConnect).
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptions<AuthenticationOptions>>((authorization, authentication) =>
            {
                var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
                var signInScheme = authentication.Value.DefaultAuthenticateScheme ?? authentication.Value.DefaultScheme;
                if (!string.IsNullOrEmpty(signInScheme))
                    policy.AddAuthenticationSchemes(signInScheme);
                policy.AddAuthenticationSchemes(AgentApiKeyDefaults.SchemeName);
                authorization.AddPolicy(AgentApiKeyDefaults.PolicyName, policy.Build());
            });

        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Conventions.Add(new AgentApiAuthorizationConvention()));

        return services;
    }
}

/// <summary>
/// Puts the <see cref="AgentApiKeyDefaults.PolicyName"/> policy on the NSwag-generated
/// <see cref="AgentApiController"/>, which cannot carry attributes. MVC combines it with the
/// global "authenticated user" filter.
/// </summary>
internal sealed class AgentApiAuthorizationConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        if (controller.ControllerType == typeof(AgentApiController))
            controller.Filters.Add(new AuthorizeFilter(AgentApiKeyDefaults.PolicyName));
    }
}
