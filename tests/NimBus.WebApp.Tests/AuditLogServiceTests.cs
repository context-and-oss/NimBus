#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 008 FR-050 — unit coverage of the centralized audit-write service.
/// Six scenarios: claim-priority resolution and "anonymous" fallback,
/// concurrent message-store + logger write, store-failure absorption,
/// logger-failure absorption, access-denied flag propagation, and full
/// round-trip of the four new entity fields.
/// </summary>
[TestClass]
public sealed class AuditLogServiceTests
{
    // ───────── FR-050 case 1: claim-priority + anonymous fallback ─────────

    [TestMethod]
    public async Task LogAuditAsync_resolves_name_claim_first()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(
            ("name", "Alice Name"),
            (ClaimTypes.Name, "AliceClaimsName"),
            ("preferred_username", "alice@example.com"));

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx);

        Assert.AreEqual("Alice Name", store.Entries.Single().Entity.AuditorName);
    }

    [TestMethod]
    public async Task LogAuditAsync_falls_back_to_ClaimTypes_Name()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(
            (ClaimTypes.Name, "AliceClaimsName"),
            ("preferred_username", "alice@example.com"));

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx);

        Assert.AreEqual("AliceClaimsName", store.Entries.Single().Entity.AuditorName);
    }

    [TestMethod]
    public async Task LogAuditAsync_falls_back_to_preferred_username()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(("preferred_username", "alice@example.com"));

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx);

        Assert.AreEqual("alice@example.com", store.Entries.Single().Entity.AuditorName);
    }

    [TestMethod]
    [DataRow("azp", DisplayName = "v2 token (azp)")]
    [DataRow("appid", DisplayName = "v1 token (appid)")]
    public async Task LogAuditAsync_names_an_app_only_token_after_its_client(string clientClaim)
    {
        // Entra app-only tokens carry no name claim; "anonymous" would hide which agent acted.
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(("oid", "33333333-3333-3333-3333-333333333333"), (clientClaim, "agent-client"), ("roles", "Nimbus.Classify"));

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx);

        Assert.AreEqual("app:agent-client", store.Entries.Single().Entity.AuditorName);
    }

    [TestMethod]
    public async Task LogAuditAsync_prefers_a_name_claim_over_the_client_id()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(("azp", "agent-client"), ("name", "Alice Name"));

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx);

        Assert.AreEqual("Alice Name", store.Entries.Single().Entity.AuditorName);
    }

    [TestMethod]
    public async Task LogAuditAsync_falls_back_to_anonymous_when_no_principal()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = new DefaultHttpContext(); // No User identity claims at all

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx);

        Assert.AreEqual(AuditLogService.AnonymousAuditorName, store.Entries.Single().Entity.AuditorName);
    }

    // ───────── FR-050 case 2: awaits the store AND emits the logger scope ─────────

    [TestMethod]
    public async Task LogAuditAsync_awaits_message_store_and_emits_structured_log()
    {
        var store = new CapturingStore();
        var logger = new RecordingLogger<AuditLogService>();
        var sut = new AuditLogService(logger, store);
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        await sut.LogAuditAsync(MessageAuditType.SearchEvents, ctx,
            eventId: "evt-1", endpointId: "ep-1", eventTypeId: "OrderPlaced",
            data: "{\"filter\":\"x\"}");

        Assert.AreEqual(1, store.Entries.Count, "store write should have run");
        Assert.IsTrue(
            logger.Records.Any(r => r.Message == AuditLogService.StructuredLogMessage),
            "structured audit log message should have been emitted");

        // The scope dictionary keys must match the entity property names so KQL
        // queries can pivot on customDimensions.<FieldName>.
        var scope = logger.Records.Single(r => r.Message == AuditLogService.StructuredLogMessage).Scope as IReadOnlyDictionary<string, object?>;
        Assert.IsNotNull(scope);
        Assert.IsTrue(scope.ContainsKey(nameof(MessageAuditEntity.AuditorName)));
        Assert.IsTrue(scope.ContainsKey(nameof(MessageAuditEntity.AuditType)));
        Assert.IsTrue(scope.ContainsKey(nameof(MessageAuditEntity.AccessDenied)));
        Assert.IsTrue(scope.ContainsKey(nameof(MessageAuditEntity.Data)));
        Assert.IsTrue(scope.ContainsKey(nameof(MessageAuditEntity.EventId)));
        Assert.IsTrue(scope.ContainsKey(nameof(MessageAuditEntity.EndpointId)));
    }

    // ───────── FR-050 case 3: store write failure is absorbed ─────────

    [TestMethod]
    public async Task LogAuditAsync_swallows_message_store_failure_and_warns()
    {
        var store = new ThrowingStore(new InvalidOperationException("simulated cosmos throttle"));
        var logger = new RecordingLogger<AuditLogService>();
        var sut = new AuditLogService(logger, store);
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        // No exception MUST surface to the caller, even though the underlying
        // store throws. The user's privileged action proceeds.
        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx, eventId: "evt-x");

        Assert.IsTrue(logger.Records.Any(r => r.Level == LogLevel.Warning),
            "a warning must have been logged for the store failure");
    }

    // ───────── Spec 035 Phase 2a: commands that must not run unaudited ─────────

    [TestMethod]
    public async Task LogRequiredAuditAsync_throws_when_the_store_write_fails()
    {
        var store = new ThrowingStore(new InvalidOperationException("simulated cosmos outage"));
        var sut = new AuditLogService(NullLogger(), store);

        await Assert.ThrowsExactlyAsync<AuditUnavailableException>(() =>
            sut.LogRequiredAuditAsync(MessageAuditType.Resubmit, MakeContextWithClaims(("name", "Auditor")), eventId: "evt-r"));
    }

    [TestMethod]
    public async Task LogRequiredAuditAsync_records_a_type_the_selection_switched_off()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store, new EverythingDisabled());
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        await sut.LogAuditAsync(MessageAuditType.SearchEvents, ctx, eventId: "evt-s");
        await sut.LogRequiredAuditAsync(MessageAuditType.Skip, ctx, data: "{\"reason\":\"r\"}", eventId: "evt-s", endpointId: "Crm");

        var entry = store.Entries.Single();
        Assert.AreEqual(MessageAuditType.Skip, entry.Entity.AuditType);
        Assert.AreEqual("Crm", entry.EndpointId);
        Assert.AreEqual("Auditor", entry.Entity.AuditorName);
        Assert.IsFalse(entry.Entity.AccessDenied);
    }

    [TestMethod]
    public void Commands_that_change_a_message_are_always_recorded()
    {
        foreach (var type in new[]
                 {
                     MessageAuditType.Resubmit, MessageAuditType.ResubmitWithChanges, MessageAuditType.Skip,
                     MessageAuditType.ReportEvent, MessageAuditType.CommandNotSent,
                 })
        {
            Assert.Contains(type, AuditSettingsProvider.AlwaysRecorded);
            Assert.DoesNotContain(type, AuditSettingsProvider.Configurable);
        }
    }

    private sealed class EverythingDisabled : IAuditSettingsProvider
    {
        public Task<bool> IsRecordedAsync(MessageAuditType type) => Task.FromResult(false);

        public Task<IReadOnlySet<MessageAuditType>> GetDisabledAsync() =>
            Task.FromResult<IReadOnlySet<MessageAuditType>>(new HashSet<MessageAuditType>(Enum.GetValues<MessageAuditType>()));

        public Task<IReadOnlySet<MessageAuditType>> SaveAsync(IEnumerable<MessageAuditType> disabled) =>
            throw new NotSupportedException();
    }

    // ───────── FR-050 case 4: logger (App Insights) failure is absorbed ─────────

    [TestMethod]
    public async Task LogAuditAsync_swallows_logger_failure()
    {
        var store = new CapturingStore();
        var logger = new ThrowingLogger<AuditLogService>();
        var sut = new AuditLogService(logger, store);
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        // BeginScope throwing must NOT surface — best-effort sink.
        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx, eventId: "evt-y");

        Assert.AreEqual(1, store.Entries.Count, "store write should still have run");
    }

    // ───────── FR-050 case 5: accessDenied flag propagation ─────────

    [TestMethod]
    public async Task LogAuditAsync_propagates_accessDenied_flag()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        await sut.LogAuditAsync(MessageAuditType.Resubmit, ctx, accessDenied: true,
            eventId: "evt-z", endpointId: "ep-z");

        var entity = store.Entries.Single().Entity;
        Assert.IsTrue(entity.AccessDenied);
    }

    // ───────── FR-050 case 6: all four new entity fields are populated ─────────

    [TestMethod]
    public async Task LogAuditAsync_populates_four_new_entity_fields()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        await sut.LogAuditAsync(MessageAuditType.SearchEvents, ctx,
            accessDenied: true, data: "{\"filter\":\"OrderPlaced\"}",
            eventId: "evt-1", endpointId: "ep-1", eventTypeId: "OrderPlaced");

        var entry = store.Entries.Single();
        Assert.IsTrue(entry.Entity.AccessDenied);
        Assert.AreEqual("{\"filter\":\"OrderPlaced\"}", entry.Entity.Data);
        Assert.AreEqual("evt-1", entry.Entity.EventId);
        Assert.AreEqual("ep-1", entry.Entity.EndpointId);
        // The endpointId / eventTypeId are also passed through the store
        // contract so SQL provider columns get populated.
        Assert.AreEqual("ep-1", entry.EndpointId);
        Assert.AreEqual("OrderPlaced", entry.EventTypeId);
    }

    // ───────── Data truncation (NFR-004) ─────────

    [TestMethod]
    public async Task LogAuditAsync_truncates_oversized_Data_field()
    {
        var store = new CapturingStore();
        var sut = new AuditLogService(NullLogger(), store);
        var ctx = MakeContextWithClaims(("name", "Auditor"));

        // 4 KB + 1 KB of "x" — must be truncated and suffixed.
        var oversized = new string('x', AuditLogService.DataTruncationLimit + 1024);
        await sut.LogAuditAsync(MessageAuditType.SearchEvents, ctx, data: oversized,
            endpointId: "ep-trunc");

        var entity = store.Entries.Single().Entity;
        Assert.IsTrue(entity.Data!.EndsWith(AuditLogService.TruncationSuffix, StringComparison.Ordinal),
            "truncated marker must be appended");
        Assert.AreEqual(AuditLogService.DataTruncationLimit + AuditLogService.TruncationSuffix.Length,
            entity.Data!.Length);
    }

    // ───────── Helpers ─────────

    private static ILogger<AuditLogService> NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditLogService>.Instance;

    private static HttpContext MakeContextWithClaims(params (string Type, string Value)[] claims)
    {
        var identity = new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Test");
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(identity);
        return ctx;
    }

    private sealed record CapturedEntry(string EventId, MessageAuditEntity Entity, string? EndpointId, string? EventTypeId);

    private sealed class CapturingStore : InMemoryMessageStore
    {
        public List<CapturedEntry> Entries { get; } = new();
        public override Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
        {
            Entries.Add(new CapturedEntry(eventId, auditEntity, endpointId, eventTypeId));
            return base.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId);
        }
    }

    private sealed class ThrowingStore : InMemoryMessageStore
    {
        private readonly Exception _ex;
        public ThrowingStore(Exception ex) { _ex = ex; }
        public override Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
            => throw _ex;
    }

    private sealed record LogRecord(LogLevel Level, string Message, object? Scope);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogRecord> Records { get; } = new();
        private object? _currentScope;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            _currentScope = state;
            return new ScopePopper(this);
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add(new LogRecord(logLevel, formatter(state, exception), _currentScope));
        }

        private sealed class ScopePopper : IDisposable
        {
            private readonly RecordingLogger<T> _owner;
            public ScopePopper(RecordingLogger<T> owner) { _owner = owner; }
            public void Dispose() => _owner._currentScope = null;
        }
    }

    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => throw new InvalidOperationException("simulated logger scope failure");
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
