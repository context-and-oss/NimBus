using System;
using System.Collections.Generic;

namespace NimBus.Core.Events;

public interface IEventType
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }
    string? Namespace { get; }
    IEnumerable<IProperty> Properties { get; }
    Type GetEventClassType();
    IEvent? GetEventExample();
}
