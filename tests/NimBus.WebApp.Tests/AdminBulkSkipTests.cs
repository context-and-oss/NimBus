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
        Assert.IsTrue(harness.Manager.Endpoints.All(e => e == Endpoint), "Each skip goes to the endpoint the session is blocked on.");
        Assert.AreEqual(2, harness.Audit.Required.Count(type => type == MessageAuditType.Skip), "Each skip is audited like a per-message skip.");
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
/// records each SkipRequest instead of sending it.
/// </summary>
internal sealed class BulkSkipHarness
{
    private readonly string _endpoint;

    private BulkSkipHarness(string endpoint, InMemoryMessageStore store, IPlatform platform)
    {
        _endpoint = endpoint;
        Store = store;
        Manager = new RecordingSkipManagerClient();
        Audit = new RecordingAudit();
        var commands = new OperatorCommandCoordinator(
            store,
            Manager,
            new AllowAllAuthorization(),
            Audit,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            platform,
            NullLogger<OperatorCommandCoordinator>.Instance);
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

    public static BulkSkipHarness Create(string endpoint, InMemoryMessageStore? store = null, IPlatform? platform = null) =>
        new(endpoint, store ?? new InMemoryMessageStore(), platform ?? new SingleEndpointPlatform());

    /// <summary>
    /// Seeds a Failed row and the ErrorResponse it was recorded for, in a session of its own, as
    /// the Resolver leaves them for a message that failed on <c>endpoint</c>.
    /// </summary>
    public async Task SeedFailedAsync(string eventId)
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
        await OperatorCommandTestRows.SeedFailedRowAsync(Store, _endpoint, eventId, sessionId, messageId);
    }

    private sealed class SingleEndpointPlatform : Platform
    {
        public SingleEndpointPlatform() => AddEndpoint(new Crm());
    }

    private sealed class Crm : NimBus.Core.Endpoints.Endpoint
    {
    }

    internal sealed class RecordingSkipManagerClient : IManagerClient
    {
        public ConcurrentQueue<string> Skipped { get; } = new();

        public ConcurrentQueue<string> Endpoints { get; } = new();

        public HashSet<string> FailFor { get; } = new(StringComparer.Ordinal);

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) =>
            throw new AssertFailedException("Bulk skip must not resubmit.");

        public async Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        {
            await Task.Yield();
            if (FailFor.Contains(errorResponse.EventId))
                throw new InvalidOperationException("Service Bus unavailable");
            Skipped.Enqueue(errorResponse.EventId);
            Endpoints.Enqueue(endpoint);
        }
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
