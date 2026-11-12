#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>Provider-agnostic conformance suite for <see cref="IIntegratorApiKeyStore"/>.</summary>
[TestClass]
public abstract class IntegratorApiKeyStoreConformanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    /// <summary>A freshly initialized store.</summary>
    protected abstract IIntegratorApiKeyStore CreateStore();

    [TestMethod]
    public async Task GetIntegratorApiKeySettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetIntegratorApiKeySettings();

        // A shared backend may already carry saved keys, so the empty default is asserted on
        // the type and only the never-null contract against the store.
        Assert.IsNotNull(settings);
        Assert.AreEqual(IntegratorApiKeySettings.SingletonId, settings.Id);
        Assert.IsNotNull(settings.Keys);
        Assert.IsNull(new IntegratorApiKeySettings().Revision);
        Assert.AreEqual(0, new IntegratorApiKeySettings().Keys.Count);
    }

    [TestMethod]
    public async Task TrySetIntegratorApiKeySettings_round_trips_every_field()
    {
        var store = CreateStore();
        var sample = Sample();

        await SaveAsync(store, sample);
        var stored = await store.GetIntegratorApiKeySettings();

        Assert.AreEqual(sample.Revision, stored.Revision);
        Assert.AreEqual(sample.UpdatedBy, stored.UpdatedBy);
        Assert.AreEqual(sample.UpdatedAtUtc!.Value.Ticks, stored.UpdatedAtUtc!.Value.Ticks);
        Assert.AreEqual(2, stored.Keys.Count);

        var active = stored.Keys.Single(t => t.KeyId == "abcdefghijkl");
        Assert.AreEqual("Billing reconciliation", active.Name);
        Assert.AreEqual("q83vEjRWeJ1mN9u5Hl1YGqHk8Ywq4cG4x3V5D8pX1Ak=", active.SecretHash);
        Assert.AreEqual(T0.Ticks, active.CreatedAtUtc.Ticks);
        Assert.AreEqual("owner@example.com", active.CreatedBy);
        Assert.AreEqual(T0.AddDays(365).Ticks, active.ExpiresAtUtc!.Value.Ticks);
        Assert.AreEqual(IntegratorApiKeyRole.Contributor, active.Role);
        CollectionAssert.AreEqual(new[] { "Billing", "Warehouse" }, active.Endpoints);
        Assert.IsTrue(active.CanReadPii);
        Assert.IsNull(active.RevokedAtUtc);
        Assert.IsNull(active.RevokedBy);

        var revoked = stored.Keys.Single(t => t.KeyId == "mnopqrstuvwx");
        Assert.IsNull(revoked.ExpiresAtUtc);
        Assert.AreEqual(IntegratorApiKeyRole.Reader, revoked.Role);
        Assert.IsNull(revoked.Endpoints, "Null means site-wide and must survive the round trip.");
        Assert.IsFalse(revoked.CanReadPii);
        Assert.AreEqual(T0.AddHours(1).Ticks, revoked.RevokedAtUtc!.Value.Ticks);
        Assert.AreEqual("owner@example.com", revoked.RevokedBy);
    }

    [TestMethod]
    public async Task TrySetIntegratorApiKeySettings_refuses_a_stale_revision_and_keeps_the_record()
    {
        var store = CreateStore();
        var saved = await SaveAsync(store, Sample());

        var stale = Sample();
        stale.Keys.Clear();
        stale.Revision = Guid.NewGuid().ToString();
        Assert.IsFalse(await store.TrySetIntegratorApiKeySettings(stale, Guid.NewGuid().ToString()));

        // Null means "create": refused because a record exists.
        Assert.IsFalse(await store.TrySetIntegratorApiKeySettings(stale, null));

        var stored = await store.GetIntegratorApiKeySettings();
        Assert.AreEqual(saved, stored.Revision);
        Assert.AreEqual(2, stored.Keys.Count);
    }

    [TestMethod]
    public async Task TrySetIntegratorApiKeySettings_lets_exactly_one_of_two_concurrent_writers_win()
    {
        var store = CreateStore();
        var current = await SaveAsync(store, Sample());

        var first = Sample();
        first.Revision = Guid.NewGuid().ToString();
        var second = Sample();
        second.Revision = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(
            store.TrySetIntegratorApiKeySettings(first, current),
            store.TrySetIntegratorApiKeySettings(second, current));

        Assert.AreEqual(1, results.Count(r => r));
        var winner = results[0] ? first.Revision : second.Revision;
        Assert.AreEqual(winner, (await store.GetIntegratorApiKeySettings()).Revision);
    }

    [TestMethod]
    public async Task RecordIntegratorApiKeyUse_records_and_overwrites_per_key()
    {
        var store = CreateStore();
        // Key ids scoped to this test so a shared backend's other rows don't interfere.
        var keyA = _scope + "-a";
        var keyB = _scope + "-b";

        await store.RecordIntegratorApiKeyUse(keyA, T0);
        await store.RecordIntegratorApiKeyUse(keyB, T0.AddMinutes(1));
        await store.RecordIntegratorApiKeyUse(keyA, T0.AddMinutes(10));

        var usage = (await store.GetIntegratorApiKeyUsage())
            .Where(u => u.KeyId == keyA || u.KeyId == keyB)
            .ToDictionary(u => u.KeyId, u => u.LastUsedAtUtc);

        Assert.AreEqual(2, usage.Count);
        Assert.AreEqual(T0.AddMinutes(10).Ticks, usage[keyA].Ticks);
        Assert.AreEqual(T0.AddMinutes(1).Ticks, usage[keyB].Ticks);
    }

    /// <summary>Saves <paramref name="settings"/> over whatever is stored and returns its new revision.</summary>
    private static async Task<string> SaveAsync(IIntegratorApiKeyStore store, IntegratorApiKeySettings settings)
    {
        var current = await store.GetIntegratorApiKeySettings();
        settings.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetIntegratorApiKeySettings(settings, current.Revision));
        return settings.Revision;
    }

    private static IntegratorApiKeySettings Sample() => new()
    {
        UpdatedBy = "owner@example.com",
        UpdatedAtUtc = T0.AddHours(1),
        Keys =
        {
            new IntegratorApiKey
            {
                KeyId = "abcdefghijkl",
                Name = "Billing reconciliation",
                SecretHash = "q83vEjRWeJ1mN9u5Hl1YGqHk8Ywq4cG4x3V5D8pX1Ak=",
                CreatedAtUtc = T0,
                CreatedBy = "owner@example.com",
                ExpiresAtUtc = T0.AddDays(365),
                Role = IntegratorApiKeyRole.Contributor,
                Endpoints = ["Billing", "Warehouse"],
                CanReadPii = true,
            },
            new IntegratorApiKey
            {
                KeyId = "mnopqrstuvwx",
                Name = "Old CI pipeline",
                SecretHash = "4Xg2o4cH6m5L0p3x1rT9yZbE7wQkV8uJ2nA6sD5fG1h=",
                CreatedAtUtc = T0,
                CreatedBy = "owner@example.com",
                RevokedAtUtc = T0.AddHours(1),
                RevokedBy = "owner@example.com",
            },
        },
    };
}
