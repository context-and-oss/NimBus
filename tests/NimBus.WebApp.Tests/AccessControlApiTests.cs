#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using AccessRole = NimBus.WebApp.Services.AccessRole;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 026 phase C: the /api/access-control management surface — grant/revoke
/// round-trips, dedupe semantics, the endpoint piiReader rejection, Owner
/// gating with access-denied audits, and immediate effect via cache
/// invalidation.
/// </summary>
[TestClass]
public class AccessControlApiTests
{
    private const string OwnerEmail = "owner@example.com";
    private const string OtherEmail = "other@example.com";

    private InMemoryMessageStore _store = null!;
    private AccessControlSnapshotProvider _snapshotProvider = null!;
    private RecordingAuditLogService _audit = null!;

    [TestInitialize]
    public void Init()
    {
        _store = new InMemoryMessageStore();
        _snapshotProvider = new AccessControlSnapshotProvider(_store, NullLogger<AccessControlSnapshotProvider>.Instance);
        _audit = new RecordingAuditLogService();
    }

    [TestCleanup]
    public void Cleanup() => _snapshotProvider.Dispose();

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Email, OwnerEmail), new Claim("groups", "EIP_Management") },
        authenticationType: "Test"));

    private static ClaimsPrincipal User(string email = OtherEmail) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Email, email) },
        authenticationType: "Test"));

    private AccessControlImplementation CreateSut(ClaimsPrincipal principal)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var platform = new FakePlatform("Storefront", "Billing");
        var authz = new EndpointAuthorizationService(
            accessor,
            platform,
            NullLogger<EndpointAuthorizationService>.Instance,
            new ConfigurationBuilder().Build(),
            _snapshotProvider);

        return new AccessControlImplementation(_store, authz, _snapshotProvider, _audit, platform, accessor);
    }

    private EndpointAuthorizationService CreateAuthz(ClaimsPrincipal principal)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        return new EndpointAuthorizationService(
            accessor,
            new FakePlatform("Storefront", "Billing"),
            NullLogger<EndpointAuthorizationService>.Instance,
            new ConfigurationBuilder().Build(),
            _snapshotProvider);
    }

    private static RoleEntry Entry(RoleEntryRole role, string entry) => new() { Role = role, Entry = entry };

    private static T Ok<T>(ActionResult<T> result)
    {
        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        return (T)((OkObjectResult)result.Result!).Value!;
    }

    [TestMethod]
    public async Task Grant_then_revoke_round_trips_and_audits()
    {
        var sut = CreateSut(Admin());

        var afterGrant = Ok(await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Reader, OtherEmail)));
        Assert.IsTrue(afterGrant.Readers.Contains(OtherEmail));

        var afterRevoke = Ok(await sut.DeleteAccessControlRoleAsync(Entry(RoleEntryRole.Reader, OtherEmail)));
        Assert.AreEqual(0, afterRevoke.Readers.Count);

        CollectionAssert.AreEqual(
            new[] { MessageAuditType.GrantRole, MessageAuditType.RevokeRole },
            _audit.Entries.Select(e => e.Type).ToArray());
        Assert.IsTrue(_audit.Entries.All(e => !e.AccessDenied));
        StringAssert.Contains(_audit.Entries[0].Data!, OtherEmail, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Grant_dedupes_case_insensitively_and_trims()
    {
        var sut = CreateSut(Admin());

        Ok(await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Owner, "User@Example.com")));
        var second = Ok(await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Owner, "  user@example.COM ")));

        Assert.AreEqual(1, second.Owners.Count);
        Assert.AreEqual("user@example.COM", second.Owners.Single());
    }

    [TestMethod]
    public async Task Grant_replaces_padded_and_case_variant_duplicates()
    {
        await _store.SetSiteAccessControl(new AccessControlList
        {
            Readers = { " User@Example.COM ", "user@example.com" },
        });
        await _store.SetEndpointAccessControl("Storefront", new AccessControlList
        {
            EndpointId = "Storefront",
            Contributors = { " User@Example.COM " },
        });
        var sut = CreateSut(Admin());

        var site = Ok(await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Reader, "user@example.com")));
        var endpoint = Ok(await sut.PostEndpointAccessControlRoleAsync(
            Entry(RoleEntryRole.Contributor, "user@example.com"), "Storefront"));

        Assert.AreEqual(1, site.Readers.Count);
        Assert.AreEqual("user@example.com", site.Readers.Single());
        Assert.AreEqual(1, endpoint.Contributors.Count);
        Assert.AreEqual("user@example.com", endpoint.Contributors.Single());

        var authz = CreateAuthz(User("user@example.com"));
        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Reader));
        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Contributor, "Storefront"));
    }

    [TestMethod]
    public async Task Revoke_removes_case_insensitively()
    {
        var sut = CreateSut(Admin());
        Ok(await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Contributor, "User@Example.com")));

        var after = Ok(await sut.DeleteAccessControlRoleAsync(Entry(RoleEntryRole.Contributor, "USER@EXAMPLE.COM")));

        Assert.AreEqual(0, after.Contributors.Count);
    }

    [TestMethod]
    public async Task Endpoint_grant_rejects_piiReader()
    {
        var sut = CreateSut(Admin());

        var result = await sut.PostEndpointAccessControlRoleAsync(
            Entry(RoleEntryRole.PiiReader, OtherEmail), "Storefront");

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task Unknown_endpoint_returns_404()
    {
        var sut = CreateSut(Admin());

        var result = await sut.GetEndpointAccessControlAsync("Nope");

        Assert.IsInstanceOfType<NotFoundObjectResult>(result.Result);
    }

    [TestMethod]
    public async Task NonOwner_mutation_is_forbidden_and_audited()
    {
        var sut = CreateSut(User());

        var result = await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Owner, OtherEmail));

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.AreEqual(1, _audit.Entries.Count);
        Assert.IsTrue(_audit.Entries[0].AccessDenied);
        Assert.AreEqual(MessageAuditType.GrantRole, _audit.Entries[0].Type);
    }

    [TestMethod]
    public async Task NonOwner_cannot_read_site_lists()
    {
        var sut = CreateSut(User());

        var result = await sut.GetAccessControlAsync();

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
    }

    [TestMethod]
    public async Task Endpoint_owner_can_manage_only_their_endpoint()
    {
        // Site admin grants OtherEmail Owner on Storefront...
        var admin = CreateSut(Admin());
        Ok(await admin.PostEndpointAccessControlRoleAsync(Entry(RoleEntryRole.Owner, OtherEmail), "Storefront"));

        // ...who can then manage Storefront's ACL but not Billing's.
        var endpointOwner = CreateSut(User());
        var ownScope = await endpointOwner.PostEndpointAccessControlRoleAsync(
            Entry(RoleEntryRole.Reader, "reader@example.com"), "Storefront");
        Assert.IsInstanceOfType<OkObjectResult>(ownScope.Result);

        var otherScope = await endpointOwner.PostEndpointAccessControlRoleAsync(
            Entry(RoleEntryRole.Reader, "reader@example.com"), "Billing");
        Assert.IsInstanceOfType<ForbidResult>(otherScope.Result);

        var siteScope = await endpointOwner.PostAccessControlRoleAsync(Entry(RoleEntryRole.Reader, OtherEmail));
        Assert.IsInstanceOfType<ForbidResult>(siteScope.Result);
    }

    [TestMethod]
    public async Task Grant_takes_effect_on_next_request_of_this_instance()
    {
        var before = CreateAuthz(User());
        Assert.IsFalse(await before.HasRoleAsync(AccessRole.Reader));

        var admin = CreateSut(Admin());
        Ok(await admin.PostAccessControlRoleAsync(Entry(RoleEntryRole.Reader, OtherEmail)));

        var after = CreateAuthz(User());
        Assert.IsTrue(await after.HasRoleAsync(AccessRole.Reader));
    }

    [TestMethod]
    public async Task Me_reports_roles_and_manage_capability()
    {
        var admin = CreateSut(Admin());
        Ok(await admin.PostAccessControlRoleAsync(Entry(RoleEntryRole.Contributor, OtherEmail)));
        Ok(await admin.PostEndpointAccessControlRoleAsync(Entry(RoleEntryRole.Owner, OtherEmail), "Billing"));

        var me = Ok(await CreateSut(User()).GetAccessControlMeAsync());

        Assert.AreEqual(CurrentUserAccessInfoSiteRole.Contributor, me.SiteRole);
        Assert.IsFalse(me.CanManageAccessControl);
        Assert.IsFalse(me.IsPiiReader);
        Assert.AreEqual(OtherEmail, me.Email);
        var billing = me.EndpointRoles.Single();
        Assert.AreEqual("Billing", billing.EndpointId);
        Assert.AreEqual(EndpointRoleInfoRole.Owner, billing.Role);

        var adminMe = Ok(await CreateSut(Admin()).GetAccessControlMeAsync());
        Assert.AreEqual(CurrentUserAccessInfoSiteRole.Owner, adminMe.SiteRole);
        Assert.IsTrue(adminMe.CanManageAccessControl);
        Assert.IsFalse(adminMe.IsPiiReader);
    }

    [TestMethod]
    public async Task Empty_entry_is_a_400()
    {
        var sut = CreateSut(Admin());

        var result = await sut.PostAccessControlRoleAsync(Entry(RoleEntryRole.Reader, "   "));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
    }

    // ───────── Test doubles ─────────

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

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;

        public FakePlatform(params string[] endpointIds)
            => _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();

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
