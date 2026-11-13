namespace NimBus.Core.Events;

/// <summary>
/// A message that can name its routing key (Spec 045). <see cref="Event"/> implements it from
/// <see cref="RoutingKeyAttribute"/>; the publisher stamps the key on the message and the
/// subscriber's receive check compares it with what the endpoint declared.
/// </summary>
public interface IRoutingKeyProvider
{
    /// <summary>The routing key, or null when the message has none.</summary>
    string? GetRoutingKey();
}
