using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NimBus.MessageStore.States;

/// <summary>
/// The rules the management WebApp and the Resolver share for an endpoint alert that is delivered
/// to a webhook: which URLs are accepted, how a subscription's signing secret is derived, and how
/// a request is signed.
/// </summary>
/// <remarks>
/// The signing secret is not stored. It is derived from the deployment's signing key and the
/// subscription id, so the WebApp can show it once when the subscription is created and the
/// Resolver can sign with it later, without a secret at rest in the message store.
/// </remarks>
public static class EndpointAlertWebhook
{
    /// <summary>The longest URL a subscription can hold (the SQL Server column's width).</summary>
    public const int MaxUrlLength = 2000;

    /// <summary>What every signing secret starts with.</summary>
    public const string SecretPrefix = "nbwh_";

    /// <summary>Header: the id of the subscription the request was sent for.</summary>
    public const string SubscriptionHeader = "X-NimBus-Subscription";

    /// <summary>Header: a new id for each request, for a receiver that drops duplicates.</summary>
    public const string DeliveryHeader = "X-NimBus-Delivery";

    /// <summary>Header: when the request was signed, in Unix seconds.</summary>
    public const string TimestampHeader = "X-NimBus-Timestamp";

    /// <summary>Header: the signature, as returned by <see cref="Sign"/>.</summary>
    public const string SignatureHeader = "X-NimBus-Signature";

    /// <summary>Header: an alert rule notice's id, the same on every retry of the notice (Spec 053).</summary>
    public const string NoticeHeader = "X-NimBus-Notice";

    private const string SignatureVersion = "v1";
    private const string DerivationLabel = "nimbus-alert-webhook/v1/";
    private const string RuleDerivationLabel = "nimbus-alert-rule-webhook/v1/";

    /// <summary>
    /// Whether <paramref name="url"/> can be a webhook subscription's URL: absolute, HTTPS, without
    /// user information, and no longer than <see cref="MaxUrlLength"/>.
    /// </summary>
    /// <param name="url">The URL to check.</param>
    /// <param name="allowHttp">Also accept plain HTTP. For local runs only.</param>
    public static bool IsValidUrl(string? url, bool allowHttp = false)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.Length > MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var schemeAllowed = uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp);
        return schemeAllowed && uri.UserInfo.Length == 0 && uri.Host.Length > 0;
    }

    /// <summary>
    /// The secret a webhook subscription's requests are signed with:
    /// <c>nbwh_</c> + base64url(HMAC-SHA256(<paramref name="signingKey"/>,
    /// "nimbus-alert-webhook/v1/" + <paramref name="subscriptionId"/>)).
    /// </summary>
    /// <param name="signingKey">The deployment's <c>NimBus:Alerts:Webhook:SigningKey</c>.</param>
    /// <param name="subscriptionId">The subscription's id.</param>
    public static string DeriveSigningSecret(string signingKey, string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(signingKey);
        ArgumentException.ThrowIfNullOrEmpty(subscriptionId);

        return Derive(signingKey, DerivationLabel + subscriptionId);
    }

    /// <summary>
    /// The secret an alert rule's webhook target is signed with (Spec 053):
    /// <c>nbwh_</c> + base64url(HMAC-SHA256(<paramref name="signingKey"/>,
    /// "nimbus-alert-rule-webhook/v1/" + <paramref name="ruleId"/> + "/" + <paramref name="targetId"/>)).
    /// Its own label keeps a rule target's secret apart from every subscription's.
    /// </summary>
    /// <param name="signingKey">The deployment's <c>NimBus:Alerts:Webhook:SigningKey</c>.</param>
    /// <param name="ruleId">The rule's id.</param>
    /// <param name="targetId">The target's id within the rule.</param>
    public static string DeriveRuleTargetSigningSecret(string signingKey, string ruleId, string targetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(signingKey);
        ArgumentException.ThrowIfNullOrEmpty(ruleId);
        ArgumentException.ThrowIfNullOrEmpty(targetId);

        return Derive(signingKey, RuleDerivationLabel + ruleId + "/" + targetId);
    }

    private static string Derive(string signingKey, string input)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(input));
        return SecretPrefix + Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Signs a request: <c>v1=</c> + lower-case hex of HMAC-SHA256(<paramref name="secret"/>,
    /// "<paramref name="unixTimeSeconds"/>.<paramref name="body"/>"). A receiver computes the same
    /// over the timestamp header and the raw body, compares in constant time, and rejects a
    /// timestamp that is too old.
    /// </summary>
    /// <param name="secret">The subscription's signing secret.</param>
    /// <param name="unixTimeSeconds">The value sent in <see cref="TimestampHeader"/>.</param>
    /// <param name="body">The request body, exactly as sent.</param>
    public static string Sign(string secret, long unixTimeSeconds, string body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(body);

        var signed = unixTimeSeconds.ToString(CultureInfo.InvariantCulture) + "." + body;
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed));
        return SignatureVersion + "=" + Convert.ToHexStringLower(hash);
    }
}
