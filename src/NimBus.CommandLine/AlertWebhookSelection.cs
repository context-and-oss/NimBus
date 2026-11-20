using McMaster.Extensions.CommandLineUtils;

namespace NimBus.CommandLine;

/// <summary>One URL prefix whose alert webhook requests carry a bearer token for an audience.</summary>
internal sealed record AlertWebhookBearerTokenDeployment(string UrlPrefix, string Audience);

/// <summary>
/// Endpoint alert webhook settings for <c>deploy.core.bicep</c> (the Resolver signs and sends)
/// and <c>deploy.webapp.bicep</c> (the WebApp shows a subscription its signing secret).
/// </summary>
internal sealed record AlertWebhookDeployment(
    string SigningKey,
    IReadOnlyList<AlertWebhookBearerTokenDeployment> BearerTokens);

/// <summary>
/// Parses the alert webhook settings. Alert webhooks are on only with a signing key, read from
/// <see cref="DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable"/>; a
/// <c>--alert-webhook-token</c> without it is a mistake, so it fails before deploying anything.
/// </summary>
internal static class AlertWebhookSelection
{
    internal const string TokenOption = "--alert-webhook-token";

    /// <summary>The shortest signing key the Resolver accepts.</summary>
    internal const int MinSigningKeyLength = 32;

    public static AlertWebhookDeployment? Parse(IReadOnlyList<string?> tokens, string? signingKey)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var key = string.IsNullOrWhiteSpace(signingKey) ? null : signingKey.Trim();
        var entries = tokens.Where(token => !string.IsNullOrWhiteSpace(token)).Select(token => ParseToken(token!.Trim())).ToList();

        if (key is null)
        {
            if (entries.Count > 0)
            {
                throw new CommandException(
                    $"{TokenOption} needs a signing key in {DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable}; without it alert webhooks are off.");
            }

            return null;
        }

        // The key itself is never echoed.
        if (key.Length < MinSigningKeyLength)
        {
            throw new CommandException(
                $"{DeploymentSecrets.AlertWebhookSigningKeyEnvironmentVariable} must be at least {MinSigningKeyLength} characters.");
        }

        return new AlertWebhookDeployment(key, entries);
    }

    private static AlertWebhookBearerTokenDeployment ParseToken(string value)
    {
        // The audience never contains '=', a URL path may: split at the last one.
        var separator = value.LastIndexOf('=');
        var prefix = separator < 0 ? string.Empty : value[..separator].Trim();
        var audience = separator < 0 ? string.Empty : value[(separator + 1)..].Trim();

        var prefixValid = Uri.TryCreate(prefix, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps
            && url.UserInfo.Length == 0
            && url.Query.Length == 0
            && url.Fragment.Length == 0;
        if (!prefixValid || audience.Length == 0 || audience.Any(char.IsWhiteSpace))
        {
            throw new CommandException(
                $"Invalid {TokenOption} value '{value}'. Expected <https-url-prefix>=<audience>, " +
                "for example https://my-agent.example.com/api/=https://azuresre.dev. The prefix takes no query, fragment or user information.");
        }

        return new AlertWebhookBearerTokenDeployment(prefix, audience);
    }
}
