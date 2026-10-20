#pragma warning disable CA1707, CA2007
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class DeferredRecoveryApiTests
{
    [TestMethod]
    public async Task Skip_updates_only_inspected_event_preserves_history_and_writes_reason_audit()
    {
        var (sut, store) = await CreateAsync();
        await store.StoreMessage(new MessageEntity { EventId = "event", MessageId = "history", SessionId = "session" });
        var inspection = (await sut.GetDeferredInspectionAsync("crM", "event")).Value!;
        Assert.IsNotNull(inspection);
        var result = await sut.PostSkipDeferredTrackingAsync("crm", "event",
            new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = "Checked with CRM operator" });
        Assert.IsTrue(result.Value!.AuditRecorded);
        Assert.AreEqual(ResolutionStatus.Skipped, (await store.GetEvent("Crm", "event"))!.ResolutionStatus);
        var audit = (await store.GetMessageAudits("event")).Single();
        Assert.AreEqual("Checked with CRM operator", audit.Comment);
        Assert.AreEqual("operator", audit.AuditorName);
        StringAssert.Contains(audit.Data!, "skip-deferred-tracking");
        Assert.AreEqual("history", (await store.GetEventHistory("event")).Single().MessageId);
    }

    [TestMethod]
    public async Task Changed_row_and_empty_reason_are_rejected()
    {
        var (sut, store) = await CreateAsync();
        var inspection = (await sut.GetDeferredInspectionAsync("Crm", "event")).Value!;
        var empty = await sut.PostSkipDeferredTrackingAsync("Crm", "event", new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = " " });
        Assert.IsInstanceOfType<BadRequestObjectResult>(empty.Result);
        (await store.GetEvent("Crm", "event"))!.LastMessageId = "new-deferral";
        var changed = await sut.PostSkipDeferredTrackingAsync("Crm", "event", new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = "checked" });
        Assert.IsInstanceOfType<ConflictObjectResult>(changed.Result);
        Assert.AreEqual(ResolutionStatus.Deferred, (await store.GetEvent("Crm", "event"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Skip_rechecks_broker_and_refuses_newly_present_message()
    {
        var broker = new DeferredMessageInspectorTests.FakeClient();
        var (sut, store) = await CreateAsync(broker: broker);
        var inspection = (await sut.GetDeferredInspectionAsync("Crm", "event")).Value!;
        broker.Messages = [Azure.Messaging.ServiceBus.ServiceBusModelFactory.ServiceBusReceivedMessage(
            sessionId: "session", sequenceNumber: 1, properties: new Dictionary<string, object> { ["EventId"] = "event" })];
        var result = await sut.PostSkipDeferredTrackingAsync("Crm", "event", new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = "checked" });
        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
        Assert.AreEqual(ResolutionStatus.Deferred, (await store.GetEvent("Crm", "event"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Reader_can_inspect_but_cannot_skip_and_denial_is_audited()
    {
        var audit = new Audit();
        var (sut, _) = await CreateAsync(AccessRole.Reader, audit);
        var inspection = (await sut.GetDeferredInspectionAsync("Crm", "event")).Value!;
        Assert.IsFalse(inspection.CanSkip);
        var result = await sut.PostSkipDeferredTrackingAsync("Crm", "event", new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = "checked" });
        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.IsTrue(audit.Denied);
    }

    [TestMethod]
    public async Task Unauthorised_reader_and_unknown_endpoint_are_rejected()
    {
        var (sut, _) = await CreateAsync(AccessRole.None);
        Assert.IsInstanceOfType<ForbidResult>((await sut.GetDeferredInspectionAsync("Crm", "event")).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>((await sut.GetDeferredInspectionAsync("unknown", "event")).Result);
    }

    private static async Task<(EventImplementation Sut, InMemoryMessageStore Store)> CreateAsync(
        AccessRole role = AccessRole.Contributor, Audit? audit = null, DeferredMessageInspectorTests.FakeClient? broker = null,
        InMemoryMessageStore? store = null)
    {
        store ??= new InMemoryMessageStore();
        var row = DeferredMessageInspectorTests.Row();
        await store.UploadDeferredMessage(row.EventId, row.SessionId!, "Crm", row);
        (await store.GetEvent("Crm", "event"))!.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        var sut = new EventImplementation(null!, new Catalog(), null!, null!, NullLogger<EventImplementation>.Instance,
            store, new Authorization(role), null!, broker ?? new DeferredMessageInspectorTests.FakeClient(), audit ?? new Audit(), null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);
        return (sut, store);
    }

    [TestMethod]
    public async Task Completion_during_broker_recheck_is_not_overwritten()
    {
        var broker = new DeferredMessageInspectorTests.FakeClient();
        var (sut, store) = await CreateAsync(broker: broker);
        var inspection = (await sut.GetDeferredInspectionAsync("Crm", "event")).Value!;
        broker.BeforePeek = () => store.UploadCompletedMessage("event", "session", "Crm",
            DeferredMessageInspectorTests.Row()).GetAwaiter().GetResult();
        var result = await sut.PostSkipDeferredTrackingAsync("Crm", "event", new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = "checked" });
        Assert.IsInstanceOfType<ConflictObjectResult>(result.Result);
        Assert.AreEqual(ResolutionStatus.Completed, (await store.GetEvent("Crm", "event"))!.ResolutionStatus);
        Assert.AreEqual(0, (await store.GetMessageAudits("event")).Count());
    }

    [TestMethod]
    public async Task Audit_failure_reports_successful_skip_without_claiming_an_audit_was_saved()
    {
        var (sut, store) = await CreateAsync(store: new AuditFailureStore());
        var inspection = (await sut.GetDeferredInspectionAsync("Crm", "event")).Value!;
        var result = await sut.PostSkipDeferredTrackingAsync("Crm", "event", new DeferredSkipRequest { RowVersion = inspection.RowVersion, Reason = "checked" });
        Assert.IsFalse(result.Value!.AuditRecorded);
        Assert.AreEqual(ResolutionStatus.Skipped, (await store.GetEvent("Crm", "event"))!.ResolutionStatus);
    }

    private sealed class AuditFailureStore : InMemoryMessageStore
    {
        public override Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null) =>
            throw new InvalidOperationException("Audit store unavailable");
    }

    private sealed class Catalog : Platform { public Catalog() { AddEndpoint(new Crm()); } }
    private sealed class Crm : NimBus.Core.Endpoints.Endpoint { }
    private sealed class Authorization(AccessRole role) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(role >= required);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string GetCurrentUserName() => "operator";
    }
    private sealed class Audit : IAuditLogService
    {
        internal bool Denied { get; private set; }
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        { Denied = accessDenied; return Task.CompletedTask; }
    }
}
