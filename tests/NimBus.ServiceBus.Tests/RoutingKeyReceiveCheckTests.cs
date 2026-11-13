#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.Testing;
using NimBus.Testing.Extensions;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Spec 045 §5 and §6, through the in-memory test transport, which composes the subscriber the
/// way the hosted SDK does: a message whose routing key the endpoint doesn't take, or whose
/// property disagrees with its payload, is dead-lettered and never reaches the handler.
/// </summary>
[TestClass]
public sealed class RoutingKeyReceiveCheckTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class GuardedCustomerCreated : Event
    {
        public string? Instance { get; set; }
        public string CustomerNo { get; set; } = "C-1";
    }

    public sealed class GuardedErp01 : Endpoint
    {
        public GuardedErp01() => Consumes<GuardedCustomerCreated>("01");
    }

    public sealed class GuardedUnrouted : Endpoint
    {
        public GuardedUnrouted() => ConsumesUnmatched<GuardedCustomerCreated>();
    }

    private sealed class GuardedPlatform : Platform
    {
        public GuardedPlatform()
        {
            AddEndpoint(new GuardedErp01());
            AddEndpoint(new GuardedUnrouted());
        }
    }

    public sealed class Recorder
    {
        public List<string?> Handled { get; } = [];
    }

    public sealed class RecordingHandler(Recorder recorder) : IEventHandler<GuardedCustomerCreated>
    {
        public Task Handle(GuardedCustomerCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            recorder.Handled.Add(message.Instance);
            return Task.CompletedTask;
        }
    }

    private static (ServiceProvider Root, InMemoryMessageBus Bus, IMessageHandler Handler, Recorder Recorder) Build(
        Action<NimBusSubscriberBuilder> configure,
        string endpoint = nameof(GuardedErp01),
        IPlatform? platform = null)
    {
        var services = new ServiceCollection();
        var recorder = new Recorder();
        services.AddSingleton(recorder);
        if (platform is not null)
            services.AddSingleton(platform);
        services.AddNimBusTestTransport(configure, endpoint);
        var root = services.BuildServiceProvider();
        return (root, root.GetRequiredService<InMemoryMessageBus>(), root.GetRequiredService<IMessageHandler>(), recorder);
    }

    // The in-memory transport dead-letters even a successful delivery ("Failed to handle
    // message.", a NullReferenceException after the handler), so success is asserted through the
    // handler's side effects and the absence of a routing-key rejection.
    private static bool Rejected(InMemoryDeliveryResult result) =>
        result.Context.DeadLetterReasonRecorded?.Contains(nameof(Core.Messages.Exceptions.RoutingKeyRejectedException), StringComparison.Ordinal) == true;

    private static void AssertNotRejected(InMemoryDeliveryResult result) =>
        Assert.IsFalse(Rejected(result), result.Context.DeadLetterReasonRecorded);

    private static Message RawEvent(string? payloadKey, string? propertyKey, MessageType type = MessageType.EventRequest) => new()
    {
        To = nameof(GuardedErp01),
        From = Constants.ManagerId,
        EventId = Guid.NewGuid().ToString(),
        SessionId = "session-1",
        CorrelationId = "correlation-1",
        MessageId = Guid.NewGuid().ToString(),
        EventTypeId = nameof(GuardedCustomerCreated),
        MessageType = type,
        RoutingKey = propertyKey,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent
            {
                EventTypeId = nameof(GuardedCustomerCreated),
                EventJson = JsonConvert.SerializeObject(new GuardedCustomerCreated { Instance = payloadKey }),
            },
        },
    };

    [TestMethod]
    public async Task AnAcceptedKey_ReachesTheHandler()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<GuardedCustomerCreated, RecordingHandler>()
            .EnforceRoutingKeys<GuardedErp01>());
        await using var _ = root;

        await root.GetRequiredService<IPublisherClient>().Publish(new GuardedCustomerCreated { Instance = "01" });
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        AssertNotRejected(result);
        CollectionAssert.AreEqual(new[] { "01" }, recorder.Handled);
    }

    [TestMethod]
    public async Task AForeignKey_IsDeadLetteredWithAReason()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<GuardedCustomerCreated, RecordingHandler>()
            .EnforceRoutingKeys<GuardedErp01>());
        await using var _ = root;

        await root.GetRequiredService<IPublisherClient>().Publish(new GuardedCustomerCreated { Instance = "02" });
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsTrue(Rejected(result), result.Context.DeadLetterReasonRecorded);
        StringAssert.Contains(result.Context.DeadLetterReasonRecorded, "RoutingKeyRejectedException", StringComparison.Ordinal);
        StringAssert.Contains(result.Context.DeadLetterReasonRecorded, "Routing key '02' is not accepted by endpoint GuardedErp01 (accepts: 01)", StringComparison.Ordinal);
        Assert.AreEqual(0, recorder.Handled.Count);
    }

    [TestMethod]
    public async Task AMissingKey_IsDeadLettered()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<GuardedCustomerCreated, RecordingHandler>()
            .EnforceRoutingKeys("01"));
        await using var _ = root;

        await bus.Send(RawEvent(payloadKey: null, propertyKey: null));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsTrue(Rejected(result), result.Context.DeadLetterReasonRecorded);
        StringAssert.Contains(result.Context.DeadLetterReasonRecorded, "No routing key is not accepted", StringComparison.Ordinal);
        Assert.AreEqual(0, recorder.Handled.Count);
    }

    [TestMethod]
    public async Task AKeylessResubmissionWithAMatchingPayload_IsHandled()
    {
        // ManagerClient.Resubmit builds a new message from the stored row: no RoutingKey property.
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<GuardedCustomerCreated, RecordingHandler>()
            .EnforceRoutingKeys<GuardedErp01>());
        await using var _ = root;

        await bus.Send(RawEvent(payloadKey: "01", propertyKey: null, MessageType.ResubmissionRequest));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        AssertNotRejected(result);
        CollectionAssert.AreEqual(new[] { "01" }, recorder.Handled);
    }

    [TestMethod]
    public async Task APayloadEditedToAnotherKeyOnResubmit_IsDeadLettered()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<GuardedCustomerCreated, RecordingHandler>()
            .EnforceRoutingKeys<GuardedErp01>());
        await using var _ = root;

        await bus.Send(RawEvent(payloadKey: "02", propertyKey: null, MessageType.ResubmissionRequest));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsTrue(Rejected(result), result.Context.DeadLetterReasonRecorded);
        Assert.AreEqual(0, recorder.Handled.Count);
    }

    [TestMethod]
    public async Task APropertyDisagreeingWithThePayload_IsDeadLettered()
    {
        var (root, bus, handler, recorder) = Build(builder => builder
            .AddHandler<GuardedCustomerCreated, RecordingHandler>()
            .EnforceRoutingKeys<GuardedErp01>());
        await using var _ = root;

        await bus.Send(RawEvent(payloadKey: "01", propertyKey: "02"));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsTrue(Rejected(result), result.Context.DeadLetterReasonRecorded);
        StringAssert.Contains(result.Context.DeadLetterReasonRecorded, "RoutingKey property '02' does not match the routing key '01'", StringComparison.Ordinal);
        Assert.AreEqual(0, recorder.Handled.Count);
    }

    [TestMethod]
    public async Task TheCheckRunsFromARegisteredPlatform()
    {
        var (root, bus, handler, recorder) = Build(
            builder => builder.AddHandler<GuardedCustomerCreated, RecordingHandler>(),
            platform: new GuardedPlatform());
        await using var _ = root;

        await root.GetRequiredService<IPublisherClient>().Publish(new GuardedCustomerCreated { Instance = "02" });
        await root.GetRequiredService<IPublisherClient>().Publish(new GuardedCustomerCreated { Instance = "01", CustomerNo = "C-2" });
        var results = await bus.DeliverAllWithResults(handler);

        CollectionAssert.AreEqual(new[] { true, false }, results.Select(Rejected).ToArray());
        CollectionAssert.AreEqual(new[] { "01" }, recorder.Handled);
    }

    [TestMethod]
    public async Task WithoutEnforcementOrPlatform_NothingIsChecked()
    {
        var (root, bus, handler, recorder) = Build(builder => builder.AddHandler<GuardedCustomerCreated, RecordingHandler>());
        await using var _ = root;

        await root.GetRequiredService<IPublisherClient>().Publish(new GuardedCustomerCreated { Instance = "02" });
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        AssertNotRejected(result);
        CollectionAssert.AreEqual(new[] { "02" }, recorder.Handled);
    }

    [TestMethod]
    [DataRow("99", "with routing key '99'")]
    [DataRow(null, "with no routing key")]
    public async Task TheUnmatchedHandler_DeadLettersNamingTheKey(string? key, string expected)
    {
        var (root, bus, handler, _) = Build(
            builder => builder.AddUnmatchedHandler<GuardedCustomerCreated>(),
            endpoint: nameof(GuardedUnrouted),
            platform: new GuardedPlatform());
        await using var __ = root;

        await root.GetRequiredService<IPublisherClient>().Publish(new GuardedCustomerCreated { Instance = key });
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsTrue(Rejected(result), result.Context.DeadLetterReasonRecorded);
        StringAssert.Contains(result.Context.DeadLetterReasonRecorded, $"No endpoint consumes GuardedCustomerCreated {expected}", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task TheUnmatchedConsumer_StillChecksPropertyAgainstPayload()
    {
        var (root, bus, handler, _) = Build(
            builder => builder.AddUnmatchedHandler<GuardedCustomerCreated>(),
            endpoint: nameof(GuardedUnrouted));
        await using var __ = root;

        await bus.Send(RawEvent(payloadKey: "99", propertyKey: "98"));
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.IsTrue(Rejected(result), result.Context.DeadLetterReasonRecorded);
        StringAssert.Contains(result.Context.DeadLetterReasonRecorded, "does not match", StringComparison.Ordinal);
    }

    // ── String-keyed handlers ──────────────────────────────────────

    private sealed class DynamicConsumer : IEndpoint
    {
        private readonly IEventType _type = new ConfiguredType("crm.contact.enriched.v1");

        public string Id => "DynamicErp01";
        public string Name => Id;
        public string? Description => null;
        public string? Namespace => null;
        public string SecurityGroupName => "";
        public ISystem? System => null;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [_type];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
        public IReadOnlyCollection<string> GetRoutingKeys(IEventType eventType) => eventType.Id == _type.Id ? ["01"] : [];
    }

    private sealed class ConfiguredType(string id) : IEventType
    {
        public string Id => id;
        public string Name => id;
        public string? Description => null;
        public string? Namespace => null;
        public IEnumerable<IProperty> Properties => [];
        public Type GetEventClassType() => null!;
        public IEvent? GetEventExample() => null;
    }

    private sealed class DynamicPlatform : Platform
    {
        public DynamicPlatform() => AddEndpoint(new DynamicConsumer());
    }

    private sealed class CountingJsonHandler : IEventJsonHandler
    {
        public int Calls { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow("01", false)]
    [DataRow("02", true)]
    [DataRow(null, false)]
    public async Task AStringKeyedHandler_ChecksOnlyTheProperty(string? property, bool deadLettered)
    {
        var json = new CountingJsonHandler();
        var (root, bus, handler, _) = Build(
            builder => builder.AddDynamicHandler("crm.contact.enriched.v1", () => json),
            endpoint: "DynamicErp01",
            platform: new DynamicPlatform());
        await using var __ = root;

        var message = RawEvent(payloadKey: null, propertyKey: property);
        message.EventTypeId = "crm.contact.enriched.v1";
        message.MessageContent.EventContent!.EventTypeId = "crm.contact.enriched.v1";
        message.MessageContent.EventContent.EventJson = "{}";
        await bus.Send(message);
        var result = (await bus.DeliverAllWithResults(handler)).Single();

        Assert.AreEqual(deadLettered, Rejected(result));
        Assert.AreEqual(deadLettered ? 0 : 1, json.Calls);
    }

    [TestMethod]
    public void EnforceRoutingKeys_RejectsBadInputAndASecondCall()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentException>(() => builder.EnforceRoutingKeys());
        Assert.ThrowsExactly<ArgumentException>(() => builder.EnforceRoutingKeys("o'clock"));
        builder.EnforceRoutingKeys("01");
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.EnforceRoutingKeys<GuardedErp01>());
    }
}
