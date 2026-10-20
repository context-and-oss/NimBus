#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK.EventHandlers;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Spec 022 Phase 0 — the dynamic-routing gate. Proves a <em>dynamically-typed</em> event
/// (identified only by an <c>EventTypeId</c> string with a JSON body and NO compiled C# IEvent
/// class) publishes, round-trips the Service Bus wire, dispatches to a coded participant, and
/// settles with a ResolutionResponse — exactly like a compiled event, with no special casing.
/// </summary>
[TestClass]
public class DynamicEventRoutingTests
{
    private const string DynamicEventTypeId = "crm.contact.enriched.v1";

    [TestMethod]
    public async Task DynamicallyTypedEvent_RoutesToCodedHandler_AndSettles()
    {
        // Arrange — a coded participant subscribes to a dynamic event type by string id alone;
        // there is no compiled class to deserialize into (it reads the raw JSON).
        var fixture = new EndToEndFixture();
        string? receivedJson = null;
        string? receivedEventTypeId = null;
        fixture.RegisterDynamicHandler(DynamicEventTypeId, () => new DelegateEventJsonHandler((context, ct) =>
        {
            receivedJson = context.MessageContent.EventContent!.EventJson;
            receivedEventTypeId = context.MessageContent.EventContent.EventTypeId;
            return Task.CompletedTask;
        }));

        const string payload = "{\"contactId\":\"C-1\",\"industry\":\"Manufacturing\",\"leadScore\":87}";

        // Act — publish a classless event: just an EventTypeId + JSON, the way the future
        // REST /api/agent/publish will construct the message internally.
        await fixture.PublishBus.Send(CreateDynamicEventRequest(DynamicEventTypeId, "session-1", payload));
        await fixture.DeliverAll();

        // Assert — the coded handler received the dynamic event with its payload intact after a
        // full wire round-trip (serialize → ServiceBusReceivedMessage → MessageContext deserialize).
        Assert.AreEqual(DynamicEventTypeId, receivedEventTypeId, "Handler must be selected by the dynamic EventTypeId.");
        Assert.IsNotNull(receivedJson, "Handler must receive the raw event JSON.");
        StringAssert.Contains(receivedJson, "\"leadScore\":87", "Payload must survive the wire round-trip.");

        // ...and the event settles with a ResolutionResponse carrying the dynamic type id —
        // the Resolver audit (proven separately in NimBus.Resolver.Tests) keys off this string.
        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.ResolutionResponse, responses[0].MessageType);
        Assert.AreEqual(DynamicEventTypeId, responses[0].MessageContent.EventContent!.EventTypeId);
    }

    [TestMethod]
    public async Task DynamicallyTypedEvent_NoSubscriber_DegradesToUnsupported_NotCrash()
    {
        // A dynamic event with no registered handler must degrade gracefully via the existing
        // Unsupported path — never a crash or a stuck message. This is the same guarantee
        // compiled events get; spec 022 reuses it rather than inventing agent-specific handling.
        var fixture = new EndToEndFixture();

        await fixture.PublishBus.Send(CreateDynamicEventRequest("crm.contact.unhandled.v1", "session-2", "{}"));
        await fixture.DeliverAll();

        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.UnsupportedResponse, responses[0].MessageType);
    }

    /// <summary>
    /// Builds an EventRequest for a dynamically-typed event, mirroring exactly the fields a real
    /// publish sets (<see cref="PublisherClient"/>): To/EventTypeId/Session/Correlation/MessageId/
    /// RetryCount + EventContent. From and EventId are injected by the SB rule action, which the
    /// in-memory transport simulates on delivery.
    /// </summary>
    private static Message CreateDynamicEventRequest(string eventTypeId, string sessionId, string json)
    {
        return new Message
        {
            To = eventTypeId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            RetryCount = 0,
            MessageType = MessageType.EventRequest,
            EventTypeId = eventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = json,
                },
            },
        };
    }
}
