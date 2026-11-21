using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.Alerts;

/// <summary>
/// Settings for delivering endpoint alerts to webhook subscriptions, bound from
/// <see cref="SectionName"/>. Webhooks are off until <see cref="SigningKey"/> is set.
/// </summary>
public sealed class AlertWebhookOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "NimBus:Alerts:Webhook";

    /// <summary>The shortest signing key that is accepted.</summary>
    public const int MinSigningKeyLength = 32;

    /// <summary>
    /// The key every subscription's signing secret is derived from. The management WebApp needs
    /// the same value to show a subscription its secret.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>How long one delivery may take. Default: 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether plain HTTP URLs are delivered to. For local runs only. Default: false.</summary>
    public bool AllowInsecureHttp { get; set; }

    /// <summary>
    /// Whether the Resolver may connect to loopback, private and link-local addresses, and through
    /// a configured proxy. Default: false, which allows direct connections to public addresses only.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>
    /// The URL prefixes whose requests also carry a bearer token for the Resolver's identity.
    /// Only an administrator sets these: a subscription never chooses an audience.
    /// </summary>
    public IList<AlertWebhookBearerToken> BearerTokens { get; set; } = new List<AlertWebhookBearerToken>();

    /// <summary>Whether a signing key is set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SigningKey);

    /// <summary>Returns a message for every invalid setting; empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SigningKey is null || SigningKey.Trim().Length < MinSigningKeyLength)
        {
            errors.Add($"{SectionName}:SigningKey must be at least {MinSigningKeyLength} characters.");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            errors.Add($"{SectionName}:Timeout must be positive.");
        }

        for (var i = 0; i < BearerTokens.Count; i++)
        {
            var entry = BearerTokens[i];
            if (entry?.TryGetPrefix(out _) != true)
            {
                errors.Add($"{SectionName}:BearerTokens:{i}:UrlPrefix must be an absolute https URL without a query, a fragment or user information.");
            }

            if (string.IsNullOrWhiteSpace(entry?.Audience) || entry.Audience.Any(char.IsWhiteSpace))
            {
                errors.Add($"{SectionName}:BearerTokens:{i}:Audience must be the token audience (an application ID URI or a client id).");
            }
        }

        return errors;
    }
}

/// <summary>
/// One URL prefix whose webhook requests carry a bearer token, and the audience of that token.
/// </summary>
public sealed class AlertWebhookBearerToken
{
    /// <summary>
    /// An absolute HTTPS URL. A subscription URL gets the token when it has the same host and
    /// port and its path is this path or below it.
    /// </summary>
    public string? UrlPrefix { get; set; }

    /// <summary>
    /// The audience the token is requested for, for example <c>https://azuresre.dev</c>. The
    /// Resolver requests the scope <c>{Audience}/.default</c>.
    /// </summary>
    public string? Audience { get; set; }

    internal bool TryGetPrefix(out Uri prefix)
    {
        if (Uri.TryCreate(UrlPrefix, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0)
        {
            prefix = uri;
            return true;
        }

        prefix = null!;
        return false;
    }

    /// <summary>Whether <paramref name="url"/> is HTTPS, on this prefix's host and port, and at or below its path.</summary>
    internal bool Covers(Uri url)
    {
        if (!TryGetPrefix(out var prefix)
            || url.Scheme != Uri.UriSchemeHttps
            || url.Port != prefix.Port
            || !string.Equals(url.IdnHost, prefix.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // An encoded dot, slash or backslash could be decoded by the receiver into a path outside
        // the prefix. Such a URL is still delivered, without the token.
        var path = url.AbsolutePath;
        if (path.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var prefixPath = prefix.AbsolutePath;
        return prefixPath.EndsWith('/')
            ? path.StartsWith(prefixPath, StringComparison.Ordinal)
            : path.Equals(prefixPath, StringComparison.Ordinal) || path.StartsWith(prefixPath + "/", StringComparison.Ordinal);
    }
}
