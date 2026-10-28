#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
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
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #287: Operations → Skip messages has to send a SkipRequest for every row it skips. Only
/// the endpoint can clear the session block, so marking the row Skipped in the store alone left
/// the session blocked and its later messages deferred. The bulk skip now goes through the same
/// operator-command path as the per-message Skip.
/// </summary>
[TestClass]
public sealed class AdminBulkSkipTests
{
    private const string Endpoint = "Crm";
    private static readonly string[] BothEvents = { "evt-1", "evt-2" };

    [TestMethod]
    public async Task Bulk_skip_sends_a_skip_request_for_each_matching_row()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedFailedAsync("evt-1");
        await harness.SeedFailedAsync("evt-2");

        var result = await harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Failed" }, before: null);

        Assert.AreEqual(2, result.Succeeded, string.Join("; ", result.Errors ?? new List<string>()));
        Assert.AreEqual(0, result.Failed);
        CollectionAssert.AreEquivalent(BothEvents, harness.Manager.Skipped.ToArray());
        // Each skip is parented to the row's latest message, on the session that message blocked,
        // and goes to the endpoint whose session it is.
        foreach (var call in harness.Manager.Requests)
            Assert.AreEqual(new BulkSkipHarness.SkipCall(call.EventId, "sess-" + call.EventId, "term-" + call.EventId, Endpoint), call);
        Assert.AreEqual(2, harness.Audit.Required.Count(type => type == MessageAuditType.Skip), "Each skip is audited like a per-message skip.");
    }

    // The wire message the endpoint acts on, built by the real ManagerClient.
    // EventRoutingTests.Publish_SameSession_FirstFails_SecondDeferred_SkipFirst_SecondEventuallyProcesses
    // shows that this SkipRequest unblocks the session and replays its deferred message.
    [TestMethod]
    public async Task Bulk_skip_publishes_a_manager_skip_request_on_the_blocked_session()
    {
        var bus = new BulkSkipHarness.RecordingServiceBusClient();
        var harness = BulkSkipHarness.Create(Endpoint, sendThrough: new ManagerClient(bus));
        await harness.SeedFailedAsync("evt-1");

        await harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Failed" }, before: null);

        Assert.AreEqual(Endpoint, bus.LastSenderEntityPath);
        var sent = bus.Sender.SentMessages.Single();
        Assert.AreEqual("sess-evt-1", sent.SessionId);
        Assert.AreEqual(nameof(MessageType.SkipRequest), sent.ApplicationProperties["MessageType"]);
        Assert.AreEqual("evt-1", sent.ApplicationProperties["EventId"]);
        Assert.AreEqual(Endpoint, sent.ApplicationProperties["To"]);
        Assert.AreEqual(CoreConstants.ManagerId, sent.ApplicationProperties["From"]);
        Assert.AreEqual("term-evt-1", sent.ApplicationProperties["ParentMessageId"]);
    }

    // A Deferred event's parked copy replays and runs once its session's block clears, so a skip
    // cannot stop it (#287).
    [TestMethod]
    public async Task Bulk_skip_refuses_deferred_events_and_sends_nothing()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedRowAsync("evt-1", ResolutionStatus.Deferred);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Deferred" }, before: null));

        Assert.AreEqual(0, harness.Manager.Skipped.Count);
        Assert.AreEqual(ResolutionStatus.Deferred, (await harness.Store.GetEvent(Endpoint, "evt-1"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Bulk_skip_reports_a_row_whose_lookup_failed_and_continues()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedFailedAsync("evt-1");
        await harness.SeedFailedAsync("evt-2");
        harness.FailLookupFor.Add("evt-1");

        var result = await harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Failed" }, before: null);

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        StringAssert.StartsWith(result.Errors!.Single(), "evt-1:", StringComparison.Ordinal);
        Assert.AreEqual("evt-2", harness.Manager.Skipped.Single());
    }

    [TestMethod]
    public async Task Bulk_skip_claims_the_row_until_the_endpoint_answers()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedFailedAsync("evt-1");

        await harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Failed" }, before: null);

        // The store must not claim Skipped before the endpoint has cleared the session block:
        // the Resolver records Skipped from the endpoint's SkipResponse.
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, "evt-1"));
    }

    [TestMethod]
    public async Task Bulk_skip_leaves_rows_updated_after_the_cutoff()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedFailedAsync("evt-1");

        var result = await harness.Sut.SkipMessagesAsync(
            Endpoint, new List<string> { "Failed" }, before: DateTime.UtcNow.AddHours(-1));

        Assert.AreEqual(0, result.Processed);
        Assert.AreEqual(0, harness.Manager.Skipped.Count);
        Assert.AreEqual(ResolutionStatus.Failed, (await harness.Store.GetEvent(Endpoint, "evt-1"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Bulk_skip_stops_and_sends_nothing_when_the_audit_log_is_unavailable()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedFailedAsync("evt-1");
        await harness.SeedFailedAsync("evt-2");
        harness.Audit.FailRequired = true;

        var result = await harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Failed" }, before: null);

        Assert.AreEqual(0, result.Succeeded);
        Assert.AreEqual(0, harness.Manager.Skipped.Count);
        Assert.AreEqual(1, result.Errors!.Count, "The run stops at the first refusal rather than failing every row the same way.");
        StringAssert.Contains(result.Errors[0], "audit log is unavailable", StringComparison.Ordinal);
        Assert.AreEqual(ResolutionStatus.Failed, (await harness.Store.GetEvent(Endpoint, "evt-1"))!.ResolutionStatus);
        Assert.AreEqual(ResolutionStatus.Failed, (await harness.Store.GetEvent(Endpoint, "evt-2"))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Bulk_skip_reports_a_row_whose_send_failed_and_continues()
    {
        var harness = BulkSkipHarness.Create(Endpoint);
        await harness.SeedFailedAsync("evt-1");
        await harness.SeedFailedAsync("evt-2");
        harness.Manager.FailFor.Add("evt-1");

        var result = await harness.Sut.SkipMessagesAsync(Endpoint, new List<string> { "Failed" }, before: null);

        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        StringAssert.StartsWith(result.Errors!.Single(), "evt-1:", StringComparison.Ordinal);
        Assert.AreEqual("evt-2", harness.Manager.Skipped.Single());
        Assert.AreEqual(ResolutionStatus.Failed, (await harness.Store.GetEvent(Endpoint, "evt-1"))!.ResolutionStatus,
            "A skip that was never sent releases its claim.");
    }
}

/// <summary>
/// An <see cref="AdminService"/> whose bulk skip runs through a real
/// <see cref="OperatorCommandCoordinator"/> over an in-memory store, with a manager client that
/// records each SkipRequest and, when given one, hands it on to a real client.
/// </summary>
internal sealed class BulkSkipHarness
{
    private readonly string _endpoint;

    private BulkSkipHarness(string endpoint, InMemoryMessageStore store, IPlatform platform, IManagerClient? sendThrough)
    {
        _endpoint = endpoint;
        Store = store;
        Manager = new RecordingSkipManagerClient(sendThrough);
        Audit = new RecordingAudit();
        var coordinator = new OperatorCommandCoordinator(
            store,
            Manager,
            new AllowAllAuthorization(),
            Audit,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            platform,
            NullLogger<OperatorCommandCoordinator>.Instance);
        var commands = new FailingLookupCommands(coordinator, FailLookupFor);
        Sut = new AdminService(
            platform: platform,
            messageStore: store,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: null!,
            managerClient: Manager,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null,
            operatorCommands: commands);
    }

    public InMemoryMessageStore Store { get; }

    public RecordingSkipManagerClient Manager { get; }

    public RecordingAudit Audit { get; }

    public AdminService Sut { get; }

    /// <summary>Events whose row lookup throws, as a store outage would.</summary>
    public HashSet<string> FailLookupFor { get; } = new(StringComparer.Ordinal);

    public static BulkSkipHarness Create(
        string endpoint,
        InMemoryMessageStore? store = null,
        IPlatform? platform = null,
        IManagerClient? sendThrough = null) =>
        new(endpoint, store ?? new InMemoryMessageStore(), platform ?? new SingleEndpointPlatform(), sendThrough);

    public Task SeedFailedAsync(string eventId) => SeedRowAsync(eventId, ResolutionStatus.Failed);

    /// <summary>
    /// Seeds a row and the message it was recorded for, in a session of its own, as the Resolver
    /// leaves them for a message that ended in <paramref name="status"/> on <c>endpoint</c>.
    /// </summary>
    public async Task SeedRowAsync(string eventId, ResolutionStatus status)
    {
        var messageId = "term-" + eventId;
        var sessionId = "sess-" + eventId;
        await Store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            SessionId = sessionId,
            MessageType = MessageType.ErrorResponse,
            EnqueuedTimeUtc = DateTime.UtcNow,
            EventTypeId = "Demo.Type",
            From = _endpoint,
            To = "Resolver",
            OriginatingMessageId = "req-" + eventId,
            MessageContent = new MessageContent(),
        });
        await OperatorCommandTestRows.SeedFailedRowAsync(Store, _endpoint, eventId, sessionId, messageId, status);
    }

    /// <summary>A SkipRequest as the manager client was asked to send it.</summary>
    public sealed record SkipCall(string EventId, string SessionId, string MessageId, string Endpoint);

    private sealed class SingleEndpointPlatform : Platform
    {
        public SingleEndpointPlatform() => AddEndpoint(new Crm());
    }

    private sealed class Crm : NimBus.Core.Endpoints.Endpoint
    {
    }

    internal sealed class RecordingSkipManagerClient(IManagerClient? sendThrough) : IManagerClient
    {
        public ConcurrentQueue<string> Skipped { get; } = new();

        public ConcurrentQueue<SkipCall> Requests { get; } = new();

        public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) =>
            throw new AssertFailedException("Bulk skip must not resubmit.");

        public async Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        {
            await Task.Yield();
            if (FailFor.Contains(errorResponse.EventId))
                throw new InvalidOperationException("Service Bus unavailable");
            if (sendThrough != null)
                await sendThrough.Skip(errorResponse, endpoint, eventTypeId);
            Skipped.Enqueue(errorResponse.EventId);
            Requests.Enqueue(new SkipCall(errorResponse.EventId, errorResponse.SessionId, errorResponse.MessageId, endpoint));
        }
    }

    /// <summary>Delegates to the real commands, except that chosen lookups throw.</summary>
    private sealed class FailingLookupCommands(IOperatorCommands inner, HashSet<string> failFor) : IOperatorCommands
    {
        public Task<OperatorTargetLookup> FindByMessageAsync(string eventId, string messageId) =>
            inner.FindByMessageAsync(eventId, messageId);

        public Task<OperatorTargetLookup> FindCurrentAsync(string endpointId, string eventId) =>
            failFor.Contains(eventId)
                ? throw new InvalidOperationException("Store unavailable")
                : inner.FindCurrentAsync(endpointId, eventId);

        public Task<string> ResolveEventTypeIdAsync(OperatorCommandTarget target) => inner.ResolveEventTypeIdAsync(target);

        public Task<OperatorCommandResult> ResubmitAsync(OperatorCommandTarget target, OperatorCommandContext context) =>
            inner.ResubmitAsync(target, context);

        public Task<OperatorCommandResult> ResubmitWithChangesAsync(OperatorCommandTarget target, string eventTypeId,
            string eventJson, string auditData, OperatorCommandContext context) =>
            inner.ResubmitWithChangesAsync(target, eventTypeId, eventJson, auditData, context);

        public Task<OperatorCommandResult> SkipAsync(OperatorCommandTarget target, OperatorCommandContext context) =>
            inner.SkipAsync(target, context);

        public Task<OperatorCommandResult> SetReportedAsync(string endpointId, string eventId, bool reported,
            string? ticketId, OperatorCommandContext context) =>
            inner.SetReportedAsync(endpointId, eventId, reported, ticketId, context);
    }

    /// <summary>Captures what a real <see cref="ManagerClient"/> sends, and to which entity.</summary>
    internal sealed class RecordingServiceBusClient : ServiceBusClient
    {
        public RecordingServiceBusSender Sender { get; } = new();

        public string? LastSenderEntityPath { get; private set; }

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            LastSenderEntityPath = queueOrTopicName;
            return Sender;
        }
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    internal sealed class RecordingServiceBusSender : ServiceBusSender
    {
        public List<ServiceBusMessage> SentMessages { get; } = new();

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class RecordingAudit : IAuditLogService
    {
        public ConcurrentQueue<MessageAuditType> Required { get; } = new();

        public bool FailRequired { get; set; }

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
        {
            if (FailRequired)
                throw new AuditUnavailableException("store down");
            Required.Enqueue(type);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowAllAuthorization : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> HasRoleFreshAsync(AccessRole required, string endpointId) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess { SiteRole = AccessRole.Owner });

        public string? GetCurrentUserName() => "operator";
    }
}
