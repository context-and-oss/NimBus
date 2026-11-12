#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
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

namespace NimBus.WebApp.Tests;

/// <summary>
/// Behavior of <see cref="EventImplementation.PostReportEventAsync"/>: the
/// nullable <c>reported</c> field guards against MVC's System.Text.Json binding
/// silently defaulting an omitted value to <c>false</c> (which would CLEAR the
/// marker on an empty body), and the marker is stored under the platform's
/// canonical endpoint casing regardless of the request's casing.
/// </summary>
[TestClass]
public sealed class EventImplementationReportTests
{
    private const string CanonicalEndpointId = "SubscriberEp";
    private const string EventId = "evt-1";

    [TestMethod]
    public async Task Report_without_reported_field_is_rejected_and_does_not_clear_the_marker()
    {
        var store = new InMemoryMessageStore();
        await store.SetEventReport(CanonicalEndpointId, EventId, isReported: true, reportedBy: "alice", ticketId: "T-1");
        var sut = CreateSut(store);

        // An empty `{}` body binds Reported to null (nullable DTO) — must 400.
        var result = await sut.PostReportEventAsync(new ReportEventRequest(), CanonicalEndpointId, EventId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var reports = await store.GetEventReports(CanonicalEndpointId, new[] { EventId });
        Assert.IsTrue(reports[EventId].IsReported, "a rejected request must not clear the existing marker");
        Assert.AreEqual("T-1", reports[EventId].TicketId);
    }

    [TestMethod]
    public async Task Report_stores_the_marker_under_the_canonical_endpoint_casing()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store);

        // Authorization/existence checks are case-insensitive; the stored
        // partition key must still be the platform's canonical casing.
        var result = await sut.PostReportEventAsync(
            new ReportEventRequest { Reported = true, TicketId = "OPS-9" },
            CanonicalEndpointId.ToLowerInvariant(),
            EventId);

        Assert.IsInstanceOfType<OkResult>(result);
        var reports = await store.GetEventReports(CanonicalEndpointId, new[] { EventId });
        Assert.IsTrue(reports.ContainsKey(EventId), "marker must be found under the canonical endpoint id");
        Assert.AreEqual("OPS-9", reports[EventId].TicketId);
        Assert.AreEqual(CanonicalEndpointId, reports[EventId].EndpointId);
    }

    [TestMethod]
    public async Task Report_rejects_invalid_ticket_ids()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store);

        var result = await sut.PostReportEventAsync(
            new ReportEventRequest { Reported = true, TicketId = "has spaces!" },
            CanonicalEndpointId,
            EventId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var reports = await store.GetEventReports(CanonicalEndpointId, new[] { EventId });
        Assert.AreEqual(0, reports.Count);
    }

    [TestMethod]
    public async Task Report_clear_drops_marker_and_ticket()
    {
        var store = new InMemoryMessageStore();
        await store.SetEventReport(CanonicalEndpointId, EventId, isReported: true, reportedBy: "alice", ticketId: "T-1");
        var sut = CreateSut(store);

        var result = await sut.PostReportEventAsync(
            new ReportEventRequest { Reported = false },
            CanonicalEndpointId,
            EventId);

        Assert.IsInstanceOfType<OkResult>(result);
        var reports = await store.GetEventReports(CanonicalEndpointId, new[] { EventId });
        Assert.IsFalse(reports[EventId].IsReported);
        Assert.IsNull(reports[EventId].TicketId);
    }

    private static EventImplementation CreateSut(InMemoryMessageStore store) =>
        new(
            applicationInsightsService: null!,
            new FakePlatform(new[] { CanonicalEndpointId }),
            managerClient: null!,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new AllowAllAuthorizationService(),
            adminService: null!,
            serviceBusClient: null!,
            new NoOpAuditLogService(),
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;

        public FakePlatform(IEnumerable<string> endpointIds)
        {
            _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        }

        public IEnumerable<IEndpoint> Endpoints => _endpoints;

        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
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
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }

    private sealed class NoOpAuditLogService : IAuditLogService
    {
        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
