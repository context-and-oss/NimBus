#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK.Tests;

/// <summary>
/// The cancellation-aware <c>Publish(event, sessionId, correlationId, messageId, token)</c>
/// overload: <see cref="PublisherClient"/> passes the token to the send, and the default
/// interface method keeps custom implementers compiling.
/// </summary>
[TestClass]
public class PublisherClientCancellationTests
{
    private sealed class SampleEvent : Event
    {
        public string Name { get; set; } = "contoso";
    }

    private sealed class CapturingSender : ISender
    {
        public List<(IMessage Message, CancellationToken Token)> Sent { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add((message, cancellationToken));
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            foreach (var message in messages)
            {
                Sent.Add((message, cancellationToken));
            }

            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    [TestMethod]
    public async Task Token_reaches_the_sender()
    {
        var sender = new CapturingSender();
        using var cts = new CancellationTokenSource();

        await new PublisherClient(sender).Publish(new SampleEvent(), "s-1", "c-1", "m-1", cts.Token);

        Assert.AreEqual(1, sender.Sent.Count);
        Assert.AreEqual(cts.Token, sender.Sent[0].Token);
        Assert.AreEqual("s-1", sender.Sent[0].Message.SessionId);
        Assert.AreEqual("c-1", sender.Sent[0].Message.CorrelationId);
        Assert.AreEqual("m-1", sender.Sent[0].Message.MessageId);
    }

    [TestMethod]
    public async Task Pre_cancelled_token_throws_without_sending()
    {
        var sender = new CapturingSender();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => new PublisherClient(sender).Publish(new SampleEvent(), "s-1", "c-1", null!, cts.Token));

        Assert.AreEqual(0, sender.Sent.Count);
    }

    [TestMethod]
    public async Task Existing_overload_is_unchanged_and_matches_token_overload()
    {
        var oldSender = new CapturingSender();
        var newSender = new CapturingSender();

        await new PublisherClient(oldSender).Publish(new SampleEvent(), "s-1", "c-1", null!);
        await new PublisherClient(newSender).Publish(new SampleEvent(), "s-1", "c-1", null!, CancellationToken.None);

        Assert.AreEqual(CancellationToken.None, oldSender.Sent[0].Token);
        Assert.AreEqual(oldSender.Sent[0].Message.MessageId, newSender.Sent[0].Message.MessageId);
        Assert.AreEqual(oldSender.Sent[0].Message.MessageContent.EventContent!.EventJson, newSender.Sent[0].Message.MessageContent.EventContent!.EventJson);
    }

    [TestMethod]
    public async Task Default_interface_method_delegates_for_a_minimal_implementer()
    {
        var client = new MinimalPublisher();
        IPublisherClient publisher = client;

        await publisher.Publish(new SampleEvent(), "s-1", "c-1", "m-1", CancellationToken.None);

        Assert.AreEqual("s-1|c-1|m-1", client.LastCall);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => publisher.Publish(new SampleEvent(), "s-2", "c-2", "m-2", cts.Token));
        Assert.AreEqual("s-1|c-1|m-1", client.LastCall, "A cancelled call must not delegate.");
    }

    private sealed class MinimalPublisher : IPublisherClient
    {
        public string? LastCall { get; private set; }

        public Task Publish(IEvent @event) => throw new NotSupportedException();

        public Task Publish(IMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(IEvent @event, string sessionId, string correlationId) => throw new NotSupportedException();

        public Task Publish(IEvent @event, string sessionId, string correlationId, string? messageId)
        {
            LastCall = $"{sessionId}|{correlationId}|{messageId}";
            return Task.CompletedTask;
        }

        public Task PublishFromContext(IEvent @event, IEventHandlerContext context, string messageId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task PublishBatch(IEnumerable<IEvent> events, string? correlationId = null) => throw new NotSupportedException();

        public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events) => throw new NotSupportedException();
    }
}
