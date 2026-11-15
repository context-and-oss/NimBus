#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
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
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Tests.Mcp;

namespace NimBus.WebApp.Tests;

/// <summary>
/// GH#401: reprocessing a session's deferred messages from the Web UI writes a required
/// <see cref="MessageAuditType.ReprocessDeferred"/> row before anything is reset or sent, as
/// the same action over MCP does. The denial path is covered by
/// <see cref="EventImplementationAuthorizationDenialTests"/>.
/// </summary>
[TestClass]
public sealed class EventImplementationReprocessDeferredAuditTests
{
    private const string EndpointId = "Crm";
    private const string SessionId = "sess-1";
    private const string Operator = "operator-olivia";

    [TestMethod]
    public async Task Reprocess_writes_one_ReprocessDeferred_row_before_the_session_is_reset()
    {
        var store = new InMemoryMessageStore();
        var auditRowsWhenReset = -1;
        async Task<DeferredReprocessResult> Reprocess(string sessionId)
        {
            auditRowsWhenReset = (await store.SearchAudits(new AuditFilter(), continuationToken: null, maxItemCount: 50)).Audits.Count();
            return new DeferredReprocessResult
            {
                SessionId = sessionId, SessionStateCleared = true, ProcessRequestSent = true, Errors = new List<string>(),
            };
        }
        var admin = InterfaceFake<IAdminService>.Create().On("ReprocessDeferredAsync", args => Reprocess((string)args[1]!));

        // Lower-case route id: the audit row is filed under the platform's casing.
        var result = await CreateController(store, admin.Instance).PostReprocessDeferredAsync("crm", SessionId);

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        Assert.AreEqual(1, admin.Calls.Count);
        Assert.AreEqual(1, auditRowsWhenReset, "The audit row is written before the session is reset.");
        var audit = (await store.SearchAudits(new AuditFilter(), continuationToken: null, maxItemCount: 50)).Audits.Single().Audit;
        Assert.AreEqual(MessageAuditType.ReprocessDeferred, audit.AuditType);
        Assert.IsFalse(audit.AccessDenied);
        Assert.AreEqual(EndpointId, audit.EndpointId);
        Assert.AreEqual(Operator, audit.AuditorName);
        StringAssert.Contains(audit.Data, "\"channel\":\"WebApp\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"sessionId\":\"sess-1\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Reprocess_is_refused_and_resets_nothing_when_the_audit_row_cannot_be_written()
    {
        var store = new UnwritableAuditStore();
        var admin = InterfaceFake<IAdminService>.Create();

        var result = await CreateController(store, admin.Instance).PostReprocessDeferredAsync(EndpointId, SessionId);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ((ObjectResult)result.Result!).StatusCode);
        Assert.AreEqual(0, admin.Calls.Count, "Nothing is reset or sent without the audit row.");
    }

    private static EventImplementation CreateController(InMemoryMessageStore store, IAdminService admin)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("name", Operator) },
                authenticationType: "Test")),
        };
        return new EventImplementation(
            applicationInsightsService: null!,
            new FakePlatform(EndpointId),
            managerClient: null!,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new AllowAllAuthorizationService(),
            admin,
            serviceBusClient: null!,
            new AuditLogService(NullLogger<AuditLogService>.Instance, store),
            handoffSettlement: null!,
            new StubHttpContextAccessor { HttpContext = context },
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);
    }

    private sealed class UnwritableAuditStore : InMemoryMessageStore
    {
        public override Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
            => throw new InvalidOperationException("The audit store is down.");
    }

    private sealed class StubHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess());

        public string? GetCurrentUserName() => Operator;
    }

    private sealed class FakePlatform(params string[] endpointIds) : IPlatform
    {
        private readonly IEndpoint[] _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToArray();

        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Array.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Array.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Array.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
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
