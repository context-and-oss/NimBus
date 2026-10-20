#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.SDK.Tests;

/// <summary>
/// Serialize-once batch publishing: PublishBatches builds and serializes each
/// event exactly once, pages greedily against the Service Bus batch budget,
/// and stashes the serialized body on the message so the transport reuses it.
/// The [JsonIgnore] on Message.SerializedMessageContent is load-bearing for
/// the outbox payload shape and is pinned here too.
/// </summary>
[TestClass]
public class PublisherClientBatchTests
{
    [TestMethod]
    public void Serialized_Message_json_omits_the_SerializedMessageContent_cache()
    {
        var message = new Message
        {
            MessageId = "m-1",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "T", EventJson = "{}" },
            },
            SerializedMessageContent = "cached-body",
        };

        var json = JsonConvert.SerializeObject(message);

        Assert.IsFalse(json.Contains("SerializedMessageContent", StringComparison.Ordinal),
            "OutboxSender serializes the whole Message into the outbox Payload column — " +
            "the serialized-body cache must never be persisted (JsonIgnore is mandatory).");
        Assert.IsFalse(json.Contains("cached-body", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToServiceBusMessage_uses_the_stashed_body_and_falls_back_to_serializing()
    {
        var content = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "T", EventJson = "{\"a\":1}" },
        };
        const string sentinel = "{\"sentinel\":true}";

        var withCache = new Message { MessageId = "m-1", MessageContent = content, SerializedMessageContent = sentinel };
        var withoutCache = new Message { MessageId = "m-2", MessageContent = content };

        Assert.AreEqual(sentinel, MessageHelper.ToServiceBusMessage(withCache).Body.ToString(),
            "A pre-serialized body must be sent verbatim, not re-serialized.");
        Assert.AreEqual(JsonConvert.SerializeObject(content), MessageHelper.ToServiceBusMessage(withoutCache).Body.ToString(),
            "Without the cache the transport serializes MessageContent itself.");
    }

    [TestMethod]
    public async Task PublishBatches_sends_small_events_as_one_page_with_shared_correlation()
    {
        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender, "publisher-1");
        var events = Enumerable.Range(0, 10).Select(i => new SmallEvent { Value = $"v{i}" }).ToList();

        await publisher.PublishBatches(events, "corr-1");

        Assert.AreEqual(1, sender.Pages.Count, "10 small events fit one page.");
        Assert.AreEqual(10, sender.Pages[0].Count);
        foreach (var message in sender.Pages[0])
        {
            Assert.AreEqual("corr-1", message.CorrelationId);
            Assert.IsNotNull(message.SerializedMessageContent, "The serialized body must be stashed for the transport.");
            Assert.AreEqual(JsonConvert.SerializeObject(message.MessageContent), message.SerializedMessageContent);
            Assert.AreEqual("publisher-1", message.OriginatingFrom);
        }
    }

    [TestMethod]
    public async Task PublishBatches_splits_pages_when_the_body_budget_is_exceeded()
    {
        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender);
        // ~30 KB each against the 64 KB budget: two fit, the third starts page 2.
        var events = Enumerable.Range(0, 3).Select(_ => new SmallEvent { Value = new string('x', 30_000) }).ToList();

        await publisher.PublishBatches(events);

        CollectionAssert.AreEqual(new[] { 2, 1 }, sender.Pages.Select(p => p.Count).ToArray());
    }

    [TestMethod]
    public async Task PublishBatches_sends_an_oversized_event_as_its_own_page()
    {
        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender);
        var events = new List<IEvent>
        {
            new SmallEvent { Value = new string('x', 100_000) }, // alone over the 64 KB budget
            new SmallEvent { Value = "tiny" },
        };

        await publisher.PublishBatches(events);

        CollectionAssert.AreEqual(new[] { 1, 1 }, sender.Pages.Select(p => p.Count).ToArray(),
            "An oversized event still goes out as a single-item page (Service Bus rejects it at send time).");
    }

    [TestMethod]
    public async Task PublishBatches_with_no_events_sends_nothing()
    {
        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender);

        await publisher.PublishBatches(Array.Empty<IEvent>());

        Assert.AreEqual(0, sender.Pages.Count);
    }

    [TestMethod]
    public async Task PublishBatches_builds_and_serializes_each_event_exactly_once_end_to_end()
    {
        // Control: property reads for exactly one message build (Validate reads
        // every getter once via DataAnnotations, serialization reads it once more).
        var control = new CountingEvent { Id = "control" };
        var controlPublisher = new PublisherClient(new RecordingSender());
        await controlPublisher.PublishBatches(new IEvent[] { control });
        var readsPerBuild = control.GetterReads;
        Assert.IsTrue(readsPerBuild >= 1, "Sanity: building a message reads the event's properties.");

        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender);
        var events = Enumerable.Range(0, 3).Select(i => new CountingEvent { Id = $"e{i}" }).ToList();

        await publisher.PublishBatches(events);

        foreach (var @event in events)
        {
            Assert.AreEqual(readsPerBuild, @event.GetterReads,
                "PublishBatches must build (validate + serialize) each event exactly once.");
        }

        // Simulate the transport step: the stashed body means no further
        // event/content serialization happens at send time.
        foreach (var message in sender.Pages.SelectMany(p => p))
        {
            var sbMessage = MessageHelper.ToServiceBusMessage(message);
            StringAssert.Contains(sbMessage.Body.ToString(), "EventJson");
        }

        foreach (var @event in events)
        {
            Assert.AreEqual(readsPerBuild, @event.GetterReads,
                "The transport must reuse the stashed body, not re-serialize the event.");
        }
    }

    [TestMethod]
    public void GetBatchesStatic_measures_each_event_once_even_across_page_boundaries()
    {
        // Control: property reads for exactly one measurement.
        var control = new CountingEvent { Id = "control", Padding = "p" };
        PublisherClient.GetBatchesStatic(new List<IEvent> { control }).SelectMany(b => b).ToList();
        var readsPerMeasure = control.GetterReads;
        Assert.IsTrue(readsPerMeasure >= 1, "Sanity: measuring an event reads its properties.");

        // ~30 KB each: events 0-1 fill page 1; event 2 overflows it (measured
        // during the page-1 probe) and must NOT be measured again for page 2.
        var events = Enumerable.Range(0, 5)
            .Select(i => new CountingEvent { Id = $"e{i}", Padding = new string('x', 30_000) })
            .ToList();
        var working = events.Cast<IEvent>().ToList();

        var batches = PublisherClient.GetBatchesStatic(working).Select(b => b.ToList()).ToList();

        Assert.AreEqual(0, working.Count, "GetBatchesStatic drains the input list.");
        CollectionAssert.AreEqual(new[] { 2, 2, 1 }, batches.Select(b => b.Count).ToArray());
        CollectionAssert.AreEqual(
            events.Cast<IEvent>().ToList(),
            batches.SelectMany(b => b).ToList(),
            "Batches must partition the input in order.");
        foreach (var @event in events)
        {
            Assert.AreEqual(readsPerMeasure, @event.GetterReads,
                $"Event {@event.Id} was measured more than once — page-boundary events must carry their measurement.");
        }
    }

    private sealed class SmallEvent : Event
    {
        public string Value { get; set; } = null!;

        public override string GetSessionId() => "session-1";
    }

    private sealed class CountingEvent : Event
    {
        private int _getterReads;
        private string _id = null!;

        public string Id
        {
            get
            {
                Interlocked.Increment(ref _getterReads);
                return _id;
            }
            set => _id = value;
        }

        public string Padding { get; set; } = null!;

        [JsonIgnore]
        public int GetterReads => Volatile.Read(ref _getterReads);

        public override string GetSessionId() => "session-1";
    }

    private sealed class RecordingSender : ISender
    {
        public List<List<IMessage>> Pages { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Pages.Add(new List<IMessage> { message });
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Pages.Add(messages.ToList());
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
