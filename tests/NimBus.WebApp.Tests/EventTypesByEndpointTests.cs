#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Regression pins for <see cref="EventTypeImplementation.GetEventtypesByEndpointIdAsync"/>:
/// detail ordering (consumed first, then produced), namespace grouping with
/// first-occurrence order, duplicate both-direction types appearing on both
/// sides, and the 404 mapping. Written against the original double-mapping
/// implementation so the map-once refactor provably preserves the contract.
/// </summary>
[TestClass]
public sealed class EventTypesByEndpointTests
{
    [TestMethod]
    public async Task Details_list_consumed_types_first_then_produced_in_endpoint_order()
    {
        var consumed = new[] { Et("OrderPlaced", "Ns.Orders"), Et("InvoiceSent", "Ns.Billing") };
        var produced = new[] { Et("OrderShipped", "Ns.Orders") };
        var sut = CreateSut(new FakeEndpoint("ep-1", consumed, produced));

        var response = await GetResponse(sut, "ep-1");

        CollectionAssert.AreEqual(
            new List<string> { "OrderPlaced", "InvoiceSent", "OrderShipped" },
            response.EventTypeDetails.Select(d => d.EventType.Name).ToList(),
            "Details must list consumed types first (in endpoint order), then produced.");
    }

    [TestMethod]
    public async Task Groupings_use_namespaces_in_first_occurrence_order()
    {
        var consumed = new[]
        {
            Et("A1", "Ns.Alpha"),
            Et("B1", "Ns.Beta"),
            Et("A2", "Ns.Alpha"),
        };
        var sut = CreateSut(new FakeEndpoint("ep-1", consumed, Array.Empty<IEventType>()));

        var response = await GetResponse(sut, "ep-1");

        CollectionAssert.AreEqual(
            new List<string> { "Ns.Alpha", "Ns.Beta" },
            response.Consumes.Select(g => g.Namespace).ToList(),
            "GroupBy preserves first-occurrence namespace order.");
        CollectionAssert.AreEqual(
            new List<string> { "A1", "A2" },
            response.Consumes.First(g => g.Namespace == "Ns.Alpha").Events.Select(e => e.Name).ToList());
        Assert.AreEqual(0, response.Produces.Count, "No produced types — Produces must be empty.");
    }

    [TestMethod]
    public async Task Type_consumed_and_produced_appears_in_both_groupings_and_twice_in_details()
    {
        var both = Et("RoundTrip", "Ns.Both");
        var sut = CreateSut(new FakeEndpoint("ep-1", new[] { both }, new[] { both }));

        var response = await GetResponse(sut, "ep-1");

        Assert.AreEqual(2, response.EventTypeDetails.Count,
            "A type both consumed and produced yields one detail entry per direction.");
        Assert.AreEqual("RoundTrip", response.Consumes.Single().Events.Single().Name);
        Assert.AreEqual("RoundTrip", response.Produces.Single().Events.Single().Name);
    }

    [TestMethod]
    public async Task Details_carry_code_repo_link_and_producer_consumer_names()
    {
        var consumed = new[] { Et("OrderPlaced", "Ns.Orders") };
        var sut = CreateSut(
            new FakeEndpoint("ep-1", consumed, Array.Empty<IEventType>()),
            producers: new[] { "ep-producer" },
            consumers: new[] { "ep-1" });

        var response = await GetResponse(sut, "ep-1");

        var detail = response.EventTypeDetails.Single();
        Assert.AreEqual("repo://OrderPlaced/Ns.Orders", detail.CodeRepoLink);
        CollectionAssert.AreEqual(new List<string> { "ep-producer" }, detail.Producers.ToList());
        CollectionAssert.AreEqual(new List<string> { "ep-1" }, detail.Consumers.ToList());
    }

    [TestMethod]
    public async Task Unknown_endpoint_returns_404()
    {
        var sut = CreateSut(new FakeEndpoint("ep-1", Array.Empty<IEventType>(), Array.Empty<IEventType>()));

        var result = await sut.GetEventtypesByEndpointIdAsync("no-such-endpoint");

        Assert.IsInstanceOfType(result.Result, typeof(NotFoundObjectResult));
    }

    private static async Task<Response> GetResponse(EventTypeImplementation sut, string endpointId)
    {
        var result = await sut.GetEventtypesByEndpointIdAsync(endpointId);
        Assert.IsNotNull(result.Value, $"Expected a Response body, got {result.Result?.GetType().Name}.");
        return result.Value;
    }

    private static EventTypeImplementation CreateSut(
        FakeEndpoint endpoint,
        IReadOnlyList<string>? producers = null,
        IReadOnlyList<string>? consumers = null)
    {
        return new EventTypeImplementation(
            new FakePlatform(endpoint, producers ?? Array.Empty<string>(), consumers ?? Array.Empty<string>()),
            new StubCodeRepoService(),
            new FakeEventPayloadGenerator(),
            new AllowAllAuthorizationService());
    }

    private sealed class AllowAllAuthorizationService : NimBus.WebApp.Services.IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(NimBus.WebApp.Services.AccessRole required, string? endpointId = null) => Task.FromResult(true);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);
        public Task<NimBus.WebApp.Services.CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new NimBus.WebApp.Services.CurrentUserAccess { SiteRole = NimBus.WebApp.Services.AccessRole.Owner });
        public string GetCurrentUserName() => "test-user";
    }

    private static IEventType Et(string name, string ns) => new FakeEventType(name, ns);

    private sealed class FakeEventType : IEventType
    {
        public FakeEventType(string name, string ns)
        {
            Name = name;
            Namespace = ns;
        }

        public string Id => Name;
        public string Name { get; }
        public string Description => string.Empty;
        public string Namespace { get; }
        public IEnumerable<IProperty> Properties => Enumerable.Empty<IProperty>();
        public Type GetEventClassType() => typeof(object);
        public IEvent GetEventExample() => null!;
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id, IEnumerable<IEventType> consumed, IEnumerable<IEventType> produced)
        {
            Id = id;
            EventTypesConsumed = consumed.ToList();
            EventTypesProduced = produced.ToList();
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly FakeEndpoint _endpoint;
        private readonly IReadOnlyList<string> _producers;
        private readonly IReadOnlyList<string> _consumers;

        public FakePlatform(FakeEndpoint endpoint, IReadOnlyList<string> producers, IReadOnlyList<string> consumers)
        {
            _endpoint = endpoint;
            _producers = producers;
            _consumers = consumers;
        }

        public IEnumerable<IEndpoint> Endpoints => new IEndpoint[] { _endpoint };

        public IEnumerable<IEventType> EventTypes =>
            _endpoint.EventTypesConsumed.Concat(_endpoint.EventTypesProduced).Distinct();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) =>
            _consumers.Select(id => (IEndpoint)new FakeEndpoint(id, Array.Empty<IEventType>(), Array.Empty<IEventType>()));

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) =>
            _producers.Select(id => (IEndpoint)new FakeEndpoint(id, Array.Empty<IEventType>(), Array.Empty<IEventType>()));
    }

    private sealed class StubCodeRepoService : ICodeRepoService
    {
        public string CodeRepoUrl => string.Empty;
        public string GetSearchUrl(string className, string? namespaceName) => $"repo://{className}/{namespaceName}";
    }
}
