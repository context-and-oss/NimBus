using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.Core.Endpoints;
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
/// (configuration-loaded or dynamically-typed events) are skipped wherever a rule needs
/// the CLR type.
/// </remarks>
public static class PlatformValidation
{
    /// <summary>
    /// Validates that every <see cref="Command"/>-derived event type in the catalog has a
    /// single recipient. Unkeyed: exactly one consumer, declaring no routing keys. Keyed
    /// (Spec 045 §7): the command class provides a routing key, every consumer is keyed or
    /// unmatched, each key is claimed by exactly one consumer, exactly one consumer is the
    /// unmatched one, and the class doesn't set <see cref="RoutingKeyAttribute.DropUnmatched"/>.
    /// </summary>
    /// <param name="platform">The platform catalog to validate.</param>
    /// <returns>One error message per violation; empty when the catalog is valid.</returns>
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
                .DistinctBy(endpoint => endpoint.Id)
                .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal)
                .ToList();

            if (consumers.Count == 0)
            {
                errors.Add(
                    $"Command '{eventType.Id}' has no consuming endpoint. " +
                    "A command must have exactly one consumer (with none, every send dead-letters).");
                continue;
            }

            var keyed = consumers.Any(endpoint => endpoint.GetRoutingKeys(eventType).Count > 0 || endpoint.IsUnmatchedConsumer(eventType));
            if (!keyed)
            {
                if (consumers.Count > 1)
                {
                    errors.Add(
                        $"Command '{eventType.Id}' has {consumers.Count} consumers: {string.Join(", ", consumers.Select(endpoint => endpoint.Id))}. " +
                        "A command must have exactly one consumer, or one consumer per routing key.");
                }
                continue;
            }

            ValidateKeyedCommand(eventType, clrType, consumers, errors);
        }

        return errors;
    }

    /// <summary>
    /// Validates routing-key declarations (Spec 045 §8): key format, case-only duplicates, an
    /// endpoint declaring keys and unmatched for one type, a keyed type without an unmatched
    /// consumer (unless its class sets <see cref="RoutingKeyAttribute.DropUnmatched"/>), keys
    /// declared for a type whose class provides no routing key, and a routing-key property that
    /// is not a string or is sensitive.
    /// </summary>
    /// <param name="platform">The platform catalog to validate.</param>
    /// <returns>One error message per violation; empty when the catalog is valid.</returns>
    public static IReadOnlyList<string> ValidateRoutingKeys(IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var errors = new List<string>();
        foreach (var eventType in platform.EventTypes.OrderBy(eventType => eventType.Id, StringComparer.Ordinal))
        {
            var clrType = eventType.GetEventClassType();
            var attribute = RoutingKeys.GetAttribute(clrType);
            if (clrType is not null && attribute is not null)
            {
                var problem = RoutingKeys.DescribePropertyProblem(clrType, attribute.PropertyName, out _);
                if (problem is not null)
                    errors.Add(problem);
            }

            var consumers = platform.GetConsumers(eventType)
                .DistinctBy(endpoint => endpoint.Id)
                .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal)
                .ToList();

            var claimed = new List<string>();
            var hasUnmatched = false;
            foreach (var consumer in consumers)
            {
                var keys = consumer.GetRoutingKeys(eventType);
                var unmatched = consumer.IsUnmatchedConsumer(eventType);
                hasUnmatched |= unmatched;

                if (keys.Count > 0 && unmatched)
                {
                    errors.Add(
                        $"Endpoint '{consumer.Id}' declares routing keys and unmatched for '{eventType.Id}'. Declare one or the other.");
                }

                foreach (var key in keys.Where(key => !RoutingKeys.IsValid(key)))
                {
                    errors.Add(
                        $"Endpoint '{consumer.Id}' declares routing key '{key}' for '{eventType.Id}'; " +
                        $"a key is 1 to {RoutingKeys.MaxLength} characters from [A-Za-z0-9_.-].");
                }

                if ((keys.Count > 0 || unmatched) && clrType is not null && !RoutingKeys.IsProvidedBy(clrType))
                {
                    errors.Add(
                        $"Endpoint '{consumer.Id}' declares routing for '{eventType.Id}', but {clrType.Name} provides no routing key. " +
                        "Add [RoutingKey(nameof(Property))] to the class or override GetRoutingKey().");
                }

                claimed.AddRange(keys);
            }

            foreach (var group in claimed
                .Distinct(StringComparer.Ordinal)
                .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                errors.Add(
                    $"Routing keys {string.Join(", ", group.Order(StringComparer.Ordinal).Select(key => $"'{key}'"))} for '{eventType.Id}' differ only by case. " +
                    "Keys are compared ordinally; pick one spelling.");
            }

            if (claimed.Count > 0 && !hasUnmatched && attribute?.DropUnmatched != true)
            {
                errors.Add(
                    $"'{eventType.Id}' has keyed consumers but no unmatched consumer, so a message with an unclaimed key would be dropped. " +
                    "Declare ConsumesUnmatched<T>() on one endpoint, or set [RoutingKey(..., DropUnmatched = true)] to accept the drop.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Runs every catalog rule (<see cref="ValidateCommandConsumers"/> and
    /// <see cref="ValidateRoutingKeys"/>) and throws when any is violated.
    /// </summary>
    /// <param name="platform">The platform catalog to validate.</param>
    /// <exception cref="InvalidOperationException">Thrown with all violations joined.</exception>
    public static void EnsureValid(IPlatform platform)
    {
        var errors = ValidateCommandConsumers(platform).Concat(ValidateRoutingKeys(platform)).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(System.Environment.NewLine, errors));
        }
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
            throw new InvalidOperationException(string.Join(System.Environment.NewLine, errors));
        }
    }

    private static void ValidateKeyedCommand(IEventType eventType, Type clrType, List<IEndpoint> consumers, List<string> errors)
    {
        if (!RoutingKeys.IsProvidedBy(clrType))
        {
            errors.Add(
                $"Command '{eventType.Id}' has keyed consumers but {clrType.Name} provides no routing key.");
        }

        if (RoutingKeys.GetAttribute(clrType)?.DropUnmatched == true)
        {
            errors.Add(
                $"Command '{eventType.Id}' sets DropUnmatched; a command must reach a consumer, so it needs an unmatched consumer instead.");
        }

        var unkeyed = consumers
            .Where(endpoint => endpoint.GetRoutingKeys(eventType).Count == 0 && !endpoint.IsUnmatchedConsumer(eventType))
            .Select(endpoint => endpoint.Id)
            .ToList();
        if (unkeyed.Count > 0)
        {
            errors.Add(
                $"Command '{eventType.Id}' mixes keyed and unkeyed consumers ({string.Join(", ", unkeyed)} takes every key). " +
                "Every consumer of a keyed command must declare keys or be the unmatched consumer.");
        }

        var unmatched = consumers.Where(endpoint => endpoint.IsUnmatchedConsumer(eventType)).Select(endpoint => endpoint.Id).ToList();
        if (unmatched.Count != 1)
        {
            errors.Add(
                $"Command '{eventType.Id}' has {unmatched.Count} unmatched consumers{(unmatched.Count > 0 ? $" ({string.Join(", ", unmatched)})" : "")}. " +
                "A keyed command needs exactly one.");
        }

        foreach (var group in consumers
            .SelectMany(endpoint => endpoint.GetRoutingKeys(eventType).Select(key => (Key: key, Endpoint: endpoint.Id)))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            errors.Add(
                $"Command '{eventType.Id}' routing key '{group.Key}' is claimed by {string.Join(", ", group.Select(entry => entry.Endpoint))}. " +
                "Each key of a command must have exactly one consumer.");
        }
    }
}
