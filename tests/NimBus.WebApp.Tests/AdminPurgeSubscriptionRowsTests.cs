#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Endpoints;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Endpoint = NimBus.Core.Endpoints.Endpoint;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Operations → Purge subscription deletes the stored Pending and Deferred rows of the messages
/// it removes, keeps every other row, and reports how many it deleted (#289).
/// </summary>
[TestClass]
public sealed class AdminPurgeSubscriptionRowsTests
{
    private const string EndpointId = "CurrentEndpoint";

    [TestMethod]
    public async Task Pending_rows_of_purged_messages_are_deleted()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        foreach (var (eventId, sessionId) in new[] { ("evt-1", "s1"), ("evt-2", "s1"), ("evt-3", "s2") })
        {
            bus.Add(sessionId, eventId);
            await store.UploadPendingMessage(eventId, sessionId, EndpointId, Row(eventId));
        }

        var result = await Purge(store, bus);

        Assert.AreEqual(3, result.Succeeded);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(3, result.Processed);
        Assert.AreEqual(3, result.RowsDeleted);
        Assert.AreEqual(0, result.Errors.Count);
        foreach (var eventId in new[] { "evt-1", "evt-2", "evt-3" })
            Assert.IsNull(await store.GetEvent(EndpointId, eventId), eventId);
    }

    [TestMethod]
    public async Task Deferred_rows_of_purged_deferred_messages_are_deleted()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        bus.Add("s1", "evt-1", ServiceBusMessageState.Deferred);
        bus.Add("s1", "evt-2", ServiceBusMessageState.Deferred);
        await store.UploadDeferredMessage("evt-1", "s1", EndpointId, Row("evt-1"));
        await store.UploadDeferredMessage("evt-2", "s1", EndpointId, Row("evt-2"));

        var result = await Purge(store, bus);

        Assert.AreEqual(2, result.Succeeded);
        Assert.AreEqual(2, result.RowsDeleted);
        Assert.IsNull(await store.GetEvent(EndpointId, "evt-1"));
        Assert.IsNull(await store.GetEvent(EndpointId, "evt-2"));
    }

    [TestMethod]
    public async Task Rows_in_any_other_status_are_kept()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        await store.UploadFailedMessage("evt-failed", "s1", EndpointId, Row("evt-failed"));
        await store.UploadPendingMessage("evt-handoff", "s1", EndpointId, Row("evt-handoff", pendingSubStatus: "handoff"));
        await store.UploadCompletedMessage("evt-completed", "s1", EndpointId, Row("evt-completed"));
        await store.UploadSkippedMessage("evt-skipped", "s1", EndpointId, Row("evt-skipped"));
        await store.UploadDeadletteredMessage("evt-dead", "s1", EndpointId, Row("evt-dead"));
        await store.UploadUnsupportedMessage("evt-unsupported", "s1", EndpointId, Row("evt-unsupported"));
        var kept = new[] { "evt-failed", "evt-handoff", "evt-completed", "evt-skipped", "evt-dead", "evt-unsupported" };
        foreach (var eventId in kept)
            bus.Add("s1", eventId);

        var result = await Purge(store, bus);

        Assert.AreEqual(kept.Length, result.Succeeded);
        Assert.AreEqual(0, result.RowsDeleted);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.AreEqual(0, store.RemoveCalls.Count);
        foreach (var eventId in kept)
            Assert.IsNotNull(await store.GetEvent(EndpointId, eventId), eventId);
    }

    [TestMethod]
    public async Task A_message_without_an_EventId_is_purged_and_touches_no_row()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        bus.Add("s1", eventId: null);
        bus.Add("s1", eventId: "");

        var result = await Purge(store, bus);

        Assert.AreEqual(2, result.Succeeded);
        Assert.AreEqual(0, result.RowsDeleted);
        Assert.AreEqual(0, store.GetEventCalls.Count);
        Assert.AreEqual(0, store.RemoveCalls.Count);
    }

    [TestMethod]
    public async Task A_purged_message_without_a_row_is_not_a_failure()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        bus.Add("s1", "evt-gone");

        var result = await Purge(store, bus);

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, result.RowsDeleted);
        Assert.AreEqual(0, result.Errors.Count);
        CollectionAssert.AreEqual(new[] { "evt-gone" }, store.GetEventCalls);
        Assert.AreEqual(0, store.RemoveCalls.Count);
    }

    [TestMethod]
    public async Task Two_messages_with_the_same_EventId_delete_its_row_once()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        bus.Add("s1", "evt-1");
        bus.Add("s1", "evt-1");
        await store.UploadPendingMessage("evt-1", "s1", EndpointId, Row("evt-1"));

        var result = await Purge(store, bus);

        Assert.AreEqual(2, result.Succeeded);
        Assert.AreEqual(1, result.RowsDeleted);
        Assert.AreEqual(1, store.GetEventCalls.Count);
        Assert.AreEqual(1, store.RemoveCalls.Count);
    }

    [TestMethod]
    public async Task Messages_the_purge_does_not_complete_touch_no_row()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        var missing = bus.Add("s1", "evt-missing", ServiceBusMessageState.Deferred);
        bus.MissingDeferred.Add(missing);
        bus.Add("s1", "evt-old", enqueuedTime: DateTimeOffset.UtcNow.AddHours(-1));
        bus.Add("s1", "evt-new", enqueuedTime: DateTimeOffset.UtcNow.AddMinutes(-1));
        bus.Add("s-locked", "evt-locked");
        bus.LockedSessions.Add("s-locked");
        var failing = bus.Add("s2", "evt-fails", ServiceBusMessageState.Deferred);
        bus.FailCompletion.Add(failing);
        foreach (var eventId in new[] { "evt-missing", "evt-old", "evt-new", "evt-locked", "evt-fails" })
            await store.UploadPendingMessage(eventId, eventId == "evt-locked" ? "s-locked" : eventId == "evt-fails" ? "s2" : "s1", EndpointId, Row(eventId));

        var result = await Purge(store, bus, before: cutoff);

        CollectionAssert.AreEqual(new[] { "evt-old" }, store.GetEventCalls);
        Assert.AreEqual(1, result.RowsDeleted);
        Assert.IsNotNull(await store.GetEvent(EndpointId, "evt-missing"));
        Assert.IsNotNull(await store.GetEvent(EndpointId, "evt-new"));
        Assert.IsNotNull(await store.GetEvent(EndpointId, "evt-locked"));
        Assert.IsNotNull(await store.GetEvent(EndpointId, "evt-fails"));
    }

    [TestMethod]
    public async Task An_abandoned_active_message_in_a_purged_session_touches_no_row()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        bus.Add("s1", "evt-old", enqueuedTime: DateTimeOffset.UtcNow.AddHours(-1));
        bus.Add("s1", "evt-new", enqueuedTime: DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.UploadPendingMessage("evt-old", "s1", EndpointId, Row("evt-old"));
        await store.UploadPendingMessage("evt-new", "s1", EndpointId, Row("evt-new"));

        var result = await Purge(store, bus, before: cutoff);

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(1, result.RowsDeleted);
        Assert.IsTrue(bus.Abandoned.Count > 0, "the newer message is received and abandoned");
        CollectionAssert.AreEqual(new[] { "evt-old" }, store.GetEventCalls);
        Assert.IsNotNull(await store.GetEvent(EndpointId, "evt-new"));
    }

    [TestMethod]
    public async Task A_failed_row_deletion_is_counted_and_the_rest_still_run()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        foreach (var eventId in new[] { "evt-throws", "evt-false", "evt-get-throws", "evt-ok" })
        {
            bus.Add("s1", eventId);
            await store.UploadPendingMessage(eventId, "s1", EndpointId, Row(eventId));
        }
        store.RemoveThrows.Add("evt-throws");
        store.RemoveReturnsFalse.Add("evt-false");
        store.GetEventThrows.Add("evt-get-throws");

        var result = await Purge(store, bus);

        Assert.AreEqual(4, result.Succeeded, "every removed message stays counted as purged");
        Assert.AreEqual(3, result.Failed);
        Assert.AreEqual(7, result.Processed);
        Assert.AreEqual(1, result.RowsDeleted);
        Assert.AreEqual(3, result.Errors.Count);
        foreach (var eventId in new[] { "evt-throws", "evt-false", "evt-get-throws" })
            Assert.IsTrue(result.Errors.Any(e => e.Contains(eventId, StringComparison.Ordinal)), eventId);
        Assert.IsNull(await store.GetEvent(EndpointId, "evt-ok"));
    }

    [TestMethod]
    public async Task RemoveMessage_gets_the_session_the_message_was_completed_in()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        bus.Add("session-a", "evt-1");
        bus.Add("session-b", "evt-2", ServiceBusMessageState.Deferred);
        await store.UploadPendingMessage("evt-1", "session-a", EndpointId, Row("evt-1"));
        await store.UploadDeferredMessage("evt-2", "session-b", EndpointId, Row("evt-2"));

        await Purge(store, bus);

        CollectionAssert.AreEquivalent(
            new[] { ("evt-1", "session-a", EndpointId), ("evt-2", "session-b", EndpointId) },
            store.RemoveCalls);
    }

    [TestMethod]
    public async Task An_empty_purge_reports_zero_rows_deleted()
    {
        var result = await Purge(new RecordingStore(), new FakePurgeBus());

        Assert.AreEqual(0, result.RowsDeleted);
    }

    [TestMethod]
    public async Task Delete_by_status_leaves_rowsDeleted_unset()
    {
        var store = new RecordingStore();
        await store.UploadCompletedMessage("evt-1", "s1", EndpointId, Row("evt-1"));

        var result = await CreateAdminService(store, new FakePurgeBus())
            .DeleteByStatusAsync(EndpointId, new List<string> { "Completed" });

        Assert.AreEqual(1, result.Succeeded);
        Assert.IsNull(result.RowsDeleted);
    }

    [TestMethod]
    public async Task A_successful_purge_audits_the_request_and_rowsDeleted()
    {
        var store = new RecordingStore();
        var bus = new FakePurgeBus();
        bus.Add("s1", "evt-1");
        await store.UploadPendingMessage("evt-1", "s1", EndpointId, Row("evt-1"));
        var audit = new RecordingAuditLogService();
        var sut = CreateController(CreateAdminService(store, bus), audit, authorized: true);
        var before = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var response = await sut.PostAdminPurgeAsync(
            EndpointId, new PurgeRequest { Subscription = EndpointId, States = new List<string> { "active" }, Before = before });

        var ok = Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        Assert.AreEqual(1, ((BulkOperationResult)ok.Value!).RowsDeleted);
        var entry = audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.PurgeMessages, entry.Type);
        Assert.IsFalse(entry.AccessDenied);
        var data = JObject.Parse(entry.Data);
        Assert.AreEqual(1, (int)data["rowsDeleted"]!);
        Assert.AreEqual(EndpointId, (string?)data["subscription"]);
        Assert.AreEqual("active", (string?)data["states"]![0]);
        Assert.AreEqual(before, (DateTime)data["before"]!);
    }

    [TestMethod]
    public async Task A_denied_purge_audits_the_bare_request()
    {
        var audit = new RecordingAuditLogService();
        var sut = CreateController(CreateAdminService(new RecordingStore(), new FakePurgeBus()), audit, authorized: false);

        var response = await sut.PostAdminPurgeAsync(
            EndpointId, new PurgeRequest { Subscription = EndpointId, States = new List<string> { "active" } });

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        var entry = audit.Entries.Single();
        Assert.IsTrue(entry.AccessDenied);
        var data = JObject.Parse(entry.Data);
        Assert.IsNull(data["rowsDeleted"]);
        Assert.AreEqual(EndpointId, (string?)data["subscription"]);
    }

    private static Task<BulkOperationResult> Purge(RecordingStore store, FakePurgeBus bus, DateTime? before = null) =>
        CreateAdminService(store, bus).PurgeSubscriptionAsync(EndpointId, EndpointId, new List<string>(), before);

    private static UnresolvedEvent Row(string eventId, string? pendingSubStatus = null) => new()
    {
        EventId = eventId,
        EventTypeId = "OrderPlaced",
        PendingSubStatus = pendingSubStatus,
        UpdatedAt = DateTime.UtcNow,
        EnqueuedTimeUtc = DateTime.UtcNow,
    };

    private static AdminService CreateAdminService(RecordingStore store, FakePurgeBus bus) =>
        new(
            platform: null!,
            messageStore: store,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: bus,
            managerClient: null!,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null);

    private static AdminImplementation CreateController(IAdminService adminService, IAuditLogService audit, bool authorized) =>
        new(
            new TestHttpContextAccessor(new DefaultHttpContext()),
            adminService,
            subscriptionAdminService: null!,
            new TestPlatform(),
            new ConfigurationBuilder().Build(),
            audit,
            new FixedAuthorizationService(authorized),
            heartbeatService: null!);

    /// <summary>
    /// Records the row lookups and deletions the purge makes, and fails chosen ones.
    /// </summary>
    private sealed class RecordingStore : InMemoryMessageStore, IMessageTrackingStore
    {
        public List<string> GetEventCalls { get; } = new();

        public List<(string EventId, string SessionId, string EndpointId)> RemoveCalls { get; } = new();

        public HashSet<string> GetEventThrows { get; } = new();

        public HashSet<string> RemoveThrows { get; } = new();

        public HashSet<string> RemoveReturnsFalse { get; } = new();

        Task<UnresolvedEvent?> IMessageTrackingStore.GetEvent(string endpointId, string eventId)
        {
            GetEventCalls.Add(eventId);
            if (GetEventThrows.Contains(eventId))
                throw new InvalidOperationException("store unavailable");
            return GetEvent(endpointId, eventId);
        }

        Task<bool> IMessageTrackingStore.RemoveMessage(string eventId, string sessionId, string endpointId)
        {
            RemoveCalls.Add((eventId, sessionId, endpointId));
            if (RemoveThrows.Contains(eventId))
                throw new InvalidOperationException("delete failed");
            if (RemoveReturnsFalse.Contains(eventId))
                return Task.FromResult(false);
            return RemoveMessage(eventId, sessionId, endpointId);
        }
    }

    private sealed class TestPlatform : NimBus.Core.Platform
    {
        public TestPlatform() => AddEndpoint(new CurrentEndpoint());
    }

    private sealed class CurrentEndpoint : Endpoint { }

    private sealed class TestHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class FixedAuthorizationService(bool authorized) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(authorized);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string? GetCurrentUserName() => "test-owner";
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<(MessageAuditType Type, bool AccessDenied, string Data)> Entries { get; } = [];

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false,
            string? data = null, string? eventId = null, string? endpointId = null,
            string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((type, accessDenied, data ?? string.Empty));
            return Task.CompletedTask;
        }
    }
}
