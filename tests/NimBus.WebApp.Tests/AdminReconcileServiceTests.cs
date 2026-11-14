#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using MessageType = NimBus.Core.Messages.MessageType;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;
using StoreEventFilter = NimBus.MessageStore.EventFilter;
using StoreSearchResponse = NimBus.MessageStore.States.SearchResponse;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 032 §5: the preview classifies the endpoint's Pending rows from their stored history, and
/// the reconcile repairs only the rows the rule calls repairable — over a real store, so the
/// projection, the conditional write and the audit are the ones production would produce.
/// </summary>
[TestClass]
public sealed class AdminReconcileServiceTests
{
    private const string EndpointId = "endpoint-a";
    private const string SessionId = "session-a";
    private static readonly DateTime Base = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public async Task Preview_classifies_the_incident_and_its_neighbours()
    {
        var store = new InMemoryMessageStore();
        await SeedRepairable(store, "evt-repairable");
        await SeedNoTerminal(store, "evt-in-flight");
        await SeedLatestTerminalIsError(store, "evt-errored");
        await SeedLaterControlMessage(store, "evt-skip-request");
        var service = CreateAdminService(store);

        var preview = await service.PreviewStalePendingAsync(EndpointId, Base.AddHours(1), 500);

        Assert.AreEqual(EndpointId, preview.EndpointId);
        Assert.AreEqual(4, preview.Candidates);
        Assert.AreEqual(1, preview.Repairable);
        Assert.IsFalse(preview.Truncated);
        var verdicts = preview.Rows.ToDictionary(row => row.EventId, row => row.Verdict);
        Assert.AreEqual(StalePendingRowVerdict.Repairable, verdicts["evt-repairable"]);
        Assert.AreEqual(StalePendingRowVerdict.NoTerminal, verdicts["evt-in-flight"]);
        Assert.AreEqual(StalePendingRowVerdict.LatestTerminalIsError, verdicts["evt-errored"]);
        Assert.AreEqual(StalePendingRowVerdict.LaterControlMessage, verdicts["evt-skip-request"]);

        var repairable = preview.Rows.Single(row => row.EventId == "evt-repairable");
        Assert.AreEqual("rsp-1", repairable.ResponseMessageId);
        Assert.AreEqual("req-copy", repairable.StaleMessageId);
        Assert.AreEqual(nameof(MessageType.EventRequest), repairable.RowMessageType);
        Assert.AreEqual("OrderPlaced", repairable.EventTypeId);
    }

    [TestMethod]
    public async Task Preview_skips_rows_newer_than_the_cut_off()
    {
        var store = new InMemoryMessageStore();
        await SeedRepairable(store, "evt-repairable");
        var service = CreateAdminService(store);

        var preview = await service.PreviewStalePendingAsync(EndpointId, Base.AddMinutes(-1), 500);

        Assert.AreEqual(0, preview.Candidates);
        Assert.AreEqual(1, preview.Scanned, "the row was read and then excluded by the cut-off");
    }

    [TestMethod]
    public async Task Preview_reports_truncation_at_maxRows()
    {
        var store = new InMemoryMessageStore();
        await SeedRepairable(store, "evt-a");
        await SeedRepairable(store, "evt-b");
        var service = CreateAdminService(store);

        var preview = await service.PreviewStalePendingAsync(EndpointId, Base.AddHours(1), maxRows: 1);

        Assert.IsTrue(preview.Truncated);
        Assert.AreEqual(1, preview.Candidates);
    }

    [TestMethod]
    public async Task Reconcile_completes_only_the_repairable_row_and_audits_it()
    {
        var store = new InMemoryMessageStore();
        await SeedRepairable(store, "evt-repairable");
        await SeedNoTerminal(store, "evt-in-flight");
        await SeedLaterControlMessage(store, "evt-skip-request");
        var service = CreateAdminService(store);

        var result = await service.ReconcileStalePendingAsync(
            EndpointId, Base.AddHours(1), maxRepairs: null, auditorName: "owner@example.com", note: "incident 42");

        Assert.AreEqual(1, result.Processed);
        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, result.Skipped);
        CollectionAssert.AreEqual(new[] { "evt-repairable" }, result.RepairedEventIds.ToArray());

        var repaired = (await store.GetEvent(EndpointId, "evt-repairable"))!;
        Assert.AreEqual(ResolutionStatus.Completed, repaired.ResolutionStatus);
        Assert.AreEqual(MessageType.ResolutionResponse, repaired.MessageType);
        Assert.AreEqual("rsp-1", repaired.LastMessageId, "the row now answers the response the Resolver stored");
        Assert.AreEqual("OrderPlaced", repaired.EventTypeId);

        // The rows the rule left to a human are untouched.
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(EndpointId, "evt-in-flight", SessionId))!.ResolutionStatus);
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(EndpointId, "evt-skip-request", SessionId))!.ResolutionStatus);

        var audit = (await store.GetMessageAudits("evt-repairable")).Single();
        Assert.AreEqual(MessageAuditType.ReconcileStalePending, audit.AuditType);
        Assert.AreEqual("owner@example.com", audit.AuditorName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(audit.Comment));
        var data = JObject.Parse(audit.Data!);
        Assert.AreEqual("Pending", data["previousStatus"]!.ToString());
        Assert.AreEqual("Completed", data["newStatus"]!.ToString());
        Assert.AreEqual(nameof(MessageType.EventRequest), data["staleMessageType"]!.ToString());
        Assert.AreEqual("req-copy", data["staleMessageId"]!.ToString());
        Assert.AreEqual("rsp-1", data["responseMessageId"]!.ToString());
        Assert.AreEqual("incident 42", data["note"]!.ToString());
    }

    [TestMethod]
    public async Task Reconcile_is_idempotent()
    {
        var store = new InMemoryMessageStore();
        await SeedRepairable(store, "evt-repairable");
        var service = CreateAdminService(store);

        await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), null, "owner", null);
        var second = await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), null, "owner", null);

        Assert.AreEqual(0, second.Processed, "the repaired row is no longer Pending, so it is not a candidate");
        Assert.AreEqual(0, second.Succeeded);
        Assert.AreEqual(1, (await store.GetMessageAudits("evt-repairable")).Count(), "no second audit row");
    }

    [TestMethod]
    public async Task Reconcile_honours_maxRepairs()
    {
        var store = new InMemoryMessageStore();
        await SeedRepairable(store, "evt-a");
        await SeedRepairable(store, "evt-b");
        var service = CreateAdminService(store);

        var result = await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), maxRepairs: 1, auditorName: "owner", note: null);

        Assert.AreEqual(1, result.Processed);
        Assert.AreEqual(1, result.Succeeded);
        var stillPending = new[] { "evt-a", "evt-b" }
            .Count(id => TryReadPending(store, id) is { ResolutionStatus: ResolutionStatus.Pending });
        Assert.AreEqual(1, stillPending, "exactly one row was left for the next run");
    }

    [TestMethod]
    public async Task Reconcile_skips_a_row_that_was_touched_within_the_age_window()
    {
        var store = new InMemoryMessageStore();
        // Left at the store's own write stamp (now), so the row reads as freshly touched.
        await SeedRepairable(store, "evt-fresh", backdateRow: false);
        var service = CreateAdminService(store);

        var result = await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), null, "owner", null);

        Assert.AreEqual(1, result.Processed);
        Assert.AreEqual(0, result.Succeeded);
        Assert.AreEqual(1, result.Skipped, "a row written minutes ago may still be in flight");
        Assert.AreEqual(ResolutionStatus.Pending, (await store.GetPendingEvent(EndpointId, "evt-fresh", SessionId))!.ResolutionStatus);
    }

    [TestMethod]
    public void Every_store_verdict_has_a_contract_twin_of_the_same_name()
    {
        // ToContractRow maps the two enums by name; a verdict added to one and not the other would
        // only fail at runtime, on the first row that classified to it.
        foreach (var verdict in Enum.GetValues<StalePendingVerdict>())
        {
            Assert.IsTrue(
                Enum.TryParse<StalePendingRowVerdict>(verdict.ToString(), ignoreCase: false, out _),
                $"{verdict} has no StalePendingRowVerdict counterpart in api-spec.yaml");
        }

        Assert.AreEqual(
            Enum.GetValues<StalePendingVerdict>().Length,
            Enum.GetValues<StalePendingRowVerdict>().Length,
            "the contract enum has members the store enum does not");
    }

    [TestMethod]
    public async Task A_failed_audit_write_still_counts_the_repair_and_reports_the_gap()
    {
        // The row is already replaced by the time the audit is written. Reporting that as a failed
        // repair would be a lie, and no later run can compensate: the row is no longer Pending.
        var store = new AuditRefusingStore();
        await SeedRepairable(store, "evt-repairable");
        var service = CreateAdminService(store);

        var result = await service.ReconcileStalePendingAsync(
            EndpointId, Base.AddHours(1), null, "owner", null);

        Assert.AreEqual(1, result.Succeeded, "the repair did happen");
        Assert.AreEqual(0, result.Failed);
        CollectionAssert.AreEqual(new[] { "evt-repairable" }, result.RepairedEventIds.ToArray());
        Assert.AreEqual(1, result.Errors.Count, "the missing audit row is surfaced");
        StringAssert.Contains(result.Errors[0], "audit", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(
            ResolutionStatus.Completed,
            (await store.GetEvent(EndpointId, "evt-repairable"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Reconcile_without_a_budget_scans_past_the_preview_page_cap()
    {
        // The incident shape at scale: hundreds of rows a human must decide sort ahead of the one
        // the rule can repair. A reconcile that reuses the 500-row preview page never reaches it,
        // and no amount of "repair what is listed and preview again" makes progress.
        var store = new PagingStore();
        for (var i = 0; i < AdminService.DefaultStalePendingMaxRows + 1; i++)
        {
            await SeedNoTerminal(store, $"evt-in-flight-{i:D4}");
        }

        await SeedRepairable(store, "evt-repairable", updatedAt: DateTime.UtcNow.AddHours(-2));
        var service = CreateAdminService(store);

        var result = await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), maxRepairs: null, "owner", null);

        Assert.AreEqual(1, result.Succeeded, "the repairable row sorts after 501 operator-decision rows");
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent(EndpointId, "evt-repairable"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Reconcile_with_a_budget_reports_a_full_batch_so_the_caller_knows_to_continue()
    {
        var store = new PagingStore();
        await SeedRepairable(store, "evt-a");
        await SeedRepairable(store, "evt-b");
        await SeedRepairable(store, "evt-c");
        var service = CreateAdminService(store);

        var first = await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), maxRepairs: 2, "owner", null);
        var second = await service.ReconcileStalePendingAsync(EndpointId, Base.AddHours(1), maxRepairs: 2, "owner", null);

        Assert.AreEqual(2, first.Processed, "a full batch: there may be more");
        Assert.AreEqual(1, second.Processed, "a short batch: the scan reached the end");
        Assert.AreEqual(3, first.Succeeded + second.Succeeded);
    }

    /// <summary>
    /// The shipped in-memory store answers one page and no continuation token, which is enough for
    /// every conformance case but hides a scan that stops early. This one pages the way SQL Server
    /// and Cosmos do, with the token carrying the offset.
    /// </summary>
    private sealed class PagingStore : InMemoryMessageStore
    {
        public override async Task<StoreSearchResponse> GetEventsByFilter(StoreEventFilter filter, string continuationToken, int maxSearchItemsCount)
        {
            var all = (await base.GetEventsByFilter(filter, string.Empty, int.MaxValue)).Events.ToList();
            var offset = string.IsNullOrEmpty(continuationToken) ? 0 : int.Parse(continuationToken, System.Globalization.CultureInfo.InvariantCulture);
            var page = all.Skip(offset).Take(maxSearchItemsCount).ToList();
            var next = offset + page.Count;
            return new StoreSearchResponse
            {
                Events = page,
                ContinuationToken = next < all.Count ? next.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            };
        }
    }

    private sealed class AuditRefusingStore : InMemoryMessageStore
    {
        public override Task StoreMessageAudit(
            string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null) =>
            throw new InvalidOperationException("audit sink unavailable");
    }

    // ── seeding ──────────────────────────────────────────────────────────────
    //
    // The incident shape of Spec 030 §1: the endpoint answered, and then a redelivered copy of the
    // request overtook the answer and reopened the row as Pending.

    private static async Task SeedRepairable(
        InMemoryMessageStore store, string eventId, bool backdateRow = true, DateTime? updatedAt = null)
    {
        await store.StoreMessage(Message(eventId, "req-1", MessageType.EventRequest, Base, from: "publisher"));
        await store.StoreMessage(Message(eventId, "rsp-1", MessageType.ResolutionResponse, Base.AddMinutes(1), from: EndpointId));
        await store.StoreMessage(Message(eventId, "req-copy", MessageType.EventRequest, Base.AddMinutes(2), from: "publisher"));
        await Park(store, eventId, MessageType.EventRequest, "req-copy", Base.AddMinutes(2), backdateRow, updatedAt);
    }

    private static async Task SeedNoTerminal(InMemoryMessageStore store, string eventId)
    {
        await store.StoreMessage(Message(eventId, "req-1", MessageType.EventRequest, Base, from: "publisher"));
        await Park(store, eventId, MessageType.EventRequest, "req-1", Base);
    }

    private static async Task SeedLatestTerminalIsError(InMemoryMessageStore store, string eventId)
    {
        await store.StoreMessage(Message(eventId, "req-1", MessageType.EventRequest, Base, from: "publisher"));
        await store.StoreMessage(Message(eventId, "rsp-1", MessageType.ResolutionResponse, Base.AddMinutes(1), from: EndpointId));
        await store.StoreMessage(Message(eventId, "err-1", MessageType.ErrorResponse, Base.AddMinutes(3), from: EndpointId));
        await Park(store, eventId, MessageType.EventRequest, "req-copy", Base.AddMinutes(2));
    }

    private static async Task SeedLaterControlMessage(InMemoryMessageStore store, string eventId)
    {
        await store.StoreMessage(Message(eventId, "req-1", MessageType.EventRequest, Base, from: "publisher"));
        await store.StoreMessage(Message(eventId, "rsp-1", MessageType.ResolutionResponse, Base.AddMinutes(1), from: EndpointId));
        await store.StoreMessage(Message(eventId, "skip-1", MessageType.SkipRequest, Base.AddMinutes(2), from: "manager"));
        await Park(store, eventId, MessageType.SkipRequest, "skip-1", Base.AddMinutes(2));
    }

    /// <summary>
    /// Writes the Pending row the way a request copy would. The store stamps <c>UpdatedAt</c> with
    /// its own clock and keeps the instance it was handed, so back-dating it afterwards is how a test
    /// produces a row that is old enough for the 15-minute age gate.
    /// </summary>
    private static async Task Park(
        InMemoryMessageStore store,
        string eventId,
        MessageType messageType,
        string lastMessageId,
        DateTime enqueuedTimeUtc,
        bool backdate = true,
        DateTime? updatedAt = null)
    {
        var row = new UnresolvedEvent
        {
            EventId = eventId,
            SessionId = SessionId,
            EndpointId = EndpointId,
            EventTypeId = "OrderPlaced",
            MessageType = messageType,
            LastMessageId = lastMessageId,
            EnqueuedTimeUtc = enqueuedTimeUtc,
            To = EndpointId,
            From = "publisher",
        };
        Assert.IsTrue(await store.UploadPendingMessage(eventId, SessionId, EndpointId, row));
        if (backdate)
        {
            row.UpdatedAt = updatedAt ?? DateTime.UtcNow.AddHours(-1);
        }
    }

    private static MessageEntity Message(
        string eventId,
        string messageId,
        MessageType messageType,
        DateTime enqueuedTimeUtc,
        string from) => new()
    {
        EventId = eventId,
        MessageId = messageId,
        SessionId = SessionId,
        EndpointId = EndpointId,
        EventTypeId = "OrderPlaced",
        MessageType = messageType,
        EnqueuedTimeUtc = enqueuedTimeUtc,
        From = from,
        To = from == EndpointId ? "resolver" : EndpointId,
    };

    private static UnresolvedEvent? TryReadPending(InMemoryMessageStore store, string eventId)
    {
        try
        {
            return store.GetPendingEvent(EndpointId, eventId, SessionId).GetAwaiter().GetResult();
        }
        catch (NimBus.MessageStore.Abstractions.EndpointNotFoundException)
        {
            return null;
        }
    }

    private static AdminService CreateAdminService(InMemoryMessageStore store) =>
        new(
            platform: null!,
            messageStore: store,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: null!,
            managerClient: null!,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null);
}
