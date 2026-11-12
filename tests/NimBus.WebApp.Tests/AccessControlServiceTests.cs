#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 026: role resolution from the storage-backed ACLs unioned with
/// claim-based compat grants. Uses the real snapshot provider over the
/// in-memory store, so cache invalidation is exercised end-to-end.
/// </summary>
[TestClass]
public class AccessControlServiceTests
{
    private const string UserEmail = "user@example.com";
    private const string UserOid = "11111111-1111-1111-1111-111111111111";

    private InMemoryMessageStore _store = null!;
    private AccessControlSnapshotProvider _snapshotProvider = null!;

    [TestInitialize]
    public void Init()
    {
        _store = new InMemoryMessageStore();
        _snapshotProvider = new AccessControlSnapshotProvider(_store, NullLogger<AccessControlSnapshotProvider>.Instance);
    }

    [TestCleanup]
    public void Cleanup() => _snapshotProvider.Dispose();

    private EndpointAuthorizationService CreateService(
        ClaimsPrincipal principal,
        IAccessControlSnapshotProvider? snapshotProvider = null,
        Dictionary<string, string?>? config = null,
        string[]? roleAssignmentEndpoints = null,
        ILogger<EndpointAuthorizationService>? logger = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>())
            .Build();
        var platform = new FakePlatform(
            new[] { "Storefront", "Billing", "Warehouse" },
            roleAssignmentEndpoints ?? System.Array.Empty<string>(),
            UserOid);

        return new EndpointAuthorizationService(
            accessor,
            platform,
            logger ?? NullLogger<EndpointAuthorizationService>.Instance,
            configuration,
            snapshotProvider ?? _snapshotProvider);
    }

    private static ClaimsPrincipal Principal(params Claim[] claims)
        => new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

    private static ClaimsPrincipal EmailPrincipal(string email = UserEmail, string? oid = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.Email, email) };
        if (oid != null) claims.Add(new Claim("oid", oid));
        return Principal(claims.ToArray());
    }

    private Task SeedSite(
        string[]? readers = null, string[]? contributors = null, string[]? owners = null, string[]? piiReaders = null)
        => _store.SetSiteAccessControl(new AccessControlList
        {
            Readers = (readers ?? System.Array.Empty<string>()).ToList(),
            Contributors = (contributors ?? System.Array.Empty<string>()).ToList(),
            Owners = (owners ?? System.Array.Empty<string>()).ToList(),
            PiiReaders = (piiReaders ?? System.Array.Empty<string>()).ToList(),
        });

    // ───────── Store-granted roles ─────────

    [TestMethod]
    public async Task SiteReader_grants_reader_everywhere_but_not_contributor()
    {
        await SeedSite(readers: new[] { UserEmail });
        var sut = CreateService(EmailPrincipal());

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Reader));
        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Reader, "Storefront"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Contributor, "Storefront"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task Ladder_higher_role_implies_lower()
    {
        await SeedSite(owners: new[] { UserEmail });
        var sut = CreateService(EmailPrincipal());

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Reader));
        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Contributor));
        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task EndpointGrant_applies_only_to_that_endpoint()
    {
        await _store.SetEndpointAccessControl("Storefront", new AccessControlList
        {
            Contributors = new List<string> { UserEmail },
        });
        var sut = CreateService(EmailPrincipal());

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Contributor, "Storefront"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Contributor, "Billing"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader));
    }

    [TestMethod]
    public async Task EndpointGrant_raises_above_site_role()
    {
        // Deliberate improvement over DIS's site-role short-circuit: a site Reader
        // holding an endpoint Owner grant IS Owner on that endpoint.
        await SeedSite(readers: new[] { UserEmail });
        await _store.SetEndpointAccessControl("Storefront", new AccessControlList
        {
            Owners = new List<string> { UserEmail },
        });
        var sut = CreateService(EmailPrincipal());

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner, "Storefront"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Owner, "Billing"));
    }

    // ───────── Identity matching ─────────

    [TestMethod]
    public async Task Matching_is_case_insensitive_and_trimmed()
    {
        await SeedSite(owners: new[] { "  USER@Example.COM  " });
        var sut = CreateService(EmailPrincipal("User@example.com"));

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task ObjectId_entry_matches_oid_claim()
    {
        await SeedSite(owners: new[] { UserOid });
        var sut = CreateService(Principal(new Claim("oid", UserOid)));

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task Upn_claim_matches_email_entry()
    {
        await SeedSite(contributors: new[] { UserEmail });
        var sut = CreateService(Principal(new Claim(ClaimTypes.Upn, UserEmail.ToUpperInvariant())));

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Contributor));
    }

    [TestMethod]
    public async Task NonEmail_name_claim_does_not_match()
    {
        // A bare display name ("Alvin") must never match an ACL entry — only
        // email-shaped claim values participate in matching.
        await SeedSite(owners: new[] { "Alvin" });
        var sut = CreateService(Principal(new Claim(ClaimTypes.Name, "Alvin")));

        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Owner));
    }

    // ───────── Compat union (bootstrap) ─────────

    [TestMethod]
    public async Task AdminMarkerClaim_grants_site_owner_with_empty_store()
    {
        var sut = CreateService(Principal(
            new Claim(ClaimTypes.Email, UserEmail),
            new Claim("groups", "EIP_Management")));

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner, "Storefront"));
    }

    [TestMethod]
    public async Task StoreGrant_cannot_reduce_compat_owner()
    {
        await SeedSite(readers: new[] { UserEmail });
        var sut = CreateService(Principal(
            new Claim(ClaimTypes.Email, UserEmail),
            new Claim("groups", "EIP_Management")));

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task RoleAssignment_oid_grants_endpoint_owner()
    {
        var sut = CreateService(
            EmailPrincipal(oid: UserOid),
            roleAssignmentEndpoints: new[] { "Billing" });

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner, "Billing"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader, "Storefront"));
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task Bypass_config_grants_site_owner()
    {
        var logger = new BypassLogger();
        var sut = CreateService(
            EmailPrincipal(),
            config: new Dictionary<string, string?> { ["BypassEndpointAuthorization"] = "true" }, logger: logger);

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
        Assert.IsFalse(logger.Levels.Any(l => l >= LogLevel.Warning));
        Assert.IsTrue(logger.Levels.Contains(LogLevel.Debug));
    }

    // ───────── PiiReader ─────────

    [TestMethod]
    public async Task Owner_does_not_imply_pii_reader()
    {
        await SeedSite(owners: new[] { UserEmail });
        var sut = CreateService(EmailPrincipal());

        Assert.IsTrue(await sut.HasRoleAsync(AccessRole.Owner));
        Assert.IsFalse(await sut.CanReadPiiAsync());
    }

    [TestMethod]
    public async Task AdminMarker_does_not_imply_pii_reader()
    {
        var sut = CreateService(Principal(
            new Claim(ClaimTypes.Email, UserEmail),
            new Claim("groups", "EIP_Management")));

        Assert.IsFalse(await sut.CanReadPiiAsync());
    }

    [TestMethod]
    public async Task Stored_pii_reader_grant_is_honored()
    {
        await SeedSite(piiReaders: new[] { UserEmail });
        var sut = CreateService(EmailPrincipal());

        Assert.IsTrue(await sut.CanReadPiiAsync());
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader));
    }

    [TestMethod]
    public async Task Entra_pii_reader_role_claim_is_honored()
    {
        var sut = CreateService(Principal(
            new Claim(ClaimTypes.Email, UserEmail),
            new Claim(ClaimTypes.Role, "PiiReader")));

        Assert.IsTrue(await sut.CanReadPiiAsync());
    }

    // ───────── Cache invalidation + fault behavior ─────────

    [TestMethod]
    public async Task Grant_takes_effect_immediately_after_invalidate()
    {
        var sut = CreateService(EmailPrincipal());
        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader));

        await SeedSite(readers: new[] { UserEmail });
        _snapshotProvider.Invalidate();

        // A NEW scoped service (new request) sees the grant immediately.
        var next = CreateService(EmailPrincipal());
        Assert.IsTrue(await next.HasRoleAsync(AccessRole.Reader));
    }

    [TestMethod]
    public async Task Fresh_check_honors_a_revocation_the_cached_snapshot_still_grants()
    {
        await SeedSite(contributors: new[] { UserEmail });
        var warm = CreateService(EmailPrincipal());
        Assert.IsTrue(await warm.HasRoleAsync(AccessRole.Contributor, "Storefront"));

        // Revoked directly in the store, as another instance would: this instance's cache
        // still grants it until the TTL, which operator commands must not rely on.
        await SeedSite();
        var cached = CreateService(EmailPrincipal());
        Assert.IsTrue(await cached.HasRoleAsync(AccessRole.Contributor, "Storefront"));

        var fresh = CreateService(EmailPrincipal());
        Assert.IsFalse(await fresh.HasRoleFreshAsync(AccessRole.Contributor, "Storefront"));
    }

    [TestMethod]
    public async Task Fresh_check_fails_closed_on_a_store_fault_but_keeps_compat()
    {
        await SeedSite(contributors: new[] { UserEmail });
        var faultingStore = new FaultableStore(_store);
        using var provider = new AccessControlSnapshotProvider(faultingStore, NullLogger<AccessControlSnapshotProvider>.Instance);
        Assert.IsTrue(await CreateService(EmailPrincipal(), snapshotProvider: provider).HasRoleAsync(AccessRole.Contributor, "Storefront"));

        faultingStore.Fail = true;

        Assert.IsFalse(await CreateService(EmailPrincipal(), snapshotProvider: provider)
            .HasRoleFreshAsync(AccessRole.Contributor, "Storefront"));
        var admin = Principal(new Claim("groups", EndpointAuthorizationService.AdminMarkerClaimValue));
        Assert.IsTrue(await CreateService(admin, snapshotProvider: provider)
            .HasRoleFreshAsync(AccessRole.Contributor, "Storefront"));
    }

    [TestMethod]
    public async Task Invalidate_during_in_flight_refresh_does_not_cache_stale_snapshot()
    {
        var store = new PausableAccessControlStore(new AccessControlList
        {
            Readers = new List<string> { UserEmail },
        });
        using var provider = new AccessControlSnapshotProvider(
            store,
            NullLogger<AccessControlSnapshotProvider>.Instance);

        var refresh = provider.GetSnapshotAsync();
        await store.FirstRefreshPaused;

        store.RevokeSiteReaders();
        provider.Invalidate();
        store.ResumeFirstRefresh();

        var refreshedSnapshot = await refresh;
        var snapshot = await provider.GetSnapshotAsync();

        Assert.AreEqual(
            2,
            store.SiteReadCount,
            "The snapshot invalidated during refresh must be reloaded instead of receiving the success TTL.");
        Assert.IsFalse(
            refreshedSnapshot.Site?.Readers.Contains(UserEmail) ?? false,
            "The in-flight caller must not receive the revoked grant.");
        Assert.IsFalse(snapshot.Site?.Readers.Contains(UserEmail) ?? false);
    }

    [TestMethod]
    public async Task Store_fault_after_invalidate_fails_closed_and_keeps_compat()
    {
        await SeedSite(readers: new[] { UserEmail });
        var faultingStore = new FaultableStore(_store);
        using var provider = new AccessControlSnapshotProvider(faultingStore, NullLogger<AccessControlSnapshotProvider>.Instance);

        var warm = CreateService(EmailPrincipal(), snapshotProvider: provider);
        Assert.IsTrue(await warm.HasRoleAsync(AccessRole.Reader));

        faultingStore.Fail = true;
        provider.Invalidate();

        // Explicit invalidation means a mutation may have revoked this grant;
        // a failed reload must not promote the old snapshot to the new generation.
        var invalidated = CreateService(EmailPrincipal(), snapshotProvider: provider);
        Assert.IsFalse(await invalidated.HasRoleAsync(AccessRole.Reader));

        // A fresh provider with a faulting store has no last-known-good: store
        // roles fail closed, but the compat marker claim still grants Owner.
        using var coldProvider = new AccessControlSnapshotProvider(faultingStore, NullLogger<AccessControlSnapshotProvider>.Instance);
        var denied = CreateService(EmailPrincipal(), snapshotProvider: coldProvider);
        Assert.IsFalse(await denied.HasRoleAsync(AccessRole.Reader));

        var compatAdmin = CreateService(
            Principal(new Claim(ClaimTypes.Email, UserEmail), new Claim("groups", "EIP_Management")),
            snapshotProvider: coldProvider);
        Assert.IsTrue(await compatAdmin.HasRoleAsync(AccessRole.Owner));
    }

    [TestMethod]
    public async Task Unauthenticated_principal_has_no_access()
    {
        await SeedSite(owners: new[] { UserEmail });
        var sut = CreateService(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader));
        Assert.IsFalse(await sut.CanReadPiiAsync());
    }

    [TestMethod]
    public async Task Unauthenticated_principal_with_admin_marker_has_no_access()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("groups", EndpointAuthorizationService.AdminMarkerClaimValue) }));
        var sut = CreateService(principal);

        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader));
        Assert.IsFalse(await sut.CanReadPiiAsync());
    }

    [TestMethod]
    public async Task Unauthenticated_principal_with_matching_acl_email_has_no_access()
    {
        await SeedSite(owners: new[] { UserEmail }, piiReaders: new[] { UserEmail });
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Email, UserEmail) }));
        var sut = CreateService(principal);

        Assert.IsFalse(await sut.HasRoleAsync(AccessRole.Reader));
        Assert.IsFalse(await sut.CanReadPiiAsync());
    }

    // ───────── Test doubles ─────────

    private sealed class PausableAccessControlStore : IAccessControlStore
    {
        private readonly TaskCompletionSource<bool> _firstRefreshPaused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _resumeFirstRefresh =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AccessControlList _site;
        private int _endpointReadCount;
        private int _siteReadCount;

        public PausableAccessControlStore(AccessControlList site) => _site = site;

        public Task FirstRefreshPaused => _firstRefreshPaused.Task;

        public int SiteReadCount => Volatile.Read(ref _siteReadCount);

        public Task<AccessControlList?> GetSiteAccessControl()
        {
            Interlocked.Increment(ref _siteReadCount);
            return Task.FromResult<AccessControlList?>(Volatile.Read(ref _site));
        }

        public Task SetSiteAccessControl(AccessControlList accessControl)
        {
            Volatile.Write(ref _site, accessControl);
            return Task.CompletedTask;
        }

        public Task<AccessControlList?> GetEndpointAccessControl(string endpointId)
            => Task.FromResult<AccessControlList?>(null);

        public async Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls()
        {
            if (Interlocked.Increment(ref _endpointReadCount) == 1)
            {
                _firstRefreshPaused.TrySetResult(true);
                await _resumeFirstRefresh.Task;
            }

            return System.Array.Empty<AccessControlList>();
        }

        public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl)
            => Task.CompletedTask;

        public void RevokeSiteReaders() => Volatile.Write(ref _site, new AccessControlList());

        public void ResumeFirstRefresh() => _resumeFirstRefresh.TrySetResult(true);
    }

    private sealed class FaultableStore : IAccessControlStore
    {
        private readonly IAccessControlStore _inner;
        public FaultableStore(IAccessControlStore inner) => _inner = inner;

        public bool Fail { get; set; }

        public Task<AccessControlList?> GetSiteAccessControl()
            => Fail ? throw new System.InvalidOperationException("store down") : _inner.GetSiteAccessControl();

        public Task SetSiteAccessControl(AccessControlList accessControl) => _inner.SetSiteAccessControl(accessControl);

        public Task<AccessControlList?> GetEndpointAccessControl(string endpointId)
            => Fail ? throw new System.InvalidOperationException("store down") : _inner.GetEndpointAccessControl(endpointId);

        public Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls()
            => Fail ? throw new System.InvalidOperationException("store down") : _inner.GetEndpointAccessControls();

        public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl)
            => _inner.SetEndpointAccessControl(endpointId, accessControl);
    }

    private sealed class BypassLogger : ILogger<EndpointAuthorizationService>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;

        public FakePlatform(IEnumerable<string> endpointIds, string[] roleAssignmentEndpoints, string principalId)
        {
            _endpoints = endpointIds
                .Select(id => (IEndpoint)new FakeEndpoint(
                    id,
                    roleAssignmentEndpoints.Contains(id) ? principalId : null))
                .ToList();
        }

        public IEnumerable<IEndpoint> Endpoints => _endpoints;

        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        private readonly string? _assignedPrincipalId;

        public FakeEndpoint(string id, string? assignedPrincipalId)
        {
            Id = id;
            _assignedPrincipalId = assignedPrincipalId;
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => _assignedPrincipalId == null
            ? Enumerable.Empty<IRoleAssignment>()
            : new IRoleAssignment[] { new FakeRoleAssignment(_assignedPrincipalId) };
    }

    private sealed class FakeRoleAssignment : IRoleAssignment
    {
        public FakeRoleAssignment(string principalId) => PrincipalId = principalId;

        public string PrincipalId { get; }
        public string Environment => "dev";
    }
}
