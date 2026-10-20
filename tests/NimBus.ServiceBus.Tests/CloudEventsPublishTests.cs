#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CloudEvents;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Publish-side CloudEvents tests: they drive a real <see cref="PublisherClient"/>
/// through <see cref="Sender"/> (which converts via <see cref="MessageHelper"/>) and
/// assert the emitted Azure Service Bus message. Covers AC4, AC5, AC6, AC10 (publish
/// side) and AC14 (native wire unchanged).
/// </summary>
[TestClass]
public class CloudEventsPublishTests
{
    private static (RecordingServiceBusSender recorder, PublisherClient publisher) BuildPublisher(CloudEventPublisherOptions options)
    {
        var recorder = new RecordingServiceBusSender();
        var sender = new Sender(recorder);
        var publisher = new PublisherClient(sender, "BillingEndpoint", options);
        return (recorder, publisher);
    }

    [TestMethod]
    public async Task BinaryMode_EmitsCloudEventsContextAttributesAndRawDomainBody()
    {
        var options = new CloudEventPublisherOptions
        {
            Source = new Uri("urn:test:billing"),
            ContentMode = CloudEventContentMode.Binary,
            DataSchema = new Uri("https://schemas.test/order.json"),
            Subject = _ => "order-subject",
        };
        var (recorder, publisher) = BuildPublisher(options);

        await publisher.Publish(new TestEvent { Payload = "hello" }, "sess-1", "corr-1", "msg-1");

        var sent = recorder.SentMessages.Single();
        Assert.AreEqual("1.0", sent.ApplicationProperties["cloudEvents:specversion"]);
        Assert.AreEqual("msg-1", sent.ApplicationProperties["cloudEvents:id"]);
        Assert.AreEqual("urn:test:billing", sent.ApplicationProperties["cloudEvents:source"]);
        Assert.AreEqual("TestEvent", sent.ApplicationProperties["cloudEvents:type"]);
        Assert.AreEqual("order-subject", sent.ApplicationProperties["cloudEvents:subject"]);
        Assert.AreEqual("https://schemas.test/order.json", sent.ApplicationProperties["cloudEvents:dataschema"]);

        // AC4: content-type is the data content type; body is the raw domain event, NOT the NimBus envelope.
        Assert.AreEqual("application/json", sent.ContentType);
        var body = Encoding.UTF8.GetString(sent.Body.ToArray());
        StringAssert.Contains(body, "\"Payload\":\"hello\"");
        Assert.IsFalse(body.Contains("EventContent", StringComparison.Ordinal), "Binary body must be the domain event, not the MessageContent envelope.");

        // AC4: native NimBus routing metadata is still stamped so routing/sessions/resolver keep working.
        Assert.AreEqual("TestEvent", sent.ApplicationProperties[UserPropertyName.EventTypeId.ToString()]);
        Assert.AreEqual("BillingEndpoint", sent.ApplicationProperties[UserPropertyName.OriginatingFrom.ToString()]);
        Assert.AreEqual("msg-1", sent.MessageId);
        Assert.AreEqual("sess-1", sent.SessionId);
    }

    [TestMethod]
    public async Task BinaryMode_PreservesNimBusIdentityAsMappedAttributes()
    {
        var options = new CloudEventPublisherOptions { Source = new Uri("urn:test:billing") };
        var (recorder, publisher) = BuildPublisher(options);

        await publisher.Publish(new TestEvent { Payload = "x" }, "sess-42", "corr-99", "msg-7");

        var sent = recorder.SentMessages.Single();
        // AC6: correlation id and session id ride as the default extension attributes.
        Assert.AreEqual("corr-99", sent.ApplicationProperties["cloudEvents:correlationid"]);
        Assert.AreEqual("sess-42", sent.ApplicationProperties["cloudEvents:sessionid"]);
    }

    [TestMethod]
    public async Task PublishFromContext_PreservesCloudEventsAndNativeWorkflowLineage()
    {
        var options = new CloudEventPublisherOptions { Source = new Uri("urn:test:billing") };
        var (recorder, publisher) = BuildPublisher(options);
        var context = new EventHandlerContext
        {
            MessageId = "inventory-reserved-1",
            SessionId = "order-42",
            CorrelationId = "conversation-7",
            ParentMessageId = "reserve-inventory-1",
            OriginatingMessageId = "order-placed-1",
        };

        await publisher.PublishFromContext(
            new TestEvent { Payload = "x" },
            context,
            messageId: "order-42:capture-payment:1");

        var sent = recorder.SentMessages.Single();
        Assert.AreEqual("order-42:capture-payment:1", sent.MessageId);
        Assert.AreEqual("order-42:capture-payment:1", sent.ApplicationProperties["cloudEvents:id"]);
        Assert.AreEqual("order-42", sent.SessionId);
        Assert.AreEqual("order-42", sent.ApplicationProperties["cloudEvents:sessionid"]);
        Assert.AreEqual("conversation-7", sent.CorrelationId);
        Assert.AreEqual("conversation-7", sent.ApplicationProperties["cloudEvents:correlationid"]);
        Assert.AreEqual("inventory-reserved-1", sent.ApplicationProperties[UserPropertyName.ParentMessageId.ToString()]);
        Assert.AreEqual("order-placed-1", sent.ApplicationProperties[UserPropertyName.OriginatingMessageId.ToString()]);
    }

    [TestMethod]
    public async Task StructuredMode_EmitsCloudEventsJsonEnvelope()
    {
        var options = new CloudEventPublisherOptions
        {
            Source = new Uri("urn:test:billing"),
            ContentMode = CloudEventContentMode.StructuredJson,
        };
        var (recorder, publisher) = BuildPublisher(options);

        await publisher.Publish(new TestEvent { Payload = "hello" }, "sess-1", "corr-1", "msg-1");

        var sent = recorder.SentMessages.Single();
        Assert.AreEqual("application/cloudevents+json", sent.ContentType);

        var envelope = JObject.Parse(Encoding.UTF8.GetString(sent.Body.ToArray()));
        Assert.AreEqual("1.0", (string?)envelope["specversion"]);
        Assert.AreEqual("msg-1", (string?)envelope["id"]);
        Assert.AreEqual("urn:test:billing", (string?)envelope["source"]);
        Assert.AreEqual("TestEvent", (string?)envelope["type"]);
        Assert.AreEqual("application/json", (string?)envelope["datacontenttype"]);
        // AC5: data equals the domain event (embedded JSON, not a string).
        Assert.AreEqual("hello", (string?)envelope["data"]!["Payload"]);
        // AC6: NimBus identity present as extension attributes.
        Assert.AreEqual("corr-1", (string?)envelope["correlationid"]);
        Assert.AreEqual("sess-1", (string?)envelope["sessionid"]);
    }

    [TestMethod]
    public async Task FullNameStrategy_UsesFullyQualifiedType()
    {
        var options = new CloudEventPublisherOptions
        {
            Source = new Uri("urn:test:billing"),
            TypeNameStrategy = CloudEventTypeNameStrategy.FullName,
        };
        var (recorder, publisher) = BuildPublisher(options);

        await publisher.Publish(new TestEvent { Payload = "x" }, "s", "c", "m");

        var sent = recorder.SentMessages.Single();
        Assert.AreEqual(typeof(TestEvent).FullName, sent.ApplicationProperties["cloudEvents:type"]);
    }

    [TestMethod]
    public async Task MappingOverride_SessionIdToSubject_IsHonoredOnPublish()
    {
        var options = new CloudEventPublisherOptions { Source = new Uri("urn:test:billing") };
        options.Mapping.SessionIdAttribute = CloudEventMapping.SubjectAttribute; // "subject"
        var (recorder, publisher) = BuildPublisher(options);

        await publisher.Publish(new TestEvent { Payload = "x" }, "sess-map", "corr-1", "msg-1");

        var sent = recorder.SentMessages.Single();
        // AC10: session id now lands in the CloudEvents subject core attribute...
        Assert.AreEqual("sess-map", sent.ApplicationProperties["cloudEvents:subject"]);
        // ...and not as the default sessionid extension.
        Assert.IsFalse(sent.ApplicationProperties.ContainsKey("cloudEvents:sessionid"));
    }

    [TestMethod]
    public async Task NativePublish_NoCloudEventsConfig_ProducesNimBusEnvelopeAndNoCloudEventsProps()
    {
        // AC14: with no CloudEvents config the wire is unchanged — envelope body, no cloudEvents:* props.
        var recorder = new RecordingServiceBusSender();
        var sender = new Sender(recorder);
        var publisher = new PublisherClient(sender, "BillingEndpoint"); // cloudEvents == null

        await publisher.Publish(new TestEvent { Payload = "hello" }, "sess-1", "corr-1", "msg-1");

        var sent = recorder.SentMessages.Single();
        var body = Encoding.UTF8.GetString(sent.Body.ToArray());
        StringAssert.Contains(body, "EventContent");
        Assert.IsFalse(sent.ApplicationProperties.Keys.Any(k => k.StartsWith("cloudEvents:", StringComparison.Ordinal)),
            "Native publish must not stamp any cloudEvents:* application properties.");
    }
}
