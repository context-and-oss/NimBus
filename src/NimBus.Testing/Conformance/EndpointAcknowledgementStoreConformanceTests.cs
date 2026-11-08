#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IEndpointAcknowledgementStore"/>:
/// one acknowledgement per endpoint, upsert replaces, and the token-guarded remove
/// never deletes an acknowledgement it did not read.
/// </summary>
[TestClass]
public abstract class EndpointAcknowledgementStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract IEndpointAcknowledgementStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    private static EndpointAcknowledgement Ack(string endpointId, string reason = "Known ERP outage") => new()
    {
        EndpointId = endpointId,
        AcknowledgementId = Guid.NewGuid().ToString("N"),
        Reason = reason,
        AcknowledgedBy = "alice@example.com",
        AcknowledgedAtUtc = new DateTime(2026, 9, 25, 8, 15, 30, DateTimeKind.Utc),
        ExpiresAtUtc = new DateTime(2026, 9, 25, 12, 15, 30, DateTimeKind.Utc),
        FailedCountAtAcknowledgement = 42,
    };

    private static async Task<EndpointAcknowledgement?> Row(IEndpointAcknowledgementStore store, string endpointId)
        => (await store.GetEndpointAcknowledgements()).SingleOrDefault(a => a.EndpointId == endpointId);

    [TestMethod]
    public async Task Set_then_Get_round_trips_every_field()
    {
        var store = CreateStore();
        var ack = Ack(Id("ep-roundtrip"));

        await store.SetEndpointAcknowledgement(ack);

        var stored = await Row(store, ack.EndpointId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(ack.AcknowledgementId, stored.AcknowledgementId);
        Assert.AreEqual(ack.Reason, stored.Reason);
        Assert.AreEqual(ack.AcknowledgedBy, stored.AcknowledgedBy);
        Assert.AreEqual(ack.FailedCountAtAcknowledgement, stored.FailedCountAtAcknowledgement);
        AssertUtc(ack.AcknowledgedAtUtc, stored.AcknowledgedAtUtc);
        AssertUtc(ack.ExpiresAtUtc, stored.ExpiresAtUtc);
    }

    [TestMethod]
    public async Task Optional_fields_round_trip_empty_and_null()
    {
        var store = CreateStore();
        var ack = Ack(Id("ep-optional"), reason: string.Empty);
        ack.AcknowledgedBy = null;

        await store.SetEndpointAcknowledgement(ack);

        var stored = await Row(store, ack.EndpointId);
        Assert.IsNotNull(stored);
        Assert.AreEqual(string.Empty, stored.Reason);
        Assert.IsNull(stored.AcknowledgedBy);
    }

    [TestMethod]
    public async Task Set_replaces_the_existing_acknowledgement_for_the_endpoint()
    {
        var store = CreateStore();
        var endpointId = Id("ep-replace");
        await store.SetEndpointAcknowledgement(Ack(endpointId, "first"));
        var second = Ack(endpointId, "second");

        await store.SetEndpointAcknowledgement(second);

        var rows = (await store.GetEndpointAcknowledgements()).Where(a => a.EndpointId == endpointId).ToList();
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("second", rows[0].Reason);
        Assert.AreEqual(second.AcknowledgementId, rows[0].AcknowledgementId);
    }

    [TestMethod]
    public async Task Get_lists_acknowledgements_across_endpoints()
    {
        var store = CreateStore();
        await store.SetEndpointAcknowledgement(Ack(Id("ep-list-a")));
        await store.SetEndpointAcknowledgement(Ack(Id("ep-list-b")));

        var ids = (await store.GetEndpointAcknowledgements())
            .Select(a => a.EndpointId)
            .Where(id => id.StartsWith(_scope, StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(new[] { Id("ep-list-a"), Id("ep-list-b") }, ids);
    }

    [TestMethod]
    public async Task Remove_without_token_removes_unconditionally()
    {
        var store = CreateStore();
        var endpointId = Id("ep-remove");
        await store.SetEndpointAcknowledgement(Ack(endpointId));

        Assert.IsTrue(await store.RemoveEndpointAcknowledgement(endpointId));
        Assert.IsNull(await Row(store, endpointId));
        Assert.IsFalse(await store.RemoveEndpointAcknowledgement(endpointId), "Nothing left to remove.");
    }

    [TestMethod]
    public async Task Remove_with_token_removes_only_the_acknowledgement_that_was_read()
    {
        var store = CreateStore();
        var endpointId = Id("ep-token");
        var stale = Ack(endpointId, "stale");
        await store.SetEndpointAcknowledgement(stale);
        var current = Ack(endpointId, "current");
        await store.SetEndpointAcknowledgement(current);

        Assert.IsFalse(await store.RemoveEndpointAcknowledgement(endpointId, stale.AcknowledgementId),
            "A token from a replaced acknowledgement must not delete the newer one.");
        Assert.AreEqual("current", (await Row(store, endpointId))?.Reason);

        Assert.IsTrue(await store.RemoveEndpointAcknowledgement(endpointId, current.AcknowledgementId));
        Assert.IsNull(await Row(store, endpointId));
        Assert.IsFalse(await store.RemoveEndpointAcknowledgement(Id("ep-missing"), current.AcknowledgementId));
    }

    private static void AssertUtc(DateTime expected, DateTime actual)
    {
        Assert.AreEqual(DateTimeKind.Utc, actual.Kind);
        Assert.AreEqual(expected.Ticks, actual.Ticks);
    }
}
