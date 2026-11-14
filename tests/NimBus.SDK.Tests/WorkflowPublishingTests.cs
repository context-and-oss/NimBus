#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.CloudEvents;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

[TestClass]
public class WorkflowPublishingTests
{
    [TestMethod]
    public void LegacyHandlerContext_NewWorkflowMembersUseBackwardCompatibleDefaults()
    {
        IEventHandlerContext context = new LegacyHandlerContext();

        Assert.IsNull(context.SessionId);
        Assert.IsNull(context.ParentMessageId);
        Assert.IsNull(context.OriginatingMessageId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishFromContext_FirstHopWithMissingLegacyLineage_UsesInboundMessageAsOrigin(bool useCloudEvents)
    {
        var sender = new CapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents);
        var context = CreateContext(
            messageId: "order-placed-1",
            parentMessageId: Constants.Self,
            originatingMessageId: Constants.Self);

        await publisher.PublishFromContext(
            NewEvent(),
            context,
            messageId: "order-42:reserve-inventory:1");

        var sent = sender.Sent.Single();
        AssertWorkflowMetadata(
            sent,
            messageId: "order-42:reserve-inventory:1",
            parentMessageId: "order-placed-1",
            originatingMessageId: "order-placed-1");
        Assert.AreEqual(useCloudEvents, sent.CloudEvent is not null);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishFromContext_SecondHop_ReparentsAndPreservesOrigin(bool useCloudEvents)
    {
        var sender = new CapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents);
        var context = CreateContext(
            messageId: "inventory-reserved-1",
            parentMessageId: "reserve-inventory-1",
            originatingMessageId: "order-placed-1");

        await publisher.PublishFromContext(
            NewEvent(),
            context,
            messageId: "order-42:capture-payment:1");

        var sent = sender.Sent.Single();
        AssertWorkflowMetadata(
            sent,
            messageId: "order-42:capture-payment:1",
            parentMessageId: "inventory-reserved-1",
            originatingMessageId: "order-placed-1");
        Assert.AreEqual(useCloudEvents, sent.CloudEvent is not null);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishFromContext_OutboxRoundTrip_MatchesDirectMetadata(bool useCloudEvents)
    {
        var context = CreateContext(
            messageId: "inventory-reserved-1",
            parentMessageId: "reserve-inventory-1",
            originatingMessageId: "order-placed-1");

        var directSender = new CapturingSender();
        await CreatePublisher(directSender, useCloudEvents).PublishFromContext(
            NewEvent(),
            context,
            messageId: "order-42:capture-payment:1");

        var outbox = new CapturingOutbox();
        var outboxPublisher = new PublisherClient(
            new OutboxSender(outbox),
            "OrderOrchestrator",
            CreateCloudEventOptions(useCloudEvents));
        await outboxPublisher.PublishFromContext(
            NewEvent(),
            context,
            messageId: "order-42:capture-payment:1");

        var roundTripped = JsonConvert.DeserializeObject<Message>(
            outbox.Stored.Single().Payload,
            Constants.CreateSafeJsonSettings());
        Assert.IsNotNull(roundTripped);
        AssertEquivalentMetadata(directSender.Sent.Single(), roundTripped);
    }

    [TestMethod]
    public async Task PublishFromContext_PropagatesCancellationToken()
    {
        var sender = new CapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        using var cancellation = new CancellationTokenSource();

        await publisher.PublishFromContext(
            NewEvent(),
            CreateContext("order-placed-1", Constants.Self, Constants.Self),
            messageId: "order-42:reserve-inventory:1",
            cancellationToken: cancellation.Token);

        Assert.AreEqual(cancellation.Token, sender.LastCancellationToken);
    }

    [TestMethod]
    public async Task PublishFromContext_MissingExplicitMessageId_Throws()
    {
        var sender = new CapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => publisher.PublishFromContext(
            NewEvent(),
            CreateContext("order-placed-1", Constants.Self, Constants.Self),
            messageId: " "));

        Assert.AreEqual(0, sender.Sent.Count);
    }

    [TestMethod]
    public async Task PublishFromContext_MissingInboundIdentity_ThrowsInsteadOfGeneratingReplacement()
    {
        var sender = new CapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);
        var contexts = new[]
        {
            CreateContext(string.Empty, Constants.Self, Constants.Self),
            CreateContext("order-placed-1", Constants.Self, Constants.Self),
            CreateContext("order-placed-1", Constants.Self, Constants.Self),
        };
        contexts[1].SessionId = string.Empty;
        contexts[2].CorrelationId = string.Empty;

        foreach (var context in contexts)
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => publisher.PublishFromContext(
                NewEvent(),
                context,
                messageId: "order-42:reserve-inventory:1"));
        }

        Assert.AreEqual(0, sender.Sent.Count);
    }

    [TestMethod]
    public async Task PublishFromContext_InvalidEvent_PreservesExistingValidation()
    {
        var sender = new CapturingSender();
        IPublisherClient publisher = CreatePublisher(sender, useCloudEvents: false);

        await Assert.ThrowsExactlyAsync<ValidationException>(() => publisher.PublishFromContext(
            new WorkflowEvent(),
            CreateContext("order-placed-1", Constants.Self, Constants.Self),
            messageId: "order-42:reserve-inventory:1"));

        Assert.AreEqual(0, sender.Sent.Count);
    }

    private static PublisherClient CreatePublisher(CapturingSender sender, bool useCloudEvents) =>
        new(sender, "OrderOrchestrator", CreateCloudEventOptions(useCloudEvents));

    private static CloudEventPublisherOptions CreateCloudEventOptions(bool useCloudEvents) =>
        useCloudEvents
            ? new CloudEventPublisherOptions
            {
                Source = new Uri("urn:test:order-orchestrator"),
                Time = _ => new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
            : null!;

    private static EventHandlerContext CreateContext(
        string messageId,
        string parentMessageId,
        string originatingMessageId) =>
        new()
        {
            MessageId = messageId,
            SessionId = "order-42",
            CorrelationId = "conversation-7",
            ParentMessageId = parentMessageId,
            OriginatingMessageId = originatingMessageId,
        };

    private static WorkflowEvent NewEvent() => new() { OrderId = "order-42" };

    private static void AssertWorkflowMetadata(
        IMessage message,
        string messageId,
        string parentMessageId,
        string originatingMessageId)
    {
        Assert.AreEqual(messageId, message.MessageId);
        Assert.AreEqual("order-42", message.SessionId);
        Assert.AreEqual("conversation-7", message.CorrelationId);
        Assert.AreEqual(parentMessageId, message.ParentMessageId);
        Assert.AreEqual(originatingMessageId, message.OriginatingMessageId);
        Assert.AreEqual("OrderOrchestrator", message.OriginatingFrom);
    }

    private static void AssertEquivalentMetadata(IMessage expected, IMessage actual)
    {
        Assert.AreEqual(expected.MessageId, actual.MessageId);
        Assert.AreEqual(expected.SessionId, actual.SessionId);
        Assert.AreEqual(expected.CorrelationId, actual.CorrelationId);
        Assert.AreEqual(expected.ParentMessageId, actual.ParentMessageId);
        Assert.AreEqual(expected.OriginatingMessageId, actual.OriginatingMessageId);
        Assert.AreEqual(expected.OriginatingFrom, actual.OriginatingFrom);
        Assert.AreEqual(
            JsonConvert.SerializeObject(expected.MessageContent),
            JsonConvert.SerializeObject(actual.MessageContent));
        Assert.AreEqual(
            JsonConvert.SerializeObject(expected.CloudEvent),
            JsonConvert.SerializeObject(actual.CloudEvent));
    }

    private sealed class WorkflowEvent : Event
    {
        [Required]
        public string OrderId { get; set; } = string.Empty;

        public override string GetSessionId() => OrderId;
    }

    private sealed class LegacyHandlerContext : IEventHandlerContext
    {
        public string MessageId => "legacy-message";

        public string EventId => "legacy-event";

        public string EventType => "LegacyEvent";

        public string CorrelationId => "legacy-correlation";

        public HandlerOutcome Outcome => HandlerOutcome.Default;

        public HandoffMetadata? HandoffMetadata => null;

        public void MarkPendingHandoff(string reason, string? externalJobId = null, TimeSpan? expectedBy = null)
        {
        }
    }

    private sealed class CapturingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

        public CancellationToken LastCancellationToken { get; private set; }

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            LastCancellationToken = cancellationToken;
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(
            IMessage message,
            DateTimeOffset scheduledEnqueueTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class CapturingOutbox : IOutbox
    {
        public List<OutboxMessage> Stored { get; } = new();

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Stored.Add(message);
            return Task.CompletedTask;
        }

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
        {
            Stored.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
            int batchSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Stored.Take(batchSize).ToList());

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
