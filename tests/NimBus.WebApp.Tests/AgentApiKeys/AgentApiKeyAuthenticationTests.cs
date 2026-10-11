#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.AgentApiKeys;

[TestClass]
public class AgentApiKeyAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task No_header_is_no_result()
    {
        await using var host = await Host.CreateAsync();

        var result = await host.AuthenticateAsync(key: null);

        Assert.IsTrue(result.None);
    }

    [TestMethod]
    public async Task A_valid_key_authenticates_as_its_agent_without_admin_or_acl_claims()
    {
        await using var host = await Host.CreateAsync();
        var key = await host.IssueAsync("enrichment-agent");

        var result = await host.AuthenticateAsync(key.Key);

        Assert.IsTrue(result.Succeeded, result.Failure?.Message);
        var principal = result.Principal!;
        Assert.AreEqual(AgentApiKeyDefaults.SchemeName, principal.Identity!.AuthenticationType);
        Assert.AreEqual("agent:enrichment-agent", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.AreEqual("agent:enrichment-agent", principal.Identity.Name);
        Assert.AreEqual("enrichment-agent", principal.FindFirstValue(AgentApiKeyDefaults.AgentIdClaim));
        Assert.AreEqual(key.KeyId, principal.FindFirstValue(AgentApiKeyDefaults.KeyIdClaim));
        // A key must never reach the admin marker or an ACL entry.
        Assert.IsFalse(principal.Claims.Any(c => c.Type == "groups"));
        Assert.IsFalse(principal.Claims.Any(c => c.Type is "oid" or "http://schemas.microsoft.com/identity/claims/objectidentifier"));
    }

    [TestMethod]
    [DataRow("nbak_short")]
    [DataRow("Bearer something")]
    public async Task A_malformed_key_fails(string key)
    {
        await using var host = await Host.CreateAsync();

        var result = await host.AuthenticateAsync(key);

        Assert.IsNotNull(result.Failure);
    }

    [TestMethod]
    public async Task An_unknown_key_fails()
    {
        await using var host = await Host.CreateAsync();
        await host.IssueAsync("enrichment-agent");

        var result = await host.AuthenticateAsync(AgentApiKeyFormat.Generate().Key);

        Assert.IsNotNull(result.Failure);
    }

    [TestMethod]
    public async Task A_known_key_id_with_the_wrong_secret_fails()
    {
        await using var host = await Host.CreateAsync();
        var key = await host.IssueAsync("enrichment-agent");
        var other = AgentApiKeyFormat.Generate();
        var forged = AgentApiKeyFormat.Prefix + key.KeyId + other.Key[(AgentApiKeyFormat.Prefix.Length + other.KeyId.Length)..];

        var result = await host.AuthenticateAsync(forged);

        Assert.IsNotNull(result.Failure);
    }

    [TestMethod]
    public async Task A_revoked_key_fails()
    {
        await using var host = await Host.CreateAsync();
        var key = await host.IssueAsync("enrichment-agent", k => k.RevokedAtUtc = Now.UtcDateTime.AddMinutes(-1));

        var result = await host.AuthenticateAsync(key.Key);

        Assert.IsNotNull(result.Failure);
    }

    [TestMethod]
    public async Task An_expired_key_fails_and_a_not_yet_expired_one_works()
    {
        await using var host = await Host.CreateAsync();
        var expired = await host.IssueAsync("a", k => k.ExpiresAtUtc = Now.UtcDateTime.AddSeconds(-1));
        var current = await host.IssueAsync("b", k => k.ExpiresAtUtc = Now.UtcDateTime.AddDays(1));

        Assert.IsNotNull((await host.AuthenticateAsync(expired.Key)).Failure);
        var currentResult = await host.AuthenticateAsync(current.Key);
        Assert.IsTrue(currentResult.Succeeded, currentResult.Failure?.Message);
    }

    [TestMethod]
    public async Task An_unreadable_store_fails_closed()
    {
        await using var host = await Host.CreateAsync(new UnreadableKeyStore());

        var result = await host.AuthenticateAsync(AgentApiKeyFormat.Generate().Key);

        Assert.IsNotNull(result.Failure);
    }

    [TestMethod]
    public async Task A_successful_authentication_records_the_key_use()
    {
        await using var host = await Host.CreateAsync();
        var key = await host.IssueAsync("enrichment-agent");

        Assert.IsTrue((await host.AuthenticateAsync(key.Key)).Succeeded);

        var usage = await host.Store.GetAgentApiKeyUsage();
        Assert.AreEqual(Now.UtcDateTime, usage.Single(u => u.KeyId == key.KeyId).LastUsedAtUtc);
    }

    [TestMethod]
    public async Task Key_use_is_written_at_most_once_per_interval()
    {
        var store = new CountingKeyStore();
        var time = new FakeTimeProvider(Now);
        var recorder = new AgentApiKeyUsageRecorder(store, time, NullLogger<AgentApiKeyUsageRecorder>.Instance);

        await recorder.RecordAsync("abcdefghijkl");
        time.Advance(TimeSpan.FromMinutes(4));
        await recorder.RecordAsync("abcdefghijkl");
        await recorder.RecordAsync("mnopqrstuvwx");
        time.Advance(TimeSpan.FromMinutes(2));
        await recorder.RecordAsync("abcdefghijkl");

        CollectionAssert.AreEqual(new[] { "abcdefghijkl", "mnopqrstuvwx", "abcdefghijkl" }, store.Writes);
    }

    [TestMethod]
    public async Task A_failed_usage_write_is_swallowed()
    {
        var recorder = new AgentApiKeyUsageRecorder(new UnreadableKeyStore(), new FakeTimeProvider(Now), NullLogger<AgentApiKeyUsageRecorder>.Instance);

        await recorder.RecordAsync("abcdefghijkl"); // must not throw
    }

    [TestMethod]
    public async Task Provider_keeps_serving_a_saved_list_after_Accept_without_reading()
    {
        var store = new UnreadableKeyStore();
        var provider = new AgentApiKeyProvider(store, NullLogger<AgentApiKeyProvider>.Instance, new FakeTimeProvider(Now));
        var settings = new AgentApiKeySettings { Revision = "r1", Keys = { new AgentApiKey { KeyId = "abcdefghijkl", AgentId = "a" } } };

        Assert.IsNull(await provider.GetAsync(), "an unreadable store yields nothing until a list is known");
        provider.Accept(settings);

        Assert.AreEqual("r1", (await provider.GetAsync())!.Revision);
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Host(ServiceProvider services, IEndpointMetadataStore store)
        {
            _services = services;
            Store = store;
        }

        public IEndpointMetadataStore Store { get; }

        public static Task<Host> CreateAsync(IEndpointMetadataStore? store = null)
        {
            store ??= new InMemoryMessageStore();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
            services.AddSingleton(store);
            services.AddSingleton<IAgentApiKeyProvider, AgentApiKeyProvider>();
            services.AddSingleton<IAgentApiKeyUsageRecorder, AgentApiKeyUsageRecorder>();
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, AgentApiKeyAuthenticationHandler>(AgentApiKeyDefaults.SchemeName, null);
            return Task.FromResult(new Host(services.BuildServiceProvider(), store));
        }

        public async Task<GeneratedAgentApiKey> IssueAsync(string agentId, Action<AgentApiKey>? customize = null)
        {
            var generated = AgentApiKeyFormat.Generate();
            var record = new AgentApiKey
            {
                KeyId = generated.KeyId,
                AgentId = agentId,
                SecretHash = generated.SecretHash,
                CreatedAtUtc = Now.UtcDateTime.AddDays(-1),
            };
            customize?.Invoke(record);

            var current = await Store.GetAgentApiKeySettings();
            var next = current.Clone();
            next.Keys.Add(record);
            next.Revision = Guid.NewGuid().ToString();
            Assert.IsTrue(await Store.TrySetAgentApiKeySettings(next, current.Revision));
            _services.GetRequiredService<IAgentApiKeyProvider>().Accept(next);
            return generated;
        }

        public async Task<AuthenticateResult> AuthenticateAsync(string? key)
        {
            // One scope per request, as ASP.NET Core does: the handler provider is scoped and
            // caches handler instances, which cache their first result.
            await using var scope = _services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (key is not null)
                context.Request.Headers[AgentApiKeyDefaults.HeaderName] = key;
            return await context.AuthenticateAsync(AgentApiKeyDefaults.SchemeName);
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    private sealed class UnreadableKeyStore : InMemoryMessageStore
    {
        public override Task<AgentApiKeySettings> GetAgentApiKeySettings() => throw new InvalidOperationException("store down");

        public override Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc) => throw new InvalidOperationException("store down");
    }

    private sealed class CountingKeyStore : InMemoryMessageStore
    {
        public List<string> Writes { get; } = new();

        public override Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc)
        {
            Writes.Add(keyId);
            return base.RecordAgentApiKeyUse(keyId, usedAtUtc);
        }
    }
}
