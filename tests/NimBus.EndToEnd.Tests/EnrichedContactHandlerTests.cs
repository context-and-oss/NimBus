#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK.EventHandlers;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Spec 022 Phase 3 Task D — handler logic test for the DataPlatform enriched-contact consumer.
/// Verifies that the crm.contact.enriched.v1 payload (industry, leadScore, rationale) round-trips
/// correctly through the in-memory bus and that the handler deserializes and captures the enriched
/// fields, mirroring the logic in DataPlatform.Adapter.Functions/Handlers/EnrichedContactHandler.cs.
/// </summary>
[TestClass]
public class EnrichedContactHandlerTests
{
    private const string EventTypeId = "crm.contact.enriched.v1";

    /// <summary>
    /// Payload record that mirrors <c>EnrichedContact</c> in the DataPlatform host.
    /// </summary>
    private sealed record EnrichedContact(
        string ContactId,
        string Industry,
        int LeadScore,
        string? Rationale);

    [TestMethod]
    public async Task EnrichedContactHandler_ParsesAllFields_FromJsonPayload()
    {
        // Arrange — a captured-sink handler that replicates the logic of EnrichedContactHandler:
        // read EventJson, deserialize into EnrichedContact, record the fields.
        EnrichedContact? captured = null;

        var fixture = new EndToEndFixture();
        fixture.RegisterDynamicHandler(EventTypeId, () => new DelegateEventJsonHandler((context, ct) =>
        {
            var json = context.MessageContent.EventContent!.EventJson;
            captured = JsonConvert.DeserializeObject<EnrichedContact>(json!);
            return Task.CompletedTask;
        }));

        const string contactId = "C-42";
        const string industry = "Manufacturing";
        const int leadScore = 87;
        const string rationale = "High purchasing signals from CRM activity.";

        var payload = JsonConvert.SerializeObject(new EnrichedContact(contactId, industry, leadScore, rationale));

        // Act
        await fixture.PublishBus.Send(CreateEnrichedEventRequest(EventTypeId, "session-dp-1", payload));
        await fixture.DeliverAll();

        // Assert — handler received and correctly deserialized all enriched fields.
        Assert.IsNotNull(captured, "Handler must have been invoked and captured the payload.");
        Assert.AreEqual(contactId, captured.ContactId, "ContactId must round-trip through the wire.");
        Assert.AreEqual(industry, captured.Industry, "Industry must round-trip through the wire.");
        Assert.AreEqual(leadScore, captured.LeadScore, "LeadScore must round-trip through the wire.");
        Assert.AreEqual(rationale, captured.Rationale, "Rationale must round-trip through the wire.");
    }

    [TestMethod]
    public async Task EnrichedContactHandler_NullRationale_IsAccepted()
    {
        // Rationale is optional — the agent may omit it.
        EnrichedContact? captured = null;

        var fixture = new EndToEndFixture();
        fixture.RegisterDynamicHandler(EventTypeId, () => new DelegateEventJsonHandler((context, ct) =>
        {
            var json = context.MessageContent.EventContent!.EventJson;
            captured = JsonConvert.DeserializeObject<EnrichedContact>(json!);
            return Task.CompletedTask;
        }));

        var payload = JsonConvert.SerializeObject(new EnrichedContact("C-99", "Retail", 55, null));

        await fixture.PublishBus.Send(CreateEnrichedEventRequest(EventTypeId, "session-dp-2", payload));
        await fixture.DeliverAll();

        Assert.IsNotNull(captured);
        Assert.IsNull(captured.Rationale, "Null rationale must be preserved as null.");
        Assert.AreEqual(55, captured.LeadScore);
    }

    [TestMethod]
    public async Task EnrichedContactHandler_EventSettles_WithResolutionResponse()
    {
        // The enriched-contact handler must settle correctly — producing a ResolutionResponse,
        // not an error or stuck message. This mirrors DynamicEventRoutingTests.
        var fixture = new EndToEndFixture();
        fixture.RegisterDynamicHandler(EventTypeId, () => new DelegateEventJsonHandler((context, ct) =>
            Task.CompletedTask));

        await fixture.PublishBus.Send(CreateEnrichedEventRequest(EventTypeId, "session-dp-3", "{\"contactId\":\"C-1\",\"industry\":\"Tech\",\"leadScore\":70}"));
        await fixture.DeliverAll();

        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count, "Exactly one ResolutionResponse must be produced.");
        Assert.AreEqual(MessageType.ResolutionResponse, responses[0].MessageType);
        Assert.AreEqual(EventTypeId, responses[0].MessageContent.EventContent!.EventTypeId);
    }

    private static IMessage CreateEnrichedEventRequest(string eventTypeId, string sessionId, string json)
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
