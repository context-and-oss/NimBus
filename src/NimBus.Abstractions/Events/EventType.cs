using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace NimBus.Core.Events;

public class EventType<TEvent> : EventType, IEventType
    where TEvent : IEvent
{
    public EventType() : base(typeof(TEvent)) { }
}

public class EventType : IEventType
{
    private readonly Type _type;

    public EventType(Type type)
    {
        _type = type;
    }

    public string Id => _type.Name;

    public string Name => _type.Name;

    public string? Namespace => _type.Namespace;

    public string? Description => _type.GetCustomAttribute<DescriptionAttribute>()?.Description;

    public string? SessionKeyProperty => _type.GetCustomAttribute<SessionKeyAttribute>()?.PropertyName;

    public IEnumerable<IProperty> Properties =>
        _type.GetProperties()
        .Select(p => new Property(p));

    public override bool Equals(object? obj) =>
        obj is EventType et &&
        et._type == _type;

    public override int GetHashCode() =>
        _type.GetHashCode();

    public Type GetEventClassType() => _type;

    /// <summary>
    /// The sample instance an event class authors for catalog and documentation
    /// rendering, or null when it declares none.
    /// </summary>
    /// <remarks>
    /// Resolved by name and type rather than by declaration order. This used to
    /// take the first public field and cast it, which made a correct answer
    /// depend on where <c>Example</c> happened to sit in the file: a public
    /// const declared above it was returned instead and the cast threw, and a
    /// public instance field first threw from GetValue(null). Heartbeat still
    /// carries a comment about having to declare its Example above its
    /// EventTypeId const for exactly that reason. Nothing has to be ordered
    /// now, and an event class without an example reports null instead of
    /// throwing.
    /// </remarks>
    public IEvent? GetEventExample()
    {
        var staticFields = _type.GetFields(BindingFlags.Public | BindingFlags.Static);

        // Prefer the conventional name; fall back to any static field that is
        // actually an event, so a differently-named example still resolves.
        var field = Array.Find(staticFields, f => f.Name == "Example" && typeof(IEvent).IsAssignableFrom(f.FieldType))
            ?? Array.Find(staticFields, f => typeof(IEvent).IsAssignableFrom(f.FieldType));

        return field?.GetValue(null) as IEvent;
    }
}
