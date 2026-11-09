#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System.Linq;
using System.Text;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="NimBus.SDK.Extensions.ServiceCollectionExtensions.AddNimBusHandoffClient"/>'s
/// DI behaviour. Earlier iterations of the registration used
/// <c>TryAddSingleton</c> for both the options and the client, which silently
/// dropped second-endpoint registrations and routed all settlements to the
/// first endpoint. The keyed-singleton design fixes that without changing the
/// single-endpoint DX.
/// </summary>
[TestClass]
public class HandoffClientRegistrationTests
{
    [TestMethod]
    public void Two_endpoints_each_resolve_their_own_keyed_handoff_client()
    {
        // Arrange — a process that subscribes to / settles handoffs for two
        // separate endpoints in the same DI container.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA="));
        services.AddNimBusHandoffClient("EndpointA");
        services.AddNimBusHandoffClient("EndpointB");

        using var provider = services.BuildServiceProvider();

        // Act — resolve each endpoint's client via the keyed lookup.
        var clientA = provider.GetRequiredKeyedService<IHandoffClient>("EndpointA");
        var clientB = provider.GetRequiredKeyedService<IHandoffClient>("EndpointB");

        // Assert — distinct instances, each bound to the expected endpoint via
        // an end-to-end Send. We don't have an in-memory bus here; instead
        // construct the same shape directly with a recording ISender to verify
        // routing without invoking ServiceBusClient itself.
        Assert.IsNotNull(clientA);
        Assert.IsNotNull(clientB);
        Assert.AreNotSame(clientA, clientB,
            "Each endpoint must resolve to its own IHandoffClient — TryAddSingleton would have collapsed both onto one instance.");
    }

    [TestMethod]
    public void Singular_IHandoffClient_resolves_to_the_first_registered_endpoint()
    {
        // Single-endpoint processes commonly just inject IHandoffClient without
        // any keyed lookup. The non-keyed binding must remain stable for that
        // case — falling back to whichever endpoint registered first.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA="));
        services.AddNimBusHandoffClient("FirstEndpoint");
        services.AddNimBusHandoffClient("SecondEndpoint");

        using var provider = services.BuildServiceProvider();

        var nonKeyed = provider.GetRequiredService<IHandoffClient>();
        var firstKeyed = provider.GetRequiredKeyedService<IHandoffClient>("FirstEndpoint");

        Assert.AreSame(firstKeyed, nonKeyed,
            "Non-keyed IHandoffClient must forward to the first-registered keyed binding.");
    }

    [TestMethod]
    public async Task Each_keyed_client_routes_its_send_to_its_own_endpoint()
    {
        // Direct unit test on HandoffClient — without DI plumbing — to confirm
        // each instance hands the supplied coords + endpoint to the underlying
        // sender unchanged. This is the assertion the silent-mis-routing review
        // comment was about: a process that registers EndpointA + EndpointB must
        // never accidentally route a settlement message for B to A.
        var recorderA = new RecordingSender();
        var recorderB = new RecordingSender();
        var clientA = new HandoffClient(recorderA, new HandoffClientOptions { Endpoint = "EndpointA" });
        var clientB = new HandoffClient(recorderB, new HandoffClientOptions { Endpoint = "EndpointB" });

        var coords = new HandoffSettlement(
            EventId: "evt-1",
            SessionId: "sess-1",
            MessageId: "msg-1",
            EventTypeId: "OrderPlaced",
            CorrelationId: "corr-1",
            OriginatingMessageId: "origin-1");

        await clientA.CompleteAsync(coords, new { ok = true });
        await clientB.FailAsync(coords, "boom", "ErrorType");

        Assert.AreEqual(1, recorderA.Sent.Count);
        Assert.AreEqual(1, recorderB.Sent.Count);
        Assert.AreEqual("EndpointA", recorderA.Sent[0].To);
        Assert.AreEqual("EndpointB", recorderB.Sent[0].To);
        Assert.AreEqual(MessageType.HandoffCompletedRequest, recorderA.Sent[0].MessageType);
        Assert.AreEqual(MessageType.HandoffFailedRequest, recorderB.Sent[0].MessageType);
    }

    [TestMethod]
    public async Task CompleteAsync_with_missing_lineage_fields_sends_with_wire_fallbacks()
    {
        // Lineage fields (CorrelationId, OriginatingMessageId, EventTypeId) may
        // legitimately be null on legacy store rows — the Manager path always
        // passed them through, and HandoffControlMessageFactory falls back
        // OriginatingMessageId ?? ParentMessageId on the wire. The SDK boundary
        // must accept them so settlement of old rows keeps working.
        var recorder = new RecordingSender();
        var client = new HandoffClient(recorder, new HandoffClientOptions { Endpoint = "Endpoint" });
        var coords = new HandoffSettlement(
            EventId: "evt-1",
            SessionId: "sess-1",
            MessageId: "msg-1",
            EventTypeId: null!,
            CorrelationId: null!,
            OriginatingMessageId: null!);

        await client.CompleteAsync(coords);

        Assert.AreEqual(1, recorder.Sent.Count);
        var sent = (Message)recorder.Sent[0];
        Assert.AreEqual("msg-1", sent.OriginatingMessageId,
            "Wire OriginatingMessageId must fall back to the settlement MessageId (ParentMessageId).");
        Assert.IsNull(sent.CorrelationId);

        // Drive the SDK-produced core message through the real transport converter.
        // Legacy rows with missing lineage must remain sendable, and the two identity
        // fields required by the Resolver must retain their factory fallbacks.
        var wire = MessageHelper.ToServiceBusMessage(sent);
        Assert.AreEqual("sess-1", wire.SessionId);
        Assert.IsNull(wire.CorrelationId);
        Assert.AreEqual("evt-1", wire.ApplicationProperties[nameof(UserPropertyName.EventId)]);
        Assert.AreEqual("msg-1", wire.ApplicationProperties[nameof(UserPropertyName.ParentMessageId)]);
        Assert.AreEqual("msg-1", wire.ApplicationProperties[nameof(UserPropertyName.OriginatingMessageId)]);
    }

    [TestMethod]
    public async Task CompleteAsync_HappyPath_PinsAbsoluteWireShape()
    {
        // Absolute wire-shape pin (ported from the retired ManagerClient parity
        // tests in SdkAndManagerTests): HandoffClient is now the only settlement
        // emitter, so this is the contract the Resolver and subscribers rely on.
        var recorder = new RecordingSender();
        var client = new HandoffClient(recorder, new HandoffClientOptions { Endpoint = "billing" });
        var coords = new HandoffSettlement(
            EventId: "event-1",
            SessionId: "session-1",
            MessageId: "message-1",
            EventTypeId: "OrderPlaced",
            CorrelationId: "correlation-1",
            OriginatingMessageId: "originating-1");

        await client.CompleteAsync(coords, "{\"jobId\":\"abc\"}");

        var wire = MessageHelper.ToServiceBusMessage((Message)recorder.Sent.Single());
        Assert.AreEqual("billing", wire.ApplicationProperties["To"]);
        Assert.AreEqual(Constants.ManagerId, wire.ApplicationProperties["From"]);
        Assert.AreEqual("OrderPlaced", wire.ApplicationProperties["EventTypeId"]);
        Assert.AreEqual(MessageType.HandoffCompletedRequest.ToString(), wire.ApplicationProperties["MessageType"]);
        Assert.AreEqual("event-1", wire.ApplicationProperties["EventId"]);
        Assert.AreEqual("originating-1", wire.ApplicationProperties["OriginatingMessageId"]);
        Assert.AreEqual("message-1", wire.ApplicationProperties["ParentMessageId"]);
        Assert.AreEqual("session-1", wire.SessionId);
        Assert.AreEqual("correlation-1", wire.CorrelationId);

        var content = JsonConvert.DeserializeObject<MessageContent>(Encoding.UTF8.GetString(wire.Body.ToArray()));
        Assert.IsNotNull(content);
        Assert.IsNotNull(content.EventContent);
        Assert.AreEqual("OrderPlaced", content.EventContent.EventTypeId);
        Assert.AreEqual("{\"jobId\":\"abc\"}", content.EventContent.EventJson);
        Assert.IsNull(content.ErrorContent);
    }

    [TestMethod]
    public async Task FailAsync_HappyPath_PinsErrorContentWireShape()
    {
        var recorder = new RecordingSender();
        var client = new HandoffClient(recorder, new HandoffClientOptions { Endpoint = "billing" });
        var coords = new HandoffSettlement(
            EventId: "event-1",
            SessionId: "session-1",
            MessageId: "message-1",
            EventTypeId: "OrderPlaced",
            CorrelationId: "correlation-1",
            OriginatingMessageId: "originating-1");

        await client.FailAsync(coords, "downstream rejected", "DownstreamRejected");

        var wire = MessageHelper.ToServiceBusMessage((Message)recorder.Sent.Single());
        Assert.AreEqual("billing", wire.ApplicationProperties["To"]);
        Assert.AreEqual(Constants.ManagerId, wire.ApplicationProperties["From"]);
        Assert.AreEqual(MessageType.HandoffFailedRequest.ToString(), wire.ApplicationProperties["MessageType"]);
        Assert.AreEqual("originating-1", wire.ApplicationProperties["OriginatingMessageId"]);
        Assert.AreEqual("message-1", wire.ApplicationProperties["ParentMessageId"]);
        Assert.AreEqual("session-1", wire.SessionId);
        Assert.AreEqual("correlation-1", wire.CorrelationId);

        var content = JsonConvert.DeserializeObject<MessageContent>(Encoding.UTF8.GetString(wire.Body.ToArray()));
        Assert.IsNotNull(content);
        Assert.IsNotNull(content.ErrorContent);
        Assert.AreEqual("downstream rejected", content.ErrorContent.ErrorText);
        Assert.AreEqual("DownstreamRejected", content.ErrorContent.ErrorType);
        Assert.IsNull(content.EventContent);
    }

    [TestMethod]
    public async Task CompleteAsync_with_missing_required_coords_throws()
    {
        // EventId/SessionId/MessageId are what the wire genuinely needs
        // (session routing + pending-row identity) — these stay strict.
        var client = new HandoffClient(new RecordingSender(), new HandoffClientOptions { Endpoint = "Endpoint" });

        HandoffSettlement Coords(string eventId = "evt-1", string sessionId = "sess-1", string messageId = "msg-1") =>
            new(EventId: eventId, SessionId: sessionId, MessageId: messageId,
                EventTypeId: "OrderPlaced", CorrelationId: "corr-1", OriginatingMessageId: "origin-1");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.CompleteAsync(Coords(eventId: null!)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.CompleteAsync(Coords(sessionId: null!)));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.CompleteAsync(Coords(messageId: null!)));
    }

    /// <summary>
    /// Minimal <see cref="ISender"/> that records every outbound message so the
    /// routing assertions can read back exactly what HandoffClient produced.
    /// </summary>
    private sealed class RecordingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            foreach (var m in messages) Sent.Add(m);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.FromResult(0L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
