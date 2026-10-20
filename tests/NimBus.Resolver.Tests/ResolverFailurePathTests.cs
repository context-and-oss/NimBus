#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;
using FakeCosmosDbClient = NimBus.Resolver.Tests.ResolverServiceTests.FakeCosmosDbClient;
using RecordingNotifier = NimBus.Resolver.Tests.ResolverHeartbeatTests.RecordingNotifier;
using static NimBus.Resolver.Tests.ResolverTestMessages;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The Resolver paths that settle a message as something other than a plain audit write:
/// the dead-lettered projection every permanent subscriber failure takes, the settlement of
/// failures the store did not cause, and the best-effort side writes that must never turn a
/// recorded outcome into a redelivery.
/// </summary>
[TestClass]
public class ResolverFailurePathTests
{
    private const string Endpoint = "BillingEndpoint";

    // ── Dead-lettered projection ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Handle_DeadLetterResponse_ProjectsDeadLetteredNotFailed()
    {
        // ResponseService.SendDeadLetterResponse routes a permanent failure to the Resolver as
        // an ErrorResponse carrying the dead-letter properties. The description decides the
        // outcome: the row must read DeadLettered, not Failed, or the WebApp offers a resubmit
        // of a message the subscriber already gave up on.
        var store = new FakeCosmosDbClient();
        var message = CreateMessageContext(MessageType.ErrorResponse, to: Constants.ResolverId, from: Endpoint);
        message.DeadLetterReason = "PermanentFailure";
        message.DeadLetterErrorDescription = "System.FormatException: bad payload";
        var service = new ResolverService(store);

        await service.Handle(message);

        Assert.AreEqual(0, store.FailedUploads.Count, "A dead-letter response must not be projected as Failed.");
        Assert.AreEqual(1, store.DeadLetteredUploads.Count);
        var upload = store.DeadLetteredUploads[0];
        Assert.AreEqual(Endpoint, upload.EndpointId);
        Assert.AreEqual(ResolutionStatus.DeadLettered, upload.Content.ResolutionStatus);
        Assert.AreEqual("PermanentFailure", upload.Content.DeadLetterReason);
        Assert.AreEqual("System.FormatException: bad payload", upload.Content.DeadLetterErrorDescription);
        Assert.AreEqual("System.FormatException: bad payload", upload.Content.Reason,
            "The description is the operator-facing reason on a dead-lettered row.");
        Assert.AreEqual(1, store.StoredMessages.Count, "The response still lands in the event history.");
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls, "The Resolver records the dead-letter; it does not dead-letter its own copy.");
    }

    [TestMethod]
    [DataRow(MessageType.ResolutionResponse)]
    [DataRow(MessageType.DeferralResponse)]
    public async Task Handle_AnyResponseCarryingDeadLetterDescription_IsProjectedAsDeadLettered(MessageType messageType)
    {
        // The description short-circuits the message-type map regardless of type.
        var store = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType, to: Constants.ResolverId, from: Endpoint);
        message.DeadLetterErrorDescription = "dead-lettered by the subscriber";
        var service = new ResolverService(store);

        await service.Handle(message);

        Assert.AreEqual(1, store.DeadLetteredUploads.Count);
        Assert.AreEqual(0, store.CompletedUploads.Count);
        Assert.AreEqual(0, store.DeferredUploads.Count);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_DeadLetterResponse_NotifiesEndpointStateChange()
    {
        var notifier = new RecordingNotifier();
        var message = CreateMessageContext(MessageType.ErrorResponse, to: Constants.ResolverId, from: Endpoint);
        message.DeadLetterErrorDescription = "boom";
        var service = new ResolverService(new FakeCosmosDbClient(), notifier);

        await service.Handle(message);

        CollectionAssert.AreEqual(new[] { Endpoint }, notifier.EndpointIds);
    }

    // ── Failures the store did not cause ──────────────────────────────────────────────

    [TestMethod]
    public async Task Handle_TransientException_AbandonsWithoutConsumingTheStoreBudget()
    {
        // A TransientException is not a store failure: it is abandoned for a plain broker
        // redelivery, never rescheduled (which would burn the store-retry budget) and never
        // dead-lettered.
        var store = new FakeCosmosDbClient { StoreMessageException = new TransientException("service bus hiccup") };
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        var service = new ResolverService(store);

        await service.Handle(message);

        Assert.AreEqual(1, message.AbandonCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.CompletedCalls);
    }

    [TestMethod]
    [DataRow(MessageType.Unknown)]
    [DataRow(MessageType.ProcessDeferredRequest)]
    public async Task Handle_MessageTypeWithoutAProjection_IsDeadLettered(MessageType messageType)
    {
        // Neither type reaches the Resolver today: the fan-out subscription forwards only
        // To=Resolver and To=<endpoint>, and ProcessDeferredRequest is addressed to the
        // DeferredProcessor. If routing ever changes, the copy must fail loudly rather than
        // be recorded under a made-up status.
        var store = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType, to: Constants.ResolverId, from: Endpoint);
        var service = new ResolverService(store);

        await service.Handle(message);

        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("Failed to handle message.", message.LastDeadLetterReason);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, store.PendingUploads.Count + store.CompletedUploads.Count + store.FailedUploads.Count
            + store.DeferredUploads.Count + store.DeadLetteredUploads.Count + store.UnsupportedUploads.Count
            + store.SkippedUploads.Count);
    }

    // ── Best-effort side writes ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Handle_EndpointNotificationFailure_StillCompletes()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new ThrowingEndpointNotifier(new InvalidOperationException("SignalR hub down"));
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        var service = new ResolverService(store, notifier);

        await service.Handle(message);

        Assert.AreEqual(1, store.PendingUploads.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_StaleCopyAuditCancelledByShutdown_RethrowsWithoutSettling()
    {
        // The stale-copy audit swallows its own failures, but host shutdown is not a failure:
        // the message stays unsettled so the transport can redeliver it later.
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = new FakeCosmosDbClient
        {
            PendingUploadResult = false,
            StoreAuditException = new OperationCanceledException(cancellation.Token),
        };
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        var service = new ResolverService(store);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.Handle(message, cancellation.Token));

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_TerminalAfterHandoffWithoutEventRequestInHistory_KeepsHandlerProcessingTime()
    {
        // The wall-clock override needs the original EventRequest's enqueue time. Without it
        // (history trimmed, or the request copy never recorded) the handler's own timing stands.
        var store = new FakeCosmosDbClient();
        store.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "handoff-1",
            MessageType = MessageType.PendingHandoffResponse,
            EndpointId = Endpoint,
        });
        var message = CreateMessageContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint);
        message.ProcessingTimeMs = 42;
        var service = new ResolverService(store);

        await service.Handle(message);

        Assert.AreEqual(1, store.CompletedUploads.Count);
        Assert.AreEqual(42L, store.CompletedUploads[0].Content.ProcessingTimeMs);
    }

    // ── Heartbeat payload edge cases ───────────────────────────────────────────────────

    [TestMethod]
    [DataRow("")]
    [DataRow("{ not json")]
    [DataRow("null")]
    public async Task Handle_HeartbeatResponseWithUnreadablePayload_StillRecordsTheAnswerUnderFrom(string eventJson)
    {
        // A malformed probe payload still proves the endpoint answered: attribution falls back
        // to From and the timings degrade to the enqueue/receive clock instead of failing.
        var store = new FakeCosmosDbClient();
        var message = ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint);
        message.MessageContent.EventContent!.EventJson = eventJson;
        var service = CreateHeartbeatService(store);

        await service.Handle(message);

        Assert.AreEqual(1, store.WrittenHeartbeats.Count);
        Assert.AreEqual(Endpoint, store.WrittenHeartbeats[0].EndpointId);
        Assert.AreEqual(HeartbeatStatus.On, store.WrittenHeartbeats[0].Heartbeat.EndpointHeartbeatStatus);
        Assert.AreEqual(string.Empty, store.WrittenHeartbeats[0].Heartbeat.SdkVersion);
        Assert.AreEqual(message.EnqueuedTimeUtc, store.WrittenHeartbeats[0].Heartbeat.StartTime);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatNotificationCancelledByShutdown_RethrowsWithoutCompleting()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = new FakeCosmosDbClient();
        var notifier = new RecordingNotifier { HeartbeatException = new OperationCanceledException(cancellation.Token) };
        var message = ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint);
        var service = CreateHeartbeatService(store, notifier);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.Handle(message, cancellation.Token));

        Assert.AreEqual(1, store.WrittenHeartbeats.Count);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_SelfProbeNotificationFailure_StillCompletes()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new RecordingNotifier { ServiceHealthException = new InvalidOperationException("hub down") };
        var message = CreateProbe(new CoreHeartbeat { ForwardSendTime = DateTime.UtcNow });
        var service = CreateHeartbeatService(store, notifier);

        await service.Handle(message);

        Assert.AreEqual(1, store.WrittenServiceHealth.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_SelfProbeNotificationCancelledByShutdown_RethrowsWithoutCompleting()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = new FakeCosmosDbClient();
        var notifier = new RecordingNotifier { ServiceHealthException = new OperationCanceledException(cancellation.Token) };
        var message = CreateProbe(new CoreHeartbeat { ForwardSendTime = DateTime.UtcNow });
        var service = CreateHeartbeatService(store, notifier);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.Handle(message, cancellation.Token));

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_SelfProbeWithoutSendTime_FallsBackToEnqueuedTimeForRoundTrip()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateProbe(new CoreHeartbeat());
        message.EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-2);
        var service = CreateHeartbeatService(store);

        await service.Handle(message);

        var roundTrip = store.WrittenServiceHealth[0].RoundTripMs;
        Assert.IsNotNull(roundTrip);
        Assert.IsTrue(roundTrip >= 1_500, $"Round trip should be measured from the enqueue time; got {roundTrip}.");
    }

    [TestMethod]
    public async Task Handle_SelfProbeWithoutAnyTimestamp_RecordsNoRoundTrip()
    {
        // Neither the payload nor the broker supplied a send time: a round trip measured from
        // DateTime.MinValue would be nonsense, so none is recorded.
        var store = new FakeCosmosDbClient();
        var message = CreateProbe(new CoreHeartbeat());
        message.EnqueuedTimeUtc = default;
        var service = CreateHeartbeatService(store);

        await service.Handle(message);

        Assert.IsNull(store.WrittenServiceHealth[0].RoundTripMs);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    private static ResolverService CreateHeartbeatService(FakeCosmosDbClient store, IMessageStateChangeNotifier? notifier = null) =>
        new(store, notifier ?? new NoopMessageStateChangeNotifier(), logger: null!, metadataStore: store, serviceHealthStore: store);

    private static FakeMessageContext CreateProbe(CoreHeartbeat payload)
    {
        var message = ResolverHeartbeatTests.CreateHeartbeatContext(
            MessageType.EventRequest, to: Constants.ResolverId, from: Constants.ManagerId, payload: payload);
        message.MessageContent.EventContent!.EventJson = JsonConvert.SerializeObject(payload);
        return message;
    }

    private sealed class ThrowingEndpointNotifier : IMessageStateChangeNotifier
    {
        private readonly Exception _exception;

        public ThrowingEndpointNotifier(Exception exception) => _exception = exception;

        public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default) =>
            Task.FromException(_exception);
    }
}
