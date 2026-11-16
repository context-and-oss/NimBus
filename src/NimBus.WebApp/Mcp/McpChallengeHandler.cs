using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore.Authentication;

namespace NimBus.WebApp.Mcp;

/// <summary>The scopes the <c>/mcp</c> 401 challenge names, space-separated full scope URIs.</summary>
/// <param name="Value">The <c>scope</c> parameter's value.</param>
public sealed record McpChallengeScope(string Value);

/// <summary>
/// The MCP SDK's challenge handler, with a <c>scope</c> parameter added to its
/// <c>WWW-Authenticate: Bearer resource_metadata="..."</c> header (#416). An MCP client asks for
/// the challenge's scope when there is one and for every scope in the protected-resource metadata
/// otherwise; naming the configured scopes keeps both to what the app registration defines.
/// </summary>
public sealed class McpChallengeHandler : McpAuthenticationHandler
{
    private readonly McpChallengeScope _scope;

    /// <summary>Creates the handler.</summary>
    public McpChallengeHandler(
        IOptionsMonitor<McpAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        McpChallengeScope scope)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(scope);
        _scope = scope;
    }

    /// <inheritdoc />
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await base.HandleChallengeAsync(properties).ConfigureAwait(false);

        var headers = Response.Headers;
        var values = headers[HeaderNames.WWWAuthenticate].ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is not null
                && value.StartsWith("Bearer resource_metadata=", StringComparison.Ordinal)
                && !value.Contains("scope=", StringComparison.Ordinal))
            {
                values[i] = $"{value}, scope=\"{_scope.Value}\"";
            }
        }

        headers[HeaderNames.WWWAuthenticate] = values;
    }
}
