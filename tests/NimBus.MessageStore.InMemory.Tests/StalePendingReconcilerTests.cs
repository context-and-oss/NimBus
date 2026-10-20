#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.InMemory.Tests;

/// <summary>
/// The classification and projection rules of Spec 032 §4.2 (Spec 030 §9 steps 2 and 3), exercised on
/// the incident shape of 2026-09-14 and on every neighbouring shape that must NOT be repaired.
/// Ported from the EET.Deploy reference tool's RepairRulesTests.
/// </summary>
[TestClass]
public class StalePendingReconcilerTests
{
    private const string Endpoint = "Nav09Endpoint";
    private const string Producer = "CrmEndpoint";
    private const string ResolverId = "Resolver";
    private const string EventId = "0b89d545-0000-0000-0000-000000000001";

    // The incident timeline (UTC), from the Flow tab of event 0b89d545.
    private static readonly DateTime RequestAt = new(2026, 9, 14, 23, 48, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ResponseAt = new(2026, 9, 14, 23, 49, 2, 631, DateTimeKind.Utc);
    private static readonly DateTime StaleCopyAt = new(2026, 9, 14, 23, 51, 38, 679, DateTimeKind.Utc);

    private static MessageEntity Request(
        string id, DateTime at, MessageType type = MessageType.EventRequest,
        string session = "s1", string from = Producer) => new()
    {
        EventId = EventId,
        MessageId = id,
        MessageType = type,
        EnqueuedTimeUtc = at,
        From = from,
        To = Endpoint,
        EndpointId = Endpoint,
        EndpointRole = EndpointRole.Subscriber,
        SessionId = session,
        EventTypeId = "AccountUpdated",
        CorrelationId = "corr-1",
        OriginatingFrom = Producer,
        OriginatingMessageId = "orig-1",
    };

    private static MessageEntity Response(
        string id, DateTime at, MessageType type = MessageType.ResolutionResponse,
        string session = "s1", string from = Endpoint, string parent = "A") => new()
    {
        EventId = EventId,
        MessageId = id,
        MessageType = type,
        EnqueuedTimeUtc = at,
        From = from,
        To = ResolverId,
        EndpointId = from,
        EndpointRole = EndpointRole.Subscriber,
        SessionId = session,
        EventTypeId = "AccountUpdated",
        CorrelationId = parent,
        ParentMessageId = parent,
        OriginatingMessageId = "orig-1",
        OriginatingFrom = Producer,
        RetryCount = 0,
        QueueTimeMs = 120,
        ProcessingTimeMs = 850,
        MessageContent = new MessageContent(),
    };

    /// <summary>The audit row as the stale copy left it: Pending, LastMessageId = the copy's id.</summary>
    private static UnresolvedEvent RowWrittenBy(MessageEntity staleCopy) => new()
    {
        EventId = EventId,
        SessionId = staleCopy.SessionId,
        EndpointId = Endpoint,
        EventTypeId = "AccountUpdated",
        ResolutionStatus = ResolutionStatus.Pending,
        MessageType = staleCopy.MessageType,
        LastMessageId = staleCopy.MessageId,
        EnqueuedTimeUtc = staleCopy.EnqueuedTimeUtc,
        UpdatedAt = staleCopy.EnqueuedTimeUtc.AddSeconds(100),
    };

    // ── The incident, and the shapes next to it ──────────────────────────────────────────

    [TestMethod]
    public void IncidentSignature_IsRepairable_FromTheStoredResolutionResponse()
    {
        var request = Request("A", RequestAt);
        var response = Response("906c2b00", ResponseAt);
        var staleCopy = Request("9ada4185", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { request, response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.Repairable, result.Verdict);
        Assert.IsTrue(result.IsRepairable);
        Assert.AreEqual("906c2b00", result.Response!.MessageId);
        StringAssert.Contains(result.Detail, "9ada4185");
    }

    [TestMethod]
    public void RetryThenSuccessThenStaleCopy_IsRepairable()
    {
        // A failure, a retry, then the success — the latest terminal is what decides.
        var request = Request("A", RequestAt);
        var error = Response("err-1", RequestAt.AddSeconds(5), MessageType.ErrorResponse);
        var retry = Request("retry-1", RequestAt.AddSeconds(10), MessageType.EventRequest);
        var response = Response("ok-1", ResponseAt);
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { request, error, retry, response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.Repairable, result.Verdict);
        Assert.AreEqual("ok-1", result.Response!.MessageId);
    }

    [TestMethod]
    public void NoResponseAtAll_IsNoTerminal()
    {
        var request = Request("A", RequestAt);
        var row = RowWrittenBy(request);

        var result = StalePendingReconciler.Classify(row, new[] { request }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.NoTerminal, result.Verdict);
        Assert.IsNull(result.Response);
    }

    [TestMethod]
    public void EmptyHistory_IsHistoryMissing()
    {
        var row = RowWrittenBy(Request("A", RequestAt));

        var result = StalePendingReconciler.Classify(row, Array.Empty<MessageEntity>(), Endpoint);

        Assert.AreEqual(StalePendingVerdict.HistoryMissing, result.Verdict);
    }

    [TestMethod]
    public void ErrorResponseAfterTheResolution_IsLatestTerminalIsError()
    {
        var response = Response("ok-1", ResponseAt);
        var error = Response("err-1", ResponseAt.AddSeconds(30), MessageType.ErrorResponse);
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, error, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LatestTerminalIsError, result.Verdict);
        Assert.IsNull(result.Response, "an error is never applied");
        Assert.AreEqual("err-1", result.LatestTerminal!.MessageId);
    }

    [TestMethod]
    public void SkipResponseIsTheLatestTerminal_IsLatestTerminalIsSkip()
    {
        var response = Response("ok-1", ResponseAt);
        // A SkipResponse is issued on the operator's behalf, so its From is not the endpoint.
        var skip = Response("skip-1", ResponseAt.AddSeconds(30), MessageType.SkipResponse, from: "Manager");
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, skip, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LatestTerminalIsSkip, result.Verdict);
    }

    [TestMethod]
    public void ResolutionResponseCarryingADeadLetterDescription_IsLatestTerminalIsDeadLettered()
    {
        var response = Response("ok-1", ResponseAt);
        response.DeadLetterReason = "MaxDeliveryCountExceeded";
        response.DeadLetterErrorDescription = "the broker moved it";
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LatestTerminalIsDeadLettered, result.Verdict);
        StringAssert.Contains(result.Detail, "MaxDeliveryCountExceeded");
    }

    [TestMethod]
    public void AnEmptyDeadLetterDescription_IsNotADeadLetter()
    {
        // The SQL Server provider reads a NULL DeadLetterErrorDescription back as string.Empty
        // (Cosmos and in-memory give null). An empty description must classify like a null one,
        // or no SQL Server row could ever be repaired - the live-AppHost pass caught exactly this.
        var request = Request("A", RequestAt);
        var response = Response("906c2b00", ResponseAt);
        response.DeadLetterReason = string.Empty;
        response.DeadLetterErrorDescription = string.Empty;
        var staleCopy = Request("9ada4185", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { request, response, staleCopy }, Endpoint);
        Assert.AreEqual(StalePendingVerdict.Repairable, result.Verdict);

        var projection = StalePendingReconciler.BuildCompletedProjection(response, new[] { request, response }, Endpoint, DateTime.UtcNow);
        Assert.IsNull(projection.DeadLetterReason, "the Resolver writes null here; a repaired row must match");
        Assert.IsNull(projection.DeadLetterErrorDescription);
        Assert.IsNull(projection.Reason);
    }

    [TestMethod]
    public void ResponseNotOlderThanTheRow_IsResponseNotBeforeRow()
    {
        var response = Response("ok-1", StaleCopyAt.AddSeconds(5));
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.ResponseNotBeforeRow, result.Verdict);
    }

    [TestMethod]
    public void ResubmissionAfterTheResponse_IsLaterControlMessage()
    {
        var response = Response("ok-1", ResponseAt);
        var resubmit = Request("rs-1", ResponseAt.AddSeconds(20), MessageType.ResubmissionRequest, from: "Manager");
        var row = RowWrittenBy(resubmit);

        var result = StalePendingReconciler.Classify(row, new[] { response, resubmit }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LaterControlMessage, result.Verdict);
        StringAssert.Contains(result.Detail, "ResubmissionRequest rs-1");
    }

    [TestMethod]
    [DataRow(MessageType.ResubmissionRequest)]
    [DataRow(MessageType.SkipRequest)]
    [DataRow(MessageType.HandoffCompletedRequest)]
    [DataRow(MessageType.HandoffFailedRequest)]
    [DataRow(MessageType.PendingHandoffResponse)]
    [DataRow(MessageType.DeferralResponse)]
    public void AnyNonRequestMessageAfterTheResponse_IsLaterControlMessage(MessageType laterType)
    {
        var response = Response("ok-1", ResponseAt);
        var later = Request("later-1", ResponseAt.AddSeconds(20), laterType);
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, later, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LaterControlMessage, result.Verdict, $"{laterType} after the response");
    }

    [TestMethod]
    public void RequestCopyNewerThanTheRow_IsLaterRequestCopy()
    {
        var response = Response("ok-1", ResponseAt);
        var staleCopy = Request("copy-1", StaleCopyAt);
        var newerCopy = Request("copy-2", StaleCopyAt.AddSeconds(30));
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy, newerCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LaterRequestCopy, result.Verdict);
        StringAssert.Contains(result.Detail, "copy-2");
    }

    [TestMethod]
    public void ARescheduledCopySharingTheRowsMessageId_StillCountsAsALaterCopy()
    {
        // Spec 030 §5.7: ScheduleRedelivery keeps the original MessageId, so a stored copy can share
        // the row's LastMessageId. The rule keys on enqueue time alone precisely so this is caught.
        var response = Response("ok-1", ResponseAt);
        var staleCopy = Request("copy-1", StaleCopyAt);
        var resend = Request("copy-1", StaleCopyAt.AddSeconds(30));
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy, resend }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LaterRequestCopy, result.Verdict);
    }

    [TestMethod]
    public void ResponseForAnotherSession_DoesNotCount()
    {
        var response = Response("ok-1", ResponseAt, session: "other-session");
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.NoTerminal, result.Verdict);
    }

    [TestMethod]
    public void ResolutionResponseFromAnotherEndpoint_DoesNotCount()
    {
        var response = Response("ok-1", ResponseAt, from: "SomeOtherEndpoint");
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.NoTerminal, result.Verdict);
    }

    [TestMethod]
    public void AnotherSubscribersResponseAfterOurs_DoesNotBlockTheRepair()
    {
        // Fan-out: a second subscriber of the same event shares the event id and the publisher's
        // session id. Its response arriving after ours is not a control message on OUR row.
        var request = Request("A", RequestAt);
        var response = Response("906c2b00", ResponseAt);
        var siblingResponse = Response("sib-rsp", ResponseAt.AddSeconds(10), from: "CrmMirrorEndpoint");
        var staleCopy = Request("9ada4185", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { request, response, siblingResponse, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.Repairable, result.Verdict);
        Assert.AreEqual("906c2b00", result.Response!.MessageId);
    }

    [TestMethod]
    public void AnotherSubscribersLateRequestCopy_DoesNotBlockTheRepair()
    {
        // The sibling's own redelivered copy, newer than our row, is not an unprojected copy of OURS.
        var request = Request("A", RequestAt);
        var response = Response("906c2b00", ResponseAt);
        var staleCopy = Request("9ada4185", StaleCopyAt);
        var siblingCopy = Request("sib-copy", StaleCopyAt.AddSeconds(30));
        siblingCopy.To = "CrmMirrorEndpoint";
        siblingCopy.EndpointId = "CrmMirrorEndpoint";
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { request, response, staleCopy, siblingCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.Repairable, result.Verdict);
    }

    [TestMethod]
    public void AMessageWithoutAStampedEndpointId_StaysInScope()
    {
        // Older history rows may pre-date the EndpointId stamp; they must keep acting as blockers.
        var response = Response("ok-1", ResponseAt);
        var later = Request("rs-1", ResponseAt.AddSeconds(20), MessageType.ResubmissionRequest, from: "Manager");
        later.EndpointId = null!;
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, later, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.LaterControlMessage, result.Verdict);
    }

    [TestMethod]
    public void EndpointComparison_IsCaseInsensitive()
    {
        var response = Response("ok-1", ResponseAt, from: "nav09endpoint");
        var staleCopy = Request("copy-1", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var result = StalePendingReconciler.Classify(row, new[] { response, staleCopy }, Endpoint);

        Assert.AreEqual(StalePendingVerdict.Repairable, result.Verdict);
    }

    [TestMethod]
    public void HistoryOrderDoesNotChangeTheVerdict()
    {
        // Cosmos and the in-memory store return history unordered; the verdict must not depend on it.
        var request = Request("A", RequestAt);
        var response = Response("906c2b00", ResponseAt);
        var staleCopy = Request("9ada4185", StaleCopyAt);
        var row = RowWrittenBy(staleCopy);

        var shuffled = new[] { staleCopy, response, request };

        Assert.AreEqual(StalePendingVerdict.Repairable,
            StalePendingReconciler.Classify(row, shuffled, Endpoint).Verdict);
    }

    // ── Candidate selection ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void CandidateRowTypes_MatchSpec030Section9Step1()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                MessageType.EventRequest, MessageType.ResubmissionRequest, MessageType.SkipRequest,
                MessageType.HandoffCompletedRequest, MessageType.HandoffFailedRequest,
            },
            StalePendingReconciler.CandidateRowTypes.ToArray());
    }

    [TestMethod]
    public void IsCandidate_AcceptsEveryCandidateRowType()
    {
        foreach (var type in StalePendingReconciler.CandidateRowTypes)
        {
            var row = RowWrittenBy(Request("A", RequestAt, type));
            Assert.IsTrue(StalePendingReconciler.IsCandidate(row), $"{type} row");
        }
    }

    [TestMethod]
    public void IsCandidate_RejectsAParkedHandoff()
    {
        var row = RowWrittenBy(Request("A", RequestAt));
        row.PendingSubStatus = "Handoff";

        Assert.IsFalse(StalePendingReconciler.IsCandidate(row), "a parked handoff is genuinely in flight");
    }

    [TestMethod]
    [DataRow(ResolutionStatus.Completed)]
    [DataRow(ResolutionStatus.Failed)]
    [DataRow(ResolutionStatus.Deferred)]
    [DataRow(ResolutionStatus.Skipped)]
    [DataRow(ResolutionStatus.DeadLettered)]
    public void IsCandidate_RejectsNonPendingRows(ResolutionStatus status)
    {
        var row = RowWrittenBy(Request("A", RequestAt));
        row.ResolutionStatus = status;

        Assert.IsFalse(StalePendingReconciler.IsCandidate(row));
    }

    [TestMethod]
    public void IsCandidate_RejectsARowWrittenByAResponse()
    {
        var row = RowWrittenBy(Request("A", RequestAt, MessageType.DeferralResponse));

        Assert.IsFalse(StalePendingReconciler.IsCandidate(row));
    }

    // ── The projection ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Projection_MirrorsResolverServiceCreateUnresolvedEvent()
    {
        var response = Response("906c2b00", ResponseAt);
        var now = new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc);

        var projection = StalePendingReconciler.BuildCompletedProjection(response, new[] { response }, Endpoint, now);

        Assert.AreEqual(now, projection.UpdatedAt);
        Assert.AreEqual(ResponseAt, projection.EnqueuedTimeUtc);
        Assert.AreEqual(ResolutionStatus.Completed, projection.ResolutionStatus);
        Assert.AreEqual(MessageType.ResolutionResponse, projection.MessageType);
        Assert.AreEqual("906c2b00", projection.LastMessageId);
        Assert.AreEqual(response.EventId, projection.EventId);
        Assert.AreEqual(response.SessionId, projection.SessionId);
        Assert.AreEqual(response.CorrelationId, projection.CorrelationId);
        Assert.AreEqual(response.EndpointId, projection.EndpointId);
        Assert.AreEqual(response.EndpointRole, projection.EndpointRole);
        Assert.AreEqual(response.OriginatingMessageId, projection.OriginatingMessageId);
        Assert.AreEqual(response.ParentMessageId, projection.ParentMessageId);
        Assert.AreEqual(response.OriginatingFrom, projection.OriginatingFrom);
        Assert.AreEqual(response.EventTypeId, projection.EventTypeId);
        Assert.AreEqual(response.To, projection.To);
        Assert.AreEqual(response.From, projection.From);
        Assert.AreSame(response.MessageContent, projection.MessageContent);
        Assert.AreEqual(response.QueueTimeMs, projection.QueueTimeMs);
        Assert.AreEqual(response.ProcessingTimeMs, projection.ProcessingTimeMs);
        Assert.IsNull(projection.Reason, "a ResolutionResponse carries no skip reason and no dead-letter description");
        Assert.IsNull(projection.PendingSubStatus);
    }

    [TestMethod]
    public void Projection_FallsBackToTheTargetEndpoint_WhenTheStoredResponseHasNoEndpointId()
    {
        var response = Response("ok-1", ResponseAt);
        response.EndpointId = null!;

        var projection = StalePendingReconciler.BuildCompletedProjection(
            response, new[] { response }, Endpoint, DateTime.UtcNow);

        Assert.AreEqual(Endpoint, projection.EndpointId);
    }

    [TestMethod]
    public void Projection_UsesWallClockFromTheOriginalRequest_WhenTheEventWentThroughHandoff()
    {
        var request = Request("A", RequestAt);
        var park = Response("ho-1", RequestAt.AddSeconds(2), MessageType.PendingHandoffResponse);
        var response = Response("ok-1", ResponseAt);

        var projection = StalePendingReconciler.BuildCompletedProjection(
            response, new[] { request, park, response }, Endpoint, DateTime.UtcNow);

        Assert.AreEqual((long)(ResponseAt - RequestAt).TotalMilliseconds, projection.ProcessingTimeMs);
    }

    [TestMethod]
    public void Projection_KeepsTheStoredProcessingTime_WhenThereWasNoHandoff()
    {
        var request = Request("A", RequestAt);
        var response = Response("ok-1", ResponseAt);

        var projection = StalePendingReconciler.BuildCompletedProjection(
            response, new[] { request, response }, Endpoint, DateTime.UtcNow);

        Assert.AreEqual(850L, projection.ProcessingTimeMs);
    }

    [TestMethod]
    public void Projection_RefusesAnythingButACleanResolutionResponse()
    {
        var error = Response("err-1", ResponseAt, MessageType.ErrorResponse);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => StalePendingReconciler.BuildCompletedProjection(error, new[] { error }, Endpoint, DateTime.UtcNow));

        var deadLettered = Response("ok-1", ResponseAt);
        deadLettered.DeadLetterErrorDescription = "the broker moved it";
        Assert.ThrowsExactly<InvalidOperationException>(
            () => StalePendingReconciler.BuildCompletedProjection(
                deadLettered, new[] { deadLettered }, Endpoint, DateTime.UtcNow));
    }
}
