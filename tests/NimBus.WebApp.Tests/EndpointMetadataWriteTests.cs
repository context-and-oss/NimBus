#pragma warning disable CA1707, CA2007
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
using ApiTechnicalContact = NimBus.WebApp.ManagementApi.TechnicalContact;

namespace NimBus.WebApp.Tests;

/// <summary>
/// <c>POST /api/metadata/{endpointId}</c> edits an endpoint's ownership. It writes to the
/// endpoint in the route only, keeps the heartbeat and subscription state stored on the same
/// record, validates lengths against the SQL columns and records an audit row first.
/// </summary>
[TestClass]
public sealed class EndpointMetadataWriteTests
{
    private const string Endpoint = "ep-a";
    private const string Other = "ep-b";

    [TestMethod]
    public async Task The_write_keeps_the_heartbeat_and_subscription_state()
    {
        var store = new InMemoryMessageStore();
        await store.SetEndpointMetadata(new EndpointMetadata
        {
            EndpointId = Endpoint,
            IsHeartbeatEnabled = true,
            EndpointHeartbeatStatus = HeartbeatStatus.On,
            SubscriptionStatus = false,
            EndpointOwner = "Old owner",
        });
        var sut = CreateSut(store, new RecordingAudit());

        var result = await sut.PostMetadataEndpointAsync(Body(Endpoint, "New owner"), Endpoint);

        Assert.IsInstanceOfType<OkObjectResult>(result);
        var stored = await store.GetEndpointMetadata(Endpoint);
        Assert.AreEqual("New owner", stored!.EndpointOwner);
        Assert.IsTrue(stored.IsHeartbeatEnabled);
        Assert.AreEqual(HeartbeatStatus.On, stored.EndpointHeartbeatStatus);
        Assert.IsFalse(stored.SubscriptionStatus);
    }

    [TestMethod]
    public async Task A_body_naming_another_endpoint_is_refused_and_changes_nothing()
    {
        var store = new InMemoryMessageStore();
        await store.SetEndpointMetadata(new EndpointMetadata { EndpointId = Other, EndpointOwner = "B's owner" });
        var sut = CreateSut(store, new RecordingAudit());

        var result = await sut.PostMetadataEndpointAsync(Body(Other, "Someone else"), Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.AreEqual("B's owner", (await store.GetEndpointMetadata(Other))!.EndpointOwner);
        Assert.IsNull(await store.GetEndpointMetadata(Endpoint));
    }

    [TestMethod]
    public async Task A_body_without_an_id_writes_to_the_route_endpoint()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store, new RecordingAudit());

        var result = await sut.PostMetadataEndpointAsync(Body(id: null, "Owner"), Endpoint);

        Assert.IsInstanceOfType<OkObjectResult>(result);
        Assert.AreEqual("Owner", (await store.GetEndpointMetadata(Endpoint))!.EndpointOwner);
    }

    [TestMethod]
    public async Task Missing_technical_contacts_are_stored_as_none()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store, new RecordingAudit());
        var body = Body(Endpoint, "Owner");
        body.TechnicalContacts = null!;

        var result = await sut.PostMetadataEndpointAsync(body, Endpoint);

        Assert.IsInstanceOfType<OkObjectResult>(result);
        Assert.AreEqual(0, (await store.GetEndpointMetadata(Endpoint))!.TechnicalContacts!.Count);
    }

    [TestMethod]
    [DataRow(201, 0, DisplayName = "owner too long")]
    [DataRow(0, 401, DisplayName = "email too long")]
    public async Task Values_longer_than_the_stored_columns_are_refused(int ownerLength, int emailLength)
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store, new RecordingAudit());
        var body = Body(Endpoint, new string('o', ownerLength));
        body.EndpointOwnerEmail = new string('e', emailLength);

        var result = await sut.PostMetadataEndpointAsync(body, Endpoint);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.IsNull(await store.GetEndpointMetadata(Endpoint));
    }

    [TestMethod]
    public async Task The_change_is_audited_with_the_values_before_and_after()
    {
        var store = new InMemoryMessageStore();
        await store.SetEndpointMetadata(new EndpointMetadata { EndpointId = Endpoint, EndpointOwner = "Old owner" });
        var audit = new RecordingAudit();
        var sut = CreateSut(store, audit);

        await sut.PostMetadataEndpointAsync(Body(Endpoint, "New owner"), Endpoint);

        var row = audit.Required.Single();
        Assert.AreEqual(MessageAuditType.UpdateEndpointMetadata, row.Type);
        Assert.AreEqual(Endpoint, row.EndpointId);
        StringAssert.Contains(row.Data, "Old owner");
        StringAssert.Contains(row.Data, "New owner");
    }

    [TestMethod]
    public async Task An_unwritable_audit_refuses_the_change()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store, new RecordingAudit { Fail = true });

        var result = await sut.PostMetadataEndpointAsync(Body(Endpoint, "Owner"), Endpoint);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ((ObjectResult)result).StatusCode);
        Assert.IsNull(await store.GetEndpointMetadata(Endpoint));
    }

    [TestMethod]
    public void Endpoint_metadata_changes_are_always_recorded()
        => Assert.IsTrue(AuditSettingsProvider.AlwaysRecorded.Contains(MessageAuditType.UpdateEndpointMetadata));

    private static Metadata Body(string? id, string owner) => new()
    {
        Id = id!,
        EndpointOwner = owner,
        EndpointOwnerTeam = "Team",
        EndpointOwnerEmail = "owner@example.com",
        TechnicalContacts = [new ApiTechnicalContact { Name = "Contact", Email = "contact@example.com" }],
    };

    private static EndpointImplementation CreateSut(InMemoryMessageStore store, RecordingAudit audit)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment"] = "dev" })
            .Build();

        return new EndpointImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakePlatform(Endpoint, Other),
            configuration,
            store,
            store,
            store,
            serviceBusManagement: null!,
            new OwnerOfRouteEndpointOnly(),
            NullLogger<EndpointImplementation>.Instance,
            audit,
            new StoreResultCache(new MemoryCache(new MemoryCacheOptions())),
            PayloadRedactionTests.NewRedaction());
    }

    private sealed class RecordingAudit : IAuditLogService
    {
        public bool Fail { get; init; }

        public List<(MessageAuditType Type, string? EndpointId, string Data)> Required { get; } = [];

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

        public Task LogRequiredAuditAsync(
            MessageAuditType type,
            HttpContext context,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new AuditUnavailableException();
            Required.Add((type, endpointId, data ?? string.Empty));
            return Task.CompletedTask;
        }
    }

    /// <summary>Owner of the route endpoint <see cref="Endpoint"/> only.</summary>
    private sealed class OwnerOfRouteEndpointOnly : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => Task.FromResult(endpointId == Endpoint);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());

        public string GetCurrentUserName() => "test-user";
    }

    private sealed class FakePlatform(params string[] endpointIds) : IPlatform
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
