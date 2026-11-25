#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

/// <summary>
/// Spec 049 rules that the conformance suite can't reach on a healthy store: failed writes,
/// source retention, and the shared Compose matching rule.
/// </summary>
[TestClass]
public sealed class EventErasureTests
{
    private const string Endpoint = "Billing";
    private const string Producer = "Crm";
    private const string EventType = "CustomerUpdated";
    private const string EventId = "event-1";
    private const string Payload = "{\"customerId\":42,\"email\":\"leaked@example.com\"}";

    private sealed class FaultyStore : InMemoryMessageStore
    {
        public HashSet<ErasurePlace> Failing { get; } = new();

        protected override void OnErasureWrite(ErasurePlace place)
        {
            if (Failing.Contains(place))
                throw new InvalidOperationException($"{place} write failed");
        }
    }

    private static async Task<FaultyStore> SeedAsync()
    {
        var store = new FaultyStore();
        var row = new UnresolvedEvent
        {
            EventId = EventId, SessionId = "s1", EndpointId = Endpoint, EventTypeId = EventType,
            From = Producer, MessageType = MessageType.EventRequest, EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = EventType, EventJson = Payload } },
        };
        await store.UploadDeadletteredMessage(EventId, "s1", Endpoint, row);
        await store.StoreMessage(new MessageEntity
        {
            EventId = EventId, MessageId = "m1", EndpointId = Endpoint, SessionId = "s1", EventTypeId = EventType,
            To = Endpoint, MessageType = MessageType.EventRequest, EnqueuedTimeUtc = DateTime.UtcNow,
            MessageContent = row.MessageContent,
        });
        await store.StoreMessageAudit(EventId, new MessageAuditEntity
        {
            AuditorName = "op", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Resubmit, Data = "{\"reason\":\"x\"}",
        }, Endpoint, EventType);
        await store.SetEventReport(Endpoint, EventId, true, "op", "INC-1");
        await store.StoreMessageAudit(string.Empty, new MessageAuditEntity
        {
            AuditorName = "op", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Compose,
            Data = JsonConvert.SerializeObject(new { eventTypeId = EventType, eventContent = Payload }),
        }, Producer, EventType);
        return store;
    }

    private static EventErasureRequest Request(bool confirmed = false) =>
        new(Endpoint, EventId, "erasure-1", Producer, EventType, confirmed);

    [TestMethod]
    public async Task FailedWrite_IsOutstanding_AndTheOtherPlacesStillRun()
    {
        var store = await SeedAsync();
        store.Failing.Add(ErasurePlace.EventReport);

        var result = await store.EraseEventCopyAsync(Request());

        Assert.AreEqual(ErasurePlaceState.Outstanding, result[ErasurePlace.EventReport].State);
        Assert.AreEqual(ErasureOutstandingReasons.WriteFailed, result[ErasurePlace.EventReport].OutstandingReason);
        Assert.AreEqual(ErasurePlaceState.Removed, result[ErasurePlace.TrackingRow].State);
        Assert.AreEqual(ErasurePlaceState.Removed, result[ErasurePlace.MessageHistory].State);
        Assert.AreEqual(ErasurePlaceState.Redacted, result[ErasurePlace.EventAudits].State);
        Assert.AreEqual(ErasurePlaceState.Redacted, result[ErasurePlace.ComposeAudits].State);
        Assert.IsFalse(result.IsComplete);
    }

    [TestMethod]
    public async Task EveryWriteFailing_LeavesEveryPlaceOutstanding()
    {
        var store = await SeedAsync();
        foreach (var place in Enum.GetValues<ErasurePlace>())
            store.Failing.Add(place);

        var result = await store.EraseEventCopyAsync(Request());

        Assert.IsTrue(result.Places.All(p => p.State == ErasurePlaceState.Outstanding), string.Join(", ", result.Places));
        Assert.AreEqual(ErasureOutstandingReasons.WriteFailed, result[ErasurePlace.ComposeAudits].OutstandingReason);
        // Source retention keeps the row and history for the rerun, rather than trying to delete them.
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, result[ErasurePlace.TrackingRow].OutstandingReason);
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, result[ErasurePlace.MessageHistory].OutstandingReason);
        Assert.IsNotNull(await store.GetEvent(Endpoint, EventId));
    }

    [TestMethod]
    public async Task FailedComposeWrite_RetainsHistoryAndRow_UntilAHealedRerun()
    {
        var store = await SeedAsync();
        store.Failing.Add(ErasurePlace.ComposeAudits);

        var first = await store.EraseEventCopyAsync(Request());

        Assert.AreEqual(ErasureOutstandingReasons.WriteFailed, first[ErasurePlace.ComposeAudits].OutstandingReason);
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, first[ErasurePlace.MessageHistory].OutstandingReason);
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, first[ErasurePlace.TrackingRow].OutstandingReason);
        // The places that feed nothing else still ran.
        Assert.AreEqual(ErasurePlaceState.Redacted, first[ErasurePlace.EventAudits].State);
        Assert.AreEqual(ErasurePlaceState.Removed, first[ErasurePlace.EventReport].State);
        Assert.IsNotNull(await store.GetEvent(Endpoint, EventId));
        Assert.HasCount(1, (await store.GetEventHistory(EventId)).ToList());

        store.Failing.Clear();
        var rerun = await store.EraseEventCopyAsync(Request());

        Assert.IsTrue(rerun.IsComplete, string.Join(", ", rerun.Places));
        Assert.AreEqual(ErasurePlaceState.Redacted, rerun[ErasurePlace.ComposeAudits].State);
        Assert.AreEqual(ErasurePlaceState.Removed, rerun[ErasurePlace.MessageHistory].State);
        Assert.AreEqual(ErasurePlaceState.Removed, rerun[ErasurePlace.TrackingRow].State);
        Assert.IsNull(await store.GetEvent(Endpoint, EventId));
    }

    [TestMethod]
    public async Task MissingProducer_IsRetryContextMissing_AndKeepsTheSources()
    {
        var store = await SeedAsync();

        var result = await store.EraseEventCopyAsync(new EventErasureRequest(Endpoint, EventId, "erasure-1", null, EventType, false));

        Assert.AreEqual(ErasureOutstandingReasons.RetryContextMissing, result[ErasurePlace.ComposeAudits].OutstandingReason);
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, result[ErasurePlace.TrackingRow].OutstandingReason);
    }

    [TestMethod]
    public async Task EmptyIds_Throw()
    {
        var store = new InMemoryMessageStore();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.EraseEventCopyAsync(new EventErasureRequest(Endpoint, " ", "erasure-1", Producer, EventType, false)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.EraseEventCopyAsync(new EventErasureRequest(Endpoint, EventId, "", Producer, EventType, false)));
    }

    [TestMethod]
    public void ComposeDataMatches_JsonEqualContent()
    {
        var data = JsonConvert.SerializeObject(new { eventTypeId = EventType, eventContent = "{ \"email\": \"leaked@example.com\", \"customerId\": 42 }" });

        Assert.IsTrue(ErasureRules.ComposeDataMatches(data, EventType, new[] { Payload }));
        Assert.IsFalse(ErasureRules.ComposeDataMatches(data, EventType, new[] { "{\"customerId\":43}" }));
    }

    [TestMethod]
    public void ComposeDataMatches_TruncatedPrefixOfTheRebuiltRequest()
    {
        var full = JsonConvert.SerializeObject(new { eventTypeId = EventType, eventContent = Payload });
        var truncated = full[..(full.Length - 8)] + ErasureRules.AuditTruncationSuffix;
        var headerOnly = full[..20] + ErasureRules.AuditTruncationSuffix;

        Assert.IsTrue(ErasureRules.ComposeDataMatches(truncated, EventType, new[] { Payload }));
        Assert.IsFalse(ErasureRules.ComposeDataMatches(truncated, EventType, new[] { "{\"other\":1}" }));
        // A prefix that never reaches the content would match every event of the type.
        Assert.IsFalse(ErasureRules.ComposeDataMatches(headerOnly, EventType, new[] { Payload }));
    }

    [TestMethod]
    public void ComposeDataMatches_NeverMatchesHashedEntriesOrMarkers()
    {
        // Entries written since #415 keep a hash and property names, not the content.
        var hashed = JsonConvert.SerializeObject(new { eventTypeId = EventType, payloadSha256 = "ab12", properties = new[] { "email" } });

        Assert.IsFalse(ErasureRules.ComposeDataMatches(hashed, EventType, new[] { Payload }));
        Assert.IsFalse(ErasureRules.ComposeDataMatches(ErasureRules.RedactionMarker("x"), EventType, new[] { Payload }));
        Assert.IsFalse(ErasureRules.ComposeDataMatches("not json", EventType, new[] { Payload }));
    }

    [TestMethod]
    public void RedactionMarker_IsJsonWithTheErasureId()
    {
        var marker = ErasureRules.RedactionMarker("id-\"1\"");

        Assert.IsTrue(ErasureRules.IsRedactionMarker(marker));
        var parsed = Newtonsoft.Json.Linq.JObject.Parse(marker);
        Assert.IsTrue(parsed.Value<bool>("erased"));
        Assert.AreEqual("id-\"1\"", parsed.Value<string>("erasureId"));
    }
}
