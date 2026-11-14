#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.Core.Events;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Basic end-to-end tests: publish an event and verify it is received,
/// deserialized, and processed through the full NimBus pipeline.
/// </summary>
[TestClass]
public class BasicPublishReceiveTests
{
    [TestMethod]
    public async Task Publish_SingleEvent_HandlerReceivesDeserializedEvent()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-1")
        {
            OrderId = "ORD-001",
            CustomerName = "Alice",
            Amount = 99.95m
        };

        // Act
        await fixture.Publisher.Publish(@event);
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, handler.ReceivedEvents.Count, "Handler should receive exactly one event");
        var received = handler.ReceivedEvents[0];
        Assert.AreEqual("ORD-001", received.OrderId);
        Assert.AreEqual("Alice", received.CustomerName);
        Assert.AreEqual(99.95m, received.Amount);
    }

    [TestMethod]
    public async Task Publish_SingleEvent_ContextHasCorrectEventTypeId()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-1") { OrderId = "ORD-002" };

        // Act
        await fixture.Publisher.Publish(@event);
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, handler.ReceivedContexts.Count);
        Assert.AreEqual("OrderPlaced", handler.ReceivedContexts[0].EventType);
    }

    [TestMethod]
    public async Task Publish_SingleEvent_ContextHasCorrelationId()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-1") { OrderId = "ORD-003" };
        var correlationId = Guid.NewGuid().ToString();

        // Act
        await fixture.Publisher.Publish(@event, "session-1", correlationId);
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, handler.ReceivedContexts.Count);
        Assert.AreEqual(correlationId, handler.ReceivedContexts[0].CorrelationId);
    }

    [TestMethod]
    public async Task Publish_SingleEvent_MessageIsCompletedAfterHandling()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-1") { OrderId = "ORD-004" };

        // Act
        await fixture.Publisher.Publish(@event);
        var results = await fixture.DeliverAllWithResults();

        // Assert
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Session.WasCompleted, "Message should be completed after successful handling");
        Assert.IsFalse(results[0].Session.WasDeadLettered, "Message should not be dead-lettered on success");
    }

    [TestMethod]
    public async Task Publish_SingleEvent_ResolutionResponseIsSent()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-1") { OrderId = "ORD-005" };

        // Act
        await fixture.Publisher.Publish(@event);
        await fixture.DeliverAll();

        // Assert — StrictMessageHandler sends a ResolutionResponse after success
        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count, "Should send exactly one response");
        Assert.AreEqual(MessageType.ResolutionResponse, responses[0].MessageType);
    }

    [TestMethod]
    public async Task Publish_WithExplicitIds_AllIdsRoundTrip()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("my-session") { OrderId = "ORD-006" };
        var correlationId = "corr-123";
        var messageId = "msg-456";

        // Act
        await fixture.Publisher.Publish(@event, "my-session", correlationId, messageId);
        var results = await fixture.DeliverAllWithResults();

        // Assert
        Assert.AreEqual(1, results.Count);
        var ctx = results[0].Context;
        Assert.AreEqual("my-session", ctx.SessionId);
        Assert.AreEqual(correlationId, ctx.CorrelationId);
        Assert.AreEqual(messageId, ctx.MessageId);
        Assert.AreEqual("OrderPlaced", ctx.EventTypeId);
        Assert.AreEqual(MessageType.EventRequest, ctx.MessageType);
    }

    [TestMethod]
    public async Task Publish_MultipleEventsSequentially_AllHandled()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        for (int i = 0; i < 5; i++)
        {
            await fixture.Publisher.Publish(new OrderPlaced($"session-{i}") { OrderId = $"ORD-{i:D3}" });
        }
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(5, handler.ReceivedEvents.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.AreEqual($"ORD-{i:D3}", handler.ReceivedEvents[i].OrderId);
        }
    }

}
