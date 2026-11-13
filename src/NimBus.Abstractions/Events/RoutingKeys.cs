using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NimBus.Core.Messages.PII;

namespace NimBus.Core.Events;

/// <summary>
/// Shared rules for routing keys (Spec 045): the declared-key format, the canonical order keys
/// are written in, and how the routing-key property of an event class is resolved.
/// </summary>
public static partial class RoutingKeys
{
    /// <summary>The longest declared routing key.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// True when <paramref name="key"/> may be declared by a consumer: 1 to 64 characters from
    /// <c>[A-Za-z0-9_.-]</c>. Declared keys are interpolated into Service Bus SQL filters, so
    /// this is what keeps that interpolation safe.
    /// </summary>
    /// <param name="key">The key to check.</param>
    public static bool IsValid(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= MaxLength && KeyPattern().IsMatch(key);

    /// <summary>The keys, distinct and in ordinal order, as every filter writes them.</summary>
    /// <param name="keys">The keys to order.</param>
    public static IReadOnlyList<string> Canonical(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>The routing-key attribute of <paramref name="eventClass"/>, or null.</summary>
    /// <param name="eventClass">The event class.</param>
    public static RoutingKeyAttribute? GetAttribute(Type? eventClass) =>
        eventClass?.GetCustomAttribute<RoutingKeyAttribute>(inherit: true);

    /// <summary>
    /// True when the class of <paramref name="eventClass"/> provides a routing key: it carries
    /// <see cref="RoutingKeyAttribute"/>, or overrides <see cref="Event.GetRoutingKey"/>.
    /// </summary>
    /// <param name="eventClass">The event class.</param>
    public static bool IsProvidedBy(Type? eventClass)
    {
        if (eventClass is null)
            return false;
        if (GetAttribute(eventClass) is not null)
            return true;

        var method = eventClass.GetMethod(nameof(Event.GetRoutingKey), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        return method is not null && method.DeclaringType != typeof(Event) && typeof(IRoutingKeyProvider).IsAssignableFrom(eventClass);
    }

    /// <summary>
    /// The property <see cref="RoutingKeyAttribute"/> names on <paramref name="eventClass"/>, or
    /// null without the attribute. Throws when the property is missing, is not a string, or is
    /// sensitive.
    /// </summary>
    /// <param name="eventClass">The event class.</param>
    /// <exception cref="InvalidOperationException">The named property is not usable as a routing key.</exception>
    public static PropertyInfo? ResolveProperty(Type eventClass)
    {
        ArgumentNullException.ThrowIfNull(eventClass);
        var attribute = GetAttribute(eventClass);
        if (attribute is null)
            return null;

        var error = DescribePropertyProblem(eventClass, attribute.PropertyName, out var property);
        if (error is not null)
            throw new InvalidOperationException(error);
        return property;
    }

    /// <summary>
    /// Why the property <paramref name="propertyName"/> of <paramref name="eventClass"/> can't
    /// be a routing key, or null when it can.
    /// </summary>
    /// <param name="eventClass">The event class.</param>
    /// <param name="propertyName">The property the attribute names.</param>
    /// <param name="property">The resolved property, when it exists.</param>
    public static string? DescribePropertyProblem(Type eventClass, string propertyName, out PropertyInfo? property)
    {
        ArgumentNullException.ThrowIfNull(eventClass);
        property = eventClass.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
            return $"[RoutingKey(\"{propertyName}\")] on {eventClass.Name} references a property that does not exist.";
        if (property.PropertyType != typeof(string))
            return $"[RoutingKey(\"{propertyName}\")] on {eventClass.Name} references a {property.PropertyType.Name} property; a routing key must be a string.";
        if (property.IsDefined(typeof(SensitiveAttribute), inherit: true) || eventClass.IsDefined(typeof(SensitiveAttribute), inherit: true))
            return $"[RoutingKey(\"{propertyName}\")] on {eventClass.Name} references a sensitive property; a routing key travels unmasked.";
        return null;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_.\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
