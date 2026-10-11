#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Direct controller coverage for the operator complete/fail handoff routes.
/// The shared settlement service is real so guard and audit ordering are tested
/// together with the route-specific authorization and payload mapping.
/// </summary>
[TestClass]
public sealed class HandoffOperatorImplementationTests
{
    private const string EndpointId = "OrdersEndpoint";
    private const string EventId = "event-1";
    private const string MessageId = "message-route-1";

    [TestMethod]
    public async Task PostHandoffComplete_authorized_pending_event_publishes_exact_coordinates_and_audits()
    {
        var harness = await CreateHarness(allowed: true);

        var result = await harness.Controller.PostHandoffCompleteAsync(
            new CompleteHandoffRequest { Note = "confirmed in ERP" },
            EndpointId,
            EventId,
            MessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual(1, harness.Handoffs.CompleteCount);
        Assert.AreEqual(0, harness.Handoffs.FailCount);
        Assert.AreEqual(EndpointId, harness.Handoffs.EndpointId);
        AssertCoordinates(harness.Handoffs.Coordinates);

        var details = JObject.Parse(harness.Handoffs.CompleteDetails!);
        Assert.AreEqual("confirmed in ERP", (string?)details["note"]);
        Assert.AreEqual("operator-alice", (string?)details["completedBy"]);

        var audit = AssertSingleAudit(await harness.Store.GetMessageAudits(EventId));
        Assert.AreEqual(MessageAuditType.CompleteHandoff, audit.AuditType);
        Assert.AreEqual("confirmed in ERP", audit.Data);
        Assert.AreEqual("operator-alice", audit.AuditorName);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        Assert.IsFalse(audit.AccessDenied);
    }

    [TestMethod]
    public async Task PostHandoffFail_authorized_pending_event_publishes_exact_coordinates_and_audits()
    {
        var harness = await CreateHarness(allowed: true);

        var result = await harness.Controller.PostHandoffFailAsync(
            new FailHandoffRequest { Reason = "external job failed", ErrorType = "PartnerError" },
            EndpointId,
            EventId,
            MessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual(0, harness.Handoffs.CompleteCount);
        Assert.AreEqual(1, harness.Handoffs.FailCount);
        Assert.AreEqual(EndpointId, harness.Handoffs.EndpointId);
        AssertCoordinates(harness.Handoffs.Coordinates);
        Assert.AreEqual("external job failed", harness.Handoffs.ErrorText);
        Assert.AreEqual("PartnerError", harness.Handoffs.ErrorType);

        var audit = AssertSingleAudit(await harness.Store.GetMessageAudits(EventId));
        Assert.AreEqual(MessageAuditType.FailHandoff, audit.AuditType);
        Assert.AreEqual("external job failed", audit.Data);
        Assert.AreEqual("operator-alice", audit.AuditorName);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        Assert.IsFalse(audit.AccessDenied);
    }

    [TestMethod]
    [DataRow(true, DisplayName = "complete")]
    [DataRow(false, DisplayName = "fail")]
    public async Task Handoff_operator_route_denied_audits_and_never_settles(bool complete)
    {
        var harness = await CreateHarness(allowed: false);

        var result = await Invoke(harness.Controller, complete);

        Assert.IsInstanceOfType<ForbidResult>(result);
        Assert.AreEqual(0, harness.Handoffs.CompleteCount + harness.Handoffs.FailCount);
        Assert.AreEqual(AccessRole.Contributor, harness.Authorization.LastRequiredRole);
        Assert.AreEqual(EndpointId, harness.Authorization.LastEndpointId);
        var audit = AssertSingleAudit(await harness.Store.GetMessageAudits(EventId));
        Assert.AreEqual(
            complete ? MessageAuditType.CompleteHandoff : MessageAuditType.FailHandoff,
            audit.AuditType);
        Assert.IsTrue(audit.AccessDenied);
    }

    [TestMethod]
    public async Task PostHandoffComplete_non_pending_event_returns_400_without_settlement_or_audit()
    {
        var harness = await CreateHarness(allowed: true, pendingHandoff: false);

        var result = await harness.Controller.PostHandoffCompleteAsync(
            new CompleteHandoffRequest(), EndpointId, EventId, MessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual(0, harness.Handoffs.CompleteCount);
        Assert.AreEqual(0, (await harness.Store.GetMessageAudits(EventId)).Count());
    }

    [TestMethod]
    public async Task PostHandoffFail_without_reason_returns_400_before_authorization_or_settlement()
    {
        var harness = await CreateHarness(allowed: true);

        var result = await harness.Controller.PostHandoffFailAsync(
            new FailHandoffRequest { Reason = "   " }, EndpointId, EventId, MessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual(0, harness.Authorization.CallCount);
        Assert.AreEqual(0, harness.Handoffs.FailCount);
        Assert.AreEqual(0, (await harness.Store.GetMessageAudits(EventId)).Count());
    }

    [TestMethod]
    [DataRow(true, DisplayName = "complete")]
    [DataRow(false, DisplayName = "fail")]
    public async Task Handoff_publish_failure_propagates_and_does_not_write_success_audit(bool complete)
    {
        var harness = await CreateHarness(allowed: true);
        harness.Handoffs.ExceptionToThrow = new InvalidOperationException("send failed");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Invoke(harness.Controller, complete));

        Assert.AreEqual(1, harness.Handoffs.CompleteCount + harness.Handoffs.FailCount);
        Assert.AreEqual(0, (await harness.Store.GetMessageAudits(EventId)).Count(),
            "A failed publish must not create a successful-settlement audit row.");
    }

    private static Task<IActionResult> Invoke(EventImplementation controller, bool complete) =>
        complete
            ? controller.PostHandoffCompleteAsync(
                new CompleteHandoffRequest { Note = "note" }, EndpointId, EventId, MessageId)
            : controller.PostHandoffFailAsync(
                new FailHandoffRequest { Reason = "reason", ErrorType = "type" }, EndpointId, EventId, MessageId);

    private static void AssertCoordinates(HandoffSettlement? coordinates)
    {
        Assert.IsNotNull(coordinates);
        Assert.AreEqual(EventId, coordinates.EventId);
        Assert.AreEqual("session-1", coordinates.SessionId);
        Assert.AreEqual(MessageId, coordinates.MessageId);
        Assert.AreEqual("orders.created.v1", coordinates.EventTypeId);
        Assert.AreEqual("correlation-1", coordinates.CorrelationId);
        Assert.AreEqual("origin-1", coordinates.OriginatingMessageId);
    }

    private static MessageAuditEntity AssertSingleAudit(IEnumerable<MessageAuditEntity> audits)
    {
        var rows = audits.ToList();
        Assert.AreEqual(1, rows.Count);
        return rows[0];
    }

    private static async Task<Harness> CreateHarness(bool allowed, bool pendingHandoff = true)
    {
        var store = new InMemoryMessageStore();
        if (pendingHandoff)
        {
            await store.UploadPendingMessage(EventId, "session-1", EndpointId, PendingEvent());
        }
        else
        {
            await store.UploadCompletedMessage(EventId, "session-1", EndpointId, PendingEvent());
        }

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("name", "operator-alice") },
                authenticationType: "Test")),
        };
        var contextAccessor = new StubHttpContextAccessor { HttpContext = context };
        var authorization = new StubAuthorizationService(allowed);
        var handoffs = new CapturingHandoffClientFactory();
        var audit = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var settlement = new HandoffSettlementService(
            store,
            audit,
            new HandoffSettlementTracker(TimeProvider.System),
            NullLogger<HandoffSettlementService>.Instance);
        var controller = new EventImplementation(
            applicationInsightsService: null!,
            new FakePlatform(EndpointId),
            managerClient: null!,
            handoffs,
            NullLogger<EventImplementation>.Instance,
            store,
            authorization,
            adminService: null!,
            serviceBusClient: null!,
            audit,
            settlement,
            contextAccessor,
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);

        return new Harness(controller, store, handoffs, authorization);
    }

    private static UnresolvedEvent PendingEvent() => new()
    {
        EventTypeId = "orders.created.v1",
        LastMessageId = "message-original-1",
        CorrelationId = "correlation-1",
        OriginatingMessageId = "origin-1",
        PendingSubStatus = "Handoff",
    };

    private sealed record Harness(
        EventImplementation Controller,
        InMemoryMessageStore Store,
        CapturingHandoffClientFactory Handoffs,
        StubAuthorizationService Authorization);

    // HttpContextAccessor stores its value in AsyncLocal. Assigning it inside this
    // async factory would be unwound when the factory returns, so use a direct test
    // double whose value remains stable for the controller invocation.
    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class StubAuthorizationService : IEndpointAuthorizationService
    {
        private readonly bool _allowed;

        public StubAuthorizationService(bool allowed)
        {
            _allowed = allowed;
        }

        public int CallCount { get; private set; }
        public AccessRole? LastRequiredRole { get; private set; }
        public string? LastEndpointId { get; private set; }

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
        {
            CallCount++;
            LastRequiredRole = required;
            LastEndpointId = endpointId;
            return Task.FromResult(_allowed);
        }

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess());

        public string? GetCurrentUserName() => "operator-alice";
    }

    private sealed class CapturingHandoffClientFactory : IHandoffClientFactory
    {
        public int CompleteCount { get; private set; }
        public int FailCount { get; private set; }
        public string? EndpointId { get; private set; }
        public HandoffSettlement? Coordinates { get; private set; }
        public string? CompleteDetails { get; private set; }
        public string? ErrorText { get; private set; }
        public string? ErrorType { get; private set; }
        public Exception? ExceptionToThrow { get; set; }

        public IHandoffClient ForEndpoint(string endpointId) => new Client(this, endpointId);

        private sealed class Client : IHandoffClient
        {
            private readonly CapturingHandoffClientFactory _owner;
            private readonly string _endpointId;

            public Client(CapturingHandoffClientFactory owner, string endpointId)
            {
                _owner = owner;
                _endpointId = endpointId;
            }

            public Task CompleteAsync(
                HandoffSettlement coords,
                object? result = null,
                CancellationToken cancellationToken = default)
            {
                _owner.EndpointId = _endpointId;
                _owner.Coordinates = coords;
                _owner.CompleteDetails = result as string;
                _owner.CompleteCount++;
                return _owner.ExceptionToThrow is null
                    ? Task.CompletedTask
                    : Task.FromException(_owner.ExceptionToThrow);
            }

            public Task FailAsync(
                HandoffSettlement coords,
                string errorText,
                string? errorType = null,
                CancellationToken cancellationToken = default)
            {
                _owner.EndpointId = _endpointId;
                _owner.Coordinates = coords;
                _owner.ErrorText = errorText;
                _owner.ErrorType = errorType;
                _owner.FailCount++;
                return _owner.ExceptionToThrow is null
                    ? Task.CompletedTask
                    : Task.FromException(_owner.ExceptionToThrow);
            }
        }
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly IEndpoint[] _endpoints;

        public FakePlatform(params string[] endpointIds)
        {
            _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToArray();
        }

        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Array.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Array.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Array.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesConsumed => Array.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesProduced => Array.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }
}
