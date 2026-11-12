#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>Provider-agnostic conformance suite for <see cref="IDashboardTokenStore"/>.</summary>
[TestClass]
public abstract class DashboardTokenStoreConformanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    /// <summary>A freshly initialized store.</summary>
    protected abstract IDashboardTokenStore CreateStore();

    [TestMethod]
    public async Task GetDashboardTokenSettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetDashboardTokenSettings();

        // A shared backend may already carry saved tokens, so the empty default is asserted on
        // the type and only the never-null contract against the store.
        Assert.IsNotNull(settings);
        Assert.AreEqual(DashboardTokenSettings.SingletonId, settings.Id);
        Assert.IsNotNull(settings.Tokens);
        Assert.IsNull(new DashboardTokenSettings().Revision);
        Assert.AreEqual(0, new DashboardTokenSettings().Tokens.Count);
    }

    [TestMethod]
    public async Task TrySetDashboardTokenSettings_round_trips_every_field()
    {
        var store = CreateStore();
        var sample = Sample();

        await SaveAsync(store, sample);
        var stored = await store.GetDashboardTokenSettings();

        Assert.AreEqual(sample.Revision, stored.Revision);
        Assert.AreEqual(sample.UpdatedBy, stored.UpdatedBy);
        Assert.AreEqual(sample.UpdatedAtUtc!.Value.Ticks, stored.UpdatedAtUtc!.Value.Ticks);
        Assert.AreEqual(2, stored.Tokens.Count);

        var active = stored.Tokens.Single(t => t.TokenId == "abcdefghijkl");
        Assert.AreEqual("Operations wall", active.Name);
        Assert.AreEqual("q83vEjRWeJ1mN9u5Hl1YGqHk8Ywq4cG4x3V5D8pX1Ak=", active.SecretHash);
        Assert.AreEqual(T0.Ticks, active.CreatedAtUtc.Ticks);
        Assert.AreEqual("owner@example.com", active.CreatedBy);
        Assert.AreEqual(T0.AddDays(365).Ticks, active.ExpiresAtUtc!.Value.Ticks);
        Assert.IsNull(active.RevokedAtUtc);
        Assert.IsNull(active.RevokedBy);

        var revoked = stored.Tokens.Single(t => t.TokenId == "mnopqrstuvwx");
        Assert.IsNull(revoked.ExpiresAtUtc);
        Assert.AreEqual(T0.AddHours(1).Ticks, revoked.RevokedAtUtc!.Value.Ticks);
        Assert.AreEqual("owner@example.com", revoked.RevokedBy);
    }

    [TestMethod]
    public async Task TrySetDashboardTokenSettings_refuses_a_stale_revision_and_keeps_the_record()
    {
        var store = CreateStore();
        var saved = await SaveAsync(store, Sample());

        var stale = Sample();
        stale.Tokens.Clear();
        stale.Revision = Guid.NewGuid().ToString();
        Assert.IsFalse(await store.TrySetDashboardTokenSettings(stale, Guid.NewGuid().ToString()));

        // Null means "create": refused because a record exists.
        Assert.IsFalse(await store.TrySetDashboardTokenSettings(stale, null));

        var stored = await store.GetDashboardTokenSettings();
        Assert.AreEqual(saved, stored.Revision);
        Assert.AreEqual(2, stored.Tokens.Count);
    }

    [TestMethod]
    public async Task TrySetDashboardTokenSettings_lets_exactly_one_of_two_concurrent_writers_win()
    {
        var store = CreateStore();
        var current = await SaveAsync(store, Sample());

        var first = Sample();
        first.Revision = Guid.NewGuid().ToString();
        var second = Sample();
        second.Revision = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(
            store.TrySetDashboardTokenSettings(first, current),
            store.TrySetDashboardTokenSettings(second, current));

        Assert.AreEqual(1, results.Count(r => r));
        var winner = results[0] ? first.Revision : second.Revision;
        Assert.AreEqual(winner, (await store.GetDashboardTokenSettings()).Revision);
    }

    [TestMethod]
    public async Task RecordDashboardTokenUse_records_and_overwrites_per_token()
    {
        var store = CreateStore();
        // Token ids scoped to this test so a shared backend's other rows don't interfere.
        var tokenA = _scope + "-a";
        var tokenB = _scope + "-b";

        await store.RecordDashboardTokenUse(tokenA, T0);
        await store.RecordDashboardTokenUse(tokenB, T0.AddMinutes(1));
        await store.RecordDashboardTokenUse(tokenA, T0.AddMinutes(10));

        var usage = (await store.GetDashboardTokenUsage())
            .Where(u => u.TokenId == tokenA || u.TokenId == tokenB)
            .ToDictionary(u => u.TokenId, u => u.LastUsedAtUtc);

        Assert.AreEqual(2, usage.Count);
        Assert.AreEqual(T0.AddMinutes(10).Ticks, usage[tokenA].Ticks);
        Assert.AreEqual(T0.AddMinutes(1).Ticks, usage[tokenB].Ticks);
    }

    /// <summary>Saves <paramref name="settings"/> over whatever is stored and returns its new revision.</summary>
    private static async Task<string> SaveAsync(IDashboardTokenStore store, DashboardTokenSettings settings)
    {
        var current = await store.GetDashboardTokenSettings();
        settings.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetDashboardTokenSettings(settings, current.Revision));
        return settings.Revision;
    }

    private static DashboardTokenSettings Sample() => new()
    {
        UpdatedBy = "owner@example.com",
        UpdatedAtUtc = T0.AddHours(1),
        Tokens =
        {
            new DashboardToken
            {
                TokenId = "abcdefghijkl",
                Name = "Operations wall",
                SecretHash = "q83vEjRWeJ1mN9u5Hl1YGqHk8Ywq4cG4x3V5D8pX1Ak=",
                CreatedAtUtc = T0,
                CreatedBy = "owner@example.com",
                ExpiresAtUtc = T0.AddDays(365),
            },
            new DashboardToken
            {
                TokenId = "mnopqrstuvwx",
                Name = "Old wall",
                SecretHash = "4Xg2o4cH6m5L0p3x1rT9yZbE7wQkV8uJ2nA6sD5fG1h=",
                CreatedAtUtc = T0,
                CreatedBy = "owner@example.com",
                RevokedAtUtc = T0.AddHours(1),
                RevokedBy = "owner@example.com",
            },
        },
    };
}
