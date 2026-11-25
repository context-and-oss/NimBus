namespace NimBus.Adapter.Mcp;

/// <summary>
/// What an adapter exposes of its system to the adapter MCP server (Spec 051 §7). The adapter
/// author implements it; every member only reads.
/// </summary>
/// <remarks>
/// The server cannot limit a caller to the records of one message: a caller with the read role can
/// read any record of a served entity by key. Leave an entity out of <see cref="Entities"/> to keep
/// it off the server.
/// </remarks>
public interface IAdapterSystem
{
    /// <summary>
    /// The entities the server serves, such as <c>customer</c>. A record or a last call of an
    /// entity not listed here is never served. Callers' names are matched ignoring case.
    /// </summary>
    IReadOnlyCollection<string> Entities { get; }

    /// <summary>
    /// The record as it is now in the system, or null when the system has no record under the key.
    /// Return the typed object the adapter already uses: properties marked
    /// <see cref="NimBus.Core.Messages.PII.SensitiveAttribute"/> are masked before the record
    /// leaves the server, as NimBus masks event payloads. An untyped value (JSON) is not masked.
    /// </summary>
    /// <param name="entity">An entity from <see cref="Entities"/>, as listed there.</param>
    /// <param name="key">The record's key in the system.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    Task<object?> GetRecordAsync(string entity, string key, CancellationToken cancellationToken);

    /// <summary>
    /// How the adapter maps an event type to the system's fields and the settings it runs with, or
    /// null when the adapter does not handle the event type.
    /// </summary>
    /// <param name="eventTypeId">The NimBus event type id, the unqualified class name.</param>
    AdapterMapping? GetMapping(string eventTypeId);

    /// <summary>Whether the system is reachable from the adapter now.</summary>
    /// <param name="cancellationToken">Cancelled when the caller goes away.</param>
    Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken);
}

/// <summary>How an adapter maps one event type to its system.</summary>
public sealed record AdapterMapping
{
    /// <summary>The event's fields and the system field each one is written to.</summary>
    public IReadOnlyList<AdapterFieldMapping> Fields { get; init; } = [];

    /// <summary>
    /// The settings the adapter runs with for this event type, such as a company or an upsert
    /// switch. Never put a secret here: settings are returned as they are.
    /// </summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
}

/// <summary>One mapped field.</summary>
/// <param name="EventPath">The field's path in the event payload, such as <c>address.city</c>.</param>
/// <param name="SystemField">The system field it is written to.</param>
/// <param name="Note">How the value is changed on the way, if it is.</param>
public sealed record AdapterFieldMapping(string EventPath, string SystemField, string? Note = null);

/// <summary>Whether the system is reachable from the adapter.</summary>
/// <param name="Reachable">True when the system answered.</param>
/// <param name="Message">What the check found, such as a response time or the error's kind. No secrets.</param>
public sealed record AdapterHealth(bool Reachable, string? Message = null);
