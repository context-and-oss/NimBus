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
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The admin API for Operations → Session state and Clean up session state: Site Owner only,
/// every write audited before it happens, and a write that can't be audited refused.
/// </summary>
[TestClass]
public sealed class AdminSessionStateApiTests
{
    private const string Endpoint = "Crm";

    private readonly FakeSessionStateBus _bus = new();
    private readonly InMemoryMessageStore _store = new();
    private readonly RecordingAudit _audit = new();
    private readonly IPlatform _platform = new SingleEndpointPlatform();

    [TestMethod]
    public async Task NonOwner_IsForbidden_AndTheAttemptedChangeIsAudited()
    {
        var api = Create(owner: false);

        Assert.IsInstanceOfType<ForbidResult>((await api.GetAdminSessionStateAsync(Endpoint, "s-1")).Result);
        Assert.IsInstanceOfType<ForbidResult>((await api.PostAdminSessionCleanupPreviewAsync(new SessionCleanupPreviewRequest(), Endpoint)).Result);
        Assert.IsInstanceOfType<ForbidResult>((await api.PostAdminSessionStateAsync(Reset("v"), Endpoint, "s-1")).Result);
        Assert.IsInstanceOfType<ForbidResult>((await api.PostAdminSessionCleanupAsync(new SessionCleanupRequest(), Endpoint)).Result);

        Assert.AreEqual(2, _audit.Logged.Count);
        Assert.IsTrue(_audit.Logged.All(a => a.Type == MessageAuditType.ChangeSessionState && a.AccessDenied));
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task UnknownEndpoint_IsNotFound()
    {
        var api = Create();

        Assert.IsInstanceOfType<NotFoundObjectResult>((await api.GetAdminSessionStateAsync("Nope", "s-1")).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>((await api.PostAdminSessionStateAsync(Reset("v"), "Nope", "s-1")).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>((await api.PostAdminSessionCleanupPreviewAsync(new SessionCleanupPreviewRequest(), "Nope")).Result);
    }

    [TestMethod]
    public async Task SessionIdLongerThanServiceBusAllows_IsABadRequest()
    {
        var result = await Create().GetAdminSessionStateAsync(Endpoint, new string('x', 129));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Lookup_ReturnsTheView()
    {
        _bus.SetState(Endpoint, "s-1", new SessionState { BlockedByEventId = "evt-1" }.ToBinaryData());

        var view = (SessionStateView)((OkObjectResult)(await Create().GetAdminSessionStateAsync(Endpoint, "s-1")).Result!).Value!;

        Assert.AreEqual("evt-1", view.BlockedByEventId);
        Assert.AreEqual(0, _audit.Logged.Count);
    }

    [TestMethod]
    public async Task Change_IsAuditedWithBeforeAndAfter()
    {
        var stored = new SessionState { BlockedByEventId = "evt-1" }.ToBinaryData();
        _bus.SetState(Endpoint, "s-1", stored);

        var result = await Create().PostAdminSessionStateAsync(Reset(SessionStateService.VersionOf(stored)), Endpoint, "s-1");

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var audit = _audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.ChangeSessionState, audit.Type);
        Assert.IsTrue(audit.Required);
        Assert.AreEqual(Endpoint, audit.EndpointId);
        var data = JObject.Parse(audit.Data!);
        Assert.AreEqual("reset", (string?)data["action"]);
        Assert.AreEqual("s-1", (string?)data["sessionId"]);
        StringAssert.Contains((string?)data["before"], "evt-1");
        Assert.AreEqual(JTokenType.Null, data["after"]!.Type);
    }

    [TestMethod]
    public async Task Change_AtAStaleVersion_IsAConflictWithTheCurrentState()
    {
        _bus.SetState(Endpoint, "s-1", new SessionState { BlockedByEventId = "evt-1" }.ToBinaryData());

        var result = await Create().PostAdminSessionStateAsync(Reset("stale"), Endpoint, "s-1");

        var conflict = (ConflictObjectResult)result.Result!;
        Assert.AreEqual("evt-1", ((SessionStateView)conflict.Value!).BlockedByEventId);
        Assert.AreEqual(0, _bus.Writes.Count);
        Assert.AreEqual(0, _audit.Logged.Count);
    }

    [TestMethod]
    public async Task Change_WithInvalidJson_IsABadRequest()
    {
        var result = await Create().PostAdminSessionStateAsync(new SessionStateChangeRequest
        {
            Action = SessionStateChangeRequestAction.Replace,
            ExpectedVersion = SessionStateService.VersionOf(null),
            StateJson = "{\"DeferredCount\":-1}",
        }, Endpoint, "s-1");

        StringAssert.Contains((string)((BadRequestObjectResult)result.Result!).Value!, "DeferredCount");
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task Change_WithoutAnExpectedVersion_IsABadRequest()
    {
        var result = await Create().PostAdminSessionStateAsync(Reset(""), Endpoint, "s-1");

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Change_WhoseAuditCannotBeWritten_IsRefusedAndWritesNothing()
    {
        _audit.FailRequired = true;

        var result = await Create().PostAdminSessionStateAsync(Reset(SessionStateService.VersionOf(null)), Endpoint, "s-1");

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ((ObjectResult)result.Result!).StatusCode);
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task Cleanup_IsAuditedBeforeItRuns_AndRefusedWhenTheAuditFails()
    {
        await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, "evt-1", "s-1", "m", NimBus.MessageStore.ResolutionStatus.Completed);
        _bus.SetState(Endpoint, "s-1", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 }.ToBinaryData());
        var preview = (SessionCleanupPreview)((OkObjectResult)(await Create().PostAdminSessionCleanupPreviewAsync(
            new SessionCleanupPreviewRequest(), Endpoint)).Result!).Value!;
        var request = new SessionCleanupRequest
        {
            Sessions = preview.Sessions.Select(s => new SessionCleanupSelection { SessionId = s.SessionId, StateVersion = s.StateVersion }).ToList(),
        };

        _audit.FailRequired = true;
        var refused = await Create().PostAdminSessionCleanupAsync(request, Endpoint);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ((ObjectResult)refused.Result!).StatusCode);
        Assert.AreEqual(0, _bus.Writes.Count);

        _audit.FailRequired = false;
        var result = (SessionCleanupResult)((OkObjectResult)(await Create().PostAdminSessionCleanupAsync(request, Endpoint)).Result!).Value!;
        Assert.AreEqual(1, result.ResetCount);
        var audit = _audit.Logged.Single();
        Assert.IsTrue(audit.Required);
        var data = JObject.Parse(audit.Data!);
        Assert.AreEqual("session-cleanup", (string?)data["action"]);
        Assert.AreEqual("s-1", (string?)data["sessionIds"]![0]);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public async Task Preview_WithAnOutOfRangeSessionCap_IsABadRequest(int maxSessions)
    {
        var result = await Create().PostAdminSessionCleanupPreviewAsync(new SessionCleanupPreviewRequest { MaxSessions = maxSessions }, Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Cleanup_WithoutSessions_IsABadRequest()
    {
        var result = await Create().PostAdminSessionCleanupAsync(new SessionCleanupRequest { Sessions = new List<SessionCleanupSelection>() }, Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    private static SessionStateChangeRequest Reset(string version) =>
        new() { Action = SessionStateChangeRequestAction.Reset, ExpectedVersion = version };

    private AdminImplementation Create(bool owner = true)
    {
        var claims = owner ? new[] { new Claim("groups", "EIP_Management") } : Array.Empty<Claim>();
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
            adminService: null!,
            subscriptionAdminService: null!,
            _platform,
            configuration,
            _audit,
            authorization,
            heartbeatService: null!,
            sessionStates: new SessionStateService(_bus, _store, NullLogger<SessionStateService>.Instance));
    }

    private sealed class SingleEndpointPlatform : Platform
    {
        public SingleEndpointPlatform() => AddEndpoint(new Crm());
    }

    private sealed class Crm : NimBus.Core.Endpoints.Endpoint
    {
    }

    private sealed record AuditCall(MessageAuditType Type, bool AccessDenied, bool Required, string? EndpointId, string? Data);

    private sealed class RecordingAudit : IAuditLogService
    {
        private readonly ConcurrentQueue<AuditCall> _logged = new();

        public bool FailRequired { get; set; }

        public IReadOnlyList<AuditCall> Logged => _logged.ToList();

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            _logged.Enqueue(new AuditCall(type, accessDenied, Required: false, endpointId, data));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
        {
            if (FailRequired)
                throw new AuditUnavailableException("audit store down");
            _logged.Enqueue(new AuditCall(type, AccessDenied: false, Required: true, endpointId, data));
            return Task.CompletedTask;
        }
    }
}
