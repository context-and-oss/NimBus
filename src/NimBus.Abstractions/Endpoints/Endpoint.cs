using NimBus.Core.Events;
using System;
using System.Collections.Generic;

namespace NimBus.Core.Endpoints;

public abstract class Endpoint : IEndpoint
{
    private Dictionary<string, IEventType> _eventTypesProduced;
    private Dictionary<string, IEventType> _eventTypesConsumed;
    private List<IRoleAssignment> roleAssignments;

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
