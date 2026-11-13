#pragma warning disable CA1707, CA1515, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.CloudEvents;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.Extensions;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Spec 045 §2: every publish path stamps the payload's routing key as the <c>RoutingKey</c>
/// application property, a keyless message carries none, and the key survives the outbox and
/// the deferred copy.
/// </summary>
[TestClass]
public class RoutingKeyPublishTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class RoutedCustomerCreated : Event
    {
        public string? Instance { get; set; }
        public string CustomerNo { get; set; } = "C-1";
    }

    public sealed class UnroutedCustomerCreated : Event
    {
        public string CustomerNo { get; set; } = "C-1";
    }

    private static readonly string Property = UserPropertyName.RoutingKey.ToString();

    private static (RecordingServiceBusSender Recorder, PublisherClient Publisher) Build(CloudEventPublisherOptions? cloudEvents = null)
    {
        var recorder = new RecordingServiceBusSender();
        return (recorder, new PublisherClient(new Sender(recorder), "CrmEndpoint", cloudEvents));
    }

    [TestMethod]
    public async Task Publish_StampsTheRoutingKey()
    {
        var (recorder, publisher) = Build();

        await publisher.Publish(new RoutedCustomerCreated { Instance = "01" });

        Assert.AreEqual("01", recorder.SentMessages.Single().ApplicationProperties[Property]);
    }

    [TestMethod]
    public async Task Publish_KeylessEvent_HasNoRoutingKeyProperty()
    {
        var (recorder, publisher) = Build();

        await publisher.Publish(new UnroutedCustomerCreated());
        await publisher.Publish(new RoutedCustomerCreated { Instance = null }, "s", "c", "m");

        Assert.IsTrue(recorder.SentMessages.All(message => !message.ApplicationProperties.ContainsKey(Property)));
    }

    [TestMethod]
    public async Task PublishBatch_And_PublishBatches_StampEveryMessage()
    {
        var (recorder, publisher) = Build();
        var events = new IEvent[]
        {
            new RoutedCustomerCreated { Instance = "01", CustomerNo = "A" },
            new RoutedCustomerCreated { Instance = "02", CustomerNo = "B" },
        };

        await publisher.PublishBatch(events);
        await publisher.PublishBatches(events);

        CollectionAssert.AreEqual(
            new object[] { "01", "02", "01", "02" },
            recorder.SentMessages.Select(message => message.ApplicationProperties[Property]).ToArray());
    }

    [TestMethod]
    public async Task Schedule_StampsTheRoutingKey()
    {
        var (recorder, publisher) = Build();

        await publisher.Schedule(new RoutedCustomerCreated { Instance = "03" }, DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.AreEqual("03", recorder.ScheduledMessages.Single().Message.ApplicationProperties[Property]);
    }

    [TestMethod]
    [DataRow(CloudEventContentMode.Binary)]
    [DataRow(CloudEventContentMode.StructuredJson)]
    public async Task CloudEventsMode_StillStampsTheNativeProperty(CloudEventContentMode mode)
    {
        var (recorder, publisher) = Build(new CloudEventPublisherOptions { Source = new Uri("urn:test:crm"), ContentMode = mode });

        await publisher.Publish(new RoutedCustomerCreated { Instance = "02" }, "s", "c", "m");

        Assert.AreEqual("02", recorder.SentMessages.Single().ApplicationProperties[Property]);
    }

    [TestMethod]
    public void RoutingKey_SurvivesTheOutboxSerialisation()
    {
        var message = new Message { RoutingKey = "01", EventTypeId = "X", To = "X", SessionId = "s", EventId = "e", MessageContent = new MessageContent() };

        var roundTripped = JsonConvert.DeserializeObject<Message>(JsonConvert.SerializeObject(message))!;

        Assert.AreEqual("01", roundTripped.RoutingKey);
    }

    [TestMethod]
    public void CreateDeferredMessage_KeepsTheRoutingKey()
    {
        var message = new Message
        {
            RoutingKey = "01",
            EventTypeId = "RoutedCustomerCreated",
            To = "ErpInstance01",
            SessionId = "s",
            EventId = "e",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent(),
        };

        var deferred = MessageHelper.CreateDeferredMessage(message, "s", 1);

        Assert.AreEqual("01", deferred.ApplicationProperties[Property]);
    }

    [TestMethod]
    public void IMessageDefault_IsNull()
    {
        IMessage message = new MinimalMessage();

        Assert.IsNull(message.RoutingKey);
        Assert.IsFalse(MessageHelper.ToServiceBusMessage(message).ApplicationProperties.ContainsKey(Property));
    }

    private sealed class MinimalMessage : IMessage
    {
        public string EventId => "e";
        public string To => "t";
        public string SessionId => "s";
        public string? CorrelationId => null;
        public string? MessageId => null;
        public MessageType MessageType => MessageType.EventRequest;
        public MessageContent MessageContent => new();
        public string? ParentMessageId => null;
        public string? OriginatingMessageId => null;
        public int? RetryCount => null;
        public string? From => null;
        public string? OriginatingFrom => null;
        public string EventTypeId => "X";
        public string? OriginalSessionId => null;
        public int? DeferralSequence => null;
    }
}
