#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #284: five Owner-only Operations cleanup actions changed or moved stored data without
/// an audit record. Each now writes one record per completed run and an access-denied record for
/// a non-Owner, like Delete all events and Delete dead-lettered.
/// </summary>
[TestClass]
public sealed class AdminCleanupAuditTests
{
    private const string EndpointId = "endpoint-a";
    private const string UnknownEndpoint = "no-such-endpoint";
    private const string EventId = "event-a";
    private const string SecretConnectionString = "AccountEndpoint=https://secret-host.example/;AccountKey=S3cr3tK3y==";

    // ───────── Success records ─────────

    [TestMethod]
    public async Task Bulk_resubmit_is_audited_as_a_resubmit_with_its_counts()
    {
        var harness = new Harness();
        harness.Admin.Result = new BulkOperationResult { Processed = 5, Succeeded = 4, Failed = 1 };

        var response = await harness.Owner().PostAdminBulkResubmitAsync(EndpointId);

        Assert.AreSame(harness.Admin.Result, ((OkObjectResult)response.Result!).Value);
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.Resubmit, audit.Type);
        Assert.IsFalse(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        AssertData(audit, new JObject
        {
            ["action"] = "bulk-resubmit-failed",
            ["processed"] = 5,
            ["succeeded"] = 4,
            ["failed"] = 1,
        });
    }

    [TestMethod]
    [DataRow("deleted", typeof(OkResult))]
    [DataRow("not-found", typeof(NotFoundResult))]
    [DataRow("blocks-session", typeof(ConflictObjectResult))]
    public async Task Delete_event_is_audited_with_its_outcome(string outcome, Type expectedResult)
    {
        var harness = new Harness();
        harness.Admin.DeleteEventOutcome = outcome;

        var response = await harness.Owner().DeleteAdminEventAsync(EndpointId, EventId);

        Assert.IsInstanceOfType(response, expectedResult);
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.PurgeMessages, audit.Type);
        Assert.IsFalse(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        Assert.AreEqual(EventId, audit.EventId);
        AssertData(audit, new JObject
        {
            ["action"] = "delete-event",
            ["eventId"] = EventId,
            ["outcome"] = outcome,
        });
    }

    [TestMethod]
    public async Task Delete_by_to_is_audited_with_its_filter_and_counts()
    {
        var harness = new Harness();
        harness.Admin.Result = new BulkOperationResult { Processed = 3, Succeeded = 3, Failed = 0 };

        var response = await harness.Owner().PostAdminDeleteByToAsync(new DeleteByToRequest { ToField = "Erp" });

        Assert.AreSame(harness.Admin.Result, ((OkObjectResult)response.Result!).Value);
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.PurgeMessages, audit.Type);
        Assert.IsFalse(audit.AccessDenied);
        Assert.IsNull(audit.EndpointId, "Delete by To is not scoped to an endpoint.");
        AssertData(audit, new JObject
        {
            ["action"] = "delete-by-to",
            ["toField"] = "Erp",
            ["processed"] = 3,
            ["succeeded"] = 3,
            ["failed"] = 0,
        });
    }

    [TestMethod]
    public async Task Delete_by_status_is_audited_with_the_normalized_statuses_and_counts()
    {
        var harness = new Harness();
        harness.Admin.Result = new BulkOperationResult { Processed = 2, Succeeded = 1, Failed = 1 };

        var response = await harness.Owner().PostAdminDeleteByStatusAsync(EndpointId, new DeleteByStatusRequest
        {
            Statuses = new List<AdminDeleteStatus> { AdminDeleteStatus.Failed, AdminDeleteStatus.Pending },
        });

        Assert.AreSame(harness.Admin.Result, ((OkObjectResult)response.Result!).Value);
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.PurgeMessages, audit.Type);
        Assert.IsFalse(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        AssertData(audit, new JObject
        {
            ["action"] = "delete-by-status",
            ["statuses"] = new JArray(harness.Admin.DeletedStatuses!.Cast<object>().ToArray()),
            ["processed"] = 2,
            ["succeeded"] = 1,
            ["failed"] = 1,
        });
    }

    [TestMethod]
    public async Task Copy_is_audited_with_its_window_statuses_and_counts()
    {
        var harness = new Harness();
        harness.Admin.CopyResult = new CopyResult { EventsCopied = 7, MessagesCopied = 9 };
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

        var response = await harness.Owner().PostAdminCopyAsync(EndpointId, CopyRequest(from, to));

        Assert.AreSame(harness.Admin.CopyResult, ((OkObjectResult)response.Result!).Value);
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.CopyEndpointData, audit.Type);
        Assert.IsFalse(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        var data = JObject.Parse(audit.Data!);
        Assert.AreEqual("copy-endpoint-data", (string?)data["action"]);
        Assert.AreEqual(from, ((DateTime?)data["from"])?.ToUniversalTime());
        Assert.AreEqual(to, ((DateTime?)data["to"])?.ToUniversalTime());
        CollectionAssert.AreEqual(new[] { "Failed" }, data["statuses"]!.Select(s => (string?)s).ToArray());
        Assert.AreEqual(7, (int?)data["eventsCopied"]);
        Assert.AreEqual(9, (int?)data["messagesCopied"]);
    }

    // ───────── The copy target's connection string never reaches the audit log ─────────

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Copy_audit_never_contains_the_target_connection_string(bool owner)
    {
        var harness = new Harness();

        await (owner ? harness.Owner() : harness.NonOwner()).PostAdminCopyAsync(EndpointId, CopyRequest(null, null));

        var audit = harness.Audit.Logged.Single();
        var data = audit.Data ?? string.Empty;
        foreach (var fragment in new[] { SecretConnectionString, "secret-host", "S3cr3tK3y", "AccountKey", "AccountEndpoint" })
            Assert.IsFalse(data.Contains(fragment, StringComparison.OrdinalIgnoreCase), $"Audit data leaks '{fragment}': {data}");
        if (data.Length > 0)
            Assert.IsNull(JObject.Parse(data).Property("targetConnectionString", StringComparison.OrdinalIgnoreCase));
    }

    // ───────── Access denied ─────────

    [TestMethod]
    public async Task Bulk_resubmit_by_a_non_owner_is_denied_and_audited()
    {
        var harness = new Harness();

        var response = await harness.NonOwner().PostAdminBulkResubmitAsync(EndpointId);

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        AssertDenied(harness, MessageAuditType.Resubmit, EndpointId);
    }

    [TestMethod]
    public async Task Delete_event_by_a_non_owner_is_denied_and_audited()
    {
        var harness = new Harness();

        var response = await harness.NonOwner().DeleteAdminEventAsync(EndpointId, EventId);

        Assert.IsInstanceOfType<ForbidResult>(response);
        var audit = AssertDenied(harness, MessageAuditType.PurgeMessages, EndpointId);
        Assert.AreEqual(EventId, audit.EventId);
    }

    [TestMethod]
    public async Task Delete_by_to_by_a_non_owner_is_denied_and_audited()
    {
        var harness = new Harness();

        var response = await harness.NonOwner().PostAdminDeleteByToAsync(new DeleteByToRequest { ToField = "Erp" });

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        AssertDenied(harness, MessageAuditType.PurgeMessages, expectedEndpointId: null);
    }

    [TestMethod]
    public async Task Delete_by_status_by_a_non_owner_is_denied_and_audited()
    {
        var harness = new Harness();

        var response = await harness.NonOwner().PostAdminDeleteByStatusAsync(EndpointId, new DeleteByStatusRequest
        {
            Statuses = new List<AdminDeleteStatus> { AdminDeleteStatus.Failed },
        });

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        AssertDenied(harness, MessageAuditType.PurgeMessages, EndpointId);
    }

    [TestMethod]
    public async Task Copy_by_a_non_owner_is_denied_and_audited()
    {
        var harness = new Harness();

        var response = await harness.NonOwner().PostAdminCopyAsync(EndpointId, CopyRequest(null, null));

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        AssertDenied(harness, MessageAuditType.CopyEndpointData, EndpointId);
    }

    // ───────── Requests rejected before the action runs ─────────

    [TestMethod]
    public async Task Unknown_endpoint_writes_no_record()
    {
        var harness = new Harness();
        var sut = harness.Owner();

        Assert.IsInstanceOfType<NotFoundObjectResult>((await sut.PostAdminBulkResubmitAsync(UnknownEndpoint)).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>(await sut.DeleteAdminEventAsync(UnknownEndpoint, EventId));
        Assert.IsInstanceOfType<NotFoundObjectResult>((await sut.PostAdminDeleteByStatusAsync(UnknownEndpoint,
            new DeleteByStatusRequest { Statuses = new List<AdminDeleteStatus> { AdminDeleteStatus.Failed } })).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>((await sut.PostAdminCopyAsync(UnknownEndpoint, CopyRequest(null, null))).Result);

        Assert.AreEqual(0, harness.Audit.Logged.Count);
        Assert.AreEqual(0, harness.Admin.ActionCalls);
    }

    [TestMethod]
    public async Task Delete_by_status_with_invalid_statuses_writes_no_record()
    {
        var harness = new Harness();

        var response = await harness.Owner().PostAdminDeleteByStatusAsync(EndpointId,
            new DeleteByStatusRequest { Statuses = new List<AdminDeleteStatus>() });

        Assert.IsInstanceOfType<BadRequestObjectResult>(response.Result);
        Assert.AreEqual(0, harness.Audit.Logged.Count);
        Assert.AreEqual(0, harness.Admin.ActionCalls);
    }

    // ───────── The new audit type ─────────

    [TestMethod]
    public void CopyEndpointData_is_appended_without_moving_existing_members()
    {
        Assert.AreEqual(14, (int)MessageAuditType.PurgeMessages);
        Assert.AreEqual(39, (int)MessageAuditType.AgentApiKeyRemoved);
        Assert.AreEqual(40, (int)MessageAuditType.CopyEndpointData);
        // Session state changes (Operations → Session state) were appended after it.
        Assert.AreEqual(41, (int)MessageAuditType.ChangeSessionState);
        // Dashboard token changes were appended after that.
        Assert.AreEqual(42, (int)MessageAuditType.DashboardTokenCreated);
        Assert.AreEqual(44, (int)MessageAuditType.DashboardTokenRemoved);
        // Endpoint metadata changes were appended after that.
        Assert.AreEqual(45, (int)MessageAuditType.UpdateEndpointMetadata);
        Assert.AreEqual(MessageAuditType.UpdateEndpointMetadata, Enum.GetValues<MessageAuditType>().Max());
    }

    [TestMethod]
    public void CopyEndpointData_can_be_switched_off_in_audit_settings()
    {
        Assert.IsFalse(AuditSettingsProvider.AlwaysRecorded.Contains(MessageAuditType.CopyEndpointData));
        CollectionAssert.Contains(AuditSettingsProvider.Configurable.ToList(), MessageAuditType.CopyEndpointData);
    }

    [TestMethod]
    public async Task Disabled_CopyEndpointData_skips_a_completed_copy_but_records_a_denied_one()
    {
        var store = new AuditStore();
        var provider = new AuditSettingsProvider(store, NullLogger<AuditSettingsProvider>.Instance);
        await provider.SaveAsync(new[] { MessageAuditType.CopyEndpointData });
        var harness = new Harness(new AuditLogService(NullLogger<AuditLogService>.Instance, store, provider));

        await harness.Owner().PostAdminCopyAsync(EndpointId, CopyRequest(null, null));
        Assert.AreEqual(0, store.Written.Count, "A disabled type writes no record for a completed copy.");

        await harness.NonOwner().PostAdminCopyAsync(EndpointId, CopyRequest(null, null));
        var denied = store.Written.Single();
        Assert.AreEqual(MessageAuditType.CopyEndpointData, denied.AuditType);
        Assert.IsTrue(denied.AccessDenied);
    }

    // ───────── Helpers ─────────

    private static CopyRequest CopyRequest(DateTime? from, DateTime? to) => new()
    {
        TargetConnectionString = SecretConnectionString,
        From = from,
        To = to,
        Statuses = new List<string> { "Failed" },
        BatchSize = 50,
    };

    private static void AssertData(AuditCall audit, JObject expected)
    {
        var actual = JObject.Parse(audit.Data!);
        Assert.IsTrue(JToken.DeepEquals(expected, actual), $"Expected {expected.ToString(Newtonsoft.Json.Formatting.None)} but got {audit.Data}");
    }

    private static AuditCall AssertDenied(Harness harness, MessageAuditType expectedType, string? expectedEndpointId)
    {
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(expectedType, audit.Type);
        Assert.IsTrue(audit.AccessDenied);
        Assert.AreEqual(expectedEndpointId, audit.EndpointId);
        Assert.AreEqual(0, harness.Admin.ActionCalls, "A denied caller must not reach the action.");
        return audit;
    }

    private sealed class Harness
    {
        private readonly IPlatform _platform = new FakePlatform(EndpointId);
        private readonly IAuditLogService _auditService;

        public Harness(IAuditLogService? audit = null) => _auditService = audit ?? Audit;

        public RecordingAdminService Admin { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public AdminImplementation Owner() => Create(new[] { new Claim("groups", "EIP_Management") });

        public AdminImplementation NonOwner() => Create(Array.Empty<Claim>());

        private AdminImplementation Create(Claim[] claims)
        {
            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
                },
            };
            var configuration = new ConfigurationBuilder().Build();
            var authorization = new EndpointAuthorizationService(
                accessor,
                _platform,
                NullLogger<EndpointAuthorizationService>.Instance,
                configuration,
                new AccessControlSnapshotProvider(new InMemoryMessageStore(), NullLogger<AccessControlSnapshotProvider>.Instance));

            return new AdminImplementation(
                accessor,
                Admin,
                subscriptionAdminService: null!,
                _platform,
                configuration,
                _auditService,
                authorization,
                heartbeatService: null!);
        }
    }

    private sealed record AuditCall(MessageAuditType Type, bool AccessDenied, string? EndpointId, string? EventId, string? Data);

    private sealed class RecordingAudit : IAuditLogService
    {
        private readonly ConcurrentQueue<AuditCall> _logged = new();

        public IReadOnlyList<AuditCall> Logged => _logged.ToList();

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            _logged.Enqueue(new AuditCall(type, accessDenied, endpointId, eventId, data));
            return Task.CompletedTask;
        }
    }

    private sealed class AuditStore : InMemoryMessageStore
    {
        public List<MessageAuditEntity> Written { get; } = new();

        public override Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
        {
            Written.Add(auditEntity);
            return base.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId);
        }
    }

    /// <summary>Records the action calls the audited handlers make and returns canned results.</summary>
    private sealed class RecordingAdminService : IAdminService
    {
        public BulkOperationResult Result { get; set; } = new() { Processed = 1, Succeeded = 1, Failed = 0 };

        public CopyResult CopyResult { get; set; } = new() { EventsCopied = 1, MessagesCopied = 1 };

        public string DeleteEventOutcome { get; set; } = "deleted";

        public List<string>? DeletedStatuses { get; private set; }

        public int ActionCalls { get; private set; }

        public Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId) => Act(Result);

        public Task<bool> DeleteEventAsync(string endpointId, string eventId)
        {
            ActionCalls++;
            return DeleteEventOutcome switch
            {
                "deleted" => Task.FromResult(true),
                "not-found" => Task.FromResult(false),
                _ => throw new EventBlocksSessionException(EventId),
            };
        }

        public Task<BulkOperationResult> DeleteMessagesByToAsync(string toField) => Act(Result);

        public Task<BulkOperationResult> DeleteByStatusAsync(string endpointId, List<string> statuses)
        {
            DeletedStatuses = statuses;
            return Act(Result);
        }

        public Task<CopyResult> CopyEndpointDataAsync(string endpointId, string targetConnectionString, DateTime? from, DateTime? to, List<string> statuses, int? batchSize)
            => Act(CopyResult);

        private Task<T> Act<T>(T result)
        {
            ActionCalls++;
            return Task.FromResult(result);
        }

        private static Task<T> Unexpected<T>() => throw new AssertFailedException("Not part of the audited cleanup actions.");

        public Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform) => Unexpected<PlatformConfig>();
        public Task<TopologyAuditResult> AuditTopologyAsync(string endpointName) => Unexpected<TopologyAuditResult>();
        public Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName) => Unexpected<TopologyCleanupResult>();
        public Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId) => Unexpected<BulkResubmitPreview>();
        public Task<int> GetDeadLetteredCountAsync(string endpointId) => Unexpected<int>();
        public Task<DeadLetterQueueCounts?> GetDeadLetterQueueCountsAsync(string endpointId) => Unexpected<DeadLetterQueueCounts?>();
        public Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId) => Unexpected<SessionPurgePreview>();
        public Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId) => Unexpected<SessionPurgeResult>();
        public Task<BulkOperationResult> DeleteAllEventsAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<PurgePreview> PurgeSubscriptionPreviewAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<PurgePreview>();
        public Task<BulkOperationResult> PurgeSubscriptionAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteMessagesByToPreviewAsync(string toField) => Unexpected<int>();
        public Task<int> DeleteByStatusPreviewAsync(string endpointId, List<string> statuses) => Unexpected<int>();
        public Task<int> CountSessionBlockingEventsAsync(string endpointId, List<string> statuses) => Unexpected<int>();
        public Task<int> SkipMessagesPreviewAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<int>();
        public Task<BulkOperationResult> SkipMessagesAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<DeferredReprocessResult> ReprocessDeferredAsync(string endpointId, string sessionId) => Unexpected<DeferredReprocessResult>();
        public Task<StalePendingPreview> PreviewStalePendingAsync(string endpointId, DateTime? enqueuedBefore, int maxRows) => Unexpected<StalePendingPreview>();
        public Task<StalePendingReconcileResult> ReconcileStalePendingAsync(string endpointId, DateTime enqueuedBefore, int? maxRepairs, string auditorName, string? note) => Unexpected<StalePendingReconcileResult>();
    }

    private sealed class FakePlatform : IPlatform
    {
        public FakePlatform(string endpointId) => Endpoints = new[] { new FakeEndpoint(endpointId) };

        public IEnumerable<IEndpoint> Endpoints { get; }
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id) => Id = id;

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }
}
