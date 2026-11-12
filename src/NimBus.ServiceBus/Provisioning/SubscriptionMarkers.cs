namespace NimBus.ServiceBus.Provisioning;

/// <summary>
/// Values NimBus writes to a Service Bus subscription's <c>UserMetadata</c>. Each one is a
/// persisted convention on the Service Bus entity, shared by the WebApp's subscription admin
/// and <see cref="ServiceBusTopologyProvisioner"/>: changing a value strands every marker
/// already written with the old one. Compare against them with ordinal equality.
/// </summary>
public static class SubscriptionMarkers
{
    /// <summary>
    /// Set on a subscription's <c>UserMetadata</c> while a WebApp Purge drains a paused
    /// subscription, which is Active with its forward target detached for the duration.
    /// <c>nb topology apply</c> leaves a forwarder carrying it alone, so it does not re-attach
    /// forwarding mid-drain. The purge puts the original <c>UserMetadata</c> back when it ends;
    /// Pause and Resume clear a marker an interrupted purge left behind.
    /// </summary>
    public const string PurgeInProgress = "nimbus:purge-in-progress";
}
