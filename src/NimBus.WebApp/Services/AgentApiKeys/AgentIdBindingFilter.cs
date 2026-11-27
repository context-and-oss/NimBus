using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>
/// Refuses an agent API key caller that claims a different agent in <c>X-Agent-Id</c> (Spec 039),
/// so a mismatch is visible instead of silently ignored. A signed-in person's header is not
/// refused here; the agent API ignores it and records the person by name instead. Only the
/// identity the key scheme authenticated counts (<see cref="AgentApiKeyDefaults.FindAgentId"/>),
/// so a sign-in token carrying the agent id claim is still a person's.
/// </summary>
internal sealed class AgentIdBindingFilter : IAsyncActionFilter
{
    /// <summary>The header agents send to name themselves.</summary>
    public const string HeaderName = "X-Agent-Id";

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Only the key scheme's own identity counts, as in AgentImplementation: a signed-in
        // person whose token carries the claim is a person, and their header is ignored later.
        var keyAgent = AgentApiKeyDefaults.FindAgentId(context.HttpContext.User);
        var header = context.HttpContext.Request.Headers[HeaderName].ToString().Trim();
        if (!string.IsNullOrEmpty(keyAgent) && header.Length > 0 && !string.Equals(header, keyAgent, StringComparison.Ordinal))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Agent id mismatch",
                Detail = $"This agent API key was issued for '{keyAgent}'; {HeaderName} must match it or be omitted.",
            })
            { StatusCode = StatusCodes.Status403Forbidden };
            return Task.CompletedTask;
        }

        return next();
    }
}
