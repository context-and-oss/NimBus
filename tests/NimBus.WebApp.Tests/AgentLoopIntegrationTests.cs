#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using IMessage = global::NimBus.Core.Messages.IMessage;
using MessageEntity = global::NimBus.MessageStore.MessageEntity;
using UnresolvedEvent = global::NimBus.MessageStore.UnresolvedEvent;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Composition integration tests for the agent loop: define → publish → receive → settle
/// all running on ONE <see cref="AgentImplementation"/> instance over SHARED in-memory state.
///
/// These tests prove that the endpoints compose — a schema defined via PostAgentEventTypes
/// is visible to PostAgentPublish for validation, a parked row is reachable from
/// GetAgentReceive, and the coordinates handed back by receive can be passed directly into
/// PostAgentSettle.
///
/// The park boundary (the hosted Agent Zone subscriber that writes Pending+Handoff rows) is
/// represented by direct <see cref="InMemoryMessageStore.UploadPendingMessage"/> calls, exactly
/// as AgentImplementationTests does via its SeedPendingHandoff helper. That subscriber is
/// built in Phase 3 (Task C) and its park behaviour is separately covered in
/// NimBus.EndToEnd.Tests/AgentParkAndSettleTests.cs.
/// </summary>
[TestClass]
public class AgentLoopIntegrationTests
{
    // ── Constants ────────────────────────────────────────────────────────────

    // Default zone id resolved when IConfiguration is null.
    private const string ZoneId = AgentZone.DefaultAgentZoneEndpointId;

    // ── Fake helpers (same pattern as AgentImplementationTests inner classes) ──

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;
        public FakePlatform(params string[] endpointIds)
            => _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id) { Id = id; }
        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }

    private sealed class CapturingPublisher : IAgentEventPublisher
    {
        public IMessage? Published { get; private set; }
        public int CallCount { get; private set; }
        public Task PublishAsync(IMessage message, CancellationToken cancellationToken = default)
        {
            Published = message;
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHandoffClientFactory : IHandoffClientFactory
    {
        public HandoffSettlement? CompletedCoords { get; private set; }
        public string? CompletedEndpoint { get; private set; }
        public int CompleteCount { get; private set; }

        public HandoffSettlement? FailedCoords { get; private set; }
        public string? FailedEndpoint { get; private set; }
        public int FailCount { get; private set; }

        public IHandoffClient ForEndpoint(string endpointId) => new Client(this, endpointId);

        private sealed class Client : IHandoffClient
        {
            private readonly CapturingHandoffClientFactory _owner;
            private readonly string _endpoint;

            public Client(CapturingHandoffClientFactory owner, string endpoint)
            {
                _owner = owner;
                _endpoint = endpoint;
            }

            public Task CompleteAsync(HandoffSettlement coords, object? result = null, CancellationToken cancellationToken = default)
            {
                _owner.CompletedCoords = coords;
                _owner.CompletedEndpoint = _endpoint;
                _owner.CompleteCount++;
                return Task.CompletedTask;
            }

            public Task FailAsync(HandoffSettlement coords, string errorText, string? errorType = null, CancellationToken cancellationToken = default)
            {
                _owner.FailedCoords = coords;
                _owner.FailedEndpoint = _endpoint;
                _owner.FailCount++;
                return Task.CompletedTask;
            }
        }
    }

    // ── BuildAgent ───────────────────────────────────────────────────────────

    // Builds one AgentImplementation over a single shared InMemoryMessageStore that
    // serves as both IEventSchemaStore (for define/publish) and INimBusMessageStore
    // (for receive/settle).  Mirrors the pattern in AgentImplementationTests.BuildAgent.
    private static (
        AgentImplementation Impl,
        InMemoryMessageStore Store,
        CapturingPublisher Publisher,
        CapturingHandoffClientFactory Handoffs,
        AgentSubscriptionRegistry Registry)
        BuildAgent(params string[] endpointIds)
    {
        var store = new InMemoryMessageStore();
        var platform = new FakePlatform(endpointIds);
        var publisher = new CapturingPublisher();
        var handoffs = new CapturingHandoffClientFactory();
        var registry = new AgentSubscriptionRegistry();
        var audit = new AuditLogService(NullLogger<AuditLogService>.Instance, store);
        var settlement = new HandoffSettlementService(store, audit, new HandoffSettlementTracker(TimeProvider.System), NullLogger<HandoffSettlementService>.Instance);
        var impl = new AgentImplementation(
            store,
            platform,
            publisher,
            store,
            handoffs,
            settlement,
            registry,
            config: null!,              // -> AgentZone.DefaultAgentZoneEndpointId
            httpContextAccessor: null!, // -> CurrentAgentId() falls back to "demo-agent"
            NullLogger<AgentImplementation>.Instance);
        return (impl, store, publisher, handoffs, registry);
    }

    // ── SeedPendingHandoff ───────────────────────────────────────────────────

    // Seeds a Pending+Handoff row exactly as the Agent Zone park subscriber (Task C,
    // Phase 3) would — mirrors AgentImplementationTests.SeedPendingHandoff.
    private static Task SeedPendingHandoff(
        InMemoryMessageStore store,
        string eventId,
        string sessionId,
        string eventTypeId,
        string payload,
        string messageId = "msg-1",
        string correlationId = "corr-1",
        string originatingMessageId = "orig-1")
    {
        return store.UploadPendingMessage(eventId, sessionId, ZoneId, new UnresolvedEvent
        {
            EventTypeId = eventTypeId,
            LastMessageId = messageId,
            CorrelationId = correlationId,
            OriginatingMessageId = originatingMessageId,
            PendingSubStatus = "Handoff",
            MessageContent = new global::NimBus.Core.Messages.MessageContent
            {
                EventContent = new global::NimBus.Core.Messages.EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = payload,
                },
            },
        });
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 1 — Full loop: define → publish (valid) → publish (invalid) → receive → settle
    // ────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AgentLoop_Define_Publish_Receive_Settle_ComposesOnSharedState()
    {
        var (impl, store, publisher, handoffs, _) = BuildAgent(ZoneId);

        // ── Step 1: Define ────────────────────────────────────────────────────
        // Proves PostAgentEventTypes writes a schema into the shared store so that
        // subsequent Publish calls can validate against it in the same instance.
        const string eventTypeId = "crm.contact.enriched.v1";
        const string schema =
            "{\"type\":\"object\",\"required\":[\"industry\"]," +
            "\"properties\":{\"industry\":{\"type\":\"string\"},\"leadScore\":{\"type\":\"integer\"}}}";

        var defineResult = await impl.PostAgentEventTypesAsync(new DefineEventTypeRequest
        {
            EventTypeId = eventTypeId,
            JsonSchema = schema,
            Name = "CRM Contact Enriched v1",
        });
        Assert.IsInstanceOfType(defineResult.Result, typeof(OkObjectResult),
            "Step 1 (Define): must return 200");

        // ── Step 2a: Publish (valid) ──────────────────────────────────────────
        // Proves that the just-defined schema is immediately usable by Publish on
        // the same instance (compose: define → publish). Publisher must be called
        // and the event type id / payload must round-trip correctly.
        const string validPayload = "{\"industry\":\"Manufacturing\",\"leadScore\":87}";
        var publishResult = await impl.PostAgentPublishAsync(new AgentPublishRequest
        {
            EventTypeId = eventTypeId,
            Payload = validPayload,
        });
        Assert.IsInstanceOfType(publishResult, typeof(OkResult),
            "Step 2a (Publish valid): must return 200");
        Assert.AreEqual(1, publisher.CallCount,
            "Step 2a: publisher must be called exactly once");
        Assert.AreEqual(eventTypeId, publisher.Published?.EventTypeId,
            "Step 2a: published message EventTypeId must match");
        Assert.IsTrue(
            publisher.Published?.MessageContent?.EventContent?.EventJson?.Contains("leadScore") == true,
            "Step 2a: published EventJson must contain 'leadScore'");

        // ── Step 2b: Publish (invalid — missing required 'industry') ─────────
        // Proves the registered schema actually gates publish: the registry read by
        // Publish is the same shared store that Define wrote to, so validation is
        // live and a violating payload must be rejected.
        var invalidPublishResult = await impl.PostAgentPublishAsync(new AgentPublishRequest
        {
            EventTypeId = eventTypeId,
            Payload = "{\"leadScore\":42}",   // missing required "industry"
        });
        Assert.IsInstanceOfType(invalidPublishResult, typeof(BadRequestObjectResult),
            "Step 2b (Publish invalid): must return 400 when required 'industry' is absent");
        Assert.AreEqual(1, publisher.CallCount,
            "Step 2b: publisher must NOT be called again after a schema-violating payload");

        // ── Step 3: Receive ───────────────────────────────────────────────────
        // Seed a Pending+Handoff row as the Agent Zone park subscriber would, then
        // call GetAgentReceive. Proves receive reads from the shared store and maps
        // the parked row to AgentReceivedMessage with correct coordinates.
        const string eventId = "evt-enriched-1";
        const string sessionId = "sess-crm-1";
        const string msgId = "msg-99";
        const string corrId = "corr-77";
        const string origId = "orig-55";
        const string parkedPayload = "{\"industry\":\"Manufacturing\",\"leadScore\":87}";

        await SeedPendingHandoff(store,
            eventId: eventId,
            sessionId: sessionId,
            eventTypeId: eventTypeId,
            payload: parkedPayload,
            messageId: msgId,
            correlationId: corrId,
            originatingMessageId: origId);

        var receiveResult = await impl.GetAgentReceiveAsync(eventTypeId: null!, waitSeconds: 0);
        var receiveOk = receiveResult.Result as OkObjectResult;
        Assert.IsNotNull(receiveOk,
            "Step 3 (Receive): must return 200 OkObjectResult for a parked handoff");
        var received = receiveOk!.Value as AgentReceivedMessage;
        Assert.IsNotNull(received, "Step 3: result value must be AgentReceivedMessage");
        Assert.AreEqual(eventTypeId, received!.EventTypeId,
            "Step 3: EventTypeId must match the parked event");
        Assert.AreEqual(parkedPayload, received.Payload,
            "Step 3: Payload must round-trip from the seeded row");
        Assert.IsNotNull(received.Coordinates, "Step 3: Coordinates must be populated");
        Assert.AreEqual(eventId, received.Coordinates!.EventId,
            "Step 3: Coordinates.EventId must match the seeded event id");
        Assert.AreEqual(sessionId, received.Coordinates.SessionId,
            "Step 3: Coordinates.SessionId must match");
        Assert.AreEqual(msgId, received.Coordinates.MessageId,
            "Step 3: Coordinates.MessageId must match");
        Assert.AreEqual(eventTypeId, received.Coordinates.EventTypeId,
            "Step 3: Coordinates.EventTypeId must match");
        Assert.AreEqual(corrId, received.Coordinates.CorrelationId,
            "Step 3: Coordinates.CorrelationId must match");
        Assert.AreEqual(origId, received.Coordinates.OriginatingMessageId,
            "Step 3: Coordinates.OriginatingMessageId must match");

        // ── Step 4: Settle ────────────────────────────────────────────────────
        // Pass the coordinates returned by Receive straight into Settle.
        // Proves the receive→settle coordinate round-trip composes end-to-end on
        // the shared store: the settle looks up the event by EventId, validates it
        // is still Pending+Handoff, builds the MessageEntity, and calls
        // CompleteHandoff on the manager.
        var settleResult = await impl.PostAgentSettleAsync(new AgentSettleRequest
        {
            Coordinates = received.Coordinates,
            Outcome = AgentSettleRequestOutcome.Complete,
        });
        Assert.IsInstanceOfType(settleResult, typeof(OkResult),
            "Step 4 (Settle): must return 200");
        Assert.AreEqual(1, handoffs.CompleteCount,
            "Step 4: CompleteHandoff must be called exactly once");
        Assert.AreEqual(0, handoffs.FailCount,
            "Step 4: FailHandoff must NOT be called on a complete settle");
        Assert.AreEqual(eventId, handoffs.CompletedCoords?.EventId,
            "Step 4: CompletedEntry.EventId must be the seeded event id");
        Assert.AreEqual(ZoneId, handoffs.CompletedEndpoint,
            "Step 4: settled endpoint must be the Agent Zone endpoint id");
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Test 2 — Subscribe filter composes with receive on shared state
    // ────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AgentLoop_Subscribe_Receive_FiltersToSubscribedType_OnSharedState()
    {
        // Proves subscribe→receive filter composition: two parked rows of different
        // event types are in the shared store; subscribe to only one of them; then
        // GetAgentReceive (no explicit eventTypeId param) must return the subscribed
        // row only, not the other one.
        var (impl, store, _, _, registry) = BuildAgent(ZoneId);

        // Seed two parked rows of different event types.
        await SeedPendingHandoff(store, "evt-a", "sess-a", "type.alpha.v1", "{\"a\":1}",
            messageId: "msg-a", correlationId: "corr-a", originatingMessageId: "orig-a");
        await SeedPendingHandoff(store, "evt-b", "sess-b", "type.beta.v1", "{\"b\":2}",
            messageId: "msg-b", correlationId: "corr-b", originatingMessageId: "orig-b");

        // Subscribe only to beta.
        var subscribeResult = await impl.PostAgentSubscribeAsync(new AgentSubscribeRequest
        {
            EventTypeId = "type.beta.v1",
        });
        Assert.IsInstanceOfType(subscribeResult, typeof(OkResult),
            "Subscribe must succeed");
        CollectionAssert.Contains(
            registry.GetSubscriptions("demo-agent").ToArray(),
            "type.beta.v1",
            "Registry must record the beta subscription for the current agent");

        // Receive (no explicit eventTypeId param) → must return the beta row only.
        var receiveResult = await impl.GetAgentReceiveAsync(eventTypeId: null!, waitSeconds: 0);
        var receiveOk = receiveResult.Result as OkObjectResult;
        Assert.IsNotNull(receiveOk,
            "Receive must return 200 when a subscribed type is parked");
        var received = receiveOk!.Value as AgentReceivedMessage;
        Assert.IsNotNull(received);
        Assert.AreEqual("type.beta.v1", received!.EventTypeId,
            "Receive must return the event type the agent subscribed to (beta), not alpha");
    }
}
