using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;

namespace NimBus.Core.Events;

public abstract class Event : IEvent, IRoutingKeyProvider
{
    public EventValidationResult TryValidate()
    {
        var validationResults = new List<ValidationResult>();
        return new EventValidationResult(Validator.TryValidateObject(this, new ValidationContext(this), validationResults, true), validationResults);
    }

    public void Validate()
    {
        Validator.ValidateObject(this, new ValidationContext(this), true);
    }

    public IEventType GetEventType() => new EventType(GetType());

    public virtual string GetSessionId()
    {
        var attr = GetType().GetCustomAttribute<SessionKeyAttribute>();
        if (attr is null)
            return Guid.NewGuid().ToString();

        var prop = GetType().GetProperty(attr.PropertyName);
        if (prop is null)
            throw new InvalidOperationException(
                $"[SessionKey(\"{attr.PropertyName}\")] on {GetType().Name} references a property that does not exist.");

        return prop.GetValue(this)?.ToString() ?? Guid.NewGuid().ToString();
    }

    /// <summary>
    /// The routing key (Spec 045): the value of the property <see cref="RoutingKeyAttribute"/>
    /// names, or null when the class has no attribute or the value is null or empty. Override
    /// to compute a key from other properties.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The attribute names a property that is missing, not a string, or sensitive.
    /// </exception>
    public virtual string? GetRoutingKey()
    {
        var property = RoutingKeys.ResolveProperty(GetType());
        if (property is null)
            return null;

        var value = property.GetValue(this) as string;
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
