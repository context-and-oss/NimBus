#pragma warning disable CA1707, CA2007
using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Behavior of <see cref="EventImplementation.PostResubmitEventIdsAsync"/>:
/// plain Resubmit must replay the latest payload-carrying REQUEST message
/// (the original EventRequest, or a later Resubmission/Retry request) rather
/// than the terminal ErrorResponse — which, for a failed hand-off, carries no
/// usable event JSON. Complements
/// <see cref="EventImplementationResubmitPayloadTests"/>, which covers the
/// shared <c>LatestRequestMessageWithPayload</c> selection helper.
/// </summary>
[TestClass]
public sealed class EventImplementationPlainResubmitTests
{
    private const string EventId = "evt-1";
    private const string TerminalMessageId = "term-1";

    [TestMethod]
    public async Task Resubmit_uses_the_parked_payload_when_the_original_CloudEvent_has_no_native_event_json()
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity("req-1", MessageType.EventRequest, "2026-06-01T10:00:00Z",
            eventTypeId: "Demo.Type", from: "PublisherEp", to: "SubscriberEp"));
        await store.StoreMessage(Entity("pending-1", MessageType.PendingHandoffResponse, "2026-06-01T10:00:01Z",
            eventJson: "{\"v\":2}", eventTypeId: "Demo.Type", from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));
        await store.StoreMessage(Entity(TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T10:00:02Z",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));
        var manager = new CapturingManagerClient();
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);
        var result = await CreateSut(store, manager).PostResubmitEventIdsAsync(EventId, TerminalMessageId);
        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("{\"v\":2}", manager.EventJson);
        Assert.AreEqual("Demo.Type", manager.EventTypeId);
        Assert.AreEqual("SubscriberEp", manager.Endpoint);
    }

    [TestMethod]
    public async Task Resubmit_replays_the_original_EventRequest_payload_for_a_failed_handoff()
    {
        // Failed hand-off history: the terminal ErrorResponse (which lastMessageId
        // points at) carries no event JSON — the original EventRequest must win.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            "req-1", MessageType.EventRequest, "2026-06-01T23:36:50Z",
            eventJson: "{\"Fail\":true}", eventTypeId: "Demo.Type",
            from: "PublisherEp", to: "SubscriberEp"));
        await store.StoreMessage(Entity(
            "pending-1", MessageType.PendingHandoffResponse, "2026-06-01T23:36:51Z",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));
        await store.StoreMessage(Entity(
            TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T23:37:02Z",
            eventTypeId: "Demo.Type",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));

        var manager = new CapturingManagerClient();
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("{\"Fail\":true}", manager.EventJson);
        Assert.AreEqual("Demo.Type", manager.EventTypeId);
        Assert.AreEqual("SubscriberEp", manager.Endpoint);
    }

    [TestMethod]
    public async Task Resubmit_prefers_the_latest_payload_carrying_request()
    {
        // A prior resubmission supersedes the original EventRequest as the
        // payload to replay.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            "req-1", MessageType.EventRequest, "2026-06-01T10:00:00Z",
            eventJson: "{\"v\":1}", eventTypeId: "Demo.Type",
            from: "PublisherEp", to: "SubscriberEp"));
        await store.StoreMessage(Entity(
            "resub-1", MessageType.ResubmissionRequest, "2026-06-01T11:00:00Z",
            eventJson: "{\"v\":2}", eventTypeId: "Demo.Type",
            from: "Manager", to: "SubscriberEp"));
        await store.StoreMessage(Entity(
            TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T11:00:05Z",
            eventTypeId: "Demo.Type",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "resub-1"));

        var manager = new CapturingManagerClient();
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("{\"v\":2}", manager.EventJson);
    }

    [TestMethod]
    public async Task Resubmit_falls_back_to_the_terminal_message_when_no_request_carries_a_payload()
    {
        // Non-hand-off shape with a partial history: the terminal ErrorResponse
        // still carries the payload, and behavior is unchanged.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            eventJson: "{\"orig\":1}", eventTypeId: "Demo.Type",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));

        var manager = new CapturingManagerClient();
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("{\"orig\":1}", manager.EventJson);
        Assert.AreEqual("SubscriberEp", manager.Endpoint);
    }

    [TestMethod]
    public async Task Resubmit_resolves_the_event_type_from_the_request_history_when_the_terminal_lacks_it()
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            "req-1", MessageType.EventRequest, "2026-06-01T10:00:00Z",
            eventJson: "{\"v\":1}", eventTypeId: "Demo.Type",
            from: "PublisherEp", to: "SubscriberEp"));
        await store.StoreMessage(Entity(
            TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));

        var manager = new CapturingManagerClient();
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("Demo.Type", manager.EventTypeId);
        Assert.AreEqual("{\"v\":1}", manager.EventJson);
    }

    [TestMethod]
    public async Task Resubmit_self_originating_terminal_targets_To_case_insensitively()
    {
        // The wire default is Constants.Self = "self", but the platform compares
        // case-insensitively everywhere (ResponseService, PublisherClient). The
        // WebApp historically used a case-SENSITIVE check here — "Self" must
        // route to the terminal's To endpoint, not fall through to From.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            eventJson: "{\"orig\":1}", eventTypeId: "Demo.Type",
            from: "PublisherEp", to: "SubscriberEp", originatingMessageId: "Self"));

        var manager = new CapturingManagerClient();
        await OperatorCommandTestRows.SeedRowForMessageAsync(store, EventId, TerminalMessageId);
        var sut = CreateSut(store, manager);

        var result = await sut.PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.AreEqual("SubscriberEp", manager.Endpoint,
            "A self-originating terminal must target its To endpoint regardless of casing");
    }

    [TestMethod]
    public async Task Resubmit_of_a_message_that_is_no_longer_the_latest_attempt_returns_409()
    {
        // The page loaded term-1, but the endpoint has since failed again (term-2):
        // replaying the old attempt would act on a state the operator never saw.
        var store = new InMemoryMessageStore();
        await store.StoreMessage(Entity(
            TerminalMessageId, MessageType.ErrorResponse, "2026-06-01T10:00:05Z",
            eventJson: "{\"orig\":1}", eventTypeId: "Demo.Type",
            from: "SubscriberEp", to: "Resolver", originatingMessageId: "req-1"));
        await OperatorCommandTestRows.SeedFailedRowAsync(store, "SubscriberEp", EventId, "sess-1", "term-2");

        var manager = new CapturingManagerClient();
        var result = await CreateSut(store, manager).PostResubmitEventIdsAsync(EventId, TerminalMessageId);

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
        Assert.IsNull(manager.Endpoint, "Nothing may be sent for a stale message.");
    }

    private static MessageEntity Entity(
        string messageId,
        MessageType type,
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
                : new MessageContent { EventContent = new EventContent { EventJson = eventJson, EventTypeId = eventTypeId! } },
        };

    private static EventImplementation CreateSut(InMemoryMessageStore store, IManagerClient managerClient) =>
        new(
            applicationInsightsService: null!,
            platform: null!,
            managerClient,
            handoffClientFactory: null!,  // resubmit/skip paths never touch handoff settlement
            NullLogger<EventImplementation>.Instance,
            store,
            new AllowAllAuthorizationService(),
            adminService: null!,
            serviceBusClient: null!,
            new NoOpAuditLogService(),
            handoffSettlement: null!,   // resubmit/skip paths never touch handoff settlement
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);

    private sealed class CapturingManagerClient : IManagerClient
    {
        public string? Endpoint { get; private set; }
        public string? EventTypeId { get; private set; }
        public string? EventJson { get; private set; }

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
        {
            Endpoint = endpoint;
            EventTypeId = eventTypeId;
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

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
