using NimBus.Core.Events;
using System;
using System.Collections.Generic;

namespace NimBus.Core.Endpoints;

public abstract class Endpoint : IEndpoint
{
    private Dictionary<string, IEventType> _eventTypesProduced;
    private Dictionary<string, IEventType> _eventTypesConsumed;
    private List<IRoleAssignment> roleAssignments;
    private readonly Dictionary<string, IReadOnlyList<string>> _routingKeysConsumed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unmatchedConsumed = new(StringComparer.Ordinal);

    protected Endpoint()
    {
        _eventTypesProduced = new Dictionary<string, IEventType>();
        _eventTypesConsumed = new Dictionary<string, IEventType>();
        roleAssignments = new List<IRoleAssignment>();
    }

    public string Id => GetType().Name;
    public string Name => GetType().Name;
    public string? Namespace => GetType().Namespace;
    public string SecurityGroupName => $"azu-endpoint-{Id}";
    public virtual string? Description => null;
    public virtual ISystem? System => null;

    public IEnumerable<IEventType> EventTypesProduced => _eventTypesProduced.Values;
    public IEnumerable<IEventType> EventTypesConsumed => _eventTypesConsumed.Values;

    public IEnumerable<IRoleAssignment> RoleAssignments => roleAssignments;

    /// <summary>
    /// Register endpoint as producer of <typeparamref name="TEvent"/>.
    /// </summary>
    protected void Produces<TEvent>()
        where TEvent : IEvent
    {
        var eventType = new EventType<TEvent>();
        _eventTypesProduced.Add(eventType.Id, eventType);
    }

    /// <summary>
    /// Register endpoint as consumer of <typeparamref name="TEvent"/>.
    /// </summary>
    protected void Consumes<TEvent>()
        where TEvent : IEvent
    {
        var eventType = new EventType<TEvent>();
        _eventTypesConsumed.Add(eventType.Id, eventType);
    }

    /// <summary>
    /// Register endpoint as consumer of <typeparamref name="TEvent"/> for the given routing keys
    /// only (Spec 045). Messages whose key the endpoint doesn't claim are not forwarded to it.
    /// </summary>
    /// <param name="routingKeys">One or more keys, each 1 to 64 characters from <c>[A-Za-z0-9_.-]</c>.</param>
    /// <exception cref="ArgumentException">No key is given, or a key is empty or malformed.</exception>
    protected void Consumes<TEvent>(params string[] routingKeys)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(routingKeys);
        if (routingKeys.Length == 0)
            throw new ArgumentException(
                $"Consumes<{typeof(TEvent).Name}>(routingKeys) needs at least one key. Call Consumes<{typeof(TEvent).Name}>() to take every key.",
                nameof(routingKeys));
        foreach (var key in routingKeys)
        {
            if (!RoutingKeys.IsValid(key))
                throw new ArgumentException(
                    $"Routing key '{key}' for {typeof(TEvent).Name} is not valid: use 1 to {RoutingKeys.MaxLength} characters from [A-Za-z0-9_.-].",
                    nameof(routingKeys));
        }

        var eventType = new EventType<TEvent>();
        _eventTypesConsumed.Add(eventType.Id, eventType);
        _routingKeysConsumed[eventType.Id] = RoutingKeys.Canonical(routingKeys);
    }

    /// <summary>
    /// Register endpoint as the unmatched consumer of <typeparamref name="TEvent"/> (Spec 045):
    /// it receives the messages whose routing key no consumer claims, and those with no key.
    /// </summary>
    protected void ConsumesUnmatched<TEvent>()
        where TEvent : IEvent
    {
        var eventType = new EventType<TEvent>();
        _eventTypesConsumed.Add(eventType.Id, eventType);
        _unmatchedConsumed.Add(eventType.Id);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> GetRoutingKeys(IEventType eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _routingKeysConsumed.TryGetValue(eventType.Id, out var keys) ? keys : Array.Empty<string>();
    }

    /// <inheritdoc />
    public bool IsUnmatchedConsumer(IEventType eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _unmatchedConsumed.Contains(eventType.Id);
    }

    protected void AssignRole(string principalId, Environment environment)
    {
        string env = Enum.GetName(environment)!;
        var roleAssignment = new RoleAssignment() { PrincipalId = principalId, Environment = env };
        roleAssignments.Add(roleAssignment);
    }
}

public interface IRoleAssignment
{
    string PrincipalId { get; }
    string Environment { get; }
}

public class RoleAssignment : IRoleAssignment
{
    public string PrincipalId { get; set; } = string.Empty;

    public string Environment { get; set; } = string.Empty;
}

public enum Environment
{
    SBDev,
    Dev,
    Test,
    UAT,
    Education,
    Stag,
    Prod,
}
