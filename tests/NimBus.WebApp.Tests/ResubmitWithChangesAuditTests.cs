#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Messages.PII;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The <c>ResubmitWithChanges</c> audit row written by the Web UI records what changed, not the
/// edited payload: the event type, the SHA-256 of the payload and the JSON paths it adds, removes
/// or changes, on the accepted path and on every refusal.
/// </summary>
[TestClass]
public sealed class ResubmitWithChangesAuditTests
{
    private const string EventId = "evt-audit-1";
    private const string TerminalMessageId = "term-audit-1";
    private const string StoredJson = "{\"Cpr\":\"010101-1234\",\"OrderId\":\"order-original\"}";
    private const string EditedJson = "{\"Cpr\":\"020202-5678\",\"OrderId\":\"order-edited\",\"Note\":\"rush order\"}";

    private static readonly string[] PayloadValues = ["010101-1234", "020202-5678", "order-original", "order-edited", "rush order"];

    private static readonly string EventTypeId = nameof(PayloadRedactionTests.OrderPlaced);

    private sealed class AuditEndpoint : NimBus.Core.Endpoints.Endpoint
    {
        public AuditEndpoint() => Produces<PayloadRedactionTests.OrderPlaced>();
    }

    private sealed class AuditPlatform : NimBus.Core.Platform
    {
        public AuditPlatform() => AddEndpoint(new AuditEndpoint());
    }

    private static EventJsonMasker NewMasker() => new(new AuditPlatform());

    [TestMethod]
    public async Task Accepted_row_holds_the_hash_and_changed_paths_and_no_payload_values()
    {
        var (sut, audit) = await CreateSutAsync(canReadPii: true);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventTypeId = EventTypeId, EventContent = EditedJson }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        var row = audit.Rows.Single(r => r.Type == MessageAuditType.ResubmitWithChanges);
        Assert.IsFalse(row.AccessDenied);
        AssertDescribesEdit(row.Data, OperatorPayloadEdit.Hash(EditedJson),
            "$.Cpr (changed)", "$.OrderId (changed)", "$.Note (added)");
    }

    [TestMethod]
    public async Task Row_refused_for_a_masked_payload_has_the_same_shape()
    {
        var masked = NewMasker().Mask(EventTypeId, StoredJson);
        var (sut, audit) = await CreateSutAsync(canReadPii: false);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = masked }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        var row = audit.Rows.Single(r => r.Type == MessageAuditType.ResubmitWithChanges);
        Assert.IsTrue(row.AccessDenied);
        AssertDescribesEdit(row.Data, OperatorPayloadEdit.Hash(NewMasker().StripMaskedMarker(masked)), "$.Cpr (changed)");
    }

    [TestMethod]
    public async Task Row_refused_for_a_missing_role_has_the_same_shape()
    {
        var (sut, audit) = await CreateSutAsync(canReadPii: true, isContributor: false);

        await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = EditedJson }, EventId, TerminalMessageId));

        var row = audit.Rows.Single(r => r.Type == MessageAuditType.ResubmitWithChanges);
        Assert.IsTrue(row.AccessDenied);
        AssertDescribesEdit(row.Data, OperatorPayloadEdit.Hash(EditedJson),
            "$.Cpr (changed)", "$.OrderId (changed)", "$.Note (added)");
    }

    private static void AssertDescribesEdit(string? data, string expectedHash, params string[] expectedPaths)
    {
        Assert.IsNotNull(data, "The row must describe the edit.");
        foreach (var value in PayloadValues)
            Assert.IsFalse(data.Contains(value, StringComparison.Ordinal), $"The row must not hold the payload value '{value}': {data}");

        var json = JObject.Parse(data);
        Assert.IsNull(json.Property("EventContent", StringComparison.OrdinalIgnoreCase), "The row must not hold the payload.");
        Assert.AreEqual("WebApp", json.Value<string>("channel"));
        Assert.AreEqual(EventTypeId, json.Value<string>("eventTypeId"));
        Assert.AreEqual(expectedHash, json.Value<string>("contentHash"));
        CollectionAssert.AreEquivalent(expectedPaths, json["changedPaths"]!.Values<string>().ToArray());
    }

    // ---------------- Harness ----------------

    private static async Task<(EventImplementation Sut, RecordingAudit Audit)> CreateSutAsync(bool canReadPii, bool isContributor = true)
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            "req-1", NimBus.Core.Messages.MessageType.EventRequest, "2026-06-01T10:00:00Z",
            eventJson: StoredJson, from: "PublisherEp", to: "SubscriberEp"));
        await store.StoreMessage(Entity(
            TerminalMessageId, NimBus.Core.Messages.MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);

        var audit = new RecordingAudit();
        var sut = new EventImplementation(
            applicationInsightsService: null!,
            platform: null!,
            new NoOpManagerClient(),
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new Authorization(canReadPii, isContributor),
            adminService: null!,
            serviceBusClient: null!,
            audit,
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new PayloadRedaction(NewMasker()),
            NewMasker());
        return (sut, audit);
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
            EventTypeId = EventTypeId,
            From = from!,
            To = to!,
            OriginatingMessageId = originatingMessageId!,
            MessageContent = eventJson == null
                ? null!
                : new NimBus.Core.Messages.MessageContent
                {
                    EventContent = new NimBus.Core.Messages.EventContent { EventJson = eventJson, EventTypeId = EventTypeId },
                },
        };

    internal sealed record AuditRow(MessageAuditType Type, bool AccessDenied, string? Data);

    internal sealed class RecordingAudit : IAuditLogService
    {
        public List<AuditRow> Rows { get; } = [];

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Rows.Add(new AuditRow(type, accessDenied, data));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
        {
            Rows.Add(new AuditRow(type, false, data));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpManagerClient : IManagerClient
    {
        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) => Task.CompletedTask;

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId) => throw new NotSupportedException();
    }

    private sealed class Authorization(bool canReadPii, bool isContributor) : IEndpointAuthorizationService
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
