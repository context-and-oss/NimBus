#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;
using FakeCosmosDbClient = NimBus.Resolver.Tests.ResolverServiceTests.FakeCosmosDbClient;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The Resolver diverts platform heartbeat traffic before anything reaches the
/// tracking store: an answer updates the heartbeat store, a request copy is dropped,
/// and neither ever becomes an audit row on the Events / Flow / Monitor pages.
/// </summary>
[TestClass]
public class ResolverHeartbeatTests
{
    [TestMethod]
    public async Task Handle_HeartbeatRequestCopyForEndpoint_CompletesWithoutWritingAnything()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(MessageType.EventRequest, to: "BillingEndpoint", from: "Manager");
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(0, store.WrittenHeartbeats.Count, "A request copy carries no answer to record.");
        Assert.AreEqual(0, store.WrittenServiceHealth.Count);
        Assert.AreEqual(0, store.StoredMessages.Count, "Heartbeat traffic must never reach the audit trail.");
        Assert.AreEqual(0, store.PendingUploads.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    [DataRow(MessageType.ResolutionResponse, HeartbeatStatus.On)]
    [DataRow(MessageType.UnsupportedResponse, HeartbeatStatus.Unsupported)]
    [DataRow(MessageType.ErrorResponse, HeartbeatStatus.Off)]
    [DataRow(MessageType.DeferralResponse, HeartbeatStatus.Off)]
    [DataRow(MessageType.SkipResponse, HeartbeatStatus.Unknown)]
    public async Task Handle_HeartbeatResponse_MapsMessageTypeToStatus(MessageType messageType, HeartbeatStatus expected)
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(messageType, to: Constants.ResolverId, from: "BillingEndpoint");
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(1, store.WrittenHeartbeats.Count);
        Assert.AreEqual(expected, store.WrittenHeartbeats[0].Heartbeat.EndpointHeartbeatStatus);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_AttributesToPayloadEndpointOverFrom()
    {
        // ResponseService.CreateResponse is static and does not stamp From, so the
        // payload's Endpoint is the authoritative attribution.
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(
            MessageType.ResolutionResponse,
            to: Constants.ResolverId,
            from: "SomeOtherEndpoint",
            payload: new CoreHeartbeat { Endpoint = "BillingEndpoint" });
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual("BillingEndpoint", store.WrittenHeartbeats[0].EndpointId);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponseWithoutPayloadEndpoint_FallsBackToFrom()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(
            MessageType.ResolutionResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint",
            payload: new CoreHeartbeat { Endpoint = "  " });
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual("BillingEndpoint", store.WrittenHeartbeats[0].EndpointId);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponseWithoutAnyEndpoint_CompletesWithoutWriting()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(
            MessageType.ResolutionResponse,
            to: Constants.ResolverId,
            from: "",
            payload: new CoreHeartbeat());
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(0, store.WrittenHeartbeats.Count);
        Assert.AreEqual(1, message.CompletedCalls, "An unattributable heartbeat is still settled, never dead-lettered.");
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_KeysRowByCorrelationId()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        message.CorrelationId = "probe-42";
        message.MessageId = "response-99";
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual("probe-42", store.WrittenHeartbeats[0].Heartbeat.MessageId,
            "The row must merge with the Pending probe the sender wrote under the correlation id.");
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponseWithoutCorrelationId_KeysRowByMessageId()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        message.CorrelationId = "";
        message.MessageId = "response-99";
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual("response-99", store.WrittenHeartbeats[0].Heartbeat.MessageId);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_CarriesTimingsAndSdkVersion()
    {
        var store = new FakeCosmosDbClient();
        var sentAt = DateTime.UtcNow.AddSeconds(-5);
        var receivedAt = sentAt.AddMilliseconds(120);
        var message = CreateHeartbeatContext(
            MessageType.ResolutionResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint",
            payload: new CoreHeartbeat
            {
                Endpoint = "BillingEndpoint",
                ForwardSendTime = sentAt,
                ForwardReceivedTime = receivedAt,
                SdkVersion = "1.2.3",
            });
        var service = CreateService(store);

        await service.Handle(message);

        var written = store.WrittenHeartbeats[0].Heartbeat;
        Assert.AreEqual(sentAt, written.StartTime);
        Assert.AreEqual(receivedAt, written.ReceivedTime);
        Assert.AreEqual("1.2.3", written.SdkVersion);
        Assert.IsTrue(written.EndTime >= receivedAt, "EndTime is stamped when the Resolver settles the probe.");
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponseWithoutPayloadTimestamps_FallsBackToEnqueuedTime()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(
            MessageType.ResolutionResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint",
            payload: new CoreHeartbeat { Endpoint = "BillingEndpoint" });
        var service = CreateService(store);

        await service.Handle(message);

        var written = store.WrittenHeartbeats[0].Heartbeat;
        Assert.AreEqual(message.EnqueuedTimeUtc, written.StartTime);
        Assert.AreEqual(string.Empty, written.SdkVersion, "A pre-heartbeat SDK reports no version.");
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_TransientStorageFailure_LeavesMessageUnsettled()
    {
        var store = new FakeCosmosDbClient
        {
            SetHeartbeatException = new StorageProviderTransientException("throttled", TimeSpan.FromSeconds(3)),
        };
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(0, message.CompletedCalls, "The session must redeliver the answer.");
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls, "Heartbeats skip the scheduled-redelivery path.");
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.AbandonCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_CosmosThrottle_RetriesInPlaceInsteadOfRescheduling()
    {
        // A rescheduled copy lands minutes later and would overwrite a newer heartbeat; the next
        // heartbeat supersedes this one anyway, so the session simply redelivers it.
        var store = new FakeCosmosDbClient
        {
            SetHeartbeatException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(0, message.ScheduleRedeliveryCalls, "Heartbeats skip the scheduled-redelivery path.");
        Assert.AreEqual(0, message.CompletedCalls, "The session must redeliver the answer.");
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_FinalCosmosThrottle_UsesStableDeadLetterReason()
    {
        var store = new FakeCosmosDbClient
        {
            SetHeartbeatException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateHeartbeatContext(
            MessageType.ResolutionResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint");
        message.ThrottleRetryCount = 9;
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("CosmosDbThrottled", message.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_WithoutHeartbeatStore_CompletesWithoutThrowing()
    {
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        var service = new ResolverService(store, new NoopMessageStateChangeNotifier());

        await service.Handle(message);

        Assert.AreEqual(0, store.WrittenHeartbeats.Count);
        Assert.AreEqual(0, store.StoredMessages.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_NotifiesHeartbeatChangeOnSuccess()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new RecordingNotifier();
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        var service = CreateService(store, notifier);

        await service.Handle(message);

        Assert.AreEqual(1, notifier.HeartbeatIds.Count);
        Assert.AreEqual("BillingEndpoint", notifier.HeartbeatIds[0]);
        Assert.AreEqual(0, notifier.EndpointIds.Count, "The endpoint-state hook is for event traffic only.");
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatResponse_FailedNotificationStillCompletes()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new RecordingNotifier { HeartbeatException = new InvalidOperationException("hub down") };
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        var service = CreateService(store, notifier);

        await service.Handle(message);

        Assert.AreEqual(1, store.WrittenHeartbeats.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_HeartbeatIdentifiedOnlyByEventContent_IsStillDiverted()
    {
        // The divert reads EventTypeId with a fall back to the event content, so a
        // message that carries the id only in its payload envelope is caught too.
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        message.EventTypeId = null!;
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(1, store.WrittenHeartbeats.Count);
        Assert.AreEqual(0, store.StoredMessages.Count);
    }

    [TestMethod]
    public async Task Handle_NonHeartbeatResponse_StillFlowsThroughTheNormalPath()
    {
        // Guard against the divert swallowing ordinary traffic.
        var store = new FakeCosmosDbClient();
        var message = CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint");
        message.EventTypeId = "OrderPlaced";
        message.MessageContent.EventContent!.EventTypeId = "OrderPlaced";
        var service = CreateService(store);

        await service.Handle(message);

        Assert.AreEqual(0, store.WrittenHeartbeats.Count);
        Assert.AreEqual(1, store.StoredMessages.Count);
        Assert.AreEqual(1, store.CompletedUploads.Count);
    }

    private static ResolverService CreateService(
        INimBusMessageStore store,
        IMessageStateChangeNotifier? notifier = null) =>
        new(store, notifier ?? new NoopMessageStateChangeNotifier(), logger: null, metadataStore: store, serviceHealthStore: store);

    internal static FakeMessageContext CreateHeartbeatContext(
        MessageType messageType,
        string to,
        string from,
        CoreHeartbeat? payload = null)
    {
        payload ??= new CoreHeartbeat { Endpoint = from };

        return new FakeMessageContext
        {
            EventId = "event-heartbeat",
            MessageId = "message-heartbeat",
            CorrelationId = "correlation-heartbeat",
            SessionId = "Heartbeat",
            ParentMessageId = "self",
            OriginatingMessageId = "self",
            OriginatingFrom = from,
            From = from,
            To = to,
            MessageType = messageType,
            EventTypeId = CoreHeartbeat.EventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = CoreHeartbeat.EventTypeId,
                    EventJson = JsonConvert.SerializeObject(payload),
                },
            },
            EnqueuedTimeUtc = new DateTime(2026, 08, 12, 09, 00, 00, DateTimeKind.Utc),
        };
    }

    /// <summary>Records which notifications fired, and optionally fails them.</summary>
    internal sealed class RecordingNotifier : IMessageStateChangeNotifier
    {
        public List<string> EndpointIds { get; } = new();
        public List<string> HeartbeatIds { get; } = new();
        public List<string> ServiceIds { get; } = new();
        public Exception? HeartbeatException { get; set; }
        public Exception? ServiceHealthException { get; set; }

        public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            EndpointIds.Add(endpointId);
            return Task.CompletedTask;
        }

        public Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            HeartbeatIds.Add(endpointId);
            return HeartbeatException is null ? Task.CompletedTask : Task.FromException(HeartbeatException);
        }

        public Task NotifyServiceHealthChangedAsync(string serviceId, CancellationToken cancellationToken = default)
        {
            ServiceIds.Add(serviceId);
            return ServiceHealthException is null ? Task.CompletedTask : Task.FromException(ServiceHealthException);
        }
    }
}
