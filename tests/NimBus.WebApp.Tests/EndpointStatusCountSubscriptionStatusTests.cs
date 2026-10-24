#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Threading;
using System.Linq;
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
/// EndpointStatusCount carries SubscriptionStatus, but the receive-enabled flag
/// lives on endpoint metadata rather than on the state-count aggregate the store
/// returns. The count paths therefore have to join it in — when they don't, the
/// field is null on every response and the endpoints list cannot tell a disabled
/// endpoint from an active one, however the toggle was flipped.
/// </summary>
[TestClass]
public sealed class EndpointStatusCountSubscriptionStatusTests
{
    [TestMethod]
    public async Task PostApiEndpointStatusCountAsync_reports_each_endpoints_subscription_status()
    {
        var store = await StoreWithAsync(("ep-active", true), ("ep-disabled", false));
        var sut = CreateSut(store, "ep-active", "ep-disabled");

        var result = await sut.PostApiEndpointStatusCountAsync(["ep-active", "ep-disabled"]);

        var counts = CountsOf(result);
        Assert.AreEqual("active", counts["ep-active"]);
        Assert.AreEqual("disabled", counts["ep-disabled"]);
    }

    [TestMethod]
    public async Task GetEndpointStatusCountAllAsync_reports_each_endpoints_subscription_status()
    {
        var store = await StoreWithAsync(("ep-active", true), ("ep-disabled", false));
        var sut = CreateSut(store, "ep-active", "ep-disabled");

        var result = await sut.GetEndpointStatusCountAllAsync();

        var counts = CountsOf(result);
        Assert.AreEqual("active", counts["ep-active"]);
        Assert.AreEqual("disabled", counts["ep-disabled"]);
    }

    [TestMethod]
    public async Task GetEndpointStatusCountIdAsync_reports_the_endpoints_subscription_status()
    {
        // The row refresh after a toggle reads this single-endpoint route, so it
        // has to carry the status the batch route does or the switch springs back.
        var store = await StoreWithAsync(("ep-disabled", false));
        var sut = CreateSut(store, "ep-disabled");

        var result = await sut.GetEndpointStatusCountIdAsync("ep-disabled");

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200 OK, got {result.Result?.GetType().Name}");
        var count = (EndpointStatusCount)ok.Value!;
        Assert.AreEqual("disabled", count.SubscriptionStatus);
    }

    [TestMethod]
    public async Task An_unknown_subscription_status_is_reported_as_unknown_not_as_missing()
    {
        // No stored flag and no reachable Service Bus to probe. The metadata model
        // cannot tell a failed probe from a genuinely absent subscription — both
        // land as null — so the count says nothing rather than asserting the
        // subscription is missing, which would show a healthy endpoint as
        // Subscription Missing and dead its enable/disable switch.
        var sut = CreateSut(new InMemoryMessageStore(), "ep-unknown");

        var result = await sut.PostApiEndpointStatusCountAsync(["ep-unknown"]);

        Assert.IsNull(CountsOf(result)["ep-unknown"]);
    }

    private static Dictionary<string, string?> CountsOf(
        ActionResult<IEnumerable<EndpointStatusCount>> result)
    {
        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200 OK, got {result.Result?.GetType().Name}");
        return ((IEnumerable<EndpointStatusCount>)ok.Value!)
            .ToDictionary(c => c.EndpointId!, c => (string?)c.SubscriptionStatus);
    }

    private static EndpointImplementation CreateSut(InMemoryMessageStore store, params string[] endpointIds)
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
            topologyRebuilder: null!,
            new AllowAllAuthorizationService(),
            NullLogger<EndpointImplementation>.Instance,
            new NoOpAuditLogService(),
            new StoreResultCache(new MemoryCache(new MemoryCacheOptions())),
            PayloadRedactionTests.NewRedaction());
    }

    // The single-endpoint route audits every read, so it needs a real service
    // rather than the null the other harnesses get away with.
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
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Store seeded with a receive-enabled flag per endpoint.</summary>
    private static async Task<InMemoryMessageStore> StoreWithAsync(params (string EndpointId, bool Enabled)[] endpoints)
    {
        var store = new InMemoryMessageStore();
        foreach (var (endpointId, enabled) in endpoints)
        {
            await store.SetEndpointMetadata(new EndpointMetadata
            {
                EndpointId = endpointId,
                SubscriptionStatus = enabled,
            });
        }

        return store;
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
