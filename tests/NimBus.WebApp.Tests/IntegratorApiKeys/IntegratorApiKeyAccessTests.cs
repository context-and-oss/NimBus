#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegratorApiKeys;

namespace NimBus.WebApp.Tests.IntegratorApiKeys;

/// <summary>
/// Spec 045: a request authenticated by an integrator API key gets exactly the access its record
/// grants — the site role, or that role on the listed endpoints only, and PII when issued — and
/// nothing a claim, an ACL, a compat grant or a development flag would add. Owner is unreachable.
/// </summary>
[TestClass]
public class IntegratorApiKeyAccessTests
{
    private const string KeyId = "abcdefghijkl";

    [TestMethod]
    public async Task A_site_wide_reader_reads_everywhere_and_changes_nothing()
    {
        var authz = Create(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Reader });

        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Reader));
        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Reader, "Billing"));
        Assert.IsFalse(await authz.HasRoleAsync(AccessRole.Contributor, "Billing"));
        Assert.IsFalse(await authz.CanReadPiiAsync());
    }

    [TestMethod]
    public async Task An_endpoint_scoped_contributor_acts_only_on_its_endpoints()
    {
        var authz = Create(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Contributor, Endpoints = ["Billing"] });

        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Contributor, "Billing"));
        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Contributor, "billing"), "Endpoint ids match case-insensitively, as ACLs do.");
        Assert.IsFalse(await authz.HasRoleAsync(AccessRole.Reader, "Warehouse"));
        Assert.IsFalse(await authz.HasRoleAsync(AccessRole.Reader), "No site-wide role for a scoped key.");
    }

    [TestMethod]
    public async Task A_key_is_never_owner_even_with_the_admin_marker_or_the_development_bypass()
    {
        var authz = Create(
            new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Contributor },
            extraClaims: [new Claim("groups", EndpointAuthorizationService.AdminMarkerClaimValue)],
            config: new() { ["BypassEndpointAuthorization"] = "true", ["Authorization:GrantPiiReaderInDevelopment"] = "true" });

        Assert.IsFalse(await authz.HasRoleAsync(AccessRole.Owner));
        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Contributor));
        Assert.IsFalse(await authz.CanReadPiiAsync(), "Only the key's own PII flag grants PII.");
    }

    [TestMethod]
    public async Task A_sign_in_on_the_same_request_does_not_widen_the_key()
    {
        var store = new InMemoryMessageStore();
        var person = new ClaimsIdentity([new Claim(ClaimTypes.Email, "owner@example.com"), new Claim("groups", EndpointAuthorizationService.AdminMarkerClaimValue)], "Cookies");
        var authz = Create(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Reader }, otherIdentity: person, store: store);

        Assert.IsFalse(await authz.HasRoleAsync(AccessRole.Contributor), "The key alone decides.");
        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Reader));
    }

    [TestMethod]
    public async Task The_pii_flag_grants_payload_access()
    {
        var authz = Create(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Reader, CanReadPii = true });

        Assert.IsTrue(await authz.CanReadPiiAsync());
    }

    [TestMethod]
    public async Task A_key_revoked_or_expired_after_authenticating_has_no_access()
    {
        var revoked = Create(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Contributor, RevokedAtUtc = DateTime.UtcNow });
        var expired = Create(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Contributor, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1) });
        var missing = Create(new IntegratorApiKey { KeyId = "zzzzzzzzzzzz", Role = IntegratorApiKeyRole.Contributor });

        Assert.IsFalse(await revoked.HasRoleAsync(AccessRole.Reader));
        Assert.IsFalse(await expired.HasRoleAsync(AccessRole.Reader));
        Assert.IsFalse(await missing.HasRoleAsync(AccessRole.Reader));
    }

    [TestMethod]
    public async Task A_fresh_check_reads_the_store_past_the_cache()
    {
        var keys = new FakeKeys(new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Contributor });
        var authz = Create(keys);

        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Contributor, "Billing"));
        keys.Fresh = new IntegratorApiKeySettings { Keys = { new IntegratorApiKey { KeyId = KeyId, Role = IntegratorApiKeyRole.Contributor, RevokedAtUtc = DateTime.UtcNow } } };

        Assert.IsFalse(await authz.HasRoleFreshAsync(AccessRole.Contributor, "Billing"), "The revocation is seen before a change.");
        Assert.AreEqual(1, keys.FreshReads);
    }

    [TestMethod]
    public async Task A_person_without_a_key_is_resolved_as_before()
    {
        var store = new InMemoryMessageStore();
        await store.SetSiteAccessControl(new AccessControlList { Contributors = ["alice@example.com"] });
        using var snapshots = new AccessControlSnapshotProvider(store, NullLogger<AccessControlSnapshotProvider>.Instance);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "alice@example.com")], "Cookies"));
        var authz = new EndpointAuthorizationService(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } },
            new FakePlatform(),
            NullLogger<EndpointAuthorizationService>.Instance,
            new ConfigurationBuilder().Build(),
            snapshots,
            new FakeKeys());

        Assert.IsTrue(await authz.HasRoleAsync(AccessRole.Contributor, "Billing"));
    }

    private static EndpointAuthorizationService Create(
        IntegratorApiKey key,
        Claim[]? extraClaims = null,
        Dictionary<string, string?>? config = null,
        ClaimsIdentity? otherIdentity = null,
        InMemoryMessageStore? store = null)
        => Create(new FakeKeys(key), extraClaims, config, otherIdentity, store);

    private static EndpointAuthorizationService Create(
        FakeKeys keys,
        Claim[]? extraClaims = null,
        Dictionary<string, string?>? config = null,
        ClaimsIdentity? otherIdentity = null,
        InMemoryMessageStore? store = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, IntegratorApiKeyDefaults.PrincipalName(KeyId)),
            new(ClaimTypes.Name, IntegratorApiKeyDefaults.PrincipalName(KeyId)),
            new(IntegratorApiKeyDefaults.KeyIdClaim, KeyId),
        };
        claims.AddRange(extraClaims ?? []);
        var identities = new List<ClaimsIdentity>();
        if (otherIdentity is not null)
            identities.Add(otherIdentity);
        identities.Add(new ClaimsIdentity(claims, IntegratorApiKeyDefaults.SchemeName));

        var snapshots = new AccessControlSnapshotProvider(store ?? new InMemoryMessageStore(), NullLogger<AccessControlSnapshotProvider>.Instance);
        return new EndpointAuthorizationService(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identities) } },
            new FakePlatform(),
            NullLogger<EndpointAuthorizationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build(),
            snapshots,
            keys);
    }

    private sealed class FakeKeys : IIntegratorApiKeyProvider
    {
        private IntegratorApiKeySettings _cached;

        public FakeKeys(params IntegratorApiKey[] keys)
        {
            _cached = new IntegratorApiKeySettings { Revision = "r1" };
            _cached.Keys.AddRange(keys);
            Fresh = _cached;
        }

        public IntegratorApiKeySettings Fresh { get; set; }

        public int FreshReads { get; private set; }

        public Task<IntegratorApiKeySettings?> GetAsync() => Task.FromResult<IntegratorApiKeySettings?>(_cached);

        public Task<IntegratorApiKeySettings?> GetFreshAsync()
        {
            FreshReads++;
            _cached = Fresh;
            return Task.FromResult<IntegratorApiKeySettings?>(Fresh);
        }

        public void Accept(IntegratorApiKeySettings settings) => _cached = settings;
    }

    private sealed class FakePlatform : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }
}
