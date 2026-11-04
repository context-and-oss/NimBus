#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Shared Monitor acknowledgements: the 4 h expiry and clear-on-recovery policy
/// (<see cref="MonitorAcknowledgementService"/>) and the API's authorization
/// (<see cref="MonitorImplementation"/>).
/// </summary>
[TestClass]
public sealed class MonitorAcknowledgementTests
{
    private const string Endpoint = "Billing";

    // ───────── Policy ─────────

    [TestMethod]
    public async Task Acknowledge_records_the_live_failed_count_including_dead_letters_and_unsupported()
    {
        var harness = new Harness();
        await harness.Fail(Endpoint, "f1");
        await harness.DeadLetter(Endpoint, "d1");
        await harness.Unsupported(Endpoint, "u1");

        var ack = await harness.Service.AcknowledgeAsync(Endpoint, "  ERP outage  ", "alice@example.com");

        // Same total as the status API's failedCount, which the Monitor compares it with.
        Assert.AreEqual(3, ack.FailedCountAtAcknowledgement);
        Assert.AreEqual("ERP outage", ack.Reason);
        Assert.AreEqual("alice@example.com", ack.AcknowledgedBy);
        Assert.AreEqual(harness.Clock.GetUtcNow().UtcDateTime, ack.AcknowledgedAtUtc);
        Assert.AreEqual(ack.AcknowledgedAtUtc + TimeSpan.FromHours(4), ack.ExpiresAtUtc);
        Assert.IsFalse(string.IsNullOrEmpty(ack.AcknowledgementId));
        Assert.AreEqual(ack.AcknowledgementId, (await harness.Store.GetEndpointAcknowledgements()).Single().AcknowledgementId);
    }

    [TestMethod]
    public async Task Acknowledge_rejects_a_reason_longer_than_500_characters()
    {
        var harness = new Harness();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => harness.Service.AcknowledgeAsync(Endpoint, new string('x', 501), null));
    }

    [TestMethod]
    public async Task GetActive_removes_acknowledgements_after_four_hours()
    {
        var harness = new Harness();
        await harness.Fail(Endpoint, "f1");
        await harness.Service.AcknowledgeAsync(Endpoint, null, null);

        harness.Clock.Advance(TimeSpan.FromHours(4) - TimeSpan.FromSeconds(1));
        Assert.AreEqual(1, (await harness.Service.GetActiveAsync()).Count);

        harness.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, (await harness.Service.GetActiveAsync()).Count);
        Assert.AreEqual(0, (await harness.Store.GetEndpointAcknowledgements()).Count, "Expired rows are deleted, not just hidden.");
    }

    [TestMethod]
    public async Task GetActive_removes_the_acknowledgement_once_the_endpoint_recovers()
    {
        // The store stamps count snapshots with the real clock, so place the ack in the past.
        var harness = new Harness(DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.Fail(Endpoint, "f1");
        await harness.Service.AcknowledgeAsync(Endpoint, "known", null);

        await harness.Store.RemoveMessage("f1", "f1", Endpoint);

        Assert.AreEqual(0, (await harness.Service.GetActiveAsync()).Count);
        Assert.AreEqual(0, (await harness.Store.GetEndpointAcknowledgements()).Count,
            "A recovered endpoint's ack is gone for good, so a later failure is not pre-silenced.");
    }

    [TestMethod]
    public async Task GetActive_keeps_the_acknowledgement_while_unsupported_messages_remain()
    {
        var harness = new Harness(DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.Fail(Endpoint, "f1");
        await harness.Unsupported(Endpoint, "u1");
        await harness.Service.AcknowledgeAsync(Endpoint, "known", null);

        await harness.Store.RemoveMessage("f1", "f1", Endpoint);

        Assert.AreEqual(1, (await harness.Service.GetActiveAsync()).Count,
            "An unsupported message is still a failure, so the endpoint hasn't recovered.");
    }

    [TestMethod]
    public async Task GetActive_ignores_a_cached_count_taken_before_the_acknowledgement()
    {
        // Snapshot cached while the endpoint was healthy; the ack comes after it.
        var harness = new Harness(DateTimeOffset.UtcNow.AddMinutes(1));
        await harness.Cache.GetEndpointStateCountAsync(harness.Store, Endpoint);
        await harness.Fail(Endpoint, "f1");
        await harness.Service.AcknowledgeAsync(Endpoint, null, null);

        Assert.AreEqual(1, (await harness.Service.GetActiveAsync()).Count);
    }

    [TestMethod]
    public async Task GetActive_keeps_an_acknowledgement_placed_while_not_failing_until_it_expires()
    {
        var harness = new Harness(DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.Service.AcknowledgeAsync(Endpoint, null, null);

        Assert.AreEqual(1, (await harness.Service.GetActiveAsync()).Count);
    }

    [TestMethod]
    public async Task GetActive_keeps_the_acknowledgement_when_the_endpoint_status_is_unreadable()
    {
        var store = new UnreadableStatusStore();
        var harness = new Harness(DateTimeOffset.UtcNow.AddMinutes(-1), store);
        await store.SetEndpointAcknowledgement(new EndpointAcknowledgement
        {
            EndpointId = Endpoint,
            AcknowledgementId = "a1",
            AcknowledgedAtUtc = harness.Clock.GetUtcNow().UtcDateTime,
            ExpiresAtUtc = harness.Clock.GetUtcNow().UtcDateTime.AddHours(4),
            FailedCountAtAcknowledgement = 3,
        });

        Assert.AreEqual(1, (await harness.Service.GetActiveAsync()).Count);
    }

    // ───────── API ─────────

    [TestMethod]
    public async Task Get_returns_only_acknowledgements_on_readable_platform_endpoints()
    {
        var harness = new Harness();
        await harness.Service.AcknowledgeAsync("Billing", "a", null);
        await harness.Service.AcknowledgeAsync("Orders", "b", null);
        await harness.Service.AcknowledgeAsync("Retired", "c", null);
        var sut = harness.Api(new RoleStub(readable: ["Billing", "Retired"]), "Billing", "Orders");

        var result = await sut.GetMonitorAcknowledgementsAsync();

        var acks = ((IEnumerable<MonitorAcknowledgement>)((OkObjectResult)result.Result!).Value!).ToList();
        CollectionAssert.AreEqual(new[] { "Billing" }, acks.Select(a => a.EndpointId).ToList());
        Assert.AreEqual("a", acks[0].Reason);
    }

    [TestMethod]
    public async Task Put_requires_contributor_on_the_endpoint_and_audits_the_denial()
    {
        var harness = new Harness();
        var sut = harness.Api(new RoleStub(readable: [Endpoint]), Endpoint);

        var result = await sut.PutMonitorAcknowledgementAsync(new MonitorAcknowledgementRequest { Reason = "x" }, Endpoint);

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.AreEqual(0, (await harness.Store.GetEndpointAcknowledgements()).Count);
        var audit = harness.Audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.AcknowledgeEndpoint, audit.Type);
        Assert.IsTrue(audit.AccessDenied);
    }

    [TestMethod]
    public async Task Put_acknowledges_under_the_platform_endpoint_id_and_audits_the_reason()
    {
        var harness = new Harness();
        await harness.Fail(Endpoint, "f1");
        var sut = harness.Api(new RoleStub(contributor: [Endpoint], userName: "alice@example.com"), Endpoint);

        var result = await sut.PutMonitorAcknowledgementAsync(new MonitorAcknowledgementRequest { Reason = "ERP down" }, "billing");

        var ack = (MonitorAcknowledgement)((OkObjectResult)result.Result!).Value!;
        Assert.AreEqual(Endpoint, ack.EndpointId);
        Assert.AreEqual("ERP down", ack.Reason);
        Assert.AreEqual("alice@example.com", ack.AcknowledgedBy);
        Assert.AreEqual(1, ack.FailedCountAtAcknowledgement);
        Assert.AreEqual(Endpoint, (await harness.Store.GetEndpointAcknowledgements()).Single().EndpointId);
        var audit = harness.Audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.AcknowledgeEndpoint, audit.Type);
        Assert.IsFalse(audit.AccessDenied);
        Assert.AreEqual("ERP down", audit.Data);
        Assert.AreEqual(Endpoint, audit.EndpointId);
    }

    [TestMethod]
    public async Task Put_rejects_unknown_endpoints_and_overlong_reasons()
    {
        var harness = new Harness();
        var sut = harness.Api(new RoleStub(contributor: [Endpoint]), Endpoint);

        var unknown = await sut.PutMonitorAcknowledgementAsync(new MonitorAcknowledgementRequest(), "Nope");
        var overlong = await sut.PutMonitorAcknowledgementAsync(
            new MonitorAcknowledgementRequest { Reason = new string('x', 501) }, Endpoint);

        Assert.IsInstanceOfType<NotFoundObjectResult>(unknown.Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>(overlong.Result);
        Assert.AreEqual(0, (await harness.Store.GetEndpointAcknowledgements()).Count);
    }

    [TestMethod]
    public async Task Delete_requires_contributor_and_clears_the_acknowledgement()
    {
        var harness = new Harness();
        await harness.Service.AcknowledgeAsync(Endpoint, null, null);

        var denied = await harness.Api(new RoleStub(readable: [Endpoint]), Endpoint).DeleteMonitorAcknowledgementAsync(Endpoint);
        Assert.IsInstanceOfType<ForbidResult>(denied);
        Assert.AreEqual(1, (await harness.Store.GetEndpointAcknowledgements()).Count);

        var cleared = await harness.Api(new RoleStub(contributor: [Endpoint]), Endpoint).DeleteMonitorAcknowledgementAsync("billing");
        Assert.IsInstanceOfType<NoContentResult>(cleared);
        Assert.AreEqual(0, (await harness.Store.GetEndpointAcknowledgements()).Count);
        Assert.AreEqual(MessageAuditType.ClearEndpointAcknowledgement, harness.Audit.Entries.Last().Type);

        var unknown = await harness.Api(new RoleStub(contributor: [Endpoint]), Endpoint).DeleteMonitorAcknowledgementAsync("Nope");
        Assert.IsInstanceOfType<NotFoundObjectResult>(unknown);
    }

    // ───────── Test doubles ─────────

    private sealed class Harness
    {
        public Harness(DateTimeOffset? start = null, InMemoryMessageStore? store = null)
        {
            Store = store ?? new InMemoryMessageStore();
            Clock = new FakeTimeProvider(start ?? new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero));
            Cache = new StoreResultCache(new MemoryCache(new MemoryCacheOptions()));
            Service = new MonitorAcknowledgementService(
                Store, Store, Cache, NullLogger<MonitorAcknowledgementService>.Instance, Clock);
        }

        public InMemoryMessageStore Store { get; }
        public FakeTimeProvider Clock { get; }
        public StoreResultCache Cache { get; }
        public MonitorAcknowledgementService Service { get; }
        public RecordingAuditLogService Audit { get; } = new();

        public Task Fail(string endpointId, string eventId)
            => Store.UploadFailedMessage(eventId, eventId, endpointId, new UnresolvedEvent { EventId = eventId, SessionId = eventId, EndpointId = endpointId });

        public Task DeadLetter(string endpointId, string eventId)
            => Store.UploadDeadletteredMessage(eventId, eventId, endpointId, new UnresolvedEvent { EventId = eventId, SessionId = eventId, EndpointId = endpointId });

        public Task Unsupported(string endpointId, string eventId)
            => Store.UploadUnsupportedMessage(eventId, eventId, endpointId, new UnresolvedEvent { EventId = eventId, SessionId = eventId, EndpointId = endpointId });

        public MonitorImplementation Api(RoleStub roles, params string[] platformEndpoints)
            => new(
                new FakePlatform(platformEndpoints),
                Service,
                roles,
                Audit,
                new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
    }

    private sealed class UnreadableStatusStore : InMemoryMessageStore
    {
        public override Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
            => throw new EndpointNotFoundException(endpointId);
    }

    private sealed class RoleStub(
        string[]? readable = null,
        string[]? contributor = null,
        string? userName = null) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
        {
            var granted = required switch
            {
                AccessRole.Reader => (readable ?? []).Concat(contributor ?? []),
                AccessRole.Contributor => contributor ?? [],
                _ => [],
            };
            return Task.FromResult(granted.Contains(endpointId, StringComparer.OrdinalIgnoreCase));
        }

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => throw new NotSupportedException();
        public string? GetCurrentUserName() => userName;
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public sealed record Entry(MessageAuditType Type, bool AccessDenied, string? Data, string? EndpointId);

        public List<Entry> Entries { get; } = new();

        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new Entry(type, accessDenied, data, endpointId));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform(IEnumerable<string> endpointIds) : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints { get; } = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEventType> EventTypes => [];
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => [];
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => [];
    }

    private sealed class FakeEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
