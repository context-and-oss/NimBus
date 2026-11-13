#pragma warning disable CA1707, CA2007

using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.ServiceBus.Provisioning;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Tests;

/// <summary>
/// Spec 045 end to end on the in-process broker: the descriptor's routing-key rules, loaded into
/// the emulator, send each published key to exactly one consumer, and everything unclaimed or
/// keyless to the unmatched consumer.
/// </summary>
[TestClass]
public sealed class RoutingKeyForwardingTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class RoutedCustomerCreated : Event
    {
        public string? Instance { get; set; }
        public string CustomerNo { get; set; } = "C-1";
    }

    [RoutingKey(nameof(Instance))]
    public sealed class RouteToInstance : Command
    {
        public string? Instance { get; set; }
    }

    public sealed class RoutingCrm : Endpoint
    {
        public RoutingCrm()
        {
            Produces<RoutedCustomerCreated>();
            Produces<RouteToInstance>();
        }
    }

    public sealed class RoutingErp01 : Endpoint
    {
        public RoutingErp01()
        {
            Consumes<RoutedCustomerCreated>("01");
            Consumes<RouteToInstance>("01");
        }
    }

    public sealed class RoutingErp02 : Endpoint
    {
        public RoutingErp02()
        {
            Consumes<RoutedCustomerCreated>("02");
            Consumes<RouteToInstance>("02");
        }
    }

    public sealed class RoutingUnrouted : Endpoint
    {
        public RoutingUnrouted()
        {
            ConsumesUnmatched<RoutedCustomerCreated>();
            ConsumesUnmatched<RouteToInstance>();
        }
    }

    public sealed class RoutingReporting : Endpoint
    {
        public RoutingReporting() => Consumes<RoutedCustomerCreated>();
    }

    private sealed class RoutingPlatform : NimBus.Core.Platform
    {
        public RoutingPlatform()
        {
            AddEndpoint(new RoutingCrm());
            AddEndpoint(new RoutingErp01());
            AddEndpoint(new RoutingErp02());
            AddEndpoint(new RoutingUnrouted());
            AddEndpoint(new RoutingReporting());
        }
    }

    [TestMethod]
    [DataRow("01", nameof(RoutingErp01))]
    [DataRow("02", nameof(RoutingErp02))]
    [DataRow("99", nameof(RoutingUnrouted))]
    [DataRow(null, nameof(RoutingUnrouted))]
    public async Task Each_key_reaches_exactly_one_instance_or_the_unmatched_consumer(string? key, string expectedConsumer)
    {
        var broker = BuildBroker(out var keyedForwarders);

        await PublishAsync(broker, new RoutedCustomerCreated { Instance = key });

        foreach (var forwarder in keyedForwarders)
        {
            var expected = forwarder == expectedConsumer ? 1L : 0L;
            Assert.AreEqual(expected, Count(broker, forwarder), $"{forwarder} for key '{key ?? "<none>"}'");
        }

        // The unkeyed reporting consumer takes every key, as before routing keys.
        Assert.AreEqual(1L, Count(broker, nameof(RoutingReporting)));
    }

    [TestMethod]
    [DataRow("01", nameof(RoutingErp01))]
    [DataRow("02", nameof(RoutingErp02))]
    [DataRow("xx", nameof(RoutingUnrouted))]
    public async Task A_keyed_command_reaches_one_consumer(string key, string expectedConsumer)
    {
        var broker = BuildBroker(out var keyedForwarders);

        await PublishAsync(broker, new RouteToInstance { Instance = key });

        Assert.AreEqual(1L, keyedForwarders.Sum(forwarder => Count(broker, forwarder)));
        Assert.AreEqual(1L, Count(broker, expectedConsumer));
    }

    [TestMethod]
    public void The_platform_is_valid()
    {
        NimBus.Core.PlatformValidation.EnsureValid(new RoutingPlatform());
    }

    private static readonly string[] KeyedForwarderNames =
        [nameof(RoutingErp01), nameof(RoutingErp02), nameof(RoutingUnrouted)];

    private static BrokerNamespace BuildBroker(out IReadOnlyList<string> keyedForwarders)
    {
        var platform = new RoutingPlatform();
        var crm = platform.Endpoints.Single(endpoint => endpoint.Id == nameof(RoutingCrm));
        var expected = TopologyDescriptor.ForEndpointTopic(crm, platform);

        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition(crm.Id));
        foreach (var subscription in expected)
        {
            // ForwardTo is left off so what each forwarder takes stays countable here.
            broker.CreateSubscription(crm.Id, new SubscriptionDefinition(subscription.Name) { RequiresSession = subscription.RequiresSession });
            if (!subscription.KeepDefaultRule)
            {
                broker.DeleteRule(crm.Id, subscription.Name, TopologyDescriptor.DefaultRuleName);
            }

            foreach (var rule in subscription.Rules)
            {
                broker.CreateRule(crm.Id, subscription.Name, new RuleDefinition(rule.Name, rule.Filter, rule.Action));
            }
        }

        keyedForwarders = KeyedForwarderNames;
        return broker;
    }

    private static async Task PublishAsync(BrokerNamespace broker, IEvent @event)
    {
        var sender = new CapturingSender();
        await new PublisherClient(sender, nameof(RoutingCrm)).Publish(@event);
        var wire = NimBus.ServiceBus.MessageHelper.ToServiceBusMessage(sender.Sent.Single());
        broker.Publish(nameof(RoutingCrm), new BrokerMessage
        {
            MessageId = wire.MessageId,
            SessionId = wire.SessionId,
            CorrelationId = wire.CorrelationId,
            Body = wire.Body.ToMemory(),
            ApplicationProperties = wire.ApplicationProperties.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal),
        });
    }

    private static long Count(BrokerNamespace broker, string subscription) =>
        broker.GetSubscriptionRuntimeProperties(nameof(RoutingCrm), subscription).TotalMessageCount;

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

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
