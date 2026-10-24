#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.ServiceBus.Provisioning;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// <c>POST /api/endpoint/{endpointName}/purge</c> discards the endpoint subscription's backlog
/// by recreating the subscription. It must come back exactly as provisioning declares it:
/// rebuilt from <see cref="TopologyDescriptor.EndpointSubscription"/>, not from a hand-kept copy
/// of its rules that can drift from what <c>nb topology apply</c> creates.
/// </summary>
[TestClass]
public sealed class EndpointPurgeTests
{
    [TestMethod]
    public async Task Purge_recreates_the_endpoint_subscription_from_the_topology_descriptor()
    {
        var serviceBus = new FakeServiceBusManagement();
        var rebuilder = new FakeTopologyRebuilder();
        var sut = CreateSut(serviceBus, rebuilder, "ep-1");

        await sut.PostEndpointPurgeAsync("ep-1");

        CollectionAssert.AreEqual(new[] { ("ep-1", "ep-1") }, serviceBus.DeletedSubscriptions);
        Assert.HasCount(1, rebuilder.Rebuilt);
        var (topic, rebuilt) = rebuilder.Rebuilt[0];
        var expected = TopologyDescriptor.EndpointSubscription("ep-1");
        Assert.AreEqual("ep-1", topic);
        Assert.AreEqual(expected.Name, rebuilt.Name);
        Assert.AreEqual(expected.RequiresSession, rebuilt.RequiresSession);
        Assert.AreEqual(expected.ForwardTo, rebuilt.ForwardTo);
        Assert.AreEqual(expected.KeepDefaultRule, rebuilt.KeepDefaultRule);
        CollectionAssert.AreEqual(expected.Rules.ToList(), rebuilt.Rules.ToList());
    }

    [TestMethod]
    public async Task Purge_fails_loudly_when_the_subscription_cannot_be_rebuilt()
    {
        var serviceBus = new FakeServiceBusManagement();
        var audit = new RecordingAuditLogService();
        var sut = CreateSut(serviceBus, new FakeTopologyRebuilder { Fail = true }, "ep-1", audit);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.PostEndpointPurgeAsync("ep-1"));

        Assert.IsFalse(audit.Types.Contains(MessageAuditType.PurgeMessages));
    }

    private static EndpointImplementation CreateSut(
        FakeServiceBusManagement serviceBus,
        FakeTopologyRebuilder rebuilder,
        string endpointId,
        RecordingAuditLogService? audit = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment"] = "dev" })
            .Build();
        var store = new InMemoryMessageStore();

        return new EndpointImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakePlatform(endpointId),
            configuration,
            store,
            store,
            store,
            serviceBus,
            rebuilder,
            new OwnerAuthorizationService(),
            NullLogger<EndpointImplementation>.Instance,
            audit ?? new RecordingAuditLogService(),
            new StoreResultCache(new MemoryCache(new MemoryCacheOptions())),
            PayloadRedactionTests.NewRedaction());
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<MessageAuditType> Types { get; } = new();

        public Task LogAuditAsync(
            MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null,
            string? auditorNameOverride = null, CancellationToken cancellationToken = default)
        {
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    private sealed class OwnerAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess { SiteRole = AccessRole.Owner, IsPiiReader = true });

        public string GetCurrentUserName() => "test-user";
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;
        public FakePlatform(params string[] ids) => _endpoints = ids.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEndpoint> Endpoints => _endpoints;
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
