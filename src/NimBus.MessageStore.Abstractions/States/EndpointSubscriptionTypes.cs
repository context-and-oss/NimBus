using System;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

/// <summary>
/// The delivery types an <see cref="EndpointSubscription"/> can name in its <c>type</c>: one of
/// them, or several separated by <c>;</c> or <c>,</c> (for example <c>mail;webhook</c>).
/// </summary>
public static class EndpointSubscriptionTypes
{
    /// <summary>Delivered by mail to <see cref="EndpointSubscription.Mail"/>.</summary>
    public const string Mail = "mail";

    /// <summary>A Teams subscription. Stored, not delivered.</summary>
    public const string Teams = "teams";

    /// <summary>Delivered as an HTTP POST to <see cref="EndpointSubscription.Url"/>.</summary>
    public const string Webhook = "webhook";

    private static readonly char[] Separators = [';', ','];
    private static readonly string[] Known = [Mail, Teams, Webhook];

    /// <summary>
    /// Splits <paramref name="type"/> into its delivery types, lower-cased, without duplicates and
    /// in the order written. Join them with <c>;</c> to get the stored form.
    /// </summary>
    /// <param name="type">The subscription's <c>type</c>.</param>
    /// <param name="types">The delivery types; empty when the method returns false.</param>
    /// <returns>False when <paramref name="type"/> names no type or an unknown one.</returns>
    public static bool TryParse(string? type, out IReadOnlyList<string> types)
    {
        types = [];
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        var parsed = new List<string>();
        foreach (var part in type.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var known = Array.Find(Known, k => string.Equals(k, part, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                return false;
            }

            if (!parsed.Contains(known))
            {
                parsed.Add(known);
            }
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        types = parsed;
        return true;
    }

    /// <summary>
    /// Whether a stored <paramref name="type"/> names <paramref name="wanted"/>. Unlike
    /// <see cref="TryParse"/> it ignores parts it does not know, so a subscription stored by an
    /// older version is still delivered through the types that are known.
    /// </summary>
    /// <param name="type">The subscription's <c>type</c>.</param>
    /// <param name="wanted">One of <see cref="Mail"/>, <see cref="Teams"/> or <see cref="Webhook"/>.</param>
    public static bool Includes(string? type, string wanted)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        foreach (var part in type.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
