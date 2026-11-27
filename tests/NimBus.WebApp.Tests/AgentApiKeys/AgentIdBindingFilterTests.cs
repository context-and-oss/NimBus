#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.AgentApiKeys;

/// <summary>
/// <see cref="AgentIdBindingFilter"/> refuses only an agent API key caller whose <c>X-Agent-Id</c>
/// names another agent. The agent id claim counts only on the key scheme's own identity.
/// </summary>
[TestClass]
public class AgentIdBindingFilterTests
{
    [TestMethod]
    public async Task A_key_caller_with_a_mismatched_header_is_refused()
    {
        var (context, nextCalled) = await RunAsync(Identity(AgentApiKeyDefaults.SchemeName, "enrichment-agent"), "someone-else");

        Assert.IsFalse(nextCalled());
        Assert.AreEqual(StatusCodes.Status403Forbidden, (context.Result as ObjectResult)?.StatusCode);
    }

    [TestMethod]
    public async Task A_key_caller_with_its_own_header_passes()
    {
        var (context, nextCalled) = await RunAsync(Identity(AgentApiKeyDefaults.SchemeName, "enrichment-agent"), "enrichment-agent");

        Assert.IsTrue(nextCalled());
        Assert.IsNull(context.Result);
    }

    [TestMethod]
    public async Task A_signed_in_person_whose_token_carries_the_claim_passes_with_a_mismatched_header()
    {
        // The agent API ignores a person's header and records them by name; refusing here would
        // also skip the access-denied audit on settle.
        var (context, nextCalled) = await RunAsync(Identity("Identity.Application", "enrichment-agent"), "someone-else");

        Assert.IsTrue(nextCalled());
        Assert.IsNull(context.Result);
    }

    [TestMethod]
    public async Task An_unauthenticated_identity_carrying_the_key_scheme_is_not_a_key_caller()
    {
        var identity = new ClaimsIdentity(new[] { new Claim(AgentApiKeyDefaults.AgentIdClaim, "enrichment-agent") });
        var (context, nextCalled) = await RunAsync(identity, "someone-else");

        Assert.IsTrue(nextCalled());
        Assert.IsNull(context.Result);
    }

    private static ClaimsIdentity Identity(string scheme, string agentIdClaim)
        => new(new[] { new Claim(ClaimTypes.Name, "caller"), new Claim(AgentApiKeyDefaults.AgentIdClaim, agentIdClaim) }, scheme);

    private static async Task<(ActionExecutingContext Context, System.Func<bool> NextCalled)> RunAsync(ClaimsIdentity identity, string header)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        http.Request.Headers[AgentIdBindingFilter.HeaderName] = header;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

        var called = false;
        await new AgentIdBindingFilter().OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(new ActionExecutedContext(action, new List<IFilterMetadata>(), controller: new object()));
        });
        return (context, () => called);
    }
}
