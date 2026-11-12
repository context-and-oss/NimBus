#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services.IntegratorApiKeys;

namespace NimBus.WebApp.Tests.IntegratorApiKeys;

/// <summary>
/// The integrator key cache fails closed (Spec 045): a failed fresh read denies, a store outage
/// never keeps a snapshot past the 30-second revocation bound, and a read that started before a
/// local save cannot undo it.
/// </summary>
[TestClass]
public class IntegratorApiKeyProviderTests
{
    [TestMethod]
    public async Task A_fresh_read_that_fails_denies_instead_of_using_the_cache()
    {
        var store = new ControllableStore(Active());
        var provider = Create(store, new FakeTimeProvider(DateTimeOffset.UtcNow));
        Assert.IsNotNull(await provider.GetAsync());

        store.Fail = true;

        Assert.IsNull(await provider.GetFreshAsync());
    }

    [TestMethod]
    public async Task A_store_outage_refuses_every_key_once_the_snapshot_is_30_seconds_old()
    {
        var store = new ControllableStore(Active());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = Create(store, time);
        Assert.IsNotNull(await provider.GetAsync());

        store.Fail = true;
        time.Advance(TimeSpan.FromSeconds(31));

        Assert.IsNull(await provider.GetAsync(), "A failed refresh must not extend the snapshot past the revocation bound.");
    }

    [TestMethod]
    public async Task A_snapshot_younger_than_30_seconds_survives_a_failed_refresh()
    {
        var store = new ControllableStore(Active());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = Create(store, time);
        Assert.IsNotNull(await provider.GetAsync());

        // Force a refresh before the snapshot ages out: Accept a copy, then age it past the TTL
        // boundary minus a little and fail the read.
        store.Fail = true;
        time.Advance(TimeSpan.FromSeconds(29));

        Assert.IsNotNull(await provider.GetAsync(), "Within the bound the last good snapshot is still trusted.");
    }

    [TestMethod]
    public async Task A_read_that_started_before_a_local_revocation_does_not_undo_it()
    {
        var store = new ControllableStore(Active()) { Hold = new TaskCompletionSource() };
        var provider = Create(store, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var inFlight = provider.GetAsync();
        await store.ReadStarted.Task;

        var revoked = Active();
        revoked.Revision = "r2";
        revoked.Keys[0].RevokedAtUtc = DateTime.UtcNow;
        provider.Accept(revoked);

        store.Hold.SetResult();
        var seenByStaleRead = await inFlight;

        Assert.IsNotNull(seenByStaleRead!.Keys[0].RevokedAtUtc, "The stale read returns the newer saved snapshot.");
        Assert.IsNotNull((await provider.GetAsync())!.Keys[0].RevokedAtUtc, "The revocation stays cached.");
    }

    [TestMethod]
    public async Task A_fresh_read_that_started_before_a_local_revocation_does_not_undo_it()
    {
        var store = new ControllableStore(Active()) { Hold = new TaskCompletionSource() };
        var provider = Create(store, new FakeTimeProvider(DateTimeOffset.UtcNow));

        var inFlight = provider.GetFreshAsync();
        await store.ReadStarted.Task;

        var revoked = Active();
        revoked.Revision = "r2";
        revoked.Keys[0].RevokedAtUtc = DateTime.UtcNow;
        provider.Accept(revoked);
        store.Hold.SetResult();
        await inFlight;

        Assert.IsNotNull((await provider.GetAsync())!.Keys[0].RevokedAtUtc);
    }

    private static IntegratorApiKeyProvider Create(IIntegratorApiKeyStore store, TimeProvider time)
        => new(NullLogger<IntegratorApiKeyProvider>.Instance, time, store);

    private static IntegratorApiKeySettings Active() => new()
    {
        Revision = "r1",
        Keys = { new IntegratorApiKey { KeyId = "abcdefghijkl", Role = IntegratorApiKeyRole.Contributor, CreatedAtUtc = DateTime.UtcNow } },
    };

    private sealed class ControllableStore(IntegratorApiKeySettings settings) : IIntegratorApiKeyStore
    {
        public bool Fail { get; set; }

        public TaskCompletionSource? Hold { get; set; }

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IntegratorApiKeySettings> GetIntegratorApiKeySettings()
        {
            ReadStarted.TrySetResult();
            if (Hold is not null)
                await Hold.Task;
            if (Fail)
                throw new InvalidOperationException("store unavailable");
            return settings.Clone();
        }

        public Task<bool> TrySetIntegratorApiKeySettings(IntegratorApiKeySettings value, string? expectedRevision) => Task.FromResult(true);

        public Task<List<IntegratorApiKeyUsage>> GetIntegratorApiKeyUsage() => Task.FromResult(new List<IntegratorApiKeyUsage>());

        public Task RecordIntegratorApiKeyUse(string keyId, DateTime usedAtUtc) => Task.CompletedTask;
    }
}
