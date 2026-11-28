#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// <see cref="SenderBase"/>'s <see cref="IBatchingSender"/> implementation on top of
/// <c>ServiceBusMessageBatch</c>.
/// </summary>
[TestClass]
public class SenderBatchTests
{
    [TestMethod]
    public async Task A_batched_message_is_the_message_a_single_send_would_put_on_the_wire()
    {
        var sbSender = new RecordingServiceBusSender();
        IBatchingSender sut = new Sender(sbSender);
        var first = CreateMessage("A", "m-1", to: "Billing");
        var second = CreateMessage("A", "m-2", to: "Billing");
        second.RoutingKey = "eu";

        using (var batch = await sut.CreateBatchAsync())
        {
            Assert.IsTrue(batch.TryAdd(first, messageEnqueueDelay: 0));
            Assert.IsTrue(batch.TryAdd(second, messageEnqueueDelay: 5));
            Assert.AreEqual(2, batch.Count);
            await batch.SendAsync();
        }

        Assert.AreEqual(1, sbSender.SentBatches.Count);
        var sent = sbSender.SentBatches[0];
        AssertSameWireMessage(MessageHelper.ToServiceBusMessage(first, 0), sent[0]);
        AssertSameWireMessage(MessageHelper.ToServiceBusMessage(second, 5), sent[1]);
        Assert.IsTrue(sent[1].ScheduledEnqueueTime > DateTimeOffset.UtcNow.AddMinutes(4), "The second message carries its own 5-minute delay.");
        Assert.AreEqual(0, sbSender.SentMessages.Count, "The batch goes out in one SendMessagesAsync(batch) call.");
    }

    [TestMethod]
    public async Task An_add_that_does_not_fit_reports_false_instead_of_throwing()
    {
        var sbSender = new RecordingServiceBusSender { BatchCapacity = 1 };
        IBatchingSender sut = new Sender(sbSender);

        using var batch = await sut.CreateBatchAsync();

        Assert.IsTrue(batch.TryAdd(CreateMessage("A", "m-1"), 0));
        Assert.IsFalse(batch.TryAdd(CreateMessage("A", "m-2"), 0));
        Assert.AreEqual(1, batch.Count);
    }

    [TestMethod]
    public async Task Adding_a_message_of_another_session_throws_and_leaves_the_batch_unchanged()
    {
        IBatchingSender sut = new Sender(new RecordingServiceBusSender());

        using var batch = await sut.CreateBatchAsync();
        Assert.IsTrue(batch.TryAdd(CreateMessage("A", "m-1"), 0));

        Assert.ThrowsExactly<ArgumentException>(() => batch.TryAdd(CreateMessage("B", "m-2"), 0));
        Assert.AreEqual(1, batch.Count);
    }

    [TestMethod]
    public async Task A_null_session_and_an_empty_session_are_the_same_routing_key()
    {
        IBatchingSender sut = new Sender(new RecordingServiceBusSender());

        using var batch = await sut.CreateBatchAsync();

        Assert.IsTrue(batch.TryAdd(CreateMessage(null, "m-1"), 0));
        Assert.IsTrue(batch.TryAdd(CreateMessage("", "m-2"), 0));
        Assert.ThrowsExactly<ArgumentException>(() => batch.TryAdd(CreateMessage("A", "m-3"), 0));
        Assert.AreEqual(2, batch.Count);
    }

    [TestMethod]
    public async Task The_dispatcher_sends_a_row_rejected_by_the_routing_key_guard_on_its_own_and_carries_on()
    {
        var sbSender = new RecordingServiceBusSender();
        // Hands A:001 to the real batch with a foreign session, so SenderBase's guard throws.
        var sender = new GuardTrippingSender(new Sender(sbSender), "A:001");
        var rows = Enumerable.Range(0, 3).Select(position => CreateRow("A", position)).ToList();
        var outbox = new ClaimingListOutbox(rows);

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.IsTrue(sender.GuardTripped, "The real routing-key guard rejected the add.");
        Assert.AreEqual(3, dispatched);
        CollectionAssert.AreEqual(
            new[] { "A:000", "A:002" },
            sbSender.SentBatches.Select(batch => string.Join(",", batch.Select(message => message.MessageId))).ToArray());
        CollectionAssert.AreEqual(new[] { "A:001" }, sbSender.SentMessages.Select(message => message.MessageId).ToArray(), "The rejected row is sent on its own.");
        Assert.AreEqual(0, outbox.Failures.Count, "The successful single send records no attempt.");
        CollectionAssert.AreEquivalent(rows.Select(row => row.Id).ToArray(), outbox.Checkpointed.ToArray());
    }

    private static void AssertSameWireMessage(Azure.Messaging.ServiceBus.ServiceBusMessage expected, Azure.Messaging.ServiceBus.ServiceBusMessage actual)
    {
        Assert.AreEqual(expected.MessageId, actual.MessageId);
        Assert.AreEqual(expected.SessionId, actual.SessionId);
        Assert.AreEqual(expected.PartitionKey, actual.PartitionKey);
        Assert.AreEqual(expected.CorrelationId, actual.CorrelationId);
        Assert.AreEqual(expected.ContentType, actual.ContentType);
        Assert.AreEqual(expected.Subject, actual.Subject);
        CollectionAssert.AreEqual(expected.Body.ToArray(), actual.Body.ToArray());
        // The SDK may stamp diagnostic properties on any message it sends; compare NimBus's own.
        var ownKeys = expected.ApplicationProperties.Keys.ToArray();
        CollectionAssert.IsSubsetOf(ownKeys, actual.ApplicationProperties.Keys.ToArray());
        foreach (var key in ownKeys)
            Assert.AreEqual(expected.ApplicationProperties[key], actual.ApplicationProperties[key], $"Application property {key}");
        Assert.IsTrue((expected.ScheduledEnqueueTime - actual.ScheduledEnqueueTime).Duration() < TimeSpan.FromSeconds(5), "Scheduled enqueue time");
    }

    private static Message CreateMessage(string? sessionId, string messageId, string to = "Billing") => new()
    {
        To = to,
        SessionId = sessionId!,
        MessageId = messageId,
        CorrelationId = "correlation-1",
        EventId = "event-1",
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        },
    };

    private static OutboxMessage CreateRow(string sessionId, int position)
    {
        var messageId = $"{sessionId}:{position:D3}";
        return new OutboxMessage
        {
            Id = $"outbox-{messageId}",
            MessageId = messageId,
            To = "Billing",
            SessionId = sessionId,
            Payload = JsonConvert.SerializeObject(CreateMessage(sessionId, messageId)),
            CreatedAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(position),
        };
    }

    /// <summary>
    /// Delegates to a real <see cref="SenderBase"/>, but hands one message to its batch under a
    /// different session so that <see cref="SenderBase"/>'s routing-key guard rejects it.
    /// </summary>
    private sealed class GuardTrippingSender(SenderBase inner, string messageIdToReroute) : ISender, IBatchingSender
    {
        public string MessageIdToReroute { get; } = messageIdToReroute;

        public bool GuardTripped { get; private set; }

        public async Task<ISenderBatch> CreateBatchAsync(CancellationToken cancellationToken = default) =>
            new Batch(this, await ((IBatchingSender)inner).CreateBatchAsync(cancellationToken));

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            inner.Send(message, messageEnqueueDelay, cancellationToken);

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            inner.Send(messages, messageEnqueueDelay, cancellationToken);

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            inner.ScheduleMessage(message, scheduledEnqueueTime, cancellationToken);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            inner.CancelScheduledMessage(sequenceNumber, cancellationToken);

        private sealed class Batch(GuardTrippingSender owner, ISenderBatch batch) : ISenderBatch
        {
            public int Count => batch.Count;

            public bool TryAdd(IMessage message, int messageEnqueueDelay)
            {
                if (message.MessageId != owner.MessageIdToReroute)
                    return batch.TryAdd(message, messageEnqueueDelay);

                var rerouted = JsonConvert.DeserializeObject<Message>(JsonConvert.SerializeObject(message))!;
                rerouted.SessionId = "another-session";
                try
                {
                    return batch.TryAdd(rerouted, messageEnqueueDelay);
                }
                catch (ArgumentException)
                {
                    owner.GuardTripped = true;
                    throw;
                }
            }

            public Task SendAsync(CancellationToken cancellationToken = default) => batch.SendAsync(cancellationToken);

            public void Dispose() => batch.Dispose();
        }
    }

    /// <summary>A minimal claiming outbox: claims every pending row and records the dispatcher's calls.</summary>
    private sealed class ClaimingListOutbox(List<OutboxMessage> rows) : IOutbox, IOutboxClaiming
    {
        public List<string> Checkpointed { get; } = new();
        public List<string> Failures { get; } = new();

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(rows.Where(row => !Checkpointed.Contains(row.Id)).Take(batchSize).ToList());

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(int batchSize, string claimantId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            GetPendingAsync(batchSize, cancellationToken);

        public Task<IReadOnlyCollection<string>> MarkClaimedAsDispatchedAsync(string claimantId, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            var list = ids.ToList();
            Checkpointed.AddRange(list);
            return Task.FromResult<IReadOnlyCollection<string>>(list);
        }

        public Task RecordDispatchFailureAsync(string id, string claimantId, string error, TimeSpan retryDelay, bool park, CancellationToken cancellationToken = default)
        {
            Failures.Add(id);
            return Task.CompletedTask;
        }

        public Task ReleaseClaimsAsync(string claimantId, IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
