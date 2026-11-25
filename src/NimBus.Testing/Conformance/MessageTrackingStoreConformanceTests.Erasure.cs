#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing.Conformance;

// Spec 049 (erase one event copy) and Spec 050 (delete expired data), pinned for every provider.
public abstract partial class MessageTrackingStoreConformanceTests
{
    /// <summary>
    /// True for providers that delete expired data by age through
    /// <see cref="IMessageTrackingStore.PurgeExpiredData"/>; false for those that expire it
    /// themselves (Cosmos DB, by TTL) and return zeros.
    /// </summary>
    protected virtual bool DeletesExpiredDataByAge => true;

    private const string ErasedType = "OrderPlaced";

    private string Payload(string eventId) =>
        JsonConvert.SerializeObject(new { orderId = eventId, email = "leaked@example.com", secret = Id("secret") });

    private EventErasureRequest EraseRequest(string endpointId, string eventId, string? producer = null, bool composeConfirmed = false) =>
        new(endpointId, eventId, Id("erasure"), producer ?? Id("producer"), ErasedType, composeConfirmed);

    private UnresolvedEvent EventWithPayload(string endpointId, string eventId, string sessionId)
    {
        var row = SampleEvent(endpointId, eventId, sessionId);
        row.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = ErasedType, EventJson = Payload(eventId) },
        };
        return row;
    }

    private Task StoreHistory(IMessageTrackingStore store, string endpointId, string eventId, string messageId, MessageType type = MessageType.EventRequest) =>
        store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = endpointId,
            SessionId = "s1",
            EventTypeId = ErasedType,
            To = endpointId,
            MessageType = type,
            EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = ErasedType, EventJson = Payload(eventId) },
            },
        });

    private static MessageAuditEntity Audit(MessageAuditType type, string? data, string? comment = null) => new()
    {
        AuditorName = "operator@example.com",
        AuditTimestamp = DateTime.UtcNow,
        AuditType = type,
        Data = data,
        Comment = comment,
    };

    private static void AssertEveryPlace(EventErasureResult result, ErasurePlaceState state, string? reason = null)
    {
        Assert.HasCount(Enum.GetValues<ErasurePlace>().Length, result.Places);
        foreach (var place in result.Places)
        {
            Assert.AreEqual(state, place.State, $"{place.Place}: {place.State} ({place.OutstandingReason})");
            Assert.AreEqual(reason, place.OutstandingReason, place.Place.ToString());
        }
    }

    private static void AssertPlace(EventErasureResult result, ErasurePlace place, ErasurePlaceState state, int count = -1, string? reason = null)
    {
        var actual = result[place];
        Assert.AreEqual(state, actual.State, $"{place}: {actual.State} ({actual.OutstandingReason})");
        Assert.AreEqual(reason, actual.OutstandingReason, place.ToString());
        if (count >= 0)
            Assert.AreEqual(count, actual.Count, place.ToString());
    }

    // Compose entries have no event id; find them by their producer endpoint, which every
    // provider returns on the search item.
    private static async Task<List<MessageAuditEntity>> ComposeAudits(IMessageTrackingStore store, string producer) =>
        (await store.SearchAudits(
            new AuditFilter { EndpointId = producer, EndpointIdExact = true, AuditType = MessageAuditType.Compose },
            continuationToken: null,
            maxItemCount: 50))
            .Audits.Select(a => a.Audit)
            .ToList();

    [TestMethod]
    [DataRow(ResolutionStatus.Pending)]
    [DataRow(ResolutionStatus.Deferred)]
    [DataRow(ResolutionStatus.DeadLettered)]
    [DataRow(ResolutionStatus.Unsupported)]
    [DataRow(ResolutionStatus.Completed)]
    [DataRow(ResolutionStatus.Skipped)]
    public async Task EraseEventCopy_DeletesTrackingRowInEveryStatus(ResolutionStatus status)
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-status");
        var eventId = Id($"e-{status}");
        var row = EventWithPayload(endpointId, eventId, "s1");
        var upload = status switch
        {
            ResolutionStatus.Pending => store.UploadPendingMessage(eventId, "s1", endpointId, row),
            ResolutionStatus.Deferred => store.UploadDeferredMessage(eventId, "s1", endpointId, row),
            ResolutionStatus.DeadLettered => store.UploadDeadletteredMessage(eventId, "s1", endpointId, row),
            ResolutionStatus.Unsupported => store.UploadUnsupportedMessage(eventId, "s1", endpointId, row),
            ResolutionStatus.Completed => store.UploadCompletedMessage(eventId, "s1", endpointId, row),
            _ => store.UploadSkippedMessage(eventId, "s1", endpointId, row),
        };
        Assert.IsTrue(await upload);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertPlace(result, ErasurePlace.TrackingRow, ErasurePlaceState.Removed, count: 1);
        Assert.IsTrue(result.IsComplete);
        Assert.IsNull(await store.GetEvent(endpointId, eventId));
        AssertPlace(await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true)),
            ErasurePlace.TrackingRow, ErasurePlaceState.AlreadyAbsent, count: 0);
    }

    [TestMethod]
    public async Task EraseEventCopy_DeletesArchivedAndSoftDeletedRows()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-hidden");
        var archived = Id("e-archived");
        var removed = Id("e-removed");
        await store.UploadFailedMessage(archived, "s1", endpointId, EventWithPayload(endpointId, archived, "s1"));
        await store.ArchiveFailedEvent(archived, "s1", endpointId);
        await store.UploadPendingMessage(removed, "s2", endpointId, EventWithPayload(endpointId, removed, "s2"));
        Assert.IsTrue(await store.RemoveMessage(removed, "s2", endpointId));

        foreach (var eventId in new[] { archived, removed })
        {
            // The hidden row is found and deleted: its payload must not outlive the erasure.
            var first = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));
            AssertPlace(first, ErasurePlace.TrackingRow, ErasurePlaceState.Removed, count: 1);

            var second = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));
            AssertEveryPlace(second, ErasurePlaceState.AlreadyAbsent);
        }
    }

    [TestMethod]
    public async Task EraseEventCopy_DeletesHistoryOnlyForTheEndpoint()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-history");
        var otherEndpoint = Id("ep-erase-history-other");
        var eventId = Id("e-history");
        await StoreHistory(store, endpointId, eventId, Id("m1"));
        await StoreHistory(store, endpointId, eventId, Id("m2"), MessageType.ErrorResponse);
        await StoreHistory(store, otherEndpoint, eventId, Id("m3"));

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertPlace(result, ErasurePlace.MessageHistory, ErasurePlaceState.Removed, count: 2);
        var left = (await store.GetEventHistory(eventId)).ToList();
        Assert.HasCount(1, left);
        Assert.AreEqual(otherEndpoint, left[0].EndpointId);
    }

    [TestMethod]
    public async Task EraseEventCopy_RedactsPayloadAudits_KeepsIdentifiers()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-audits");
        var eventId = Id("e-audits");
        await store.UploadPendingMessage(eventId, "s1", endpointId, EventWithPayload(endpointId, eventId, "s1"));
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.ResubmitWithChanges, Payload(eventId), "fixed the e-mail"), endpointId, ErasedType);
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.Skip, "{\"reason\":\"customer leaked@example.com\"}"), endpointId, ErasedType);
        var countsBefore = await store.GetResubmitCounts(endpointId, new[] { eventId });

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertPlace(result, ErasurePlace.EventAudits, ErasurePlaceState.Redacted, count: 2);
        var marker = ErasureRules.RedactionMarker(Id("erasure"));
        var audits = (await store.GetMessageAudits(eventId)).ToList();
        Assert.HasCount(2, audits);
        foreach (var audit in audits)
        {
            Assert.AreEqual(marker, audit.Data);
            Assert.IsNull(audit.Comment);
            Assert.AreEqual("operator@example.com", audit.AuditorName);
        }

        CollectionAssert.AreEquivalent(
            new[] { MessageAuditType.ResubmitWithChanges, MessageAuditType.Skip },
            audits.Select(a => a.AuditType).ToList());
        var countsAfter = await store.GetResubmitCounts(endpointId, new[] { eventId });
        Assert.AreEqual(countsBefore[eventId], countsAfter[eventId]);
    }

    [TestMethod]
    public async Task EraseEventCopy_LeavesNonPayloadAuditData()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-nonpayload");
        var eventId = Id("e-nonpayload");
        const string classified = "{\"category\":\"Transient\"}";
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.FailureClassified, classified), endpointId, ErasedType);
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.GetEventDetails, null, "looked at it"), endpointId, ErasedType);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        // Only the comment counts as redacted; the classification metadata stays as it was.
        AssertPlace(result, ErasurePlace.EventAudits, ErasurePlaceState.Redacted, count: 1);
        var audits = (await store.GetMessageAudits(eventId)).ToList();
        Assert.AreEqual(classified, audits.Single(a => a.AuditType == MessageAuditType.FailureClassified).Data);
        Assert.IsTrue(audits.All(a => a.Comment is null));
    }

    [TestMethod]
    public async Task EraseEventCopy_RedactsMatchingComposeAudit_AndNotOthers()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-compose");
        var producer = Id("producer-compose");
        var eventId = Id("e-compose");
        var payload = Payload(eventId);
        await store.UploadPendingMessage(eventId, "s1", endpointId, EventWithPayload(endpointId, eventId, "s1"));

        // The operator typed the same JSON with other whitespace: JSON-equal, so it matches.
        var typed = JsonConvert.SerializeObject(JsonConvert.DeserializeObject(payload), Formatting.Indented);
        var matching = JsonConvert.SerializeObject(new { eventTypeId = ErasedType, eventContent = typed });
        var other = JsonConvert.SerializeObject(new { eventTypeId = ErasedType, eventContent = Payload(Id("another-event")) });
        var full = JsonConvert.SerializeObject(new { eventTypeId = ErasedType, eventContent = payload });
        var truncated = full[..(full.Length - 10)] + ErasureRules.AuditTruncationSuffix;
        await store.StoreMessageAudit(string.Empty, Audit(MessageAuditType.Compose, matching, "composed by hand"), producer, ErasedType);
        await store.StoreMessageAudit(string.Empty, Audit(MessageAuditType.Compose, other), producer, ErasedType);
        await store.StoreMessageAudit(string.Empty, Audit(MessageAuditType.Compose, truncated), producer, ErasedType);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, producer));

        AssertPlace(result, ErasurePlace.ComposeAudits, ErasurePlaceState.Redacted, count: 2);
        Assert.IsTrue(result.IsComplete);
        var marker = ErasureRules.RedactionMarker(Id("erasure"));
        var compose = await ComposeAudits(store, producer);
        Assert.HasCount(3, compose);
        Assert.AreEqual(2, compose.Count(a => a.Data == marker && a.Comment is null));
        Assert.AreEqual(1, compose.Count(a => a.Data == other));
    }

    [TestMethod]
    public async Task EraseEventCopy_NeverTouchesEraseEventRecords()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-records");
        var eventId = Id("e-records");
        const string started = "{\"stage\":\"started\"}";
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.EraseEvent, started, "erasing a leak"), endpointId, ErasedType);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertPlace(result, ErasurePlace.EventAudits, ErasurePlaceState.AlreadyAbsent, count: 0);
        var record = (await store.GetMessageAudits(eventId)).Single();
        Assert.AreEqual(started, record.Data);
        Assert.AreEqual("erasing a leak", record.Comment);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EraseEventCopy_RefusesBlockingRow(bool handoff)
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-blocking");
        var eventId = Id($"e-blocking-{handoff}");
        var row = EventWithPayload(endpointId, eventId, "s1");
        if (handoff)
        {
            row.PendingSubStatus = "Handoff";
            await store.UploadPendingMessage(eventId, "s1", endpointId, row);
        }
        else
        {
            await store.UploadFailedMessage(eventId, "s1", endpointId, row);
        }

        await StoreHistory(store, endpointId, eventId, Id("m-blocking"));
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.Resubmit, "{\"reason\":\"x\"}"), endpointId, ErasedType);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertEveryPlace(result, ErasurePlaceState.Outstanding, ErasureOutstandingReasons.BlocksSession);
        Assert.IsNotNull(await store.GetEvent(endpointId, eventId));
        Assert.HasCount(1, (await store.GetEventHistory(eventId)).ToList());
        Assert.AreEqual("{\"reason\":\"x\"}", (await store.GetMessageAudits(eventId)).Single().Data);
    }

    [TestMethod]
    public async Task EraseEventCopy_SecondCallReportsAlreadyAbsent()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-twice");
        var eventId = Id("e-twice");
        await store.UploadDeferredMessage(eventId, "s1", endpointId, EventWithPayload(endpointId, eventId, "s1"));
        await StoreHistory(store, endpointId, eventId, Id("m-twice"));
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.Skip, "{\"reason\":\"x\"}", "note"), endpointId, ErasedType);

        var first = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId));
        Assert.IsTrue(first.IsComplete);

        var second = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertEveryPlace(second, ErasurePlaceState.AlreadyAbsent);
        Assert.IsTrue(second.Places.All(p => p.Count == 0));
    }

    [TestMethod]
    public async Task EraseEventCopy_UnknownPairReportsAlreadyAbsent()
    {
        var store = CreateStore();

        var result = await store.EraseEventCopyAsync(EraseRequest(Id("ep-erase-unknown"), Id("e-unknown"), composeConfirmed: true));

        AssertEveryPlace(result, ErasurePlaceState.AlreadyAbsent);
    }

    [TestMethod]
    public async Task EraseEventCopy_DeletesEventReportMarker()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-report");
        var eventId = Id("e-report");
        await store.UploadDeadletteredMessage(eventId, "s1", endpointId, EventWithPayload(endpointId, eventId, "s1"));
        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: "operator", ticketId: "INC-1");

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertPlace(result, ErasurePlace.EventReport, ErasurePlaceState.Removed, count: 1);
        Assert.IsEmpty(await store.GetEventReports(endpointId, new[] { eventId }));
    }

    [TestMethod]
    public async Task EraseEventCopy_ComposeConfirmedWithoutSources_ReportsAlreadyAbsent()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-confirmed");
        var eventId = Id("e-confirmed");
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.Resubmit, "{\"reason\":\"x\"}"), endpointId, ErasedType);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        AssertPlace(result, ErasurePlace.ComposeAudits, ErasurePlaceState.AlreadyAbsent, count: 0);
        AssertPlace(result, ErasurePlace.EventAudits, ErasurePlaceState.Redacted, count: 1);
        Assert.IsTrue(result.IsComplete);
    }

    [TestMethod]
    public async Task EraseEventCopy_ComposeUnconfirmedWithoutSources_ReportsRetryContextMissing()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-unconfirmed");
        var eventId = Id("e-unconfirmed");

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId));

        // With no payload left to match against, the Compose audits can't be confirmed clean.
        AssertPlace(result, ErasurePlace.ComposeAudits, ErasurePlaceState.Outstanding, reason: ErasureOutstandingReasons.RetryContextMissing);
        AssertPlace(result, ErasurePlace.TrackingRow, ErasurePlaceState.AlreadyAbsent);
        AssertPlace(result, ErasurePlace.MessageHistory, ErasurePlaceState.AlreadyAbsent);
        AssertPlace(result, ErasurePlace.EventAudits, ErasurePlaceState.AlreadyAbsent);
        AssertPlace(result, ErasurePlace.EventReport, ErasurePlaceState.AlreadyAbsent);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EraseEventCopy_DeletesTrackingRowsUnderEverySession(bool oneBlocks)
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-sessions");
        var eventId = Id($"e-sessions-{oneBlocks}");
        await store.UploadCompletedMessage(eventId, "s1", endpointId, EventWithPayload(endpointId, eventId, "s1"));
        var second = EventWithPayload(endpointId, eventId, "s2");
        if (oneBlocks)
            await store.UploadFailedMessage(eventId, "s2", endpointId, second);
        else
            await store.UploadDeferredMessage(eventId, "s2", endpointId, second);

        var result = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, composeConfirmed: true));

        if (oneBlocks)
        {
            AssertEveryPlace(result, ErasurePlaceState.Outstanding, ErasureOutstandingReasons.BlocksSession);
            Assert.IsNotNull(await store.GetFailedEvent(endpointId, eventId, "s2"));
        }
        else
        {
            AssertPlace(result, ErasurePlace.TrackingRow, ErasurePlaceState.Removed, count: 2);
            Assert.IsNull(await store.GetEvent(endpointId, eventId));
        }
    }

    [TestMethod]
    public async Task EraseEventCopy_VerifyCallAfterErasure_ReportsEveryPlaceAlreadyAbsent()
    {
        var store = CreateStore();
        var endpointId = Id("ep-erase-verify");
        var producer = Id("producer-verify");
        var eventId = Id("e-verify");
        var payload = Payload(eventId);
        await store.UploadDeadletteredMessage(eventId, "s1", endpointId, EventWithPayload(endpointId, eventId, "s1"));
        await StoreHistory(store, endpointId, eventId, Id("m-verify"));
        await store.StoreMessageAudit(eventId, Audit(MessageAuditType.ReportEvent, "{\"ticketId\":\"INC-2\"}"), endpointId, ErasedType);
        await store.SetEventReport(endpointId, eventId, isReported: true, reportedBy: "operator", ticketId: "INC-2");
        await store.StoreMessageAudit(string.Empty,
            Audit(MessageAuditType.Compose, JsonConvert.SerializeObject(new { eventTypeId = ErasedType, eventContent = payload })),
            producer, ErasedType);

        var first = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, producer));

        Assert.IsTrue(first.IsComplete, string.Join(", ", first.Places.Select(p => $"{p.Place}={p.State}")));
        AssertPlace(first, ErasurePlace.ComposeAudits, ErasurePlaceState.Redacted, count: 1);
        AssertPlace(first, ErasurePlace.TrackingRow, ErasurePlaceState.Removed, count: 1);
        AssertPlace(first, ErasurePlace.MessageHistory, ErasurePlaceState.Removed, count: 1);
        AssertPlace(first, ErasurePlace.EventReport, ErasurePlaceState.Removed, count: 1);
        AssertPlace(first, ErasurePlace.EventAudits, ErasurePlaceState.Redacted, count: 1);

        var verify = await store.EraseEventCopyAsync(EraseRequest(endpointId, eventId, producer, composeConfirmed: true));

        AssertEveryPlace(verify, ErasurePlaceState.AlreadyAbsent);
    }

    // ───────── Spec 050: PurgeExpiredData ─────────

    private static readonly RetentionPeriods ThirtyDays = new(30, 30, 30, 30);

    private async Task SeedRetentionSet(IMessageTrackingStore store, string endpointId, string tag)
    {
        await store.UploadPendingMessage(Id($"{tag}-pending"), "s1", endpointId, SampleEvent(endpointId, Id($"{tag}-pending"), "s1"));
        await store.UploadFailedMessage(Id($"{tag}-failed"), "s2", endpointId, SampleEvent(endpointId, Id($"{tag}-failed"), "s2"));
        await store.UploadCompletedMessage(Id($"{tag}-completed"), "s3", endpointId, SampleEvent(endpointId, Id($"{tag}-completed"), "s3"));
        await store.UploadFailedMessage(Id($"{tag}-archived"), "s4", endpointId, SampleEvent(endpointId, Id($"{tag}-archived"), "s4"));
        await store.ArchiveFailedEvent(Id($"{tag}-archived"), "s4", endpointId);
        await store.UploadDeferredMessage(Id($"{tag}-removed"), "s5", endpointId, SampleEvent(endpointId, Id($"{tag}-removed"), "s5"));
        await store.RemoveMessage(Id($"{tag}-removed"), "s5", endpointId);
        await StoreHistory(store, endpointId, Id($"{tag}-pending"), Id($"{tag}-message"));
        await store.StoreMessageAudit(Id($"{tag}-pending"), Audit(MessageAuditType.Resubmit, null), endpointId, ErasedType);
    }

    [TestMethod]
    public async Task PurgeExpiredData_DeletesEachPeriodsRows_AndNothingNewer()
    {
        var store = CreateStore();
        var endpointId = Id("ep-retention");
        await SeedRetentionSet(store, endpointId, "old");
        // Margins on both sides of the cutoff absorb clock skew between this process and the database.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var cutoff = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        await SeedRetentionSet(store, endpointId, "new");

        var result = await store.PurgeExpiredData(ThirtyDays, cutoff.AddDays(30), batchSize: 100);

        if (!DeletesExpiredDataByAge)
        {
            Assert.AreEqual(RetentionPurgeResult.None, result);
            return;
        }

        Assert.AreEqual(new RetentionPurgeResult(OpenEvents: 2, ResolvedEvents: 3, MessageHistory: 1, AuditTrail: 1), result);
        Assert.IsNull(await store.GetEvent(endpointId, Id("old-pending")));
        Assert.IsNull(await store.GetEvent(endpointId, Id("old-failed")));
        Assert.IsNull(await store.GetEvent(endpointId, Id("old-completed")));
        Assert.IsNull(await store.GetMessage(Id("old-pending"), Id("old-message")));
        Assert.IsEmpty(await store.GetMessageAudits(Id("old-pending")));

        Assert.IsNotNull(await store.GetEvent(endpointId, Id("new-pending")));
        Assert.IsNotNull(await store.GetEvent(endpointId, Id("new-failed")));
        Assert.IsNotNull(await store.GetEvent(endpointId, Id("new-completed")));
        Assert.IsNotNull(await store.GetMessage(Id("new-pending"), Id("new-message")));
        Assert.HasCount(1, (await store.GetMessageAudits(Id("new-pending"))).ToList());

        // The hidden rows went with the resolved period: erasing them now finds nothing.
        foreach (var hidden in new[] { "old-archived", "old-removed" })
        {
            AssertPlace(await store.EraseEventCopyAsync(EraseRequest(endpointId, Id(hidden), composeConfirmed: true)),
                ErasurePlace.TrackingRow, ErasurePlaceState.AlreadyAbsent);
        }
    }

    [TestMethod]
    public async Task PurgeExpiredData_UnlimitedPeriodsDeleteNothing()
    {
        var store = CreateStore();
        var endpointId = Id("ep-retention-unlimited");
        await SeedRetentionSet(store, endpointId, "kept");

        var result = await store.PurgeExpiredData(RetentionPeriods.AllUnlimited, DateTime.UtcNow.AddYears(50), batchSize: 100);

        Assert.AreEqual(RetentionPurgeResult.None, result);
        Assert.IsNotNull(await store.GetEvent(endpointId, Id("kept-pending")));
        Assert.IsNotNull(await store.GetMessage(Id("kept-pending"), Id("kept-message")));
        Assert.HasCount(1, (await store.GetMessageAudits(Id("kept-pending"))).ToList());
    }

    [TestMethod]
    public async Task PurgeExpiredData_HonoursTheBatchSize()
    {
        var store = CreateStore();
        var endpointId = Id("ep-retention-batch");
        for (var i = 0; i < 3; i++)
            await store.UploadPendingMessage(Id($"batch-{i}"), "s1", endpointId, SampleEvent(endpointId, Id($"batch-{i}"), "s1"));
        var openOnly = new RetentionPeriods(30, RetentionPeriods.Unlimited, RetentionPeriods.Unlimited, RetentionPeriods.Unlimited);
        var now = DateTime.UtcNow.AddDays(31);

        var first = await store.PurgeExpiredData(openOnly, now, batchSize: 2);
        var second = await store.PurgeExpiredData(openOnly, now, batchSize: 2);
        var third = await store.PurgeExpiredData(openOnly, now, batchSize: 2);

        if (!DeletesExpiredDataByAge)
        {
            Assert.AreEqual(RetentionPurgeResult.None, first);
            return;
        }

        Assert.AreEqual(2, first.OpenEvents);
        Assert.AreEqual(1, second.OpenEvents);
        Assert.AreEqual(0, third.OpenEvents);
    }
}
