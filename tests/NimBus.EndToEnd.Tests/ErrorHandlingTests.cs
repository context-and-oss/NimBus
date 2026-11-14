#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests error handling paths: non-transient exceptions, transient exceptions,
/// and retry policies through the full pipeline.
/// </summary>
[TestClass]
public class ErrorHandlingTests
{
    [TestMethod]
    public async Task Publish_HandlerThrowsException_ErrorResponseSentAndSessionBlocked()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Processing failed")
        };
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("session-err") { OrderId = "ORD-ERR" };

        // Act
        await fixture.Publisher.Publish(@event);
        var results = await fixture.DeliverAllWithResults();

        // Assert — StrictMessageHandler wraps in EventContextHandlerException, sends error response
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Session.WasCompleted, "Message should still be completed after error");

        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.ErrorResponse, responses[0].MessageType);
    }

    [TestMethod]
    public async Task Publish_HandlerThrowsException_ExceptionPropagatesThroughPipeline()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Boom")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-X" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — the exception is re-thrown as EventContextHandlerException
        // which is caught by MessageHandler (no further propagation beyond DeliverAllWithResults)
        Assert.AreEqual(1, results.Count);
        // MessageHandler catches EventContextHandlerException (line 78-79), so no external exception
        Assert.IsNull(results[0].Exception, "MessageHandler should swallow EventContextHandlerException");
    }

    [TestMethod]
    public async Task Publish_HandlerThrowsTransientException_MessageAbandoned()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new TransientException("Timeout", new TimeoutException())
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-transient") { OrderId = "ORD-T" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — TransientException in StrictMessageHandler.HandleEventContent
        // propagates to MessageHandler which abandons (no-op in NimBus) and does NOT complete
        Assert.AreEqual(1, results.Count);
        Assert.IsFalse(results[0].Session.WasCompleted, "Message should not be completed on transient error");
        Assert.IsFalse(results[0].Session.WasDeadLettered, "Message should not be dead-lettered on transient error");
    }

    [TestMethod]
    public async Task Publish_WithRetryPolicy_RetryResponseSentOnFailure()
    {
        // Arrange
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(1)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Retriable failure")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-retry") { OrderId = "ORD-R" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — StrictMessageHandler sends ErrorResponse AND then RetryRequest
        var responses = fixture.ResponseBus.SentMessages;
        Assert.IsTrue(responses.Count >= 1, "Should send at least an error response");
        Assert.IsTrue(responses.Any(r => r.MessageType == MessageType.ErrorResponse), "Should have error response");
        Assert.IsTrue(responses.Any(r => r.MessageType == MessageType.RetryRequest), "Should have retry request");
    }

    [TestMethod]
    public async Task Publish_SuccessAfterSetup_NoErrorResponse()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler(); // No exception
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-ok") { OrderId = "ORD-OK" });
        await fixture.DeliverAll();

        // Assert
        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.ResolutionResponse, responses[0].MessageType, "Success path sends ResolutionResponse only");
    }

    // ── Retry backoff strategy tests ─────────────────────────────────────

    [TestMethod]
    public async Task Publish_WithLinearBackoff_CorrectDelayCalculated()
    {
        // Arrange — Linear: delay = baseDelay * (attempt + 1). attempt=0 → 2 * 1 = 2 minutes
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            Strategy = BackoffStrategy.Linear,
            BaseDelay = TimeSpan.FromMinutes(2)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("fail")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-linear") { OrderId = "ORD-LIN" });
        await fixture.DeliverAllWithResults();

        // Assert — delay for attempt 0 with Linear: 2min * (0+1) = 2 minutes
        var retryRecord = fixture.ResponseBus.SentMessagesWithDelay
            .Single(r => r.Message.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(2, retryRecord.EnqueueDelay, "Linear backoff at attempt 0: baseDelay * 1 = 2 minutes");
    }

    [TestMethod]
    public async Task Publish_WithExponentialBackoff_CorrectDelayCalculated()
    {
        // Arrange — Exponential: delay = baseDelay * 2^attempt. attempt=0 → 3 * 1 = 3 minutes
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromMinutes(3)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("fail")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-exp") { OrderId = "ORD-EXP" });
        await fixture.DeliverAllWithResults();

        // Assert — delay for attempt 0 with Exponential: 3min * 2^0 = 3 minutes
        var retryRecord = fixture.ResponseBus.SentMessagesWithDelay
            .Single(r => r.Message.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(3, retryRecord.EnqueueDelay, "Exponential backoff at attempt 0: baseDelay * 2^0 = 3 minutes");
    }

    [TestMethod]
    public async Task Publish_WithMaxDelayCap_DelayDoesNotExceedMaxDelay()
    {
        // Arrange — Exponential with BaseDelay=10 and MaxDelay=5: delay capped to 5
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromMinutes(10),
            MaxDelay = TimeSpan.FromMinutes(5)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("fail")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-cap") { OrderId = "ORD-CAP" });
        await fixture.DeliverAllWithResults();

        // Assert — 10 * 2^0 = 10 minutes, capped to MaxDelay = 5 minutes
        var retryRecord = fixture.ResponseBus.SentMessagesWithDelay
            .Single(r => r.Message.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(5, retryRecord.EnqueueDelay, "Delay should be capped at MaxDelay");
    }

    [TestMethod]
    public async Task Publish_MaxRetriesExhausted_NoRetryRequestSent()
    {
        // Arrange — MaxRetries=1: first failure retries, second failure does not
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 1,
            BaseDelay = TimeSpan.FromMinutes(1)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("always fails")
        };
        fixture.RegisterHandler(() => handler);

        // Act 1: initial publish → failure → retry sent (retryCount 0 < 1)
        await fixture.Publisher.Publish(new OrderPlaced("s-exhaust") { OrderId = "ORD-EXH" });
        await fixture.DeliverAllWithResults();

        var firstRetry = fixture.ResponseBus.SentMessages
            .Single(r => r.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(1, firstRetry.RetryCount, "First retry should have RetryCount=1");

        // Act 2: re-deliver the retry → failure → no more retries (retryCount 1 >= 1)
        await fixture.PublishBus.Send(CreateRetryRedelivery(firstRetry));
        await fixture.DeliverAllWithResults();

        // Assert — should have 2 ErrorResponses but only 1 RetryRequest total
        var allRetries = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.RetryRequest).ToList();
        Assert.AreEqual(1, allRetries.Count, "Should not send another RetryRequest when MaxRetries exhausted");

        var allErrors = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.ErrorResponse).ToList();
        Assert.AreEqual(2, allErrors.Count, "Should have 2 ErrorResponses (initial + retry failure)");
    }

    // ── Exception-based retry policy tests ───────────────────────────────

    [TestMethod]
    public async Task Publish_ExceptionMatchingRule_UsesRuleRetryPolicy()
    {
        // Arrange — exception rule matches "timeout" in exception message
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddExceptionRule("timeout", new RetryPolicy
        {
            MaxRetries = 5,
            BaseDelay = TimeSpan.FromMinutes(10)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Connection timeout occurred")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-rule") { OrderId = "ORD-RULE" });
        await fixture.DeliverAllWithResults();

        // Assert — exception contains "timeout" → uses the rule's policy (10min delay)
        var retryRecord = fixture.ResponseBus.SentMessagesWithDelay
            .Single(r => r.Message.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(10, retryRecord.EnqueueDelay, "Should use exception rule's policy delay");
    }

    [TestMethod]
    public async Task Publish_ExceptionNotMatchingRule_FallsBackToEventTypePolicy()
    {
        // Arrange — exception rule for "timeout", but exception message is "null reference"
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddExceptionRule("timeout", new RetryPolicy
        {
            MaxRetries = 5,
            BaseDelay = TimeSpan.FromMinutes(10)
        });
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(2)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Null reference in handler")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-fallback") { OrderId = "ORD-FB" });
        await fixture.DeliverAllWithResults();

        // Assert — "Null reference" doesn't match "timeout" → falls back to event-type policy (2min)
        var retryRecord = fixture.ResponseBus.SentMessagesWithDelay
            .Single(r => r.Message.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(2, retryRecord.EnqueueDelay, "Should fall back to event-type policy delay");
    }

    // ── Retry count propagation tests ────────────────────────────────────

    [TestMethod]
    public async Task Publish_RetryResponse_HasRetryCountOne()
    {
        // Arrange
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(1)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("fail")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-rc") { OrderId = "ORD-RC" });
        await fixture.DeliverAllWithResults();

        // Assert — first retry should have RetryCount=1
        var retry = fixture.ResponseBus.SentMessages
            .Single(r => r.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(1, retry.RetryCount, "First RetryRequest should have RetryCount=1");
    }

    [TestMethod]
    public async Task Publish_RetryRequestRedelivered_RetryCountIncrementsAgain()
    {
        // Arrange
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 5,
            BaseDelay = TimeSpan.FromMinutes(1)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("always fails")
        };
        fixture.RegisterHandler(() => handler);

        // Act 1: initial failure → RetryRequest with RetryCount=1
        await fixture.Publisher.Publish(new OrderPlaced("s-rc2") { OrderId = "ORD-RC2" });
        await fixture.DeliverAllWithResults();

        var firstRetry = fixture.ResponseBus.SentMessages
            .Single(r => r.MessageType == MessageType.RetryRequest);
        Assert.AreEqual(1, firstRetry.RetryCount);

        // Act 2: re-deliver first retry → RetryRequest with RetryCount=2
        await fixture.PublishBus.Send(CreateRetryRedelivery(firstRetry));
        await fixture.DeliverAllWithResults();

        var secondRetry = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.RetryRequest)
            .OrderBy(r => r.RetryCount)
            .Last();
        Assert.AreEqual(2, secondRetry.RetryCount, "Second RetryRequest should have RetryCount=2");
    }

    // ── Response metadata integrity tests ────────────────────────────────

    [TestMethod]
    public async Task Publish_ErrorResponse_PreservesResponseMetadata()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("test error")
        };
        fixture.RegisterHandler(() => handler);

        var @event = new OrderPlaced("s-meta") { OrderId = "ORD-META" };
        var messageId = "msg-meta-test";

        // Act
        await fixture.Publisher.Publish(@event, "s-meta", "corr-meta", messageId);
        var results = await fixture.DeliverAllWithResults();

        // Assert
        var errorResponse = fixture.ResponseBus.SentMessages
            .Single(r => r.MessageType == MessageType.ErrorResponse);

        Assert.AreEqual("s-meta", errorResponse.SessionId, "SessionId should be preserved");
        Assert.AreEqual(messageId, errorResponse.CorrelationId, "CorrelationId is set to original MessageId");
        Assert.AreEqual(messageId, errorResponse.ParentMessageId, "ParentMessageId should be original MessageId");
        Assert.AreEqual(messageId, errorResponse.OriginatingMessageId, "OriginatingMessageId should be original MessageId when original was 'self'");
        Assert.AreEqual("OrderPlaced", errorResponse.EventTypeId, "EventTypeId should be preserved");
        Assert.AreEqual(results[0].Context.EventId, errorResponse.EventId, "EventId should be preserved");
    }

    [TestMethod]
    public async Task Publish_RetryRequest_PreservesResponseMetadata()
    {
        // Arrange
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(1)
        });

        var fixture = new EndToEndFixture(retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("test error")
        };
        fixture.RegisterHandler(() => handler);

        var messageId = "msg-retry-meta";

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-rmeta") { OrderId = "ORD-RM" }, "s-rmeta", "corr-rm", messageId);
        var results = await fixture.DeliverAllWithResults();

        // Assert
        var retryRequest = fixture.ResponseBus.SentMessages
            .Single(r => r.MessageType == MessageType.RetryRequest);

        Assert.AreEqual("s-rmeta", retryRequest.SessionId, "SessionId should be preserved");
        Assert.AreEqual(messageId, retryRequest.CorrelationId, "CorrelationId is set to original MessageId");
        Assert.AreEqual(messageId, retryRequest.ParentMessageId, "ParentMessageId should be original MessageId");
        Assert.AreEqual(messageId, retryRequest.OriginatingMessageId, "OriginatingMessageId should be original MessageId");
        Assert.AreEqual("OrderPlaced", retryRequest.EventTypeId, "EventTypeId should be preserved");
        Assert.AreEqual(results[0].Context.EventId, retryRequest.EventId, "EventId should be preserved");
        Assert.AreEqual(Constants.RetryId, retryRequest.To, "RetryRequest should be addressed to Retry");
    }

    // ── Permanent Failure Classification ───────────────────────────────

    [TestMethod]
    public async Task Publish_PermanentFailure_DeadLetteredImmediatelyNoRetry()
    {
        // Arrange — handler throws ArgumentException (permanent by default)
        var classifier = new DefaultPermanentFailureClassifier();
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.SetDefaultPolicy(new RetryPolicy { MaxRetries = 5, Strategy = BackoffStrategy.Fixed, BaseDelay = TimeSpan.FromMinutes(1) });
        var fixture = new EndToEndFixture(classifier, retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new ArgumentException("Invalid order data")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-permanent") { OrderId = "ORD-P1" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — dead-lettered immediately, no retry response
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Session.WasDeadLettered, "Permanent failure should be dead-lettered");
        Assert.IsFalse(fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.RetryRequest),
            "No RetryRequest should be sent for permanent failures");
    }

    [TestMethod]
    public async Task Publish_FormatException_DeadLetteredImmediately()
    {
        // Arrange — FormatException is permanent by default
        var classifier = new DefaultPermanentFailureClassifier();
        var fixture = new EndToEndFixture(classifier);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new FormatException("Invalid date format")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-format") { OrderId = "ORD-FMT" });
        var results = await fixture.DeliverAllWithResults();

        // Assert
        Assert.IsTrue(results[0].Session.WasDeadLettered, "FormatException should be dead-lettered immediately");
    }

    [TestMethod]
    public async Task Publish_NonPermanentFailure_NormalRetryFlow()
    {
        // Arrange — HttpRequestException is NOT permanent (transient infra error)
        var classifier = new DefaultPermanentFailureClassifier();
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.SetDefaultPolicy(new RetryPolicy { MaxRetries = 3, Strategy = BackoffStrategy.Fixed, BaseDelay = TimeSpan.FromMinutes(1) });
        var fixture = new EndToEndFixture(classifier, retryProvider);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new System.Net.Http.HttpRequestException("Connection refused")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-http") { OrderId = "ORD-HTTP" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — NOT dead-lettered, retry response sent
        Assert.IsFalse(results[0].Session.WasDeadLettered, "HttpRequestException should NOT be dead-lettered");
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.RetryRequest),
            "RetryRequest should be sent for non-permanent failures");
    }

    [TestMethod]
    public async Task Publish_CustomPermanentType_DeadLettered()
    {
        // Arrange — register a custom exception type as permanent
        var classifier = new DefaultPermanentFailureClassifier();
        classifier.AddPermanentExceptionType<InvalidOperationException>();
        var fixture = new EndToEndFixture(classifier);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Business rule violation")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-custom") { OrderId = "ORD-CUS" });
        var results = await fixture.DeliverAllWithResults();

        // Assert
        Assert.IsTrue(results[0].Session.WasDeadLettered, "Custom permanent exception type should be dead-lettered");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a re-deliverable message from a RetryRequest response.
    /// The ResponseService doesn't set MessageId or From on retry responses
    /// (in production, the Retry service does this), so we must set them for in-memory re-delivery.
    /// </summary>
    private static Message CreateRetryRedelivery(IMessage retryResponse)
    {
        return new Message
        {
            To = retryResponse.EventTypeId,
            SessionId = retryResponse.SessionId,
            CorrelationId = retryResponse.CorrelationId,
            MessageId = Guid.NewGuid().ToString(),
            EventId = retryResponse.EventId,
            EventTypeId = retryResponse.EventTypeId,
            MessageType = MessageType.RetryRequest,
            From = retryResponse.OriginatingFrom,
            OriginatingFrom = retryResponse.OriginatingFrom,
            OriginatingMessageId = retryResponse.OriginatingMessageId,
            ParentMessageId = retryResponse.ParentMessageId,
            RetryCount = retryResponse.RetryCount,
            MessageContent = retryResponse.MessageContent,
        };
    }
}
