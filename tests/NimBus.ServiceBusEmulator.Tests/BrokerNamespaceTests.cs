#pragma warning disable CA1707, CA2007

using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK;
using NimBus.ServiceBus.Provisioning;
using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Protocol;
using NimBus.Testing;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class BrokerNamespaceTests
{
    private static readonly string[] ScheduledMessageIds = ["first", "second", "third"];
    private static readonly long[] ScheduledSequenceNumbers = [1, 2, 3];

    [TestMethod]
    public void Emulator_default_delivery_limit_matches_NimBus_provisioning_default()
    {
        Assert.AreEqual(
            NimBus.Core.Messages.Constants.ServiceBusMaxDeliveryCount,
            BrokerDefaults.MaxDeliveryCount);
    }

    [TestMethod]
    public void CreateSubscription_with_an_initial_rule_seeds_exactly_that_rule()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        var initial = new RuleDefinition("$Default", "1=0");

        broker.CreateSubscription("events", new SubscriptionDefinition("closed"), initial);
        broker.Publish("events", new BrokerMessage { MessageId = "window" });

        CollectionAssert.AreEqual(new[] { initial }, broker.GetRules("events", "closed").ToArray());
        Assert.AreEqual(0, broker.GetSubscriptionRuntimeProperties("events", "closed").TotalMessageCount);
    }

    [TestMethod]
    public void CreateSubscription_without_an_initial_rule_seeds_a_true_Default()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));

        broker.CreateSubscription("events", new SubscriptionDefinition("open"));

        CollectionAssert.AreEqual(new[] { new RuleDefinition("$Default", "1=1") }, broker.GetRules("events", "open").ToArray());
    }

    [TestMethod]
    public void PrepareCreateSubscription_snapshot_and_result_carry_the_initial_rule()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        var initial = new RuleDefinition("$Default", "1=0");

        var prepared = broker.PrepareCreateSubscription("events", new SubscriptionDefinition("closed"), initial);

        var snapshotted = prepared.Snapshot.Topics.Single().Subscriptions.Single(subscription => subscription.Definition.Name == "closed");
        CollectionAssert.AreEqual(new[] { initial }, snapshotted.Rules.ToArray());

        prepared.Apply();
        CollectionAssert.AreEqual(new[] { initial }, broker.GetRules("events", "closed").ToArray());
    }

    [TestMethod]
    public void PrepareCreateSubscription_without_an_initial_rule_snapshots_a_true_Default()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));

        var prepared = broker.PrepareCreateSubscription("events", new SubscriptionDefinition("open"));

        var snapshotted = prepared.Snapshot.Topics.Single().Subscriptions.Single(subscription => subscription.Definition.Name == "open");
        CollectionAssert.AreEqual(new[] { new RuleDefinition("$Default", "1=1") }, snapshotted.Rules.ToArray());
    }

    [TestMethod]
    public void Publish_rejects_before_exceeding_the_broker_memory_budget()
    {
        var broker = new BrokerNamespace(new BrokerOptions { MaxStoredBytes = 3 });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));

        Assert.ThrowsExactly<BrokerQuotaExceededException>(() =>
            broker.Publish("events", new BrokerMessage { Body = new byte[4] }));
        Assert.AreEqual(0, broker.GetSubscriptionRuntimeProperties("events", "consumer").TotalMessageCount);
    }

    [TestMethod]
    public void Publish_applies_rules_per_subscription_and_preserves_guid_type()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));
        broker.ReplaceRule(
            "events",
            "consumer",
            new RuleDefinition(
                "route",
                "user.EventTypeId = 'order-created' AND user.MessageType = 'EventRequest' AND user.From IS NULL",
                "SET user.From = 'orders'; SET user.EventId = newid(); SET user.To = 'consumer';"));

        var message = new BrokerMessage
        {
            MessageId = "message-1",
            Body = System.Text.Encoding.UTF8.GetBytes("payload"),
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["EventTypeId"] = "order-created",
                ["MessageType"] = "EventRequest",
            },
        };

        broker.Publish("events", message);

        var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(delivery);
        Assert.AreEqual("orders", delivery.Message.ApplicationProperties["From"]);
        Assert.AreEqual("consumer", delivery.Message.ApplicationProperties["To"]);
        Assert.IsInstanceOfType<Guid>(delivery.Message.ApplicationProperties["EventId"]);
    }

    [TestMethod]
    public void Max_delivery_count_moves_message_to_dead_letter_queue_without_extra_delivery()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { MaxDeliveryCount = 3 });
        broker.Publish("events", NewMessage("message-1"));

        for (var expected = 1; expected <= 3; expected++)
        {
            var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
            Assert.IsNotNull(delivery);
            Assert.AreEqual(expected, delivery.Message.DeliveryCount);
            broker.Abandon("events", "consumer", delivery.LockToken, "receiver-1");
        }

        Assert.IsNull(broker.TryAcquire("events", "consumer", null, "receiver-1"));
        var properties = broker.GetSubscriptionRuntimeProperties("events", "consumer");
        Assert.AreEqual(0L, properties.ActiveMessageCount);
        Assert.AreEqual(1L, properties.DeadLetterMessageCount);
    }

    [TestMethod]
    public void Dead_letter_peek_paginates_and_preserves_missing_reason()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));
        broker.Publish("events", NewMessage("message-1"));
        broker.Publish("events", NewMessage("message-2"));

        var first = broker.TryAcquire("events", "consumer", null, "receiver-1")!;
        broker.DeadLetter("events", "consumer", first.LockToken, "receiver-1", null, null);
        var second = broker.TryAcquire("events", "consumer", null, "receiver-1")!;
        broker.DeadLetter("events", "consumer", second.LockToken, "receiver-1", "CosmosDbThrottled", null);

        var firstPage = broker.PeekDeadLetter("events", "consumer", 0, 1);
        var secondPage = broker.PeekDeadLetter("events", "consumer", firstPage[0].SequenceNumber + 1, 1);

        Assert.AreEqual(1, firstPage.Count);
        Assert.IsFalse(firstPage[0].ApplicationProperties.ContainsKey("DeadLetterReason"));
        Assert.AreEqual(1, secondPage.Count);
        Assert.AreEqual("CosmosDbThrottled", secondPage[0].ApplicationProperties["DeadLetterReason"]);
    }

    [TestMethod]
    public void Dead_letter_lock_can_be_renewed()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { LockDuration = TimeSpan.FromSeconds(30) });
        broker.Publish("events", NewMessage("message-1"));
        var source = broker.TryAcquire("events", "consumer", null, "receiver-1")!;
        broker.DeadLetter("events", "consumer", source.LockToken, "receiver-1", null, null);
        var deadLetter = broker.TryAcquireDeadLetter("events", "consumer", "dlq-receiver")!;

        clock.Advance(TimeSpan.FromSeconds(20));
        broker.RenewDeadLetterLock("events", "consumer", deadLetter.LockToken);
        clock.Advance(TimeSpan.FromSeconds(15));

        broker.CompleteDeadLetter("events", "consumer", deadLetter.LockToken, "dlq-receiver");
        Assert.AreEqual(0L, broker.GetSubscriptionRuntimeProperties("events", "consumer").DeadLetterMessageCount);
    }

    [TestMethod]
    public void Dead_letter_replay_commit_is_atomic_when_destination_is_invalid()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));
        broker.Publish("events", NewMessage("message-1"));
        var source = broker.TryAcquire("events", "consumer", null, "receiver-1")!;
        broker.DeadLetter("events", "consumer", source.LockToken, "receiver-1", null, null);
        var deadLetter = broker.TryAcquireDeadLetter("events", "consumer", "dlq-receiver")!;

        Assert.ThrowsExactly<KeyNotFoundException>(() => broker.CommitDeadLetterReplay(
            "events",
            "consumer",
            deadLetter.LockToken,
            "dlq-receiver",
            "missing-topic",
            NewMessage("replacement")));

        Assert.AreEqual(1L, broker.GetSubscriptionRuntimeProperties("events", "consumer").DeadLetterMessageCount);
        broker.ReleaseDeadLetter("events", "consumer", deadLetter.LockToken, "dlq-receiver");
        Assert.IsNotNull(broker.TryAcquireDeadLetter("events", "consumer", "verification"));
    }

    [TestMethod]
    public void Session_is_exclusive_and_clean_release_does_not_increment_delivery_count()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { RequiresSession = true });
        broker.Publish("events", NewMessage("message-1", "session-1"));

        var session = broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1");
        Assert.IsNotNull(session);
        Assert.ThrowsExactly<SessionCannotBeLockedException>(() =>
            broker.TryAcceptSession("events", "consumer", "session-1", "receiver-2"));

        var first = broker.TryAcquire("events", "consumer", "session-1", "receiver-1");
        Assert.IsNotNull(first);
        Assert.AreEqual(1, first.Message.DeliveryCount);

        broker.ReleaseSession("events", "consumer", "session-1", "receiver-1");
        var reacquired = broker.TryAcceptSession("events", "consumer", "session-1", "receiver-2");
        Assert.IsNotNull(reacquired);
        var second = broker.TryAcquire("events", "consumer", "session-1", "receiver-2");
        Assert.IsNotNull(second);
        Assert.AreEqual(1, second.Message.DeliveryCount);
    }

    [TestMethod]
    public void Explicit_empty_session_can_be_locked()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { RequiresSession = true });

        var session = broker.TryAcceptSession("events", "consumer", "empty-session", "receiver-1");

        Assert.IsNotNull(session);
        Assert.AreEqual("empty-session", session.SessionId);
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("abandon")]
    [DataRow("dead-letter")]
    [DataRow("release")]
    [DataRow("complete-by-token")]
    [DataRow("abandon-by-token")]
    [DataRow("dead-letter-by-token")]
    public void Expired_message_lock_cannot_be_settled(string operation)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { LockDuration = TimeSpan.FromSeconds(1) });
        broker.Publish("events", NewMessage("message-1"));

        var expired = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(expired);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<KeyNotFoundException>(() => SettleExpired(operation, broker, expired.LockToken));
        var redelivered = broker.TryAcquire("events", "consumer", null, "receiver-2");
        Assert.IsNotNull(redelivered);
        Assert.AreEqual(2, redelivered.Message.DeliveryCount);
    }

    [TestMethod]
    public void Session_management_requires_the_current_lock_owner()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { RequiresSession = true });
        broker.Publish("events", NewMessage("message-1", "session-1"));
        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1"));

        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.RenewSessionLock("events", "consumer", "session-1", "receiver-2"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.GetSessionState("events", "consumer", "session-1", "receiver-2"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.SetSessionState("events", "consumer", "session-1", "receiver-2", new byte[] { 1 }));

        broker.SetSessionState("events", "consumer", "session-1", "receiver-1", new byte[] { 1 });
        Assert.AreEqual(1, broker.GetSessionState("events", "consumer", "session-1", "receiver-1")?.Length);
        Assert.IsGreaterThan(
            DateTimeOffset.UtcNow,
            broker.RenewSessionLock("events", "consumer", "session-1", "receiver-1"));
    }

    [TestMethod]
    public void Expired_session_lock_cannot_be_completed_or_managed()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer")
            {
                RequiresSession = true,
                LockDuration = TimeSpan.FromSeconds(1),
            });
        broker.Publish("events", NewMessage("message-1", "session-1"));
        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1"));
        var expired = broker.TryAcquire("events", "consumer", "session-1", "receiver-1");
        Assert.IsNotNull(expired);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.Complete("events", "consumer", expired.LockToken, "receiver-1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.RenewSessionLock("events", "consumer", "session-1", "receiver-1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.GetSessionState("events", "consumer", "session-1", "receiver-1"));
        Assert.ThrowsExactly<KeyNotFoundException>(() =>
            broker.SetSessionState("events", "consumer", "session-1", "receiver-1", new byte[] { 1 }));

        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-2"));
        var redelivered = broker.TryAcquire("events", "consumer", "session-1", "receiver-2");
        Assert.IsNotNull(redelivered);
        Assert.AreEqual(2, redelivered.Message.DeliveryCount);
    }

    [TestMethod]
    public void Scheduled_message_starts_ttl_at_activation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));

        var message = NewMessage("message-1") with
        {
            TimeToLive = TimeSpan.FromMinutes(1),
            ScheduledEnqueueTime = clock.GetUtcNow().AddHours(1),
        };
        broker.Publish("events", message);

        clock.Advance(TimeSpan.FromMinutes(59));
        broker.ProcessDueWork();
        Assert.IsNull(broker.TryAcquire("events", "consumer", null, "receiver-1"));

        clock.Advance(TimeSpan.FromMinutes(1));
        broker.ProcessDueWork();
        var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(delivery);
        Assert.AreEqual(clock.GetUtcNow(), delivery.Message.EnqueuedTime);
    }

    [TestMethod]
    public void Scheduled_messages_due_together_preserve_publish_order()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var broker = new BrokerNamespace(new BrokerOptions { TimeProvider = clock });
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription(
            "events",
            new SubscriptionDefinition("consumer") { RequiresSession = true });
        var due = clock.GetUtcNow().AddMinutes(1);

        foreach (var messageId in ScheduledMessageIds)
        {
            broker.Publish("events", NewMessage(messageId, "session-1") with { ScheduledEnqueueTime = due });
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        broker.ProcessDueWork();
        Assert.IsNotNull(broker.TryAcceptSession("events", "consumer", "session-1", "receiver-1"));

        var deliveries = Enumerable.Range(0, 3)
            .Select(_ => broker.TryAcquire("events", "consumer", "session-1", "receiver-1"))
            .ToArray();
        Assert.IsTrue(deliveries.All(delivery => delivery is not null));
        CollectionAssert.AreEqual(
            ScheduledMessageIds,
            deliveries.Select(delivery => delivery!.Message.MessageId).ToArray());
        CollectionAssert.AreEqual(
            ScheduledSequenceNumbers,
            deliveries.Select(delivery => delivery!.Message.SequenceNumber).ToArray());
    }

    [TestMethod]
    public void Receive_pump_restarts_when_flow_arrives_while_it_is_finishing()
    {
        var gate = new ReceivePumpGate();

        Assert.IsTrue(gate.TryStart(hasCredit: true));
        Assert.IsFalse(gate.TryStart(hasCredit: true));

        Assert.IsTrue(gate.Complete(linkOpen: true, static () => true));
    }

    [TestMethod]
    public void Receive_pump_restart_intent_wins_over_stale_credit_sample()
    {
        var gate = new ReceivePumpGate();

        Assert.IsTrue(gate.TryStart(hasCredit: true));
        const bool creditSampledBeforeFlow = false;
        Assert.IsFalse(gate.TryStart(hasCredit: true));

        Assert.IsTrue(gate.Complete(linkOpen: true, () => creditSampledBeforeFlow));
    }

    [TestMethod]
    public void Discarded_topology_candidate_does_not_rewind_acknowledged_data_plane_changes()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer"));
        broker.Publish("events", NewMessage("completed-during-save"));
        var delivery = broker.TryAcquire("events", "consumer", null, "receiver-1");
        Assert.IsNotNull(delivery);
        var mutation = broker.PrepareCreateTopic(new TopicDefinition("candidate"));

        broker.Complete("events", "consumer", delivery.LockToken, "receiver-1");
        broker.Publish("events", NewMessage("published-during-save"));

        Assert.AreEqual(1, broker.GetSubscriptionRuntimeProperties("events", "consumer").TotalMessageCount);
        Assert.IsFalse(broker.TopicExists("candidate"));
        Assert.AreEqual(2, mutation.Snapshot.Topics.Count);
    }

    [TestMethod]
    public async Task ForwarderRule_MatchesOnlyOriginalPublishes()
    {
        // CRM and ERP both produce and consume ContactCreated, so the CrmEndpoint topic
        // carries an ErpEndpoint forwarder. CRM's own control traffic about a ContactCreated
        // it handled carries that EventTypeId and no From; the forwarder must not take it.
        var platform = new ForwarderPlatform(
            [],
            new ForwarderEndpoint("CrmEndpoint", produces: [ContactCreatedType], consumes: [ContactCreatedType]),
            new ForwarderEndpoint("ErpEndpoint", produces: [ContactCreatedType], consumes: [ContactCreatedType]));
        var crm = platform.Endpoints.Single(endpoint => endpoint.Id == "CrmEndpoint");
        var expected = TopologyDescriptor.ForEndpointTopic(crm, platform);

        var forwarder = expected.Single(subscription => subscription.Name == "ErpEndpoint");
        Assert.AreEqual("ErpEndpoint", forwarder.ForwardTo);
        Assert.AreEqual(TopologyDescriptor.ForwardFilter(nameof(ContactCreated)), forwarder.Rules.Single().Filter);

        await AssertForwarderTakesOnlyOriginalPublishes(expected, "CrmEndpoint", forwarder.Name, "ErpEndpoint");
    }

    [TestMethod]
    public async Task DynamicForwarderRule_MatchesOnlyOriginalPublishes()
    {
        // The same guards apply to a dynamically-typed forward (spec 022 D5).
        var platform = new ForwarderPlatform(
            [new NimBus.Core.Endpoints.DynamicForward("CrmEndpoint", nameof(ContactCreated), "ErpEndpoint")],
            new ForwarderEndpoint("CrmEndpoint", produces: [], consumes: []),
            new ForwarderEndpoint("ErpEndpoint", produces: [], consumes: []));
        var crm = platform.Endpoints.Single(endpoint => endpoint.Id == "CrmEndpoint");
        var expected = TopologyDescriptor.ForEndpointTopic(crm, platform);

        var forwarder = expected.Single(subscription => subscription.Name == TopologyDescriptor.DynamicForwardPrefix + "ErpEndpoint");
        Assert.AreEqual("ErpEndpoint", forwarder.ForwardTo);
        var rule = forwarder.Rules.Single();
        Assert.AreEqual("dyn-" + nameof(ContactCreated), rule.Name);
        Assert.AreEqual(TopologyDescriptor.ForwardFilter(nameof(ContactCreated)), rule.Filter);

        await AssertForwarderTakesOnlyOriginalPublishes(expected, "CrmEndpoint", forwarder.Name, "ErpEndpoint");
    }

    [TestMethod]
    public void ForwardFilter_GuardsOnMessageTypeAndFrom()
    {
        Assert.AreEqual(
            "user.EventTypeId = 'ContactCreated' AND user.MessageType = 'EventRequest' AND user.From IS NULL",
            TopologyDescriptor.ForwardFilter("ContactCreated"));
    }

    private static async Task AssertForwarderTakesOnlyOriginalPublishes(
        IReadOnlyList<ExpectedSubscription> expected,
        string topicName,
        string forwarderName,
        string consumerId)
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition(topicName));
        foreach (var subscription in expected)
        {
            // ForwardTo is left off so whatever the forwarder takes stays countable here.
            broker.CreateSubscription(topicName, new SubscriptionDefinition(subscription.Name) { RequiresSession = subscription.RequiresSession });
            if (!subscription.KeepDefaultRule)
            {
                broker.DeleteRule(topicName, subscription.Name, TopologyDescriptor.DefaultRuleName);
            }

            foreach (var rule in subscription.Rules)
            {
                broker.CreateRule(topicName, subscription.Name, new RuleDefinition(rule.Name, rule.Filter, rule.Action));
            }
        }

        // An inbound ContactCreated that ERP published and the ErpEndpoint topic forwarded to CRM.
        var inbound = new InMemoryMessageContext(
            new NimBus.Core.Messages.Message
            {
                To = topicName,
                From = consumerId,
                MessageId = "inbound-1",
                EventId = "event-1",
                SessionId = "session-1",
                CorrelationId = "correlation-1",
                OriginatingMessageId = NimBus.Core.Messages.Constants.Self,
                EventTypeId = nameof(ContactCreated),
                MessageType = MessageType.EventRequest,
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent { EventTypeId = nameof(ContactCreated), EventJson = "{}" },
                },
            },
            new InMemorySessionState());

        var controlSender = new CapturingSender();
        var responses = new ResponseService(controlSender);
        await responses.SendResolutionResponse(inbound);
        await responses.SendErrorResponse(inbound, new InvalidOperationException("boom"));
        await responses.SendDeadLetterResponse(inbound, "HandlerFailed", new InvalidOperationException("boom"));
        await responses.SendDeferralResponse(inbound, new SessionBlockedException("blocked", "event-0"));
        await responses.SendRetryResponse(inbound, messageDelayMinutes: 0);
        await responses.SendProcessDeferredRequest(inbound);
        await responses.SendUnsupportedResponse(inbound);
        await responses.SendSkipResponse(inbound);
        // A parked copy: To = Deferred with a From marker.
        await responses.SendToDeferredSubscription(inbound, deferralSequence: 1);
        // A CLI resubmission, shaped like Container.Resubmit: no From.
        await controlSender.Send(new NimBus.Core.Messages.Message
        {
            To = topicName,
            MessageId = "resubmit-1",
            EventId = "event-1",
            SessionId = "session-1",
            CorrelationId = "correlation-1",
            OriginatingMessageId = "inbound-1",
            ParentMessageId = "inbound-1",
            MessageType = MessageType.ResubmissionRequest,
            EventTypeId = nameof(ContactCreated),
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = nameof(ContactCreated), EventJson = "{}" },
            },
        });

        CollectionAssert.AreEquivalent(
            new[]
            {
                MessageType.ResolutionResponse, MessageType.ErrorResponse, MessageType.ErrorResponse,
                MessageType.DeferralResponse, MessageType.RetryRequest, MessageType.ProcessDeferredRequest,
                MessageType.UnsupportedResponse, MessageType.SkipResponse, MessageType.EventRequest,
                MessageType.ResubmissionRequest,
            },
            controlSender.Sent.Select(message => message.MessageType).ToArray());

        foreach (var control in controlSender.Sent)
        {
            Assert.AreEqual(nameof(ContactCreated), control.EventTypeId);
            broker.Publish(topicName, ToBrokerMessage(control));
        }

        Assert.AreEqual(
            0L,
            broker.GetSubscriptionRuntimeProperties(topicName, forwarderName).TotalMessageCount,
            "The forwarder must not take the endpoint's own control traffic or a parked copy.");
        // The control traffic did land where it belongs, so the zero above is not a broken publish.
        Assert.IsGreaterThan(0L, broker.GetSubscriptionRuntimeProperties(topicName, NimBus.Core.Messages.Constants.ResolverId).TotalMessageCount);
        Assert.AreEqual(1L, broker.GetSubscriptionRuntimeProperties(topicName, NimBus.Core.Messages.Constants.DeferredSubscriptionName).TotalMessageCount);

        // An original publish, built exactly as PublisherClient builds it.
        var publishSender = new CapturingSender();
        await new PublisherClient(publishSender, topicName).Publish(new ContactCreated { Name = "Ada" });
        broker.Publish(topicName, ToBrokerMessage(publishSender.Sent.Single()));

        Assert.AreEqual(1L, broker.GetSubscriptionRuntimeProperties(topicName, forwarderName).TotalMessageCount);
        var forwarded = broker.TryAcquire(topicName, forwarderName, null, "receiver-1");
        Assert.IsNotNull(forwarded);
        Assert.AreEqual(consumerId, forwarded.Message.ApplicationProperties["To"]);
        Assert.AreEqual(topicName, forwarded.Message.ApplicationProperties["From"]);
    }

    private static readonly NimBus.Core.Events.EventType ContactCreatedType = new(typeof(ContactCreated));

    private static BrokerMessage ToBrokerMessage(IMessage message)
    {
        var wire = NimBus.ServiceBus.MessageHelper.ToServiceBusMessage(message);
        return new BrokerMessage
        {
            MessageId = wire.MessageId,
            SessionId = wire.SessionId,
            CorrelationId = wire.CorrelationId,
            Body = wire.Body.ToMemory(),
            ApplicationProperties = wire.ApplicationProperties.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
        };
    }

    private sealed class ContactCreated : NimBus.Core.Events.Event
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CapturingSender : ISender
    {
        public List<IMessage> Sent { get; } = [];

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult((long)Sent.Count);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ForwarderPlatform : NimBus.Core.Platform
    {
        public ForwarderPlatform(IReadOnlyList<NimBus.Core.Endpoints.DynamicForward> dynamicForwards, params NimBus.Core.Endpoints.IEndpoint[] endpoints)
        {
            DynamicForwards = dynamicForwards;
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }

        public override IReadOnlyList<NimBus.Core.Endpoints.DynamicForward> DynamicForwards { get; }
    }

    private sealed class ForwarderEndpoint(
        string id,
        IEnumerable<NimBus.Core.Events.IEventType> produces,
        IEnumerable<NimBus.Core.Events.IEventType> consumes) : NimBus.Core.Endpoints.IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public NimBus.Core.Endpoints.ISystem System => null!;
        public IEnumerable<NimBus.Core.Events.IEventType> EventTypesProduced { get; } = produces.ToList();
        public IEnumerable<NimBus.Core.Events.IEventType> EventTypesConsumed { get; } = consumes.ToList();
        public IEnumerable<NimBus.Core.Endpoints.IRoleAssignment> RoleAssignments => [];
    }

    private static BrokerMessage NewMessage(string messageId, string? sessionId = null) => new()
    {
        MessageId = messageId,
        SessionId = sessionId,
        Body = System.Text.Encoding.UTF8.GetBytes("payload"),
    };

    private static void SettleExpired(string operation, BrokerNamespace broker, Guid lockToken)
    {
        switch (operation)
        {
            case "complete":
                broker.Complete("events", "consumer", lockToken, "receiver-1");
                break;
            case "abandon":
                broker.Abandon("events", "consumer", lockToken, "receiver-1");
                break;
            case "dead-letter":
                broker.DeadLetter("events", "consumer", lockToken, "receiver-1", null, null);
                break;
            case "release":
                broker.Release("events", "consumer", lockToken, "receiver-1");
                break;
            case "complete-by-token":
                broker.CompleteByLockToken("events", "consumer", lockToken);
                break;
            case "abandon-by-token":
                broker.AbandonByLockToken("events", "consumer", lockToken);
                break;
            case "dead-letter-by-token":
                broker.DeadLetterByLockToken("events", "consumer", lockToken, null, null);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown settlement operation.");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
