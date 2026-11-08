#pragma warning disable CA1707, CA2007
using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
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
/// The resubmit PII gate. Field-level masking makes a masked payload look like
/// valid JSON, so an operator without the PiiReader role could otherwise resubmit
/// what they were shown and overwrite the real sensitive values with the mask
/// token. <see cref="EventImplementation.PostResubmitWithChangesEventIdsAsync"/>
/// must reject that, and must resolve the event type server-side so the check
/// cannot be dodged via the request body.
/// </summary>
[TestClass]
public sealed class EventImplementationResubmitPiiGateTests
{
    private const string EventId = "evt-pii-1";
    private const string TerminalMessageId = "term-pii-1";
    private const string Endpoint = "SubscriberEp";
    private const string CleanJson = "{\"Cpr\":\"010101-1234\",\"OrderId\":\"A-1\"}";

    // A type in the platform with no [Sensitive] members — used to prove the gate
    // does not trust body.EventTypeId.
    public sealed class PlainOrder : NimBus.Core.Events.Event
    {
        public string OrderId { get; set; } = string.Empty;
    }

    private sealed class GateEndpoint : NimBus.Core.Endpoints.Endpoint
    {
        public GateEndpoint()
        {
            Produces<PayloadRedactionTests.OrderPlaced>();
            Produces<PlainOrder>();
        }
    }

    private sealed class GatePlatform : NimBus.Core.Platform
    {
        public GatePlatform() { AddEndpoint(new GateEndpoint()); }
    }

    private static readonly string SensitiveTypeId = nameof(PayloadRedactionTests.OrderPlaced);

    private static EventJsonMasker NewMasker() => new(new GatePlatform());

    private static string MaskedJson() => NewMasker().Mask(SensitiveTypeId, CleanJson);

    [TestMethod]
    public async Task Non_pii_reader_cannot_resubmit_a_masked_payload()
    {
        var (sut, manager) = await CreateSutAsync(canReadPii: false, storedEventTypeId: SensitiveTypeId);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = MaskedJson() }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.IsNull(manager.EventJson, "The masked payload must never reach the manager client.");
    }

    [TestMethod]
    public async Task Pii_reader_may_resubmit_the_same_payload_unmodified()
    {
        var (sut, manager) = await CreateSutAsync(canReadPii: true, storedEventTypeId: SensitiveTypeId);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = MaskedJson() }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.IsNotNull(manager.EventJson);
    }

    [TestMethod]
    public async Task Non_pii_reader_may_resubmit_after_re_entering_sensitive_values()
    {
        var (sut, manager) = await CreateSutAsync(canReadPii: false, storedEventTypeId: SensitiveTypeId);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = CleanJson }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual(CleanJson, manager.EventJson);
    }

    [TestMethod]
    public async Task Sidecar_marker_is_stripped_before_the_payload_is_forwarded()
    {
        // A PiiReader legitimately round-trips a payload that still carries the
        // marker; it must not leak into the re-published event.
        var (sut, manager) = await CreateSutAsync(canReadPii: true, storedEventTypeId: SensitiveTypeId);

        await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = MaskedJson() }, EventId, TerminalMessageId);

        Assert.IsFalse(
            manager.EventJson!.Contains(EventJsonMasker.PiiMaskedMarker, StringComparison.Ordinal),
            "The $piiMasked sidecar must be stripped before forwarding.");
    }

    [TestMethod]
    public async Task Gate_fails_closed_when_the_event_type_cannot_be_resolved()
    {
        var (sut, manager) = await CreateSutAsync(canReadPii: false, storedEventTypeId: null);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventContent = CleanJson }, EventId, TerminalMessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.IsNull(manager.EventJson, "Without a resolvable type the payload cannot be proven clean.");
    }

    [TestMethod]
    public async Task Gate_uses_the_server_side_event_type_not_the_request_body()
    {
        // Strip the sidecar so detection must fall back to the per-field scan, which
        // is type-dependent. Claiming a type with no [Sensitive] members would hide
        // the mask token — the gate must ignore body.EventTypeId and use the stored id.
        var strippedMask = NewMasker().StripMaskedMarker(MaskedJson());
        var (sut, manager) = await CreateSutAsync(canReadPii: false, storedEventTypeId: SensitiveTypeId);

        var result = await sut.PostResubmitWithChangesEventIdsAsync(
            new ResubmitWithChanges { EventTypeId = nameof(PlainOrder), EventContent = strippedMask },
            EventId,
            TerminalMessageId);

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        Assert.IsNull(manager.EventJson, "body.EventTypeId must not be able to bypass the gate.");
    }

    // ---------------- Harness ----------------

    private static async Task<(EventImplementation Sut, CapturingManagerClient Manager)> CreateSutAsync(
        bool canReadPii,
        string? storedEventTypeId)
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            "req-1", NimBus.Core.Messages.MessageType.EventRequest, "2026-06-01T10:00:00Z",
            eventJson: CleanJson, eventTypeId: storedEventTypeId,
            from: "PublisherEp", to: Endpoint));
        await store.StoreMessage(Entity(
            TerminalMessageId, NimBus.Core.Messages.MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            eventTypeId: storedEventTypeId,
            from: Endpoint, to: "Resolver", originatingMessageId: "req-1"));

        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);

        var manager = new CapturingManagerClient();
        var sut = new EventImplementation(
            applicationInsightsService: null!,
            platform: null!,
            manager,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new PiiAuthorizationService(canReadPii),
            adminService: null!,
            serviceBusClient: null!,
            new NoOpAuditLogService(),
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new PayloadRedaction(NewMasker()),
            NewMasker());

        return (sut, manager);
    }

    private static MessageEntity Entity(
        string messageId,
        NimBus.Core.Messages.MessageType type,
        string enqueuedUtc,
        string? eventJson = null,
        string? eventTypeId = null,
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
            EventTypeId = eventTypeId!,
            From = from!,
            To = to!,
            OriginatingMessageId = originatingMessageId!,
            MessageContent = eventJson == null
                ? null!
                : new NimBus.Core.Messages.MessageContent
                {
                    EventContent = new NimBus.Core.Messages.EventContent { EventJson = eventJson, EventTypeId = eventTypeId! },
                },
        };

    private sealed class CapturingManagerClient : IManagerClient
    {
        public string? EventJson { get; private set; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
        {
            EventJson = eventJson;
            return Task.CompletedTask;
        }

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId) => throw new NotSupportedException();
    }

    private sealed class NoOpAuditLogService : IAuditLogService
    {
        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PiiAuthorizationService(bool canReadPii) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(canReadPii);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = canReadPii,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
