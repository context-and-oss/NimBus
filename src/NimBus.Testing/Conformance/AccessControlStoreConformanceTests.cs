#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IAccessControlStore"/> (spec 026).
/// Entries are opaque strings (emails or object ids) and must round-trip verbatim —
/// normalization is the WebApp's concern, never the store's.
/// </summary>
[TestClass]
public abstract class AccessControlStoreConformanceTests
{
    protected abstract IAccessControlStore CreateStore();

    private static AccessControlList Sample(string? endpointId = null) => new AccessControlList
    {
        Id = endpointId == null ? AccessControlList.SiteId : AccessControlList.IdForEndpoint(endpointId),
        EndpointId = endpointId,
        Readers = new List<string> { "Reader@Example.COM", "102ce428-e204-4048-9f22-9be33f2867ac" },
        Contributors = new List<string> { " padded@example.com " },
        Owners = new List<string> { "owner@example.com" },
        PiiReaders = new List<string> { "pii@example.com" },
        Labels = new Dictionary<string, string> { ["102ce428-e204-4048-9f22-9be33f2867ac"] = "Nightly export job" },
        UpdatedAtUtc = new DateTime(2026, 07, 27, 0, 0, 0, DateTimeKind.Utc),
    };

    [TestMethod]
    public async Task GetSiteAccessControl_missing_returns_null_then_round_trips_verbatim()
    {
        var store = CreateStore();
        // Site doc is a singleton, so "missing" can only be asserted before any set in
        // this store's scope; providers with shared state may already have one.
        var acl = Sample();
        await store.SetSiteAccessControl(acl);
        var got = await store.GetSiteAccessControl();
        Assert.IsNotNull(got);
        Assert.AreEqual(AccessControlList.SiteId, got.Id);
        Assert.IsNull(got.EndpointId);
        // Verbatim: casing and padding preserved; mixed email + oid entries preserved.
        CollectionAssert.AreEqual(acl.Readers, got.Readers.ToList());
        CollectionAssert.AreEqual(acl.Contributors, got.Contributors.ToList());
        CollectionAssert.AreEqual(acl.Owners, got.Owners.ToList());
        CollectionAssert.AreEqual(acl.PiiReaders, got.PiiReaders.ToList());
        Assert.AreEqual(1, got.Labels.Count);
        Assert.AreEqual("Nightly export job", got.Labels["102ce428-e204-4048-9f22-9be33f2867ac"]);
    }

    [TestMethod]
    public async Task GetEndpointAccessControl_missing_returns_null()
    {
        var store = CreateStore();
        Assert.IsNull(await store.GetEndpointAccessControl($"ct-{Guid.NewGuid():N}"));
    }

    [TestMethod]
    public async Task SetEndpointAccessControl_round_trips()
    {
        var store = CreateStore();
        var endpointId = $"ct-{Guid.NewGuid():N}";
        await store.SetEndpointAccessControl(endpointId, Sample(endpointId));
        var got = await store.GetEndpointAccessControl(endpointId);
        Assert.IsNotNull(got);
        Assert.AreEqual(AccessControlList.IdForEndpoint(endpointId), got.Id);
        Assert.AreEqual(endpointId, got.EndpointId);
        Assert.AreEqual("owner@example.com", got.Owners.Single());
    }

    [TestMethod]
    public async Task SetEndpointAccessControl_overwrite_replaces_document()
    {
        var store = CreateStore();
        var endpointId = $"ct-{Guid.NewGuid():N}";
        await store.SetEndpointAccessControl(endpointId, Sample(endpointId));

        var replacement = Sample(endpointId);
        replacement.Readers = new List<string> { "only@example.com" };
        replacement.Owners = new List<string>();
        await store.SetEndpointAccessControl(endpointId, replacement);

        var got = await store.GetEndpointAccessControl(endpointId);
        Assert.IsNotNull(got);
        Assert.AreEqual("only@example.com", got.Readers.Single());
        Assert.AreEqual(0, got.Owners.Count);
    }

    [TestMethod]
    public async Task Endpoint_acls_are_isolated_per_endpoint()
    {
        var store = CreateStore();
        var a = $"ct-{Guid.NewGuid():N}";
        var b = $"ct-{Guid.NewGuid():N}";
        var aclA = Sample(a);
        aclA.Owners = new List<string> { "a-owner@example.com" };
        var aclB = Sample(b);
        aclB.Owners = new List<string> { "b-owner@example.com" };
        await store.SetEndpointAccessControl(a, aclA);
        await store.SetEndpointAccessControl(b, aclB);

        Assert.AreEqual("a-owner@example.com", (await store.GetEndpointAccessControl(a))!.Owners.Single());
        Assert.AreEqual("b-owner@example.com", (await store.GetEndpointAccessControl(b))!.Owners.Single());
    }

    [TestMethod]
    public async Task GetEndpointAccessControls_returns_endpoint_docs_and_excludes_site()
    {
        var store = CreateStore();
        await store.SetSiteAccessControl(Sample());
        var endpointId = $"ct-{Guid.NewGuid():N}";
        await store.SetEndpointAccessControl(endpointId, Sample(endpointId));

        var all = await store.GetEndpointAccessControls();
        Assert.IsTrue(all.Any(a => a.EndpointId == endpointId));
        Assert.IsFalse(all.Any(a => a.Id == AccessControlList.SiteId));
        Assert.IsTrue(all.All(a => a.Id.StartsWith(AccessControlList.EndpointIdPrefix, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Set_normalizes_id_from_scope_not_caller_input()
    {
        // The store derives the document id from the call (site vs endpointId); a
        // caller-supplied mismatched Id must not create a stray document.
        var store = CreateStore();
        var endpointId = $"ct-{Guid.NewGuid():N}";
        var acl = Sample(endpointId);
        acl.Id = "wrong-id";
        acl.EndpointId = "wrong-endpoint";
        await store.SetEndpointAccessControl(endpointId, acl);

        var got = await store.GetEndpointAccessControl(endpointId);
        Assert.IsNotNull(got);
        Assert.AreEqual(AccessControlList.IdForEndpoint(endpointId), got.Id);
        Assert.AreEqual(endpointId, got.EndpointId);
    }

    [TestMethod]
    public async Task UpdatedAtUtc_round_trips()
    {
        var store = CreateStore();
        var endpointId = $"ct-{Guid.NewGuid():N}";
        var acl = Sample(endpointId);
        await store.SetEndpointAccessControl(endpointId, acl);
        var got = await store.GetEndpointAccessControl(endpointId);
        Assert.IsNotNull(got);
        // Compare by ticks: providers must not shift the value through timezone conversion.
        Assert.AreEqual(acl.UpdatedAtUtc.Ticks, got.UpdatedAtUtc.Ticks);
    }
}
