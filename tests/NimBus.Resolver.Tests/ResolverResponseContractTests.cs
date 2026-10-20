#pragma warning disable CA1707, CA1515, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver.Services;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;
using NimBus.Testing.Conformance;
using SbMessage = NimBus.ServiceBus.ServiceBusMessage;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The contract between what an SDK subscriber sends back and what the Resolver records.
/// Each test runs the real pipeline — <see cref="PublisherClient"/> →
/// <see cref="EventHandlerProvider"/> + <see cref="StrictMessageHandler"/> +
/// <see cref="ResponseService"/> → <see cref="ResolverService"/> over the in-memory store —
/// with every hop crossing the real Service Bus wire mapping and the rule actions
/// <c>TopologyDescriptor</c> provisions. Drift on either side (a new response type, a
/// renamed property, a dead-letter flag the Resolver no longer reads) fails here instead of
/// surfacing as a wrong status on the WebApp.
/// </summary>
[TestClass]
public class ResolverResponseContractTests
{
    private const string Publisher = "StorefrontEndpoint";
    private const string Subscriber = "BillingEndpoint";

    [TestMethod]
    public async Task Success_RecordsCompletedWithHandlerTimings()
    {
        var pipeline = new Pipeline((_, _) => Task.CompletedTask);

        var eventId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-ok"));

        var row = await pipeline.Store.GetEvent(Subscriber, eventId);
        Assert.AreEqual(ResolutionStatus.Completed, row.ResolutionStatus);
        Assert.AreEqual(MessageType.ResolutionResponse, row.MessageType);
        Assert.AreEqual(Subscriber, row.EndpointId);
        Assert.AreEqual(nameof(ContractOrderPlaced), row.EventTypeId);
        Assert.AreEqual(Publisher, row.OriginatingFrom, "The publisher identity must survive to the audit row.");
        Assert.IsNotNull(row.ProcessingTimeMs, "The subscriber's handler duration is carried on the response.");
        Assert.AreEqual(Pipeline.QueueTimeMs, row.QueueTimeMs, "The subscriber's queue time is carried on the response.");
        // Both copies are kept in the history; their order follows the enqueue clock.
        CollectionAssert.AreEquivalent(
            new[] { MessageType.EventRequest, MessageType.ResolutionResponse },
            await pipeline.HistoryTypes(eventId));
        Assert.AreEqual(0, pipeline.ResolverSettlement.DeadLetteredCount);
    }

    [TestMethod]
    public async Task HandlerException_RecordsFailedWithTheErrorText()
    {
        var pipeline = new Pipeline((_, _) => throw new InvalidOperationException("ERP rejected the order"));

        var eventId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-fail"));

        var row = await pipeline.Store.GetEvent(Subscriber, eventId);
        Assert.AreEqual(ResolutionStatus.Failed, row.ResolutionStatus);
        Assert.AreEqual(MessageType.ErrorResponse, row.MessageType);
        StringAssert.Contains(row.MessageContent?.ErrorContent?.ErrorText, "ERP rejected the order");
        Assert.IsTrue(string.IsNullOrEmpty(row.DeadLetterErrorDescription), "A retryable failure is not a dead-letter.");
    }

    [TestMethod]
    public async Task PermanentFailure_RecordsDeadLetteredWithTheFailureAsReason()
    {
        // ArgumentException is permanent under DefaultPermanentFailureClassifier: the subscriber
        // dead-letters its copy and tells the Resolver with a dead-letter ErrorResponse.
        var pipeline = new Pipeline((_, _) => throw new ArgumentException("OrderId is malformed"));

        var eventId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-permanent"));

        var row = await pipeline.Store.GetEvent(Subscriber, eventId);
        Assert.AreEqual(ResolutionStatus.DeadLettered, row.ResolutionStatus);
        StringAssert.StartsWith(row.DeadLetterReason, "Permanent failure");
        StringAssert.Contains(row.Reason, "OrderId is malformed");
        Assert.IsTrue(pipeline.SubscriberSettlement.DeadLetteredCount > 0, "The subscriber dead-letters its own copy.");
        Assert.AreEqual(0, pipeline.ResolverSettlement.DeadLetteredCount, "The Resolver records it; it does not dead-letter the response.");
    }

    [TestMethod]
    public async Task NoHandlerForTheType_RecordsUnsupported()
    {
        var pipeline = new Pipeline(handler: null);

        var eventId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-unsupported"));

        var row = await pipeline.Store.GetEvent(Subscriber, eventId);
        Assert.AreEqual(ResolutionStatus.Unsupported, row.ResolutionStatus);
        Assert.AreEqual(MessageType.UnsupportedResponse, row.MessageType);
    }

    [TestMethod]
    public async Task SiblingBehindAFailedEvent_RecordsDeferred()
    {
        // The first event fails and blocks its session; the next event in the same session
        // must be parked and recorded Deferred, not processed out of order.
        var calls = 0;
        var pipeline = new Pipeline((_, _) => ++calls == 1
            ? throw new InvalidOperationException("first one fails")
            : Task.CompletedTask);

        var failedId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-blocked") { OrderId = "A" });
        var siblingId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-blocked") { OrderId = "B" });

        Assert.AreEqual(ResolutionStatus.Failed, (await pipeline.Store.GetEvent(Subscriber, failedId)).ResolutionStatus);
        var sibling = await pipeline.Store.GetEvent(Subscriber, siblingId);
        Assert.AreEqual(ResolutionStatus.Deferred, sibling.ResolutionStatus);
        Assert.AreEqual(MessageType.DeferralResponse, sibling.MessageType);
        Assert.AreEqual(1, calls, "The sibling's handler must not run while the session is blocked.");
    }

    [TestMethod]
    public async Task PendingHandoff_RecordsPendingHandoffWithItsMetadata()
    {
        var pipeline = new Pipeline((_, context) =>
        {
            context.MarkPendingHandoff("Awaiting ERP batch", externalJobId: "erp-job-7", expectedBy: TimeSpan.FromMinutes(30));
            return Task.CompletedTask;
        });

        var eventId = await pipeline.PublishAndProcess(new ContractOrderPlaced("session-handoff"));

        var row = await pipeline.Store.GetEvent(Subscriber, eventId);
        Assert.AreEqual(ResolutionStatus.Pending, row.ResolutionStatus);
        Assert.AreEqual(MessageType.PendingHandoffResponse, row.MessageType);
        Assert.AreEqual("Handoff", row.PendingSubStatus);
        Assert.AreEqual("Awaiting ERP batch", row.HandoffReason);
        Assert.AreEqual("erp-job-7", row.ExternalJobId);
        Assert.IsNotNull(row.ExpectedBy);
    }

    [TestMethod]
    public async Task RequestCopy_IsRecordedPendingUnderTheConsumingEndpointBeforeAnyResponse()
    {
        // The Resolver sees the request copy (to-{endpoint} rule) before the subscriber answers.
        var pipeline = new Pipeline((_, _) => Task.CompletedTask);

        var request = await pipeline.Publish(new ContractOrderPlaced("session-pending"));
        await pipeline.Resolve(request);

        var row = await pipeline.Store.GetEvent(Subscriber, request.GetUserProperty(UserPropertyName.EventId));
        Assert.AreEqual(ResolutionStatus.Pending, row.ResolutionStatus);
        Assert.AreEqual(MessageType.EventRequest, row.MessageType);
        Assert.AreEqual(Subscriber, row.To);
        Assert.AreEqual(Publisher, row.From);
    }

    // ── The pipeline ───────────────────────────────────────────────────────────────────

    public sealed class ContractOrderPlaced : Event
    {
        private readonly string _sessionId;

        public ContractOrderPlaced() : this("session") { }

        public ContractOrderPlaced(string sessionId) => _sessionId = sessionId;

        public string OrderId { get; set; } = Guid.NewGuid().ToString();

        public override string GetSessionId() => _sessionId;
    }

    private sealed class ContractHandler : IEventHandler<ContractOrderPlaced>
    {
        private readonly Func<ContractOrderPlaced, IEventHandlerContext, Task> _behavior;

        public ContractHandler(Func<ContractOrderPlaced, IEventHandlerContext, Task> behavior) => _behavior = behavior;

        public Task Handle(ContractOrderPlaced message, IEventHandlerContext context, CancellationToken cancellationToken = default) =>
            _behavior(message, context);
    }

    private sealed class Pipeline
    {
        private readonly CapturingSender _publisherBus = new();
        private readonly CapturingSender _subscriberBus = new();
        private readonly PublisherClient _publisher;
        private readonly StrictMessageHandler _subscriber;
        private readonly ResolverService _resolver;
        private readonly Dictionary<string, RecordingSession> _subscriberSessions = new();

        public Pipeline(Func<ContractOrderPlaced, IEventHandlerContext, Task>? handler)
        {
            _publisher = new PublisherClient(_publisherBus, Publisher);

            var handlers = new EventHandlerProvider();
            if (handler is not null)
                handlers.RegisterHandler<ContractOrderPlaced>(() => new ContractHandler(handler));

            _subscriber = new StrictMessageHandler(
                handlers,
                new ResponseService(_subscriberBus),
                logger: NullLogger.Instance,
                retryPolicyProvider: null!,
                pipeline: null!,
                lifecycleNotifier: null!,
                failureDispositionClassifier: new DefaultPermanentFailureClassifier());

            _resolver = new ResolverService(Store);
        }

        public const long QueueTimeMs = 25;

        public InMemoryMessageStore Store { get; } = new();
        public RecordingSession SubscriberSettlement { get; } = new();
        public RecordingSession ResolverSettlement { get; } = new();

        /// <summary>Publishes and returns the request as the consuming endpoint receives it.</summary>
        public async Task<IServiceBusMessage> Publish(ContractOrderPlaced @event)
        {
            await _publisher.Publish(@event);
            var published = _publisherBus.Sent.Last();

            // Forward rule on the consumer subscription:
            // SET user.From = '<producer>'; SET user.EventId = newid(); SET user.To = '<consumer>'
            return ToWire(published, new Dictionary<string, object>
            {
                ["From"] = Publisher,
                ["EventId"] = Guid.NewGuid().ToString(),
                ["To"] = Subscriber,
            });
        }

        /// <summary>
        /// Runs one event through the whole pipeline and returns its EventId: the Resolver
        /// records the request copy, the subscriber handles the request, and the Resolver
        /// records every response the subscriber addressed to it.
        /// </summary>
        public async Task<string> PublishAndProcess(ContractOrderPlaced @event)
        {
            var request = await Publish(@event);
            await Resolve(request);

            var alreadySent = _subscriberBus.Sent.Count;
            // ServiceBusAdapter stamps the timings at the transport boundary before dispatch.
            var context = new MessageContext(request, SessionFor(request.SessionId))
            {
                QueueTimeMs = QueueTimeMs,
                HandlerStartedAtUtc = DateTime.UtcNow,
            };
            await _subscriber.Handle(context);

            foreach (var response in _subscriberBus.Sent.Skip(alreadySent).Where(m => m.To == Constants.ResolverId))
            {
                // Fan-out rule from-{endpoint}: SET user.From = '<endpoint>'.
                await Resolve(ToWire(response, new Dictionary<string, object> { ["From"] = Subscriber }));
            }

            return request.GetUserProperty(UserPropertyName.EventId);
        }

        public Task Resolve(IServiceBusMessage wireMessage) =>
            _resolver.Handle(new MessageContext(wireMessage, ResolverSettlement));

        public async Task<MessageType[]> HistoryTypes(string eventId) =>
            (await Store.GetEventHistory(eventId)).Select(m => m.MessageType).ToArray();

        private RecordingSession SessionFor(string sessionId)
        {
            // Session state (the block) is per session, settlement counts are pipeline-wide.
            if (!_subscriberSessions.TryGetValue(sessionId, out var session))
            {
                session = new RecordingSession(SubscriberSettlement);
                _subscriberSessions[sessionId] = session;
            }

            return session;
        }

        private static IServiceBusMessage ToWire(IMessage message, IDictionary<string, object> ruleActions)
        {
            var outgoing = MessageHelper.ToServiceBusMessage(message);
            var properties = outgoing.ApplicationProperties.ToDictionary(p => p.Key, p => p.Value);
            foreach (var (key, value) in ruleActions)
                properties[key] = value;

            return new SbMessage(ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: outgoing.Body,
                // Responses leave MessageId unset and the broker assigns one; the Resolver
                // needs it to key the history row.
                messageId: outgoing.MessageId ?? Guid.NewGuid().ToString("N"),
                sessionId: outgoing.SessionId,
                correlationId: outgoing.CorrelationId,
                properties: properties,
                enqueuedTime: DateTimeOffset.UtcNow,
                deliveryCount: 1));
        }
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
        {
            Sent.Add(message);
            return Task.FromResult(0L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Records settlements; holds its own session state and optionally shares counts.</summary>
    private sealed class RecordingSession : IServiceBusSession
    {
        private readonly RecordingSession? _counts;
        private SessionState _state = new();
        private int _completed;
        private int _deadLettered;

        public RecordingSession(RecordingSession? counts = null) => _counts = counts;

        public int CompletedCount => _counts?.CompletedCount ?? _completed;
        public int DeadLetteredCount => _counts?.DeadLetteredCount ?? _deadLettered;

        public Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            if (_counts is null) _completed++; else _counts._completed++;
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(IServiceBusMessage message, string reason, string? v, CancellationToken cancellationToken = default)
        {
            if (_counts is null) _deadLettered++; else _counts._deadLettered++;
            return Task.CompletedTask;
        }

        public Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default)
        {
            _state = sessionState;
            return Task.CompletedTask;
        }

        public Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default) => Task.FromResult(_state);

        public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
