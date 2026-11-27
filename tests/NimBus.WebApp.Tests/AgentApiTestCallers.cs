#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using NimBus.Core.Messages.PII;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests;

/// <summary>Request principals and role fakes for exercising the agent API directly.</summary>
internal static class AgentApiTestCallers
{
    /// <summary>A caller that authenticated with an agent API key issued for <paramref name="agentId"/>.</summary>
    public static IHttpContextAccessor AgentKey(string agentId, string? agentIdHeader = null)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, "agent:" + agentId),
                new Claim(AgentApiKeyDefaults.AgentIdClaim, agentId),
            },
            AgentApiKeyDefaults.SchemeName);
        return Accessor(new ClaimsPrincipal(identity), agentIdHeader);
    }

    /// <summary>A person signed in with the default sign-in scheme.</summary>
    public static IHttpContextAccessor Person(string name, string? agentIdHeader = null, string scheme = "Identity.Application")
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) }, scheme);
        return Accessor(new ClaimsPrincipal(identity), agentIdHeader);
    }

    private static FixedHttpContextAccessor Accessor(ClaimsPrincipal user, string? agentIdHeader)
    {
        var context = new DefaultHttpContext { User = user };
        if (agentIdHeader is not null)
            context.Request.Headers[AgentIdBindingFilter.HeaderName] = agentIdHeader;
        return new FixedHttpContextAccessor(context);
    }

    // HttpContextAccessor keeps its value in an AsyncLocal, which does not flow into a
    // TestServer request; a plain field keeps the caller visible wherever the request runs.
    private sealed class FixedHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }
}

/// <summary>Grants a fixed role (site-wide) and PiiReader flag, and records every role check.</summary>
internal sealed class FixedRoleAuthorization : IEndpointAuthorizationService
{
    private readonly AccessRole _role;
    private readonly bool _piiReader;

    public FixedRoleAuthorization(AccessRole role, bool piiReader = false)
    {
        _role = role;
        _piiReader = piiReader;
    }

    /// <summary>A service that grants nothing.</summary>
    public static FixedRoleAuthorization None => new(AccessRole.None);

    public List<(AccessRole Required, string? EndpointId)> Checks { get; } = new();

    public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
    {
        Checks.Add((required, endpointId));
        return Task.FromResult(_role >= required);
    }

    public Task<bool> CanReadPiiAsync() => Task.FromResult(_piiReader);

    public Task<CurrentUserAccess> GetCurrentUserAccessAsync()
        => Task.FromResult(new CurrentUserAccess { SiteRole = _role, IsPiiReader = _piiReader });

    public string? GetCurrentUserName() => null;
}

/// <summary>Masks every payload to a recognisable value, so a test can see masking was applied.</summary>
internal sealed class MarkingMasker : IEventJsonMasker
{
    public const string Masked = "{\"masked\":true}";

    public string Mask(string eventTypeId, string eventJson) => Masked;

    public bool ContainsRedactPlaceholder(string eventTypeId, string eventJson) => eventJson == Masked;

    public string StripMaskedMarker(string eventJson) => eventJson;

    public bool TryCollectSensitiveValues(string eventTypeId, string eventJson, [NotNullWhen(true)] out IReadOnlyCollection<string>? values)
    {
        values = System.Array.Empty<string>();
        return true;
    }
}
