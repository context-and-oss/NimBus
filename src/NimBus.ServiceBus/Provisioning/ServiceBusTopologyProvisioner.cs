using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;

namespace NimBus.ServiceBus.Provisioning;

/// <summary>
/// Provisions the Service Bus topology (topics, session-enabled subscriptions, forwarding
/// subscriptions, and SQL routing rules) for every endpoint declared by an <see cref="IPlatform"/>.
/// Idempotent: existing entities are left untouched when they already match the desired shape;
/// subscriptions whose session flag differs are deleted and recreated, a wrong forward target is
/// corrected in place, and a forwarding subscription paused from the WebApp keeps its backlog.
/// </summary>
/// <remarks>
/// The <c>nb topology apply</c> command wraps this class for the built-in platform configuration.
/// External platforms (e.g. an integrations repository) run it in-process against their own
/// <see cref="IPlatform"/> implementation, exactly like the sample provisioner consoles do.
/// </remarks>
public sealed class ServiceBusTopologyProvisioner
{
    private readonly ServiceBusAdministrationClient _client;
    private readonly Func<IPlatform> _platformFactory;
    private readonly bool _isEmulator;
    private readonly Action<string> _log;

    /// <summary>
    /// Creates a provisioner from a Service Bus connection string. The official Service Bus
    /// emulator is detected via <c>UseDevelopmentEmulator=true</c> in the connection string and
    /// gets entity sizes/TTLs lowered to values the emulator accepts.
    /// </summary>
    /// <param name="connectionString">Service Bus namespace connection string with Manage rights.</param>
    /// <param name="platformFactory">Factory producing the platform whose topology to provision.</param>
    /// <param name="log">Optional progress sink; defaults to <see cref="Console.WriteLine(string)"/>.</param>
    public ServiceBusTopologyProvisioner(string connectionString, Func<IPlatform> platformFactory, Action<string>? log = null)
        : this(
            new ServiceBusAdministrationClient(connectionString ?? throw new ArgumentNullException(nameof(connectionString))),
            platformFactory,
            IsEmulator(connectionString),
            log)
    {
    }

    /// <summary>
    /// Creates a provisioner from a pre-built <see cref="ServiceBusAdministrationClient"/>, e.g.
    /// one constructed with a fully-qualified namespace and a <c>TokenCredential</c> so CI can
    /// provision via OIDC/managed identity instead of a shared access key. Emulator-specific
    /// entity limits are not applied on this path (the emulator only speaks connection strings).
    /// </summary>
    /// <param name="client">Administration client for the target namespace.</param>
    /// <param name="platformFactory">Factory producing the platform whose topology to provision.</param>
    /// <param name="log">Optional progress sink; defaults to <see cref="Console.WriteLine(string)"/>.</param>
    public ServiceBusTopologyProvisioner(ServiceBusAdministrationClient client, Func<IPlatform> platformFactory, Action<string>? log = null)
        : this(client, platformFactory, isEmulator: false, log)
    {
    }

    internal ServiceBusTopologyProvisioner(ServiceBusAdministrationClient client, Func<IPlatform> platformFactory, bool isEmulator, Action<string>? log = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _platformFactory = platformFactory ?? throw new ArgumentNullException(nameof(platformFactory));
        _isEmulator = isEmulator;
        _log = log ?? Console.WriteLine;
    }

    // The official Azure Service Bus emulator advertises itself in the
    // connection string via UseDevelopmentEmulator=true. NimBus's defaults
    // (5 GB topic size, 14-day deferred-subscription TTL) exceed the
    // emulator's hard caps (100 MB topics, conservative TTL upper bound),
    // so when we detect the emulator we drop those down to values the
    // emulator accepts. Production / real-Azure paths are untouched.
    /// <summary>
    /// True when <paramref name="connectionString"/> targets the official Service Bus
    /// emulator, whose entity size and TTL caps are far below a real namespace's. Public
    /// so callers that describe the topology without provisioning it — the WebApp's
    /// subscription admin — can ask for the same emulator-safe values.
    /// </summary>
    public static bool IsEmulator(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return false;
        return connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Applies the platform's topology to the target namespace.</summary>
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var platform = _platformFactory();
        PlatformValidation.EnsureCommandConsumers(platform);
        await ApplyCoreAsync(_client, platform, _isEmulator, _log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyCoreAsync(ServiceBusAdministrationClient client, IPlatform platform, bool isEmulator, Action<string> log, CancellationToken cancellationToken)
    {
        await EnsureTopicAsync(client, Constants.ResolverId, isEmulator, log, cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            await EnsureTopicAsync(client, endpoint.Id, isEmulator, log, cancellationToken).ConfigureAwait(false);
        }

        // What to lay down comes from TopologyDescriptor, not from strings interpolated
        // here: the WebApp's subscription admin rebuilds a deleted subscription from the
        // same descriptor, and that rebuild is only safe while the two cannot drift.
        foreach (var expected in TopologyDescriptor.ForSystemTopic(Constants.ResolverId))
        {
            await EnsureSubscriptionAsync(client, Constants.ResolverId, expected, isEmulator, log, cancellationToken).ConfigureAwait(false);
        }

        var endpointIds = new HashSet<string>(
            platform.Endpoints.Select(endpoint => endpoint.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var endpoint in platform.Endpoints.OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal))
        {
            foreach (var expected in TopologyDescriptor.ForEndpointTopic(endpoint, platform, isEmulator))
            {
                await EnsureSubscriptionAsync(client, endpoint.Id, expected, isEmulator, log, cancellationToken).ConfigureAwait(false);
            }
        }

        // A DynamicForward (spec 022 D5) whose source isn't a declared endpoint has no
        // topic in the loop above. It is malformed — its topic won't exist — but this
        // pass keeps the failure exactly where it was before the descriptor refactor
        // rather than silently dropping the forward.
        foreach (var group in platform.DynamicForwards
            .Where(forward => !endpointIds.Contains(forward.SourceEndpoint))
            .GroupBy(forward => forward.SourceEndpoint, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            foreach (var target in group
                .GroupBy(forward => forward.TargetEndpoint, StringComparer.Ordinal)
                .OrderBy(byTarget => byTarget.Key, StringComparer.Ordinal))
            {
                var expected = TopologyDescriptor.DynamicForwardSubscription(
                    group.Key,
                    target.Key,
                    target.Select(forward => forward.EventTypeId).OrderBy(id => id, StringComparer.Ordinal).Distinct(StringComparer.Ordinal));

                await EnsureSubscriptionAsync(client, group.Key, expected, isEmulator, log, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Creates <paramref name="expected"/> on <paramref name="topicName"/>, or brings an
    /// existing subscription up to it. A subscription whose session flag differs is deleted
    /// and recreated, because that cannot change in place. A wrong or missing forward target
    /// and a too-short message expiry are corrected in place. A paused forwarder (forward target
    /// detached and either not Active or carrying <see cref="SubscriptionMarkers.PurgeInProgress"/>)
    /// is left alone with its messages; only its rules are ensured.
    /// </summary>
    /// <remarks>
    /// Also the rebuild path behind <see cref="ITopologyRebuilder"/>: the WebApp's
    /// subscription admin deletes a subscription to discard a backlog and calls this to put
    /// back something identical to what provisioning would have created.
    /// <paramref name="isEmulator"/> selects how a mismatched rule is reconciled: updated in
    /// place on a real namespace, deleted and recreated on an emulator.
    /// </remarks>
    internal static async Task EnsureSubscriptionAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        ExpectedSubscription expected,
        bool isEmulator,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetSubscriptionAsync(client, topicName, expected.Name, cancellationToken).ConfigureAwait(false);
        var sessionMismatched = existing is not null && existing.RequiresSession != expected.RequiresSession;

        if (existing is null || sessionMismatched)
        {
            if (sessionMismatched)
            {
                await client.DeleteSubscriptionAsync(topicName, expected.Name, cancellationToken).ConfigureAwait(false);
            }

            var options = CreateSubscriptionOptions(topicName, expected);
            if (expected.KeepDefaultRule)
            {
                await client.CreateSubscriptionAsync(options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await client.CreateSubscriptionAsync(options, ClosedDefaultRule(), cancellationToken).ConfigureAwait(false);
            }

            log($"{(sessionMismatched ? "Recreated" : "Created")} {DescribeKind(expected)}subscription '{expected.Name}' on topic '{topicName}'{DescribeForwarding(expected)}.");
        }
        else if (IsPurgingForwarder(existing, expected))
        {
            // A WebApp Purge of a paused forwarder makes it Active for the drain with ForwardTo
            // still detached, and marks it. Re-attaching forwarding now would hand the backlog
            // being discarded to the destination and break the drain.
            log($"Skipped forward subscription '{expected.Name}' on topic '{topicName}': it carries the WebApp purge marker. " +
                "A purge is draining it, or an interrupted purge left the marker behind; Pause in the WebApp clears it " +
                "(then Resume, if forwarding should be re-attached).");
        }
        else if (IsPausedForwarder(existing, expected))
        {
            // The WebApp's Pause detaches ForwardTo and disables receive so messages collect
            // here. Touching it would undo the pause, and recreating it would discard the
            // backlog it was paused to hold. Resume puts the forward target back.
            log($"Skipped paused forward subscription '{expected.Name}' on topic '{topicName}': left as is with its messages until it is resumed.");
        }
        else
        {
            // Forward target, TTL and dead-lettering on expiry all change in place.
            // Recreating would delete whatever the subscription holds — for Deferred, every
            // parked message; for a forwarder, anything not yet forwarded.
            var updates = new List<string>();

            if (!ForwardToMatches(existing.ForwardTo, expected.ForwardTo))
            {
                existing.ForwardTo = expected.ForwardTo ?? string.Empty;
                updates.Add($"forward target{DescribeForwarding(expected)}");
            }

            if (NeedsExpiryUpdate(existing, expected))
            {
                existing.DefaultMessageTimeToLive = expected.DefaultMessageTimeToLive ?? existing.DefaultMessageTimeToLive;
                existing.DeadLetteringOnMessageExpiration |= expected.DeadLetterOnMessageExpiration;
                updates.Add("message expiry");
            }

            if (updates.Count > 0)
            {
                await client.UpdateSubscriptionAsync(existing, cancellationToken).ConfigureAwait(false);
                log($"Updated {string.Join(" and ", updates)} on subscription '{expected.Name}' on topic '{topicName}'.");
            }
        }

        // A subscription created here carries the closed $Default placeholder, which this
        // removes; the subscription then matches nothing until its real rules are in place.
        // A subscription that predates that still has the true-filter $Default Service Bus
        // seeds a rule-less create with, which hands it everything published to the topic
        // and is only ever right for the Resolver's own subscription.
        if (!expected.KeepDefaultRule)
        {
            await DeleteRuleIfExistsAsync(client, topicName, expected.Name, TopologyDescriptor.DefaultRuleName, cancellationToken).ConfigureAwait(false);
        }

        foreach (var rule in expected.Rules)
        {
            await EnsureRuleAsync(client, topicName, expected.Name, rule.Name, rule.Filter, rule.Action, isEmulator, log, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when an existing subscription expires messages sooner than expected, or drops
    /// them on expiry where they should be dead-lettered. Never shortens a longer TTL or
    /// turns dead-lettering off that an operator turned on.
    /// </summary>
    private static bool NeedsExpiryUpdate(SubscriptionProperties existing, ExpectedSubscription expected) =>
        (expected.DefaultMessageTimeToLive is { } timeToLive && existing.DefaultMessageTimeToLive < timeToLive)
        || (expected.DeadLetterOnMessageExpiration && !existing.DeadLetteringOnMessageExpiration);

    /// <summary>
    /// True when <paramref name="existing"/> is a forwarder the WebApp has paused: it should
    /// forward, but its forward target is detached and it is not Active.
    /// </summary>
    private static bool IsPausedForwarder(SubscriptionProperties existing, ExpectedSubscription expected) =>
        !string.IsNullOrEmpty(expected.ForwardTo)
        && string.IsNullOrEmpty(existing.ForwardTo)
        && existing.Status != EntityStatus.Active;

    /// <summary>
    /// True when <paramref name="existing"/> is a forwarder with its forward target detached
    /// that carries <see cref="SubscriptionMarkers.PurgeInProgress"/>: a WebApp purge is
    /// draining it, or an interrupted purge left the marker behind.
    /// </summary>
    private static bool IsPurgingForwarder(SubscriptionProperties existing, ExpectedSubscription expected) =>
        !string.IsNullOrEmpty(expected.ForwardTo)
        && string.IsNullOrEmpty(existing.ForwardTo)
        && string.Equals(existing.UserMetadata, SubscriptionMarkers.PurgeInProgress, StringComparison.Ordinal);

    private static string DescribeKind(ExpectedSubscription expected) =>
        expected.ForwardTo is not null ? "forward " : expected.RequiresSession ? "session " : string.Empty;

    private static string DescribeForwarding(ExpectedSubscription expected) =>
        expected.ForwardTo is null ? string.Empty : $" to '{expected.ForwardTo}'";

    private static async Task EnsureTopicAsync(ServiceBusAdministrationClient client, string topicName, bool isEmulator, Action<string> log, CancellationToken cancellationToken)
    {
        if (await client.TopicExistsAsync(topicName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var options = new CreateTopicOptions(topicName)
        {
            SupportOrdering = true,
            DuplicateDetectionHistoryTimeWindow = TimeSpan.FromMinutes(10),
            EnableBatchedOperations = true,
        };

        // Real Azure namespaces accept up to 5 GB per topic; the emulator caps
        // at 100 MB and rejects anything larger. Setting the cap explicitly on
        // production matches the historical default; omitting it on the emulator
        // lets the server pick its own (100 MB) ceiling without a 400 response.
        if (!isEmulator)
        {
            options.MaxSizeInMegabytes = 5120;
        }

        await client.CreateTopicAsync(options, cancellationToken).ConfigureAwait(false);
        log($"Created topic '{topicName}'.");
    }

    /// <summary>
    /// Compares an existing subscription's ForwardTo (which Azure stores as a normalised
    /// entity path — usually lowercased, sometimes the bare name and sometimes a full URL)
    /// against the desired entity name we passed at creation time.
    ///
    /// The previous implementation used <see cref="string.Equals(string?, string?, StringComparison)"/>
    /// with <see cref="StringComparison.Ordinal"/>, which rejected matches whenever Azure
    /// normalised the value (e.g. lowercasing "ErpEndpoint" to "erpendpoint", or expanding
    /// to a full sb://... URL). That caused the surrounding code to delete and recreate the
    /// subscription on every call, wiping out previously-added forwarding rules.
    /// </summary>
    private static bool ForwardToMatches(string? existingForwardTo, string? desiredForwardTo)
    {
        var existingEmpty = string.IsNullOrEmpty(existingForwardTo);
        var desiredEmpty = string.IsNullOrEmpty(desiredForwardTo);
        if (existingEmpty && desiredEmpty) return true;
        if (existingEmpty || desiredEmpty) return false;

        // Compare trailing entity names case-insensitively. Handles all observed forms:
        //   "ErpEndpoint", "erpendpoint", "sb://ns.servicebus.windows.net/erpendpoint".
        var existingTail = TrailingSegment(existingForwardTo!);
        var desiredTail = TrailingSegment(desiredForwardTo!);
        return string.Equals(existingTail, desiredTail, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrailingSegment(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path.Substring(lastSlash + 1);
    }

    /// <summary>
    /// Creates a missing rule, leaves a matching one untouched, and reconciles a rule whose
    /// filter or action differs.
    /// </summary>
    /// <remarks>
    /// On a real namespace a mismatched rule is replaced in place with one
    /// <c>UpdateRuleAsync</c> call, so there is no window between a delete and a create in
    /// which publishes go unrouted. Creating the new rule beside the old one is not an
    /// option: every matching rule with an action produces its own copy, so an overlap
    /// would duplicate forwards with fresh EventIds. The emulators keep delete-then-create:
    /// the in-repo emulator's admin PUT rejects an existing rule, and the official
    /// emulator's support for rule updates is not guaranteed.
    /// </remarks>
    private static async Task EnsureRuleAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        string filter,
        string? action,
        bool isEmulator,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetRuleAsync(client, topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        if (existing is not null && RuleMatches(existing, filter, action))
        {
            return;
        }

        if (existing is not null && !isEmulator)
        {
            existing.Filter = new SqlRuleFilter(filter);
            existing.Action = string.IsNullOrWhiteSpace(action) ? null : new SqlRuleAction(action);
            await client.UpdateRuleAsync(topicName, subscriptionName, existing, cancellationToken).ConfigureAwait(false);
            log($"Updated rule '{ruleName}' on '{topicName}/{subscriptionName}'.");
            return;
        }

        if (existing is not null)
        {
            await DeleteRuleToleratingMissingAsync(client, topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        }

        var createRule = new CreateRuleOptions
        {
            Name = ruleName,
            Filter = new SqlRuleFilter(filter),
        };

        if (!string.IsNullOrWhiteSpace(action))
        {
            createRule.Action = new SqlRuleAction(action);
        }

        await client.CreateRuleAsync(topicName, subscriptionName, createRule, cancellationToken).ConfigureAwait(false);
        log($"Ensured rule '{ruleName}' on '{topicName}/{subscriptionName}'.");
    }

    private static bool RuleMatches(RuleProperties rule, string filter, string? action)
    {
        var existingFilter = (rule.Filter as SqlRuleFilter)?.SqlExpression ?? rule.Filter?.ToString() ?? string.Empty;
        var existingAction = (rule.Action as SqlRuleAction)?.SqlExpression ?? string.Empty;
        return string.Equals(existingFilter, filter, StringComparison.Ordinal) &&
               string.Equals(existingAction, action ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule a subscription is created with when it must not keep $Default: a $Default
    /// that matches nothing (<c>1=0</c>).
    /// </summary>
    /// <remarks>
    /// Created without a rule, Service Bus seeds a true-filter $Default, and until the
    /// provisioner deletes it the subscription receives everything published to its topic —
    /// on a live topic, messages addressed elsewhere, and for a forwarder, unrelated traffic
    /// auto-forwarded without the forward rule's action. Creating it closed keeps it from
    /// ever existing with a true filter; the $Default delete and the rule loop that follow
    /// then run exactly as for any other subscription.
    /// </remarks>
    private static CreateRuleOptions ClosedDefaultRule() =>
        new(TopologyDescriptor.DefaultRuleName, new FalseRuleFilter());

    private static CreateSubscriptionOptions CreateSubscriptionOptions(string topicName, ExpectedSubscription expected)
    {
        var options = new CreateSubscriptionOptions(topicName, expected.Name)
        {
            MaxDeliveryCount = Constants.ServiceBusMaxDeliveryCount,
            LockDuration = TimeSpan.FromSeconds(30),
            EnableBatchedOperations = true,
            EnableDeadLetteringOnFilterEvaluationExceptions = true,
            RequiresSession = expected.RequiresSession,
            DeadLetteringOnMessageExpiration = expected.DeadLetterOnMessageExpiration,
        };

        if (!string.IsNullOrWhiteSpace(expected.ForwardTo))
        {
            options.ForwardTo = expected.ForwardTo;
        }

        if (expected.DefaultMessageTimeToLive is { } timeToLive)
        {
            options.DefaultMessageTimeToLive = timeToLive;
        }

        return options;
    }

    private static async Task<SubscriptionProperties?> TryGetSubscriptionAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetSubscriptionAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (Exception exception) when (IsEntityNotFound(exception))
        {
            return null;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException exception) when (exception.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return null;
        }
    }

    private static async Task<RuleProperties?> TryGetRuleAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.GetRuleAsync(topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException exception) when (exception.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return null;
        }
    }

    private static async Task DeleteRuleIfExistsAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken)
    {
        var existing = await TryGetRuleAsync(client, topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await DeleteRuleToleratingMissingAsync(client, topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes a rule, treating "already gone" as success.
    /// </summary>
    /// <remarks>
    /// Reading a rule and then deleting it is check-then-act against a remote broker:
    /// the rule can be seen by the read and be absent by the time the delete lands.
    /// The postcondition is the same either way — the rule is not there — so a 404
    /// is the outcome this method wanted, not a failure. Without this, provisioning
    /// aborts partway through on a race it should simply absorb, which showed up as
    /// an intermittent MessagingEntityNotFound for '$Default' on the Deferred
    /// subscription, roughly one run in ten against the emulator.
    /// </remarks>
    private static async Task DeleteRuleToleratingMissingAsync(
        ServiceBusAdministrationClient client,
        string topicName,
        string subscriptionName,
        string ruleName,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.DeleteRuleAsync(topicName, subscriptionName, ruleName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsEntityNotFound(exception))
        {
        }
    }

    /// <summary>
    /// True when a failure means the entity is not there.
    /// </summary>
    /// <remarks>
    /// The administration client reports this two ways: a bare
    /// <see cref="Azure.RequestFailedException"/> with status 404, and a
    /// <see cref="ServiceBusException"/> whose Reason is MessagingEntityNotFound and
    /// which carries the RequestFailedException as its inner exception. Catching only
    /// the former silently misses the delete path, which throws the latter.
    /// </remarks>
    private static bool IsEntityNotFound(Exception exception) => exception switch
    {
        ServiceBusException serviceBusException =>
            serviceBusException.Reason == ServiceBusFailureReason.MessagingEntityNotFound,
        Azure.RequestFailedException requestFailed => requestFailed.Status == 404,
        _ => false,
    };
}
