#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests;

/// <summary>
/// GH#88: a failed Contributor role check on the privileged event operations
/// must surface as HTTP 403 (<see cref="ForbidResult"/>) — consistent with
/// every other authorization check in the WebApp — instead of throwing
/// <see cref="UnauthorizedAccessException"/> (which the exception handler
/// turned into a 500). Covers resubmit, skip, and deferred reprocess, each of
/// which also writes an access-denied audit row (deferred reprocess since
/// GH#401); the shared handoff-settlement denial path is covered by
/// <see cref="HandoffOperatorImplementationTests"/>.
/// </summary>
[TestClass]
public sealed class EventImplementationAuthorizationDenialTests
{
    private const string EndpointId = "SubscriberEp";
    private const string EventId = "evt-denied-1";
    private const string TerminalMessageId = "term-1";

    [TestMethod]
    public async Task Resubmit_denied_returns_Forbid_and_writes_accessDenied_audit()
    {
        var harness = await CreateHarness();

        var result = await harness.Controller.PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<ForbidResult>(result);
        Assert.AreEqual(0, harness.Manager.ResubmitCount);
        var audit = AssertSingleAudit(await harness.Store.GetMessageAudits(EventId));
        Assert.AreEqual(MessageAuditType.Resubmit, audit.AuditType);
        Assert.IsTrue(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
    }

    [TestMethod]
    public async Task Skip_denied_returns_Forbid_and_writes_accessDenied_audit()
    {
        var harness = await CreateHarness();

        var result = await harness.Controller.PostSkipEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<ForbidResult>(result);
        Assert.AreEqual(0, harness.Manager.SkipCount);
        var audit = AssertSingleAudit(await harness.Store.GetMessageAudits(EventId));
        Assert.AreEqual(MessageAuditType.Skip, audit.AuditType);
        Assert.IsTrue(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
    }

    [TestMethod]
    public async Task ReprocessDeferred_denied_returns_Forbid_and_writes_accessDenied_audit()
    {
        var harness = await CreateHarness();

        // adminService is null! — a NullReferenceException instead of a
        // ForbidResult would itself prove the authorization guard was bypassed.
        var result = await harness.Controller.PostReprocessDeferredAsync(EndpointId, "session-1");

        Assert.IsInstanceOfType<ForbidResult>(result.Result);

        // GH#88 added no audit write here; GH#401 audits the denial as
        // resubmit and skip denials are. The route carries no event id, so the
        // row is filed under the endpoint with the session in its data.
        var audits = await harness.Store.SearchAudits(new AuditFilter(), continuationToken: null, maxItemCount: 50);
        var audit = audits.Audits.Single().Audit;
        Assert.AreEqual(MessageAuditType.ReprocessDeferred, audit.AuditType);
        Assert.IsTrue(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        Assert.AreEqual("operator-mallory", audit.AuditorName);
        StringAssert.Contains(audit.Data, "\"channel\":\"WebApp\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"sessionId\":\"session-1\"", StringComparison.Ordinal);
    }

    private static MessageAuditEntity AssertSingleAudit(IEnumerable<MessageAuditEntity> audits)
    {
        var rows = audits.ToList();
        Assert.AreEqual(1, rows.Count);
        return rows[0];
    }

    private static async Task<Harness> CreateHarness()
    {
        // Terminal ErrorResponse carrying its own event type and payload, so
        // the resubmit/skip paths reach the role check without extra lookups.
        // From = the failing endpoint (not self-originating), so the denial is
        // attributed to SubscriberEp.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = TerminalMessageId,
            SessionId = "sess-1",
            MessageType = MessageType.ErrorResponse,
            EnqueuedTimeUtc = DateTime.Parse("2026-06-01T10:00:05Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            EventTypeId = "Demo.Type",
            From = EndpointId,
            To = "Resolver",
            OriginatingMessageId = "req-1",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = "{\"v\":1}", EventTypeId = "Demo.Type" },
            },
        });

        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("name", "operator-mallory") },
                authenticationType: "Test")),
        };
        var manager = new CountingManagerClient();
        var controller = new EventImplementation(
            applicationInsightsService: null!,
            new FakePlatform(EndpointId),
            manager,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new DenyAllAuthorizationService(),
            adminService: null!,
            serviceBusClient: null!,
            new AuditLogService(NullLogger<AuditLogService>.Instance, store),
            handoffSettlement: null!,
            new StubHttpContextAccessor { HttpContext = context },
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);

        return new Harness(controller, store, manager);
    }

    private sealed record Harness(
        EventImplementation Controller,
        InMemoryMessageStore Store,
        CountingManagerClient Manager);

    // HttpContextAccessor stores its value in AsyncLocal. Assigning it inside
    // the async factory would be unwound when the factory returns, so use a
    // direct test double whose value remains stable for the controller call.
    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class DenyAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(false);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess());

        public string? GetCurrentUserName() => "operator-mallory";
    }

    private sealed class CountingManagerClient : IManagerClient
    {
        public int ResubmitCount { get; private set; }
        public int SkipCount { get; private set; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
        {
            ResubmitCount++;
            return Task.CompletedTask;
        }

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        {
            SkipCount++;
            return Task.CompletedTask;
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
