#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests for batch publishing, deterministic message IDs, and event validation.
/// </summary>
[TestClass]
public class BatchAndValidationTests
{
    [TestMethod]
    public async Task PublishBatch_AllEventsHandled()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var events = Enumerable.Range(1, 10)
            .Select(i => new OrderPlaced($"session-{i}") { OrderId = $"BATCH-{i:D3}", Amount = i * 10m })
            .ToList();

        // Act
        await fixture.Publisher.PublishBatch(events.Cast<NimBus.Core.Events.IEvent>(), "batch-correlation");
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(10, handler.ReceivedEvents.Count, "All 10 events should be handled");
        for (int i = 0; i < 10; i++)
        {
            Assert.AreEqual($"BATCH-{i + 1:D3}", handler.ReceivedEvents[i].OrderId);
        }
    }

    [TestMethod]
    public async Task PublishBatch_SharedCorrelationId()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var events = new[]
        {
            new OrderPlaced("s1") { OrderId = "B-001" },
            new OrderPlaced("s2") { OrderId = "B-002" },
        };

        // Act
        await fixture.Publisher.PublishBatch(events, "shared-corr");
        await fixture.DeliverAll();

        // Assert — all events in batch share the same correlation ID
        Assert.AreEqual(2, handler.ReceivedContexts.Count);
        Assert.AreEqual("shared-corr", handler.ReceivedContexts[0].CorrelationId);
        Assert.AreEqual("shared-corr", handler.ReceivedContexts[1].CorrelationId);
    }

    [TestMethod]
    public async Task Publish_DeterministicMessageId_SamePayloadProducesSameId()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var event1 = new OrderPlaced("session-1") { OrderId = "DET-001", Amount = 42m };
        var event2 = new OrderPlaced("session-1") { OrderId = "DET-001", Amount = 42m };

        // Act — publish same event twice with auto-generated IDs
        await fixture.Publisher.Publish(event1, "session-1", "corr-1");
        await fixture.Publisher.Publish(event2, "session-1", "corr-2");

        // Assert — MessageIds are deterministic based on serialized content
        var messages = fixture.PublishBus.SentMessages;
        Assert.AreEqual(2, messages.Count);
        Assert.AreEqual(messages[0].MessageId, messages[1].MessageId,
            "Same event payload should produce same deterministic MessageId");
    }

    [TestMethod]
    public async Task Publish_DifferentPayloads_ProduceDifferentMessageIds()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var event1 = new OrderPlaced("session-1") { OrderId = "DET-001", Amount = 10m };
        var event2 = new OrderPlaced("session-1") { OrderId = "DET-002", Amount = 20m };

        // Act
        await fixture.Publisher.Publish(event1, "session-1", "corr-1");
        await fixture.Publisher.Publish(event2, "session-1", "corr-2");

        // Assert
        var messages = fixture.PublishBus.SentMessages;
        Assert.AreEqual(2, messages.Count);
        Assert.AreNotEqual(messages[0].MessageId, messages[1].MessageId,
            "Different payloads should produce different MessageIds");
    }

    [TestMethod]
    public async Task Publish_InvalidEvent_ThrowsValidationException()
    {
        // Arrange
        var fixture = new EndToEndFixture();

        var invalidEvent = new InvalidEvent(); // RequiredField is null

        // Act & Assert — PublisherClient.Publish calls event.Validate() which throws
        await Assert.ThrowsExactlyAsync<System.ComponentModel.DataAnnotations.ValidationException>(
            () => fixture.Publisher.Publish(invalidEvent));
    }

    [TestMethod]
    public async Task Publish_ValidEvent_DoesNotThrow()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var validEvent = new OrderPlaced("s1") { OrderId = "VALID-001" };

        // Act — should not throw
        await fixture.Publisher.Publish(validEvent);
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
    }

    [TestMethod]
    public async Task PublishBatch_EmptyCollection_NoMessagesPublished()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act — publish empty batch
        await fixture.Publisher.PublishBatch(Enumerable.Empty<NimBus.Core.Events.IEvent>(), "empty-corr");
        await fixture.DeliverAll();

        // Assert — no messages sent, no handler invoked
        Assert.AreEqual(0, fixture.PublishBus.SentMessages.Count, "No messages should be published for empty batch");
        Assert.AreEqual(0, handler.ReceivedEvents.Count, "Handler should not be invoked");
    }

    [TestMethod]
    public async Task PublishBatch_SingleItem_BehavesLikeIndividualPublish()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var events = new[] { new OrderPlaced("s-single") { OrderId = "SINGLE-001" } };

        // Act
        await fixture.Publisher.PublishBatch(events.Cast<NimBus.Core.Events.IEvent>(), "single-corr");
        await fixture.DeliverAll();

        // Assert — single item in batch processed like individual publish
        Assert.AreEqual(1, fixture.PublishBus.SentMessages.Count, "Should publish exactly one message");
        Assert.AreEqual(1, handler.ReceivedEvents.Count, "Handler should receive exactly one event");
        Assert.AreEqual("SINGLE-001", handler.ReceivedEvents[0].OrderId);
        Assert.AreEqual("single-corr", handler.ReceivedContexts[0].CorrelationId);
    }
}
