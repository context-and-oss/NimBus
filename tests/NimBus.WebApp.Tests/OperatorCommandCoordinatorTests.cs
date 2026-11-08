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
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 035 Phase 2a: the shared guard for operator commands. A command runs only while the
/// row still has the version the caller saw, after a fresh role check and an audit row, so the
/// Web UI and an agent racing on the same message send exactly one command.
/// </summary>
[TestClass]
public sealed class OperatorCommandCoordinatorTests
{
    private const string Endpoint = "Crm";
    private const string EventId = "evt-guard-1";
    private const string Session = "sess-1";
    private const string TerminalMessageId = "term-1";

    [TestMethod]
    public async Task Resubmit_audits_then_sends_once_and_archives_the_row()
    {
        var harness = await Harness.CreateAsync();
        var target = await harness.FindAsync();

        var result = await harness.Sut.ResubmitAsync(target, new OperatorCommandContext(OperatorChannel.Mcp, "Downstream fixed", "key-1", "client-1"));

        Assert.AreEqual(OperatorCommandStatus.Accepted, result.Status);
        CollectionAssert.AreEqual(new[] { "audit:Resubmit", "send:Resubmit" }, harness.Timeline.ToArray());
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, EventId), "The claimed row is archived.");
        StringAssert.Contains(harness.Audit.Required.Single().Data, "\"reason\":\"Downstream fixed\"", StringComparison.Ordinal);
        StringAssert.Contains(harness.Audit.Required.Single().Data, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_message_that_is_no_longer_the_latest_attempt_is_stale()
    {
        var harness = await Harness.CreateAsync(rowLastMessageId: "term-2");

        var lookup = await harness.Sut.FindByMessageAsync(EventId, TerminalMessageId);

        Assert.AreEqual(OperatorCommandStatus.Stale, lookup.Status);
        Assert.IsNull(lookup.Target);
    }

    [TestMethod]
    public async Task A_row_that_changed_after_it_was_read_is_stale_and_nothing_is_sent()
    {
        var harness = await Harness.CreateAsync();
        var target = await harness.FindAsync();
        // The endpoint fails again: the row moves to a newer attempt.
        await OperatorCommandTestRows.SeedFailedRowAsync(harness.Store, Endpoint, EventId, Session, "term-2");

        var result = await harness.Sut.SkipAsync(target, new OperatorCommandContext(OperatorChannel.WebApp));

        Assert.AreEqual(OperatorCommandStatus.Stale, result.Status);
        Assert.AreEqual(0, harness.Manager.Sends);
        Assert.AreEqual(0, harness.Audit.Required.Count);
        Assert.AreEqual("term-2", (await harness.Store.GetEvent(Endpoint, EventId))!.LastMessageId);
    }

    [TestMethod]
    public async Task Web_UI_and_agent_racing_on_one_version_send_exactly_one_command()
    {
        var harness = await Harness.CreateAsync();
        var uiTarget = await harness.FindAsync();
        var agentTarget = await harness.FindAsync();

        var results = await Task.WhenAll(
            Task.Run(() => harness.Sut.ResubmitAsync(uiTarget, new OperatorCommandContext(OperatorChannel.WebApp))),
            Task.Run(() => harness.Sut.SkipAsync(agentTarget, new OperatorCommandContext(OperatorChannel.Mcp, "Agent skip", "key-2"))));

        Assert.AreEqual(1, results.Count(r => r.Status == OperatorCommandStatus.Accepted));
        Assert.AreEqual(1, results.Count(r => r.Status == OperatorCommandStatus.Stale));
        Assert.AreEqual(1, harness.Manager.Sends);
        Assert.AreEqual(1, harness.Audit.Required.Count, "Only the winner records the command.");
    }

    [TestMethod]
    public async Task A_role_revoked_since_the_cached_check_is_refused_and_audited()
    {
        var harness = await Harness.CreateAsync(authorization: new Authorization { Cached = true, Fresh = false });
        var target = await harness.FindAsync();

        var result = await harness.Sut.ResubmitAsync(target, new OperatorCommandContext(OperatorChannel.WebApp));

        Assert.AreEqual(OperatorCommandStatus.Forbidden, result.Status);
        Assert.AreEqual(0, harness.Manager.Sends);
        Assert.IsTrue(harness.Audit.Logged.Single().AccessDenied);
        Assert.IsNotNull(await harness.Store.GetEvent(Endpoint, EventId), "A refused command leaves the row alone.");
    }

    [TestMethod]
    public async Task An_unrecordable_audit_refuses_the_command_and_restores_the_row()
    {
        var harness = await Harness.CreateAsync();
        harness.Audit.FailRequired = true;
        var target = await harness.FindAsync();

        var result = await harness.Sut.SkipAsync(target, new OperatorCommandContext(OperatorChannel.Mcp));

        Assert.AreEqual(OperatorCommandStatus.AuditUnavailable, result.Status);
        Assert.AreEqual(0, harness.Manager.Sends);
        var restored = await harness.Store.GetFailedEvent(Endpoint, EventId, Session);
        Assert.IsNotNull(restored);
        Assert.AreEqual(TerminalMessageId, restored.LastMessageId);
    }

    [TestMethod]
    public async Task A_failed_publish_restores_the_row_and_records_CommandNotSent()
    {
        var harness = await Harness.CreateAsync();
        harness.Manager.Fail = true;
        var target = await harness.FindAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            harness.Sut.ResubmitAsync(target, new OperatorCommandContext(OperatorChannel.WebApp)));

        Assert.IsNotNull(await harness.Store.GetFailedEvent(Endpoint, EventId, Session));
        var notSent = harness.Audit.Logged.Single(a => a.Type == MessageAuditType.CommandNotSent);
        StringAssert.Contains(notSent.Data, "Resubmit", StringComparison.Ordinal);

        // The restored row is the same version, so a retry can claim it again.
        harness.Manager.Fail = false;
        var retry = await harness.Sut.ResubmitAsync(await harness.FindAsync(), new OperatorCommandContext(OperatorChannel.WebApp));
        Assert.AreEqual(OperatorCommandStatus.Accepted, retry.Status);
    }

    [TestMethod]
    [DataRow(ResolutionStatus.Completed, OperatorChannel.WebApp)]
    [DataRow(ResolutionStatus.Deferred, OperatorChannel.Mcp)]
    [DataRow(ResolutionStatus.Pending, OperatorChannel.Mcp)]
    public async Task A_state_the_channel_cannot_act_on_is_not_allowed(ResolutionStatus status, OperatorChannel channel)
    {
        var harness = await Harness.CreateAsync(rowStatus: status);
        var target = await harness.FindAsync();

        var result = await harness.Sut.ResubmitAsync(target, new OperatorCommandContext(channel));

        Assert.AreEqual(OperatorCommandStatus.NotAllowed, result.Status);
        Assert.AreEqual(0, harness.Manager.Sends);
        Assert.IsNotNull(await harness.Store.GetEvent(Endpoint, EventId));
    }

    [TestMethod]
    public async Task The_web_UI_keeps_resubmit_on_deferred_rows()
    {
        var harness = await Harness.CreateAsync(rowStatus: ResolutionStatus.Deferred);

        var result = await harness.Sut.ResubmitAsync(await harness.FindAsync(), new OperatorCommandContext(OperatorChannel.WebApp));

        Assert.AreEqual(OperatorCommandStatus.Accepted, result.Status);
        Assert.AreEqual(1, harness.Manager.Sends);
    }

    [TestMethod]
    public async Task Report_is_audited_before_the_marker_is_written()
    {
        var harness = await Harness.CreateAsync();
        harness.Audit.FailRequired = true;

        var refused = await harness.Sut.SetReportedAsync(Endpoint.ToLowerInvariant(), EventId, true, "INC-1", new OperatorCommandContext(OperatorChannel.Mcp, "Ticket raised"));

        Assert.AreEqual(OperatorCommandStatus.AuditUnavailable, refused.Status);
        Assert.AreEqual(0, (await harness.Store.GetEventReports(Endpoint, new[] { EventId })).Count);

        harness.Audit.FailRequired = false;
        var accepted = await harness.Sut.SetReportedAsync(Endpoint.ToLowerInvariant(), EventId, true, "INC-1", new OperatorCommandContext(OperatorChannel.Mcp, "Ticket raised"));

        Assert.AreEqual(OperatorCommandStatus.Accepted, accepted.Status);
        var reports = await harness.Store.GetEventReports(Endpoint, new[] { EventId });
        Assert.IsTrue(reports.TryGetValue(EventId, out var report), "Stored under the catalog's casing.");
        Assert.AreEqual("INC-1", report.TicketId);
        StringAssert.Contains(harness.Audit.Required[harness.Audit.Required.Count - 1].Data, "\"reason\":\"Ticket raised\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Report_rejects_a_malformed_ticket_id()
    {
        var harness = await Harness.CreateAsync();

        var result = await harness.Sut.SetReportedAsync(Endpoint, EventId, true, "not a ticket", new OperatorCommandContext(OperatorChannel.WebApp));

        Assert.AreEqual(OperatorCommandStatus.Invalid, result.Status);
        Assert.AreEqual(0, harness.Audit.Required.Count);
    }

    [TestMethod]
    public void Message_versions_round_trip_and_reject_garbage()
    {
        var version = new OperatorMessageVersion(ResolutionStatus.Failed, Session, TerminalMessageId, 638_000_000_000_000_000);

        Assert.IsTrue(OperatorMessageVersion.TryDecode(version.Encode(), out var decoded));
        Assert.AreEqual(version, decoded);
        Assert.IsFalse(OperatorMessageVersion.TryDecode("not-a-version", out _));
        Assert.IsFalse(OperatorMessageVersion.TryDecode(null, out _));
    }

    private sealed class Harness
    {
        public required InMemoryMessageStore Store { get; init; }
        public required RecordingManagerClient Manager { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required OperatorCommandCoordinator Sut { get; init; }
        public required ConcurrentQueue<string> Timeline { get; init; }

        public static async Task<Harness> CreateAsync(
            string rowLastMessageId = TerminalMessageId,
            ResolutionStatus rowStatus = ResolutionStatus.Failed,
            Authorization? authorization = null)
        {
            var store = new InMemoryMessageStore();
            await store.StoreMessage(new MessageEntity
            {
                EventId = EventId,
                MessageId = TerminalMessageId,
                SessionId = Session,
                MessageType = MessageType.ErrorResponse,
                EnqueuedTimeUtc = DateTime.UtcNow,
                EventTypeId = "Demo.Type",
                From = Endpoint,
                To = "Resolver",
                OriginatingMessageId = "req-1",
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent { EventJson = "{\"v\":1}", EventTypeId = "Demo.Type" },
                },
            });
            await OperatorCommandTestRows.SeedFailedRowAsync(store, Endpoint, EventId, Session, rowLastMessageId, rowStatus);

            var timeline = new ConcurrentQueue<string>();
            var manager = new RecordingManagerClient(timeline);
            var audit = new RecordingAudit(timeline);
            var sut = new OperatorCommandCoordinator(
                store,
                manager,
                authorization ?? new Authorization(),
                audit,
                new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
                new Catalog(),
                NullLogger<OperatorCommandCoordinator>.Instance);
            return new Harness { Store = store, Manager = manager, Audit = audit, Sut = sut, Timeline = timeline };
        }

        public async Task<OperatorCommandTarget> FindAsync()
        {
            var lookup = await Sut.FindByMessageAsync(EventId, TerminalMessageId);
            Assert.AreEqual(OperatorCommandStatus.Accepted, lookup.Status);
            return lookup.Target!;
        }
    }

    private sealed class Catalog : Platform
    {
        public Catalog() => AddEndpoint(new Crm());
    }

    private sealed class Crm : NimBus.Core.Endpoints.Endpoint
    {
    }

    private sealed class RecordingManagerClient(ConcurrentQueue<string> timeline) : IManagerClient
    {
        private int _sends;

        public int Sends => _sends;

        public bool Fail { get; set; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) => Send("Resubmit");

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId) => Send("Skip");

        private async Task Send(string command)
        {
            await Task.Yield();
            if (Fail)
                throw new InvalidOperationException("Service Bus unavailable");
            Interlocked.Increment(ref _sends);
            timeline.Enqueue("send:" + command);
        }
    }

    private sealed record AuditCall(MessageAuditType Type, bool AccessDenied, string? Data);

    private sealed class RecordingAudit(ConcurrentQueue<string> timeline) : IAuditLogService
    {
        public ConcurrentQueue<AuditCall> LoggedCalls { get; } = new();

        public ConcurrentQueue<AuditCall> RequiredCalls { get; } = new();

        public IReadOnlyList<AuditCall> Logged => LoggedCalls.ToList();

        public IReadOnlyList<AuditCall> Required => RequiredCalls.ToList();

        public bool FailRequired { get; set; }

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            LoggedCalls.Enqueue(new AuditCall(type, accessDenied, data));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
        {
            if (FailRequired)
                throw new AuditUnavailableException("store down");
            RequiredCalls.Enqueue(new AuditCall(type, false, data));
            timeline.Enqueue("audit:" + type);
            return Task.CompletedTask;
        }
    }

    private sealed class Authorization : IEndpointAuthorizationService
    {
        public bool Cached { get; init; } = true;

        public bool Fresh { get; init; } = true;

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(Cached);

        public Task<bool> HasRoleFreshAsync(AccessRole required, string endpointId) => Task.FromResult(Fresh);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess { SiteRole = AccessRole.Contributor });

        public string? GetCurrentUserName() => "operator";
    }
}
