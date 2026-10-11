using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace NimBus.Extensions.Identity.Services;

/// <summary>
/// Maps ASP.NET Core Identity claims to the claim types that
/// <c>EndpointAuthorizationService</c> already checks (oid, name, groups).
/// This allows Identity users to work with existing NimBus authorization
/// without any changes to the authorization service.
/// </summary>
public class NimBusClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity)
            return Task.FromResult(principal);

        if (identity.AuthenticationType != IdentityConstants.ApplicationScheme)
            return Task.FromResult(principal);

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId != null && !principal.HasClaim(c => c.Type == "oid"))
        {
            identity.AddClaim(new Claim("oid", userId));
        }

        var displayName = principal.FindFirstValue("DisplayName")
            ?? principal.FindFirstValue(ClaimTypes.Name);
        if (displayName != null && !principal.HasClaim(c => c.Type == "name"))
        {
            identity.AddClaim(new Claim("name", displayName));
        }

        // Map Identity roles to "groups" claims (EndpointAuthorizationService checks
        // for "EIP_Management" in claim values, regardless of claim type). Snapshot the roles:
        // AddClaim below changes the collection principal.Claims enumerates.
        foreach (var roleClaim in principal.Claims.Where(c => c.Type == ClaimTypes.Role).ToList())
        {
            if (!principal.HasClaim(c => c.Value == roleClaim.Value && c.Type != ClaimTypes.Role))
            {
                identity.AddClaim(new Claim("groups", roleClaim.Value));
            }
        }

        return Task.FromResult(principal);
    }
}
