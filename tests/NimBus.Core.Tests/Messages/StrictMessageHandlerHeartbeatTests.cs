#pragma warning disable CA1707, CA1515, CA2007

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests.Messages;

/// <summary>
/// The SDK side of the platform heartbeat: an <c>EventRequest</c> carrying
/// <see cref="Heartbeat.EventTypeId"/> is answered by the handler itself, without a
/// registered user handler, ahead of the inbox duplicate check and the session guard.
/// </summary>
[TestClass]
public class StrictMessageHandlerHeartbeatTests
{
    [TestMethod]
    public async Task HandleEventRequest_HeartbeatWithoutRegisteredHandler_SendsResolutionResponseAndCompletes()
    {
        var ctx = CreateHeartbeatContext();
        // No handler is registered for Heartbeat on a real adapter, which is what the
        // EventHandlerNotFoundException stands in for here. The short-circuit must fire
        // before the handler is ever consulted, so the reply is a ResolutionResponse
        // ("On") rather than the UnsupportedResponse an older SDK would send.
        var handler = new FakeEventContextHandler { ThrowOnHandle = new EventHandlerNotFoundException("not found") };
        var response = new CountingResponseService();
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "The heartbeat must never reach the user handler");
        Assert.AreEqual(1, response.HeartbeatCalls);
        Assert.AreEqual(0, response.UnsupportedCalls, "A heartbeat must not answer Unsupported on a heartbeat-aware SDK");
        Assert.AreEqual(0, response.ResolutionCalls, "The plain resolution path must not also fire");
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Heartbeat_StampsTimesEndpointVersionAndFrom()
    {
        var forwardSendTime = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc);
        var ctx = CreateHeartbeatContext(eventJson: JsonConvert.SerializeObject(new Heartbeat
        {
            ForwardSendTime = forwardSendTime,
        }));
        var bus = new InMemoryMessageBus();
        var sut = new StrictMessageHandler(new FakeEventContextHandler(), new ResponseService(bus), NullLogger.Instance);
        var before = DateTime.UtcNow;

        await sut.Handle(ctx);

        var after = DateTime.UtcNow;
        var sent = bus.SentMessages.Single();
        Assert.AreEqual(MessageType.ResolutionResponse, sent.MessageType);
        Assert.AreEqual(Heartbeat.EventTypeId, sent.MessageContent.EventContent.EventTypeId);
        // The Resolver attributes the row by the payload's Endpoint first and the
        // message's From second — both have to name the answering endpoint.
        Assert.AreEqual(ctx.To, sent.From);

        var heartbeat = JsonConvert.DeserializeObject<Heartbeat>(sent.MessageContent.EventContent.EventJson);
        Assert.IsNotNull(heartbeat);
        Assert.AreEqual(forwardSendTime, heartbeat.ForwardSendTime, "The sender's outbound stamp must round-trip");
        Assert.IsTrue(heartbeat.ForwardReceivedTime >= before && heartbeat.ForwardReceivedTime <= after);
        Assert.AreEqual(heartbeat.ForwardReceivedTime, heartbeat.BackwardSendTime);
        Assert.AreEqual(ctx.To, heartbeat.Endpoint);
        Assert.IsFalse(string.IsNullOrWhiteSpace(heartbeat.SdkVersion));
        Assert.IsFalse(heartbeat.SdkVersion.Contains('+'), "SdkVersion must be the bare package version without the '+<sha>' build suffix");
    }

    [TestMethod]
    public async Task HandleEventRequest_HeartbeatWithBlankBody_StillAnswers()
    {
        var ctx = CreateHeartbeatContext(eventJson: "   ");
        var bus = new InMemoryMessageBus();
        var sut = new StrictMessageHandler(new FakeEventContextHandler(), new ResponseService(bus), NullLogger.Instance);

        await sut.Handle(ctx);

        var heartbeat = JsonConvert.DeserializeObject<Heartbeat>(bus.SentMessages.Single().MessageContent.EventContent.EventJson);
        Assert.IsNotNull(heartbeat);
        Assert.AreEqual(ctx.To, heartbeat.Endpoint);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HeartbeatOnBlockedSession_StillAnswers()
    {
        // A session blocked by a failed event is exactly when an operator needs the
        // probe to work — the endpoint is alive, its session is not.
        var ctx = CreateHeartbeatContext();
        ctx.BlockedByEventId = "other-event";
        var response = new CountingResponseService();
        var sut = new StrictMessageHandler(new FakeEventContextHandler(), response, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.HeartbeatCalls);
        Assert.AreEqual(0, response.DeferralCalls, "The heartbeat must bypass the session guard, not defer behind the blocker");
        Assert.AreEqual(0, response.SendToDeferredSubscriptionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HeartbeatAlreadyRecordedInInbox_StillAnswers()
    {
        // Probes are not business traffic: they carry a fresh id every interval and must
        // never be deduplicated away, so the short-circuit precedes the inbox pre-check.
        var trace = new List<string>();
        var ctx = CreateHeartbeatContext();
        ctx.Trace = trace;
        ctx.BlockedByEventId = "other-event";
        var response = new CountingResponseService(trace);
        var sut = new StrictMessageHandler(
            new FakeEventContextHandler(trace),
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            failureDispositionClassifier: null,
            inboxDuplicateDetector: new InboxDuplicateDetector(new AlwaysProcessedInboxStore(trace)));

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.HeartbeatCalls);
        Assert.AreEqual(0, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        List<string> expectedTrace = ["response-heartbeat", "complete"];
        CollectionAssert.AreEqual(
            expectedTrace,
            trace,
            "Heartbeat handling must bypass the inbox check, session guard, block/defer operations, and user handler.");
    }

    [TestMethod]
    public async Task HandleEventRequest_HeartbeatEventTypeIdCasingDiffers_StillAnswers()
    {
        var ctx = CreateHeartbeatContext(eventTypeId: Heartbeat.EventTypeId.ToUpperInvariant());
        var response = new CountingResponseService();
        var sut = new StrictMessageHandler(new FakeEventContextHandler(), response, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.HeartbeatCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HeartbeatOnlyInEventContent_StillAnswers()
    {
        // Senders that stamp the event type only inside the serialized content — the
        // user property is authoritative, the body is the fallback.
        var ctx = CreateHeartbeatContext();
        ctx.EventTypeId = null!;
        var response = new CountingResponseService();
        var sut = new StrictMessageHandler(new FakeEventContextHandler(), response, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.HeartbeatCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Heartbeat_ProcessorStopsAfterAnswering_StillCompletes()
    {
        // The receiver stops its processor when the endpoint circuit opens, cancelling the
        // token of every in-flight delivery. An answered heartbeat is settled like any other
        // delivery rather than redelivered and answered twice after the restart.
        using var processorStop = new CancellationTokenSource();
        var ctx = CreateHeartbeatContext();
        var response = new CountingResponseService { AfterHeartbeat = processorStop.Cancel };
        var sut = new StrictMessageHandler(new FakeEventContextHandler(), response, NullLogger.Instance);

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, response.HeartbeatCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_NonHeartbeatEvent_TakesTheNormalPath()
    {
        var ctx = CreateHeartbeatContext(eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler();
        var response = new CountingResponseService();
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.HeartbeatCalls);
        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
    }

    private static FakeMessageContext CreateHeartbeatContext(
        string eventTypeId = Heartbeat.EventTypeId,
        string eventJson = "{}")
    {
        return new FakeMessageContext
        {
            EventId = "event-1",
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            SessionId = "Heartbeat",
            ParentMessageId = "self",
            OriginatingMessageId = "self",
            OriginatingFrom = "Manager",
            From = "Manager",
            To = "AnalyticsEndpoint",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = eventJson,
                },
            },
            EventTypeId = eventTypeId,
            EnqueuedTimeUtc = new DateTime(2026, 8, 12, 9, 0, 0, DateTimeKind.Utc),
        };
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeEventContextHandler : IEventContextHandler
    {
        private readonly List<string>? _trace;

        public FakeEventContextHandler(List<string>? trace = null)
        {
            _trace = trace;
        }

        public int HandleCalls { get; private set; }
        public Exception ThrowOnHandle { get; set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            _trace?.Add("handler");
            HandleCalls++;
            if (ThrowOnHandle != null)
                throw ThrowOnHandle;
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysProcessedInboxStore : IInboxStore
    {
        private readonly List<string>? _trace;

        public AlwaysProcessedInboxStore(List<string>? trace = null)
        {
            _trace = trace;
        }

        public Task<bool> HasProcessedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default)
        {
            _trace?.Add("inbox-check");
            return Task.FromResult(true);
        }

        public Task RecordProcessedAsync(string endpointId, string messageId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(string endpointId, DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class CountingResponseService : IResponseService
    {
        private readonly List<string>? _trace;

        public CountingResponseService(List<string>? trace = null)
        {
            _trace = trace;
        }

        public int HeartbeatCalls { get; private set; }
        public int ResolutionCalls { get; private set; }
        public int UnsupportedCalls { get; private set; }
        public int DuplicateCalls { get; private set; }
        public int DeferralCalls { get; private set; }
        public int SendToDeferredSubscriptionCalls { get; private set; }
        public Action? AfterHeartbeat { get; set; }

        public Task SendHeartbeatResolutionResponse(IMessageContext mc, CancellationToken ct = default) { _trace?.Add("response-heartbeat"); HeartbeatCalls++; AfterHeartbeat?.Invoke(); return Task.CompletedTask; }
        public Task SendResolutionResponse(IMessageContext mc, CancellationToken ct = default) { ResolutionCalls++; return Task.CompletedTask; }
        public Task SendSkipResponse(IMessageContext mc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDuplicateResponse(IMessageContext mc, CancellationToken ct = default) { _trace?.Add("response-duplicate"); DuplicateCalls++; return Task.CompletedTask; }
        public Task SendErrorResponse(IMessageContext mc, Exception ex, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeadLetterResponse(IMessageContext mc, string reason, Exception? ex, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendDeferralResponse(IMessageContext mc, SessionBlockedException ex, CancellationToken ct = default) { _trace?.Add("response-deferral"); DeferralCalls++; return Task.CompletedTask; }
        public Task SendRetryResponse(IMessageContext mc, int delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendUnsupportedResponse(IMessageContext mc, CancellationToken ct = default) { UnsupportedCalls++; return Task.CompletedTask; }
        public Task SendContinuationRequestToSelf(IMessageContext mc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendToDeferredSubscription(IMessageContext mc, int seq, CancellationToken ct = default) { _trace?.Add("response-deferred-forward"); SendToDeferredSubscriptionCalls++; return Task.CompletedTask; }
        public Task SendProcessDeferredRequest(IMessageContext mc, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPendingHandoffResponse(IMessageContext mc, HandoffMetadata handoff, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeMessageContext : IMessageContext
    {
        public List<string>? Trace { get; set; }
        public string EventId { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public MessageType MessageType { get; set; }
        public MessageContent MessageContent { get; set; } = new();
        public string ParentMessageId { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public int? RetryCount { get; set; }
        public string OriginatingFrom { get; set; } = string.Empty;
        public string EventTypeId { get; set; } = string.Empty;
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public string From { get; set; } = string.Empty;
        public string DeadLetterReason { get; set; }
        public string DeadLetterErrorDescription { get; set; }
        public string HandoffReason { get; set; }
        public string ExternalJobId { get; set; }
        public DateTime? ExpectedBy { get; set; }
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata HandoffMetadata { get; set; }

        public string BlockedByEventId { get; set; }
        public int CompletedCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int AbandonCalls { get; private set; }
        public int BlockSessionCalls { get; private set; }
        public int UnblockSessionCalls { get; private set; }

        // Like the Service Bus SDK, a completion made with a cancelled token fails.
        public Task Complete(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); Trace?.Add("complete"); CompletedCalls++; return Task.CompletedTask; }
        public Task Abandon(TransientException ex) { AbandonCalls++; return Task.CompletedTask; }
        public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) { DeadLetterCalls++; return Task.CompletedTask; }
        public Task BlockSession(CancellationToken ct = default) { Trace?.Add("block"); BlockSessionCalls++; return Task.CompletedTask; }
        public Task UnblockSession(CancellationToken ct = default) { Trace?.Add("unblock"); UnblockSessionCalls++; return Task.CompletedTask; }
        public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(BlockedByEventId));
        public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) { Trace?.Add("verify-owner"); return Task.FromResult(false); }
        public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(BlockedByEventId));
        public Task<string> GetBlockedByEventId(CancellationToken ct = default) { Trace?.Add("session-guard"); return Task.FromResult(BlockedByEventId); }
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken ct = default) { Trace?.Add("deferred-count-increment"); return Task.CompletedTask; }
        public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
    }
}
