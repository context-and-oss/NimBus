namespace NimBus.Core.Events;

public interface IProperty
{
    string Name { get; }
    string TypeName { get; }
    string TypeFullName { get; }
    string? Description { get; }
    bool IsRequired { get; }
}
