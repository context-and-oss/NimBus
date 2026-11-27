#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.Testing;
using NimBus.Testing.Extensions;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// The receive check parses a guarded payload once and the typed handler reuses it, so each
/// message is deserialized exactly once whether or not a routing-key policy applies.
/// Deserializations are counted per <see cref="CountedCustomerCreated.Marker"/>, so the tests
/// stay independent when they run in parallel.
/// </summary>
[TestClass]
public sealed class RoutingKeyPayloadReuseTests
{
    private static readonly ConcurrentDictionary<string, int> Deserializations = new(StringComparer.Ordinal);

    [RoutingKey(nameof(Instance))]
    public sealed class CountedCustomerCreated : Event
    {
        public string? Instance { get; set; }
        public string CustomerNo { get; set; } = "C-1";
        public string Marker { get; set; } = "";

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context) =>
            Deserializations.AddOrUpdate(Marker, 1, (_, count) => count + 1);
    }

    public sealed class UnkeyedCustomerCreated : Event
    {
        public string CustomerNo { get; set; } = "C-1";
        public string Marker { get; set; } = "";

        [OnDeserialized]
        internal void OnDeserialized(StreamingContext context) =>
            Deserializations.AddOrUpdate(Marker, 1, (_, count) => count + 1);
    }

    public sealed class Recorder
    {
        public List<CountedCustomerCreated> Keyed { get; } = [];
        public List<UnkeyedCustomerCreated> Unkeyed { get; } = [];
    }

    public sealed class KeyedHandler(Recorder recorder) : IEventHandler<CountedCustomerCreated>
    {
        public Task Handle(CountedCustomerCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            recorder.Keyed.Add(message);
            return Task.CompletedTask;
        }
    }

    public sealed class UnkeyedHandler(Recorder recorder) : IEventHandler<UnkeyedCustomerCreated>
    {
        public Task Handle(UnkeyedCustomerCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            recorder.Unkeyed.Add(message);
            return Task.CompletedTask;
        }
    }

    private static (ServiceProvider Root, InMemoryMessageBus Bus, IMessageHandler Handler, Recorder Recorder) Build(
        Action<NimBusSubscriberBuilder> configure)
    {
        var services = new ServiceCollection();
        var recorder = new Recorder();
        services.AddSingleton(recorder);
        services.AddNimBusTestTransport(configure, "PayloadReuseErp01");
        var root = services.BuildServiceProvider();
        return (root, root.GetRequiredService<InMemoryMessageBus>(), root.GetRequiredService<IMessageHandler>(), recorder);
    }

    private static string NewMarker() => Guid.NewGuid().ToString("N");

    private static int CountOf(string marker) => Deserializations.TryGetValue(marker, out var count) ? count : 0;

    private static bool Rejected(InMemoryDeliveryResult result) =>
        result.Context.DeadLetterReasonRecorded?.Contains(nameof(RoutingKeyRejectedException), StringComparison.Ordinal) == true;

    private static Message RawEvent(string eventJson) => new()
    {
        To = "PayloadReuseErp01",
        From = Constants.ManagerId,
        EventId = Guid.NewGuid().ToString(),
        SessionId = "session-1",
        CorrelationId = "correlation-1",
        MessageId = Guid.NewGuid().ToString(),
        EventTypeId = nameof(CountedCustomerCreated),
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent
            {
                EventTypeId = nameof(CountedCustomerCreated),
                EventJson = eventJson,
            },
        },
    };

    [TestMethod]
    public async Task AGuardedAcceptedPayload_IsDeserializedOnce_AndReachesTheHandlerIntact()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<CountedCustomerCreated, KeyedHandler>()
            .EnforceRoutingKeys("01"));
        await using var _ = root;
        var marker = NewMarker();

        await root.GetRequiredService<IPublisherClient>().Publish(
            new CountedCustomerCreated { Instance = "01", CustomerNo = "C-42", Marker = marker });
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsFalse(Rejected(result), result.Context.DeadLetterReasonRecorded);
        Assert.AreEqual(1, recorder.Keyed.Count);
        Assert.AreEqual("01", recorder.Keyed[0].Instance);
        Assert.AreEqual("C-42", recorder.Keyed[0].CustomerNo);
        Assert.AreEqual(marker, recorder.Keyed[0].Marker);
        Assert.AreEqual(1, CountOf(marker));
    }

    [TestMethod]
    public async Task SeveralGuardedMessages_EachGetTheirOwnPayload_DeserializedOnce()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<CountedCustomerCreated, KeyedHandler>()
            .EnforceRoutingKeys("01", "02"));
        await using var _ = root;
        var first = NewMarker();
        var second = NewMarker();

        var publisher = root.GetRequiredService<IPublisherClient>();
        await publisher.Publish(new CountedCustomerCreated { Instance = "01", CustomerNo = "C-1", Marker = first });
        await publisher.Publish(new CountedCustomerCreated { Instance = "02", CustomerNo = "C-2", Marker = second });
        var results = await bus.DeliverAllWithResults(handler);

        Assert.IsFalse(results.Any(Rejected));
        CollectionAssert.AreEqual(new[] { first, second }, recorder.Keyed.Select(e => e.Marker).ToArray());
        CollectionAssert.AreEqual(new[] { "01", "02" }, recorder.Keyed.Select(e => e.Instance).ToArray());
        CollectionAssert.AreEqual(new[] { "C-1", "C-2" }, recorder.Keyed.Select(e => e.CustomerNo).ToArray());
        Assert.AreEqual(1, CountOf(first));
        Assert.AreEqual(1, CountOf(second));
    }

    [TestMethod]
    public async Task WithoutEnforcement_TheKeyedPayloadIsDeserializedOnce()
    {
        var (root, bus, handler, recorder) = Build(builder => builder.AddHandler<CountedCustomerCreated, KeyedHandler>());
        await using var _ = root;
        var marker = NewMarker();

        await root.GetRequiredService<IPublisherClient>().Publish(
            new CountedCustomerCreated { Instance = "02", CustomerNo = "C-7", Marker = marker });
        await bus.DeliverAllWithResults(handler);

        Assert.AreEqual(1, recorder.Keyed.Count);
        Assert.AreEqual("C-7", recorder.Keyed[0].CustomerNo);
        Assert.AreEqual(1, CountOf(marker));
    }

    [TestMethod]
    public async Task AnEventTypeWithoutAPolicy_IsDeserializedOnce()
    {
        // Enforcement is on, but the unkeyed type has no policy, so the guard never parses it.
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<UnkeyedCustomerCreated, UnkeyedHandler>()
            .EnforceRoutingKeys("01"));
        await using var _ = root;
        var marker = NewMarker();

        await root.GetRequiredService<IPublisherClient>().Publish(
            new UnkeyedCustomerCreated { CustomerNo = "C-9", Marker = marker });
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsFalse(Rejected(result), result.Context.DeadLetterReasonRecorded);
        Assert.AreEqual(1, recorder.Unkeyed.Count);
        Assert.AreEqual("C-9", recorder.Unkeyed[0].CustomerNo);
        Assert.AreEqual(1, CountOf(marker));
    }

    [TestMethod]
    public async Task AMalformedGuardedPayload_IsNotRejectedByTheGuard_AndFailsInTheHandler()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<CountedCustomerCreated, KeyedHandler>()
            .EnforceRoutingKeys("01"));
        await using var _ = root;

        await bus.Send(RawEvent("{"));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsFalse(Rejected(result), result.Context.DeadLetterReasonRecorded);
        Assert.IsNotNull(result.Context.DeadLetterReasonRecorded);
        // MessageHandler records a PermanentFailureException as "Permanent failure: <inner type>: …".
        StringAssert.StartsWith(result.Context.DeadLetterReasonRecorded, "Permanent failure: JsonSerializationException:", StringComparison.Ordinal);
        Assert.AreEqual(0, recorder.Keyed.Count);
    }

    [TestMethod]
    public async Task ANullGuardedPayload_FailsInTheHandlerAsDeserializedToNull()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<CountedCustomerCreated, KeyedHandler>()
            .EnforceRoutingKeys("01"));
        await using var _ = root;

        await bus.Send(RawEvent("null"));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsFalse(Rejected(result), result.Context.DeadLetterReasonRecorded);
        StringAssert.Contains(
            result.Context.DeadLetterReasonRecorded + result.Context.DeadLetterErrorDescriptionRecorded,
            "deserialized to null",
            StringComparison.Ordinal);
        Assert.AreEqual(0, recorder.Keyed.Count);
    }
}
