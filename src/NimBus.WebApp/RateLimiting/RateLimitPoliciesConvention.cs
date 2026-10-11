using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using NimBus.Extensions.Identity.Controllers;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// Attaches the four rate-limiting policies to exactly the actions they protect.
/// <para>
/// The controllers carrying these routes live in <c>Controllers/ApiContract.g.cs</c>,
/// which NSwag regenerates on every build, so an <c>[EnableRateLimiting]</c>
/// attribute there would be erased. The repo already solves this exact problem
/// the same way — see <see cref="AllowAnonymousActionsConvention"/>, whose
/// comment records why a class-level attribute is the wrong tool. Keying on
/// controller type plus <c>nameof(action)</c> also turns an NSwag rename into a
/// build break rather than a silently dropped policy.
/// </para>
/// </summary>
internal sealed class RateLimitPoliciesConvention : IApplicationModelConvention
{
    private readonly RateLimitOptions _options;

    public RateLimitPoliciesConvention(RateLimitOptions options) => _options = options;

    public void Apply(ApplicationModel application)
    {
        if (!_options.Enabled)
        {
            // Kill switch: the policies stay registered (so UseRateLimiter cannot
            // throw at startup) but nothing carries their metadata.
            return;
        }

        foreach (var controller in application.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                var policy = PolicyFor(controller, action);
                if (policy is null)
                {
                    continue;
                }

                foreach (var selector in action.Selectors)
                {
                    if (policy == RateLimitPolicyNames.Login && !MatchesHttpMethod(selector, "POST"))
                    {
                        // AccountController.Login is overloaded: GET renders the
                        // sign-in page, POST verifies the password. Throttling the
                        // page would break sign-in for anyone who reloads it.
                        continue;
                    }

                    selector.EndpointMetadata.Add(new EnableRateLimitingAttribute(policy));
                }
            }
        }
    }

    private static string? PolicyFor(ControllerModel controller, ActionModel action)
    {
        var type = controller.ControllerType;

        // /api/admin/* is precisely this controller's actions — matched at
        // controller scope deliberately, and asserted in both directions by
        // RateLimitEndpointMetadataTests so a future route move breaks the test
        // instead of silently widening or narrowing the policy.
        if (type == typeof(AdminApiController)
            || type == typeof(SimulationApiController)
            || type == typeof(McpAccessApiController)
            || type == typeof(AgentKeysApiController)
            || type == typeof(NimBus.WebApp.Controllers.IntelligenceSettingsController))
        {
            return RateLimitPolicyNames.Admin;
        }

        if (type == typeof(AgentApiController)
            && action.ActionMethod.Name == nameof(AgentApiController.GetAgentReceive))
        {
            return RateLimitPolicyNames.AgentReceive;
        }

        if (type == typeof(MessageApiController)
            && action.ActionMethod.Name == nameof(MessageApiController.PostMessagesSearch))
        {
            return RateLimitPolicyNames.Search;
        }

        if (type == typeof(AuditApiController)
            && action.ActionMethod.Name == nameof(AuditApiController.PostAuditsSearch))
        {
            return RateLimitPolicyNames.Search;
        }

        if (type == typeof(AccountController)
            && action.ActionMethod.Name == nameof(AccountController.Login))
        {
            return RateLimitPolicyNames.Login;
        }

        if (type == typeof(IntegrationIntelligenceController)
            && action.ActionMethod.Name == nameof(IntegrationIntelligenceController.PostClassification))
        {
            return RateLimitPolicyNames.Intelligence;
        }

        return null;
    }

    private static bool MatchesHttpMethod(SelectorModel selector, string method)
        => selector.ActionConstraints
            .OfType<HttpMethodActionConstraint>()
            .Any(constraint => constraint.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
        || selector.EndpointMetadata
            .OfType<HttpMethodMetadata>()
            .Any(metadata => metadata.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase));
}
