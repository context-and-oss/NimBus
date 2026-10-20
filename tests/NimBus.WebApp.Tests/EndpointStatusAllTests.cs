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
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Behavior of <see cref="EndpointImplementation.EndpointstatusAllAsync"/>:
/// per-endpoint state counts are independent storage aggregate queries
/// (~100-500ms each against Cosmos), so they must run concurrently rather
/// than serially, while preserving result order and the 404 mapping for
/// missing endpoint storage.
/// </summary>
[TestClass]
public sealed class EndpointStatusAllTests
{
    [TestMethod]
    public async Task EndpointstatusAllAsync_downloads_counts_concurrently()
    {
        var store = new ConcurrencyTrackingStore();
        var sut = CreateSut(store, "ep-1", "ep-2", "ep-3", "ep-4");

        var result = await sut.EndpointstatusAllAsync();

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200 OK, got {result.Result?.GetType().Name}");
        Assert.IsTrue(store.MaxObservedConcurrency >= 2,
            $"Expected per-endpoint count queries to overlap, but max observed concurrency was {store.MaxObservedConcurrency} (serial execution).");
    }

    [TestMethod]
    public async Task EndpointstatusAllAsync_preserves_endpoint_order()
    {
        var store = new ConcurrencyTrackingStore();
        var sut = CreateSut(store, "ep-1", "ep-2", "ep-3", "ep-4");

        var result = await sut.EndpointstatusAllAsync();

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok);
        var counts = ((IEnumerable<EndpointStatusCount>)ok.Value!).Select(c => c.EndpointId).ToList();
        CollectionAssert.AreEqual(new List<string> { "ep-1", "ep-2", "ep-3", "ep-4" }, counts);
    }

    [TestMethod]
    public async Task EndpointstatusAllAsync_maps_missing_endpoint_storage_to_404()
    {
        var store = new ConcurrencyTrackingStore { ThrowNotFoundFor = "ep-3" };
        var sut = CreateSut(store, "ep-1", "ep-2", "ep-3", "ep-4");

        var result = await sut.EndpointstatusAllAsync();

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundObjectResult));
    }

    private static EndpointImplementation CreateSut(INimBusMessageStore store, params string[] endpointIds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment"] = "dev" })
            .Build();

        return new EndpointImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakePlatform(endpointIds),
            configuration,
            store,
            store,
            store,
            serviceBusManagement: null!,
            new AllowAllAuthorizationService(),
            NullLogger<EndpointImplementation>.Instance,
            auditLogService: null!,
            new StoreResultCache(new MemoryCache(new MemoryCacheOptions())),
            PayloadRedactionTests.NewRedaction());
    }

    /// <summary>
    /// In-memory store whose <see cref="DownloadEndpointStateCount"/> holds each
    /// call open briefly and records how many calls were in flight at once.
    /// </summary>
    private sealed class ConcurrencyTrackingStore : InMemoryMessageStore
    {
        private int _inFlight;
        private int _maxObserved;

        public string? ThrowNotFoundFor { get; init; }

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);

        public override async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
        {
            if (endpointId == ThrowNotFoundFor)
            {
                throw new EndpointNotFoundException(endpointId);
            }

            var inFlight = Interlocked.Increment(ref _inFlight);
            try
            {
                int seen;
                while (inFlight > (seen = Volatile.Read(ref _maxObserved)) &&
                       Interlocked.CompareExchange(ref _maxObserved, inFlight, seen) != seen)
                {
                }

                // Hold the call open long enough for overlapping queries to be observable.
                await Task.Delay(100);
                return await base.DownloadEndpointStateCount(endpointId);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

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

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string GetCurrentUserName() => "test-user";
    }
}
