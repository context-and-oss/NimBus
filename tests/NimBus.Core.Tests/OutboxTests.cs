#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

[TestClass]
public class OutboxSenderTests
{
    [TestMethod]
    public void Constructor_NullOutbox_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutboxSender(null!));
    }

    [TestMethod]
    public async Task Send_SingleMessage_StoresInOutbox()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");

        await sender.Send(message);

        Assert.AreEqual(1, outbox.StoredMessages.Count);
        Assert.AreEqual("msg-1", outbox.StoredMessages[0].MessageId);
        Assert.AreEqual("session-1", outbox.StoredMessages[0].SessionId);
    }

    [TestMethod]
    public async Task Send_WithDelay_SetsEnqueueDelayMinutes()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");

        await sender.Send(message, messageEnqueueDelay: 5);

        Assert.AreEqual(5, outbox.StoredMessages[0].EnqueueDelayMinutes);
    }

    [TestMethod]
    public async Task Send_BatchMessages_StoresAllInOutbox()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var messages = new[]
        {
            CreateMessage("msg-1", "OrderPlaced", "session-1"),
            CreateMessage("msg-2", "OrderPlaced", "session-1"),
            CreateMessage("msg-3", "PaymentCaptured", "session-2"),
        };

        await sender.Send(messages);

        Assert.AreEqual(3, outbox.BatchStoredMessages.Count);
    }

    [TestMethod]
    public async Task Send_SerializesPayloadAsJson()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");

        await sender.Send(message);

        var stored = outbox.StoredMessages[0];
        Assert.IsFalse(string.IsNullOrEmpty(stored.Payload));
        var deserialized = JsonConvert.DeserializeObject<Message>(stored.Payload);
        Assert.AreEqual("msg-1", deserialized!.MessageId);
    }

    [TestMethod]
    public async Task ScheduleMessage_SetsScheduledEnqueueTimeUtc()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-1", "OrderPlaced", "session-1");
        var scheduledTime = DateTimeOffset.UtcNow.AddHours(1);

        var seq = await sender.ScheduleMessage(message, scheduledTime);

        Assert.AreEqual(0L, seq, "Outbox always returns 0 for sequence number");
        Assert.IsNotNull(outbox.StoredMessages[0].ScheduledEnqueueTimeUtc);
        var diff = Math.Abs((scheduledTime.UtcDateTime - outbox.StoredMessages[0].ScheduledEnqueueTimeUtc!.Value).TotalSeconds);
        Assert.IsTrue(diff < 1, "ScheduledEnqueueTimeUtc should match within 1 second");
    }

    [TestMethod]
    public async Task CancelScheduledMessage_ThrowsNotSupported()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);

        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => sender.CancelScheduledMessage(42));
    }

    [TestMethod]
    public async Task Send_SetsCreatedAtUtc()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var before = DateTime.UtcNow;

        await sender.Send(CreateMessage("msg-1", "OrderPlaced", "s1"));

        var after = DateTime.UtcNow;
        var created = outbox.StoredMessages[0].CreatedAtUtc;
        Assert.IsTrue(created >= before && created <= after);
    }

    [TestMethod]
    public async Task Send_DispatchedAtUtcIsNull()
    {
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);

        await sender.Send(CreateMessage("msg-1", "OrderPlaced", "s1"));

        Assert.IsNull(outbox.StoredMessages[0].DispatchedAtUtc);
    }

    [TestMethod]
    public async Task Send_CloudEventPublishContext_SurvivesOutboxPayloadRoundTrip()
    {
        // The whole outbox-published-CloudEvents story rests on Message.CloudEvent
        // NOT being [JsonIgnore]: OutboxSender serializes the entire Message into
        // the Payload column and OutboxDispatcher deserializes it with
        // Constants.SafeJsonSettings before re-sending. If the envelope were lost,
        // an outboxed CloudEvents message would silently degrade to native format.
        var outbox = new InMemoryOutbox();
        var sender = new OutboxSender(outbox);
        var message = CreateMessage("msg-ce", "OrderPlaced", "session-ce");
        var cloudEvent = new NimBus.Core.CloudEvents.CloudEvent
        {
            Id = "msg-ce",
            Source = "urn:test:orders",
            Type = "OrderPlaced",
            Subject = "orders/42",
            Time = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero),
            DataContentType = "application/json",
            Data = "{\"orderId\":42}",
        };
        cloudEvent.Extensions["correlationid"] = "corr-1";
        cloudEvent.Extensions["sessionid"] = "session-ce";
        message.CloudEvent = new NimBus.Core.CloudEvents.CloudEventPublishContext(
            cloudEvent,
            NimBus.Core.CloudEvents.CloudEventContentMode.Binary);

        await sender.Send(message);

        // Mirror OutboxDispatcher.DispatchAsync exactly.
        var roundTripped = JsonConvert.DeserializeObject<Message>(
            outbox.StoredMessages[0].Payload, Constants.SafeJsonSettings);

        Assert.IsNotNull(roundTripped);
        Assert.IsNotNull(roundTripped.CloudEvent, "CloudEventPublishContext must survive the outbox Payload round-trip");
        Assert.AreEqual(NimBus.Core.CloudEvents.CloudEventContentMode.Binary, roundTripped.CloudEvent.ContentMode);
        var ce = roundTripped.CloudEvent.CloudEvent;
        Assert.AreEqual("msg-ce", ce.Id);
        Assert.AreEqual("urn:test:orders", ce.Source);
        Assert.AreEqual("OrderPlaced", ce.Type);
        Assert.AreEqual("orders/42", ce.Subject);
        Assert.AreEqual("application/json", ce.DataContentType);
        Assert.AreEqual("{\"orderId\":42}", ce.Data);
        Assert.AreEqual("corr-1", ce.Extensions["correlationid"]);
        Assert.AreEqual("session-ce", ce.Extensions["sessionid"]);
    }

    private static Message CreateMessage(string messageId, string eventTypeId, string sessionId) => new()
    {
        MessageId = messageId,
        EventTypeId = eventTypeId,
        SessionId = sessionId,
        CorrelationId = "corr-1",
        To = "TestEndpoint",
        EventId = "evt-1",
        MessageType = MessageType.EventRequest,
        OriginatingMessageId = "self",
        ParentMessageId = "self",
        From = "publisher",
        OriginatingFrom = "publisher",
        OriginalSessionId = sessionId,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = eventTypeId, EventJson = "{}" }
        }
    };
}

[TestClass]
public class OutboxDispatcherTests
{
    [TestMethod]
    public void Constructor_NullOutbox_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutboxDispatcher(null!, new RecordingSender()));
    }

    [TestMethod]
    public void Constructor_NullSender_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutboxDispatcher(new InMemoryOutbox(), null!));
    }

    [TestMethod]
    public async Task DispatchPending_NoPending_ReturnsZero()
    {
        var outbox = new InMemoryOutbox();
        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, count);
        Assert.AreEqual(0, sender.SentMessages.Count);
    }

    [TestMethod]
    public async Task DispatchPending_SendsMessagesAndMarksDispatched()
    {
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-1",
            MessageId = "msg-1",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-1",
                To = "Test",
                SessionId = "s1",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            EnqueueDelayMinutes = 0,
            CreatedAtUtc = DateTime.UtcNow
        });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, sender.SentMessages.Count);
        Assert.IsTrue(outbox.DispatchedIds.Contains("out-1"));
    }

    [TestMethod]
    public async Task DispatchPending_ScheduledMessage_UsesScheduleMessage()
    {
        var scheduledTime = DateTime.UtcNow.AddHours(1);
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-1",
            MessageId = "msg-1",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-1",
                To = "Test",
                SessionId = "s1",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            ScheduledEnqueueTimeUtc = scheduledTime,
            EnqueueDelayMinutes = 0,
            CreatedAtUtc = DateTime.UtcNow
        });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, count);
        Assert.AreEqual(1, sender.ScheduledMessages.Count, "Should use ScheduleMessage for scheduled outbox entries");
    }

    [TestMethod]
    public async Task DispatchPending_FirstFailure_StopsDispatching()
    {
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage { Id = "out-1", MessageId = "msg-1", Payload = "{invalid json", CreatedAtUtc = DateTime.UtcNow });
        outbox.AddPending(new OutboxMessage { Id = "out-2", MessageId = "msg-2", Payload = "{invalid json", CreatedAtUtc = DateTime.UtcNow });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(0, count, "Should stop on first failure");
        Assert.AreEqual(0, outbox.DispatchedIds.Count);
    }

    [TestMethod]
    public async Task DispatchPending_PoisonSession_DoesNotBlockOtherSessions()
    {
        var outbox = new InMemoryOutbox();
        // Session "A" leads with a poison row (payload fails to deserialize),
        // followed by a healthy row on the SAME session that must stay parked
        // behind it to preserve per-session FIFO (ADR-001).
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-a-poison",
            MessageId = "msg-a-poison",
            SessionId = "A",
            Payload = "{invalid json",
            CreatedAtUtc = DateTime.UtcNow
        });
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-a-good",
            MessageId = "msg-a-good",
            SessionId = "A",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-a-good",
                To = "Test",
                SessionId = "A",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            CreatedAtUtc = DateTime.UtcNow
        });
        // Session "B" is healthy and must dispatch despite session "A" being stuck.
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-b-good",
            MessageId = "msg-b-good",
            SessionId = "B",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-b-good",
                To = "Test",
                SessionId = "B",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            CreatedAtUtc = DateTime.UtcNow
        });

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync();

        Assert.AreEqual(1, count, "Only session B's healthy row should dispatch");
        CollectionAssert.Contains(outbox.DispatchedIds, "out-b-good", "Session B must not be blocked by session A's poison row");
        CollectionAssert.DoesNotContain(outbox.DispatchedIds, "out-a-poison");
        CollectionAssert.DoesNotContain(outbox.DispatchedIds, "out-a-good", "Session A's later row must stay parked behind its poison row");
        Assert.AreEqual(1, sender.SentMessages.Count, "Only session B should reach the sender");
    }

    [TestMethod]
    public async Task DispatchPending_RespectsBatchSize()
    {
        var outbox = new InMemoryOutbox();
        for (int i = 0; i < 10; i++)
        {
            outbox.AddPending(new OutboxMessage
            {
                Id = $"out-{i}",
                MessageId = $"msg-{i}",
                Payload = JsonConvert.SerializeObject(new Message
                {
                    MessageId = $"msg-{i}",
                    To = "Test",
                    SessionId = "s1",
                    MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
                }),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        var sender = new RecordingSender();
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var count = await dispatcher.DispatchPendingAsync(batchSize: 3);

        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public async Task DispatchPending_CallerCancellation_PropagatesWithoutMarkingFailureOrDispatched()
    {
        var outbox = new InMemoryOutbox();
        outbox.AddPending(new OutboxMessage
        {
            Id = "out-1",
            MessageId = "msg-1",
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = "msg-1",
                To = "Test",
                SessionId = "s1",
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
            }),
            CreatedAtUtc = DateTime.UtcNow
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sender = new CancellationAwareSender();
        var logger = new OutboxRecordingLogger();
        var dispatcher = new OutboxDispatcher(outbox, sender, logger);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => dispatcher.DispatchPendingAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(1, sender.SendCalls);
        Assert.AreEqual(0, outbox.DispatchedIds.Count);
        Assert.AreEqual(0, logger.ErrorCalls, "Cooperative cancellation must not be recorded as an outbox failure");
    }

    [TestMethod]
    public async Task DispatchPending_CancellationAfterSuccessfulSend_MarksPriorSuccessBeforePropagating()
    {
        var outbox = new InMemoryOutbox();
        outbox.AddPending(CreatePendingMessage("out-1", "msg-1", "s1"));
        outbox.AddPending(CreatePendingMessage("out-2", "msg-2", "s2"));
        using var cancellation = new CancellationTokenSource();
        var sender = new CancelOnSecondSendSender(cancellation);
        var logger = new OutboxRecordingLogger();
        var dispatcher = new OutboxDispatcher(outbox, sender, logger);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => dispatcher.DispatchPendingAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(2, sender.SendCalls);
        Assert.AreEqual(1, outbox.DispatchedIds.Count);
        Assert.AreEqual("out-1", outbox.DispatchedIds[0]);
        Assert.AreEqual(0, logger.ErrorCalls, "Cooperative cancellation must not be recorded as an outbox failure");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DispatchPending_CancellationDuringCheckpoint_RetriesIdempotently(bool commitsBeforeCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        var outbox = new CancelDuringCheckpointOutbox(cancellation, commitsBeforeCancellation);
        outbox.AddPending(CreatePendingMessage("out-1", "msg-1", "s1"));
        outbox.AddPending(CreatePendingMessage("out-2", "msg-2", "s2"));
        var dispatcher = new OutboxDispatcher(outbox, new RecordingSender());

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => dispatcher.DispatchPendingAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(2, outbox.MarkCalls, "The cancelled checkpoint must be completed with a non-cancelled idempotent retry");
        string[] expectedDispatchedIds = ["out-1", "out-2"];
        CollectionAssert.AreEqual(expectedDispatchedIds, outbox.DispatchedIds);
    }

    [TestMethod]
    public async Task DispatchPending_CompensatingCheckpointFailure_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var outbox = new FailingCheckpointOutbox();
        outbox.AddPending(CreatePendingMessage("out-1", "msg-1", "s1"));
        outbox.AddPending(CreatePendingMessage("out-2", "msg-2", "s2"));
        var logger = new OutboxRecordingLogger();
        var dispatcher = new OutboxDispatcher(outbox, new CancelOnSecondSendSender(cancellation), logger);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => dispatcher.DispatchPendingAsync(cancellationToken: cancellation.Token));

        Assert.AreEqual(1, outbox.MarkCalls);
        Assert.AreEqual(1, logger.WarningCalls);
        Assert.IsInstanceOfType<InvalidOperationException>(logger.LastWarningException);
        Assert.AreEqual("Checkpoint unavailable", logger.LastWarningException!.Message);
    }

    [TestMethod]
    public async Task DispatchPending_HangingCompensatingCheckpoint_TimesOutAndPreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var outbox = new HangingCheckpointOutbox();
        outbox.AddPending(CreatePendingMessage("out-1", "msg-1", "s1"));
        outbox.AddPending(CreatePendingMessage("out-2", "msg-2", "s2"));
        var logger = new OutboxRecordingLogger();
        var dispatcher = new OutboxDispatcher(
            outbox,
            new CancelOnSecondSendSender(cancellation),
            TimeSpan.FromMilliseconds(50),
            logger);

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => dispatcher.DispatchPendingAsync(cancellationToken: cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.AreEqual(1, outbox.MarkCalls);
        Assert.AreEqual(1, logger.WarningCalls);
        Assert.IsInstanceOfType<TimeoutException>(logger.LastWarningException);
    }

    private static OutboxMessage CreatePendingMessage(string outboxId, string messageId, string sessionId) => new()
    {
        Id = outboxId,
        MessageId = messageId,
        SessionId = sessionId,
        Payload = JsonConvert.SerializeObject(new Message
        {
            MessageId = messageId,
            To = "Test",
            SessionId = sessionId,
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } }
        }),
        CreatedAtUtc = DateTime.UtcNow
    };
}

// ── Test doubles ─────────────────────────────────────────────────────

file sealed class InMemoryOutbox : IOutbox
{
    public List<OutboxMessage> StoredMessages { get; } = new();
    public List<OutboxMessage> BatchStoredMessages { get; } = new();
    public List<string> DispatchedIds { get; } = new();

    private readonly List<OutboxMessage> _pending = new();

    public void AddPending(OutboxMessage msg) => _pending.Add(msg);

    public Task StoreAsync(OutboxMessage message, CancellationToken ct = default)
    {
        StoredMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default)
    {
        BatchStoredMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken ct = default)
    {
        var result = _pending.Take(batchSize).ToList();
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(result);
    }

    public Task MarkAsDispatchedAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!DispatchedIds.Contains(id, StringComparer.Ordinal))
        {
            DispatchedIds.Add(id);
        }

        return Task.CompletedTask;
    }

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var id in ids)
        {
            if (!DispatchedIds.Contains(id, StringComparer.Ordinal))
            {
                DispatchedIds.Add(id);
            }
        }

        return Task.CompletedTask;
    }
}

file sealed class RecordingSender : ISender
{
    public List<IMessage> SentMessages { get; } = new();
    public List<(IMessage Message, DateTimeOffset ScheduledTime)> ScheduledMessages { get; } = new();
    public int LastDelay { get; private set; }

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken ct = default)
    {
        SentMessages.Add(message);
        LastDelay = messageEnqueueDelay;
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken ct = default)
    {
        SentMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default)
    {
        ScheduledMessages.Add((message, scheduledEnqueueTime));
        return Task.FromResult(42L);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class CancelDuringCheckpointOutbox : IOutbox
{
    private readonly CancellationTokenSource _cancellation;
    private readonly bool _commitsBeforeCancellation;
    private readonly List<OutboxMessage> _pending = new();

    public CancelDuringCheckpointOutbox(
        CancellationTokenSource cancellation,
        bool commitsBeforeCancellation)
    {
        _cancellation = cancellation;
        _commitsBeforeCancellation = commitsBeforeCancellation;
    }

    public int MarkCalls { get; private set; }
    public List<string> DispatchedIds { get; } = new();

    public void AddPending(OutboxMessage message) => _pending.Add(message);

    public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_pending.Take(batchSize).ToList());

    public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        MarkCalls++;
        var idList = ids.ToList();
        if (MarkCalls == 1)
        {
            if (_commitsBeforeCancellation)
            {
                DispatchedIds.Add(idList[0]);
            }

            _cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }

        foreach (var id in idList)
        {
            if (!DispatchedIds.Contains(id, StringComparer.Ordinal))
            {
                DispatchedIds.Add(id);
            }
        }

        return Task.CompletedTask;
    }
}

file sealed class FailingCheckpointOutbox : IOutbox
{
    private readonly List<OutboxMessage> _pending = new();

    public int MarkCalls { get; private set; }

    public void AddPending(OutboxMessage message) => _pending.Add(message);

    public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_pending.Take(batchSize).ToList());

    public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        MarkCalls++;
        throw new InvalidOperationException("Checkpoint unavailable");
    }
}

file sealed class HangingCheckpointOutbox : IOutbox
{
    private readonly List<OutboxMessage> _pending = new();
    private readonly TaskCompletionSource _checkpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int MarkCalls { get; private set; }

    public void AddPending(OutboxMessage message) => _pending.Add(message);

    public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_pending.Take(batchSize).ToList());

    public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        MarkCalls++;
        return _checkpoint.Task;
    }
}

file sealed class CancellationAwareSender : ISender
{
    public int SendCalls { get; private set; }

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken ct = default)
    {
        SendCalls++;
        return Task.FromCanceled(ct);
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken ct = default) =>
        Task.FromCanceled(ct);

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default) =>
        Task.FromCanceled<long>(ct);

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class CancelOnSecondSendSender : ISender
{
    private readonly CancellationTokenSource _cancellation;

    public CancelOnSecondSendSender(CancellationTokenSource cancellation)
    {
        _cancellation = cancellation;
    }

    public int SendCalls { get; private set; }

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken ct = default)
    {
        SendCalls++;
        if (SendCalls == 1)
            return Task.CompletedTask;

        _cancellation.Cancel();
        return Task.FromCanceled(ct);
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

file sealed class OutboxRecordingLogger : Microsoft.Extensions.Logging.ILogger<OutboxDispatcher>
{
    public int ErrorCalls { get; private set; }
    public int WarningCalls { get; private set; }
    public Exception? LastWarningException { get; private set; }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
            ErrorCalls++;
        if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
        {
            WarningCalls++;
            LastWarningException = exception;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
