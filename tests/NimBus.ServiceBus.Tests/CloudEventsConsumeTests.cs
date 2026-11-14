#pragma warning disable CA1707, CA1515, CA2007, CS0618
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CloudEvents;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using Newtonsoft.Json;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Consume-side CloudEvents tests: a message authored by a non-NimBus producer is
/// detected, normalized into the NimBus <see cref="MessageContent"/> envelope, and
/// exposed via <c>GetCloudEvent()</c>. Covers AC7, AC8, AC11, AC10 (consume side)
/// and the documented <c>ce-</c> alternate prefix.
/// </summary>
[TestClass]
public class CloudEventsConsumeTests
{
    private static MessageContext Consume(FakeCloudEventMessage message, CloudEventReadOptions? options = null) =>
        new(message, new InertServiceBusSession(), isDeferred: false, options ?? new CloudEventReadOptions());

    [TestMethod]
    public void BinaryCloudEvent_NormalizesToEventContentAndExposesCloudEvent()
    {
        var message = new FakeCloudEventMessage
        {
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"orderId\":\"O-1\"}"),
        };
        message.Properties["cloudEvents:specversion"] = "1.0";
        message.Properties["cloudEvents:id"] = "ext-1";
        message.Properties["cloudEvents:source"] = "urn:ext:crm";
        message.Properties["cloudEvents:type"] = "com.acme.OrderPlaced";

        var ctx = Consume(message);

        // AC7: data → EventJson, type → dispatch key (unqualified last segment by default).
        Assert.AreEqual("OrderPlaced", ctx.MessageContent.EventContent!.EventTypeId);
        Assert.AreEqual("{\"orderId\":\"O-1\"}", ctx.MessageContent.EventContent.EventJson);

        // AC11: the inbound CloudEvent is readable.
        var ce = ctx.GetCloudEvent();
        Assert.IsNotNull(ce);
        Assert.AreEqual("ext-1", ce.Id);
        Assert.AreEqual("urn:ext:crm", ce.Source);
        Assert.AreEqual("com.acme.OrderPlaced", ce.Type);
        Assert.AreEqual("1.0", ce.SpecVersion);

        // MessageId falls back to the CloudEvent id when the producer set no native SB MessageId.
        Assert.AreEqual("ext-1", ctx.MessageId);
    }

    [TestMethod]
    public void BinaryCloudEvent_AlternateCePrefix_IsDetected()
    {
        var message = new FakeCloudEventMessage
        {
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{\"orderId\":\"O-9\"}"),
        };
        message.Properties["ce-specversion"] = "1.0";
        message.Properties["ce-id"] = "ext-9";
        message.Properties["ce-source"] = "urn:ext:crm";
        message.Properties["ce-type"] = "OrderPlaced";

        var ctx = Consume(message);

        Assert.AreEqual("OrderPlaced", ctx.MessageContent.EventContent!.EventTypeId);
        Assert.AreEqual("ext-9", ctx.GetCloudEvent()!.Id);
    }

    [TestMethod]
    public void BinaryCloudEvent_CustomExtensionAttributes_AreExposedViaGetCloudEvent()
    {
        // AC11 / finding #2: arbitrary (non-core) cloudEvents:* / ce-* attributes are
        // surfaced as CloudEvent extensions on the binary consume path, not just the
        // two mapping-configured names.
        var message = new FakeCloudEventMessage
        {
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        message.Properties["cloudEvents:specversion"] = "1.0";
        message.Properties["cloudEvents:id"] = "ext-ext-1";
        message.Properties["cloudEvents:source"] = "urn:ext:crm";
        message.Properties["cloudEvents:type"] = "OrderPlaced";
        message.Properties["cloudEvents:correlationid"] = "corr-42"; // mapping-configured extension
        message.Properties["cloudEvents:tenant"] = "acme";           // custom extension (canonical prefix)
        message.Properties["ce-region"] = "eu-west";                 // custom extension (alternate prefix)

        var ctx = Consume(message);
        var ce = ctx.GetCloudEvent();

        Assert.IsNotNull(ce);
        Assert.AreEqual("acme", ce.Extensions["tenant"]);
        Assert.AreEqual("eu-west", ce.Extensions["region"]);
        // The mapping-configured correlationid extension is still read back both ways.
        Assert.AreEqual("corr-42", ce.Extensions["correlationid"]);
        Assert.AreEqual("corr-42", ctx.CorrelationId);
        // Core context attributes are never mis-classified as extensions.
        Assert.IsFalse(ce.Extensions.ContainsKey("id"));
        Assert.IsFalse(ce.Extensions.ContainsKey("type"));
        Assert.IsFalse(ce.Extensions.ContainsKey("specversion"));
    }

    [TestMethod]
    public void StructuredCloudEvent_ParsesEnvelope()
    {
        const string envelope =
            "{\"specversion\":\"1.0\",\"id\":\"ext-2\",\"source\":\"urn:ext:crm\"," +
            "\"type\":\"OrderPlaced\",\"datacontenttype\":\"application/json\",\"data\":{\"orderId\":\"O-2\"}}";
        var message = new FakeCloudEventMessage
        {
            ContentType = "application/cloudevents+json",
            Body = Encoding.UTF8.GetBytes(envelope),
        };

        var ctx = Consume(message);

        // AC8: structured envelope → dispatch key + payload.
        Assert.AreEqual("OrderPlaced", ctx.MessageContent.EventContent!.EventTypeId);
        var payload = JsonConvert.DeserializeObject<OrderPlacedPayload>(ctx.MessageContent.EventContent.EventJson!);
        Assert.AreEqual("O-2", payload!.OrderId);
        Assert.AreEqual("ext-2", ctx.GetCloudEvent()!.Id);
    }

    [TestMethod]
    public void MappingOverride_SessionIdFromSubject_IsHonoredOnConsume()
    {
        var options = new CloudEventReadOptions();
        options.Mapping.SessionIdAttribute = CloudEventMapping.SubjectAttribute;

        var message = new FakeCloudEventMessage
        {
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("{}"),
        };
        message.Properties["cloudEvents:specversion"] = "1.0";
        message.Properties["cloudEvents:id"] = "ext-3";
        message.Properties["cloudEvents:source"] = "urn:ext:crm";
        message.Properties["cloudEvents:type"] = "OrderPlaced";
        message.Properties["cloudEvents:subject"] = "session-from-subject";

        var ctx = Consume(message, options);

        // AC10: session id is read back from the configured attribute (subject).
        Assert.AreEqual("session-from-subject", ctx.SessionId);
    }

    [TestMethod]
    public void NativeMessage_WithReadOptions_IsNotTreatedAsCloudEvent()
    {
        // AC11: a native NimBus message yields a null CloudEvent even when the
        // subscriber has CloudEvents enabled (AutoDetect per message).
        var content = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{\"orderId\":\"native\"}" },
        };
        var message = new FakeCloudEventMessage
        {
            MessageId = "native-1",
            SessionId = "s-native",
            Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(content)),
        };
        message.Properties[UserPropertyName.From.ToString()] = "StorefrontEndpoint";
        message.Properties[UserPropertyName.MessageType.ToString()] = MessageType.EventRequest.ToString();

        var ctx = Consume(message);

        Assert.IsNull(ctx.GetCloudEvent());
        Assert.AreEqual("OrderPlaced", ctx.MessageContent.EventContent!.EventTypeId);
        Assert.AreEqual("{\"orderId\":\"native\"}", ctx.MessageContent.EventContent.EventJson);
    }

    private sealed class OrderPlacedPayload
    {
        public string OrderId { get; set; } = null!;
    }

}
