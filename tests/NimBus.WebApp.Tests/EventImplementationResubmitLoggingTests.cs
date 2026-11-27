#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Resubmit-with-changes must never write the request payload to the log sinks: the
/// payload can carry <c>[Sensitive]</c> values that PiiReader masking hides everywhere
/// else. The request line carries only the ids, and is written only once the caller has
/// passed the Contributor check.
/// </summary>
[TestClass]
public sealed class EventImplementationResubmitLoggingTests
{
    private const string EventId = "evt-log-1";
    private const string TerminalMessageId = "term-log-1";
    private const string Endpoint = "SubscriberEp";
    private const string RequestLinePrefix = "Resubmit message with changes";

    private static readonly string SensitiveTypeId = nameof(PayloadRedactionTests.OrderPlaced);

    private readonly string _cprSentinel = "S2-cpr-" + Guid.NewGuid().ToString("N");
    private readonly string _orderSentinel = "S2-sentinel-" + Guid.NewGuid().ToString("N");

    private string EditedJson => $"{{\"Cpr\":\"{_cprSentinel}\",\"OrderId\":\"{_orderSentinel}\"}}";

    private sealed class LogEndpoint : NimBus.Core.Endpoints.Endpoint
    {
        public LogEndpoint() { Produces<PayloadRedactionTests.OrderPlaced>(); }
    }

    private sealed class LogPlatform : NimBus.Core.Platform
    {
        public LogPlatform() { AddEndpoint(new LogEndpoint()); }
    }

    private static EventJsonMasker NewMasker() => new(new LogPlatform());

    [TestMethod]
    public async Task Allowed_call_logs_only_the_ids()
    {
        var harness = await CreateAsync(isContributor: true, canReadPii: true);

        var result = await harness.Sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = EditedJson }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        AssertNoPayloadLogged(harness.Logger, EditedJson);

        var requestLines = RequestLines(harness.Logger);
        Assert.HasCount(1, requestLines);
        var line = requestLines[0];
        Assert.AreEqual(LogLevel.Information, line.Level);
        Assert.AreEqual(EventId, line.State["EventId"]);
        Assert.AreEqual(TerminalMessageId, line.State["MessageId"]);
        Assert.IsFalse(line.State.ContainsKey("Body"), "The request line must not carry the body.");
        Assert.IsFalse(line.State.ContainsKey("ContentHash"), "This line logs only the ids.");
    }

    [TestMethod]
    public async Task Refused_403_call_writes_no_request_line_and_no_payload()
    {
        var harness = await CreateAsync(isContributor: false, canReadPii: true);

        var result = await harness.Sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = EditedJson }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<ForbidResult>(result);
        Assert.IsTrue(harness.Audit.Rows.Single().AccessDenied, "The access-denied audit row is still written.");
        AssertNoPayloadLogged(harness.Logger, EditedJson);
        Assert.IsEmpty(RequestLines(harness.Logger), "No request line before the role check passes.");
    }

    [TestMethod]
    public async Task Pii_gate_400_call_writes_no_payload()
    {
        var harness = await CreateAsync(isContributor: true, canReadPii: false);
        var masked = NewMasker().Mask(SensitiveTypeId, EditedJson);

        var result = await harness.Sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = masked }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        AssertNoPayloadLogged(harness.Logger, masked);
    }

    private void AssertNoPayloadLogged(CapturingLogger logger, string rawContent)
    {
        string[] forbidden = [_cprSentinel, _orderSentinel, rawContent];
        foreach (var entry in logger.Entries)
        {
            var texts = new List<string> { entry.Message };
            texts.AddRange(entry.State.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty));
            foreach (var text in texts)
            {
                foreach (var value in forbidden)
                {
                    Assert.DoesNotContain(value, text, $"Log entry leaks payload: {entry.Message}");
                }
            }
        }
    }

    private static List<CapturedLog> RequestLines(CapturingLogger logger) =>
        logger.Entries.Where(e => e.Message.StartsWith(RequestLinePrefix, StringComparison.Ordinal)).ToList();

    // ---------------- Harness ----------------

    private sealed record Harness(EventImplementation Sut, CapturingLogger Logger, RecordingAuditLogService Audit);

    private static async Task<Harness> CreateAsync(bool isContributor, bool canReadPii)
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            "req-1", NimBus.Core.Messages.MessageType.EventRequest, "2026-06-01T10:00:00Z",
            eventJson: "{\"Cpr\":\"stored\",\"OrderId\":\"stored\"}",
            from: "PublisherEp", to: Endpoint));
        await store.StoreMessage(Entity(
            TerminalMessageId, NimBus.Core.Messages.MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            from: Endpoint, to: "Resolver", originatingMessageId: "req-1"));

        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);

        var logger = new CapturingLogger();
        var audit = new RecordingAuditLogService();
        var sut = new EventImplementation(
            applicationInsightsService: null!,
            platform: null!,
            new AcceptingManagerClient(),
            handoffClientFactory: null!,
            logger,
            store,
            new RoleAuthorizationService(isContributor, canReadPii),
            adminService: null!,
            serviceBusClient: null!,
            audit,
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new PayloadRedaction(NewMasker()),
            NewMasker());

        return new Harness(sut, logger, audit);
    }

    private static MessageEntity Entity(
        string messageId,
        NimBus.Core.Messages.MessageType type,
        string enqueuedUtc,
        string? eventJson = null,
        string? from = null,
        string? to = null,
        string? originatingMessageId = null) =>
        new()
        {
            EventId = EventId,
            MessageId = messageId,
            SessionId = "sess-1",
            MessageType = type,
            EnqueuedTimeUtc = DateTime.Parse(enqueuedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            EventTypeId = SensitiveTypeId,
            From = from!,
            To = to!,
            OriginatingMessageId = originatingMessageId!,
            MessageContent = eventJson == null
                ? null!
                : new NimBus.Core.Messages.MessageContent
                {
                    EventContent = new NimBus.Core.Messages.EventContent { EventJson = eventJson, EventTypeId = SensitiveTypeId },
                },
        };

    private sealed record CapturedLog(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

    /// <summary>Records every entry, at every level, with its structured state.</summary>
    private sealed class CapturingLogger : ILogger<EventImplementation>
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                : [];
            _entries.Enqueue(new CapturedLog(logLevel, formatter(state, exception), values));
        }
    }

    private sealed class AcceptingManagerClient : IManagerClient
    {
        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) => Task.CompletedTask;

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId) => throw new NotSupportedException();
    }

    private sealed record AuditRow(MessageAuditType Type, bool AccessDenied, string? Data);

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<AuditRow> Rows { get; } = [];

        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            Rows.Add(new AuditRow(type, accessDenied, data));
            return Task.CompletedTask;
        }
    }

    private sealed class RoleAuthorizationService(bool isContributor, bool canReadPii) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(isContributor);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(canReadPii);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = isContributor ? AccessRole.Contributor : AccessRole.Reader,
            IsPiiReader = canReadPii,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
