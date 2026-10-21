#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IMessageTrackingStore"/>.
/// Each concrete provider supplies a freshly-initialized store or isolated ids.
/// </summary>
[TestClass]
public abstract class MessageTrackingStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract IMessageTrackingStore CreateStore();

    protected string Id(string value) => $"{_scope}-{value}";

    private static string StoredId(string eventId, string sessionId) => $"{eventId}_{sessionId}";

    private static UnresolvedEvent SampleEvent(
        string endpointId,
        string eventId,
        string sessionId,
        EndpointRole endpointRole = EndpointRole.Subscriber) => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        EndpointId = endpointId,
        EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-1),
        UpdatedAt = DateTime.UtcNow,
        CorrelationId = "corr-1",
        EndpointRole = endpointRole,
        MessageType = MessageType.EventRequest,
        EventTypeId = "OrderPlaced",
        To = endpointId,
        From = "publisher",
        LastMessageId = "last-message",
        OriginatingMessageId = "origin-message",
        MessageContent = new MessageContent(),
    };

    [TestMethod]
    public async Task UploadPending_then_GetPending_returns_event()
    {
        var store = CreateStore();
        var endpointId = Id("ep1");
        var eventId = Id("e1");
        await store.UploadPendingMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var fetched = await store.GetPendingEvent(endpointId, eventId, "s1");

        Assert.AreEqual(eventId, fetched!.EventId);
        Assert.AreEqual(ResolutionStatus.Pending, fetched.ResolutionStatus);
    }

    [TestMethod]
    public async Task GetPendingHandoffByExternalJobId_returns_the_pending_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff");
        var eventId = Id("e-handoff");
        var externalJobId = Id("ext-job");

        var content = SampleEvent(endpointId, eventId, "s-handoff");
        content.PendingSubStatus = "Handoff";
        content.HandoffReason = "Awaiting external work";
        content.ExternalJobId = externalJobId;
        await store.UploadPendingMessage(eventId, "s-handoff", endpointId, content);

        var fetched = await store.GetPendingHandoffByExternalJobId(endpointId, externalJobId);

        Assert.IsNotNull(fetched);
        Assert.AreEqual(eventId, fetched.EventId);
        Assert.AreEqual("Handoff", fetched.PendingSubStatus);
        Assert.AreEqual(externalJobId, fetched.ExternalJobId);
    }

    [TestMethod]
    public async Task GetPendingHandoffByExternalJobId_returns_null_when_no_match()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff-miss");

        var fetched = await store.GetPendingHandoffByExternalJobId(endpointId, Id("never-registered"));

        Assert.IsNull(fetched);
    }

    [TestMethod]
    public async Task GetNextPendingHandoffEvent_returns_only_the_handoff_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-next");

        // A plain pending event (no sub-status) and a failed event must be ignored;
        // only the single Pending+Handoff row should come back.
        var plain = SampleEvent(endpointId, Id("e-plain"), "s1");
        await store.UploadPendingMessage(plain.EventId, "s1", endpointId, plain);

        var failed = SampleEvent(endpointId, Id("e-failed"), "s2");
        failed.PendingSubStatus = "Handoff";
        await store.UploadFailedMessage(failed.EventId, "s2", endpointId, failed);

        var handoff = SampleEvent(endpointId, Id("e-handoff"), "s3");
        handoff.PendingSubStatus = "Handoff";
        handoff.ExternalJobId = Id("job");
        await store.UploadPendingMessage(handoff.EventId, "s3", endpointId, handoff);

        var fetched = await store.GetNextPendingHandoffEvent(endpointId, null);

        Assert.IsNotNull(fetched);
        Assert.AreEqual(handoff.EventId, fetched.EventId);
        Assert.AreEqual("Handoff", fetched.PendingSubStatus);
    }

    [TestMethod]
    public async Task GetNextPendingHandoffEvent_returns_null_when_no_handoff()
    {
        var store = CreateStore();
        var endpointId = Id("ep-next-miss");

        var plain = SampleEvent(endpointId, Id("e-plain"), "s1");
        await store.UploadPendingMessage(plain.EventId, "s1", endpointId, plain);

        var fetched = await store.GetNextPendingHandoffEvent(endpointId, null);

        Assert.IsNull(fetched);
    }

    [TestMethod]
    public async Task GetNextPendingHandoffEvent_respects_eventTypeIds_filter()
    {
        var store = CreateStore();
        var endpointId = Id("ep-next-filter");

        var handoff = SampleEvent(endpointId, Id("e-handoff"), "s1");
        handoff.PendingSubStatus = "Handoff";
        handoff.EventTypeId = "OrderPlaced";
        await store.UploadPendingMessage(handoff.EventId, "s1", endpointId, handoff);

        // A non-matching type filter finds nothing.
        Assert.IsNull(await store.GetNextPendingHandoffEvent(endpointId, new[] { "SomethingElse" }));

        // A matching type filter finds the row.
        var matched = await store.GetNextPendingHandoffEvent(endpointId, new[] { "OrderPlaced", "AnotherType" });
        Assert.IsNotNull(matched);
        Assert.AreEqual(handoff.EventId, matched.EventId);
    }

    [TestMethod]
    public async Task UploadStatus_is_idempotent_under_repeated_writes()
    {
        var store = CreateStore();
        var endpointId = Id("ep1");
        var eventId = Id("e2");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.AreEqual(1, counts.FailedCount);
    }

    [TestMethod]
    public async Task Status_transition_replaces_previous_status()
    {
        var store = CreateStore();
        var endpointId = Id("ep1");
        var eventId = Id("e3");
        await store.UploadPendingMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadCompletedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.AreEqual(0, counts.PendingCount);
    }

    [TestMethod]
    public async Task TrySkipDeferredMessage_preserves_identity_and_rejects_stale_versions()
    {
        var store = CreateStore();
        var endpoint = Id("deferred-recovery");
        var eventId = Id("deferred-event");
        var row = SampleEvent(endpoint, eventId, "s1");
        row.LastMessageId = "deferral-response";
        await store.UploadDeferredMessage(eventId, "s1", endpoint, row);
        var stored = await store.GetEvent(endpoint, eventId);

        Assert.IsFalse(await store.TrySkipDeferredMessage(eventId, "s1", endpoint, "wrong", stored!.UpdatedAt));
        Assert.IsFalse(await store.TrySkipDeferredMessage(eventId, "s1", endpoint, stored.LastMessageId, stored.UpdatedAt.AddSeconds(-1)));
        Assert.IsFalse(await store.TrySkipDeferredMessage(eventId, "other-session", endpoint, stored.LastMessageId, stored.UpdatedAt));
        Assert.IsFalse(await store.TrySkipDeferredMessage(eventId, "s1", Id("other-endpoint"), stored.LastMessageId, stored.UpdatedAt));
        Assert.IsTrue(await store.TrySkipDeferredMessage(eventId, "s1", endpoint, stored.LastMessageId, stored.UpdatedAt));
        var skipped = await store.GetEvent(endpoint, eventId);
        Assert.AreEqual(ResolutionStatus.Skipped, skipped!.ResolutionStatus);
        Assert.AreEqual(stored.LastMessageId, skipped.LastMessageId);
        Assert.AreEqual(stored.MessageType, skipped.MessageType);
        Assert.AreEqual(stored.EventTypeId, skipped.EventTypeId);
        Assert.AreEqual(0, (await store.DownloadEndpointStateCount(endpoint)).DeferredCount);
        Assert.IsFalse(await store.TrySkipDeferredMessage(eventId, "s1", endpoint, stored.LastMessageId, stored.UpdatedAt));
    }

    [TestMethod]
    public async Task TrySkipDeferredMessage_never_overwrites_completed_outcome()
    {
        var store = CreateStore();
        var endpoint = Id("deferred-race");
        var eventId = Id("deferred-event");
        await store.UploadDeferredMessage(eventId, "s1", endpoint, SampleEvent(endpoint, eventId, "s1"));
        var inspected = await store.GetEvent(endpoint, eventId);
        await store.UploadCompletedMessage(eventId, "s1", endpoint, SampleEvent(endpoint, eventId, "s1"));
        Assert.IsFalse(await store.TrySkipDeferredMessage(eventId, "s1", endpoint, inspected!.LastMessageId, inspected.UpdatedAt));
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(endpoint, eventId))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task TryCompletePendingMessage_replaces_matching_pending_row_and_guard_refuses_late_copy()
    {
        var store = CreateStore();
        var endpointId = Id("ep-reconcile");
        var eventId = Id("repair-1");
        var pending = SampleEvent(endpointId, eventId, "s1");
        pending.LastMessageId = "stale-message";
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId, pending));

        var completed = SampleEvent(endpointId, eventId, "s1");
        completed.MessageType = MessageType.ResolutionResponse;
        completed.LastMessageId = "response-message";
        Assert.IsTrue(await store.TryCompletePendingMessage(
            eventId, "s1", endpointId, "stale-message", completed));
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(endpointId, eventId))!.ResolutionStatus);
        Assert.AreEqual(0, (await store.DownloadEndpointStateCount(endpointId)).PendingCount);

        Assert.IsFalse(await store.UploadPendingMessage(
            eventId, "s1", endpointId, GuardedEvent(
                endpointId, eventId, "s1", MessageType.EventRequest, "stale-message")));
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(endpointId, eventId))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task TryCompletePendingMessage_refuses_mismatch_terminal_and_missing_rows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-reconcile-refuse");

        var mismatchId = Id("mismatch");
        var mismatch = SampleEvent(endpointId, mismatchId, "s1");
        mismatch.LastMessageId = "actual";
        await store.UploadPendingMessage(mismatchId, "s1", endpointId, mismatch);
        Assert.IsFalse(await store.TryCompletePendingMessage(
            mismatchId, "s1", endpointId, "expected", SampleEvent(endpointId, mismatchId, "s1")));
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(endpointId, mismatchId, "s1"))!.ResolutionStatus);

        var terminalId = Id("terminal");
        await store.UploadCompletedMessage(terminalId, "s1", endpointId, SampleEvent(endpointId, terminalId, "s1"));
        Assert.IsFalse(await store.TryCompletePendingMessage(
            terminalId, "s1", endpointId, "last-message", SampleEvent(endpointId, terminalId, "s1")));

        Assert.IsFalse(await store.TryCompletePendingMessage(
            Id("missing"), "s1", endpointId, null, SampleEvent(endpointId, Id("missing"), "s1")));
    }

    [TestMethod]
    public async Task StalePendingReconciler_classifies_the_incident_shape_read_back_through_this_provider()
    {
        // The rule's own tests run on in-memory MessageEntity objects. This case runs it on history
        // the PROVIDER hands back, because providers disagree on what a NULL column reads as (SQL
        // Server: string.Empty; Cosmos, in-memory: null) and that difference once made every SQL
        // Server row classify as dead-lettered. Spec 032 §4.2.
        var store = CreateStore();
        var endpointId = Id("ep-reconcile-history");
        var eventId = Id("classify-1");
        var t0 = DateTime.UtcNow.AddHours(-3);

        await store.StoreMessage(HistoryMessage(endpointId, eventId, "req-1", MessageType.EventRequest, t0, from: "publisher", to: endpointId));
        await store.StoreMessage(HistoryMessage(endpointId, eventId, "rsp-1", MessageType.ResolutionResponse, t0.AddSeconds(30), from: endpointId, to: "Resolver"));
        await store.StoreMessage(HistoryMessage(endpointId, eventId, "req-copy", MessageType.EventRequest, t0.AddMinutes(3), from: "publisher", to: endpointId));

        var row = SampleEvent(endpointId, eventId, "s1");
        row.MessageType = MessageType.EventRequest;
        row.LastMessageId = "req-copy";
        row.EnqueuedTimeUtc = t0.AddMinutes(3);
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId, row));

        var history = (await store.GetEventHistory(eventId)).ToList();
        Assert.AreEqual(3, history.Count);
        var stored = await store.GetPendingEvent(endpointId, eventId, "s1");

        var verdict = StalePendingReconciler.Classify(stored!, history, endpointId);
        Assert.AreEqual(StalePendingVerdict.Repairable, verdict.Verdict, verdict.Detail);
        Assert.AreEqual("rsp-1", verdict.Response!.MessageId);

        var projection = StalePendingReconciler.BuildCompletedProjection(verdict.Response, history, endpointId, DateTime.UtcNow);
        Assert.IsNull(projection.DeadLetterErrorDescription, "a clean response must project without a dead-letter description, whatever the provider reads NULL back as");
        Assert.IsTrue(await store.TryCompletePendingMessage(eventId, "s1", endpointId, stored!.LastMessageId, projection));
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(endpointId, eventId))!.ResolutionStatus);
    }

    private static MessageEntity HistoryMessage(
        string endpointId, string eventId, string messageId, MessageType type, DateTime enqueuedUtc, string from, string to) => new()
    {
        EventId = eventId,
        MessageId = messageId,
        EndpointId = endpointId,
        SessionId = "s1",
        CorrelationId = "corr-1",
        EventTypeId = "OrderPlaced",
        MessageType = type,
        EndpointRole = EndpointRole.Subscriber,
        EnqueuedTimeUtc = enqueuedUtc,
        From = from,
        To = to,
        OriginatingFrom = "publisher",
        OriginatingMessageId = "req-1",
        ParentMessageId = type == MessageType.ResolutionResponse ? "req-1" : "self",
        MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" } },
    };

    [TestMethod]
    public async Task TryCompletePendingMessage_null_expected_id_matches_only_null()
    {
        var store = CreateStore();
        var endpointId = Id("ep-reconcile-null");
        var eventId = Id("null-id");
        var pending = SampleEvent(endpointId, eventId, "s1");
        pending.LastMessageId = null;
        await store.UploadPendingMessage(eventId, "s1", endpointId, pending);

        Assert.IsTrue(await store.TryCompletePendingMessage(
            eventId, "s1", endpointId, null, SampleEvent(endpointId, eventId, "s1")));

        // ...and a row that does carry one is not matched by a null expectation.
        var withIdEventId = Id("null-id-negative");
        await store.UploadPendingMessage(withIdEventId, "s1", endpointId, SampleEvent(endpointId, withIdEventId, "s1"));
        Assert.IsFalse(await store.TryCompletePendingMessage(
            withIdEventId, "s1", endpointId, null, SampleEvent(endpointId, withIdEventId, "s1")));
        Assert.AreEqual(
            ResolutionStatus.Pending,
            (await store.GetPendingEvent(endpointId, withIdEventId, "s1"))!.ResolutionStatus);
    }

    // ---------------------------------------------------------------------------------
    // Spec 030 - the stale-write guard. A late copy of a message (auto-forward lag, a
    // ScheduleRedelivery copy, a dead-letter replay) must never reopen or downgrade a row
    // that already holds a later outcome. StaleWriteGuard is the single source of truth;
    // these cases pin that every provider applies it identically.
    //
    // Convention (as in the cases above): statuses are looped inside ONE endpoint container
    // with distinct event ids, because every distinct endpoint id is a new Cosmos container
    // on the emulator. A fresh SampleEvent is passed per call.
    // ---------------------------------------------------------------------------------

    private static readonly ResolutionStatus[] TerminalStatuses =
    [
        ResolutionStatus.Completed, ResolutionStatus.Skipped, ResolutionStatus.Failed,
        ResolutionStatus.DeadLettered, ResolutionStatus.Unsupported,
    ];

    /// <summary>Dispatches to the upload method that writes <paramref name="status"/>.</summary>
    private static Task<bool> Upload(
        IMessageTrackingStore store,
        ResolutionStatus status,
        string eventId,
        string sessionId,
        string endpointId,
        UnresolvedEvent content) => status switch
        {
            ResolutionStatus.Pending => store.UploadPendingMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Deferred => store.UploadDeferredMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Failed => store.UploadFailedMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.DeadLettered => store.UploadDeadletteredMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Unsupported => store.UploadUnsupportedMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Skipped => store.UploadSkippedMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Completed => store.UploadCompletedMessage(eventId, sessionId, endpointId, content),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No upload method writes this status."),
        };

    /// <summary>A <see cref="SampleEvent"/> carrying the identity fields the guard reads.</summary>
    private static UnresolvedEvent GuardedEvent(
        string endpointId,
        string eventId,
        string sessionId,
        MessageType messageType,
        string lastMessageId,
        string? parentMessageId = null)
    {
        var content = SampleEvent(endpointId, eventId, sessionId);
        content.MessageType = messageType;
        content.LastMessageId = lastMessageId;
        content.ParentMessageId = parentMessageId;
        return content;
    }

    [TestMethod]
    public async Task Stale_request_copy_does_not_regress_settled_rows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-stale-request");

        foreach (var terminal in TerminalStatuses)
        {
            // The incident: a rescheduled EventRequest copy arriving after the response.
            var requestId = Id($"sr-req-{terminal}");
            Assert.IsTrue(await store.UploadPendingMessage(requestId, "s1", endpointId,
                GuardedEvent(endpointId, requestId, "s1", MessageType.EventRequest, "req-1")));
            Assert.IsTrue(await Upload(store, terminal, requestId, "s1", endpointId,
                GuardedEvent(endpointId, requestId, "s1", MessageType.ResolutionResponse, "rsp-1", parentMessageId: "req-1")));

            var lateRequest = await store.UploadPendingMessage(requestId, "s1", endpointId,
                GuardedEvent(endpointId, requestId, "s1", MessageType.EventRequest, "req-late"));

            Assert.IsFalse(lateRequest, $"a late EventRequest copy must not reopen a {terminal} row");
            Assert.AreEqual(terminal, (await store.GetEvent(endpointId, requestId))!.ResolutionStatus);

            // The stuck-Deferred variant: a throttled DeferralResponse landing after the response.
            var deferralId = Id($"sr-def-{terminal}");
            Assert.IsTrue(await store.UploadPendingMessage(deferralId, "s1", endpointId,
                GuardedEvent(endpointId, deferralId, "s1", MessageType.EventRequest, "req-2")));
            Assert.IsTrue(await Upload(store, terminal, deferralId, "s1", endpointId,
                GuardedEvent(endpointId, deferralId, "s1", MessageType.ResolutionResponse, "rsp-2", parentMessageId: "req-2")));

            var lateDeferral = await store.UploadDeferredMessage(deferralId, "s1", endpointId,
                GuardedEvent(endpointId, deferralId, "s1", MessageType.DeferralResponse, "def-late"));

            Assert.IsFalse(lateDeferral, $"a late DeferralResponse must not reopen a {terminal} row");
            Assert.AreEqual(terminal, (await store.GetEvent(endpointId, deferralId))!.ResolutionStatus);
        }

        var counts = await store.DownloadEndpointStateCount(endpointId);
        Assert.AreEqual(0, counts.PendingCount, "no settled row may have been reopened as Pending");
        Assert.AreEqual(0, counts.DeferredCount, "no settled row may have been reopened as Deferred");
    }

    [TestMethod]
    public async Task Duplicate_skip_does_not_rewrite_a_settled_row()
    {
        // The inbox answers a delivery it already processed (a redelivered deferred replay, say)
        // with a SkipResponse carrying the duplicate reason. Spec 041 §2.7, bug 2.
        var store = CreateStore();
        var endpointId = Id("ep-duplicate-skip");

        UnresolvedEvent DuplicateSkip(string eventId, string messageId)
        {
            var content = GuardedEvent(endpointId, eventId, "s1", MessageType.SkipResponse, messageId, parentMessageId: "replay-2");
            content.Reason = NimBus.Core.Inbox.InboxMiddleware.DuplicateReason;
            return content;
        }

        var completed = Id("dup-completed");
        Assert.IsTrue(await store.UploadCompletedMessage(completed, "s1", endpointId,
            GuardedEvent(endpointId, completed, "s1", MessageType.ResolutionResponse, "rsp-1", parentMessageId: "replay-1")));
        Assert.IsFalse(await store.UploadSkippedMessage(completed, "s1", endpointId, DuplicateSkip(completed, "dup-1")),
            "a duplicate skip must not rewrite a Completed row");
        var completedRow = (await store.GetEvent(endpointId, completed))!;
        Assert.AreEqual(ResolutionStatus.Completed, completedRow.ResolutionStatus);
        Assert.AreEqual("rsp-1", completedRow.LastMessageId);

        // Before the outcome is recorded, the duplicate skip still lands; a later Completed wins.
        var pending = Id("dup-pending");
        Assert.IsTrue(await store.UploadPendingMessage(pending, "s1", endpointId,
            GuardedEvent(endpointId, pending, "s1", MessageType.EventRequest, "req-1")));
        Assert.IsTrue(await store.UploadSkippedMessage(pending, "s1", endpointId, DuplicateSkip(pending, "dup-2")));
        Assert.AreEqual(ResolutionStatus.Skipped, (await store.GetEvent(endpointId, pending))!.ResolutionStatus);
        Assert.IsTrue(await store.UploadCompletedMessage(pending, "s1", endpointId,
            GuardedEvent(endpointId, pending, "s1", MessageType.ResolutionResponse, "rsp-2", parentMessageId: "req-1")));
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(endpointId, pending))!.ResolutionStatus);

        // An operator's skip is not a duplicate: it still settles a Completed row as Skipped.
        var skipped = Id("dup-operator");
        Assert.IsTrue(await store.UploadCompletedMessage(skipped, "s1", endpointId,
            GuardedEvent(endpointId, skipped, "s1", MessageType.ResolutionResponse, "rsp-3", parentMessageId: "req-3")));
        var operatorSkip = GuardedEvent(endpointId, skipped, "s1", MessageType.SkipResponse, "skip-3", parentMessageId: "skip-req-3");
        operatorSkip.Reason = "Skipped by operator";
        Assert.IsTrue(await store.UploadSkippedMessage(skipped, "s1", endpointId, operatorSkip));
        Assert.AreEqual(ResolutionStatus.Skipped, (await store.GetEvent(endpointId, skipped))!.ResolutionStatus);

        // ...and a late duplicate then leaves the operator's skip, reason and message id intact.
        Assert.IsFalse(await store.UploadSkippedMessage(skipped, "s1", endpointId, DuplicateSkip(skipped, "dup-3")),
            "a duplicate skip must not replace an operator's skip");
        var operatorRow = (await store.GetEvent(endpointId, skipped))!;
        Assert.AreEqual("skip-3", operatorRow.LastMessageId);
        Assert.AreEqual("Skipped by operator", operatorRow.Reason);

        // A Failed row stays Failed: the success the duplicate reflects records its own outcome.
        var failed = Id("dup-failed");
        Assert.IsTrue(await store.UploadFailedMessage(failed, "s1", endpointId,
            GuardedEvent(endpointId, failed, "s1", MessageType.ErrorResponse, "err-4", parentMessageId: "req-4")));
        Assert.IsFalse(await store.UploadSkippedMessage(failed, "s1", endpointId, DuplicateSkip(failed, "dup-4")),
            "a duplicate skip must not replace a Failed row");
        Assert.AreEqual(ResolutionStatus.Failed, (await store.GetEvent(endpointId, failed))!.ResolutionStatus);

        // The reason must match exactly (SQL Server's = ignores trailing spaces): a padded
        // reason is an ordinary skip, applied by every provider alike.
        var padded = Id("dup-padded");
        Assert.IsTrue(await store.UploadCompletedMessage(padded, "s1", endpointId,
            GuardedEvent(endpointId, padded, "s1", MessageType.ResolutionResponse, "rsp-5", parentMessageId: "req-5")));
        var paddedSkip = DuplicateSkip(padded, "skip-5");
        paddedSkip.Reason += " ";
        Assert.IsTrue(await store.UploadSkippedMessage(padded, "s1", endpointId, paddedSkip));
        Assert.AreEqual(ResolutionStatus.Skipped, (await store.GetEvent(endpointId, padded))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Request_copy_refreshes_request_stage_rows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-refresh");

        // A broker redelivery re-stamps its own projection (this exercises the Cosmos ETag replace).
        var repeated = Id("rf-repeat");
        Assert.IsTrue(await store.UploadPendingMessage(repeated, "s1", endpointId,
            GuardedEvent(endpointId, repeated, "s1", MessageType.EventRequest, "m1")));
        Assert.IsTrue(await store.UploadPendingMessage(repeated, "s1", endpointId,
            GuardedEvent(endpointId, repeated, "s1", MessageType.EventRequest, "m2")));
        Assert.AreEqual("m2", (await store.GetPendingEvent(endpointId, repeated, "s1"))!.LastMessageId);

        // Deferred drain: the republished request reopens its own deferral.
        var drained = Id("rf-drain");
        Assert.IsTrue(await store.UploadDeferredMessage(drained, "s1", endpointId,
            GuardedEvent(endpointId, drained, "s1", MessageType.DeferralResponse, "d1")));
        Assert.IsTrue(await store.UploadPendingMessage(drained, "s1", endpointId,
            GuardedEvent(endpointId, drained, "s1", MessageType.EventRequest, "d2")));
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(endpointId, drained, "s1"))!.ResolutionStatus);

        // Re-deferral during a drain.
        var deferred = Id("rf-defer");
        Assert.IsTrue(await store.UploadPendingMessage(deferred, "s1", endpointId,
            GuardedEvent(endpointId, deferred, "s1", MessageType.EventRequest, "p1")));
        Assert.IsTrue(await store.UploadDeferredMessage(deferred, "s1", endpointId,
            GuardedEvent(endpointId, deferred, "s1", MessageType.DeferralResponse, "p2")));
        Assert.AreEqual(ResolutionStatus.Deferred, (await store.GetDeferredEvent(endpointId, deferred, "s1"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Request_copy_does_not_replace_control_or_handoff_rows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-control-row");

        // A resubmission's projection must survive the original request's late copy.
        var resubmitted = Id("cr-resubmit");
        Assert.IsTrue(await store.UploadPendingMessage(resubmitted, "s1", endpointId,
            GuardedEvent(endpointId, resubmitted, "s1", MessageType.ResubmissionRequest, "rs-1")));
        Assert.IsFalse(await store.UploadPendingMessage(resubmitted, "s1", endpointId,
            GuardedEvent(endpointId, resubmitted, "s1", MessageType.EventRequest, "req-late")));
        Assert.AreEqual(MessageType.ResubmissionRequest,
            (await store.GetPendingEvent(endpointId, resubmitted, "s1"))!.MessageType);

        // A handoff park must survive it too, or GetPendingHandoffByExternalJobId loses the row.
        var parked = Id("cr-park");
        var externalJobId = Id("cr-job");
        var park = GuardedEvent(endpointId, parked, "s2", MessageType.PendingHandoffResponse, "ho-1");
        park.PendingSubStatus = "Handoff";
        park.ExternalJobId = externalJobId;
        Assert.IsTrue(await store.UploadPendingMessage(parked, "s2", endpointId, park));
        Assert.IsFalse(await store.UploadPendingMessage(parked, "s2", endpointId,
            GuardedEvent(endpointId, parked, "s2", MessageType.EventRequest, "req-late")));
        Assert.IsNotNull(await store.GetPendingHandoffByExternalJobId(endpointId, externalJobId));

        // So must a settlement's plain Pending projection.
        var settled = Id("cr-settled");
        Assert.IsTrue(await store.UploadPendingMessage(settled, "s3", endpointId,
            GuardedEvent(endpointId, settled, "s3", MessageType.HandoffCompletedRequest, "hc-1")));
        Assert.IsFalse(await store.UploadPendingMessage(settled, "s3", endpointId,
            GuardedEvent(endpointId, settled, "s3", MessageType.EventRequest, "req-late")));
        Assert.AreEqual(MessageType.HandoffCompletedRequest,
            (await store.GetPendingEvent(endpointId, settled, "s3"))!.MessageType);
    }

    [TestMethod]
    public async Task Handoff_park_is_refused_over_completed_and_skipped_only()
    {
        var store = CreateStore();
        var endpointId = Id("ep-park");

        foreach (var settled in new[] { ResolutionStatus.Completed, ResolutionStatus.Skipped })
        {
            var eventId = Id($"hp-closed-{settled}");
            Assert.IsTrue(await Upload(store, settled, eventId, "s1", endpointId,
                GuardedEvent(endpointId, eventId, "s1", MessageType.ResolutionResponse, "rsp-1")));

            var park = GuardedEvent(endpointId, eventId, "s1", MessageType.PendingHandoffResponse, "ho-late");
            park.PendingSubStatus = "Handoff";

            Assert.IsFalse(await store.UploadPendingMessage(eventId, "s1", endpointId, park),
                $"a late handoff park must not reopen a {settled} row");
            Assert.AreEqual(settled, (await store.GetEvent(endpointId, eventId))!.ResolutionStatus);
        }

        // Failed, DeadLettered and Unsupported stay open: a policy retry parks a handoff straight
        // from a Failed row, and a resubmission's park can overtake its own throttled Pending write.
        foreach (var open in new[] { ResolutionStatus.Failed, ResolutionStatus.DeadLettered, ResolutionStatus.Unsupported })
        {
            var eventId = Id($"hp-open-{open}");
            Assert.IsTrue(await Upload(store, open, eventId, "s1", endpointId,
                GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1")));

            var park = GuardedEvent(endpointId, eventId, "s1", MessageType.PendingHandoffResponse, "ho-1");
            park.PendingSubStatus = "Handoff";

            Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId, park),
                $"a handoff park must stay allowed over a {open} row");
            Assert.AreEqual("Handoff", (await store.GetPendingEvent(endpointId, eventId, "s1"))!.PendingSubStatus);
        }

        // As do Deferred and a control-request projection.
        var deferredId = Id("hp-open-deferred");
        Assert.IsTrue(await store.UploadDeferredMessage(deferredId, "s1", endpointId,
            GuardedEvent(endpointId, deferredId, "s1", MessageType.DeferralResponse, "def-1")));
        var deferredPark = GuardedEvent(endpointId, deferredId, "s1", MessageType.PendingHandoffResponse, "ho-2");
        deferredPark.PendingSubStatus = "Handoff";
        Assert.IsTrue(await store.UploadPendingMessage(deferredId, "s1", endpointId, deferredPark));
        Assert.AreEqual("Handoff", (await store.GetPendingEvent(endpointId, deferredId, "s1"))!.PendingSubStatus);

        var resubmittedId = Id("hp-open-resubmission");
        Assert.IsTrue(await store.UploadPendingMessage(resubmittedId, "s1", endpointId,
            GuardedEvent(endpointId, resubmittedId, "s1", MessageType.ResubmissionRequest, "rs-1")));
        var resubmittedPark = GuardedEvent(endpointId, resubmittedId, "s1", MessageType.PendingHandoffResponse, "ho-3");
        resubmittedPark.PendingSubStatus = "Handoff";
        Assert.IsTrue(await store.UploadPendingMessage(resubmittedId, "s1", endpointId, resubmittedPark));
        Assert.AreEqual("Handoff", (await store.GetPendingEvent(endpointId, resubmittedId, "s1"))!.PendingSubStatus);
    }

    [TestMethod]
    public async Task Handoff_settlement_clears_substatus()
    {
        var store = CreateStore();
        var endpointId = Id("ep-settle");
        var eventId = Id("hs-1");
        var externalJobId = Id("hs-job");

        var park = GuardedEvent(endpointId, eventId, "s1", MessageType.PendingHandoffResponse, "ho-1");
        park.PendingSubStatus = "Handoff";
        park.ExternalJobId = externalJobId;
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId, park));

        // The settlement request projects a plain Pending row (ADR-012); the handoff lookup must
        // stop finding it, which is what the agent zone's receive loop and settle guard rely on.
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.HandoffCompletedRequest, Guid.NewGuid().ToString())));

        Assert.IsNull(await store.GetPendingHandoffByExternalJobId(endpointId, externalJobId));
    }

    [TestMethod]
    public async Task Control_request_reopens_settled_rows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-reopen");
        var cases = new (ResolutionStatus Settled, MessageType Control)[]
        {
            (ResolutionStatus.Failed, MessageType.ResubmissionRequest),
            (ResolutionStatus.DeadLettered, MessageType.SkipRequest),
            (ResolutionStatus.Completed, MessageType.ResubmissionRequest),
            (ResolutionStatus.Unsupported, MessageType.ResubmissionRequest),
        };

        foreach (var (settled, control) in cases)
        {
            var eventId = Id($"co-{settled}-{control}");
            Assert.IsTrue(await Upload(store, settled, eventId, "s1", endpointId,
                GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1", parentMessageId: "req-1")));

            Assert.IsTrue(
                await store.UploadPendingMessage(eventId, "s1", endpointId,
                    GuardedEvent(endpointId, eventId, "s1", control, Guid.NewGuid().ToString(), parentMessageId: "err-1")),
                $"{control} must reopen a {settled} row");
            Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(endpointId, eventId, "s1"))!.ResolutionStatus);
        }

        var counts = await store.DownloadEndpointStateCount(endpointId);
        Assert.AreEqual(cases.Length, counts.PendingCount);
    }

    [TestMethod]
    public async Task Write_answered_by_row_is_refused()
    {
        var store = CreateStore();
        var endpointId = Id("ep-ancestor");
        var eventId = Id("an-1");

        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ResubmissionRequest, "rs-1")));
        Assert.IsTrue(await store.UploadCompletedMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ResolutionResponse, "rsp-1", parentMessageId: "rs-1")));

        // The row already holds rs-1's outcome, so a rescheduled copy of rs-1 is stale even though
        // a control request may otherwise reopen a settled row.
        Assert.IsFalse(await store.UploadPendingMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ResubmissionRequest, "rs-1")));
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(endpointId, eventId))!.ResolutionStatus);

        // A genuinely new resubmission still reopens it.
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ResubmissionRequest, "rs-2")));
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(endpointId, eventId, "s1"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Pending_write_after_ArchiveFailedEvent_revives_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-archive-revive");
        var eventId = Id("ar-1");

        Assert.IsTrue(await store.UploadFailedMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1")));
        await store.ArchiveFailedEvent(eventId, "s1", endpointId);

        // No companion case for "archive, then a stale request copy": the providers legitimately
        // differ there. The in-memory store hard-removes on archive (so the copy is applied against
        // an absent row) while Cosmos and SQL soft-delete (so the Failed row is still there to
        // refuse it). See StaleWriteGuard and Spec 030 section 5.5.
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ResubmissionRequest, "rs-1")));
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(endpointId, eventId, "s1"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task TryArchiveUnresolvedEvent_archives_only_the_inspected_version()
    {
        var store = CreateStore();
        var endpointId = Id("ep-guarded-archive");
        var eventId = Id("ga-1");
        Assert.IsTrue(await store.UploadFailedMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1")));
        var stored = await store.GetFailedEvent(endpointId, eventId, "s1");

        Assert.IsFalse(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, "err-0", stored!.UpdatedAt));
        Assert.IsFalse(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt.AddSeconds(-1)));
        Assert.IsFalse(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.DeadLettered, stored.LastMessageId, stored.UpdatedAt));
        Assert.IsFalse(await store.TryArchiveUnresolvedEvent(eventId, "other-session", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
        Assert.IsNotNull(await store.GetFailedEvent(endpointId, eventId, "s1"));

        Assert.IsTrue(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
        Assert.IsNull(await store.GetFailedEvent(endpointId, eventId, "s1"));
        Assert.IsNull(await store.GetEvent(endpointId, eventId));
        Assert.IsFalse(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
    }

    [TestMethod]
    public async Task TryArchiveUnresolvedEvent_lets_exactly_one_concurrent_caller_win()
    {
        var store = CreateStore();
        var endpointId = Id("ep-guarded-race");
        var eventId = Id("gr-1");
        Assert.IsTrue(await store.UploadFailedMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1")));
        var stored = await store.GetFailedEvent(endpointId, eventId, "s1");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored!.LastMessageId, stored.UpdatedAt))));

        Assert.AreEqual(1, results.Count(won => won));
    }

    [TestMethod]
    public async Task TryRestoreArchivedEvent_reverses_a_claim_on_the_same_version()
    {
        var store = CreateStore();
        var endpointId = Id("ep-guarded-restore");
        var eventId = Id("gs-1");
        Assert.IsTrue(await store.UploadFailedMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1")));
        var stored = await store.GetFailedEvent(endpointId, eventId, "s1");

        Assert.IsFalse(await store.TryRestoreArchivedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored!.LastMessageId, stored.UpdatedAt),
            "A live row has nothing to restore.");
        Assert.IsTrue(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
        Assert.IsFalse(await store.TryRestoreArchivedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, "err-0", stored.UpdatedAt));

        Assert.IsTrue(await store.TryRestoreArchivedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
        var restored = await store.GetFailedEvent(endpointId, eventId, "s1");
        Assert.IsNotNull(restored);
        Assert.AreEqual(stored.LastMessageId, restored.LastMessageId);
        Assert.AreEqual(stored.UpdatedAt.Ticks, restored.UpdatedAt.Ticks);
        Assert.AreEqual(1, (await store.DownloadEndpointStateCount(endpointId)).FailedCount);
        Assert.IsFalse(await store.TryRestoreArchivedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
    }

    [TestMethod]
    public async Task TryRestoreArchivedEvent_never_overwrites_a_revived_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-guarded-revived");
        var eventId = Id("gv-1");
        Assert.IsTrue(await store.UploadFailedMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ErrorResponse, "err-1")));
        var stored = await store.GetFailedEvent(endpointId, eventId, "s1");
        Assert.IsTrue(await store.TryArchiveUnresolvedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored!.LastMessageId, stored.UpdatedAt));
        Assert.IsTrue(await store.UploadPendingMessage(eventId, "s1", endpointId,
            GuardedEvent(endpointId, eventId, "s1", MessageType.ResubmissionRequest, "rs-1")));

        Assert.IsFalse(await store.TryRestoreArchivedEvent(eventId, "s1", endpointId, ResolutionStatus.Failed, stored.LastMessageId, stored.UpdatedAt));
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetEvent(endpointId, eventId))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task First_write_of_every_status_reports_applied()
    {
        // Guards the SQL @@ROWCOUNT path: an insert must report true just like a replace.
        var store = CreateStore();
        var endpointId = Id("ep-first-write");
        var statuses = new[]
        {
            ResolutionStatus.Pending, ResolutionStatus.Deferred, ResolutionStatus.Failed,
            ResolutionStatus.DeadLettered, ResolutionStatus.Unsupported, ResolutionStatus.Skipped,
            ResolutionStatus.Completed,
        };

        foreach (var status in statuses)
        {
            var eventId = Id($"fw-{status}");
            Assert.IsTrue(
                await Upload(store, status, eventId, "s1", endpointId,
                    GuardedEvent(endpointId, eventId, "s1", MessageType.EventRequest, $"m-{status}")),
                $"the first {status} write must report applied");
        }
    }

    [TestMethod]
    public async Task All_lookup_resolution_statuses_round_trip()
    {
        var store = CreateStore();
        var endpointId = Id("ep-all");
        var statuses = new (string EventId, Func<string, string, string, UnresolvedEvent, Task<bool>> Up, Func<string, string, string, Task<UnresolvedEvent?>> Get, ResolutionStatus Expected)[]
        {
            (Id("p1"), store.UploadPendingMessage, store.GetPendingEvent, ResolutionStatus.Pending),
            (Id("d1"), store.UploadDeferredMessage, store.GetDeferredEvent, ResolutionStatus.Deferred),
            (Id("f1"), store.UploadFailedMessage, store.GetFailedEvent, ResolutionStatus.Failed),
            (Id("dl1"), store.UploadDeadletteredMessage, store.GetDeadletteredEvent, ResolutionStatus.DeadLettered),
            (Id("u1"), store.UploadUnsupportedMessage, store.GetUnsupportedEvent, ResolutionStatus.Unsupported),
        };

        foreach (var (eventId, up, get, expected) in statuses)
        {
            await up(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
            var fetched = await get(endpointId, eventId, "s1");
            Assert.IsNotNull(fetched);
            Assert.AreEqual(expected, fetched.ResolutionStatus, $"Round-trip failed for {expected}");
        }
    }

    [TestMethod]
    public async Task Skipped_and_Completed_statuses_round_trip_via_completed_listing()
    {
        var store = CreateStore();
        var endpointId = Id("ep-sk");
        var skippedId = Id("sk1");
        var completedId = Id("c1");
        var skippedEvent = SampleEvent(endpointId, skippedId, "s1");
        skippedEvent.MessageContent.ErrorContent = new ErrorContent
        {
            ErrorText = "InvalidOperationException: discarded by PartnerFailureDispositionClassifier",
            ErrorType = nameof(InvalidOperationException),
        };
        await store.UploadSkippedMessage(skippedId, "s1", endpointId, skippedEvent);
        await store.UploadCompletedMessage(completedId, "s1", endpointId, SampleEvent(endpointId, completedId, "s1"));

        var completed = (await store.GetCompletedEventsOnEndpoint(endpointId)).ToList();
        var skipped = await store.GetEvent(endpointId, skippedId);

        Assert.AreEqual(1, completed.Count);
        Assert.AreEqual(completedId, completed[0].EventId);
        Assert.AreEqual(ResolutionStatus.Skipped, skipped!.ResolutionStatus);
        Assert.AreEqual(nameof(InvalidOperationException), skipped.MessageContent.ErrorContent!.ErrorType);
        StringAssert.Contains(skipped.MessageContent.ErrorContent.ErrorText, "PartnerFailureDispositionClassifier");
    }

    [TestMethod]
    public async Task StoreMessage_then_GetMessage_round_trips()
    {
        var store = CreateStore();
        var eventId = Id("evt-x");
        var messageId = Id("msg-x");
        var entity = new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = Id("ep1"),
            SessionId = "s1",
            CorrelationId = "c1",
            EventTypeId = "OrderPlaced",
            CloudEventId = "ce-message-1",
            CloudEventSource = "urn:nimbus:conformance",
            CloudEventType = "com.nimbus.order-placed.v1",
            CloudEventSubject = "orders/42",
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        };
        await store.StoreMessage(entity);

        var fetched = await store.GetMessage(eventId, messageId);

        Assert.AreEqual(eventId, fetched!.EventId);
        Assert.AreEqual(messageId, fetched.MessageId);
        Assert.AreEqual("ce-message-1", fetched.CloudEventId);
        Assert.AreEqual("urn:nimbus:conformance", fetched.CloudEventSource);
        Assert.AreEqual("com.nimbus.order-placed.v1", fetched.CloudEventType);
        Assert.AreEqual("orders/42", fetched.CloudEventSubject);
    }

    [TestMethod]
    public async Task CloudEvent_identity_round_trips_on_tracked_event()
    {
        var store = CreateStore();
        var endpointId = Id("ep-ce");
        var eventId = Id("event-ce");
        var stored = SampleEvent(endpointId, eventId, "session-ce");
        stored.CloudEventId = "ce-event-1";
        stored.CloudEventSource = "urn:nimbus:conformance";
        stored.CloudEventType = "com.nimbus.order-placed.v1";
        stored.CloudEventSubject = "orders/42";

        await store.UploadFailedMessage(eventId, "session-ce", endpointId, stored);

        var fetched = await store.GetFailedEvent(endpointId, eventId, "session-ce");
        Assert.AreEqual("ce-event-1", fetched!.CloudEventId);
        Assert.AreEqual("urn:nimbus:conformance", fetched.CloudEventSource);
        Assert.AreEqual("com.nimbus.order-placed.v1", fetched.CloudEventType);
        Assert.AreEqual("orders/42", fetched.CloudEventSubject);
    }

    [TestMethod]
    public async Task Native_message_and_event_keep_CloudEvent_identity_null()
    {
        var store = CreateStore();
        var endpointId = Id("ep-native");
        var eventId = Id("event-native");
        var messageId = Id("message-native");

        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = endpointId,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        });
        await store.UploadPendingMessage(eventId, "session-native", endpointId,
            SampleEvent(endpointId, eventId, "session-native"));

        var message = await store.GetMessage(eventId, messageId);
        var trackedEvent = await store.GetPendingEvent(endpointId, eventId, "session-native");

        Assert.IsNull(message!.CloudEventId);
        Assert.IsNull(message.CloudEventSource);
        Assert.IsNull(message.CloudEventType);
        Assert.IsNull(message.CloudEventSubject);
        Assert.IsNull(trackedEvent!.CloudEventId);
        Assert.IsNull(trackedEvent.CloudEventSource);
        Assert.IsNull(trackedEvent.CloudEventType);
        Assert.IsNull(trackedEvent.CloudEventSubject);
    }

    [TestMethod]
    public async Task GetLatestEventRequestMessage_returns_newest_request_with_payload()
    {
        var store = CreateStore();
        var eventId = Id("evt-lr");
        var ep = Id("ep1");
        var now = DateTime.UtcNow;

        // Older EventRequest carrying a payload.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m1"), EndpointId = ep,
            MessageType = MessageType.EventRequest,
            EnqueuedTimeUtc = now.AddMinutes(-10),
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":1}" } },
        });
        // Newer ResubmissionRequest carrying a payload — this is the one that should win.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m2"), EndpointId = ep,
            MessageType = MessageType.ResubmissionRequest,
            EnqueuedTimeUtc = now.AddMinutes(-2),
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":2}" } },
        });
        // Newest message overall, but not a request type — must be ignored.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m3"), EndpointId = ep,
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = now,
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":3}" } },
        });

        var latest = await store.GetLatestEventRequestMessage(eventId);

        Assert.IsNotNull(latest);
        Assert.AreEqual("{\"v\":2}", latest.MessageContent?.EventContent?.EventJson);
    }

    [TestMethod]
    public async Task GetLatestEventRequestMessage_returns_null_when_no_request_carries_payload()
    {
        var store = CreateStore();
        var eventId = Id("evt-lr-none");
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = Id("m1"), EndpointId = Id("ep1"),
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        });

        var latest = await store.GetLatestEventRequestMessage(eventId);

        Assert.IsNull(latest);
    }

    [TestMethod]
    public async Task StoreMessageAudit_appends_to_history()
    {
        var store = CreateStore();
        var eventId = Id("evt-aud");
        await store.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName = Id("bob"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });

        var audits = (await store.GetMessageAudits(eventId)).ToList();

        Assert.AreEqual(2, audits.Count);
    }

    [TestMethod]
    public async Task DownloadEndpointStateCount_groups_by_status()
    {
        var store = CreateStore();
        var endpointId = Id("ep-cnt");
        await store.UploadPendingMessage(Id("p1"), "s1", endpointId, SampleEvent(endpointId, Id("p1"), "s1"));
        await store.UploadPendingMessage(Id("p2"), "s2", endpointId, SampleEvent(endpointId, Id("p2"), "s2"));
        await store.UploadFailedMessage(Id("f1"), "s1", endpointId, SampleEvent(endpointId, Id("f1"), "s1"));

        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.AreEqual(2, counts.PendingCount);
        Assert.AreEqual(1, counts.FailedCount);
        Assert.AreEqual(0, counts.DeadletterCount);
    }

    [TestMethod]
    public async Task DownloadEndpointStateCount_reports_oldest_open_failure()
    {
        var store = CreateStore();
        var endpointId = Id("ep-oldest-failure");

        // Uploads happen in real time, oldest first: providers differ on whether UpdatedAt
        // is the caller's value or stamped on write, but both preserve upload order.
        async Task Upload(Func<string, string, string, UnresolvedEvent, Task<bool>> upload, string name)
        {
            await upload(Id(name), name, endpointId, SampleEvent(endpointId, Id(name), name));
            await Task.Delay(50);
        }

        // Only open failures count: older Pending work and removed failures are ignored.
        await Upload(store.UploadPendingMessage, "pending");
        await Upload(store.UploadFailedMessage, "removed");
        await store.RemoveMessage(Id("removed"), "removed", endpointId);
        await Upload(store.UploadFailedMessage, "oldest");
        await Upload(store.UploadDeadletteredMessage, "dead");
        await Upload(store.UploadFailedMessage, "newest");

        var counts = await store.DownloadEndpointStateCount(endpointId);

        AssertUtcNear(await StoredUpdatedAt(store, endpointId, Id("oldest")), counts.OldestFailureAt);

        // Dead-lettered messages are failures too: they alone set the value.
        var deadOnly = Id("ep-oldest-dead-only");
        await store.UploadDeadletteredMessage(Id("dead-only"), "s1", deadOnly, SampleEvent(deadOnly, Id("dead-only"), "s1"));
        AssertUtcNear(
            await StoredUpdatedAt(store, deadOnly, Id("dead-only")),
            (await store.DownloadEndpointStateCount(deadOnly)).OldestFailureAt);

        var pendingOnly = Id("ep-oldest-pending-only");
        await store.UploadPendingMessage(Id("p-only"), "s1", pendingOnly, SampleEvent(pendingOnly, Id("p-only"), "s1"));
        Assert.IsNull((await store.DownloadEndpointStateCount(pendingOnly)).OldestFailureAt);
        Assert.IsNull((await store.DownloadEndpointStateCount(Id("ep-oldest-empty"))).OldestFailureAt);
    }

    private static async Task<DateTime> StoredUpdatedAt(IMessageTrackingStore store, string endpointId, string eventId)
    {
        var page = await store.DownloadEndpointStatePaging(endpointId, 100, string.Empty);
        return page!.EnrichedUnresolvedEvents.Single(e => e.EventId == eventId).UpdatedAt;
    }

    // The uploads are >= 50 ms apart, so a 5 ms tolerance still identifies the row while
    // absorbing provider rounding (SqlClient may bind DateTime as legacy DATETIME).
    private static void AssertUtcNear(DateTime expected, DateTime? actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(DateTimeKind.Utc, actual.Value.Kind);
        Assert.IsTrue(
            Math.Abs((actual.Value - DateTime.SpecifyKind(expected, DateTimeKind.Utc)).TotalMilliseconds) < 5,
            $"Expected ~{expected:O}, got {actual.Value:O}.");
    }

    [TestMethod]
    public async Task Endpoint_counts_preserve_all_unfinished_statuses_and_exclude_terminal_history()
    {
        var store = CreateStore();
        var endpointId = Id("ep-active-counts");
        var uploads = new Func<string, string, string, UnresolvedEvent, Task<bool>>[]
        {
            store.UploadPendingMessage, store.UploadDeferredMessage, store.UploadFailedMessage,
            store.UploadDeadletteredMessage, store.UploadUnsupportedMessage,
            store.UploadCompletedMessage, store.UploadSkippedMessage,
        };
        foreach (var upload in uploads)
        {
            var eventId = Id(Guid.NewGuid().ToString("N"));
            await upload(eventId, "session", endpointId, SampleEvent(endpointId, eventId, "session"));
        }

        // Pending handoffs remain part of Pending, irrespective of their substatus.
        var handoff = SampleEvent(endpointId, Id("handoff-count"), "session");
        handoff.PendingSubStatus = "Handoff";
        await store.UploadPendingMessage(handoff.EventId, "session", endpointId, handoff);
        var other = SampleEvent(Id("other-endpoint"), Id("other-event"), "session");
        await store.UploadFailedMessage(other.EventId, "session", other.EndpointId, other);

        var counts = await store.DownloadEndpointStateCount(endpointId);
        Assert.AreEqual(2, counts.PendingCount);
        Assert.AreEqual(1, counts.DeferredCount);
        Assert.AreEqual(1, counts.FailedCount);
        Assert.AreEqual(1, counts.DeadletterCount);
        Assert.AreEqual(1, counts.UnsupportedCount);
        Assert.AreEqual(endpointId, counts.EndpointId);

        var empty = await store.DownloadEndpointStateCount(Id("empty-endpoint"));
        Assert.AreEqual(0, empty.PendingCount + empty.DeferredCount + empty.FailedCount + empty.DeadletterCount + empty.UnsupportedCount);
    }

    [TestMethod]
    public async Task DownloadEndpointSessionStateCount_returns_pending_and_deferred_event_ids()
    {
        var store = CreateStore();
        var endpointId = Id("ep-session");
        var pendingId = Id("sess-pending");
        var deferredId = Id("sess-deferred");
        var failedId = Id("sess-failed");
        await store.UploadPendingMessage(pendingId, "session-1", endpointId, SampleEvent(endpointId, pendingId, "session-1"));
        await store.UploadDeferredMessage(deferredId, "session-1", endpointId, SampleEvent(endpointId, deferredId, "session-1"));
        await store.UploadFailedMessage(failedId, "session-1", endpointId, SampleEvent(endpointId, failedId, "session-1"));

        var session = await store.DownloadEndpointSessionStateCount(endpointId, "session-1");

        CollectionAssert.Contains(session.PendingEvents.ToList(), StoredId(pendingId, "session-1"));
        CollectionAssert.Contains(session.DeferredEvents.ToList(), StoredId(deferredId, "session-1"));
        CollectionAssert.DoesNotContain(session.PendingEvents.Concat(session.DeferredEvents).ToList(), StoredId(failedId, "session-1"));
    }

    [TestMethod]
    public async Task DownloadEndpointSessionStateCountBatch_groups_sessions()
    {
        var store = CreateStore();
        var endpointId = Id("ep-session-batch");
        var sessionOneId = Id("batch-pending");
        var sessionTwoId = Id("batch-deferred");
        await store.UploadPendingMessage(sessionOneId, "session-1", endpointId, SampleEvent(endpointId, sessionOneId, "session-1"));
        await store.UploadDeferredMessage(sessionTwoId, "session-2", endpointId, SampleEvent(endpointId, sessionTwoId, "session-2"));

        var sessions = (await store.DownloadEndpointSessionStateCountBatch(endpointId, new[] { "session-1", "session-2" })).ToList();

        var sessionOne = sessions.Single(s => s.SessionId == "session-1");
        var sessionTwo = sessions.Single(s => s.SessionId == "session-2");
        CollectionAssert.Contains(sessionOne.PendingEvents.ToList(), StoredId(sessionOneId, "session-1"));
        CollectionAssert.Contains(sessionTwo.DeferredEvents.ToList(), StoredId(sessionTwoId, "session-2"));
    }

    [TestMethod]
    public async Task DownloadEndpointStatePaging_lists_actionable_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-state");
        var pendingId = Id("state-pending");
        var deferredId = Id("state-deferred");
        var failedId = Id("state-failed");
        var deadletteredId = Id("state-dlq");
        var unsupportedId = Id("state-unsupported");
        var completedId = Id("state-completed");
        await store.UploadPendingMessage(pendingId, "s1", endpointId, SampleEvent(endpointId, pendingId, "s1"));
        await store.UploadDeferredMessage(deferredId, "s1", endpointId, SampleEvent(endpointId, deferredId, "s1"));
        await store.UploadFailedMessage(failedId, "s1", endpointId, SampleEvent(endpointId, failedId, "s1"));
        await store.UploadDeadletteredMessage(deadletteredId, "s1", endpointId, SampleEvent(endpointId, deadletteredId, "s1"));
        await store.UploadUnsupportedMessage(unsupportedId, "s1", endpointId, SampleEvent(endpointId, unsupportedId, "s1"));
        await store.UploadCompletedMessage(completedId, "s1", endpointId, SampleEvent(endpointId, completedId, "s1"));

        var state = await store.DownloadEndpointStatePaging(endpointId, pageSize: 20, continuationToken: string.Empty);

        CollectionAssert.Contains(state!.PendingEvents.ToList(), StoredId(pendingId, "s1"));
        CollectionAssert.Contains(state.DeferredEvents.ToList(), StoredId(deferredId, "s1"));
        CollectionAssert.Contains(state.FailedEvents.ToList(), StoredId(failedId, "s1"));
        CollectionAssert.Contains(state.DeadletteredEvents.ToList(), StoredId(deadletteredId, "s1"));
        CollectionAssert.Contains(state.UnsupportedEvents.ToList(), StoredId(unsupportedId, "s1"));
        CollectionAssert.Contains(state.GetAllUnresolvedEvents.ToList(), StoredId(deadletteredId, "s1"));
        CollectionAssert.Contains(state.GetAllUnresolvedEvents.ToList(), StoredId(unsupportedId, "s1"));
        CollectionAssert.DoesNotContain(state.GetAllUnresolvedEvents.ToList(), StoredId(completedId, "s1"));
        Assert.AreEqual(5, state.EnrichedUnresolvedEvents.Count());
    }

    [TestMethod]
    public async Task GetEventsByIds_accepts_ids_returned_by_endpoint_state()
    {
        var store = CreateStore();
        var endpointId = Id("ep-state-ids");
        var pendingId = Id("ids-pending");
        var failedId = Id("ids-failed");
        await store.UploadPendingMessage(pendingId, "s1", endpointId, SampleEvent(endpointId, pendingId, "s1"));
        await store.UploadFailedMessage(failedId, "s2", endpointId, SampleEvent(endpointId, failedId, "s2"));

        var state = await store.DownloadEndpointStatePaging(endpointId, pageSize: 20, continuationToken: string.Empty);
        var events = await store.GetEventsByIds(endpointId, state!.GetAllUnresolvedEvents);

        var eventIds = events.Select(e => e.EventId).ToList();
        CollectionAssert.Contains(eventIds, pendingId);
        CollectionAssert.Contains(eventIds, failedId);
    }

    [TestMethod]
    public async Task GetEndpointErrorList_returns_failed_and_deferred_event_ids()
    {
        var store = CreateStore();
        var endpointId = Id("ep-error-list");
        var failedId = Id("error-failed");
        var deferredId = Id("error-deferred");
        var pendingId = Id("error-pending");
        await store.UploadFailedMessage(failedId, "s1", endpointId, SampleEvent(endpointId, failedId, "s1"));
        await store.UploadDeferredMessage(deferredId, "s2", endpointId, SampleEvent(endpointId, deferredId, "s2"));
        await store.UploadPendingMessage(pendingId, "s3", endpointId, SampleEvent(endpointId, pendingId, "s3"));

        var errorList = await store.GetEndpointErrorList(endpointId);

        StringAssert.Contains(errorList, StoredId(failedId, "s1"));
        StringAssert.Contains(errorList, StoredId(deferredId, "s2"));
        Assert.IsFalse(errorList.Contains(StoredId(pendingId, "s3"), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetBlockedEventsOnSession_returns_pending_and_deferred_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-blocked");
        var pendingId = Id("blocked-pending");
        var deferredId = Id("blocked-deferred");
        var failedId = Id("blocked-failed");
        await store.UploadPendingMessage(pendingId, "session-1", endpointId, SampleEvent(endpointId, pendingId, "session-1"));
        await store.UploadDeferredMessage(deferredId, "session-1", endpointId, SampleEvent(endpointId, deferredId, "session-1"));
        await store.UploadFailedMessage(failedId, "session-1", endpointId, SampleEvent(endpointId, failedId, "session-1"));

        var page = await store.GetBlockedEventsOnSession(endpointId, "session-1", 0, 100);

        Assert.AreEqual(2, page.Total);
        Assert.AreEqual(2, page.Items.Count);
        Assert.IsTrue(page.Items.Any(e => e.EventId == pendingId && e.Status == ResolutionStatus.Pending.ToString()));
        Assert.IsTrue(page.Items.Any(e => e.EventId == deferredId && e.Status == ResolutionStatus.Deferred.ToString()));
        Assert.IsFalse(page.Items.Any(e => e.EventId == failedId));
    }

    [TestMethod]
    public async Task PurgeMessages_removes_only_the_target_session()
    {
        var store = CreateStore();
        var endpointId = Id("ep-purge");
        var purgedPendingId = Id("purge-pending");
        var purgedDeferredId = Id("purge-deferred");
        var keptId = Id("purge-kept");
        await store.UploadPendingMessage(purgedPendingId, "session-purged", endpointId, SampleEvent(endpointId, purgedPendingId, "session-purged"));
        await store.UploadDeferredMessage(purgedDeferredId, "session-purged", endpointId, SampleEvent(endpointId, purgedDeferredId, "session-purged"));
        await store.UploadPendingMessage(keptId, "session-kept", endpointId, SampleEvent(endpointId, keptId, "session-kept"));

        var purged = await store.PurgeMessages(endpointId, "session-purged");

        Assert.IsTrue(purged);
        var purgedPage = await store.GetBlockedEventsOnSession(endpointId, "session-purged", 0, 100);
        Assert.AreEqual(0, purgedPage.Total);
        Assert.AreEqual(0, purgedPage.Items.Count);
        var keptPage = await store.GetBlockedEventsOnSession(endpointId, "session-kept", 0, 100);
        Assert.AreEqual(1, keptPage.Total);
        Assert.IsTrue(keptPage.Items.Any(e => e.EventId == keptId));
    }

    [TestMethod]
    public async Task PurgeStoredMessages_removes_only_the_target_endpoints_history()
    {
        var store = CreateStore();
        var endpointId = Id("ep-purge-history");
        var otherEndpointId = Id("ep-purge-history-other");
        var purgedEventA = Id("purge-history-a");
        var purgedEventB = Id("purge-history-b");
        var keptEvent = Id("purge-history-kept");
        var t = DateTime.UtcNow;
        await store.StoreMessage(new MessageEntity { EventId = purgedEventA, MessageId = Id("ph-m1"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = purgedEventA, MessageId = Id("ph-m2"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = purgedEventB, MessageId = Id("ph-m3"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = keptEvent, MessageId = Id("ph-m4"), EndpointId = otherEndpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });

        var removed = await store.PurgeStoredMessages(endpointId);

        Assert.AreEqual(3, removed);
        Assert.IsFalse((await store.GetEventHistory(purgedEventA)).Any());
        Assert.IsFalse((await store.GetEventHistory(purgedEventB)).Any());
        Assert.AreEqual(1, (await store.GetEventHistory(keptEvent)).Count());
        Assert.AreEqual(0, await store.PurgeStoredMessages(endpointId), "A second purge has nothing left to remove.");
    }

    [TestMethod]
    public async Task GetBlockedEventsOnSession_pages_results_and_reports_total()
    {
        var store = CreateStore();
        var endpointId = Id("ep-blocked-paged");

        // Seed 5 blocked siblings on the same session (3 pending + 2 deferred).
        for (var i = 0; i < 3; i++)
        {
            var id = Id($"paged-pending-{i}");
            await store.UploadPendingMessage(id, "session-paged", endpointId, SampleEvent(endpointId, id, "session-paged"));
        }
        for (var i = 0; i < 2; i++)
        {
            var id = Id($"paged-deferred-{i}");
            await store.UploadDeferredMessage(id, "session-paged", endpointId, SampleEvent(endpointId, id, "session-paged"));
        }

        var firstPage = await store.GetBlockedEventsOnSession(endpointId, "session-paged", 0, 2);
        Assert.AreEqual(5, firstPage.Total);
        Assert.AreEqual(2, firstPage.Items.Count);

        var secondPage = await store.GetBlockedEventsOnSession(endpointId, "session-paged", 2, 2);
        Assert.AreEqual(5, secondPage.Total);
        Assert.AreEqual(2, secondPage.Items.Count);

        var thirdPage = await store.GetBlockedEventsOnSession(endpointId, "session-paged", 4, 2);
        Assert.AreEqual(5, thirdPage.Total);
        Assert.AreEqual(1, thirdPage.Items.Count);

        // No overlap across pages.
        var combined = firstPage.Items.Concat(secondPage.Items).Concat(thirdPage.Items)
            .Select(e => e.EventId).Distinct().Count();
        Assert.AreEqual(5, combined);
    }

    [TestMethod]
    public async Task GetBlockedEventsOnSession_nonpositive_take_is_capped_at_default_page_size()
    {
        var store = CreateStore();
        var endpointId = Id("ep-blocked-cap");

        // One more event than the default page size, so an uncapped implementation
        // (the historical take<=0 => int.MaxValue behavior) returns 101 and fails.
        var seeded = PaginationLimits.DefaultPageSize + 1;
        for (var i = 0; i < seeded; i++)
        {
            var id = Id($"cap-{i}");
            await store.UploadPendingMessage(id, "session-cap", endpointId, SampleEvent(endpointId, id, "session-cap"));
        }

        var zeroTake = await store.GetBlockedEventsOnSession(endpointId, "session-cap", 0, 0);
        Assert.AreEqual(seeded, zeroTake.Total);
        Assert.AreEqual(PaginationLimits.DefaultPageSize, zeroTake.Items.Count);

        var negativeTake = await store.GetBlockedEventsOnSession(endpointId, "session-cap", 0, -1);
        Assert.AreEqual(PaginationLimits.DefaultPageSize, negativeTake.Items.Count);
    }

    [TestMethod]
    public async Task GetBlockedEventsOnSession_take_above_max_is_capped()
    {
        var store = CreateStore();
        var endpointId = Id("ep-blocked-max");

        for (var i = 0; i < 3; i++)
        {
            var id = Id($"max-{i}");
            await store.UploadPendingMessage(id, "session-max", endpointId, SampleEvent(endpointId, id, "session-max"));
        }

        // Proves the request routes through PaginationLimits.Resolve without
        // throwing or overflowing; the exact clamp arithmetic is unit-tested.
        var page = await store.GetBlockedEventsOnSession(endpointId, "session-max", 0, PaginationLimits.MaxPageSize + 5);
        Assert.AreEqual(3, page.Total);
        Assert.AreEqual(3, page.Items.Count);
    }

    [TestMethod]
    public async Task GetInvalidEventsOnSession_returns_publisher_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-invalid");
        var publisherId = Id("invalid-publisher");
        var subscriberId = Id("invalid-subscriber");
        await store.UploadPendingMessage(publisherId, "session-1", endpointId, SampleEvent(endpointId, publisherId, "session-1", EndpointRole.Publisher));
        await store.UploadPendingMessage(subscriberId, "session-1", endpointId, SampleEvent(endpointId, subscriberId, "session-1", EndpointRole.Subscriber));

        var invalid = (await store.GetInvalidEventsOnSession(endpointId)).ToList();

        Assert.AreEqual(1, invalid.Count);
        Assert.AreEqual(publisherId, invalid[0].EventId);
    }

    [TestMethod]
    public async Task RemoveMessage_drops_from_state_count()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rm");
        var eventId = Id("rm1");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        var removed = await store.RemoveMessage(eventId, "s1", endpointId);
        var counts = await store.DownloadEndpointStateCount(endpointId);

        Assert.IsTrue(removed);
        Assert.AreEqual(0, counts.FailedCount);
    }

    [TestMethod]
    public async Task GetEventsByFilter_returns_matching_endpoint_events()
    {
        var store = CreateStore();
        var endpointId = Id("ep-flt");
        var otherEndpointId = Id("ep-other");
        await store.UploadFailedMessage(Id("ef1"), "s1", endpointId, SampleEvent(endpointId, Id("ef1"), "s1"));
        await store.UploadFailedMessage(Id("ef2"), "s2", endpointId, SampleEvent(endpointId, Id("ef2"), "s2"));
        await store.UploadFailedMessage(Id("ef3"), "s1", otherEndpointId, SampleEvent(otherEndpointId, Id("ef3"), "s1"));

        var resp = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId }, continuationToken: null!, maxSearchItemsCount: 50);

        var events = resp.Events.ToList();
        Assert.AreEqual(2, events.Count, "filter by endpoint should drop other-endpoint rows");
        Assert.IsTrue(events.All(e => e.EndpointId == endpointId));
    }

    [TestMethod]
    public async Task GetEventsByFilter_status_filter_narrows_results()
    {
        var store = CreateStore();
        var endpointId = Id("ep-st");
        await store.UploadFailedMessage(Id("st1"), "s1", endpointId, SampleEvent(endpointId, Id("st1"), "s1"));
        await store.UploadCompletedMessage(Id("st2"), "s1", endpointId, SampleEvent(endpointId, Id("st2"), "s1"));
        await store.UploadCompletedMessage(Id("st3"), "s1", endpointId, SampleEvent(endpointId, Id("st3"), "s1"));

        var resp = await store.GetEventsByFilter(
            new EventFilter { EndPointId = endpointId, ResolutionStatus = new List<string> { "Completed" } },
            continuationToken: null!,
            maxSearchItemsCount: 50);

        var events = resp.Events.ToList();
        Assert.AreEqual(2, events.Count);
        Assert.IsTrue(events.All(e => e.ResolutionStatus == ResolutionStatus.Completed));
    }

    [TestMethod]
    public async Task SearchMessages_returns_matching_messages()
    {
        var store = CreateStore();
        var endpointId = Id("ep-msg");
        var otherEndpointId = Id("ep-other");
        var t = DateTime.UtcNow;
        await store.StoreMessage(new MessageEntity { EventId = Id("se1"), MessageId = Id("m1"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("se2"), MessageId = Id("m2"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("se3"), MessageId = Id("m3"), EndpointId = otherEndpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });

        var resp = await store.SearchMessages(new MessageFilter { EndpointId = endpointId }, continuationToken: null, maxItemCount: 50);

        var messages = resp.Messages.ToList();
        Assert.AreEqual(2, messages.Count);
        Assert.IsTrue(messages.All(m => m.EndpointId == endpointId));
    }

    [TestMethod]
    public async Task PendingHandoff_fields_round_trip()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff");
        var eventId = Id("handoff-1");
        var sessionId = "session-handoff";
        var expectedBy = new DateTime(2026, 06, 01, 09, 00, 00, DateTimeKind.Utc);

        var sample = SampleEvent(endpointId, eventId, sessionId);
        sample.MessageType = MessageType.PendingHandoffResponse;
        sample.PendingSubStatus = "Handoff";
        sample.HandoffReason = "DMF import in progress";
        sample.ExternalJobId = "DMF-JOB-42";
        sample.ExpectedBy = expectedBy;

        await store.UploadPendingMessage(eventId, sessionId, endpointId, sample);

        var fetched = await store.GetPendingEvent(endpointId, eventId, sessionId);

        Assert.AreEqual("Handoff", fetched!.PendingSubStatus);
        Assert.AreEqual("DMF import in progress", fetched.HandoffReason);
        Assert.AreEqual("DMF-JOB-42", fetched.ExternalJobId);
        Assert.IsNotNull(fetched.ExpectedBy);
        // Tick comparison, not ToUniversalTime(): SQL Server returns datetime2 as
        // Kind=Unspecified, and ToUniversalTime() on Unspecified applies the local
        // machine's offset — the assertion would only pass on UTC machines.
        Assert.AreEqual(expectedBy, fetched.ExpectedBy.Value);
    }

    [TestMethod]
    public async Task SearchAudits_returns_matching_audits()
    {
        var store = CreateStore();
        var auditor = Id("alice");
        await store.StoreMessageAudit(Id("evt-sa1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit(Id("evt-sa1"), new MessageAuditEntity { AuditorName = Id("bob"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });
        await store.StoreMessageAudit(Id("evt-sa2"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Comment });

        var resp = await store.SearchAudits(new AuditFilter { AuditorName = auditor }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(2, items.Count);
        Assert.IsTrue(items.All(a => a.Audit.AuditorName == auditor));
    }

    // ───── Spec 008: round-trip of the four new MessageAuditEntity fields ─────

    [TestMethod]
    public async Task StoreMessageAudit_roundtrips_AccessDenied_and_Data_fields()
    {
        var store = CreateStore();
        var eventId = Id("evt-aud-new");
        var endpointId = Id("ep-aud-new");
        var dataPayload = "{\"filter\":\"OrderPlaced\",\"endpointId\":\"" + endpointId + "\"}";

        await store.StoreMessageAudit(eventId, new MessageAuditEntity
        {
            AuditorName = Id("alice"),
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.SearchEvents,
            AccessDenied = true,
            Data = dataPayload,
            EventId = eventId,
            EndpointId = endpointId,
            CloudEventId = "ce-audit-1",
            CloudEventSource = "urn:nimbus:audit",
            CloudEventType = "com.nimbus.audit.v1",
            CloudEventSubject = "audits/42",
        }, endpointId, eventTypeId: "OrderPlaced");

        var audits = (await store.GetMessageAudits(eventId)).ToList();

        Assert.AreEqual(1, audits.Count);
        var audit = audits[0];
        Assert.IsTrue(audit.AccessDenied, "AccessDenied should round-trip as true");
        Assert.AreEqual(dataPayload, audit.Data, "Data payload should round-trip");
        // Mirrored EventId/EndpointId are populated by the writer where supported
        // (SQL: read from row columns; Cosmos: from the serialized document; in-memory: from the entity itself).
        Assert.AreEqual(eventId, audit.EventId);
        Assert.AreEqual(endpointId, audit.EndpointId);
        Assert.AreEqual("ce-audit-1", audit.CloudEventId);
        Assert.AreEqual("urn:nimbus:audit", audit.CloudEventSource);
        Assert.AreEqual("com.nimbus.audit.v1", audit.CloudEventType);
        Assert.AreEqual("audits/42", audit.CloudEventSubject);
    }

    [TestMethod]
    public async Task StoreMessageAudit_legacy_row_defaults_AccessDenied_false_and_Data_null()
    {
        // Per spec FR-074: rows written without the new fields (the legacy code
        // path, or a row created from a pre-migration entity) MUST project with
        // AccessDenied = false and Data = null so the audit-list UI renders unchanged.
        var store = CreateStore();
        var eventId = Id("evt-aud-legacy");
        await store.StoreMessageAudit(eventId, new MessageAuditEntity
        {
            AuditorName = Id("legacy-user"),
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.Resubmit,
        });

        var audits = (await store.GetMessageAudits(eventId)).ToList();

        Assert.AreEqual(1, audits.Count);
        Assert.IsFalse(audits[0].AccessDenied, "legacy row should default AccessDenied to false");
        Assert.IsNull(audits[0].Data, "legacy row should default Data to null");
        Assert.IsNull(audits[0].CloudEventId, "legacy native-message audit should not gain a CloudEvent id");
        Assert.IsNull(audits[0].CloudEventSource);
        Assert.IsNull(audits[0].CloudEventType);
        Assert.IsNull(audits[0].CloudEventSubject);
    }

    [TestMethod]
    public async Task SearchAudits_projects_AccessDenied_and_Data_fields()
    {
        var store = CreateStore();
        var auditor = Id("eve");
        var eventId = Id("evt-sa-new");
        var endpointId = Id("ep-sa-new");

        await store.StoreMessageAudit(eventId, new MessageAuditEntity
        {
            AuditorName = auditor,
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.GetEventDetails,
            AccessDenied = true,
            Data = "context-payload",
            EventId = eventId,
            EndpointId = endpointId,
            CloudEventId = "ce-audit-search-1",
            CloudEventSource = "urn:nimbus:audit-search",
            CloudEventType = "com.nimbus.audit-search.v1",
            CloudEventSubject = "audits/search/42",
        }, endpointId);

        var resp = await store.SearchAudits(new AuditFilter { AuditorName = auditor }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(1, items.Count);
        Assert.IsTrue(items[0].Audit.AccessDenied);
        Assert.AreEqual("context-payload", items[0].Audit.Data);
        Assert.AreEqual("ce-audit-search-1", items[0].Audit.CloudEventId);
        Assert.AreEqual("urn:nimbus:audit-search", items[0].Audit.CloudEventSource);
        Assert.AreEqual("com.nimbus.audit-search.v1", items[0].Audit.CloudEventType);
        Assert.AreEqual("audits/search/42", items[0].Audit.CloudEventSubject);
    }

    [TestMethod]
    public async Task GetResubmitCounts_counts_resubmit_audits_per_event_excluding_denied()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rc");
        var eventA = Id("evt-rc-a");
        var eventB = Id("evt-rc-b");

        // eventA: two granted resubmits (one plain, one with changes), one denied
        // attempt (must not count), one unrelated audit type (must not count).
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = eventA, EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.ResubmitWithChanges, EventId = eventA, EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("mallory"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, AccessDenied = true, EventId = eventA, EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(eventA, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip, EventId = eventA, EndpointId = endpointId }, endpointId);
        // eventB: no resubmit audits at all — must be absent from the result.
        await store.StoreMessageAudit(eventB, new MessageAuditEntity { AuditorName = Id("bob"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Comment, EventId = eventB, EndpointId = endpointId }, endpointId);

        var counts = await store.GetResubmitCounts(endpointId, new[] { eventA, eventB });

        Assert.AreEqual(2, counts.GetValueOrDefault(eventA), "granted Resubmit + ResubmitWithChanges count; denied and unrelated audits do not");
        Assert.IsFalse(counts.ContainsKey(eventB), "events without resubmit audits are absent (missing = 0)");
    }

    [TestMethod]
    public async Task GetResubmitCounts_returns_empty_for_empty_input_or_foreign_endpoint()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rc2");
        var eventId = Id("evt-rc2");
        await store.StoreMessageAudit(eventId, new MessageAuditEntity { AuditorName = Id("alice"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = eventId, EndpointId = endpointId }, endpointId);

        var emptyIds = await store.GetResubmitCounts(endpointId, Array.Empty<string>());
        Assert.AreEqual(0, emptyIds.Count);

        // Audits are endpoint-scoped: the same event id queried under another
        // endpoint must not leak counts across endpoints.
        var foreign = await store.GetResubmitCounts(Id("ep-other"), new[] { eventId });
        Assert.AreEqual(0, foreign.Count);
    }

    [TestMethod]
    public async Task SearchAudits_scopes_by_endpointId()
    {
        var store = CreateStore();
        var endpointId = Id("ep-audit-scope");
        var auditor = Id("carol");
        await store.StoreMessageAudit(Id("evt-as1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = Id("evt-as1"), EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(Id("evt-as2"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip, EventId = Id("evt-as2"), EndpointId = Id("ep-audit-other") }, Id("ep-audit-other"));

        var resp = await store.SearchAudits(new AuditFilter { EndpointId = endpointId }, continuationToken: null, maxItemCount: 50);

        var items = resp.Audits.ToList();
        Assert.AreEqual(1, items.Count, "only the requested endpoint's audits are returned");
        Assert.AreEqual(Id("evt-as1"), items[0].EventId);
        Assert.AreEqual(endpointId, items[0].EndpointId, "EndpointId is projected so callers can build routes");
    }

    [TestMethod]
    public async Task SearchAudits_EndpointIdExact_excludes_prefix_siblings()
    {
        // Authorization-scoped queries must not let a manager of "Orders" read
        // "OrdersArchive" rows through the default prefix semantics.
        var store = CreateStore();
        var endpointId = Id("Orders");
        var sibling = Id("Orders") + "Archive";
        var auditor = Id("dave");
        await store.StoreMessageAudit(Id("evt-ex1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, EventId = Id("evt-ex1"), EndpointId = endpointId }, endpointId);
        await store.StoreMessageAudit(Id("evt-ex2"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip, EventId = Id("evt-ex2"), EndpointId = sibling }, sibling);

        var prefix = await store.SearchAudits(new AuditFilter { EndpointId = endpointId }, continuationToken: null, maxItemCount: 50);
        Assert.AreEqual(2, prefix.Audits.Count(), "default prefix semantics include the sibling");

        var exact = await store.SearchAudits(new AuditFilter { EndpointId = endpointId, EndpointIdExact = true }, continuationToken: null, maxItemCount: 50);
        var items = exact.Audits.ToList();
        Assert.AreEqual(1, items.Count, "exact scope excludes prefix-siblings");
        Assert.AreEqual(Id("evt-ex1"), items[0].EventId);

        // Equality stays case-insensitive like the rest of the contract.
        var upper = await store.SearchAudits(new AuditFilter { EndpointId = endpointId.ToUpperInvariant(), EndpointIdExact = true }, continuationToken: null, maxItemCount: 50);
        Assert.AreEqual(1, upper.Audits.Count());
    }

    [TestMethod]
    public async Task SetEventReport_roundtrips_and_updates_in_place()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rep");
        var eventId = Id("evt-rep");

        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: Id("alice"), ticketId: "INC0042");

        var reports = await store.GetEventReports(endpointId, new[] { eventId });
        Assert.IsTrue(reports.TryGetValue(eventId, out var report));
        Assert.IsTrue(report!.IsReported);
        Assert.AreEqual(Id("alice"), report.ReportedBy);
        Assert.AreEqual("INC0042", report.TicketId);
        Assert.IsNotNull(report.ReportedAtUtc);

        // Upsert: a second toggle for the same (endpoint, event) replaces the
        // marker instead of adding a row.
        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: Id("bob"), ticketId: "JIRA-7");
        reports = await store.GetEventReports(endpointId, new[] { eventId });
        Assert.AreEqual(1, reports.Count);
        Assert.AreEqual(Id("bob"), reports[eventId].ReportedBy);
        Assert.AreEqual("JIRA-7", reports[eventId].TicketId);
    }

    [TestMethod]
    public async Task SetEventReport_clearing_drops_the_ticket_reference()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rep2");
        var eventId = Id("evt-rep2");

        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: Id("alice"), ticketId: "INC0042");
        await store.SetEventReport(endpointId, eventId, isReported: false, reportedBy: Id("alice"), ticketId: "INC0042");

        var reports = await store.GetEventReports(endpointId, new[] { eventId });
        Assert.IsTrue(reports.TryGetValue(eventId, out var report));
        Assert.IsFalse(report!.IsReported);
        Assert.IsNull(report.TicketId, "clearing the marker must drop the ticket reference");
    }

    [TestMethod]
    public async Task EventReports_do_not_collide_on_ambiguous_composite_keys()
    {
        // ("a_b", "c") and ("a", "b_c") concatenate to the same string — the
        // store key must be the (endpointId, eventId) PAIR, not a joined string.
        var store = CreateStore();
        var prefix = Id("ep-amb");
        await store.SetEventReport($"{prefix}_x", "y", isReported: true, reportedBy: Id("alice"), ticketId: "T-1");

        var other = await store.GetEventReports(prefix, new[] { "x_y" });
        Assert.AreEqual(0, other.Count, "a report on endpoint '{prefix}_x' must not surface for endpoint '{prefix}'");

        var own = await store.GetEventReports($"{prefix}_x", new[] { "y" });
        Assert.AreEqual(1, own.Count);
    }

    [TestMethod]
    public async Task GetEventReports_batches_and_scopes_by_endpoint()
    {
        var store = CreateStore();
        var endpointId = Id("ep-rep3");
        var reported = Id("evt-rep3-a");
        var unreported = Id("evt-rep3-b");
        await store.SetEventReport(endpointId, reported, isReported: true, reportedBy: Id("alice"), ticketId: null);

        var reports = await store.GetEventReports(endpointId, new[] { reported, unreported });
        Assert.AreEqual(1, reports.Count, "events never reported are absent (missing = not reported)");
        Assert.IsTrue(reports.ContainsKey(reported));
        Assert.IsNull(reports[reported].TicketId, "reporting without a ticket keeps TicketId null");

        var empty = await store.GetEventReports(endpointId, Array.Empty<string>());
        Assert.AreEqual(0, empty.Count);

        // Markers are endpoint-scoped: the same event id under another endpoint
        // must not leak.
        var foreign = await store.GetEventReports(Id("ep-rep3-other"), new[] { reported });
        Assert.AreEqual(0, foreign.Count);
    }

    // ───── Prefix-search semantics (cross-provider contract) ─────
    // ID-like filter fields match by case-insensitive PREFIX on every provider:
    // Cosmos STARTSWITH(x, y, true), SQL Server LIKE 'y%' (CI collation),
    // in-memory StartsWith(OrdinalIgnoreCase). Mid-string fragments do NOT match.

    [TestMethod]
    public async Task SearchMessages_matches_eventId_by_case_insensitive_prefix()
    {
        var store = CreateStore();
        var endpointId = Id("ep-prefix");
        var eventId = Id("prefix-event-abc");
        var t = DateTime.UtcNow;
        await store.StoreMessage(new MessageEntity { EventId = eventId, MessageId = Id("pm1"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("other-event"), MessageId = Id("pm2"), EndpointId = endpointId, EnqueuedTimeUtc = t, MessageContent = new MessageContent() });

        var prefix = eventId[..(_scope.Length + 8)];
        var byPrefix = await store.SearchMessages(new MessageFilter { EventId = prefix }, null, 50);
        Assert.AreEqual(1, byPrefix.Messages.Count(), "A leading fragment of the event id must match (prefix semantics).");
        Assert.AreEqual(eventId, byPrefix.Messages.Single().EventId);

        var byUppercasePrefix = await store.SearchMessages(new MessageFilter { EventId = prefix.ToUpperInvariant() }, null, 50);
        Assert.AreEqual(1, byUppercasePrefix.Messages.Count(), "Prefix matching must be case-insensitive.");

        var midFragment = eventId[4..12];
        var byMidFragment = await store.SearchMessages(new MessageFilter { EventId = midFragment }, null, 50);
        Assert.AreEqual(0, byMidFragment.Messages.Count(), "A mid-string fragment must NOT match (no substring semantics).");
    }

    [TestMethod]
    public async Task GetEventsByFilter_matches_eventId_by_case_insensitive_prefix()
    {
        var store = CreateStore();
        var endpointId = Id("ep-evprefix");
        var eventId = Id("prefix-ev-abc");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadFailedMessage(Id("other-ev"), "s1", endpointId, SampleEvent(endpointId, Id("other-ev"), "s1"));

        var prefix = eventId[..(_scope.Length + 8)];
        var byPrefix = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = prefix }, null!, 50);
        Assert.AreEqual(1, byPrefix.Events.Count(), "A leading fragment of the event id must match (prefix semantics).");
        Assert.AreEqual(eventId, byPrefix.Events.Single().EventId);

        var byUppercasePrefix = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = prefix.ToUpperInvariant() }, null!, 50);
        Assert.AreEqual(1, byUppercasePrefix.Events.Count(), "Prefix matching must be case-insensitive.");

        var midFragment = eventId[4..12];
        var byMidFragment = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = midFragment }, null!, 50);
        Assert.AreEqual(0, byMidFragment.Events.Count(), "A mid-string fragment must NOT match (no substring semantics).");
    }

    [TestMethod]
    public async Task GetEventsByFilter_matches_endpointId_exactly_not_by_prefix()
    {
        // Callers scope authorization (endpoint search) and bulk writes (skip) by
        // EndPointId, so a Reader of "A" must not see "AB" rows and a skip on "A"
        // must not rewrite them.
        var store = CreateStore();
        var endpointId = Id("ep-exact");
        var sibling = endpointId + "b";
        var eventId = Id("ev-exact");
        const string sessionId = "session-exact";
        await store.UploadFailedMessage(eventId, sessionId, endpointId, SampleEvent(endpointId, eventId, sessionId));
        await store.UploadFailedMessage(eventId, sessionId, sibling, SampleEvent(sibling, eventId, sessionId));

        var resp = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId }, null!, 50);

        var events = resp.Events.ToList();
        Assert.AreEqual(1, events.Count, "A prefix-sibling endpoint's row must not match.");
        Assert.AreEqual(endpointId, events[0].EndpointId);

        var bySessionPrefix = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, SessionId = "session-ex" }, null!, 50);
        Assert.AreEqual(1, bySessionPrefix.Events.Count(), "Session id stays a prefix match.");
    }

    private static UnresolvedEvent FailureEvent(
        string endpointId,
        string eventId,
        string sessionId,
        string? errorText = null,
        string eventTypeId = "OrderPlaced",
        string lastMessageId = "last-message")
    {
        var ev = SampleEvent(endpointId, eventId, sessionId);
        ev.EventTypeId = eventTypeId;
        ev.LastMessageId = lastMessageId;
        ev.MessageContent = new MessageContent
        {
            ErrorContent = errorText == null ? null : new ErrorContent { ErrorText = errorText },
        };
        return ev;
    }

    [TestMethod]
    public async Task GetFailedEventsAcrossEndpoints_returns_only_failures_on_the_given_endpoints()
    {
        var store = CreateStore();
        var a = Id("fx-a");
        var b = Id("fx-b");
        var excluded = Id("fx-c");
        await store.UploadFailedMessage(Id("fa1"), "s1", a, FailureEvent(a, Id("fa1"), "s1"));
        await store.UploadDeadletteredMessage(Id("fa2"), "s2", a, FailureEvent(a, Id("fa2"), "s2"));
        await store.UploadCompletedMessage(Id("fa3"), "s3", a, FailureEvent(a, Id("fa3"), "s3"));
        await store.UploadPendingMessage(Id("fa4"), "s4", a, FailureEvent(a, Id("fa4"), "s4"));
        await store.UploadUnsupportedMessage(Id("fb1"), "s1", b, FailureEvent(b, Id("fb1"), "s1"));
        await store.UploadFailedMessage(Id("fc1"), "s1", excluded, FailureEvent(excluded, Id("fc1"), "s1"));

        var resp = await store.GetFailedEventsAcrossEndpoints(new EventFilter(), new[] { a, b }, null, 50);

        CollectionAssert.AreEquivalent(
            new[] { Id("fa1"), Id("fa2"), Id("fb1") },
            resp.Events.Select(e => e.EventId).ToList());
        Assert.IsTrue(string.IsNullOrEmpty(resp.ContinuationToken), "A complete first page has no continuation token.");
    }

    [TestMethod]
    public async Task GetFailedEventsAcrossEndpoints_intersects_the_status_filter_with_failure_statuses()
    {
        var store = CreateStore();
        var a = Id("fs-a");
        await store.UploadFailedMessage(Id("fs1"), "s1", a, FailureEvent(a, Id("fs1"), "s1"));
        await store.UploadDeadletteredMessage(Id("fs2"), "s2", a, FailureEvent(a, Id("fs2"), "s2"));
        await store.UploadCompletedMessage(Id("fs3"), "s3", a, FailureEvent(a, Id("fs3"), "s3"));

        var deadLettered = await store.GetFailedEventsAcrossEndpoints(
            new EventFilter { ResolutionStatus = new List<string> { "DeadLettered", "Completed" } }, new[] { a }, null, 50);
        CollectionAssert.AreEquivalent(new[] { Id("fs2") }, deadLettered.Events.Select(e => e.EventId).ToList());

        var completedOnly = await store.GetFailedEventsAcrossEndpoints(
            new EventFilter { ResolutionStatus = new List<string> { "Completed" } }, new[] { a }, null, 50);
        Assert.AreEqual(0, completedOnly.Events.Count(), "Non-failure statuses are never returned.");
    }

    [TestMethod]
    public async Task GetFailedEventsAcrossEndpoints_with_no_endpoints_returns_nothing()
    {
        var store = CreateStore();
        var a = Id("fe-a");
        await store.UploadFailedMessage(Id("fe1"), "s1", a, FailureEvent(a, Id("fe1"), "s1"));

        var resp = await store.GetFailedEventsAcrossEndpoints(new EventFilter(), Array.Empty<string>(), null, 50);

        Assert.AreEqual(0, resp.Events.Count());
    }

    [TestMethod]
    public async Task GetFailedEventsAcrossEndpoints_pages_every_row_exactly_once_newest_first()
    {
        var store = CreateStore();
        var endpoints = new[] { Id("fp-a"), Id("fp-b"), Id("fp-c") };
        var expected = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var endpointId = endpoints[i % 3];
            var eventId = Id($"fp{i}");
            var ev = FailureEvent(endpointId, eventId, $"s{i}");
            ev.UpdatedAt = DateTime.UtcNow;
            await store.UploadFailedMessage(eventId, $"s{i}", endpointId, ev);
            expected.Add(eventId);
            await Task.Delay(5);
        }

        var seen = new List<UnresolvedEvent>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await store.GetFailedEventsAcrossEndpoints(new EventFilter(), endpoints, token, 2);
            var rows = page.Events.ToList();
            Assert.IsTrue(rows.Count <= 2, "A page never exceeds the requested size.");
            seen.AddRange(rows);
            token = page.ContinuationToken;
            pages++;
            Assert.IsTrue(pages <= 10, "Paging must terminate.");
        }
        while (!string.IsNullOrEmpty(token));

        CollectionAssert.AreEquivalent(expected, seen.Select(e => e.EventId).ToList(), "Every row exactly once across pages.");
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.IsTrue(seen[i - 1].UpdatedAt.Ticks >= seen[i].UpdatedAt.Ticks, "Rows are ordered newest first across pages.");
        }
    }

    [TestMethod]
    public async Task GetFailedEventsAcrossEndpoints_filters_error_text_and_last_message_id()
    {
        var store = CreateStore();
        var a = Id("ft-a");
        var b = Id("ft-b");
        await store.UploadFailedMessage(Id("ft1"), "s1", a, FailureEvent(a, Id("ft1"), "s1", "HttpRequestException: 503 Service Unavailable", lastMessageId: "msg-alpha-1"));
        await store.UploadDeadletteredMessage(Id("ft2"), "s2", b, FailureEvent(b, Id("ft2"), "s2", "TimeoutException: handler exceeded", lastMessageId: "msg-beta-2"));
        await store.UploadFailedMessage(Id("ft3"), "s3", b, FailureEvent(b, Id("ft3"), "s3", null, lastMessageId: "msg-alpha-3"));

        var byError = await store.GetFailedEventsAcrossEndpoints(new EventFilter { ErrorText = "service UNAVAILABLE" }, new[] { a, b }, null, 50);
        CollectionAssert.AreEquivalent(new[] { Id("ft1") }, byError.Events.Select(e => e.EventId).ToList(), "Error text is a case-insensitive substring match.");

        var byMessageId = await store.GetFailedEventsAcrossEndpoints(new EventFilter { LastMessageId = "MSG-ALPHA" }, new[] { a, b }, null, 50);
        CollectionAssert.AreEquivalent(new[] { Id("ft1"), Id("ft3") }, byMessageId.Events.Select(e => e.EventId).ToList(), "Last message id is a case-insensitive prefix match.");

        var byMidFragment = await store.GetFailedEventsAcrossEndpoints(new EventFilter { LastMessageId = "alpha" }, new[] { a, b }, null, 50);
        Assert.AreEqual(0, byMidFragment.Events.Count(), "Last message id has no substring semantics.");

        var errorKept = byError.Events.Single();
        Assert.AreEqual("HttpRequestException: 503 Service Unavailable", errorKept.MessageContent?.ErrorContent?.ErrorText, "ErrorContent survives the search projection.");
    }

    [TestMethod]
    public async Task GetFailedEventsAcrossEndpoints_matches_the_filter_endpointId_exactly()
    {
        var store = CreateStore();
        var a = Id("fe-a");
        var ab = a + "b";
        await store.UploadFailedMessage(Id("fe1"), "s1", a, FailureEvent(a, Id("fe1"), "s1"));
        await store.UploadFailedMessage(Id("fe2"), "s1", ab, FailureEvent(ab, Id("fe2"), "s1"));

        var resp = await store.GetFailedEventsAcrossEndpoints(new EventFilter { EndPointId = a }, new[] { a, ab }, null, 50);

        CollectionAssert.AreEquivalent(new[] { Id("fe1") }, resp.Events.Select(e => e.EventId).ToList(), "The filter's endpoint id is an exact match, as in GetEventsByFilter.");
    }

    [TestMethod]
    public async Task GetEventsByFilter_honours_error_text_and_last_message_id()
    {
        var store = CreateStore();
        var a = Id("fg-a");
        await store.UploadFailedMessage(Id("fg1"), "s1", a, FailureEvent(a, Id("fg1"), "s1", "SqlException: deadlock victim", lastMessageId: "m-one"));
        await store.UploadFailedMessage(Id("fg2"), "s2", a, FailureEvent(a, Id("fg2"), "s2", "NullReferenceException", lastMessageId: "m-two"));

        var byError = await store.GetEventsByFilter(new EventFilter { EndPointId = a, ErrorText = "DEADLOCK" }, null!, 50);
        CollectionAssert.AreEquivalent(new[] { Id("fg1") }, byError.Events.Select(e => e.EventId).ToList());

        var byMessageId = await store.GetEventsByFilter(new EventFilter { EndPointId = a, LastMessageId = "m-tw" }, null!, 50);
        CollectionAssert.AreEquivalent(new[] { Id("fg2") }, byMessageId.Events.Select(e => e.EventId).ToList());
    }

    [TestMethod]
    public async Task GetFailedEventHistogram_counts_per_bucket_endpoint_and_status()
    {
        var store = CreateStore();
        var a = Id("fh-a");
        var b = Id("fh-b");
        var excluded = Id("fh-c");
        await store.UploadFailedMessage(Id("fh1"), "s1", a, FailureEvent(a, Id("fh1"), "s1"));
        await store.UploadFailedMessage(Id("fh2"), "s2", a, FailureEvent(a, Id("fh2"), "s2"));
        await store.UploadDeadletteredMessage(Id("fh3"), "s3", a, FailureEvent(a, Id("fh3"), "s3"));
        await store.UploadCompletedMessage(Id("fh4"), "s4", a, FailureEvent(a, Id("fh4"), "s4"));
        await store.UploadUnsupportedMessage(Id("fh5"), "s5", b, FailureEvent(b, Id("fh5"), "s5"));
        await store.UploadFailedMessage(Id("fh6"), "s6", excluded, FailureEvent(excluded, Id("fh6"), "s6"));

        // Events are stamped "now"; with the window starting 90 minutes ago and hourly buckets
        // they all land in the second bucket, which starts at from + 1h.
        var from = DateTime.UtcNow.AddMinutes(-90);
        var to = DateTime.UtcNow.AddMinutes(30);
        var histogram = await store.GetFailedEventHistogram(new EventFilter(), new[] { a, b }, from, to, TimeSpan.FromHours(1));

        Assert.IsFalse(histogram.Truncated);
        var expectedStart = from.AddHours(1).Ticks;
        Assert.IsTrue(histogram.Rows.All(r => r.BucketStartUtc.Ticks == expectedStart), "Buckets are aligned to the window start.");
        var cells = histogram.Rows.ToDictionary(r => (r.EndpointId, r.Status), r => r.Count);
        Assert.AreEqual(3, cells.Count, "Only non-empty cells are returned.");
        Assert.AreEqual(2, cells[(a, "Failed")]);
        Assert.AreEqual(1, cells[(a, "DeadLettered")]);
        Assert.AreEqual(1, cells[(b, "Unsupported")]);
    }

    [TestMethod]
    public async Task GetFailedEventHistogram_honours_the_window_and_the_filter()
    {
        var store = CreateStore();
        var a = Id("fw-a");
        await store.UploadFailedMessage(Id("fw1"), "s1", a, FailureEvent(a, Id("fw1"), "s1", eventTypeId: "OrderPlaced"));
        await store.UploadFailedMessage(Id("fw2"), "s2", a, FailureEvent(a, Id("fw2"), "s2", eventTypeId: "OrderCancelled"));

        var future = await store.GetFailedEventHistogram(
            new EventFilter(), new[] { a }, DateTime.UtcNow.AddMinutes(5), DateTime.UtcNow.AddMinutes(65), TimeSpan.FromMinutes(5));
        Assert.AreEqual(0, future.Rows.Count, "Events before the window start are excluded.");

        var from = DateTime.UtcNow.AddMinutes(-30);
        var byType = await store.GetFailedEventHistogram(
            new EventFilter { EventTypeId = new List<string> { "OrderCancelled" } }, new[] { a }, from, DateTime.UtcNow.AddMinutes(30), TimeSpan.FromHours(1));
        Assert.AreEqual(1, byType.Rows.Sum(r => r.Count), "The histogram applies the search filter.");
    }

    // ───── Date bounds within one second ─────
    // Cosmos DB stores DateTimes as ISO strings with trailing fractional zeros trimmed
    // ("…T10:00:00Z", "…T10:00:00.1Z") and compares them as strings, where '.' sorts before 'Z',
    // so within one second string order is not chronological. Cosmos keeps the caller's
    // timestamps, which puts these rows exactly on that edge; SQL Server and the in-memory store
    // stamp UpdatedAt with their own clock, so every bound is derived from the value read back.

    [TestMethod]
    public async Task UpdatedAt_bounds_place_a_row_just_after_a_whole_second_chronologically()
    {
        var store = CreateStore();
        var endpointId = Id("fd-a");
        var eventId = Id("fd1");
        var ev = FailureEvent(endpointId, eventId, "s1");
        ev.UpdatedAt = WholeSecond(DateTime.UtcNow.AddMinutes(-1)).AddMilliseconds(100);
        await store.UploadFailedMessage(eventId, "s1", endpointId, ev);
        var stored = (await store.GetFailedEvent(endpointId, eventId, "s1"))!.UpdatedAt;
        var second = WholeSecond(stored);

        // A bucket-aligned window start, as the Failed page's histogram and bar click use it.
        Assert.IsTrue(await FailedSearchReturns(store, new EventFilter { UpdatedAtFrom = second }, endpointId, eventId),
            "The failure search keeps a row in the first second after its lower bound.");
        Assert.IsTrue(await EventSearchReturns(store, new EventFilter { UpdatedAtFrom = second }, endpointId, eventId),
            "The event search keeps a row in the first second after its lower bound.");
        var bar = await store.GetFailedEventHistogram(new EventFilter(), new[] { endpointId }, second, second.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.AreEqual(1, bar.Rows.Sum(r => r.Count), "The histogram counts a row in the first second of its window.");

        // The same instant as an upper bound: the row lies after it (unless stored exactly on it).
        var onOrBefore = stored <= second;
        Assert.AreEqual(onOrBefore, await FailedSearchReturns(store, new EventFilter { UpdatedAtTo = second }, endpointId, eventId),
            "The failure search upper bound compares chronologically.");
        Assert.AreEqual(onOrBefore, await EventSearchReturns(store, new EventFilter { UpdatedAtTo = second }, endpointId, eventId),
            "The event search upper bound compares chronologically.");
        var previousBar = await store.GetFailedEventHistogram(new EventFilter(), new[] { endpointId }, second.AddMinutes(-1), second, TimeSpan.FromMinutes(1));
        Assert.AreEqual(0, previousBar.Rows.Sum(r => r.Count), "A window ending at the row's second excludes the row.");
    }

    [TestMethod]
    public async Task UpdatedAt_bounds_place_a_whole_second_row_before_the_rest_of_its_second()
    {
        var store = CreateStore();
        var endpointId = Id("fd-b");
        var eventId = Id("fd2");
        var ev = FailureEvent(endpointId, eventId, "s1");
        ev.UpdatedAt = WholeSecond(DateTime.UtcNow.AddMinutes(-1));
        await store.UploadFailedMessage(eventId, "s1", endpointId, ev);
        var stored = (await store.GetFailedEvent(endpointId, eventId, "s1"))!.UpdatedAt;

        // A bar's last instant, as the Failed page's bar click sends it: bucket end minus 1 ms.
        var lastInstant = WholeSecond(stored).AddSeconds(1).AddMilliseconds(-1);
        var onOrBefore = stored <= lastInstant;
        Assert.AreEqual(onOrBefore, await FailedSearchReturns(store, new EventFilter { UpdatedAtTo = lastInstant }, endpointId, eventId),
            "The failure search keeps a row earlier in the second of its upper bound.");
        Assert.AreEqual(onOrBefore, await EventSearchReturns(store, new EventFilter { UpdatedAtTo = lastInstant }, endpointId, eventId),
            "The event search keeps a row earlier in the second of its upper bound.");

        var onOrAfter = stored >= lastInstant;
        Assert.AreEqual(onOrAfter, await FailedSearchReturns(store, new EventFilter { UpdatedAtFrom = lastInstant }, endpointId, eventId),
            "The failure search lower bound compares chronologically.");
        Assert.AreEqual(onOrAfter, await EventSearchReturns(store, new EventFilter { UpdatedAtFrom = lastInstant }, endpointId, eventId),
            "The event search lower bound compares chronologically.");
        var window = await store.GetFailedEventHistogram(new EventFilter(), new[] { endpointId }, lastInstant, lastInstant.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.AreEqual(onOrAfter ? 1 : 0, window.Rows.Sum(r => r.Count), "The histogram window start compares chronologically.");
    }

    [TestMethod]
    public async Task EnqueuedAt_bounds_keep_rows_on_either_edge_of_their_second()
    {
        var store = CreateStore();
        var endpointId = Id("fq-a");
        var second = WholeSecond(DateTime.UtcNow.AddMinutes(-1));
        var justAfter = FailureEvent(endpointId, Id("fq1"), "s1");
        justAfter.EnqueuedTimeUtc = second.AddMilliseconds(100);
        var onTheSecond = FailureEvent(endpointId, Id("fq2"), "s2");
        onTheSecond.EnqueuedTimeUtc = second;
        await store.UploadFailedMessage(Id("fq1"), "s1", endpointId, justAfter);
        await store.UploadFailedMessage(Id("fq2"), "s2", endpointId, onTheSecond);

        // SQL Server rounds EnqueuedTimeUtc to its DATETIME grid, so the bounds come from the
        // values read back. A provider may leave these bounds to the caller (AdminService
        // re-checks them), so the contract is only that no row inside them is dropped.
        var storedAfter = (await store.GetFailedEvent(endpointId, Id("fq1"), "s1"))!.EnqueuedTimeUtc;
        var storedOn = (await store.GetFailedEvent(endpointId, Id("fq2"), "s2"))!.EnqueuedTimeUtc;
        Assert.IsTrue(await EventSearchReturns(store, new EventFilter { EnqueuedAtFrom = WholeSecond(storedAfter) }, endpointId, Id("fq1")),
            "The event search keeps a row in the first second after its EnqueuedAt lower bound.");
        Assert.IsTrue(await EventSearchReturns(store, new EventFilter { EnqueuedAtTo = WholeSecond(storedOn).AddSeconds(1).AddMilliseconds(-1) }, endpointId, Id("fq2")),
            "The event search keeps a row earlier in the second of its EnqueuedAt upper bound.");
    }

    [TestMethod]
    public async Task SearchMessages_EnqueuedAt_bounds_keep_messages_on_either_edge_of_their_second()
    {
        var store = CreateStore();
        var endpointId = Id("mq-a");
        var second = WholeSecond(DateTime.UtcNow.AddMinutes(-1));
        await store.StoreMessage(new MessageEntity { EventId = Id("mq1"), MessageId = Id("mq-m1"), EndpointId = endpointId, EnqueuedTimeUtc = second.AddMilliseconds(100), MessageContent = new MessageContent() });
        await store.StoreMessage(new MessageEntity { EventId = Id("mq2"), MessageId = Id("mq-m2"), EndpointId = endpointId, EnqueuedTimeUtc = second, MessageContent = new MessageContent() });

        // As for events: bounds from the values read back, and only inclusion is the contract.
        var stored = (await store.SearchMessages(new MessageFilter { EndpointId = endpointId }, null, 50)).Messages
            .ToDictionary(m => m.MessageId, m => m.EnqueuedTimeUtc);
        var from = await store.SearchMessages(
            new MessageFilter { EndpointId = endpointId, EnqueuedAtFrom = WholeSecond(stored[Id("mq-m1")]) }, null, 50);
        CollectionAssert.Contains(from.Messages.Select(m => m.MessageId).ToList(), Id("mq-m1"),
            "The message search keeps a message in the first second after its lower bound.");
        var to = await store.SearchMessages(
            new MessageFilter { EndpointId = endpointId, EnqueuedAtTo = WholeSecond(stored[Id("mq-m2")]).AddSeconds(1).AddMilliseconds(-1) }, null, 50);
        CollectionAssert.Contains(to.Messages.Select(m => m.MessageId).ToList(), Id("mq-m2"),
            "The message search keeps a message earlier in the second of its upper bound.");
    }

    [TestMethod]
    public async Task SearchAudits_CreatedAt_lower_bound_keeps_an_audit_later_in_the_same_second()
    {
        var store = CreateStore();
        var auditor = Id("edge-auditor");
        await store.StoreMessageAudit(Id("evt-edge"), new MessageAuditEntity
        {
            AuditorName = auditor,
            AuditTimestamp = WholeSecond(DateTime.UtcNow).AddMilliseconds(100),
            AuditType = MessageAuditType.Comment,
        });

        // Cosmos and SQL Server stamp CreatedAt with their own clock (the in-memory store takes
        // the audit timestamp), so the bound comes from the value read back.
        var createdAt = (await store.SearchAudits(new AuditFilter { AuditorName = auditor }, null, 50)).Audits.Single().CreatedAt;
        var found = await store.SearchAudits(new AuditFilter { AuditorName = auditor, CreatedAtFrom = WholeSecond(createdAt) }, null, 50);
        Assert.AreEqual(1, found.Audits.Count(), "The audit search keeps an audit in the first second after its lower bound.");
    }

    [TestMethod]
    public async Task DownloadEndpointStateCount_oldest_failure_is_chronological_within_one_second()
    {
        var store = CreateStore();
        var endpointId = Id("fo-a");
        var second = WholeSecond(DateTime.UtcNow.AddMinutes(-1));

        // Oldest first, uploaded in real time so the providers that stamp UpdatedAt keep the
        // order. On Cosmos "…:00Z" sorts after "…:00.1Z", which sorts after "…:00.15Z", so a
        // string MIN picks the newest of the three.
        foreach (var (name, milliseconds) in new[] { ("fo1", 0), ("fo2", 100), ("fo3", 150) })
        {
            var ev = FailureEvent(endpointId, Id(name), name);
            ev.UpdatedAt = second.AddMilliseconds(milliseconds);
            await store.UploadFailedMessage(Id(name), name, endpointId, ev);
            await Task.Delay(50);
        }

        AssertUtcNear(await StoredUpdatedAt(store, endpointId, Id("fo1")),
            (await store.DownloadEndpointStateCount(endpointId)).OldestFailureAt);

        // Without the whole-second failure the oldest is ".1Z", though ".15Z" sorts first.
        await store.RemoveMessage(Id("fo1"), "fo1", endpointId);
        AssertUtcNear(await StoredUpdatedAt(store, endpointId, Id("fo2")),
            (await store.DownloadEndpointStateCount(endpointId)).OldestFailureAt);
    }

    private static DateTime WholeSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static async Task<bool> FailedSearchReturns(IMessageTrackingStore store, EventFilter filter, string endpointId, string eventId) =>
        (await store.GetFailedEventsAcrossEndpoints(filter, new[] { endpointId }, null, 50)).Events.Any(e => e.EventId == eventId);

    private static async Task<bool> EventSearchReturns(IMessageTrackingStore store, EventFilter filter, string endpointId, string eventId)
    {
        filter.EndPointId = endpointId;
        return (await store.GetEventsByFilter(filter, null!, 50)).Events.Any(e => e.EventId == eventId);
    }

    [TestMethod]
    public async Task SearchAudits_matches_auditor_by_case_insensitive_prefix()
    {
        var store = CreateStore();
        var auditor = Id("prefix-alice");
        await store.StoreMessageAudit(Id("evt-pa1"), new MessageAuditEntity { AuditorName = auditor, AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit });
        await store.StoreMessageAudit(Id("evt-pa2"), new MessageAuditEntity { AuditorName = Id("someone-else"), AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Skip });

        var prefix = auditor[..(_scope.Length + 8)];
        var byPrefix = await store.SearchAudits(new AuditFilter { AuditorName = prefix }, null, 50);
        Assert.AreEqual(1, byPrefix.Audits.Count(), "A leading fragment of the auditor name must match (prefix semantics).");

        var byUppercasePrefix = await store.SearchAudits(new AuditFilter { AuditorName = prefix.ToUpperInvariant() }, null, 50);
        Assert.AreEqual(1, byUppercasePrefix.Audits.Count(), "Prefix matching must be case-insensitive.");

        var midFragment = auditor[4..12];
        var byMidFragment = await store.SearchAudits(new AuditFilter { AuditorName = midFragment }, null, 50);
        Assert.AreEqual(0, byMidFragment.Audits.Count(), "A mid-string fragment must NOT match (no substring semantics).");
    }

    // ───── Search projection contract: everything round-trips EXCEPT EventJson ─────

    /// <summary>
    /// Drift guard (load-bearing): reflects over every <see cref="UnresolvedEvent"/>
    /// property so that a future property silently missing from a provider's search
    /// projection (e.g. the Cosmos server-side member-init) fails here instead of
    /// shipping truncated search results.
    /// </summary>
    [TestMethod]
    public async Task GetEventsByFilter_roundtrips_every_property_except_EventJson()
    {
        var store = CreateStore();
        var endpointId = Id("ep-drift");
        var eventId = Id("drift-1");
        const string sessionId = "session-drift";

        var stored = FullySetEvent(endpointId, eventId, sessionId);
        await store.UploadFailedMessage(eventId, sessionId, endpointId, stored);

        var resp = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = eventId }, null!, 50);
        var fetched = resp.Events.Single();

        // Provider-stamped on upload — value equality is not part of the contract.
        var exempt = new HashSet<string> { nameof(UnresolvedEvent.UpdatedAt), nameof(UnresolvedEvent.ResolutionStatus) };

        foreach (var prop in typeof(UnresolvedEvent).GetProperties())
        {
            if (exempt.Contains(prop.Name)) continue;

            if (prop.Name == nameof(UnresolvedEvent.MessageContent))
            {
                Assert.IsNull(fetched.MessageContent?.EventContent?.EventJson,
                    "Search results must omit the heavy EventJson payload.");
                Assert.AreEqual(stored.MessageContent.EventContent!.EventTypeId, fetched.MessageContent?.EventContent?.EventTypeId,
                    "EventContent.EventTypeId must survive the search projection.");
                Assert.AreEqual(stored.MessageContent.ErrorContent!.ErrorText, fetched.MessageContent?.ErrorContent?.ErrorText,
                    "ErrorContent must survive the search projection (the error-grouped view reads it).");
                Assert.AreEqual(stored.MessageContent.ErrorContent.ErrorType, fetched.MessageContent?.ErrorContent?.ErrorType);
                Assert.AreEqual(stored.MessageContent.ErrorContent.ExceptionStackTrace, fetched.MessageContent?.ErrorContent?.ExceptionStackTrace);
                continue;
            }

            // DateTime values compare by ticks (Kind-insensitive) — SQL Server
            // returns datetime2 as Kind=Unspecified with unchanged UTC ticks.
            var expectedValue = prop.GetValue(stored);
            var actualValue = prop.GetValue(fetched);
            Assert.AreEqual(expectedValue, actualValue,
                $"UnresolvedEvent.{prop.Name} did not round-trip through search — is it missing from a provider's search projection?");
        }
    }

    [TestMethod]
    public async Task Search_results_omit_EventJson_without_corrupting_the_stored_event()
    {
        var store = CreateStore();
        var endpointId = Id("ep-nomut");
        var eventId = Id("nomut-1");
        const string sessionId = "session-nomut";

        await store.UploadFailedMessage(eventId, sessionId, endpointId, FullySetEvent(endpointId, eventId, sessionId));

        var searched = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, EventId = eventId }, null!, 50);
        Assert.IsNull(searched.Events.Single().MessageContent?.EventContent?.EventJson);

        var direct = await store.GetFailedEvent(endpointId, eventId, sessionId);
        Assert.AreEqual("{\"secret\":\"payload\"}", direct!.MessageContent?.EventContent?.EventJson,
            "Stripping EventJson from SEARCH results must not corrupt the stored event (in-memory must clone).");
    }

    /// <summary>
    /// Drift guard for the message search projection: reflects over every
    /// <see cref="MessageEntity"/> property. See the Cosmos
    /// <c>MessageSearchProjection</c> constant.
    /// </summary>
    [TestMethod]
    public async Task SearchMessages_roundtrips_every_property_except_EventJson()
    {
        var store = CreateStore();
        var endpointId = Id("ep-msgdrift");
        var eventId = Id("msgdrift-1");

        var stored = new MessageEntity
        {
            EventId = eventId,
            MessageId = Id("msgdrift-m1"),
            EventTypeId = "OrderPlaced",
            OriginatingMessageId = "origin-1",
            ParentMessageId = "parent-1",
            From = "publisher-1",
            To = "subscriber-1",
            OriginatingFrom = "adapter-1",
            SessionId = "session-msgdrift",
            CorrelationId = "corr-msgdrift",
            EnqueuedTimeUtc = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc),
            MessageType = MessageType.ErrorResponse,
            EndpointRole = EndpointRole.Subscriber,
            EndpointId = endpointId,
            RetryCount = 3,
            RetryLimit = 7,
            DeadLetterReason = "dl-reason",
            DeadLetterErrorDescription = "dl-description",
            OriginalSessionId = "orig-session",
            DeferralSequence = 5,
            QueueTimeMs = 111,
            ProcessingTimeMs = 222,
            PendingSubStatus = "Handoff",
            HandoffReason = "external work",
            ExternalJobId = "JOB-9",
            ExpectedBy = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
            CloudEventId = "ce-message-search-1",
            CloudEventSource = "urn:nimbus:message-search",
            CloudEventType = "com.nimbus.message-search.v1",
            CloudEventSubject = "messages/42",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{\"secret\":\"payload\"}" },
                ErrorContent = new ErrorContent { ErrorText = "boom", ErrorType = "System.InvalidOperationException", ExceptionStackTrace = "at X" },
            },
        };
        await store.StoreMessage(stored);

        var resp = await store.SearchMessages(new MessageFilter { EventId = eventId }, null, 50);
        var fetched = resp.Messages.Single();

        foreach (var prop in typeof(MessageEntity).GetProperties())
        {
            if (prop.Name == nameof(MessageEntity.MessageContent))
            {
                Assert.IsNull(fetched.MessageContent?.EventContent?.EventJson,
                    "Message search results must omit the heavy EventJson payload.");
                Assert.AreEqual(stored.MessageContent.EventContent.EventTypeId, fetched.MessageContent?.EventContent?.EventTypeId);
                Assert.AreEqual(stored.MessageContent.ErrorContent.ErrorText, fetched.MessageContent?.ErrorContent?.ErrorText,
                    "ErrorContent must survive the message search projection.");
                continue;
            }

            // DateTime values compare by ticks (Kind-insensitive) — see the
            // UnresolvedEvent drift guard.
            var expectedValue = prop.GetValue(stored);
            var actualValue = prop.GetValue(fetched);
            Assert.AreEqual(expectedValue, actualValue,
                $"MessageEntity.{prop.Name} did not round-trip through message search — is it missing from a provider's projection?");
        }
    }

    private static UnresolvedEvent FullySetEvent(string endpointId, string eventId, string sessionId) => new()
    {
        UpdatedAt = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
        EnqueuedTimeUtc = new DateTime(2026, 5, 1, 7, 59, 0, DateTimeKind.Utc),
        EventId = eventId,
        SessionId = sessionId,
        CorrelationId = "corr-drift",
        ResolutionStatus = ResolutionStatus.Failed,
        EndpointRole = EndpointRole.Subscriber,
        EndpointId = endpointId,
        RetryCount = 3,
        RetryLimit = 7,
        MessageType = MessageType.ErrorResponse,
        DeadLetterReason = "dl-reason",
        DeadLetterErrorDescription = "dl-description",
        LastMessageId = "last-1",
        OriginatingMessageId = "origin-1",
        ParentMessageId = "parent-1",
        Reason = "reason-1",
        OriginatingFrom = "adapter-1",
        EventTypeId = "OrderPlaced",
        To = "subscriber-1",
        From = "publisher-1",
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{\"secret\":\"payload\"}" },
            ErrorContent = new ErrorContent { ErrorText = "boom", ErrorType = "System.InvalidOperationException", ExceptionStackTrace = "at X" },
        },
        QueueTimeMs = 111,
        ProcessingTimeMs = 222,
        PendingSubStatus = "Handoff",
        HandoffReason = "external work",
        ExternalJobId = "JOB-9",
        ExpectedBy = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
        CloudEventId = "ce-event-search-1",
        CloudEventSource = "urn:nimbus:event-search",
        CloudEventType = "com.nimbus.event-search.v1",
        CloudEventSubject = "events/42",
    };

    // ───────── Not-found and absent-field contract ─────────
    // Single-row lookups return null for a missing row on every provider; they never throw.
    // Optional string fields round-trip exactly: a null stays null and "" stays "".

    [TestMethod]
    public async Task Single_row_event_getters_return_null_for_missing_rows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-miss");
        var present = Id("present");
        // Seed the endpoint so per-endpoint providers have its storage; only the row is missing.
        await store.UploadPendingMessage(present, "s1", endpointId, SampleEvent(endpointId, present, "s1"));
        var missing = Id("missing");

        Assert.IsNull(await store.GetPendingEvent(endpointId, missing, "s1"));
        Assert.IsNull(await store.GetFailedEvent(endpointId, missing, "s1"));
        Assert.IsNull(await store.GetDeferredEvent(endpointId, missing, "s1"));
        Assert.IsNull(await store.GetDeadletteredEvent(endpointId, missing, "s1"));
        Assert.IsNull(await store.GetUnsupportedEvent(endpointId, missing, "s1"));
        Assert.IsNull(await store.GetEvent(endpointId, missing));
        Assert.IsNull(await store.GetEventById(endpointId, StoredId(missing, "s1")));
        Assert.IsNull(await store.GetPendingHandoffByExternalJobId(endpointId, Id("no-such-job")));
    }

    [TestMethod]
    public async Task Status_getters_return_null_when_the_row_has_another_status_or_session()
    {
        var store = CreateStore();
        var endpointId = Id("ep-other-status");
        var eventId = Id("pending-only");
        await store.UploadPendingMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));

        Assert.IsNull(await store.GetFailedEvent(endpointId, eventId, "s1"));
        Assert.IsNull(await store.GetDeferredEvent(endpointId, eventId, "s1"));
        Assert.IsNull(await store.GetDeadletteredEvent(endpointId, eventId, "s1"));
        Assert.IsNull(await store.GetUnsupportedEvent(endpointId, eventId, "s1"));
        Assert.IsNull(await store.GetPendingEvent(endpointId, eventId, "other-session"));
        Assert.IsNotNull(await store.GetPendingEvent(endpointId, eventId, "s1"));
    }

    [TestMethod]
    public async Task Single_row_message_getters_return_null_for_missing_messages()
    {
        var store = CreateStore();
        var endpointId = Id("ep-miss-msg");
        var missing = Id("missing-evt");

        Assert.IsNull(await store.GetMessage(missing, Id("missing-msg")));
        Assert.IsNull(await store.GetFailedMessage(missing, endpointId));
        Assert.IsNull(await store.GetDeadletteredMessage(missing, endpointId));
        Assert.IsNull(await store.GetLatestEventRequestMessage(missing));
    }

    [TestMethod]
    public async Task Removed_and_archived_rows_are_not_found()
    {
        var store = CreateStore();
        var endpointId = Id("ep-soft-delete");
        var removed = Id("removed");
        var archived = Id("archived");
        await store.UploadPendingMessage(removed, "s1", endpointId, SampleEvent(endpointId, removed, "s1"));
        await store.UploadFailedMessage(archived, "s1", endpointId, SampleEvent(endpointId, archived, "s1"));

        await store.RemoveMessage(removed, "s1", endpointId);
        await store.ArchiveFailedEvent(archived, "s1", endpointId);

        Assert.IsNull(await store.GetPendingEvent(endpointId, removed, "s1"));
        Assert.IsNull(await store.GetEvent(endpointId, removed), "GetEvent must not return a removed row");
        Assert.IsNull(await store.GetEventById(endpointId, StoredId(removed, "s1")));
        Assert.IsNull(await store.GetFailedEvent(endpointId, archived, "s1"));
        Assert.IsNull(await store.GetEvent(endpointId, archived), "GetEvent must not return an archived row");
        Assert.IsNull(await store.GetEventById(endpointId, StoredId(archived, "s1")));
    }

    [TestMethod]
    public async Task GetEvent_returns_the_most_recently_updated_session_row()
    {
        var store = CreateStore();
        var endpointId = Id("ep-latest");
        var eventId = Id("multi-session");
        var older = SampleEvent(endpointId, eventId, "s-old");
        older.UpdatedAt = DateTime.UtcNow.AddMinutes(-10);
        var newer = SampleEvent(endpointId, eventId, "s-new");
        newer.UpdatedAt = DateTime.UtcNow;
        await store.UploadFailedMessage(eventId, "s-old", endpointId, older);
        await store.UploadPendingMessage(eventId, "s-new", endpointId, newer);

        var fetched = await store.GetEvent(endpointId, eventId);

        Assert.IsNotNull(fetched);
        Assert.AreEqual("s-new", fetched.SessionId);
    }

    [TestMethod]
    public async Task GetEventById_looks_up_by_stored_id()
    {
        var store = CreateStore();
        var endpointId = Id("ep-by-id");
        var eventId = Id("by-id");
        await store.UploadFailedMessage(eventId, "s1", endpointId, SampleEvent(endpointId, eventId, "s1"));
        await store.UploadPendingMessage(eventId, "s2", endpointId, SampleEvent(endpointId, eventId, "s2"));

        var first = await store.GetEventById(endpointId, StoredId(eventId, "s1"));
        var second = await store.GetEventById(endpointId, StoredId(eventId, "s2"));

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual("s1", first.SessionId);
        Assert.AreEqual(ResolutionStatus.Failed, first.ResolutionStatus);
        Assert.AreEqual("s2", second.SessionId);
        Assert.AreEqual(ResolutionStatus.Pending, second.ResolutionStatus);
    }

    [TestMethod]
    public async Task GetFailedMessage_returns_the_latest_message_carrying_error_content()
    {
        var store = CreateStore();
        var endpointId = Id("ep-failed-msg");
        var eventId = Id("failed-msg");
        var start = DateTime.UtcNow.AddMinutes(-5);
        MessageEntity Message(string messageId, int minute, MessageType type, bool withError) => new()
        {
            EventId = eventId,
            MessageId = Id(messageId),
            EndpointId = endpointId,
            SessionId = "s1",
            EventTypeId = "OrderPlaced",
            MessageType = type,
            EnqueuedTimeUtc = start.AddMinutes(minute),
            MessageContent = withError
                ? new MessageContent { ErrorContent = new ErrorContent { ErrorText = messageId, ErrorType = "System.InvalidOperationException" } }
                : new MessageContent(),
        };
        await store.StoreMessage(Message("request", 0, MessageType.EventRequest, withError: false));
        await store.StoreMessage(Message("error-1", 1, MessageType.ErrorResponse, withError: true));
        await store.StoreMessage(Message("error-2", 2, MessageType.ErrorResponse, withError: true));
        await store.StoreMessage(Message("resubmit", 3, MessageType.ResubmissionRequest, withError: false));

        var failed = await store.GetFailedMessage(eventId, endpointId);
        var latest = await store.GetDeadletteredMessage(eventId, endpointId);

        Assert.AreEqual(Id("error-2"), failed?.MessageId, "GetFailedMessage returns the newest message that carries ErrorContent");
        Assert.AreEqual(Id("resubmit"), latest?.MessageId, "GetDeadletteredMessage returns the newest message for the event on the endpoint");
    }

    [TestMethod]
    public async Task Optional_event_fields_round_trip_null_and_empty()
    {
        var store = CreateStore();
        var endpointId = Id("ep-absent");
        var nullId = Id("nulls");
        var emptyId = Id("empties");
        var withNulls = SampleEvent(endpointId, nullId, "s1");
        var withEmpties = SampleEvent(endpointId, emptyId, "s1");
        foreach (var (e, value) in new[] { (withNulls, (string?)null), (withEmpties, string.Empty) })
        {
            e.CorrelationId = value;
            e.LastMessageId = value;
            e.OriginatingMessageId = value;
            e.ParentMessageId = value;
            e.OriginatingFrom = value;
            e.Reason = value;
            e.DeadLetterReason = value;
            e.DeadLetterErrorDescription = value;
            e.EventTypeId = value;
            e.To = value;
            e.From = value;
        }
        await store.UploadFailedMessage(nullId, "s1", endpointId, withNulls);
        await store.UploadFailedMessage(emptyId, "s1", endpointId, withEmpties);

        foreach (var (eventId, expected) in new[] { (nullId, (string?)null), (emptyId, string.Empty) })
        {
            var label = expected is null ? "null" : "empty";
            foreach (var fetched in new[]
            {
                await store.GetFailedEvent(endpointId, eventId, "s1"),
                await store.GetEvent(endpointId, eventId),
            })
            {
                Assert.IsNotNull(fetched);
                Assert.AreEqual(expected, fetched.CorrelationId, $"CorrelationId ({label})");
                Assert.AreEqual(expected, fetched.LastMessageId, $"LastMessageId ({label})");
                Assert.AreEqual(expected, fetched.OriginatingMessageId, $"OriginatingMessageId ({label})");
                Assert.AreEqual(expected, fetched.ParentMessageId, $"ParentMessageId ({label})");
                Assert.AreEqual(expected, fetched.OriginatingFrom, $"OriginatingFrom ({label})");
                Assert.AreEqual(expected, fetched.Reason, $"Reason ({label})");
                Assert.AreEqual(expected, fetched.DeadLetterReason, $"DeadLetterReason ({label})");
                Assert.AreEqual(expected, fetched.DeadLetterErrorDescription, $"DeadLetterErrorDescription ({label})");
                Assert.AreEqual(expected, fetched.EventTypeId, $"EventTypeId ({label})");
                Assert.AreEqual(expected, fetched.To, $"To ({label})");
                Assert.AreEqual(expected, fetched.From, $"From ({label})");
            }
        }
    }

    [TestMethod]
    public async Task Optional_message_fields_round_trip_null_and_empty()
    {
        var store = CreateStore();
        var endpointId = Id("ep-absent-msg");
        var eventId = Id("absent-msg");
        MessageEntity Message(string messageId, string? value) => new()
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = endpointId,
            SessionId = value!,
            CorrelationId = value,
            EventTypeId = value!,
            OriginatingMessageId = value,
            ParentMessageId = value,
            From = value,
            To = value!,
            OriginatingFrom = value,
            OriginalSessionId = value,
            DeadLetterReason = value,
            DeadLetterErrorDescription = value,
            PendingSubStatus = value,
            HandoffReason = value,
            ExternalJobId = value,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent(),
        };
        var nullId = Id("nulls");
        var emptyId = Id("empties");
        await store.StoreMessage(Message(nullId, null));
        await store.StoreMessage(Message(emptyId, string.Empty));

        var history = (await store.GetEventHistory(eventId)).ToList();
        foreach (var (messageId, expected) in new[] { (nullId, (string?)null), (emptyId, string.Empty) })
        {
            var label = expected is null ? "null" : "empty";
            foreach (var fetched in new[] { await store.GetMessage(eventId, messageId), history.Single(m => m.MessageId == messageId) })
            {
                Assert.IsNotNull(fetched);
                Assert.AreEqual(expected, fetched.SessionId, $"SessionId ({label})");
                Assert.AreEqual(expected, fetched.CorrelationId, $"CorrelationId ({label})");
                Assert.AreEqual(expected, fetched.EventTypeId, $"EventTypeId ({label})");
                Assert.AreEqual(expected, fetched.OriginatingMessageId, $"OriginatingMessageId ({label})");
                Assert.AreEqual(expected, fetched.ParentMessageId, $"ParentMessageId ({label})");
                Assert.AreEqual(expected, fetched.From, $"From ({label})");
                Assert.AreEqual(expected, fetched.To, $"To ({label})");
                Assert.AreEqual(expected, fetched.OriginatingFrom, $"OriginatingFrom ({label})");
                Assert.AreEqual(expected, fetched.OriginalSessionId, $"OriginalSessionId ({label})");
                Assert.AreEqual(expected, fetched.DeadLetterReason, $"DeadLetterReason ({label})");
                Assert.AreEqual(expected, fetched.DeadLetterErrorDescription, $"DeadLetterErrorDescription ({label})");
                Assert.AreEqual(expected, fetched.PendingSubStatus, $"PendingSubStatus ({label})");
                Assert.AreEqual(expected, fetched.HandoffReason, $"HandoffReason ({label})");
                Assert.AreEqual(expected, fetched.ExternalJobId, $"ExternalJobId ({label})");
                Assert.IsNull(fetched.ExpectedBy, $"ExpectedBy ({label})");
            }
        }
    }

    [TestMethod]
    public async Task StoreMessage_roundtrips_handoff_fields()
    {
        var store = CreateStore();
        var endpointId = Id("ep-handoff-msg");
        var eventId = Id("handoff-msg");
        var expectedBy = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var messageId = Id("handoff-m1");
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = endpointId,
            SessionId = "s1",
            EventTypeId = "OrderPlaced",
            MessageType = MessageType.PendingHandoffResponse,
            EnqueuedTimeUtc = DateTime.UtcNow,
            PendingSubStatus = "Handoff",
            HandoffReason = "external work",
            ExternalJobId = "JOB-9",
            ExpectedBy = expectedBy,
            MessageContent = new MessageContent(),
        });

        var history = (await store.GetEventHistory(eventId)).ToList();
        foreach (var fetched in new[]
        {
            await store.GetMessage(eventId, messageId),
            history.Single(m => m.MessageId == messageId),
            await store.GetDeadletteredMessage(eventId, endpointId),
        })
        {
            Assert.IsNotNull(fetched);
            Assert.AreEqual("Handoff", fetched.PendingSubStatus);
            Assert.AreEqual("external work", fetched.HandoffReason);
            Assert.AreEqual("JOB-9", fetched.ExternalJobId);
            // DateTime equality compares ticks (Kind-insensitive); SQL DATETIME2 reads back Unspecified.
            Assert.AreEqual(expectedBy, fetched.ExpectedBy);
        }
    }
}
