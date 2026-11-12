#pragma warning disable CA1707, CA2007
using System;
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
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using ContractAuditSettings = NimBus.WebApp.ManagementApi.AuditSettings;
using StoreAuditSettings = NimBus.MessageStore.States.AuditSettings;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Settings → Audit logging: an Owner chooses which audit types are recorded. The writer skips a
/// disabled type, but a denied attempt and the settings change itself are always recorded,
/// and a settings outage never stops auditing.
/// </summary>
[TestClass]
public sealed class AuditSettingsTests
{
    // ───────── AuditLogService ─────────

    [TestMethod]
    public async Task LogAuditAsync_skips_a_disabled_type()
    {
        var store = new AuditStore();
        var provider = NewProvider(store);
        await provider.SaveAsync(new[] { MessageAuditType.SearchEvents });
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store, provider);

        await sut.LogAuditAsync(MessageAuditType.SearchEvents, new DefaultHttpContext());
        await sut.LogAuditAsync(MessageAuditType.Resubmit, new DefaultHttpContext());

        CollectionAssert.AreEqual(new[] { MessageAuditType.Resubmit }, store.Written.ToArray());
    }

    [TestMethod]
    public async Task LogAuditAsync_records_a_denied_attempt_of_a_disabled_type()
    {
        var store = new AuditStore();
        var provider = NewProvider(store);
        await provider.SaveAsync(new[] { MessageAuditType.SearchEvents });
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store, provider);

        await sut.LogAuditAsync(MessageAuditType.SearchEvents, new DefaultHttpContext(), accessDenied: true);

        CollectionAssert.AreEqual(new[] { MessageAuditType.SearchEvents }, store.Written.ToArray());
    }

    [TestMethod]
    public async Task LogAuditAsync_records_every_type_when_the_settings_read_fails()
    {
        var store = new AuditStore { FailSettingsRead = true };
        var sut = new AuditLogService(NullLogger<AuditLogService>.Instance, store, NewProvider(store));

        await sut.LogAuditAsync(MessageAuditType.SearchEvents, new DefaultHttpContext());

        CollectionAssert.AreEqual(new[] { MessageAuditType.SearchEvents }, store.Written.ToArray());
    }

    // ───────── AuditSettingsProvider ─────────

    [TestMethod]
    public async Task Provider_always_records_UpdateAuditSettings_even_if_the_store_lists_it()
    {
        var store = new AuditStore();
        await store.SetAuditSettings(new StoreAuditSettings { DisabledAuditTypes = { "UpdateAuditSettings", "SearchEvents" } });
        var provider = NewProvider(store);

        Assert.IsTrue(await provider.IsRecordedAsync(MessageAuditType.UpdateAuditSettings));
        Assert.IsFalse(await provider.IsRecordedAsync(MessageAuditType.SearchEvents));
    }

    [TestMethod]
    public async Task Provider_rejects_disabling_a_type_that_is_not_configurable()
    {
        var provider = NewProvider(new AuditStore());

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => provider.SaveAsync(new[] { MessageAuditType.UpdateAuditSettings }));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => provider.SaveAsync(new[] { MessageAuditType.Comment }));
    }

    [TestMethod]
    public async Task Provider_caches_the_selection_until_the_ttl_expires()
    {
        var store = new AuditStore();
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var provider = new AuditSettingsProvider(store, NullLogger<AuditSettingsProvider>.Instance, () => now);

        Assert.IsTrue(await provider.IsRecordedAsync(MessageAuditType.SearchEvents));

        // Another instance's save lands in the store; this instance converges on expiry.
        await store.SetAuditSettings(new StoreAuditSettings { DisabledAuditTypes = { "SearchEvents" } });
        Assert.IsTrue(await provider.IsRecordedAsync(MessageAuditType.SearchEvents));
        Assert.AreEqual(1, store.SettingsReads);

        now = now.AddSeconds(31);
        Assert.IsFalse(await provider.IsRecordedAsync(MessageAuditType.SearchEvents));
        Assert.AreEqual(2, store.SettingsReads);
    }

    [TestMethod]
    public async Task Provider_keeps_the_last_known_selection_when_a_refresh_fails()
    {
        var store = new AuditStore();
        var now = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        var provider = new AuditSettingsProvider(store, NullLogger<AuditSettingsProvider>.Instance, () => now);
        await provider.SaveAsync(new[] { MessageAuditType.SearchEvents });

        store.FailSettingsRead = true;
        now = now.AddMinutes(5);

        Assert.IsFalse(await provider.IsRecordedAsync(MessageAuditType.SearchEvents));
    }

    [TestMethod]
    [DataRow("searchEvents", true)]
    [DataRow("SearchEvents", true)]
    [DataRow("7", false)]
    [DataRow("", false)]
    [DataRow("noSuchType", false)]
    public void TryParseName_accepts_names_and_rejects_positions(string name, bool expected)
        => Assert.AreEqual(expected, AuditSettingsProvider.TryParseName(name, out _));

    // ───────── Admin API ─────────

    [TestMethod]
    public async Task Get_returns_camelCase_types_without_the_locked_ones()
    {
        var store = new AuditStore();
        var provider = NewProvider(store);
        await provider.SaveAsync(new[] { MessageAuditType.FailureClassified });

        var result = await CreateController(provider, new RecordingAuditLogService(), owner: true).GetAdminAuditSettingsAsync();

        var body = (ContractAuditSettings)((OkObjectResult)result.Result!).Value!;
        CollectionAssert.AreEqual(new[] { "failureClassified" }, body.DisabledAuditTypes.ToArray());
        CollectionAssert.Contains(body.ConfigurableAuditTypes.ToArray(), "searchEvents");
        CollectionAssert.DoesNotContain(body.ConfigurableAuditTypes.ToArray(), "updateAuditSettings");
        CollectionAssert.DoesNotContain(body.ConfigurableAuditTypes.ToArray(), "comment");
    }

    [TestMethod]
    public async Task Put_stores_the_selection_and_audits_the_change()
    {
        var store = new AuditStore();
        var provider = NewProvider(store);
        var audit = new RecordingAuditLogService();

        var result = await CreateController(provider, audit, owner: true).PutAdminAuditSettingsAsync(
            new ContractAuditSettings { DisabledAuditTypes = new List<string> { "searchEvents", "getEventDetails" } });

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        Assert.IsFalse(await provider.IsRecordedAsync(MessageAuditType.SearchEvents));
        Assert.IsFalse(await provider.IsRecordedAsync(MessageAuditType.GetEventDetails));
        var entry = audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.UpdateAuditSettings, entry.Type);
        Assert.IsFalse(entry.AccessDenied);
        StringAssert.Contains(entry.Data, "searchEvents", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("updateAuditSettings")]
    [DataRow("comment")]
    [DataRow("noSuchType")]
    [DataRow("3")]
    public async Task Put_rejects_a_type_that_cannot_be_disabled(string name)
    {
        var store = new AuditStore();
        var provider = NewProvider(store);

        var result = await CreateController(provider, new RecordingAuditLogService(), owner: true).PutAdminAuditSettingsAsync(
            new ContractAuditSettings { DisabledAuditTypes = new List<string> { name } });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, (await store.GetAuditSettings()).DisabledAuditTypes.Count);
    }

    [TestMethod]
    public async Task Put_by_a_non_owner_is_forbidden_and_audited_as_denied()
    {
        var store = new AuditStore();
        var audit = new RecordingAuditLogService();

        var result = await CreateController(NewProvider(store), audit, owner: false).PutAdminAuditSettingsAsync(
            new ContractAuditSettings { DisabledAuditTypes = new List<string> { "searchEvents" } });

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.AreEqual(0, (await store.GetAuditSettings()).DisabledAuditTypes.Count);
        Assert.IsTrue(audit.Entries.Single().AccessDenied);
    }

    // ───────── Helpers ─────────

    private static AuditSettingsProvider NewProvider(AuditStore store)
        => new(store, NullLogger<AuditSettingsProvider>.Instance);

    private static AdminImplementation CreateController(IAuditSettingsProvider provider, IAuditLogService audit, bool owner)
    {
        var claims = owner ? new[] { new Claim("groups", "EIP_Management") } : Array.Empty<Claim>();
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")) };
        var accessor = new HttpContextAccessor { HttpContext = context };
        var platform = new FakePlatform("ep-audit");
        var configuration = new ConfigurationBuilder().Build();
        var authorization = new EndpointAuthorizationService(
            accessor,
            platform,
            NullLogger<EndpointAuthorizationService>.Instance,
            configuration,
            new AccessControlSnapshotProvider(new InMemoryMessageStore(), NullLogger<AccessControlSnapshotProvider>.Instance));

        return new AdminImplementation(
            accessor,
            adminService: null!,
            subscriptionAdminService: null!,
            platform,
            configuration,
            audit,
            authorization,
            heartbeatService: null!,
            auditSettings: provider);
    }

    private sealed class AuditStore : InMemoryMessageStore
    {
        public List<MessageAuditType> Written { get; } = new();
        public bool FailSettingsRead { get; set; }
        public int SettingsReads { get; private set; }

        public override Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
        {
            Written.Add(auditEntity.AuditType);
            return base.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId);
        }

        public override Task<StoreAuditSettings> GetAuditSettings()
        {
            SettingsReads++;
            return FailSettingsRead
                ? throw new InvalidOperationException("simulated store outage")
                : base.GetAuditSettings();
        }
    }

    private sealed class FakePlatform : IPlatform
    {
        public FakePlatform(string endpointId) => Endpoints = new[] { new FakeEndpoint(endpointId) };

        public IEnumerable<IEndpoint> Endpoints { get; }
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

    private sealed record AuditCall(MessageAuditType Type, bool AccessDenied, string? Data);

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<AuditCall> Entries { get; } = new();

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditCall(type, accessDenied, data));
            return Task.CompletedTask;
        }
    }
}
