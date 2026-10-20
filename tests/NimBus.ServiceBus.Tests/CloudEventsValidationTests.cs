#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CloudEvents;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;
using NimBus.Testing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// CloudEvents opt-in, dead-letter validation and AutoDetect dispatch tests. Covers
/// AC1, AC2, AC3 (opt-in config), AC9 (AutoDetect mix), AC12 (invalid CloudEvent →
/// dead-letter) and AC13 (unknown type → dead-letter).
/// </summary>
[TestClass]
public class CloudEventsValidationTests
{
    // ── Opt-in configuration (AC1, AC2, AC3) ────────────────────────────

    [TestMethod]
    public void PublisherOptions_WithoutUseCloudEvents_KeepsNativeBehavior()
    {
        var options = new NimBusPublisherOptions { Endpoint = "Billing" };
        Assert.IsNull(options.CloudEvents); // AC1
    }

    [TestMethod]
    public void PublisherOptions_UseCloudEvents_ExposesConfiguredDefaults()
    {
        var options = new NimBusPublisherOptions { Endpoint = "Billing" };
        options.UseCloudEvents(ce => ce.Source = new System.Uri("urn:test:billing"));

        Assert.IsNotNull(options.CloudEvents); // AC2
        Assert.AreEqual("application/json", options.CloudEvents.DataContentType);
        Assert.AreEqual(CloudEventContentMode.Binary, options.CloudEvents.ContentMode);
        Assert.AreEqual(CloudEventTypeNameStrategy.UnqualifiedName, options.CloudEvents.TypeNameStrategy);
    }

    [TestMethod]
    public void SubscriberOptions_UseCloudEvents_ExposesModeAndPrefixes()
    {
        var options = new NimBusSubscriberOptions { Endpoint = "Billing" };
        options.UseCloudEvents(ce => ce.Mode = CompatibilityMode.AutoDetect);

        Assert.IsNotNull(options.CloudEvents); // AC3
        Assert.AreEqual(CompatibilityMode.AutoDetect, options.CloudEvents.Mode);
        CollectionAssert.Contains((System.Collections.ICollection)options.CloudEvents.AcceptedPrefixes, "cloudEvents:");
        CollectionAssert.Contains((System.Collections.ICollection)options.CloudEvents.AcceptedPrefixes, "ce-");

        var readOptions = options.CloudEvents.ToReadOptions();
        Assert.AreEqual(CompatibilityMode.AutoDetect, readOptions.Mode);
    }

    [TestMethod]
    public void SubscriberOptions_WithoutUseCloudEvents_KeepsNativeBehavior()
    {
        var options = new NimBusSubscriberOptions { Endpoint = "Billing" };
        Assert.IsNull(options.CloudEvents); // AC1
    }

    // ── Dead-letter validation (AC12, AC13) ─────────────────────────────

    [TestMethod]
    [DataRow("id")]
    [DataRow("source")]
    [DataRow("type")]
    [DataRow("specversion")]
    public async Task InvalidCloudEvent_MissingRequiredAttribute_IsRejectedAsPermanentFailure(string missing)
    {
        var ce = new CloudEvent { Id = "1", Source = "urn:x", Type = "OrderPlaced", SpecVersion = "1.0" };
        switch (missing)
        {
            case "id": ce.Id = null!; break;
            case "source": ce.Source = null!; break;
            case "type": ce.Type = null!; break;
            case "specversion": ce.SpecVersion = null; break;
        }

        var inner = new RecordingContextHandler();
        var decorator = new CloudEventValidatingContextHandler(inner);
        var context = ContextFor(ce);

        var ex = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => decorator.Handle(context));
        Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidCloudEventException));
        StringAssert.Contains(ex.InnerException.Message, missing);
        Assert.IsFalse(inner.WasCalled, "An invalid CloudEvent must be rejected before dispatch.");
    }

    [TestMethod]
    public async Task UnknownType_MapsToNoHandler_IsRejectedAsPermanentFailure()
    {
        var ce = new CloudEvent { Id = "1", Source = "urn:x", Type = "NoSuchType", SpecVersion = "1.0" };
        var inner = new RecordingContextHandler { ToThrow = new EventHandlerNotFoundException("no handler for NoSuchType") };
        var decorator = new CloudEventValidatingContextHandler(inner);

        var ex = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => decorator.Handle(ContextFor(ce)));
        Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidCloudEventException)); // AC13
        StringAssert.Contains(ex.InnerException.Message, "NoSuchType");
    }

    [TestMethod]
    public async Task ValidCloudEvent_DelegatesToInner()
    {
        var ce = new CloudEvent { Id = "1", Source = "urn:x", Type = "OrderPlaced", SpecVersion = "1.0" };
        var inner = new RecordingContextHandler();
        var decorator = new CloudEventValidatingContextHandler(inner);

        await decorator.Handle(ContextFor(ce));

        Assert.IsTrue(inner.WasCalled);
    }

    [TestMethod]
    public async Task NativeMessage_BypassesCloudEventsValidation()
    {
        // GetCloudEvent() == null → decorator delegates straight through (native path).
        var message = new Message { MessageId = "n", SessionId = "s", MessageType = MessageType.EventRequest, MessageContent = new MessageContent() };
        var context = new InMemoryMessageContext(message, new InMemorySessionState());
        var inner = new RecordingContextHandler();
        var decorator = new CloudEventValidatingContextHandler(inner);

        await decorator.Handle(context);

        Assert.IsTrue(inner.WasCalled);
    }

    // ── AutoDetect mixed dispatch (AC9) ─────────────────────────────────

    [TestMethod]
    public async Task AutoDetect_MixedNativeAndCloudEvents_DispatchesEachToCorrectHandler()
    {
        var provider = new EventHandlerProvider();
        var orderHandler = new RecordingJsonHandler();
        var shipHandler = new RecordingJsonHandler();
        provider.RegisterHandler("OrderPlaced", () => orderHandler);
        provider.RegisterHandler("ShipmentSent", () => shipHandler);

        var readOptions = new CloudEventReadOptions { Mode = CompatibilityMode.AutoDetect };
        var decorator = new CloudEventValidatingContextHandler(provider);

        // Native NimBus message routed by its own MessageContent.EventTypeId.
        var nativeContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" } };
        var nativeMsg = new FakeCloudEventMessage
        {
            MessageId = "native-1",
            SessionId = "s1",
            Body = Encoding.UTF8.GetBytes(Newtonsoft.Json.JsonConvert.SerializeObject(nativeContent)),
        };
        nativeMsg.Properties[UserPropertyName.From.ToString()] = "Storefront";
        nativeMsg.Properties[UserPropertyName.MessageType.ToString()] = MessageType.EventRequest.ToString();
        await decorator.Handle(new MessageContext(nativeMsg, new InertServiceBusSession(), false, readOptions));

        Assert.IsTrue(orderHandler.WasCalled);
        Assert.IsFalse(shipHandler.WasCalled, "Native OrderPlaced must not reach the ShipmentSent handler.");

        // External CloudEvent routed by its type.
        var ceMsg = new FakeCloudEventMessage
        {
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"trackingId\":\"T-1\"}"),
        };
        ceMsg.Properties["cloudEvents:specversion"] = "1.0";
        ceMsg.Properties["cloudEvents:id"] = "ext-1";
        ceMsg.Properties["cloudEvents:source"] = "urn:ext:wms";
        ceMsg.Properties["cloudEvents:type"] = "ShipmentSent";
        await decorator.Handle(new MessageContext(ceMsg, new InertServiceBusSession(), false, readOptions));

        Assert.IsTrue(shipHandler.WasCalled); // AC9
    }

    // ── Missing-id dead-letter safety (AC12) ────────────────────────────

    [TestMethod]
    public void CloudEventMissingIdAndMessageId_IdentityAccessorsDoNotThrow()
    {
        // An external CloudEvents producer may omit both the CloudEvents `id` and the
        // native Service Bus MessageId. The message is invalid (missing `id`) and will
        // be dead-lettered by the validating handler — but MessageHandler reads EventId
        // (and MessageId) while logging the dead-letter. A null there would throw
        // InvalidMessageException and crash the processor BEFORE the message is
        // dead-lettered. AC12 requires a clean dead-letter, so these accessors must
        // return a non-null placeholder instead of throwing.
        var readOptions = new CloudEventReadOptions { Mode = CompatibilityMode.AutoDetect };
        var ceMsg = new FakeCloudEventMessage
        {
            MessageId = null!, // producer omitted the native Service Bus MessageId
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        ceMsg.Properties["cloudEvents:specversion"] = "1.0";
        ceMsg.Properties["cloudEvents:source"] = "urn:ext:wms";
        ceMsg.Properties["cloudEvents:type"] = "ShipmentSent";
        // deliberately no cloudEvents:id

        var context = new MessageContext(ceMsg, new InertServiceBusSession(), false, readOptions);

        // Neither accessor may throw — this is the exact read MessageHandler performs
        // on the permanent-failure dead-letter path.
        var eventId = context.EventId;
        var messageId = context.MessageId;

        Assert.IsFalse(string.IsNullOrEmpty(eventId), "EventId must be non-null so dead-letter logging does not crash.");
        Assert.IsFalse(string.IsNullOrEmpty(messageId), "MessageId must be non-null so dead-letter logging does not crash.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static InMemoryMessageContext ContextFor(CloudEvent ce)
    {
        var message = new Message
        {
            MessageId = "m",
            SessionId = "s",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = ce.Type, EventJson = "{}" } },
            CloudEvent = new CloudEventPublishContext(ce, CloudEventContentMode.Binary),
        };
        return new InMemoryMessageContext(message, new InMemorySessionState());
    }

    private sealed class RecordingContextHandler : IEventContextHandler
    {
        public bool WasCalled { get; private set; }
        public System.Exception? ToThrow { get; set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            if (ToThrow != null) throw ToThrow;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingJsonHandler : IEventJsonHandler
    {
        public bool WasCalled { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }
}
