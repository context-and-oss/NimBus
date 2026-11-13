using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;

namespace NimBus.ServiceBus.Provisioning;

/// <summary>
/// A rule the platform expects on a subscription, described rather than created.
/// <paramref name="Action"/> is null when the rule is a pure filter.
/// </summary>
public sealed record ExpectedRule(string Name, string Filter, string? Action = null)
{
    /// <summary>
    /// The routing-key shape of a forward rule (Spec 045), or null for a rule that doesn't
    /// filter on a routing key. The provisioner uses it to widen a rule before narrowing it.
    /// </summary>
    public RoutingRule? Routing { get; init; }
}

/// <summary>How a forward rule filters on the routing key (Spec 045).</summary>
public enum RoutingRuleMode
{
    /// <summary>The consumer takes only the listed keys.</summary>
    Keyed,

    /// <summary>The unmatched consumer: messages with no key, or with a key not in the list.</summary>
    Unmatched,
}

/// <summary>
/// The routing-key part of a forward rule (Spec 045). For <see cref="RoutingRuleMode.Keyed"/>,
/// <see cref="Keys"/> are the keys the consumer takes; for <see cref="RoutingRuleMode.Unmatched"/>,
/// the keys other consumers claim, which the rule excludes. Keys are distinct and in ordinal order.
/// </summary>
/// <param name="Mode">How the rule filters.</param>
/// <param name="Keys">The keys, canonical.</param>
public sealed record RoutingRule(RoutingRuleMode Mode, IReadOnlyList<string> Keys)
{
    /// <summary>Compares mode and keys by value.</summary>
    /// <param name="other">The other rule.</param>
    public bool Equals(RoutingRule? other) =>
        other is not null && Mode == other.Mode && Keys.SequenceEqual(other.Keys, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Mode);
        foreach (var key in Keys)
            hash.Add(key, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// One event type a consumer takes from a producer, with its routing-key shape (Spec 045);
/// <paramref name="Routing"/> is null for an unkeyed consumer.
/// </summary>
/// <param name="EventTypeId">The event type id; also the forward rule's name.</param>
/// <param name="Routing">How the rule filters on the routing key, or null.</param>
public sealed record ForwardedEventType(string EventTypeId, RoutingRule? Routing = null);

/// <summary>
/// A subscription the platform expects on a topic, described rather than created.
/// </summary>
/// <param name="Name">Subscription name.</param>
/// <param name="RequiresSession">Whether the subscription is session-enabled.</param>
/// <param name="ForwardTo">Auto-forward destination, or null for a terminal subscription.</param>
/// <param name="Rules">Rules the platform attaches, in provisioning order.</param>
/// <param name="KeepDefaultRule">
/// True when Service Bus's auto-created <c>$Default</c> true-filter is deliberately
/// left in place (the Resolver topic's own subscription); false everywhere else,
/// where a true-filter would hand a subscription everything published to the topic.
/// </param>
/// <param name="DefaultMessageTimeToLive">
/// Explicit entity TTL, or null to accept the namespace default. Only the
/// <c>Deferred</c> subscription sets one — parked sessions must survive far longer
/// than an ordinary message.
/// </param>
/// <param name="DeadLetterOnMessageExpiration">
/// True when a message that expires must move to the dead-letter queue instead of
/// vanishing. Only the <c>Deferred</c> subscription sets it: an expired parked message
/// is an event the endpoint still owes, and must stay visible to an operator.
/// </param>
public sealed record ExpectedSubscription(
    string Name,
    bool RequiresSession,
    string? ForwardTo,
    IReadOnlyList<ExpectedRule> Rules,
    bool KeepDefaultRule = false,
    TimeSpan? DefaultMessageTimeToLive = null,
    bool DeadLetterOnMessageExpiration = false);

/// <summary>
/// The NimBus Service Bus topology, expressed declaratively.
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of truth for <em>what the topology should look
/// like</em>. <see cref="ServiceBusTopologyProvisioner"/> consumes it when laying a
/// namespace down; the WebApp's subscription admin consumes it to decide whether a
/// subscription it found on the bus is one the platform knows how to rebuild, and
/// to rebuild it after an operator deletes it to clear a backlog.
/// </para>
/// <para>
/// Every filter and action string here is the literal the provisioner used to
/// interpolate inline, so a descriptor-driven rebuild produces a rule that is
/// byte-identical to a provisioned one. <c>RuleMatches</c> compares ordinally, so
/// any drift churns the rule on the next <c>nb topology apply</c>.
/// </para>
/// </remarks>
public static class TopologyDescriptor
{
    /// <summary>Rule name Service Bus auto-creates on every new subscription.</summary>
    public const string DefaultRuleName = "$Default";

    /// <summary>Suffix of the per-endpoint request/reply subscription.</summary>
    public const string ReplySubscriptionSuffix = "-reply";

    /// <summary>Subscription-name prefix for dynamically-typed forwards (spec 022 D5).</summary>
    public const string DynamicForwardPrefix = "AgentDyn-";

    /// <summary>
    /// Deferred-subscription TTL on a real namespace: unbounded, the Service Bus default.
    /// A parked message waits until the event blocking its session is resolved, which can
    /// take an operator weeks. The former 14-day cap expired such messages silently, leaving
    /// the tracking row Deferred with nothing to replay when the session was unblocked.
    /// </summary>
    public static readonly TimeSpan DeferredMessageTimeToLive = TimeSpan.MaxValue;

    /// <summary>
    /// Deferred-subscription TTL on the official emulator, whose upper bound is
    /// conservative and not pinned in the public docs. An hour is long enough for
    /// sample and CI smoke runs and well inside any plausible limit.
    /// </summary>
    public static readonly TimeSpan DeferredMessageTimeToLiveOnEmulator = TimeSpan.FromHours(1);

    /// <summary>
    /// True when <paramref name="topicName"/> is one of the platform's own topics
    /// rather than an endpoint topic. NimBus provisions exactly one:
    /// <see cref="Constants.ResolverId"/>.
    /// </summary>
    public static bool IsSystemTopic(string? topicName) =>
        string.Equals(topicName, Constants.ResolverId, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── Shared SQL templates ─────────────────────────

    /// <summary>Filter matching every message addressed to <paramref name="to"/>.</summary>
    public static string ToFilter(string to) => $"user.To = '{to}'";

    /// <summary>
    /// Filter for a forwarder rule. It matches only ORIGINAL publishes of
    /// <paramref name="eventTypeId"/>, through two guards:
    /// <list type="bullet">
    /// <item><description>
    /// <c>user.MessageType = 'EventRequest'</c> excludes the endpoint's own control
    /// traffic about an event of that type. Responses to the Resolver, RetryRequest,
    /// ProcessDeferredRequest, ContinuationRequest and CLI resubmissions all carry the
    /// inbound <c>EventTypeId</c> and no <c>From</c>; without this guard they would be
    /// forwarded to the consumer as if they were events, and its dead-letter response
    /// forwarded back, round after round.
    /// </description></item>
    /// <item><description>
    /// <c>user.From IS NULL</c> excludes already-forwarded copies (only
    /// <see cref="ForwardAction"/> populates <c>From</c> on the forward path), parked
    /// copies and deferred replays, which carry a <c>From</c> marker, and Manager
    /// traffic. Without it an event type produced AND consumed by both endpoints
    /// forwards back and forth until Service Bus's MaxHopCount dead-letters it.
    /// </description></item>
    /// </list>
    /// A producer outside NimBus must stamp <c>MessageType = EventRequest</c> for its
    /// publishes to be forwarded.
    /// </summary>
    public static string ForwardFilter(string eventTypeId) =>
        $"user.EventTypeId = '{eventTypeId}' AND user.MessageType = '{nameof(MessageType.EventRequest)}' AND user.From IS NULL";

    // ───────────────────────── Routing keys (Spec 045) ─────────────────────────

    /// <summary>The Service Bus SQL filter limit.</summary>
    public const int MaxFilterLength = 1024;

    private const string RoutingKeyProperty = "user.RoutingKey";
    private const string UnmatchedOpen = " AND (" + RoutingKeyProperty + " IS NULL OR " + RoutingKeyProperty + " NOT IN (";
    private const string UnmatchedClose = "))";

    /// <summary>
    /// Filter for a consumer that takes <paramref name="eventTypeId"/> only for
    /// <paramref name="keys"/>: <see cref="ForwardFilter(string)"/> plus <c>user.RoutingKey = 'k'</c>,
    /// or <c>IN (…)</c> for several keys, written in ordinal order.
    /// </summary>
    /// <param name="eventTypeId">The event type id.</param>
    /// <param name="keys">One or more declared keys.</param>
    /// <exception cref="ArgumentException">No key, or a key outside the declared-key format.</exception>
    public static string KeyedForwardFilter(string eventTypeId, IEnumerable<string> keys)
    {
        var canonical = CheckedKeys(keys);
        if (canonical.Count == 0)
            throw new ArgumentException("A keyed forward filter needs at least one key.", nameof(keys));

        return canonical.Count == 1
            ? $"{ForwardFilter(eventTypeId)} AND {RoutingKeyProperty} = '{canonical[0]}'"
            : $"{ForwardFilter(eventTypeId)} AND {RoutingKeyProperty} IN ({KeyList(canonical)})";
    }

    /// <summary>
    /// Filter for the unmatched consumer of <paramref name="eventTypeId"/>: messages with no
    /// routing key, or with a key no consumer claims. With nothing claimed it is
    /// <see cref="ForwardFilter(string)"/> unchanged, so keyed events still flow before the first keyed
    /// consumer exists.
    /// </summary>
    /// <param name="eventTypeId">The event type id.</param>
    /// <param name="claimedKeys">Every key a consumer of the type claims.</param>
    public static string UnmatchedForwardFilter(string eventTypeId, IEnumerable<string> claimedKeys)
    {
        var canonical = CheckedKeys(claimedKeys);
        return canonical.Count == 0
            ? ForwardFilter(eventTypeId)
            : $"{ForwardFilter(eventTypeId)}{UnmatchedOpen}{KeyList(canonical)}{UnmatchedClose}";
    }

    /// <summary>The forward filter for <paramref name="eventTypeId"/> with an optional routing shape.</summary>
    /// <param name="eventTypeId">The event type id.</param>
    /// <param name="routing">The routing shape, or null for an unkeyed consumer.</param>
    public static string ForwardFilter(string eventTypeId, RoutingRule? routing) =>
        routing switch
        {
            null => ForwardFilter(eventTypeId),
            { Mode: RoutingRuleMode.Keyed } => KeyedForwardFilter(eventTypeId, routing.Keys),
            _ => UnmatchedForwardFilter(eventTypeId, routing.Keys),
        };

    /// <summary>
    /// Reads the routing shape back from a forward filter NimBus wrote. Returns false for a
    /// filter not in canonical form. On true, <paramref name="routing"/> is null when the filter
    /// is the plain <see cref="ForwardFilter(string)"/> (every key).
    /// </summary>
    /// <param name="filter">The existing filter text.</param>
    /// <param name="eventTypeId">The event type the rule forwards.</param>
    /// <param name="routing">The routing shape read, or null for every key.</param>
    public static bool TryReadRoutingKeys(string? filter, string eventTypeId, out RoutingRule? routing)
    {
        routing = null;
        if (filter is null)
            return false;

        var plain = ForwardFilter(eventTypeId);
        if (string.Equals(filter, plain, StringComparison.Ordinal))
            return true;
        if (!filter.StartsWith(plain, StringComparison.Ordinal))
            return false;

        var rest = filter[plain.Length..];
        var single = $" AND {RoutingKeyProperty} = '";
        var many = $" AND {RoutingKeyProperty} IN (";

        if (rest.StartsWith(single, StringComparison.Ordinal) && rest.EndsWith('\''))
            return TryKeys(rest[single.Length..^1], RoutingRuleMode.Keyed, requireQuotes: false, out routing);
        if (rest.StartsWith(many, StringComparison.Ordinal) && rest.EndsWith(')'))
            return TryKeys(rest[many.Length..^1], RoutingRuleMode.Keyed, requireQuotes: true, out routing);
        if (rest.StartsWith(UnmatchedOpen, StringComparison.Ordinal) && rest.EndsWith(UnmatchedClose, StringComparison.Ordinal))
            return TryKeys(rest[UnmatchedOpen.Length..^UnmatchedClose.Length], RoutingRuleMode.Unmatched, requireQuotes: true, out routing);
        return false;
    }

    /// <summary>
    /// The filter a forward rule holds between the widen and narrow passes of
    /// <c>nb topology apply</c> (Spec 045 §4): it takes every message either the existing or
    /// the final filter takes, so no message is unrouted while keys move. A keyed rule takes
    /// the union of old and new keys; an unmatched rule excludes only the keys excluded both
    /// before and after. An existing filter not in canonical form, or a change of mode, widens
    /// to the plain <see cref="ForwardFilter(string)"/>.
    /// </summary>
    /// <param name="eventTypeId">The event type the rule forwards.</param>
    /// <param name="existingFilter">The filter the rule has now.</param>
    /// <param name="final">The routing shape the rule ends with.</param>
    public static string WidenedForwardFilter(string eventTypeId, string? existingFilter, RoutingRule final)
    {
        ArgumentNullException.ThrowIfNull(final);
        if (!TryReadRoutingKeys(existingFilter, eventTypeId, out var existing) || existing is null || existing.Mode != final.Mode)
            return ForwardFilter(eventTypeId);

        if (final.Mode == RoutingRuleMode.Keyed)
            return KeyedForwardFilter(eventTypeId, existing.Keys.Union(final.Keys, StringComparer.Ordinal));

        return UnmatchedForwardFilter(eventTypeId, existing.Keys.Intersect(final.Keys, StringComparer.Ordinal));
    }

    /// <summary>
    /// One error per forward rule whose filter would exceed the Service Bus limit of
    /// <see cref="MaxFilterLength"/> characters, naming the producer, consumer and event type.
    /// </summary>
    /// <param name="platform">The platform to check.</param>
    public static IReadOnlyList<string> ValidateFilterLengths(IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        var errors = new List<string>();
        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            foreach (var subscription in ForEndpointTopic(endpoint, platform))
            {
                foreach (var rule in subscription.Rules.Where(rule => rule.Filter.Length > MaxFilterLength))
                {
                    errors.Add(
                        $"The forward rule for '{rule.Name}' from '{endpoint.Id}' to '{subscription.Name}' is " +
                        $"{rule.Filter.Length} characters; Service Bus allows {MaxFilterLength}. Declare fewer or shorter routing keys.");
                }
            }
        }

        return errors;
    }

    private static List<string> CheckedKeys(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var canonical = RoutingKeys.Canonical(keys).ToList();
        var invalid = canonical.FirstOrDefault(key => !RoutingKeys.IsValid(key));
        if (invalid is not null)
            throw new ArgumentException($"Routing key '{invalid}' is not valid and can't be written into a filter.", nameof(keys));
        return canonical;
    }

    private static string KeyList(IEnumerable<string> keys) => string.Join(", ", keys.Select(key => $"'{key}'"));

    private static bool TryKeys(string list, RoutingRuleMode mode, bool requireQuotes, out RoutingRule? routing)
    {
        routing = null;
        var parts = requireQuotes ? list.Split(", ", StringSplitOptions.None) : new[] { $"'{list}'" };
        var keys = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length < 3 || part[0] != '\'' || part[^1] != '\'')
                return false;
            var key = part[1..^1];
            if (!RoutingKeys.IsValid(key))
                return false;
            keys.Add(key);
        }

        var canonical = RoutingKeys.Canonical(keys);
        if (!canonical.SequenceEqual(keys, StringComparer.Ordinal))
            return false;

        routing = new RoutingRule(mode, canonical);
        return true;
    }

    /// <summary>
    /// The routing shape <paramref name="consumer"/> has for <paramref name="eventType"/>, or
    /// null when it takes every key.
    /// </summary>
    /// <param name="consumer">The consuming endpoint.</param>
    /// <param name="eventType">The event type.</param>
    /// <param name="platform">The platform, for the keys every consumer claims.</param>
    public static RoutingRule? RoutingFor(IEndpoint consumer, IEventType eventType, IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(platform);
        var keys = consumer.GetRoutingKeys(eventType);
        if (keys.Count > 0)
            return new RoutingRule(RoutingRuleMode.Keyed, RoutingKeys.Canonical(keys));
        if (consumer.IsUnmatchedConsumer(eventType))
            return new RoutingRule(RoutingRuleMode.Unmatched, RoutingKeys.Canonical(platform.GetClaimedRoutingKeys(eventType)));
        return null;
    }

    /// <summary>
    /// Action for a forwarder rule: re-stamps the message as coming from the
    /// producing endpoint and addressed to the consumer, with a fresh EventId per
    /// fan-out copy.
    /// </summary>
    public static string ForwardAction(string producerEndpointId, string consumerEndpointId) =>
        $"SET user.From = '{producerEndpointId}'; SET user.EventId = newid(); SET user.To = '{consumerEndpointId}';";

    /// <summary>Action re-addressing a control message (continuation, retry) to the endpoint.</summary>
    public static string RedirectAction(string endpointId, string fromId) =>
        $"SET user.To = '{endpointId}'; SET user.From = '{fromId}'";

    // ───────────────────────── Endpoint topic ─────────────────────────

    /// <summary>
    /// An endpoint's own subscription on its own topic — the session-enabled one its
    /// adapter receives from. Carries the endpoint's address rule plus the
    /// continuation and retry rules that re-address control traffic back to it.
    /// </summary>
    public static ExpectedSubscription EndpointSubscription(string endpointId) =>
        new(
            endpointId,
            RequiresSession: true,
            ForwardTo: null,
            Rules: new[]
            {
                new ExpectedRule($"to-{endpointId}", ToFilter(endpointId)),
                new ExpectedRule(
                    "continuation",
                    ToFilter(Constants.ContinuationId),
                    RedirectAction(endpointId, Constants.ContinuationId)),
                new ExpectedRule(
                    "retry",
                    ToFilter(Constants.RetryId),
                    RedirectAction(endpointId, Constants.RetryId)),
            });

    /// <summary>
    /// The request/reply subscription. Replies land on the requesting endpoint's own
    /// topic in a session subscription named <c>{endpoint}-reply</c> and carry a
    /// 5-minute message TTL set by the sender, so orphaned replies self-clean.
    /// </summary>
    public static ExpectedSubscription ReplySubscription(string endpointId)
    {
        var name = endpointId + ReplySubscriptionSuffix;
        return new ExpectedSubscription(
            name,
            RequiresSession: true,
            ForwardTo: null,
            Rules: new[] { new ExpectedRule("ReplyFilter", ToFilter(name)) });
    }

    /// <summary>
    /// The resolver fan-out subscription on an endpoint topic. Auto-forwards every
    /// audited message on to the Resolver topic — a second copy of <em>everything</em>,
    /// so a flood shows up here as well as on the consumer.
    /// </summary>
    public static ExpectedSubscription ResolverFanoutSubscription(string endpointId) =>
        new(
            Constants.ResolverId,
            RequiresSession: false,
            ForwardTo: Constants.ResolverId,
            Rules: new[]
            {
                new ExpectedRule(
                    $"from-{endpointId}",
                    ToFilter(Constants.ResolverId),
                    $"SET user.From = '{endpointId}'"),
                new ExpectedRule($"to-{endpointId}", ToFilter(endpointId)),
            });

    /// <summary>
    /// The Deferred subscription — sessions on, holds messages parked behind a
    /// failure, keyed by the original session id.
    /// </summary>
    public static ExpectedSubscription DeferredSubscription(bool isEmulator = false) =>
        new(
            Constants.DeferredSubscriptionName,
            RequiresSession: true,
            ForwardTo: null,
            Rules: new[]
            {
                new ExpectedRule(
                    "DeferredFilter",
                    $"{ToFilter(Constants.DeferredSubscriptionName)} AND user.OriginalSessionId IS NOT NULL"),
            },
            KeepDefaultRule: false,
            DefaultMessageTimeToLive: isEmulator
                ? DeferredMessageTimeToLiveOnEmulator
                : DeferredMessageTimeToLive,
            // An expired parked message is an event the endpoint still owes: keep it in the
            // dead-letter queue, where the WebApp's deferred recovery finds it. Real namespaces
            // only; the emulator's subscription settings are left as they were.
            DeadLetterOnMessageExpiration: !isEmulator);

    /// <summary>
    /// The DeferredProcessor subscription — sessions off, receives the triggers that
    /// drain parked sessions.
    /// </summary>
    public static ExpectedSubscription DeferredProcessorSubscription() =>
        new(
            Constants.DeferredProcessorId,
            RequiresSession: false,
            ForwardTo: null,
            Rules: new[]
            {
                new ExpectedRule("DeferredProcessorFilter", ToFilter(Constants.DeferredProcessorId)),
            });

    /// <summary>
    /// The forwarder subscription a producing topic carries for one consuming
    /// endpoint: one rule per event type the consumer takes from this producer,
    /// auto-forwarding to the consumer's own topic.
    /// </summary>
    public static ExpectedSubscription ConsumerForwarderSubscription(
        string producerEndpointId,
        string consumerEndpointId,
        IEnumerable<string> eventTypeIds)
    {
        ArgumentNullException.ThrowIfNull(eventTypeIds);

        return ConsumerForwarderSubscription(
            producerEndpointId,
            consumerEndpointId,
            eventTypeIds.Select(eventTypeId => new ForwardedEventType(eventTypeId)));
    }

    /// <summary>
    /// The forwarder subscription a producing topic carries for one consuming endpoint, with
    /// each event type's routing-key shape (Spec 045). Rule names are the event type ids, so a
    /// change of keys updates a rule in place.
    /// </summary>
    public static ExpectedSubscription ConsumerForwarderSubscription(
        string producerEndpointId,
        string consumerEndpointId,
        IEnumerable<ForwardedEventType> eventTypes)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);

        return new ExpectedSubscription(
            consumerEndpointId,
            RequiresSession: false,
            ForwardTo: consumerEndpointId,
            Rules: eventTypes
                .Select(eventType => new ExpectedRule(
                    eventType.EventTypeId,
                    ForwardFilter(eventType.EventTypeId, eventType.Routing),
                    ForwardAction(producerEndpointId, consumerEndpointId))
                {
                    Routing = eventType.Routing,
                })
                .ToList());
    }

    /// <summary>
    /// The forwarder subscription for dynamically-typed events (spec 022 D5), which
    /// the compiled producer/consumer loop cannot derive.
    /// </summary>
    public static ExpectedSubscription DynamicForwardSubscription(
        string sourceEndpointId,
        string targetEndpointId,
        IEnumerable<string> eventTypeIds)
    {
        ArgumentNullException.ThrowIfNull(eventTypeIds);

        return new ExpectedSubscription(
            DynamicForwardPrefix + targetEndpointId,
            RequiresSession: false,
            ForwardTo: targetEndpointId,
            Rules: eventTypeIds
                .Select(eventTypeId => new ExpectedRule(
                    $"dyn-{eventTypeId}",
                    ForwardFilter(eventTypeId),
                    ForwardAction(sourceEndpointId, targetEndpointId)))
                .ToList());
    }

    /// <summary>
    /// Every subscription expected on an endpoint's own topic: the endpoint's own
    /// consumer subscription, its reply subscription, the resolver fan-out, Deferred,
    /// DeferredProcessor, one forwarder per endpoint consuming something this
    /// endpoint produces, and one per dynamic forward out of it.
    /// </summary>
    public static IReadOnlyList<ExpectedSubscription> ForEndpointTopic(
        IEndpoint endpoint,
        IPlatform platform,
        bool isEmulator = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(platform);

        var subscriptions = new List<ExpectedSubscription>
        {
            EndpointSubscription(endpoint.Id),
            ReplySubscription(endpoint.Id),
            ResolverFanoutSubscription(endpoint.Id),
            DeferredSubscription(isEmulator),
            DeferredProcessorSubscription(),
        };

        // Group by consumer so a consumer taking three event types from this producer
        // gets one subscription carrying three rules — matching what the provisioner's
        // repeated EnsureRuleAsync calls produce. An endpoint consuming an event it
        // produces itself is excluded: it would collide with its own terminal
        // subscription above, and Service Bus rejects a subscription forwarding to its
        // own topic anyway.
        // Each rule carries the consumer's routing-key shape for that event type (Spec 045).
        var eventTypesByConsumer = new Dictionary<string, List<ForwardedEventType>>(StringComparer.Ordinal);
        foreach (var eventType in endpoint.EventTypesProduced.OrderBy(eventType => eventType.Id, StringComparer.Ordinal))
        {
            foreach (var consumer in platform
                .GetConsumers(eventType)
                .Where(consumer => !string.Equals(consumer.Id, endpoint.Id, StringComparison.Ordinal))
                .DistinctBy(consumer => consumer.Id)
                .OrderBy(consumer => consumer.Id, StringComparer.Ordinal))
            {
                if (!eventTypesByConsumer.TryGetValue(consumer.Id, out var forwarded))
                {
                    forwarded = new List<ForwardedEventType>();
                    eventTypesByConsumer[consumer.Id] = forwarded;
                }

                if (!forwarded.Exists(entry => string.Equals(entry.EventTypeId, eventType.Id, StringComparison.Ordinal)))
                {
                    forwarded.Add(new ForwardedEventType(eventType.Id, RoutingFor(consumer, eventType, platform)));
                }
            }
        }

        subscriptions.AddRange(eventTypesByConsumer
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ConsumerForwarderSubscription(endpoint.Id, pair.Key, pair.Value)));

        // The source endpoint id is taken from the DynamicForward rather than from
        // endpoint.Id: the provisioner interpolates the declared string into the
        // action, so anything else would drift the moment the two differ in case.
        var dynamicByTarget = new Dictionary<string, (string Source, List<string> EventTypeIds)>(StringComparer.Ordinal);
        foreach (var forward in platform.DynamicForwards
            .Where(forward => string.Equals(forward.SourceEndpoint, endpoint.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(forward => forward.EventTypeId, StringComparer.Ordinal))
        {
            if (!dynamicByTarget.TryGetValue(forward.TargetEndpoint, out var entry))
            {
                entry = (forward.SourceEndpoint, new List<string>());
                dynamicByTarget[forward.TargetEndpoint] = entry;
            }

            if (!entry.EventTypeIds.Contains(forward.EventTypeId, StringComparer.Ordinal))
            {
                entry.EventTypeIds.Add(forward.EventTypeId);
            }
        }

        subscriptions.AddRange(dynamicByTarget
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => DynamicForwardSubscription(pair.Value.Source, pair.Key, pair.Value.EventTypeIds)));

        return subscriptions;
    }

    // ───────────────────────── System topic ─────────────────────────

    /// <summary>
    /// The Resolver topic's own subscription: session-enabled, terminal, and keeping
    /// Service Bus's <c>$Default</c> true-filter — the Resolver consumes everything
    /// forwarded to the topic, so it needs no rule of its own.
    /// </summary>
    public static ExpectedSubscription ResolverSubscription() =>
        new(
            Constants.ResolverId,
            RequiresSession: true,
            ForwardTo: null,
            Rules: Array.Empty<ExpectedRule>(),
            KeepDefaultRule: true);

    /// <summary>Every subscription expected on a system topic.</summary>
    public static IReadOnlyList<ExpectedSubscription> ForSystemTopic(string topicName) =>
        IsSystemTopic(topicName)
            ? new[] { ResolverSubscription() }
            : Array.Empty<ExpectedSubscription>();

    // ───────────────────────── Lookup across a whole topic ─────────────────────────

    /// <summary>
    /// Every subscription the platform expects on <paramref name="topicName"/>,
    /// whichever kind of topic it is. Empty for a topic the platform doesn't know.
    /// </summary>
    public static IReadOnlyList<ExpectedSubscription> ForTopic(
        string? topicName,
        IPlatform? platform,
        bool isEmulator = false)
    {
        if (string.IsNullOrWhiteSpace(topicName)) return Array.Empty<ExpectedSubscription>();
        if (IsSystemTopic(topicName)) return ForSystemTopic(topicName);

        var endpoint = platform?.Endpoints
            .FirstOrDefault(endpoint => string.Equals(endpoint.Id, topicName, StringComparison.OrdinalIgnoreCase));

        return endpoint is null
            ? Array.Empty<ExpectedSubscription>()
            : ForEndpointTopic(endpoint, platform!, isEmulator);
    }

    /// <summary>
    /// Finds the expected shape of one subscription on one topic, or null when the
    /// platform has no recipe for it — the caller must then refuse to offer a
    /// "recreate" that would leave the entity missing.
    /// </summary>
    public static ExpectedSubscription? FindSubscription(
        string? topicName,
        string? subscriptionName,
        IPlatform? platform,
        bool isEmulator = false) =>
        subscriptionName is null
            ? null
            : ForTopic(topicName, platform, isEmulator)
                .FirstOrDefault(subscription =>
                    subscription.Name.Equals(subscriptionName, StringComparison.OrdinalIgnoreCase));
}
