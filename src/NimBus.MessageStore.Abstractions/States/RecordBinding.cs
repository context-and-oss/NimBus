namespace NimBus.MessageStore.States;

/// <summary>
/// A record binding (Spec 051 §5): for one event type, the value at <see cref="PayloadPath"/> in the
/// message is the key of <see cref="Entity"/> in this endpoint's adapter. An agent reads only the
/// records a message names through its bindings. A binding tells which record a message names; it
/// does not vouch for the message. Owner-edited, audited with the endpoint's metadata.
/// </summary>
public class RecordBinding
{
    /// <summary>The event type whose messages carry the key.</summary>
    public string EventTypeId { get; set; } = null!;

    /// <summary>A dotted JSON path into the payload, for example <c>$.AccountId</c>.</summary>
    public string PayloadPath { get; set; } = null!;

    /// <summary>The entity the adapter serves under this name, for example <c>customer</c>.</summary>
    public string Entity { get; set; } = null!;
}
