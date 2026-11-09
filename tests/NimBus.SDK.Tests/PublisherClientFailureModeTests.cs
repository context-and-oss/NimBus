#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;

namespace NimBus.SDK.Tests;

/// <summary>
/// The publisher's request/reply failure modes, scheduling, batch publishing, the manual
/// factory, and the <see cref="IPublisherClient"/> default members custom implementers inherit.
/// </summary>
[TestClass]
public class PublisherClientFailureModeTests
{
    private const string Endpoint = "CrmEndpoint";
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    // ── Request / reply ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Request_MissingReplySubscription_ExplainsHowToProvisionIt()
    {
        var client = new ScriptedClient
        {
            AcceptFailure = new ServiceBusException("not found", ServiceBusFailureReason.MessagingEntityNotFound),
        };
        var publisher = new PublisherClient(new RecordingSender(), Endpoint) { ReplyServiceBusClient = client };

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromSeconds(5)));

        StringAssert.Contains(exception.Message, "CrmEndpoint/CrmEndpoint-reply", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "nb topology apply", StringComparison.Ordinal);
        Assert.IsInstanceOfType<ServiceBusException>(exception.InnerException);
    }

    [TestMethod]
    public async Task Request_OtherAcceptFailures_PropagateUnchanged()
    {
        var client = new ScriptedClient
        {
            AcceptFailure = new ServiceBusException("denied", ServiceBusFailureReason.ServiceBusy),
        };
        var publisher = new PublisherClient(new RecordingSender(), Endpoint) { ReplyServiceBusClient = client };

        await Assert.ThrowsExactlyAsync<ServiceBusException>(
            () => publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Request_ErrorReply_RaisesTheResponderFailureAndSettlesTheReply()
    {
        var receiver = new ScriptedReceiver(Reply("{}", new Dictionary<string, object>
        {
            [ReplyConstants.ReplyStatusProperty] = ReplyConstants.StatusError,
            [ReplyConstants.ErrorTypeProperty] = "System.InvalidOperationException",
            [ReplyConstants.ErrorTextProperty] = "no price for SKU-9",
        }));
        var publisher = new PublisherClient(new RecordingSender(), Endpoint) { ReplyServiceBusClient = new ScriptedClient { Receiver = receiver } };

        var exception = await Assert.ThrowsExactlyAsync<RequestReplyException>(
            () => publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromSeconds(5)));

        Assert.AreEqual("System.InvalidOperationException", exception.ErrorType);
        Assert.AreEqual("no price for SKU-9", exception.ErrorText);
        Assert.AreEqual(1, receiver.Completed, "The error reply is consumed, not left on the reply subscription.");
        Assert.AreEqual(1, receiver.Disposed);
    }

    [TestMethod]
    public async Task Request_SuccessStatusReply_IsDeserialized()
    {
        var receiver = new ScriptedReceiver(Reply("{\"Price\":42}", new Dictionary<string, object>
        {
            [ReplyConstants.ReplyStatusProperty] = ReplyConstants.StatusSuccess,
        }));
        var publisher = new PublisherClient(new RecordingSender(), Endpoint) { ReplyServiceBusClient = new ScriptedClient { Receiver = receiver } };

        var reply = await publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromSeconds(5));

        Assert.AreEqual(42, reply.Price);
        Assert.AreEqual(1, receiver.Disposed);
    }

    [TestMethod]
    public async Task Request_NoReplyWithinTheTimeout_ThrowsTimeoutAndReleasesTheSession()
    {
        var receiver = new ScriptedReceiver(reply: null);
        var publisher = new PublisherClient(new RecordingSender(), Endpoint) { ReplyServiceBusClient = new ScriptedClient { Receiver = receiver } };

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromMilliseconds(50)));

        Assert.AreEqual(1, receiver.Disposed);
    }

    [TestMethod]
    public async Task Request_TimeoutWhileAcceptingTheSession_IsReportedAsATimeout()
    {
        var publisher = new PublisherClient(new RecordingSender(), Endpoint)
        {
            ReplyServiceBusClient = new ScriptedClient { HangOnAccept = true },
        };

        await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromMilliseconds(50)));
    }

    [TestMethod]
    public async Task Request_CallerCancellation_IsNotReportedAsATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender, Endpoint)
        {
            ReplyServiceBusClient = new ScriptedClient { HangOnAccept = true },
        };

        var request = publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromMinutes(5), cancellation.Token);
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        Assert.IsNotInstanceOfType<TimeoutException>(exception);
        Assert.AreEqual(1, sender.Sent.Count, "The request itself was sent before the wait began.");
    }

    // ── Scheduling and batches ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Schedule_SendsTheEventAtTheRequestedTime_AndCancelForwardsTheSequenceNumber()
    {
        var sender = new RecordingSender { NextSequenceNumber = 77 };
        var publisher = new PublisherClient(sender, Endpoint);
        var at = DateTimeOffset.UtcNow.AddHours(1);

        var sequenceNumber = await publisher.Schedule(new QuoteRequested { Sku = "SKU-1" }, at);
        await publisher.CancelScheduled(sequenceNumber);

        Assert.AreEqual(77, sequenceNumber);
        var scheduled = sender.Scheduled.Single();
        Assert.AreEqual(at, scheduled.At);
        Assert.AreEqual(nameof(QuoteRequested), scheduled.Message.EventTypeId);
        Assert.AreEqual(Endpoint, scheduled.Message.OriginatingFrom);
        CollectionAssert.AreEqual(new[] { 77L }, sender.Cancelled);
    }

    [TestMethod]
    public async Task PublishBatch_SendsAllEventsInOneCallUnderOneCorrelationId()
    {
        var sender = new RecordingSender();
        var publisher = new PublisherClient(sender, Endpoint);

        await publisher.PublishBatch(new IEvent[] { new QuoteRequested { Sku = "A" }, new QuoteRequested { Sku = "B" } }, "corr-1");
        await publisher.PublishBatch(new IEvent[] { new QuoteRequested { Sku = "C" }, new QuoteRequested { Sku = "D" } });

        Assert.AreEqual(2, sender.BatchSends.Count, "Each PublishBatch is a single send.");
        Assert.IsTrue(sender.BatchSends[0].All(m => m.CorrelationId == "corr-1"));
        var generated = sender.BatchSends[1].Select(m => m.CorrelationId).Distinct().ToList();
        Assert.AreEqual(1, generated.Count, "A generated correlation id is shared by the whole batch.");
        Assert.IsFalse(string.IsNullOrEmpty(generated[0]));
    }

    [TestMethod]
    public void GetBatches_PutsAnOversizedLeadingEventInItsOwnBatch()
    {
        var publisher = new PublisherClient(new RecordingSender(), Endpoint);
        var oversized = new QuoteRequested { Sku = new string('x', (int)PublisherClient.MaxBatchBodyBytes + 1) };
        var small1 = new QuoteRequested { Sku = "A" };
        var small2 = new QuoteRequested { Sku = "B" };

        var batches = publisher.GetBatches(new List<IEvent> { oversized, small1, small2 }).Select(b => b.ToList()).ToList();

        Assert.AreEqual(2, batches.Count);
        CollectionAssert.AreEqual(new IEvent[] { oversized }, batches[0]);
        CollectionAssert.AreEqual(new IEvent[] { small1, small2 }, batches[1]);
    }

    [TestMethod]
    public async Task PublishFromContext_RejectsMissingArguments()
    {
        var publisher = new PublisherClient(new RecordingSender(), Endpoint);
        var context = new EventHandlerContext { MessageId = "m", SessionId = "s", CorrelationId = "c" };

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => publisher.PublishFromContext(null!, context, "out-1"));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => publisher.PublishFromContext(new QuoteRequested(), null!, "out-1"));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => publisher.PublishFromContext(new QuoteRequested(), context, " "));
    }

    // ── Construction ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CreateAsync_ValidatesArguments_AndEnablesRequestReply()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => PublisherClient.CreateAsync(null!, Endpoint));
        await using var client = new ServiceBusClient(FakeConnection);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PublisherClient.CreateAsync(client, ""));

        var publisher = await PublisherClient.CreateAsync(client, Endpoint);

        Assert.AreSame(client, publisher.ReplyServiceBusClient, "CreateAsync wires the client request/reply needs.");
    }

    [TestMethod]
    public void Constructor_RequiresASender()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new PublisherClient(null!));
    }

    [TestMethod]
    public async Task SubscriberClient_CreateAsync_ValidatesArguments()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => SubscriberClient.CreateAsync(null!, Endpoint));
        await using var client = new ServiceBusClient(FakeConnection);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => SubscriberClient.CreateAsync(client, " ".Trim()));

        Assert.IsNotNull(await SubscriberClient.CreateAsync(client, Endpoint));
    }

    // ── IPublisherClient defaults for custom implementers ──────────────────────────────

    [TestMethod]
    public async Task DefaultPublishBatches_PublishesEachBatchFromGetBatches()
    {
        IPublisherClient publisher = new MinimalPublisher();

        await publisher.PublishBatches(new IEvent[] { new QuoteRequested(), new QuoteRequested(), new QuoteRequested() }, "corr-1");

        var minimal = (MinimalPublisher)publisher;
        CollectionAssert.AreEqual(new[] { 2, 1 }, minimal.PublishedBatchSizes);
        Assert.IsTrue(minimal.CorrelationIds.All(c => c == "corr-1"));
    }

    [TestMethod]
    public async Task DefaultContextAndRequestMembers_ExplainTheyNeedPublisherClient()
    {
        IPublisherClient publisher = new MinimalPublisher();

        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => publisher.PublishFromContext(new QuoteRequested(), new EventHandlerContext(), "out-1"));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => publisher.Request<QuoteRequested, QuoteReply>(new QuoteRequested(), TimeSpan.FromSeconds(1)));
    }

    // ── Fakes ──────────────────────────────────────────────────────────────────────────

    public sealed class QuoteRequested : Event
    {
        public string Sku { get; set; } = "SKU-1";
    }

    public sealed class QuoteReply
    {
        public int Price { get; set; }
    }

    private static ServiceBusReceivedMessage Reply(string body, IDictionary<string, object> properties) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(body: new BinaryData(body), properties: properties);

    private sealed class RecordingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();
        public List<List<IMessage>> BatchSends { get; } = new();
        public List<(IMessage Message, DateTimeOffset At)> Scheduled { get; } = new();
        public List<long> Cancelled { get; } = new();
        public long NextSequenceNumber { get; set; } = 1;

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            BatchSends.Add(messages.ToList());
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            Scheduled.Add((message, scheduledEnqueueTime));
            return Task.FromResult(NextSequenceNumber);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
        {
            Cancelled.Add(sequenceNumber);
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedClient : ServiceBusClient
    {
        public Exception? AcceptFailure { get; init; }
        public bool HangOnAccept { get; init; }
        public ServiceBusSessionReceiver? Receiver { get; init; }

        public override async Task<ServiceBusSessionReceiver> AcceptSessionAsync(
            string topicName, string subscriptionName, string sessionId,
            ServiceBusSessionReceiverOptions options = default!, CancellationToken cancellationToken = default)
        {
            if (AcceptFailure is not null)
                throw AcceptFailure;
            if (HangOnAccept)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Receiver ?? throw new InvalidOperationException("No receiver scripted.");
        }
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class ScriptedReceiver : ServiceBusSessionReceiver
    {
        private readonly ServiceBusReceivedMessage? _reply;

        public ScriptedReceiver(ServiceBusReceivedMessage? reply) => _reply = reply;

        public int Completed { get; private set; }
        public int Disposed { get; private set; }

        public override Task<ServiceBusReceivedMessage> ReceiveMessageAsync(TimeSpan? maxWaitTime = default, CancellationToken cancellationToken = default) =>
            Task.FromResult(_reply!);

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            Completed++;
            return Task.CompletedTask;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MinimalPublisher : IPublisherClient
    {
        public List<int> PublishedBatchSizes { get; } = new();
        public List<string?> CorrelationIds { get; } = new();

        public Task Publish(IEvent @event) => throw new NotSupportedException();
        public Task Publish(IMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(IEvent @event, string sessionId, string correlationId) => throw new NotSupportedException();
        public Task Publish(IEvent @event, string sessionId, string correlationId, string? messageId) => throw new NotSupportedException();

        public Task PublishBatch(IEnumerable<IEvent> events, string? correlationId = null)
        {
            PublishedBatchSizes.Add(events.Count());
            CorrelationIds.Add(correlationId);
            return Task.CompletedTask;
        }

        public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events) =>
            events.Chunk(2).Select(chunk => (IEnumerable<IEvent>)chunk);
    }
}
