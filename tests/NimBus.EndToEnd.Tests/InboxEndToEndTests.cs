#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Outbox;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK;
using NimBus.Testing;

namespace NimBus.EndToEnd.Tests;

[TestClass]
public sealed class InboxEndToEndTests
{
    private static readonly string[] BothOrders = ["ORD-BLOCKING", "ORD-PARKED"];

    [TestMethod]
    public async Task Outbox_redelivery_after_success_is_skipped_and_reported()
    {
        var store = new InMemoryInboxStore();
        var observer = new DuplicateRecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var fixture = EndToEndFixture.CreateWithHandlerDecorator(
            inner => new InboxMiddleware(inner, store, notifier),
            notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var outbox = new ReplayableOutbox();
        var publisher = new PublisherClient(new OutboxSender(outbox));
        var dispatcher = new OutboxDispatcher(outbox, fixture.PublishBus);
        await publisher.Publish(
            new OrderPlaced("inbox-success-session") { OrderId = "ORD-INBOX-1" },
            "inbox-success-session",
            "inbox-success-correlation",
            "inbox-success-message");

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        var firstDelivery = await fixture.DeliverAllWithResults();
        var endpointId = firstDelivery.Single().Context.To;
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
        Assert.IsTrue(firstDelivery.Single().Session.WasCompleted);
        Assert.IsTrue(await store.HasProcessedAsync(endpointId, "inbox-success-message"));

        // Simulate the publish-side crash window: the outbox send succeeded but its
        // checkpoint did not, so the same stored message is dispatched again.
        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        var duplicateDelivery = await fixture.DeliverAllWithResults();

        Assert.AreEqual(1, handler.ReceivedEvents.Count, "The duplicate must not reach the application handler.");
        Assert.IsTrue(duplicateDelivery.Single().Session.WasCompleted, "A duplicate must be settled normally.");
        Assert.AreEqual(1, observer.Duplicates.Count);
        Assert.AreEqual("inbox-success-message", observer.Duplicates[0].MessageId);
        Assert.AreEqual(duplicateDelivery.Single().Context.To, observer.Duplicates[0].EndpointId);
        Assert.AreEqual(duplicateDelivery.Single().Context.EventId, observer.Duplicates[0].EventId);
        Assert.AreEqual(duplicateDelivery.Single().Context.SessionId, observer.Duplicates[0].SessionId);

        var duplicateResponse = fixture.ResponseBus.SentMessages.Single(
            message => message.MessageType == MessageType.SkipResponse);
        Assert.AreEqual(
            InboxMiddleware.DuplicateReason,
            duplicateResponse.MessageContent.ErrorContent!.ErrorText);
    }

    [TestMethod]
    public async Task Failed_first_attempt_is_not_recorded_and_redelivery_runs_again()
    {
        var store = new InMemoryInboxStore();
        var observer = new DuplicateRecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var fixture = EndToEndFixture.CreateWithHandlerDecorator(
            inner => new InboxMiddleware(inner, store, notifier),
            notifier);

        var attempts = 0;
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = _ => Interlocked.Increment(ref attempts) == 1
                ? new TransientException("first attempt fails")
                : null,
        };
        fixture.RegisterHandler(() => handler);

        var outbox = new ReplayableOutbox();
        var publisher = new PublisherClient(new OutboxSender(outbox));
        var dispatcher = new OutboxDispatcher(outbox, fixture.PublishBus);
        await publisher.Publish(
            new OrderPlaced("inbox-retry-session") { OrderId = "ORD-INBOX-2" },
            "inbox-retry-session",
            "inbox-retry-correlation",
            "inbox-retry-message");

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        var firstDelivery = await fixture.DeliverAllWithResults();
        var endpointId = firstDelivery.Single().Context.To;
        Assert.AreEqual(1, attempts);
        Assert.IsFalse(await store.HasProcessedAsync(endpointId, "inbox-retry-message"));

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(2, attempts, "The unrecorded redelivery must run the handler again.");
        Assert.IsTrue(await store.HasProcessedAsync(endpointId, "inbox-retry-message"));

        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync());
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(2, attempts, "A later redelivery must be skipped after the successful attempt.");
        Assert.AreEqual(1, observer.Duplicates.Count);
    }

    // A retry, a resubmission and a deferred replay each reach the endpoint with a fresh
    // MessageId. When one of them completes the work, a later delivery of the original
    // MessageId (the at-least-once case the inbox exists for) must still be a duplicate.

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Original_redelivered_after_its_retry_succeeded_is_skipped(bool checkInMessageHandler)
    {
        var store = new InMemoryInboxStore();
        var retries = new DefaultRetryPolicyProvider()
            .SetDefaultPolicy(new RetryPolicy { MaxRetries = 3, BaseDelay = TimeSpan.Zero });
        var fixture = CreateInboxFixture(store, checkInMessageHandler, retries);
        var handler = new RecordingOrderPlacedHandler { ExceptionFactory = FailFirstAttemptOf("ORD-RETRIED") };
        fixture.RegisterHandler(() => handler);
        var (publisher, dispatcher) = CreateReplayablePublisher(fixture);
        await publisher.Publish(
            new OrderPlaced("retried-session") { OrderId = "ORD-RETRIED" },
            "retried-session",
            "retried-correlation",
            "retried-message");

        await dispatcher.DispatchPendingAsync();
        var endpointId = (await fixture.DeliverAllWithResults()).Single().Context.To;
        var retry = fixture.ResponseBus.SentMessages.Single(m => m.MessageType == MessageType.RetryRequest);
        await fixture.PublishBus.Send(AsDeliveredRetry(retry, endpointId));
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(1, handler.ReceivedEvents.Count, "The retry must complete the work.");

        await dispatcher.DispatchPendingAsync();
        await fixture.DeliverAllWithResults();

        Assert.AreEqual(1, handler.ReceivedEvents.Count, "A redelivery of the original must not run the handler again.");
        Assert.IsTrue(await store.HasProcessedAsync(endpointId, "retried-message"));
        Assert.AreEqual(1, DuplicateSkips(fixture));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Original_redelivered_after_its_resubmission_succeeded_is_skipped(bool checkInMessageHandler)
    {
        var store = new InMemoryInboxStore();
        var fixture = CreateInboxFixture(store, checkInMessageHandler);
        var handler = new RecordingOrderPlacedHandler { ExceptionFactory = FailFirstAttemptOf("ORD-RESUBMITTED") };
        fixture.RegisterHandler(() => handler);
        var (publisher, dispatcher) = CreateReplayablePublisher(fixture);
        await publisher.Publish(
            new OrderPlaced("resubmitted-session") { OrderId = "ORD-RESUBMITTED" },
            "resubmitted-session",
            "resubmitted-correlation",
            "resubmitted-message");

        await dispatcher.DispatchPendingAsync();
        var endpointId = (await fixture.DeliverAllWithResults()).Single().Context.To;
        var error = fixture.ResponseBus.SentMessages.Single(m => m.MessageType == MessageType.ErrorResponse);
        await fixture.PublishBus.Send(AsResubmission(error, endpointId));
        await fixture.DeliverAllWithResults();
        Assert.AreEqual(1, handler.ReceivedEvents.Count, "The resubmission must complete the work.");

        await dispatcher.DispatchPendingAsync();
        await fixture.DeliverAllWithResults();

        Assert.AreEqual(1, handler.ReceivedEvents.Count, "A redelivery of the original must not run the handler again.");
        Assert.IsTrue(await store.HasProcessedAsync(endpointId, "resubmitted-message"));
        Assert.AreEqual(1, DuplicateSkips(fixture));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Original_redelivered_after_its_deferred_replay_succeeded_is_skipped(bool checkInMessageHandler)
    {
        var store = new InMemoryInboxStore();
        var fixture = CreateInboxFixture(store, checkInMessageHandler);
        var handler = new RecordingOrderPlacedHandler { ExceptionFactory = FailFirstAttemptOf("ORD-BLOCKING") };
        fixture.RegisterHandler(() => handler);
        var (publisher, dispatcher) = CreateReplayablePublisher(fixture);
        await publisher.Publish(
            new OrderPlaced("parked-session") { OrderId = "ORD-BLOCKING" },
            "parked-session",
            "parked-correlation",
            "blocking-message");
        await publisher.Publish(
            new OrderPlaced("parked-session") { OrderId = "ORD-PARKED" },
            "parked-session",
            "parked-correlation",
            "parked-message");

        // The first order fails and blocks the session, so the second is parked.
        await dispatcher.DispatchPendingAsync();
        var endpointId = (await fixture.DeliverAllWithResults()).First().Context.To;
        var error = fixture.ResponseBus.SentMessages.Single(m => m.MessageType == MessageType.ErrorResponse);
        await fixture.PublishBus.Send(AsResubmission(error, endpointId));
        await fixture.DeliverAllWithResults();
        await ReplayParkedMessages(fixture, endpointId);
        CollectionAssert.AreEqual(
            BothOrders,
            handler.ReceivedEvents.Select(e => e.OrderId).ToArray(),
            "The resubmission and the replay must complete both orders.");

        await dispatcher.DispatchPendingAsync();
        await fixture.DeliverAllWithResults();

        Assert.AreEqual(2, handler.ReceivedEvents.Count, "Redeliveries of both originals must not run the handler again.");
        Assert.IsTrue(await store.HasProcessedAsync(endpointId, "parked-message"));
        Assert.AreEqual(2, DuplicateSkips(fixture));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Redelivery_parked_behind_a_blocked_session_is_skipped_when_replayed(bool checkInMessageHandler)
    {
        var store = new InMemoryInboxStore();
        var fixture = CreateInboxFixture(store, checkInMessageHandler);
        var handler = new RecordingOrderPlacedHandler { ExceptionFactory = FailFirstAttemptOf("ORD-BLOCKING") };
        fixture.RegisterHandler(() => handler);
        var (publisher, dispatcher) = CreateReplayablePublisher(fixture);
        await publisher.Publish(
            new OrderPlaced("twice-parked-session") { OrderId = "ORD-BLOCKING" },
            "twice-parked-session",
            "twice-parked-correlation",
            "twice-blocking-message");
        await publisher.Publish(
            new OrderPlaced("twice-parked-session") { OrderId = "ORD-PARKED" },
            "twice-parked-session",
            "twice-parked-correlation",
            "twice-parked-message");

        // The source delivers both orders twice while the first one's failure blocks the
        // session, so both copies of each are parked, before either has completed.
        await dispatcher.DispatchPendingAsync();
        var endpointId = (await fixture.DeliverAllWithResults()).First().Context.To;
        await dispatcher.DispatchPendingAsync();
        await fixture.DeliverAllWithResults();
        var error = fixture.ResponseBus.SentMessages.Single(m => m.MessageType == MessageType.ErrorResponse);
        await fixture.PublishBus.Send(AsResubmission(error, endpointId));
        await fixture.DeliverAllWithResults();

        await ReplayParkedMessages(fixture, endpointId);

        CollectionAssert.AreEqual(
            BothOrders,
            handler.ReceivedEvents.Select(e => e.OrderId).ToArray(),
            "Each order must run once; its parked second copy is a duplicate.");
        Assert.AreEqual(2, DuplicateSkips(fixture));
    }

    private static EndToEndFixture CreateInboxFixture(
        InMemoryInboxStore store,
        bool checkInMessageHandler,
        IRetryPolicyProvider? retryPolicyProvider = null) =>
        checkInMessageHandler
            // The hosted composition: StrictMessageHandler checks, the decorator records.
            ? EndToEndFixture.CreateWithHandlerDecorator(
                inner => new InboxMiddleware(inner, store, checkHandledUpstream: true),
                retryPolicyProvider: retryPolicyProvider,
                inboxDuplicateDetector: new InboxDuplicateDetector(store))
            : EndToEndFixture.CreateWithHandlerDecorator(
                inner => new InboxMiddleware(inner, store),
                retryPolicyProvider: retryPolicyProvider);

    private static (PublisherClient Publisher, OutboxDispatcher Dispatcher) CreateReplayablePublisher(EndToEndFixture fixture)
    {
        // Every dispatch sends every stored message again, like a source that delivers at
        // least once.
        var outbox = new ReplayableOutbox();
        return (new PublisherClient(new OutboxSender(outbox)), new OutboxDispatcher(outbox, fixture.PublishBus));
    }

    private static Func<OrderPlaced, Exception?> FailFirstAttemptOf(string orderId)
    {
        var attempts = 0;
        return order => order.OrderId == orderId && Interlocked.Increment(ref attempts) == 1
            ? new InvalidOperationException($"The first attempt of {orderId} fails.")
            : null;
    }

    private static int DuplicateSkips(EndToEndFixture fixture) =>
        fixture.ResponseBus.SentMessages.Count(message =>
            message.MessageType == MessageType.SkipResponse
            && message.MessageContent?.ErrorContent?.ErrorText == InboxMiddleware.DuplicateReason);

    // The broker keeps every application property of a scheduled RetryRequest and the
    // endpoint's retry rule re-addresses it; the broker assigns the MessageId.
    private static Message AsDeliveredRetry(IMessage retry, string endpointId)
    {
        var delivered = Clone(retry);
        delivered.MessageId = Guid.NewGuid().ToString();
        delivered.To = endpointId;
        delivered.From = Constants.RetryId;
        return delivered;
    }

    // What the Manager sends for Resubmit, built from the stored error response.
    private static Message AsResubmission(IMessage errorResponse, string endpointId) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        CorrelationId = errorResponse.CorrelationId,
        EventId = errorResponse.EventId,
        SessionId = errorResponse.SessionId,
        To = endpointId,
        From = Constants.ManagerId,
        OriginatingMessageId = errorResponse.OriginatingMessageId,
        ParentMessageId = "error-response-message",
        MessageType = MessageType.ResubmissionRequest,
        EventTypeId = errorResponse.EventTypeId,
        MessageContent = new MessageContent { EventContent = errorResponse.MessageContent.EventContent },
    };

    // Replays the parked copies in order, the way the deferred processor does: every
    // application property except the deferral ones and To is kept, with a new MessageId.
    private static async Task ReplayParkedMessages(EndToEndFixture fixture, string endpointId)
    {
        var parked = fixture.ResponseBus.SentMessages
            .Where(message => message.To == Constants.DeferredSubscriptionName)
            .OrderBy(message => message.DeferralSequence)
            .ToList();
        Assert.IsTrue(parked.Count > 0, "Expected parked messages to replay.");

        foreach (var copy in parked)
        {
            var replay = Clone(copy);
            replay.MessageId = Guid.NewGuid().ToString();
            replay.To = endpointId;
            replay.OriginalSessionId = null!;
            replay.DeferralSequence = null;
            await fixture.PublishBus.Send(replay);
            await fixture.DeliverAllWithResults();
        }
    }

    private static Message Clone(IMessage message) =>
        JsonConvert.DeserializeObject<Message>(JsonConvert.SerializeObject(message))!;

    private sealed class DuplicateRecordingObserver : IMessageLifecycleObserver
    {
        public List<MessageLifecycleContext> Duplicates { get; } = [];

        public Task OnDuplicateDetected(
            MessageLifecycleContext context,
            CancellationToken cancellationToken = default)
        {
            Duplicates.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayableOutbox : IOutbox
    {
        private readonly List<OutboxMessage> _messages = [];

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.Add(message);
            return Task.CompletedTask;
        }

        public Task StoreBatchAsync(
            IEnumerable<OutboxMessage> messages,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _messages.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
            int batchSize,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OutboxMessage>>(_messages.Take(batchSize).ToList());
        }

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task MarkAsDispatchedAsync(
            IEnumerable<string> ids,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
