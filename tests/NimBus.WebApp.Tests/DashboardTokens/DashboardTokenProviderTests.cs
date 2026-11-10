#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.DashboardTokens;

namespace NimBus.WebApp.Tests.DashboardTokens;

[TestClass]
public class DashboardTokenProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void A_token_has_the_dashboard_prefix_and_parses_back_to_its_id()
    {
        var generated = SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix);

        StringAssert.StartsWith(generated.Value, "nbdt_", StringComparison.Ordinal);
        Assert.IsTrue(SecretTokenFormat.TryParse(DashboardTokenDefaults.Prefix, generated.Value, out var id, out var secret));
        Assert.AreEqual(generated.Id, id);
        Assert.IsTrue(SecretTokenFormat.SecretMatches(secret, generated.SecretHash));
    }

    [TestMethod]
    public void An_agent_key_is_not_a_dashboard_token()
    {
        var agentKey = SecretTokenFormat.Generate("nbak_").Value;

        Assert.IsFalse(SecretTokenFormat.TryParse(DashboardTokenDefaults.Prefix, agentKey, out _, out _));
    }

    [TestMethod]
    public async Task Without_a_store_every_token_is_refused()
    {
        var provider = new DashboardTokenProvider(NullLogger<DashboardTokenProvider>.Instance, new FakeTimeProvider(Now));

        Assert.IsNull(await provider.GetAsync());
    }

    [TestMethod]
    public async Task A_cold_start_with_the_store_down_refuses_every_token()
    {
        var store = new FlakyStore { Failing = true };
        var provider = new DashboardTokenProvider(NullLogger<DashboardTokenProvider>.Instance, new FakeTimeProvider(Now), store);

        Assert.IsNull(await provider.GetAsync());
    }

    [TestMethod]
    public async Task During_a_store_outage_a_snapshot_is_trusted_for_two_minutes_and_then_refused()
    {
        var time = new FakeTimeProvider(Now);
        var store = new FlakyStore();
        await Seed(store);
        var provider = new DashboardTokenProvider(NullLogger<DashboardTokenProvider>.Instance, time, store);
        Assert.IsNotNull(await provider.GetAsync());

        store.Failing = true;
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.IsNotNull(await provider.GetAsync(), "a 31-second-old snapshot is still trusted");

        time.Advance(TimeSpan.FromSeconds(80));
        Assert.IsNotNull(await provider.GetAsync(), "under two minutes old");

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.IsNull(await provider.GetAsync(), "older than two minutes: every token is refused");

        store.Failing = false;
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.IsNotNull(await provider.GetAsync(), "a successful read restores it");
    }

    [TestMethod]
    public async Task A_change_on_another_instance_applies_after_the_success_ttl()
    {
        var time = new FakeTimeProvider(Now);
        var store = new FlakyStore();
        await Seed(store);
        var provider = new DashboardTokenProvider(NullLogger<DashboardTokenProvider>.Instance, time, store);
        Assert.IsNull((await provider.GetAsync())!.Tokens[0].RevokedAtUtc);

        var revoked = await store.GetDashboardTokenSettings();
        revoked.Tokens[0].RevokedAtUtc = Now.UtcDateTime;
        var expected = revoked.Revision;
        revoked.Revision = "r2";
        Assert.IsTrue(await store.TrySetDashboardTokenSettings(revoked, expected));

        time.Advance(TimeSpan.FromSeconds(29));
        Assert.IsNull((await provider.GetAsync())!.Tokens[0].RevokedAtUtc, "still cached");
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.IsNotNull((await provider.GetAsync())!.Tokens[0].RevokedAtUtc);
    }

    [TestMethod]
    public async Task A_save_on_this_instance_applies_at_once()
    {
        var time = new FakeTimeProvider(Now);
        var store = new FlakyStore();
        await Seed(store);
        var provider = new DashboardTokenProvider(NullLogger<DashboardTokenProvider>.Instance, time, store);
        await provider.GetAsync();

        provider.Accept(new DashboardTokenSettings { Revision = "r9" });

        Assert.AreEqual("r9", (await provider.GetAsync())!.Revision);
    }

    private static async Task Seed(InMemoryMessageStore store)
    {
        var generated = SecretTokenFormat.Generate(DashboardTokenDefaults.Prefix);
        Assert.IsTrue(await store.TrySetDashboardTokenSettings(new DashboardTokenSettings
        {
            Revision = "r1",
            Tokens = { new DashboardToken { TokenId = generated.Id, Name = "Wall", SecretHash = generated.SecretHash, CreatedAtUtc = Now.UtcDateTime } },
        }, null));
    }

    private sealed class FlakyStore : InMemoryMessageStore
    {
        public bool Failing { get; set; }

        public override Task<DashboardTokenSettings> GetDashboardTokenSettings()
            => Failing ? throw new InvalidOperationException("store down") : base.GetDashboardTokenSettings();
    }
}
