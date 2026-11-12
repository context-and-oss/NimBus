using System.Security.Claims;

namespace NimBus.WebApp.Services;

/// <summary>
/// Resolves the display name recorded for a caller. People are named by their name claims;
/// an app-only (workload) token carries none, so it is named after its client application.
/// </summary>
internal static class PrincipalNames
{
    /// <summary>Prefix for a caller named after its client application.</summary>
    internal const string ApplicationPrefix = "app:";

    /// <summary>
    /// The <c>name</c> claim, then <see cref="ClaimTypes.Name"/>, then <c>preferred_username</c>,
    /// then <c>app:</c> plus the client id (<c>azp</c>, else <c>appid</c>); null when none is present.
    /// </summary>
    /// <param name="user">The caller.</param>
    internal static string? Resolve(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        var name = user.FindFirst(c => string.Equals(c.Type, "name", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrEmpty(name))
        {
            name = user.FindFirst(ClaimTypes.Name)?.Value;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = user.FindFirst("preferred_username")?.Value;
        }

        if (string.IsNullOrEmpty(name))
        {
            var clientId = user.FindFirst("azp")?.Value;
            if (string.IsNullOrEmpty(clientId))
            {
                clientId = user.FindFirst("appid")?.Value;
            }

            if (!string.IsNullOrEmpty(clientId))
            {
                name = ApplicationPrefix + clientId;
            }
        }

        return string.IsNullOrEmpty(name) ? null : name;
    }
}
