#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Endpoints;
using NimBus.MessageStore;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Endpoint = NimBus.Core.Endpoints.Endpoint;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 032 §5: the gates in front of the reconcile. Site Owner only, a known endpoint only, and a
/// cut-off that is genuinely in the past — a denied or rejected call must not reach the service, and
/// every invocation that does is audited.
/// </summary>
[TestClass]
public sealed class AdminReconcileApiTests
{
    private const string EndpointId = "CurrentEndpoint";

    [TestMethod]
    public async Task Reconcile_requires_owner_and_audits_the_denial()
    {
        var service = new RecordingAdminService();
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: false);

        var response = await sut.PostAdminStalePendingReconcileAsync(EndpointId, Request(DateTime.UtcNow.AddHours(-1)));

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        Assert.AreEqual(0, service.ReconcileCalls);
        var entry = audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.ReconcileStalePending, entry.Type);
        Assert.IsTrue(entry.AccessDenied);
    }

    [TestMethod]
    public async Task Preview_requires_owner_and_is_not_audited()
    {
        var service = new RecordingAdminService();
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: false);

        var response = await sut.PostAdminStalePendingPreviewAsync(EndpointId, Request(DateTime.UtcNow.AddHours(-1)));

        Assert.IsInstanceOfType<ForbidResult>(response.Result);
        Assert.AreEqual(0, service.PreviewCalls);
        Assert.AreEqual(0, audit.Entries.Count, "a read-only preview is not an audited action");
    }

    [TestMethod]
    public async Task An_unknown_endpoint_is_not_found()
    {
        var service = new RecordingAdminService();
        var sut = CreateController(service, new RecordingAuditLogService(), authorized: true);

        var preview = await sut.PostAdminStalePendingPreviewAsync("nope", Request(DateTime.UtcNow.AddHours(-1)));
        var reconcile = await sut.PostAdminStalePendingReconcileAsync("nope", Request(DateTime.UtcNow.AddHours(-1)));

        Assert.IsInstanceOfType<NotFoundObjectResult>(preview.Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>(reconcile.Result);
        Assert.AreEqual(0, service.PreviewCalls);
        Assert.AreEqual(0, service.ReconcileCalls);
    }

    [TestMethod]
    public async Task A_missing_or_too_recent_cut_off_is_rejected_without_writing()
    {
        var service = new RecordingAdminService();
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: true);

        var missing = await sut.PostAdminStalePendingReconcileAsync(EndpointId, Request(null));
        var tooRecent = await sut.PostAdminStalePendingReconcileAsync(EndpointId, Request(DateTime.UtcNow.AddMinutes(-1)));

        Assert.IsInstanceOfType<BadRequestObjectResult>(missing.Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>(tooRecent.Result);
        Assert.AreEqual(0, service.ReconcileCalls);
        Assert.AreEqual(0, audit.Entries.Count, "a rejected request is not a privileged action");
    }

    [TestMethod]
    public async Task A_successful_reconcile_audits_the_request_and_its_counts()
    {
        var service = new RecordingAdminService();
        var audit = new RecordingAuditLogService();
        var sut = CreateController(service, audit, authorized: true);
        var cutoff = DateTime.UtcNow.AddHours(-2);

        var response = await sut.PostAdminStalePendingReconcileAsync(
            EndpointId, Request(cutoff, maxRepairs: 3, note: "incident 42"));

        var result = Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        Assert.IsInstanceOfType<StalePendingReconcileResult>(result.Value);
        Assert.AreEqual(1, service.ReconcileCalls);
        Assert.AreEqual(cutoff, service.LastCutoff);
        Assert.AreEqual(3, service.LastMaxRepairs);
        Assert.AreEqual("incident 42", service.LastNote);
        Assert.IsFalse(string.IsNullOrWhiteSpace(service.LastAuditorName), "the repair audit needs the operator's name");

        var entry = audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.ReconcileStalePending, entry.Type);
        Assert.IsFalse(entry.AccessDenied);
        StringAssert.Contains(entry.Data, "\"Succeeded\":2", StringComparison.Ordinal);
        StringAssert.Contains(entry.Data, "incident 42", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Preview_defaults_maxRows_when_the_caller_names_none()
    {
        var service = new RecordingAdminService();
        var sut = CreateController(service, new RecordingAuditLogService(), authorized: true);

        var response = await sut.PostAdminStalePendingPreviewAsync(EndpointId, Request(DateTime.UtcNow.AddHours(-1)));

        Assert.IsInstanceOfType<OkObjectResult>(response.Result);
        Assert.AreEqual(AdminService.DefaultStalePendingMaxRows, service.LastMaxRows);
    }

    private static StalePendingReconcileRequest Request(DateTime? enqueuedBefore, int? maxRepairs = null, string? note = null) =>
        new() { EnqueuedBefore = enqueuedBefore, MaxRepairs = maxRepairs, Note = note };

    private static AdminImplementation CreateController(
        IAdminService adminService,
        IAuditLogService audit,
        bool authorized) =>
        new(
            new TestHttpContextAccessor(new DefaultHttpContext()),
            adminService,
            subscriptionAdminService: null!,
            new TestPlatform(),
            new ConfigurationBuilder().Build(),
            audit,
            new FixedAuthorizationService(authorized),
            heartbeatService: null!);

    private sealed class RecordingAdminService : IAdminService
    {
        public int PreviewCalls { get; private set; }
        public int ReconcileCalls { get; private set; }
        public int LastMaxRows { get; private set; }
        public DateTime LastCutoff { get; private set; }
        public int? LastMaxRepairs { get; private set; }
        public string? LastAuditorName { get; private set; }
        public string? LastNote { get; private set; }

        public Task<StalePendingPreview> PreviewStalePendingAsync(string endpointId, DateTime? enqueuedBefore, int maxRows)
        {
            PreviewCalls++;
            LastMaxRows = maxRows;
            return Task.FromResult(new StalePendingPreview { EndpointId = endpointId, Rows = new List<StalePendingRow>() });
        }

        public Task<StalePendingReconcileResult> ReconcileStalePendingAsync(
            string endpointId, DateTime enqueuedBefore, int? maxRepairs, string auditorName, string? note)
        {
            ReconcileCalls++;
            LastCutoff = enqueuedBefore;
            LastMaxRepairs = maxRepairs;
            LastAuditorName = auditorName;
            LastNote = note;
            return Task.FromResult(new StalePendingReconcileResult
            {
                Processed = 2,
                Succeeded = 2,
                Errors = new List<string>(),
                RepairedEventIds = new List<string>(),
            });
        }

        private static Task<T> Unexpected<T>() =>
            throw new AssertFailedException("The reconcile routes must not reach any other admin operation.");

        public Task<PlatformConfig> GetPlatformConfigAsync(NimBus.Core.IPlatform platform) => Unexpected<PlatformConfig>();
        public Task<TopologyAuditResult> AuditTopologyAsync(string endpointName) => Unexpected<TopologyAuditResult>();
        public Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName) => Unexpected<TopologyCleanupResult>();
        public Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId) => Unexpected<BulkResubmitPreview>();
        public Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<int> GetDeadLetteredCountAsync(string endpointId) => Unexpected<int>();
        public Task<long?> GetDeadLetterQueueCountAsync(string endpointId) => Unexpected<long?>();
        public Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId) => Unexpected<SessionPurgePreview>();
        public Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId) => Unexpected<SessionPurgeResult>();
        public Task<bool> DeleteEventAsync(string endpointId, string eventId) => Unexpected<bool>();
        public Task<PurgePreview> PurgeSubscriptionPreviewAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<PurgePreview>();
        public Task<BulkOperationResult> PurgeSubscriptionAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteMessagesByToPreviewAsync(string toField) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteMessagesByToAsync(string toField) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteByStatusPreviewAsync(string endpointId, List<string> statuses) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteByStatusAsync(string endpointId, List<string> statuses) => Unexpected<BulkOperationResult>();
        public Task<int> SkipMessagesPreviewAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<int>();
        public Task<BulkOperationResult> SkipMessagesAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<CopyResult> CopyEndpointDataAsync(string endpointId, string targetConnectionString, DateTime? from, DateTime? to, List<string> statuses, int? batchSize) => Unexpected<CopyResult>();
        public Task<DeferredReprocessResult> ReprocessDeferredAsync(string endpointId, string sessionId) => Unexpected<DeferredReprocessResult>();
        public Task<BulkOperationResult> DeleteAllEventsAsync(string endpointId) => Unexpected<BulkOperationResult>();
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
