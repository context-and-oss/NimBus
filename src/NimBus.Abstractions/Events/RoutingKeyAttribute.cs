using System;

namespace NimBus.Core.Events;

/// <summary>
/// Names the payload property whose value routes a message to the consumers that claim it
/// (Spec 045). NimBus stamps the value on the message as the <c>RoutingKey</c> application
/// property, and a consumer declared with <c>Consumes&lt;TEvent&gt;(routingKeys: …)</c> only
/// receives the messages whose key it claims.
/// <code>[RoutingKey(nameof(ErpInstance))]</code>
/// The property must be a <see cref="string"/> and must not be marked sensitive: the key travels
/// as a plain property into the broker, traces and dead-letter reasons.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class RoutingKeyAttribute : Attribute
{
    /// <summary>Creates the attribute for <paramref name="propertyName"/>.</summary>
    /// <param name="propertyName">Name of the string property that holds the key.</param>
    public RoutingKeyAttribute(string propertyName)
    {
        PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
    }

    /// <summary>Name of the string property that holds the key.</summary>
    public string PropertyName { get; }

    /// <summary>
    /// When true, the platform may have keyed consumers of this event type without an
    /// unmatched consumer: a message whose key no consumer claims is then dropped by Service
    /// Bus, as a message with no consumer is today. Defaults to false, so a missing unmatched
    /// consumer fails validation. Not allowed on a command.
    /// </summary>
    public bool DropUnmatched { get; set; }
}
