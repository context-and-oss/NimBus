#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using DeferredReprocessResult = NimBus.WebApp.ManagementApi.DeferredReprocessResult;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;
using NimBus.WebApp.Tests.Mcp;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 035 Phase 3: the recovery commands the operator MCP tools run. Each is refused without
/// Contributor on fresh access lists, and a command that changes the bus runs only once its
/// audit row exists.
/// </summary>
[TestClass]
public sealed class OperatorRecoveryCommandsTests
{
    private const string Endpoint = "Crm";
    private const string EventId = "evt-handoff-1";
    private const string Session = "sess-1";
    private const string ParkedMessageId = "parked-1";

    private static readonly OperatorCommandContext Context = new(OperatorChannel.Mcp, "ERP job 4711 finished", "key-1", "client-1");

    // ── Handoff settlement ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Completing_a_handoff_audits_first_then_sends_parented_to_the_parked_attempt()
    {
        var harness = Harness.Create();

        var result = await harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context);

        Assert.AreEqual(OperatorCommandStatus.Accepted, result.Status);
        CollectionAssert.AreEqual(new[] { "audit:CompleteHandoff", "send:complete" }, harness.Timeline.ToArray());
        var sent = harness.Settlements.Single();
        Assert.AreEqual(Endpoint, sent.EndpointId);
        Assert.AreEqual(ParkedMessageId, sent.Coordinates.MessageId);
        Assert.AreEqual(EventId, sent.Coordinates.EventId);
        Assert.AreEqual(Session, sent.Coordinates.SessionId);
        StringAssert.Contains(sent.Details, "\"note\":\"ERP job 4711 finished\"", StringComparison.Ordinal);
        StringAssert.Contains(sent.Details, "\"completedBy\":\"operator\"", StringComparison.Ordinal);
        var audit = harness.Audit.Required.Single();
        StringAssert.Contains(audit.Data, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"outcome\":\"complete\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"clientId\":\"client-1\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Failing_a_handoff_sends_the_reason_and_error_type()
    {
        var harness = Harness.Create();

        var result = await harness.Sut.SettleHandoffAsync(Handoff(), complete: false, errorType: "ErpRejected",
            Context with { Reason = "ERP rejected the order" });

        Assert.AreEqual(OperatorCommandStatus.Accepted, result.Status);
        CollectionAssert.AreEqual(new[] { "audit:FailHandoff", "send:fail" }, harness.Timeline.ToArray());
        var sent = harness.Settlements.Single();
        Assert.AreEqual("ERP rejected the order", sent.ErrorText);
        Assert.AreEqual("ErpRejected", sent.ErrorType);
    }

    [TestMethod]
    [DataRow(ResolutionStatus.Failed, null)]
    [DataRow(ResolutionStatus.Pending, null)]
    [DataRow(ResolutionStatus.Completed, "Handoff")]
    public async Task A_message_that_is_not_a_pending_handoff_is_not_settled(ResolutionStatus status, string? subStatus)
    {
        var harness = Harness.Create();

        var result = await harness.Sut.SettleHandoffAsync(Handoff(status, subStatus), complete: true, errorType: null, Context);

        Assert.AreEqual(OperatorCommandStatus.NotAllowed, result.Status);
        Assert.AreEqual(0, harness.Timeline.Count, "Nothing is audited as run and nothing is sent.");
    }

    [TestMethod]
    public async Task A_role_revoked_since_the_preview_is_refused_and_the_denial_is_audited()
    {
        var harness = Harness.Create(new Authorization(AccessRole.Contributor, fresh: AccessRole.Reader));

        var result = await harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context);

        Assert.AreEqual(OperatorCommandStatus.Forbidden, result.Status);
        Assert.AreEqual(0, harness.Settlements.Count);
        var denial = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.CompleteHandoff, denial.Type);
        Assert.IsTrue(denial.AccessDenied);
    }

    [TestMethod]
    public async Task An_unrecordable_audit_sends_nothing_and_leaves_the_handoff_settleable()
    {
        var harness = Harness.Create();
        harness.Audit.FailRequired = true;

        var refused = await harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context);
        harness.Audit.FailRequired = false;
        var retried = await harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context);

        Assert.AreEqual(OperatorCommandStatus.AuditUnavailable, refused.Status);
        Assert.AreEqual(OperatorCommandStatus.Accepted, retried.Status, "The settlement claim was released.");
        Assert.AreEqual(1, harness.Settlements.Count);
    }

    [TestMethod]
    public async Task A_failed_send_releases_the_claim_and_records_CommandNotSent()
    {
        var harness = Harness.Create();
        harness.FailNextSend = new InvalidOperationException("Service Bus unavailable");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context));
        var retried = await harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context);

        var notSent = harness.Audit.Logged.Single(a => a.Type == MessageAuditType.CommandNotSent);
        StringAssert.Contains(notSent.Data, "\"command\":\"CompleteHandoff\"", StringComparison.Ordinal);
        Assert.AreEqual(OperatorCommandStatus.Accepted, retried.Status);
    }

    [TestMethod]
    public async Task A_second_settlement_while_the_first_is_in_flight_is_refused()
    {
        var harness = Harness.Create();

        var first = await harness.Sut.SettleHandoffAsync(Handoff(), complete: true, errorType: null, Context);
        var second = await harness.Sut.SettleHandoffAsync(Handoff(), complete: false, errorType: null, Context);

        Assert.AreEqual(OperatorCommandStatus.Accepted, first.Status);
        Assert.AreEqual(OperatorCommandStatus.NotAllowed, second.Status);
        StringAssert.Contains(second.Detail, "already in flight", StringComparison.Ordinal);
        Assert.AreEqual(1, harness.Settlements.Count);
    }

    // ── Deferred reprocess ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Reprocess_is_audited_before_the_session_is_reset()
    {
        var harness = Harness.Create();

        var outcome = await harness.Sut.ReprocessDeferredAsync(Endpoint, Session, EventId, deferredCount: 3, Context);

        Assert.AreEqual(OperatorCommandStatus.Accepted, outcome.Result.Status);
        Assert.IsTrue(outcome.SessionStateCleared && outcome.ProcessRequestSent);
        CollectionAssert.AreEqual(new[] { "audit:ReprocessDeferred", "reprocess:Crm/sess-1" }, harness.Timeline.ToArray());
        var audit = harness.Audit.Required.Single();
        Assert.AreEqual(EventId, audit.EventId, "The audit row is filed under the message the action was prepared from.");
        StringAssert.Contains(audit.Data, "\"deferredCount\":3", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"sessionId\":\"sess-1\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Reprocess_without_an_audit_row_or_the_role_resets_nothing()
    {
        var unrecordable = Harness.Create();
        unrecordable.Audit.FailRequired = true;
        var denied = Harness.Create(new Authorization(AccessRole.Contributor, fresh: AccessRole.Reader));

        var noAudit = await unrecordable.Sut.ReprocessDeferredAsync(Endpoint, Session, EventId, 1, Context);
        var noRole = await denied.Sut.ReprocessDeferredAsync(Endpoint, Session, EventId, 1, Context);

        Assert.AreEqual(OperatorCommandStatus.AuditUnavailable, noAudit.Result.Status);
        Assert.AreEqual(OperatorCommandStatus.Forbidden, noRole.Result.Status);
        Assert.IsFalse(unrecordable.Timeline.Any(step => step.StartsWith("reprocess", StringComparison.Ordinal)));
        Assert.IsFalse(denied.Timeline.Any(step => step.StartsWith("reprocess", StringComparison.Ordinal)));
        Assert.IsTrue(denied.Audit.Logged.Single().AccessDenied);
    }

    // ── Stale deferred tracking ─────────────────────────────────────────────

    [TestMethod]
    public async Task Skipping_stale_deferred_tracking_records_the_channel_and_reports_the_audit()
    {
        var harness = Harness.Create();
        await harness.SeedStaleDeferredRowAsync();
        var inspected = await harness.Sut.InspectDeferredAsync(Endpoint, "event");
        Assert.IsTrue(inspected!.Value.Inspection.CanSkip);

        var (result, auditRecorded) = await harness.Sut.SkipDeferredTrackingAsync(
            Endpoint, "event", inspected.Value.Inspection.RowVersion, Context with { Reason = "Completed in CRM, checked by hand" });

        Assert.AreEqual(OperatorCommandStatus.Accepted, result.Status);
        Assert.IsTrue(auditRecorded);
        Assert.AreEqual(ResolutionStatus.Skipped, (await harness.Store.GetEvent(Endpoint, "event"))!.ResolutionStatus);
        var audit = (await harness.Store.GetMessageAudits("event")).Single();
        Assert.AreEqual(MessageAuditType.Skip, audit.AuditType);
        Assert.AreEqual("Completed in CRM, checked by hand", audit.Comment);
        StringAssert.Contains(audit.Data!, "\"action\":\"skip-deferred-tracking\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data!, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data!, "\"clientId\":\"client-1\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_changed_tracking_row_is_stale_and_an_incomplete_broker_check_is_not_allowed()
    {
        var changed = Harness.Create();
        await changed.SeedStaleDeferredRowAsync();
        var present = Harness.Create();
        await present.SeedStaleDeferredRowAsync();
        var version = (await present.Sut.InspectDeferredAsync(Endpoint, "event"))!.Value.Inspection.RowVersion;
        present.Broker.Error = new Azure.Messaging.ServiceBus.ServiceBusException(
            "unavailable", Azure.Messaging.ServiceBus.ServiceBusFailureReason.ServiceCommunicationProblem);

        var stale = await changed.Sut.SkipDeferredTrackingAsync(Endpoint, "event", "session:other:1", Context);
        var unknown = await present.Sut.SkipDeferredTrackingAsync(Endpoint, "event", version, Context);
        var missing = await changed.Sut.SkipDeferredTrackingAsync(Endpoint, "no-such-event", "v", Context);

        Assert.AreEqual(OperatorCommandStatus.Stale, stale.Result.Status);
        Assert.AreEqual(OperatorCommandStatus.NotAllowed, unknown.Result.Status);
        Assert.AreEqual(OperatorCommandStatus.NotFound, missing.Result.Status);
        Assert.AreEqual(ResolutionStatus.Deferred, (await present.Store.GetEvent(Endpoint, "event"))!.ResolutionStatus);
        Assert.AreEqual(0, (await present.Store.GetMessageAudits("event")).Count());
    }

    [TestMethod]
    public async Task Skipping_tracking_without_the_role_is_refused_and_audited()
    {
        var harness = Harness.Create(new Authorization(AccessRole.Contributor, fresh: AccessRole.Reader));
        await harness.SeedStaleDeferredRowAsync();
        var version = (await harness.Sut.InspectDeferredAsync(Endpoint, "event"))!.Value.Inspection.RowVersion;

        var (result, _) = await harness.Sut.SkipDeferredTrackingAsync(Endpoint, "event", version, Context);

        Assert.AreEqual(OperatorCommandStatus.Forbidden, result.Status);
        Assert.AreEqual(ResolutionStatus.Deferred, (await harness.Store.GetEvent(Endpoint, "event"))!.ResolutionStatus);
        var denial = harness.Audit.Logged.Single();
        Assert.IsTrue(denial.AccessDenied);
        StringAssert.Contains(denial.Data, "skip-deferred-tracking", StringComparison.Ordinal);
    }

    private static OperatorCommandTarget Handoff(ResolutionStatus status = ResolutionStatus.Pending, string? subStatus = "Handoff")
    {
        var row = new UnresolvedEvent
        {
            EndpointId = Endpoint, EventId = EventId, SessionId = Session, LastMessageId = ParkedMessageId,
            EventTypeId = "Demo.Type", CorrelationId = "corr-1", OriginatingMessageId = "req-1",
            ResolutionStatus = status, PendingSubStatus = subStatus, UpdatedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var message = new MessageEntity { EventId = EventId, MessageId = ParkedMessageId, SessionId = Session, EventTypeId = "Demo.Type" };
        return new OperatorCommandTarget(Endpoint, row, message, OperatorMessageVersion.From(row));
    }

    private sealed record Settlement(string EndpointId, HandoffSettlement Coordinates, string? Details, string? ErrorText, string? ErrorType);

    private sealed class Harness
    {
        public required OperatorRecoveryCommands Sut { get; init; }
        public required InMemoryMessageStore Store { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required ConcurrentQueue<string> Timeline { get; init; }
        public required DeferredMessageInspectorTests.FakeClient Broker { get; init; }
        public List<Settlement> Settlements { get; } = [];
        public Exception? FailNextSend { get; set; }

        public static Harness Create(Authorization? authorization = null)
        {
            var timeline = new ConcurrentQueue<string>();
            var store = new InMemoryMessageStore();
            var audit = new RecordingAudit(timeline);
            var broker = new DeferredMessageInspectorTests.FakeClient();
            Harness harness = null!;

            var handoffClients = InterfaceFake<IHandoffClientFactory>.Create().On("ForEndpoint", args =>
            {
                var endpointId = (string)args[0]!;
                return InterfaceFake<IHandoffClient>.Create()
                    .On("CompleteAsync", send => harness.Send("complete",
                        new Settlement(endpointId, (HandoffSettlement)send[0]!, send[1] as string, null, null)))
                    .On("FailAsync", send => harness.Send("fail",
                        new Settlement(endpointId, (HandoffSettlement)send[0]!, null, (string)send[1]!, send[2] as string)))
                    .Instance;
            });
            var admin = InterfaceFake<IAdminService>.Create().On("ReprocessDeferredAsync", args =>
            {
                timeline.Enqueue($"reprocess:{args[0]}/{args[1]}");
                return Task.FromResult(new DeferredReprocessResult
                {
                    SessionId = (string)args[1]!, SessionStateCleared = true, ProcessRequestSent = true, Errors = new List<string>(),
                });
            });

            harness = new Harness
            {
                Store = store,
                Audit = audit,
                Timeline = timeline,
                Broker = broker,
                Sut = new OperatorRecoveryCommands(
                    authorization ?? new Authorization(AccessRole.Contributor),
                    audit,
                    new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
                    handoffClients.Instance,
                    new HandoffSettlementTracker(TimeProvider.System),
                    admin.Instance,
                    new DeferredTrackingRecovery(store, broker, null, NullLogger.Instance),
                    NullLogger<OperatorRecoveryCommands>.Instance),
            };
            return harness;
        }

        public async Task SeedStaleDeferredRowAsync()
        {
            var row = DeferredMessageInspectorTests.Row();
            await Store.UploadDeferredMessage(row.EventId, row.SessionId!, Endpoint, row);
            (await Store.GetEvent(Endpoint, "event"))!.UpdatedAt = DateTime.UtcNow.AddHours(-1);
        }

        private Task Send(string kind, Settlement settlement)
        {
            if (FailNextSend is { } failure)
            {
                FailNextSend = null;
                return Task.FromException(failure);
            }

            Timeline.Enqueue("send:" + kind);
            Settlements.Add(settlement);
            return Task.CompletedTask;
        }
    }

    private sealed class Authorization(AccessRole role, AccessRole? fresh = null) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(role >= required);

        public Task<bool> HasRoleFreshAsync(AccessRole required, string endpointId) => Task.FromResult((fresh ?? role) >= required);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());

        public string GetCurrentUserName() => "operator";
    }

    private sealed record AuditCall(MessageAuditType Type, bool AccessDenied, string? Data, string? EventId = null);

    private sealed class RecordingAudit(ConcurrentQueue<string> timeline) : IAuditLogService
    {
        public List<AuditCall> Logged { get; } = [];

        public List<AuditCall> Required { get; } = [];

        public bool FailRequired { get; set; }

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Logged.Add(new AuditCall(type, accessDenied, data, eventId));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
        {
            if (FailRequired)
                throw new AuditUnavailableException("The audit row could not be recorded, so the command was not run.");
            Required.Add(new AuditCall(type, false, data, eventId));
            timeline.Enqueue("audit:" + type);
            return Task.CompletedTask;
        }
    }
}
