#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using static NimBus.Resolver.Tests.ResolverTestMessages;
using FakeCosmosDbClient = NimBus.Resolver.Tests.ResolverServiceTests.FakeCosmosDbClient;

namespace NimBus.Resolver.Tests;

/// <summary>
/// Spec 030 from the Resolver's side. The first block drives a fake store that simply answers
/// "refused"; the rest run the real <see cref="InMemoryMessageStore"/>, which applies
/// <see cref="StaleWriteGuard"/>, so the message orders from the incident are exercised
/// end to end rather than against a stubbed decision.
/// </summary>
[TestClass]
public class ResolverStaleCopyTests
{
    private const string Endpoint = "AnalyticsEndpoint";

    // ── The Resolver's reaction to a refusal ───────────────────────────────────────────

    [TestMethod]
    public async Task A_refused_write_completes_the_message_without_dead_lettering_it()
    {
        var store = new FakeCosmosDbClient { PendingUploadResult = false };
        var notifier = new RecordingNotifier();
        var service = new ResolverService(store, notifier);
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint, throttleRetryCount: 5);

        await service.Handle(message);

        Assert.AreEqual(1, message.CompletedCalls, "a stale copy is settled, not retried");
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task A_refused_write_still_stores_the_history_entry()
    {
        // The Flow tab must keep showing the late copy after the response — the forensic trail
        // that made the incident diagnosable.
        var store = new FakeCosmosDbClient { PendingUploadResult = false };
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));

        Assert.AreEqual(1, store.StoredMessages.Count);
    }

    [TestMethod]
    public async Task A_refused_write_records_a_Comment_audit_and_does_not_notify()
    {
        var store = new FakeCosmosDbClient { PendingUploadResult = false };
        var notifier = new RecordingNotifier();
        var service = new ResolverService(store, notifier);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, messageId: "req-late"));

        var (eventId, audit) = store.StoredAudits.Single();
        Assert.AreEqual("event-1", eventId);
        Assert.AreEqual(MessageAuditType.Comment, audit.AuditType);
        Assert.AreEqual(Constants.ResolverId, audit.AuditorName);
        StringAssert.Contains(audit.Data, "req-late");
        Assert.AreEqual(0, notifier.Notifications.Count, "nothing changed, so nothing to notify about");
    }

    [TestMethod]
    public async Task An_applied_write_still_notifies()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new RecordingNotifier();
        var service = new ResolverService(store, notifier);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));

        CollectionAssert.AreEqual(new[] { Endpoint }, notifier.Notifications);
        Assert.AreEqual(0, store.StoredAudits.Count);
    }

    [TestMethod]
    public async Task A_failing_audit_write_does_not_turn_a_stale_copy_into_a_redelivery()
    {
        var store = new FakeCosmosDbClient
        {
            PendingUploadResult = false,
            StoreAuditException = new InvalidOperationException("audit store down"),
        };
        var service = new ResolverService(store, new RecordingNotifier());
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);

        await service.Handle(message);

        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    // ── The real store, the real rule ──────────────────────────────────────────────────

    [TestMethod]
    public async Task The_incident_order_leaves_the_row_Completed()
    {
        // Nav09Endpoint, 2026-09-14: the Resolver handled the terminal response first and the
        // request copy — rescheduled five times by the Cosmos throttle path — second.
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        var response = CreateMessageContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "req-1");
        var lateRequest = CreateMessageContext(
            MessageType.EventRequest, to: Endpoint, messageId: "req-1", throttleRetryCount: 5);

        await service.Handle(response);
        await service.Handle(lateRequest);

        var row = (await store.GetEvent(Endpoint, "event-1"))!;
        Assert.AreEqual(ResolutionStatus.Completed, row.ResolutionStatus);
        Assert.AreEqual(0, (await store.DownloadEndpointStateCount(Endpoint)).PendingCount);
        Assert.AreEqual(2, (await store.GetEventHistory("event-1")).Count(), "both copies stay in the Flow tab");
        Assert.AreEqual(1, response.CompletedCalls);
        Assert.AreEqual(1, lateRequest.CompletedCalls);
    }

    [TestMethod]
    public async Task The_fan_out_lag_order_leaves_the_row_Completed()
    {
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, messageId: "req-1"));
        await service.Handle(CreateMessageContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "req-1"));
        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, messageId: "req-1"));

        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(Endpoint, "event-1"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task A_late_request_copy_keeps_a_parked_handoff()
    {
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(
            MessageType.PendingHandoffResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "ho-1", parentMessageId: "req-1"));
        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, messageId: "req-late"));

        var row = (await store.GetPendingEvent(Endpoint, "event-1", "session-1"))!;
        Assert.AreEqual("Handoff", row.PendingSubStatus, "GetNextPendingHandoffEvent must keep finding the row");
        Assert.AreEqual(MessageType.PendingHandoffResponse, row.MessageType);
    }

    [TestMethod]
    public async Task A_late_request_copy_does_not_replace_a_settlement_projection()
    {
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(
            MessageType.HandoffCompletedRequest, to: Endpoint, from: Constants.ManagerId,
            messageId: "hc-1", parentMessageId: "ho-1"));
        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, messageId: "req-late"));

        var row = (await store.GetPendingEvent(Endpoint, "event-1", "session-1"))!;
        Assert.AreEqual(MessageType.HandoffCompletedRequest, row.MessageType);
        Assert.IsNull(row.PendingSubStatus, "the settlement clears the handoff sub-status (ADR-012)");
    }

    [TestMethod]
    public async Task A_resubmission_still_reopens_a_failed_row()
    {
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(
            MessageType.ErrorResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "err-1", parentMessageId: "req-1"));
        await service.Handle(CreateMessageContext(
            MessageType.ResubmissionRequest, to: Endpoint, from: Constants.ManagerId,
            messageId: "rs-1", parentMessageId: "err-1"));

        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetEvent(Endpoint, "event-1"))!.ResolutionStatus);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "duplicate after the response")]
    [DataRow(false, DisplayName = "duplicate before the response")]
    public async Task A_duplicate_skip_leaves_the_row_Completed(bool responseFirst)
    {
        // Spec 041 §2.7, bug 2: a deferred replay is redelivered after its handler and inbox
        // record committed. The endpoint's inbox answers the redelivery with a SkipResponse,
        // which must not rewrite the replay's Completed outcome.
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());
        var response = CreateMessageContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "replay-1");
        var duplicate = CreateMessageContext(
            MessageType.SkipResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "dup-1", parentMessageId: "replay-2");
        duplicate.MessageContent.ErrorContent = new ErrorContent { ErrorText = NimBus.Core.Inbox.InboxMiddleware.DuplicateReason };

        if (responseFirst)
        {
            await service.Handle(response);
            await service.Handle(duplicate);
        }
        else
        {
            await service.Handle(duplicate);
            await service.Handle(response);
        }

        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(Endpoint, "event-1"))!.ResolutionStatus);
        Assert.AreEqual(2, (await store.GetEventHistory("event-1")).Count(), "the duplicate stays in the Flow tab");
        Assert.AreEqual(1, duplicate.CompletedCalls);
    }

    [TestMethod]
    public async Task A_late_deferral_after_the_response_does_not_strand_the_row()
    {
        // Without the guard this left the row Deferred forever, with nothing behind it to
        // correct it — worse than the incident itself.
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "req-1"));
        await service.Handle(CreateMessageContext(
            MessageType.DeferralResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "def-late", parentMessageId: "req-1"));

        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(Endpoint, "event-1"))!.ResolutionStatus);
        Assert.AreEqual(0, (await store.DownloadEndpointStateCount(Endpoint)).DeferredCount);
    }

    [TestMethod]
    public async Task A_late_handoff_park_after_the_response_does_not_reopen_the_row()
    {
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "req-1"));
        await service.Handle(CreateMessageContext(
            MessageType.PendingHandoffResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "ho-late", parentMessageId: "req-2"));

        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(Endpoint, "event-1"))!.ResolutionStatus);
        Assert.IsNull(await store.GetNextPendingHandoffEvent(Endpoint, eventTypeIds: null),
            "a phantom 'awaiting external' row would be handed to an agent");
    }

    [TestMethod]
    public async Task A_rescheduled_control_request_arriving_after_its_own_response_is_refused()
    {
        // The hole the status rules alone cannot close: a resubmission the subscriber has
        // already answered and will never see again. Only the ancestor check catches it, and
        // only because ScheduleRedelivery keeps the original MessageId.
        var store = new InMemoryMessageStore();
        var service = new ResolverService(store, new RecordingNotifier());

        await service.Handle(CreateMessageContext(
            MessageType.ResubmissionRequest, to: Endpoint, from: Constants.ManagerId,
            messageId: "rs-1", parentMessageId: "err-1"));
        await service.Handle(CreateMessageContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "rs-1"));
        await service.Handle(CreateMessageContext(
            MessageType.ResubmissionRequest, to: Endpoint, from: Constants.ManagerId,
            messageId: "rs-1", parentMessageId: "err-1", throttleRetryCount: 3));

        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(Endpoint, "event-1"))!.ResolutionStatus);
        Assert.AreEqual(0, (await store.DownloadEndpointStateCount(Endpoint)).PendingCount);
    }

    private sealed class RecordingNotifier : IMessageStateChangeNotifier
    {
        public List<string> Notifications { get; } = new();

        public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            Notifications.Add(endpointId);
            return Task.CompletedTask;
        }
    }
}
