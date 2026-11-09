using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.Core.Events;

namespace NimBus.Core;

/// <summary>
/// Catalog-level validation rules for an <see cref="IPlatform"/>.
/// </summary>
/// <remarks>
/// Validation is a static helper rather than part of <see cref="Platform"/> construction
/// because endpoints are registered incrementally via <c>AddEndpoint</c>; only a caller
/// that knows the catalog is complete (e.g. a topology provisioner) can validate it.
/// Event types whose <see cref="IEventType.GetEventClassType"/> returns <c>null</c>
/// (configuration-loaded or dynamically-typed events) are skipped — command semantics
/// can only be detected on CLR-typed catalogs.
/// </remarks>
public static class PlatformValidation
{
    /// <summary>
    /// Validates that every <see cref="Command"/>-derived event type in the catalog has
    /// exactly one consuming endpoint.
    /// </summary>
    /// <param name="platform">The platform catalog to validate.</param>
    /// <returns>One error message per violating command type; empty when the catalog is valid.</returns>
    public static IReadOnlyList<string> ValidateCommandConsumers(IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var errors = new List<string>();
        foreach (var eventType in platform.EventTypes)
        {
            var clrType = eventType.GetEventClassType();
            if (clrType is null || !typeof(Command).IsAssignableFrom(clrType))
                continue;

            var consumers = platform.GetConsumers(eventType)
                .Select(endpoint => endpoint.Id)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            if (consumers.Count == 0)
            {
                errors.Add(
                    $"Command '{eventType.Id}' has no consuming endpoint. " +
                    "A command must have exactly one consumer (with none, every send dead-letters).");
            }
            else if (consumers.Count > 1)
            {
                errors.Add(
                    $"Command '{eventType.Id}' has {consumers.Count} consumers: {string.Join(", ", consumers)}. " +
                    "A command must have exactly one consumer.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Validates command consumers and throws when the catalog violates the
    /// exactly-one-consumer rule.
    /// </summary>
    /// <param name="platform">The platform catalog to validate.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown with all violations joined when any command has zero or multiple consumers.
    /// </exception>
    public static void EnsureCommandConsumers(IPlatform platform)
    {
        var errors = ValidateCommandConsumers(platform);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }
}
