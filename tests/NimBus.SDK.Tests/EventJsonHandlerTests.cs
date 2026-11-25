#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK.Tests;

[TestClass]
public class EventJsonHandlerTests
{
    [TestMethod]
    public async Task Handle_UsesAuthoritativeContextEventTypeInHandlerMetadata()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        await sut.Handle(MessageContextStub.ForEventTypes("header.event.v1", string.Empty, "{}"));

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual("header.event.v1", handler.LastContext?.EventType);
    }

    [TestMethod]
    public async Task Handle_ExposesInboundWorkflowIdentityAndLineage()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);
        var messageContext = MessageContextStub.ForWorkflowEvent(
            nameof(TestEvent),
            "{}",
            messageId: "inventory-reserved-1",
            sessionId: "order-42",
            correlationId: "conversation-7",
            parentMessageId: "reserve-inventory-1",
            originatingMessageId: "order-placed-1");

        await sut.Handle(messageContext);

        Assert.AreEqual("order-42", handler.LastContext?.SessionId);
        Assert.AreEqual("conversation-7", handler.LastContext?.CorrelationId);
        Assert.AreEqual("reserve-inventory-1", handler.LastContext?.ParentMessageId);
        Assert.AreEqual("order-placed-1", handler.LastContext?.OriginatingMessageId);
    }

    [TestMethod]
    public async Task Handle_SelfSentinelOriginatingMessageId_NormalizedToOwnMessageId()
    {
        // "self" is an internal wire sentinel meaning "this message IS the
        // origin". Handler authors persist OriginatingMessageId as handoff
        // settlement coordinates, so leaking the literal sentinel would
        // corrupt lineage on the settlement wire message.
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);
        var messageContext = MessageContextStub.ForWorkflowEvent(
            nameof(TestEvent),
            "{}",
            messageId: "order-placed-1",
            sessionId: "order-42",
            correlationId: "conversation-7",
            parentMessageId: "self",
            originatingMessageId: "self");

        await sut.Handle(messageContext);

        Assert.AreEqual("order-placed-1", handler.LastContext?.OriginatingMessageId);
    }

    [TestMethod]
    public async Task Handle_LiteralNull_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), "null")));

        Assert.IsInstanceOfType<JsonSerializationException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handle_MissingPayload_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), null!)));

        Assert.IsInstanceOfType<JsonSerializationException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handle_MalformedJson_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), "{")));

        Assert.IsInstanceOfType<JsonSerializationException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Handle_JsonExceedingSafeMaxDepth_RejectsBeforeInvokingHandler()
    {
        var handler = new RecordingHandler();
        var sut = new EventJsonHandler<TestEvent>(handler);
        var json = string.Concat(Enumerable.Repeat("{\"Value\":", 33))
            + "{}"
            + new string('}', 33);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), json)));

        Assert.IsInstanceOfType<JsonReaderException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Handle_HostileAmbientTypeNameHandling_DoesNotInstantiatePayloadType()
    {
        var previousSettings = JsonConvert.DefaultSettings;
        HostilePayload.ConstructionCount = 0;
        JsonConvert.DefaultSettings = () => new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
        };
        try
        {
            var handler = new RecordingHandler();
            var sut = new EventJsonHandler<TestEvent>(handler);
            var typeName = $"{typeof(HostilePayload).FullName}, {typeof(HostilePayload).Assembly.GetName().Name}";
            var json = $"{{\"Value\":{{\"$type\":{JsonConvert.SerializeObject(typeName)}}}}}";

            await sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), json));

            Assert.AreEqual(1, handler.Calls);
            Assert.AreEqual(0, HostilePayload.ConstructionCount);
            Assert.IsInstanceOfType<JObject>(handler.LastEvent?.Value);
        }
        finally
        {
            JsonConvert.DefaultSettings = previousSettings;
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Handle_MutatedPublicSafeSettings_DoesNotEnableTypeNameHandling()
    {
        var previousTypeNameHandling = Constants.SafeJsonSettings.TypeNameHandling;
        HostilePayload.ConstructionCount = 0;
        Constants.SafeJsonSettings.TypeNameHandling = TypeNameHandling.Auto;
        try
        {
            var handler = new RecordingHandler();
            var sut = new EventJsonHandler<TestEvent>(handler);
            var typeName = $"{typeof(HostilePayload).FullName}, {typeof(HostilePayload).Assembly.GetName().Name}";
            var json = $"{{\"Value\":{{\"$type\":{JsonConvert.SerializeObject(typeName)}}}}}";

            await sut.Handle(MessageContextStub.ForEventType(nameof(TestEvent), json));

            Assert.AreEqual(1, handler.Calls);
            Assert.AreEqual(0, HostilePayload.ConstructionCount);
            Assert.IsInstanceOfType<JObject>(handler.LastEvent?.Value);
        }
        finally
        {
            Constants.SafeJsonSettings.TypeNameHandling = previousTypeNameHandling;
        }
    }

    // The four tests below pin what docs/message-versioning.md promises about a
    // payload whose shape differs from the handler's event class.

    [TestMethod]
    public async Task Handle_PayloadWithUnknownProperty_IgnoresItAndInvokesHandler()
    {
        var handler = new ContractHandler<ContractEvent>();
        var sut = new EventJsonHandler<ContractEvent>(handler);

        await sut.Handle(MessageContextStub.ForEventType(
            nameof(ContractEvent),
            "{\"Name\":\"Ada\",\"Quantity\":2,\"AddedByNewerPublisher\":\"x\"}"));

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual("Ada", handler.LastEvent?.Name);
        Assert.AreEqual(2, handler.LastEvent?.Quantity);
    }

    [TestMethod]
    public async Task Handle_PayloadMissingProperties_LeavesDefaultsWithoutValidating()
    {
        // An older payload reaches a newer event class. The receive path does
        // not run DataAnnotations, so even a [Required] property arrives null.
        var handler = new ContractHandler<ContractEvent>();
        var sut = new EventJsonHandler<ContractEvent>(handler);

        await sut.Handle(MessageContextStub.ForEventType(nameof(ContractEvent), "{}"));

        Assert.AreEqual(1, handler.Calls);
        Assert.IsNull(handler.LastEvent?.Name);
        Assert.AreEqual(0, handler.LastEvent?.Quantity);
    }

    [TestMethod]
    public async Task Handle_PayloadMissingCSharpRequiredMember_LeavesItNull()
    {
        var handler = new ContractHandler<RequiredMemberEvent>();
        var sut = new EventJsonHandler<RequiredMemberEvent>(handler);

        await sut.Handle(MessageContextStub.ForEventType(nameof(RequiredMemberEvent), "{}"));

        Assert.AreEqual(1, handler.Calls);
        Assert.IsNull(handler.LastEvent?.Name);
    }

    [TestMethod]
    public async Task Handle_PayloadWithRetypedProperty_RejectsBeforeInvokingHandler()
    {
        var handler = new ContractHandler<ContractEvent>();
        var sut = new EventJsonHandler<ContractEvent>(handler);

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() =>
            sut.Handle(MessageContextStub.ForEventType(
                nameof(ContractEvent),
                "{\"Name\":\"Ada\",\"Quantity\":\"two\"}")));

        Assert.IsInstanceOfType<JsonException>(exception.InnerException);
        Assert.AreEqual(0, handler.Calls);
    }

    public sealed class TestEvent : Event
    {
        public object? Value { get; set; }
    }

    public sealed class ContractEvent : Event
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string? Name { get; set; }

        public int Quantity { get; set; }
    }

    public sealed class RequiredMemberEvent : Event
    {
        public required string Name { get; init; }
    }

    private sealed class ContractHandler<TEvent> : IEventHandler<TEvent>
        where TEvent : IEvent
    {
        public int Calls { get; private set; }
        public TEvent? LastEvent { get; private set; }

        public Task Handle(TEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastEvent = message;
            return Task.CompletedTask;
        }
    }

    public sealed class HostilePayload
    {
        public HostilePayload()
        {
            ConstructionCount++;
        }

        public static int ConstructionCount { get; set; }
    }

    private sealed class RecordingHandler : IEventHandler<TestEvent>
    {
        public int Calls { get; private set; }
        public TestEvent? LastEvent { get; private set; }
        public IEventHandlerContext? LastContext { get; private set; }

        public Task Handle(TestEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastEvent = message;
            LastContext = context;
            return Task.CompletedTask;
        }
    }
}
