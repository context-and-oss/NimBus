#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;

namespace NimBus.SDK.Tests;

/// <summary>
/// <see cref="PublisherClient.GetMessageId"/> must return exactly the content-derived
/// MessageId that <see cref="PublisherClient.Publish(IEvent)"/> stamps, so a caller can
/// report or link to a message before or after publishing it.
/// </summary>
[TestClass]
public class PublisherClientMessageIdTests
{
    private sealed class SampleEvent : Event
    {
        public string Name { get; set; } = "contoso";
    }

    private sealed class CapturingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

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

    [TestMethod]
    public async Task GetMessageId_matches_the_id_Publish_stamps()
    {
        var sender = new CapturingSender();
        var @event = new SampleEvent { Name = "fabrikam" };

        await new PublisherClient(sender).Publish(@event);

        Assert.AreEqual(sender.Sent[0].MessageId, PublisherClient.GetMessageId(@event));
    }

    [TestMethod]
    public void GetMessageId_is_derived_from_the_event_content()
    {
        var first = PublisherClient.GetMessageId(new SampleEvent { Name = "a" });

        Assert.AreEqual(first, PublisherClient.GetMessageId(new SampleEvent { Name = "a" }));
        Assert.AreNotEqual(first, PublisherClient.GetMessageId(new SampleEvent { Name = "b" }));
        Assert.IsTrue(first.StartsWith(nameof(SampleEvent) + "-", StringComparison.Ordinal), first);
    }

    [TestMethod]
    public void GetMessageId_rejects_null()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PublisherClient.GetMessageId(null!));
    }
}
