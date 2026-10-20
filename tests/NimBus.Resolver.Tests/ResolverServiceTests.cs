#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Core.Diagnostics;
using static NimBus.Resolver.Tests.ResolverTestMessages;

namespace NimBus.Resolver.Tests;

[TestClass]
public class ResolverServiceTests
{
    [TestMethod]
    public void DetermineEndpoint_UsesToForRequestMessages()
    {
        var service = CreateService();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint", from: "StorefrontEndpoint");

        var (endpointId, role) = service.DetermineEndpoint(message);

        Assert.AreEqual("BillingEndpoint", endpointId);
        Assert.AreEqual(EndpointRole.Subscriber, role);
    }

    [TestMethod]
    public void DetermineEndpoint_UsesFromForResponseMessages()
    {
        var service = CreateService();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver", from: "BillingEndpoint");

        var (endpointId, role) = service.DetermineEndpoint(message);

        Assert.AreEqual("BillingEndpoint", endpointId);
        Assert.AreEqual(EndpointRole.Subscriber, role);
    }

    [TestMethod]
    public async Task Handle_EventRequest_StoresMessageUploadsPendingAndCompletes()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint", from: "StorefrontEndpoint");
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.StoredMessages.Count);
        Assert.AreEqual("BillingEndpoint", cosmos.StoredMessages[0].EndpointId);
        Assert.AreEqual("OrderPlaced", cosmos.StoredMessages[0].EventTypeId);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual("BillingEndpoint", cosmos.PendingUploads[0].EndpointId);
        Assert.AreEqual(ResolutionStatus.Pending, cosmos.PendingUploads[0].Content.ResolutionStatus);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Handle_EventRequestWithoutFrom_StillRecordsTheAuditCopy()
    {
        // A request put on an endpoint topic by hand (e.g. resubmitted from a DLQ with a
        // broker tool) bypasses the forward rule that stamps From. A request is attributed
        // by To, so the audit copy must be recorded rather than dead-lettered.
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint", from: "StorefrontEndpoint");
        message.FromIsMissing = true;
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.StoredMessages.Count);
        Assert.AreEqual("BillingEndpoint", cosmos.StoredMessages[0].EndpointId);
        Assert.AreEqual("StorefrontEndpoint", cosmos.StoredMessages[0].From);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_ReplayedRequestWithDeferredMarker_RecordsTheOriginatorNotTheMarker()
    {
        // A From-less request that was parked replays with the "Deferred" marker as From.
        // Recording it would surface "Deferred" as a publishing endpoint in the metrics.
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint", from: "StorefrontEndpoint");
        message.From = Constants.DeferredSubscriptionName;
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual("StorefrontEndpoint", cosmos.StoredMessages[0].From);
    }

    [TestMethod]
    public async Task Handle_EventRequestWithoutFromOrOriginator_RecordsSelfNotNull()
    {
        // Stored rows have always had a From; readers are not written for null.
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.EventRequest, to: "BillingEndpoint");
        message.FromIsMissing = true;
        message.OriginatingFrom = null!;
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(Constants.Self, cosmos.StoredMessages[0].From);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_ResponseWithoutFrom_IsStillRejected()
    {
        // A response is attributed to an endpoint by From; without it there is no row to update.
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver");
        message.FromIsMissing = true;
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(0, cosmos.StoredMessages.Count);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual(0, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_DynamicallyTypedEvent_RecordsPendingThenCompleted_KeyedByEventTypeId()
    {
        // Spec 022 Phase 0: the Resolver/audit trail must work for an event identified only by a
        // dynamic EventTypeId string (e.g. "crm.contact.enriched.v1") with no compiled C# IEvent.
        // The Resolver keys purely off strings, so the agent zone's dynamically-typed events are
        // first-class in the audit trail with no special casing.
        const string dynamicEventTypeId = "crm.contact.enriched.v1";
        var cosmos = new FakeCosmosDbClient();
        var service = CreateService(cosmos);

        var request = CreateMessageContext(
            messageType: MessageType.EventRequest,
            to: "AgentZoneEndpoint",
            from: "CrmEndpoint",
            eventTypeId: dynamicEventTypeId);

        await service.Handle(request);

        Assert.AreEqual(1, cosmos.StoredMessages.Count);
        Assert.AreEqual(dynamicEventTypeId, cosmos.StoredMessages[0].EventTypeId, "Audit row must carry the dynamic EventTypeId.");
        Assert.AreEqual("AgentZoneEndpoint", cosmos.StoredMessages[0].EndpointId);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual(ResolutionStatus.Pending, cosmos.PendingUploads[0].Content.ResolutionStatus);

        // The subscriber's ResolutionResponse flips the same dynamic event to Completed.
        var response = CreateMessageContext(
            messageType: MessageType.ResolutionResponse,
            to: "Resolver",
            from: "AgentZoneEndpoint",
            eventTypeId: dynamicEventTypeId);

        await service.Handle(response);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count);
        Assert.AreEqual("event-1", cosmos.CompletedUploads[0].EventId);
    }

    [TestMethod]
    public async Task Handle_CloudEventResponse_PersistsCloudEventIdentityOnTrackingRecord()
    {
        // AC15: the CloudEvents identity carried on the response from a CloudEvents-
        // consuming subscriber must be projected onto the tracking record (the
        // UnresolvedEvent the WebApp surfaces) and the per-message audit row.
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver", from: "BillingEndpoint");
        message.CloudEventId = "ce-1";
        message.CloudEventSource = "urn:ext:billing";
        message.CloudEventType = "InvoiceCreated";
        message.CloudEventSubject = "customer-42";
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count);
        var tracked = cosmos.CompletedUploads[0].Content;
        Assert.AreEqual("ce-1", tracked.CloudEventId);
        Assert.AreEqual("urn:ext:billing", tracked.CloudEventSource);
        Assert.AreEqual("InvoiceCreated", tracked.CloudEventType);
        Assert.AreEqual("customer-42", tracked.CloudEventSubject);

        // The per-message audit row (MessageEntity) carries it too.
        Assert.AreEqual("ce-1", cosmos.StoredMessages[0].CloudEventId);
        Assert.AreEqual("InvoiceCreated", cosmos.StoredMessages[0].CloudEventType);
    }

    [TestMethod]
    public async Task Handle_NativeResponse_LeavesCloudEventIdentityNull()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.ResolutionResponse, to: "Resolver", from: "BillingEndpoint");
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.IsNull(cosmos.CompletedUploads[0].Content.CloudEventId);
        Assert.IsNull(cosmos.CompletedUploads[0].Content.CloudEventType);
        Assert.IsNull(cosmos.StoredMessages[0].CloudEventId);
    }

    [TestMethod]
    public async Task Handle_DiscardSkipResponse_UploadsSkippedOutcomeWithReason()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(
            messageType: MessageType.SkipResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint");
        message.MessageContent.ErrorContent = new ErrorContent
        {
            ErrorText = "InvalidOperationException: Known bad event version. Classified by PartnerFailureDispositionClassifier.",
            ErrorType = nameof(InvalidOperationException),
        };
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.SkippedUploads.Count);
        var tracked = cosmos.SkippedUploads[0].Content;
        Assert.AreEqual(ResolutionStatus.Skipped, tracked.ResolutionStatus);
        Assert.AreEqual(message.MessageContent.ErrorContent.ErrorText, tracked.Reason);
        Assert.AreEqual(nameof(InvalidOperationException), tracked.MessageContent.ErrorContent!.ErrorType);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_DuplicateSkipResponse_UploadsSkippedOutcomeWithStableReason()
    {
        const string duplicateReason = "DuplicateDetected";
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(
            messageType: MessageType.SkipResponse,
            to: Constants.ResolverId,
            from: "BillingEndpoint");
        message.MessageContent.ErrorContent = new ErrorContent
        {
            ErrorText = duplicateReason,
        };
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.SkippedUploads.Count);
        var tracked = cosmos.SkippedUploads[0].Content;
        Assert.AreEqual(ResolutionStatus.Skipped, tracked.ResolutionStatus);
        Assert.AreEqual(duplicateReason, tracked.Reason);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "StoreMessage throttled")]
    [DataRow(false, DisplayName = "Pending upload throttled")]
    public async Task Handle_RetryRequest_ThrottledThenRedelivered_WritesRetryAuditOnce(bool failStoreMessage)
    {
        // StoreMessageAudit is a plain insert. A retry audit written before the event writes
        // succeed is written again by the rescheduled copy, doubling the Retry row per throttle.
        var throttle = new RequestLimitException(TimeSpan.FromSeconds(1));
        var cosmos = new FakeCosmosDbClient();
        if (failStoreMessage) cosmos.StoreMessageException = throttle;
        else cosmos.UploadException = throttle;
        var service = CreateService(cosmos);

        var firstDelivery = CreateMessageContext(messageType: MessageType.RetryRequest, to: "BillingEndpoint", from: "Manager");
        await service.Handle(firstDelivery);

        Assert.AreEqual(1, firstDelivery.ScheduleRedeliveryCalls);
        Assert.AreEqual(0, cosmos.StoredAudits.Count, "No retry audit may be written while the event writes are failing.");

        cosmos.StoreMessageException = null;
        cosmos.UploadException = null;
        var redelivered = CreateMessageContext(messageType: MessageType.RetryRequest, to: "BillingEndpoint", from: "Manager",
            eventId: firstDelivery.EventId, messageId: firstDelivery.MessageId, throttleRetryCount: 1);
        await service.Handle(redelivered);

        Assert.AreEqual(1, cosmos.StoredAudits.Count);
        Assert.AreEqual(MessageAuditType.Retry, cosmos.StoredAudits[0].Audit.AuditType);
        Assert.AreEqual(1, redelivered.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_RetryRequest_StoresRetryAudit()
    {
        var cosmos = new FakeCosmosDbClient();
        var message = CreateMessageContext(messageType: MessageType.RetryRequest, to: "BillingEndpoint", from: "Manager");
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, cosmos.StoredAudits.Count);
        Assert.AreEqual(message.EventId, cosmos.StoredAudits[0].EventId);
        Assert.AreEqual(MessageAuditType.Retry, cosmos.StoredAudits[0].Audit.AuditType);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_SchedulesRedeliveryWithIncrementedRetryCount()
    {
        var retryAfter = TimeSpan.FromSeconds(17);
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(retryAfter),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 2);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(20), message.LastScheduledDelay);
        Assert.AreEqual(3, message.LastScheduledRetryCount);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_StorageProviderTransientExceptionWithoutRetryAfter_UsesCalculatedBackoff()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new StorageProviderTransientException("temporarily unavailable", retryAfter: null),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 1);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(10), message.LastScheduledDelay);
        Assert.AreEqual(2, message.LastScheduledRetryCount);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_DeadLettersWhenMaxRetriesReached()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 9);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("CosmosDbThrottled", message.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_CombinesScheduledAndBrokerDeliveryAttempts()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateMessageContext(
            messageType: MessageType.EventRequest,
            throttleRetryCount: 3,
            deliveryCount: 2);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(5, message.LastScheduledRetryCount);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_RequestLimitExceptionDuringEndpointProjection_UsesStableFinalReason()
    {
        var cosmos = new FakeCosmosDbClient
        {
            UploadException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateMessageContext(
            messageType: MessageType.EventRequest,
            throttleRetryCount: 8,
            deliveryCount: 2);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("CosmosDbThrottled", message.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_TerminalAfterHandoff_OverridesProcessingTimeWithWallClock()
    {
        // Simulates the lifecycle of a handoff event:
        //   T0       — EventRequest enqueued (the wall-clock anchor)
        //   T0+200ms — PendingHandoffResponse audit row arrives
        //   ...long external job runs...
        //   now()    — Final ResolutionResponse settles the event
        // The aggregate UnresolvedEvent's ProcessingTimeMs must reflect the
        // full wall-clock span (now − EventRequest.EnqueuedTimeUtc), not the
        // 250ms handler duration of the final hop.
        var cosmos = new FakeCosmosDbClient();
        var eventRequestEnqueued = DateTime.UtcNow.AddSeconds(-30);

        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-event-request",
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = eventRequestEnqueued,
            EndpointId = "BillingEndpoint",
        });
        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-pending-handoff",
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = eventRequestEnqueued.AddMilliseconds(200),
            EndpointId = "BillingEndpoint",
            PendingSubStatus = "Handoff",
        });

        var resolutionResponse = CreateMessageContext(
            messageType: MessageType.ResolutionResponse,
            to: "Resolver",
            from: "BillingEndpoint");
        resolutionResponse.ProcessingTimeMs = 250; // raw last-hop handler duration

        var service = CreateService(cosmos);
        await service.Handle(resolutionResponse);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count, "Terminal upload should have run.");
        var completed = cosmos.CompletedUploads[0].Content;
        Assert.IsNotNull(completed.ProcessingTimeMs, "Wall-clock value must be populated.");
        Assert.IsTrue(completed.ProcessingTimeMs >= 30_000,
            $"Expected wall-clock ≥ 30s; got {completed.ProcessingTimeMs}ms.");
        Assert.IsTrue(completed.ProcessingTimeMs < 60_000,
            $"Wall-clock should be the EventRequest→now span, not far longer; got {completed.ProcessingTimeMs}ms.");

        // Per-message audit row preserves the raw last-hop duration.
        var resolutionRow = cosmos.StoredMessages.Single(m => m.MessageType == MessageType.ResolutionResponse);
        Assert.AreEqual(250, resolutionRow.ProcessingTimeMs);
    }

    [TestMethod]
    public async Task Handle_TerminalWithoutHandoff_KeepsHandlerProcessingTime()
    {
        // Non-handoff event: history has only the EventRequest. The override
        // must not trigger; the response's local handler duration (75ms) wins.
        var cosmos = new FakeCosmosDbClient();
        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-event-request",
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-30),
            EndpointId = "BillingEndpoint",
        });

        var resolutionResponse = CreateMessageContext(
            messageType: MessageType.ResolutionResponse,
            to: "Resolver",
            from: "BillingEndpoint");
        resolutionResponse.ProcessingTimeMs = 75;

        var service = CreateService(cosmos);
        await service.Handle(resolutionResponse);

        Assert.AreEqual(1, cosmos.CompletedUploads.Count);
        Assert.AreEqual(75, cosmos.CompletedUploads[0].Content.ProcessingTimeMs);
    }

    [TestMethod]
    public async Task Handle_HandoffFailure_OverridesProcessingTimeOnErrorResponse()
    {
        // HandoffFailedRequest path: the terminal ErrorResponse must also use
        // the wall-clock span. Symmetric with the success path above.
        var cosmos = new FakeCosmosDbClient();
        var eventRequestEnqueued = DateTime.UtcNow.AddSeconds(-30);

        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-event-request",
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = eventRequestEnqueued,
            EndpointId = "BillingEndpoint",
        });
        cosmos.StoredMessages.Add(new MessageEntity
        {
            EventId = "event-1",
            MessageId = "msg-pending-handoff",
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = eventRequestEnqueued.AddMilliseconds(200),
            EndpointId = "BillingEndpoint",
            PendingSubStatus = "Handoff",
        });

        var errorResponse = CreateMessageContext(
            messageType: MessageType.ErrorResponse,
            to: "Resolver",
            from: "BillingEndpoint");
        errorResponse.ProcessingTimeMs = 50;

        var service = CreateService(cosmos);
        await service.Handle(errorResponse);

        Assert.AreEqual(1, cosmos.FailedUploads.Count);
        Assert.IsTrue(cosmos.FailedUploads[0].Content.ProcessingTimeMs >= 30_000);
    }

    [TestMethod]
    public async Task Handle_UnexpectedException_DeadLettersMessage()
    {
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new InvalidOperationException("boom"),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        var service = CreateService(cosmos);

        await service.Handle(message);

        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("Failed to handle message.", message.LastDeadLetterReason);
        Assert.AreEqual(0, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_StoreCancellation_RethrowsWithoutSettlingMessage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new OperationCanceledException(cancellation.Token),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        var service = CreateService(cosmos);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.Handle(message, cancellation.Token));

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Handle_NotificationCancellation_RethrowsWithoutCompletingOrDeadLettering()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var notifier = new ThrowingMessageStateChangeNotifier(
            new OperationCanceledException(cancellation.Token));
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        var service = CreateService(notifier: notifier);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.Handle(message, cancellation.Token));

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Handle_CompletionCancellation_RethrowsWithoutDeadLettering()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        message.CompleteException = new OperationCanceledException(cancellation.Token);
        var service = CreateService();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.Handle(message, cancellation.Token));

        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    [DataRow(MessageType.HandoffCompletedRequest)]
    [DataRow(MessageType.HandoffFailedRequest)]
    public async Task Handle_HandoffSettlementRequest_ProjectsPlainPendingRow(MessageType messageType)
    {
        // A settlement request is projected as a plain Pending row (sub-status cleared)
        // until the subscriber's terminal response flips it. The agent zone depends on
        // this: GetAgentReceiveAsync is a non-claiming, oldest-first read of Pending+Handoff
        // rows and HandoffSettlementService gates on that sub-status, so leaving the row
        // Pending+Handoff would re-deliver the just-settled event and admit a second
        // settlement (ADR-012, 2026-09-15 note).
        var cosmos = new FakeCosmosDbClient();
        var notifier = new ResolverHeartbeatTests.RecordingNotifier();
        var message = CreateMessageContext(messageType: messageType, to: "BillingEndpoint", from: "Manager");

        await CreateService(cosmos, notifier).Handle(message);

        Assert.AreEqual(1, cosmos.StoredMessages.Count);
        Assert.AreEqual(messageType, cosmos.StoredMessages[0].MessageType);
        Assert.AreEqual(1, cosmos.PendingUploads.Count);
        var projected = cosmos.PendingUploads[0].Content;
        Assert.AreEqual("BillingEndpoint", cosmos.PendingUploads[0].EndpointId);
        Assert.AreEqual(ResolutionStatus.Pending, projected.ResolutionStatus);
        Assert.AreEqual(messageType, projected.MessageType);
        Assert.IsNull(projected.PendingSubStatus, "The handoff discriminator must be cleared by the settlement projection.");
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        CollectionAssert.AreEqual(new[] { "BillingEndpoint" }, notifier.EndpointIds);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_CountsThrottledRescheduleAndBackoffDelay()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(17)),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 2);

        await CreateService(cosmos).Handle(message);

        AssertStoreRetry(capture, StoreRetryReason.Throttled, RetryAction.Rescheduled);
        AssertRetryDelay(capture, 20, DelaySource.Backoff); // 5 s × 2^2 = 20 s beats the 17 s hint
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_WithLongerRetryAfter_RecordsProviderDelay()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(60)),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest);

        await CreateService(cosmos).Handle(message);

        Assert.AreEqual(TimeSpan.FromSeconds(60), message.LastScheduledDelay);
        AssertStoreRetry(capture, StoreRetryReason.Throttled, RetryAction.Rescheduled);
        AssertRetryDelay(capture, 60, DelaySource.Provider);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_OnFinalAttempt_CountsDeadLettered()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 9);

        await CreateService(cosmos).Handle(message);

        Assert.AreEqual(1, message.DeadLetterCalls);
        AssertStoreRetry(capture, StoreRetryReason.Throttled, RetryAction.DeadLettered);
        Assert.AreEqual(0, capture.HistogramCount(RetryDelayInstrument), "No delay is applied when dead-lettering.");
    }

    [TestMethod]
    public async Task Handle_StorageProviderTransientException_CountsTransientReschedule()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new StorageProviderTransientException("temporarily unavailable", retryAfter: null),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 1);

        await CreateService(cosmos).Handle(message);

        AssertStoreRetry(capture, StoreRetryReason.Transient, RetryAction.Rescheduled);
        AssertRetryDelay(capture, 10, DelaySource.Backoff);
    }

    [TestMethod]
    public async Task Handle_StorageProviderTransientException_SharesTheDeliveryBudgetWithBrokerDeliveries()
    {
        // Transient and throttled failures use one logical attempt count (scheduled re-sends
        // plus broker deliveries), so the two reasons are comparable and neither can stack ten
        // scheduled retries on top of ten broker deliveries.
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new StorageProviderTransientException("temporarily unavailable", retryAfter: null),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest, throttleRetryCount: 7, deliveryCount: 3);

        await CreateService(cosmos).Handle(message);

        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("Max throttle retries exceeded", message.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_RequestLimitException_WhenSchedulingUnavailable_CountsAbandonedWithoutDelay()
    {
        using var capture = ResolverTelemetryCapture.Start();
        var cosmos = new FakeCosmosDbClient
        {
            StoreMessageException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var message = CreateMessageContext(messageType: MessageType.EventRequest);
        message.ScheduleRedeliveryException = new TransientException("Scheduled redelivery not available in current configuration.");

        await CreateService(cosmos).Handle(message);

        Assert.AreEqual(1, message.AbandonCalls);
        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        AssertStoreRetry(capture, StoreRetryReason.Throttled, RetryAction.Abandoned);
        Assert.AreEqual(0, capture.HistogramCount(RetryDelayInstrument), "An abandoned message was not delayed by the Resolver.");
    }

    private const string StoreRetryInstrument = "nimbus.resolver.store_retry";
    private const string RetryDelayInstrument = "nimbus.resolver.retry.delay";

    private static void AssertStoreRetry(ResolverTelemetryCapture capture, string reason, string action, string endpoint = "AnalyticsEndpoint")
    {
        var measurement = capture.Measurements.Single(m => m.Name == StoreRetryInstrument);
        Assert.AreEqual(1, measurement.Value);
        Assert.AreEqual(reason, measurement.Tags[MessagingAttributes.NimBusStoreReason]);
        Assert.AreEqual(action, measurement.Tags[MessagingAttributes.NimBusRetryAction]);
        Assert.AreEqual(endpoint, measurement.Tags[MessagingAttributes.NimBusEndpoint]);
    }

    private static void AssertRetryDelay(ResolverTelemetryCapture capture, double seconds, string source, string endpoint = "AnalyticsEndpoint")
    {
        var observation = capture.Histograms.Single(h => h.Name == RetryDelayInstrument);
        Assert.AreEqual(seconds, observation.Value, 0.001);
        Assert.AreEqual(source, observation.Tags[MessagingAttributes.NimBusDelaySource]);
        Assert.AreEqual(endpoint, observation.Tags[MessagingAttributes.NimBusEndpoint]);
    }

    private static ResolverService CreateService(
        FakeCosmosDbClient? cosmos = null,
        IMessageStateChangeNotifier? notifier = null)
    {
        cosmos ??= new FakeCosmosDbClient();
        return new ResolverService(cosmos, notifier ?? new NoopMessageStateChangeNotifier());
    }

    private sealed class ThrowingMessageStateChangeNotifier : IMessageStateChangeNotifier
    {
        private readonly Exception _exception;

        public ThrowingMessageStateChangeNotifier(Exception exception)
        {
            _exception = exception;
        }

        public Task NotifyEndpointStateChangedAsync(
            string endpointId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(_exception);
    }

    internal sealed class FakeCosmosDbClient : NimBus.MessageStore.Abstractions.INimBusMessageStore
    {
        public Exception? StoreMessageException { get; set; }
        public Exception? UploadException { get; set; }
        public Exception? StoreAuditException { get; set; }
        public Exception? SetHeartbeatException { get; set; }
        public Exception? SetServiceHealthException { get; set; }

        /// <summary>What <see cref="UploadPendingMessage"/> reports back (Spec 030): false stands
        /// for a write StaleWriteGuard refused because the row already holds a later outcome.</summary>
        public bool PendingUploadResult { get; set; } = true;

        public List<MessageEntity> StoredMessages { get; } = new();
        public List<(string EventId, MessageAuditEntity Audit)> StoredAudits { get; } = new();
        public List<UploadCall> PendingUploads { get; } = new();
        public List<UploadCall> DeferredUploads { get; } = new();
        public List<UploadCall> FailedUploads { get; } = new();
        public List<UploadCall> DeadLetteredUploads { get; } = new();
        public List<UploadCall> UnsupportedUploads { get; } = new();
        public List<UploadCall> CompletedUploads { get; } = new();
        public List<UploadCall> SkippedUploads { get; } = new();
        public List<(string EndpointId, Heartbeat Heartbeat)> WrittenHeartbeats { get; } = new();
        public List<ServiceHealth> WrittenServiceHealth { get; } = new();

        public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            PendingUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(PendingUploadResult);
        }

        public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            DeferredUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            FailedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            DeadLetteredUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            UnsupportedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            SkippedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        {
            if (UploadException is not null) return Task.FromException<bool>(UploadException);
            CompletedUploads.Add(new UploadCall(eventId, sessionId, endpointId, content));
            return Task.FromResult(true);
        }

        public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount) => throw new NotSupportedException();
        public Task<SearchResponse> GetFailedEventsAcrossEndpoints(EventFilter filter, IReadOnlyCollection<string> endpointIds, string? continuationToken, int maxItemCount) => throw new NotSupportedException();
        public Task<FailedEventHistogram> GetFailedEventHistogram(EventFilter filter, IReadOnlyCollection<string> endpointIds, DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, System.Threading.CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) => throw new NotSupportedException();
        public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId) => throw new NotSupportedException();
        public Task<UnresolvedEvent?> GetEventById(string endpointId, string eventId) => throw new NotSupportedException();
        public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds) => throw new NotSupportedException();
        public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId) => throw new NotSupportedException();
        public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId) => throw new NotSupportedException();
        public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds) => throw new NotSupportedException();
        public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId) => throw new NotSupportedException();
        public Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken) => throw new NotSupportedException();
        public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take) => throw new NotSupportedException();
        public Task<IEnumerable<UnresolvedEvent>?> GetPendingEventsOnSession(string endpointId) => throw new NotSupportedException();
        public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId) => throw new NotSupportedException();
        public Task<EndpointSubscription?> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency) => throw new NotSupportedException();
        public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId) => throw new NotSupportedException();
        public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText) => throw new NotSupportedException();
        public Task<string> GetEndpointErrorList(string endpointId) => throw new NotSupportedException();
        public Task<bool> UpdateSubscription(EndpointSubscription subscription) => throw new NotSupportedException();
        public Task<bool> UnsubscribeById(string endpointId, string mail) => throw new NotSupportedException();
        public Task<bool> DeleteSubscription(string subscriptionId) => throw new NotSupportedException();
        public Task<bool> UnsubscribeByMail(string endpointId, string mail) => throw new NotSupportedException();
        public Task<bool> PurgeMessages(string endpointId, string sessionId) => throw new NotSupportedException();
        public Task<bool> PurgeMessages(string endpointId) => throw new NotSupportedException();
        public Task<EndpointMetadata?> GetEndpointMetadata(string endpointId) => throw new NotSupportedException();
        public Task<List<EndpointMetadata>> GetMetadatas() => throw new NotSupportedException();
        public Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds) => throw new NotSupportedException();
        public Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata) => throw new NotSupportedException();
        // Heartbeat surface: the Resolver writes through it, so these record instead
        // of throwing. Reads return the empty/default shape a store with no rows would.
        public Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
        {
            if (SetHeartbeatException is not null) return Task.FromException<bool>(SetHeartbeatException);
            WrittenHeartbeats.Add((endpointId, heartbeat));
            return Task.FromResult(true);
        }

        public Task<bool> SetServiceHealth(ServiceHealth serviceHealth)
        {
            if (SetServiceHealthException is not null) return Task.FromException<bool>(SetServiceHealthException);
            WrittenServiceHealth.Add(serviceHealth);
            return Task.FromResult(true);
        }

        public Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat() => Task.FromResult(new List<EndpointMetadata>());
        public Task EnableHeartbeatOnEndpoint(string endpointId, bool enable) => Task.CompletedTask;
        public Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc) => Task.FromResult(new List<string>());
        public Task<HeartbeatSettings> GetHeartbeatSettings() => Task.FromResult(new HeartbeatSettings());
        public Task<bool> SetHeartbeatSettings(HeartbeatSettings settings) => Task.FromResult(true);
        public Task<AuditSettings> GetAuditSettings() => Task.FromResult(new AuditSettings());
        public Task<McpAccessSettings> GetMcpAccessSettings() => Task.FromResult(new McpAccessSettings());
        public Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision) => Task.FromResult(true);
        public Task<AgentApiKeySettings> GetAgentApiKeySettings() => Task.FromResult(new AgentApiKeySettings());
        public Task<bool> TrySetAgentApiKeySettings(AgentApiKeySettings settings, string? expectedRevision) => Task.FromResult(true);
        public Task<List<AgentApiKeyUsage>> GetAgentApiKeyUsage() => Task.FromResult(new List<AgentApiKeyUsage>());
        public Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc) => Task.CompletedTask;
        public Task<bool> SetAuditSettings(AuditSettings settings) => Task.FromResult(true);
        public Task<bool> TryClaimHeartbeatSend(DateTime dueBefore) => Task.FromResult(true);
        public Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview() => Task.FromResult(new List<HeartbeatOverviewItem>());
        public Task<List<ServiceHealth>> GetServiceHealth() => Task.FromResult(new List<ServiceHealth>());
        public Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId) => Task.FromResult(true);
        public Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc) => Task.FromResult(new List<string>());
        public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount) => throw new NotSupportedException();
        public Task<MessageEntity?> GetMessage(string eventId, string messageId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId) =>
            Task.FromResult<IEnumerable<MessageEntity>>(StoredMessages.Where(m => m.EventId == eventId).ToList());
        public Task<MessageEntity?> GetLatestEventRequestMessage(string eventId) =>
            Task.FromResult(StoredMessages
                .Where(m => m.EventId == eventId
                         && (m.MessageType == MessageType.EventRequest || m.MessageType == MessageType.ResubmissionRequest)
                         && !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson))
                .OrderByDescending(m => m.EnqueuedTimeUtc)
                .FirstOrDefault());
        public Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId) => throw new NotSupportedException();
        public Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId) => throw new NotSupportedException();
        public Task RemoveStoredMessage(string eventId, string messageId) => throw new NotSupportedException();
        public Task<int> PurgeStoredMessages(string endpointId) => throw new NotSupportedException();
        public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId) => throw new NotSupportedException();
        public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) => throw new NotSupportedException();
        public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => throw new NotSupportedException();
        public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => throw new NotSupportedException();
        public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => throw new NotSupportedException();
        public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => throw new NotSupportedException();
        public Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => throw new NotSupportedException();

        public Task StoreMessage(MessageEntity message)
        {
            if (StoreMessageException is not null)
            {
                return Task.FromException(StoreMessageException);
            }

            StoredMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
        {
            if (StoreAuditException is not null) return Task.FromException(StoreAuditException);
            StoredAudits.Add((eventId, auditEntity));
            return Task.CompletedTask;
        }

        public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount) => throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, System.Collections.Generic.IReadOnlyCollection<string> eventIds) => throw new NotSupportedException();

        public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId) => throw new NotSupportedException();

        public Task<System.Collections.Generic.IReadOnlyDictionary<string, NimBus.MessageStore.States.EventReport>> GetEventReports(string endpointId, System.Collections.Generic.IReadOnlyCollection<string> eventIds) => throw new NotSupportedException();

        public Task<NimBus.MessageStore.States.EventSchema?> GetSchema(string eventTypeId) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<NimBus.MessageStore.States.EventSchema>> GetSchemas() => throw new NotSupportedException();
        public Task<NimBus.MessageStore.States.EventSchema> DefineEventType(NimBus.MessageStore.States.EventSchema schema) => throw new NotSupportedException();
        public Task<NimBus.MessageStore.States.AccessControlList?> GetSiteAccessControl() => throw new NotSupportedException();
        public Task SetSiteAccessControl(NimBus.MessageStore.States.AccessControlList accessControl) => throw new NotSupportedException();
        public Task<NimBus.MessageStore.States.AccessControlList?> GetEndpointAccessControl(string endpointId) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<NimBus.MessageStore.States.AccessControlList>> GetEndpointAccessControls() => throw new NotSupportedException();
        public Task SetEndpointAccessControl(string endpointId, NimBus.MessageStore.States.AccessControlList accessControl) => throw new NotSupportedException();
        public Task<System.Collections.Generic.IReadOnlyList<NimBus.MessageStore.States.EndpointAcknowledgement>> GetEndpointAcknowledgements() => throw new NotSupportedException();
        public Task SetEndpointAcknowledgement(NimBus.MessageStore.States.EndpointAcknowledgement acknowledgement) => throw new NotSupportedException();
        public Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null) => throw new NotSupportedException();
    }

    internal sealed record UploadCall(string EventId, string SessionId, string EndpointId, UnresolvedEvent Content);
}


