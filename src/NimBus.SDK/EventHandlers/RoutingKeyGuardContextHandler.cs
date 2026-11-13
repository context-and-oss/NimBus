using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.SDK.EventHandlers;

/// <summary>
/// The receive check of Spec 045 §5. Before the handler runs, for an event type the endpoint
/// takes only for some routing keys, the key derived from the payload must be one of them; and
/// whenever the message carries a <c>RoutingKey</c> property, it must equal the payload's key.
/// Either failure dead-letters the message with a <see cref="RoutingKeyRejectedException"/>,
/// without blocking its session. The bus filter alone is not trusted: a rule rebuilt by older
/// code silently loses it.
/// </summary>
/// <remarks>
/// The payload is deserialised here a second time, only for event types the check covers. A
/// handler registered by string key has no CLR type to derive a key from, so for it the
/// <c>RoutingKey</c> property, when present, must be one of the declared keys.
/// </remarks>
internal sealed class RoutingKeyGuardContextHandler : IEventContextHandler
{
    private readonly IEventContextHandler _inner;
    private readonly string _endpointId;
    private readonly IReadOnlyDictionary<string, RoutingKeyPolicy> _policies;

    internal RoutingKeyGuardContextHandler(IEventContextHandler inner, string endpointId, IReadOnlyDictionary<string, RoutingKeyPolicy> policies)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _endpointId = endpointId;
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
    }

    /// <inheritdoc />
    public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var eventTypeId = context.EventTypeId;
        if (!string.IsNullOrEmpty(eventTypeId) && _policies.TryGetValue(eventTypeId, out var policy))
            Check(context, eventTypeId, policy);
        return _inner.Handle(context, cancellationToken);
    }

    private void Check(IMessageContext context, string eventTypeId, RoutingKeyPolicy policy)
    {
        var property = context.RoutingKey;
        if (string.IsNullOrEmpty(property))
            property = null;

        if (policy.EventClass is null)
        {
            // String-keyed handler: only the property can be checked.
            if (policy.Keys is not null && property is not null && !policy.Keys.Contains(property, StringComparer.Ordinal))
                throw Reject($"Routing key '{property}' is not accepted by endpoint {_endpointId} (accepts: {string.Join(", ", policy.Keys)}).");
            return;
        }

        var payloadKey = PayloadKey(context, policy.EventClass, out var unreadable);
        if (unreadable)
            return; // The handler reports the unreadable payload itself.

        if (property is not null && !string.Equals(property, payloadKey, StringComparison.Ordinal))
        {
            throw Reject(
                $"The message's RoutingKey property '{property}' does not match the routing key '{payloadKey ?? "<none>"}' " +
                $"in the {eventTypeId} payload received by endpoint {_endpointId}.");
        }

        if (policy.Keys is not null && (payloadKey is null || !policy.Keys.Contains(payloadKey, StringComparer.Ordinal)))
        {
            var keyText = payloadKey is null ? "No routing key" : $"Routing key '{payloadKey}'";
            throw Reject($"{keyText} is not accepted by endpoint {_endpointId} (accepts: {string.Join(", ", policy.Keys)}).");
        }
    }

    private static string? PayloadKey(IMessageContext context, Type eventClass, out bool unreadable)
    {
        unreadable = false;
        object? payload;
        try
        {
            var json = context.MessageContent?.EventContent?.EventJson;
            payload = json is null ? null : JsonConvert.DeserializeObject(json, eventClass, Constants.CreateSafeJsonSettings());
        }
        catch (JsonException)
        {
            unreadable = true;
            return null;
        }

        if (payload is null)
        {
            unreadable = true;
            return null;
        }

        try
        {
            var key = (payload as IRoutingKeyProvider)?.GetRoutingKey();
            return string.IsNullOrEmpty(key) ? null : key;
        }
        catch (InvalidOperationException exception)
        {
            throw new PermanentFailureException(
                new RoutingKeyRejectedException($"The routing key of {eventClass.Name} could not be read: {exception.Message}", exception));
        }
    }

    private static PermanentFailureException Reject(string reason) =>
        new(new RoutingKeyRejectedException(reason));

    /// <summary>
    /// Wraps <paramref name="inner"/> in the receive check when the subscriber has anything to
    /// enforce; otherwise returns it unchanged. Used by every subscriber composition, so the
    /// in-memory test transport checks exactly what the hosted one does.
    /// </summary>
    /// <param name="inner">The handler provider.</param>
    /// <param name="builder">The subscriber builder.</param>
    /// <param name="serviceProvider">The container, for an optional <see cref="IPlatform"/>.</param>
    /// <param name="endpointId">The subscriber's endpoint id.</param>
    internal static IEventContextHandler Decorate(
        IEventContextHandler inner,
        Extensions.NimBusSubscriberBuilder builder,
        IServiceProvider serviceProvider,
        string endpointId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var policies = CreatePolicies(
            builder.HandlerRegistrations,
            builder.RoutingKeyEnforcement,
            serviceProvider.GetService(typeof(IPlatform)) as IPlatform,
            endpointId);
        return policies is null || policies.Count == 0
            ? inner
            : new RoutingKeyGuardContextHandler(inner, endpointId, policies);
    }

    /// <summary>
    /// The policies for one subscriber, or null when nothing is checked: no explicit
    /// enforcement, no endpoint found on a registered platform, and no unmatched handler.
    /// </summary>
    /// <param name="registrations">The subscriber's handler registrations.</param>
    /// <param name="enforcement">What the builder configured.</param>
    /// <param name="platform">A platform registered in the container, or null.</param>
    /// <param name="endpointId">The subscriber's endpoint id.</param>
    internal static IReadOnlyDictionary<string, RoutingKeyPolicy>? CreatePolicies(
        IEnumerable<Extensions.NimBusSubscriberBuilder.HandlerRegistration> registrations,
        RoutingKeyEnforcement enforcement,
        IPlatform? platform,
        string endpointId)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(enforcement);

        var endpoint = enforcement.Endpoint
            ?? platform?.Endpoints.FirstOrDefault(candidate => string.Equals(candidate.Id, endpointId, StringComparison.Ordinal));
        if (endpoint is null && enforcement.Keys is null && enforcement.UnmatchedEventTypeIds.Count == 0)
            return null;

        var policies = new Dictionary<string, RoutingKeyPolicy>(StringComparer.Ordinal);
        foreach (var registration in registrations.Where(registration => !string.IsNullOrEmpty(registration.EventTypeId)))
        {
            var eventTypeId = registration.EventTypeId!;
            var eventClass = registration.EventType;
            var keyed = eventClass is not null && RoutingKeys.IsProvidedBy(eventClass);

            if (enforcement.UnmatchedEventTypeIds.Contains(eventTypeId))
            {
                if (keyed)
                    policies[eventTypeId] = new RoutingKeyPolicy(eventClass, Keys: null);
                continue;
            }

            if (enforcement.Keys is not null)
            {
                if (keyed)
                    policies[eventTypeId] = new RoutingKeyPolicy(eventClass, enforcement.Keys);
                continue;
            }

            var consumed = endpoint?.EventTypesConsumed.FirstOrDefault(type => string.Equals(type.Id, eventTypeId, StringComparison.Ordinal));
            if (consumed is null)
                continue;

            var declared = endpoint!.GetRoutingKeys(consumed);
            if (declared.Count > 0)
                policies[eventTypeId] = new RoutingKeyPolicy(keyed ? eventClass : null, RoutingKeys.Canonical(declared));
            else if (keyed)
                policies[eventTypeId] = new RoutingKeyPolicy(eventClass, Keys: null);
        }

        return policies;
    }
}

/// <summary>
/// What the receive check enforces for one event type: <paramref name="Keys"/> are the keys
/// accepted, or null to accept any key (the unmatched consumer, or an unkeyed consumer of a
/// keyed type), in which case only the property/payload agreement is checked.
/// <paramref name="EventClass"/> is null for a string-keyed handler.
/// </summary>
internal sealed record RoutingKeyPolicy(Type? EventClass, IReadOnlyList<string>? Keys);

/// <summary>What a subscriber builder configured for the receive check (Spec 045 §5).</summary>
internal sealed class RoutingKeyEnforcement
{
    /// <summary>The endpoint whose declarations are enforced, from <c>EnforceRoutingKeys&lt;TEndpoint&gt;()</c>.</summary>
    public IEndpoint? Endpoint { get; set; }

    /// <summary>Keys enforced for every keyed event type, from <c>EnforceRoutingKeys(keys)</c>.</summary>
    public IReadOnlyList<string>? Keys { get; set; }

    /// <summary>Event types registered with <c>AddUnmatchedHandler&lt;TEvent&gt;()</c>.</summary>
    public HashSet<string> UnmatchedEventTypeIds { get; } = new(StringComparer.Ordinal);
}
