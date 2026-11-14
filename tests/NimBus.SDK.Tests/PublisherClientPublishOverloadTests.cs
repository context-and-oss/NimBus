#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Messages;

namespace NimBus.SDK.Tests;

/// <summary>
/// Pins the behaviour-preservation invariant of the <see cref="PublisherClient"/>
/// 3-arg <c>Publish(event, sessionId, correlationId)</c> overload: it must produce
/// the byte-identical wire message (deterministic <c>MessageId</c> and serialized
/// <c>MessageContent</c>) as the 4-arg overload called with <c>messageId: null</c>.
/// This guards the "serialize the event once" refactor — the 3-arg overload now
/// delegates messageId derivation to <c>GetMessageStatic</c> instead of computing
/// (and serializing) it itself.
/// </summary>
[TestClass]
public class PublisherClientPublishOverloadTests
{
    private sealed class FixedEvent : Event
    {
        public string Name { get; set; } = null!;

        public int Count { get; set; }

        // Fixed session id keeps the whole message deterministic; the base
        // implementation would otherwise return a fresh GUID per call.
        public override string GetSessionId() => "fixed-session";
    }

    private sealed class CapturingSender : ISender
    {
        public readonly List<IMessage> Sent = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static FixedEvent NewEvent() => new() { Name = "contoso", Count = 7 };

    private static async Task<IMessage> PublishThreeArg()
    {
        var sender = new CapturingSender();
        await new PublisherClient(sender).Publish(NewEvent(), "session-1", "corr-1");
        Assert.AreEqual(1, sender.Sent.Count);
        return sender.Sent[0];
    }

    private static async Task<IMessage> PublishFourArgNull()
    {
        var sender = new CapturingSender();
        await new PublisherClient(sender).Publish(NewEvent(), "session-1", "corr-1", null);
        Assert.AreEqual(1, sender.Sent.Count);
        return sender.Sent[0];
    }

    [TestMethod]
    public async Task Publish_3arg_derives_same_deterministic_messageId_as_4arg_with_null()
    {
        var threeArg = await PublishThreeArg();
        var fourArgNull = await PublishFourArgNull();

        Assert.AreEqual(fourArgNull.MessageId, threeArg.MessageId,
            "The 3-arg overload must derive the same deterministic MessageId as the 4-arg overload with messageId: null.");
        StringAssert.StartsWith(threeArg.MessageId, "FixedEvent-",
            "MessageId keeps the '{eventTypeId}-{hash}' shape.");
    }

    [TestMethod]
    public async Task Publish_3arg_and_4arg_null_produce_identical_messageId_and_serialized_content()
    {
        var threeArg = await PublishThreeArg();
        var fourArgNull = await PublishFourArgNull();

        // The real invariant: both paths must emit a byte-identical wire message.
        Assert.AreEqual(fourArgNull.MessageId, threeArg.MessageId);
        Assert.AreEqual(
            JsonConvert.SerializeObject(fourArgNull.MessageContent),
            JsonConvert.SerializeObject(threeArg.MessageContent),
            "Serialized MessageContent (the wire payload) must be identical between the two paths.");
    }
}
