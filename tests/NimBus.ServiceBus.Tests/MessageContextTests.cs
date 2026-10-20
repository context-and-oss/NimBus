#pragma warning disable CA1707, CA1515, CA2007, CS0618
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Pipeline;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;
using NimBus.Testing;
using Newtonsoft.Json;
using System.Text;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class MessageContextTests
{
    // ── Constructor ──────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullMessage_ThrowsArgumentNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new MessageContext(null!, new FakeServiceBusSession()));
    }

    [TestMethod]
    public void Constructor_NullSession_ThrowsArgumentNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            new MessageContext(new FakeServiceBusMessage(), null!));
    }

    [TestMethod]
    public void Constructor_ValidArgs_SetsIsDeferred()
    {
        var ctx = CreateMessageContext(isDeferred: true);
        Assert.IsTrue(ctx.IsDeferred);
    }

    [TestMethod]
    public void Constructor_DefaultIsDeferred_IsFalse()
    {
        var ctx = CreateMessageContext();
        Assert.IsFalse(ctx.IsDeferred);
    }

    [TestMethod]
    public void DeliveryContext_ExposesWrappedBrokerDeliveryCount()
    {
        var message = new FakeServiceBusMessage { DeliveryCount = 7 };
        IMessageContext context = new MessageContext(message, new FakeServiceBusSession());

        Assert.IsInstanceOfType<IMessageDeliveryContext>(context);
        Assert.AreEqual(7, ((IMessageDeliveryContext)context).DeliveryCount);
    }

    // ── Property access ─────────────────────────────────────────────────

    [TestMethod]
    public void From_WhenPropertyExists_ReturnsValue()
    {
        var ctx = CreateMessageContext(from: "StorefrontEndpoint");
        Assert.AreEqual("StorefrontEndpoint", ctx.From);
    }

    [TestMethod]
    public void From_WhenPropertyMissing_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage(); // no properties set
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.ThrowsExactly<InvalidMessageException>(() => ctx.From);
    }

    [TestMethod]
    public void OriginatingFrom_WhenMissing_ReturnsNull()
    {
        var msg = new FakeServiceBusMessage();
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.IsNull(ctx.OriginatingFrom);
    }

    [TestMethod]
    public void EventTypeId_WhenMissing_ReturnsNull()
    {
        var msg = new FakeServiceBusMessage();
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.IsNull(ctx.EventTypeId);
    }

    [TestMethod]
    public void EventTypeId_WhenPropertyMissingAndBodyIsMalformed_ReturnsNull()
    {
        var msg = CreateDefaultMessage();
        msg.Body = Encoding.UTF8.GetBytes("{not-json");
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.IsNull(ctx.EventTypeId);
    }

    [TestMethod]
    public void EventTypeId_WhenPropertyEmptyAndBodyIsForeignJson_ReturnsNull()
    {
        var msg = CreateDefaultMessage();
        msg.UserProperties[UserPropertyName.EventTypeId.ToString()] = string.Empty;
        msg.Body = Encoding.UTF8.GetBytes("\"foreign-body\"");
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.IsNull(ctx.EventTypeId);
    }

    [TestMethod]
    public void EventTypeId_MalformedBody_ReadMultipleTimes_ParsesOnlyOnce()
    {
        var msg = CreateDefaultMessage();
        msg.Body = Encoding.UTF8.GetBytes("{not-json");
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.IsNull(ctx.EventTypeId);
        Assert.IsNull(ctx.EventTypeId);
        Assert.IsNull(ctx.EventTypeId);

        Assert.AreEqual(1, msg.BodyReadCount, "A failed body parse should be memoized per context.");
    }

    [TestMethod]
    public async Task SubscriberPipeline_LegacyBodyOnlyEventTypeId_ReachesTypedHandlerWithNormalizedMetadata()
    {
        var session = new FakeServiceBusSession();
        var context = CreateMessageContext(
            session: session,
            content: new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = nameof(LegacyBodyOnlyEvent),
                    EventJson = "{}",
                },
            });
        var handler = new RecordingLegacyEventHandler();
        var eventHandlerProvider = new EventHandlerProvider();
        eventHandlerProvider.RegisterHandler<LegacyBodyOnlyEvent>(() => handler);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ValidationMiddleware>();
        });
        using var serviceProvider = services.BuildServiceProvider();
        var pipeline = serviceProvider.GetRequiredService<MessagePipeline>();

        await pipeline.Execute(context, eventHandlerProvider.Handle);

        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(nameof(LegacyBodyOnlyEvent), handler.EventType);
        Assert.AreEqual(0, session.DeadLetterCalls);
    }

    [TestMethod]
    public void MessageType_ValidValue_ParsesCorrectly()
    {
        var ctx = CreateMessageContext(messageType: MessageType.EventRequest);
        Assert.AreEqual(MessageType.EventRequest, ctx.MessageType);
    }

    [TestMethod]
    public void MessageType_InvalidValue_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage();
        msg.UserProperties[UserPropertyName.MessageType.ToString()] = "InvalidType";
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.ThrowsExactly<InvalidMessageException>(() => ctx.MessageType);
    }

    [TestMethod]
    public void RetryCount_ValidInt_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.RetryCount, "3");
        Assert.AreEqual(3, ctx.RetryCount);
    }

    [TestMethod]
    public void RetryCount_Missing_ReturnsNull()
    {
        var msg = new FakeServiceBusMessage();
        msg.UserProperties[UserPropertyName.From.ToString()] = "test";
        msg.UserProperties[UserPropertyName.MessageType.ToString()] = "EventRequest";
        var session = new FakeServiceBusSession();
        var ctx = new MessageContext(msg, session);

        Assert.IsNull(ctx.RetryCount);
    }

    [TestMethod]
    public void RetryCount_NonNumeric_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.RetryCount, "abc");
        Assert.IsNull(ctx.RetryCount);
    }

    [TestMethod]
    public void DeferralSequence_ValidInt_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.DeferralSequence, "5");
        Assert.AreEqual(5, ctx.DeferralSequence);
    }

    [TestMethod]
    public void DeferralSequence_Missing_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        Assert.IsNull(ctx.DeferralSequence);
    }

    [TestMethod]
    public void MessageId_ReturnsValue()
    {
        var msg = new FakeServiceBusMessage { MessageId = "msg-1" };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual("msg-1", ctx.MessageId);
    }

    [TestMethod]
    public void MessageId_Null_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage { MessageId = null! };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.ThrowsExactly<InvalidMessageException>(() => ctx.MessageId);
    }

    [TestMethod]
    public void SessionId_ReturnsValue()
    {
        var msg = new FakeServiceBusMessage { SessionId = "session-1" };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual("session-1", ctx.SessionId);
    }

    [TestMethod]
    public void SessionId_Null_ThrowsInvalidMessage()
    {
        var msg = new FakeServiceBusMessage { SessionId = null! };
        SetDefaultProperties(msg);
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.ThrowsExactly<InvalidMessageException>(() => ctx.SessionId);
    }

    [TestMethod]
    public void OriginatingMessageId_WhenPropertyExists_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.OriginatingMessageId, "orig-1");
        Assert.AreEqual("orig-1", ctx.OriginatingMessageId);
    }

    [TestMethod]
    public void OriginatingMessageId_WhenMissing_ReturnsSelf()
    {
        var ctx = CreateMessageContext();
        Assert.AreEqual(Constants.Self, ctx.OriginatingMessageId);
    }

    [TestMethod]
    public void ParentMessageId_WhenPropertyExists_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.ParentMessageId, "parent-1");
        Assert.AreEqual("parent-1", ctx.ParentMessageId);
    }

    [TestMethod]
    public void ParentMessageId_WhenMissing_ReturnsSelf()
    {
        var ctx = CreateMessageContext();
        Assert.AreEqual(Constants.Self, ctx.ParentMessageId);
    }

    [TestMethod]
    public void MessageContent_ValidJson_Deserializes()
    {
        var content = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        };
        var ctx = CreateMessageContext(content: content);

        Assert.AreEqual("OrderPlaced", ctx.MessageContent.EventContent!.EventTypeId);
    }

    [TestMethod]
    public void MessageContent_ReadMultipleTimes_MaterializesBodyOnceAndReturnsSameInstance()
    {
        var msg = CreateDefaultMessage();
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        var first = ctx.MessageContent;
        var second = ctx.MessageContent;
        var third = ctx.MessageContent;

        Assert.AreSame(first, second, "MessageContent should return the same cached instance");
        Assert.AreSame(second, third, "MessageContent should return the same cached instance");
        Assert.AreEqual(1, msg.BodyReadCount, "Body should be materialized/deserialized only once per context");
    }

    [TestMethod]
    public void ThrottleRetryCount_ValidValue_ReturnsCount()
    {
        var msg = new FakeServiceBusMessage();
        SetDefaultProperties(msg);
        msg.UserProperties["ThrottleRetryCount"] = "3";
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual(3, ctx.ThrottleRetryCount);
    }

    [TestMethod]
    public void ThrottleRetryCount_Missing_ReturnsZero()
    {
        var ctx = CreateMessageContext();
        Assert.AreEqual(0, ctx.ThrottleRetryCount);
    }

    [TestMethod]
    public void ThrottleRetryCount_Invalid_ReturnsZero()
    {
        var msg = new FakeServiceBusMessage();
        SetDefaultProperties(msg);
        msg.UserProperties["ThrottleRetryCount"] = "not-a-number";
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual(0, ctx.ThrottleRetryCount);
    }

    [TestMethod]
    public void DeadLetterReason_WhenMissing_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        Assert.IsNull(ctx.DeadLetterReason);
    }

    [TestMethod]
    public void DeadLetterErrorDescription_WhenMissing_ReturnsNull()
    {
        var ctx = CreateMessageContext();
        Assert.IsNull(ctx.DeadLetterErrorDescription);
    }

    [TestMethod]
    public void HandledFailure_SetThroughTheInterface_IsStored()
    {
        // The interface default discards the value, and the circuit breaker recorder
        // reads a failed RetryRequest or resubmission from it, so this context must store it.
        IMessageContext ctx = CreateMessageContext();
        var failure = new EventContextHandlerException(new InvalidOperationException("down"));

        ctx.HandledFailure = failure;

        Assert.AreSame(failure, ctx.HandledFailure);
    }

    // ── Session state: Block/Unblock ────────────────────────────────────

    [TestMethod]
    public async Task BlockSession_SetsBlockedByEventId()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session, eventId: "evt-1");

        await ctx.BlockSession();

        var state = await session.GetStateAsync();
        Assert.AreEqual("evt-1", state.BlockedByEventId);
    }

    [TestMethod]
    public async Task BlockSession_StoresTheBlockingDeliverysMessageId()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.BlockSession();

        Assert.AreEqual("msg-1", (await session.GetStateAsync()).BlockedByMessageId);
        Assert.AreEqual("msg-1", await ctx.GetBlockedByMessageId());
    }

    [TestMethod]
    public async Task BlockSession_ByAReplayedCopy_StoresTheMessageIdItWasParkedFrom()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);
        ctx.SetUserProperty(UserPropertyName.InboxMessageId, "source-message");

        await ctx.BlockSession();

        Assert.AreEqual("source-message", (await session.GetStateAsync()).BlockedByMessageId);
    }

    [TestMethod]
    public async Task UnblockSession_ClearsTheBlockingDeliverysMessageId()
    {
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "evt-1";
        session.State.BlockedByMessageId = "msg-0";
        var ctx = CreateMessageContext(session: session);

        await ctx.UnblockSession();

        Assert.IsNull((await session.GetStateAsync()).BlockedByMessageId);
    }

    [TestMethod]
    public void InboxMessageId_WhenMissing_ReturnsNull()
    {
        var ctx = CreateMessageContext();

        Assert.IsNull(ctx.InboxMessageId);
    }

    [TestMethod]
    public void InboxMessageId_WhenPresent_ReturnsValue()
    {
        var ctx = CreateMessageContext();
        ctx.SetUserProperty(UserPropertyName.InboxMessageId, "source-message");

        Assert.AreEqual("source-message", ctx.InboxMessageId);
    }

    [TestMethod]
    public async Task Inbox_ResubmissionWhoseSessionStateCannotBeRead_RecordsOnlyItsOwnId()
    {
        // The handler already succeeded, so a failed session-state read must not turn the
        // resubmission into a failure; it only loses the extra record of the source delivery.
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "evt-1";
        session.State.BlockedByMessageId = "source-message";
        session.GetStateException = new InvalidOperationException("session state unavailable");
        var ctx = CreateMessageContext(session: session, from: Constants.ManagerId, messageType: MessageType.ResubmissionRequest);
        var store = new InMemoryInboxStore();
        var middleware = new NimBus.Core.Inbox.InboxMiddleware(new NoopEventContextHandler(), store);

        await middleware.Handle(ctx);

        Assert.IsTrue(await store.HasProcessedAsync(ctx.To, "msg-1"));
        Assert.IsFalse(await store.HasProcessedAsync(ctx.To, "source-message"), "The unreadable session state must not be guessed.");
    }

    [TestMethod]
    public async Task UnblockSession_ClearsBlockedByEventId()
    {
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "evt-1";
        var ctx = CreateMessageContext(session: session);

        await ctx.UnblockSession();

        var state = await session.GetStateAsync();
        Assert.IsNull(state.BlockedByEventId);
    }

    [TestMethod]
    public async Task IsSessionBlocked_WhenBlocked_ReturnsTrue()
    {
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "evt-1";
        var ctx = CreateMessageContext(session: session);

        Assert.IsTrue(await ctx.IsSessionBlocked());
    }

    [TestMethod]
    public async Task IsSessionBlocked_IgnoresLegacyDeferredSequences()
    {
        // v4 no longer drains messages parked with the Service Bus defer API, so a leftover
        // legacy sequence must not block the session forever.
        var session = new FakeServiceBusSession();
#pragma warning disable CS0618 // Pins the behavior of the obsolete legacy state.
        session.State.DeferredSequenceNumbers.Add(100);
#pragma warning restore CS0618
        var ctx = CreateMessageContext(session: session);

        Assert.IsFalse(await ctx.IsSessionBlocked());
    }

    [TestMethod]
    public async Task IsSessionBlocked_WhenClear_ReturnsFalse()
    {
        var ctx = CreateMessageContext();
        Assert.IsFalse(await ctx.IsSessionBlocked());
    }

    [TestMethod]
    public async Task IsSessionBlockedByThis_WhenMatchingEventId_ReturnsTrue()
    {
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "evt-1";
        var ctx = CreateMessageContext(session: session, eventId: "evt-1");

        Assert.IsTrue(await ctx.IsSessionBlockedByThis());
    }

    [TestMethod]
    public async Task IsSessionBlockedByThis_WhenDifferentEventId_ReturnsFalse()
    {
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "other-event";
        var ctx = CreateMessageContext(session: session, eventId: "evt-1");

        Assert.IsFalse(await ctx.IsSessionBlockedByThis());
    }

    [TestMethod]
    public async Task GetBlockedByEventId_ReturnsFromSessionState()
    {
        var session = new FakeServiceBusSession();
        session.State.BlockedByEventId = "evt-1";
        var ctx = CreateMessageContext(session: session);

        Assert.AreEqual("evt-1", await ctx.GetBlockedByEventId());
    }

    // ── Session state: Deferred counts ──────────────────────────────────

    [TestMethod]
    public async Task GetNextDeferralSequenceAndIncrement_IncrementsAndReturns()
    {
        var session = new FakeServiceBusSession();
        session.State.NextDeferralSequence = 5;
        var ctx = CreateMessageContext(session: session);

        int seq = await ctx.GetNextDeferralSequenceAndIncrement();

        Assert.AreEqual(5, seq);
        var state = await session.GetStateAsync();
        Assert.AreEqual(6, state.NextDeferralSequence);
    }

    [TestMethod]
    public async Task IncrementDeferredCount_IncrementsCount()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredCount = 0;
        var ctx = CreateMessageContext(session: session);

        await ctx.IncrementDeferredCount();

        var state = await session.GetStateAsync();
        Assert.AreEqual(1, state.DeferredCount);
    }

    [TestMethod]
    public async Task DecrementDeferredCount_DecrementsCount()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredCount = 2;
        var ctx = CreateMessageContext(session: session);

        await ctx.DecrementDeferredCount();

        var state = await session.GetStateAsync();
        Assert.AreEqual(1, state.DeferredCount);
    }

    [TestMethod]
    public async Task DecrementDeferredCount_AtZero_DoesNotGoNegative()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredCount = 0;
        var ctx = CreateMessageContext(session: session);

        await ctx.DecrementDeferredCount();

        var state = await session.GetStateAsync();
        Assert.AreEqual(0, state.DeferredCount);
    }

    [TestMethod]
    public async Task GetDeferredCount_ReturnsCount()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredCount = 7;
        var ctx = CreateMessageContext(session: session);

        Assert.AreEqual(7, await ctx.GetDeferredCount());
    }

    [TestMethod]
    public async Task HasDeferredMessages_WhenCountPositive_ReturnsTrue()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredCount = 3;
        var ctx = CreateMessageContext(session: session);

        Assert.IsTrue(await ctx.HasDeferredMessages());
    }

    [TestMethod]
    public async Task HasDeferredMessages_WhenLegacySequences_ReturnsTrue()
    {
        var session = new FakeServiceBusSession();
#pragma warning disable CS0618 // Legacy state still counts, so it is not silently dropped.
        session.State.DeferredSequenceNumbers.Add(100);
#pragma warning restore CS0618
        var ctx = CreateMessageContext(session: session);

        Assert.IsTrue(await ctx.HasDeferredMessages());
    }

    [TestMethod]
    public async Task HasDeferredMessages_WhenEmpty_ReturnsFalse()
    {
        var ctx = CreateMessageContext();
        Assert.IsFalse(await ctx.HasDeferredMessages());
    }

    [TestMethod]
    public async Task ResetDeferredCount_SetsToZero()
    {
        var session = new FakeServiceBusSession();
        session.State.DeferredCount = 5;
        var ctx = CreateMessageContext(session: session);

        await ctx.ResetDeferredCount();

        var state = await session.GetStateAsync();
        Assert.AreEqual(0, state.DeferredCount);
    }

    // ── Complete/DeadLetter/Defer operations ────────────────────────────

    [TestMethod]
    public async Task Complete_DelegatesToSession()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.Complete();

        Assert.AreEqual(1, session.CompleteCalls);
    }

    [TestMethod]
    public async Task Complete_SessionLockLost_ThrowsTransient()
    {
        var session = new FakeServiceBusSession
        {
            CompleteException = new ServiceBusException("lost", ServiceBusFailureReason.SessionLockLost),
        };
        var ctx = CreateMessageContext(session: session);

        await Assert.ThrowsExactlyAsync<TransientException>(() => ctx.Complete());
    }

    [TestMethod]
    public async Task Complete_TransientServiceBusException_ThrowsTransient()
    {
        var session = new FakeServiceBusSession
        {
            CompleteException = new ServiceBusException(isTransient: true, "transient"),
        };
        var ctx = CreateMessageContext(session: session);

        await Assert.ThrowsExactlyAsync<TransientException>(() => ctx.Complete());
    }

    [TestMethod]
    public async Task DeadLetter_DelegatesToSession()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.DeadLetter("reason", new InvalidOperationException("boom"));

        Assert.AreEqual(1, session.DeadLetterCalls);
        Assert.AreEqual("reason", session.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task DeadLetter_NullException_PassesNullDescription()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        await ctx.DeadLetter("reason");

        Assert.AreEqual(1, session.DeadLetterCalls);
        Assert.IsNull(session.LastDeadLetterDescription);
    }

    [TestMethod]
    public async Task DeadLetter_LongException_TruncatesAtNewline()
    {
        var session = new FakeServiceBusSession();
        var ctx = CreateMessageContext(session: session);

        // Create an exception whose ToString() is > 4096 chars with newlines
        var longMessage = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"Line {i}: " + new string('x', 30)));
        var ex = new InvalidOperationException(longMessage);

        await ctx.DeadLetter("reason", ex);

        Assert.IsNotNull(session.LastDeadLetterDescription);
        Assert.IsTrue(session.LastDeadLetterDescription.Length <= 4096,
            $"Description should be at most 4096 chars, was {session.LastDeadLetterDescription.Length}");
        Assert.IsTrue(session.LastDeadLetterDescription.EndsWith("...[TRUNCATED]..."));
    }

    [TestMethod]
    public async Task Abandon_ReturnsImmediately()
    {
        var ctx = CreateMessageContext();
        // Should not throw, just return
        await ctx.Abandon(new TransientException("test"));
    }

    // ── EnqueuedTimeUtc ─────────────────────────────────────────────────

    [TestMethod]
    public void EnqueuedTimeUtc_ReturnsValueFromMessage()
    {
        var expected = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc);
        var msg = CreateDefaultMessage();
        msg.EnqueuedTimeUtc = expected;
        var ctx = new MessageContext(msg, new FakeServiceBusSession());

        Assert.AreEqual(expected, ctx.EnqueuedTimeUtc);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    // ── ScheduleRedelivery ─────────────────────────────────────────

    [TestMethod]
    public async Task ScheduleRedelivery_CopiesBodyAndStandardProperties_AndKeepsTheMessageId()
    {
        var body = new BinaryData("scheduled payload");
        var timeToLive = TimeSpan.FromMinutes(15);
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: body,
            messageId: "original-message-id",
            partitionKey: "session-1",
            viaPartitionKey: "transaction-partition",
            sessionId: "session-1",
            replyToSessionId: "reply-session",
            timeToLive: timeToLive,
            correlationId: "correlation-1",
            subject: "order.created",
            to: "orders",
            contentType: "application/json",
            replyTo: "replies");
        var session = new FakeServiceBusSession();
        var context = new MessageContext(new NimBus.ServiceBus.ServiceBusMessage(received), session);

        await context.ScheduleRedelivery(TimeSpan.FromMinutes(2), throttleRetryCount: 3);

        var scheduled = session.ScheduledMessages.Single();
        CollectionAssert.AreEqual(body.ToArray(), scheduled.Body.ToArray());
        Assert.AreEqual(received.SessionId, scheduled.SessionId);
        Assert.AreEqual(received.CorrelationId, scheduled.CorrelationId);
        Assert.AreEqual(received.ContentType, scheduled.ContentType);
        Assert.AreEqual(received.Subject, scheduled.Subject);
        Assert.AreEqual(received.To, scheduled.To);
        Assert.AreEqual(received.ReplyTo, scheduled.ReplyTo);
        Assert.AreEqual(received.ReplyToSessionId, scheduled.ReplyToSessionId);
        Assert.AreEqual(received.PartitionKey, scheduled.PartitionKey);
        Assert.AreEqual(received.TransactionPartitionKey, scheduled.TransactionPartitionKey);
        Assert.AreEqual(received.TimeToLive, scheduled.TimeToLive);
        // Spec 030 §5.7: the copy must stay identifiable as the same message, or the store's
        // stale-write guard cannot tell a rescheduled control request from a genuinely new one.
        Assert.AreEqual(received.MessageId, scheduled.MessageId);
    }

    [TestMethod]
    public async Task ScheduleRedelivery_CopiesApplicationProperties_AndReplacesThrottleRetryCount()
    {
        var properties = new Dictionary<string, object>
        {
            { "CustomText", "preserved" },
            { "CustomNumber", 42L },
            { "ThrottleRetryCount", "1" },
        };
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            messageId: "original-message-id",
            sessionId: "session-1",
            properties: properties);
        var session = new FakeServiceBusSession();
        var context = new MessageContext(new NimBus.ServiceBus.ServiceBusMessage(received), session);

        await context.ScheduleRedelivery(TimeSpan.FromMinutes(1), throttleRetryCount: 7);

        var scheduled = session.ScheduledMessages.Single();
        Assert.AreEqual("preserved", scheduled.ApplicationProperties["CustomText"]);
        Assert.AreEqual(42L, scheduled.ApplicationProperties["CustomNumber"]);
        Assert.AreEqual("7", scheduled.ApplicationProperties["ThrottleRetryCount"]);
        Assert.AreEqual("1", received.ApplicationProperties["ThrottleRetryCount"], "The inbound message must not be mutated.");
    }

    [TestMethod]
    public async Task ScheduleRedelivery_SchedulesBeforeCompletingOriginal()
    {
        var session = new FakeServiceBusSession();
        var context = CreateScheduledRedeliveryContext(session);

        await context.ScheduleRedelivery(TimeSpan.FromSeconds(30), throttleRetryCount: 2);

        CollectionAssert.AreEqual(new List<string> { "schedule", "complete" }, session.Operations);
        Assert.AreEqual(1, session.CompleteCalls);
    }

    [TestMethod]
    public async Task ScheduleRedelivery_WhenSchedulingIsUnavailable_LeavesOriginalUnsettled()
    {
        var session = new FakeServiceBusSession
        {
            ScheduleException = new InvalidOperationException("sender unavailable"),
        };
        var context = CreateScheduledRedeliveryContext(session);

        await Assert.ThrowsExactlyAsync<TransientException>(
            () => context.ScheduleRedelivery(TimeSpan.FromSeconds(30), throttleRetryCount: 2));

        Assert.AreEqual(1, session.ScheduleAttempts);
        Assert.AreEqual(0, session.ScheduledMessages.Count);
        Assert.AreEqual(0, session.CompleteCalls);
        CollectionAssert.AreEqual(new List<string> { "schedule" }, session.Operations);
    }

    [TestMethod]
    public async Task ScheduleRedelivery_WhenTheBrokerRejectsTheCopy_LeavesOriginalUnsettled()
    {
        // Not only a missing sender: a throttled, disabled or unauthorized schedule call must also
        // surface as transient, or the Resolver's fallback never runs and the message ends up
        // dead-lettered as MaxDeliveryCountExceeded instead of retried.
        Exception[] brokerFailures =
        [
            new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy),
            new ServiceBusException("disabled", ServiceBusFailureReason.MessagingEntityDisabled),
            new UnauthorizedAccessException("missing Send claim"),
        ];

        foreach (var brokerFailure in brokerFailures)
        {
            var session = new FakeServiceBusSession { ScheduleException = brokerFailure };
            var context = CreateScheduledRedeliveryContext(session);

            var exception = await Assert.ThrowsExactlyAsync<TransientException>(
                () => context.ScheduleRedelivery(TimeSpan.FromSeconds(30), throttleRetryCount: 2));

            Assert.AreSame(brokerFailure, exception.InnerException, brokerFailure.Message);
            Assert.AreEqual(0, session.CompleteCalls, brokerFailure.Message);
        }
    }

    [TestMethod]
    public async Task ScheduleRedelivery_WhenCompletionFails_PropagatesCompletionFailureAfterScheduling()
    {
        var completionFailure = new InvalidOperationException("completion failed");
        var session = new FakeServiceBusSession
        {
            CompleteException = completionFailure,
        };
        var context = CreateScheduledRedeliveryContext(session);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => context.ScheduleRedelivery(TimeSpan.FromSeconds(30), throttleRetryCount: 2));

        Assert.AreSame(completionFailure, exception);
        Assert.AreEqual(1, session.ScheduledMessages.Count);
        Assert.AreEqual(0, session.CompleteCalls);
        CollectionAssert.AreEqual(new List<string> { "schedule", "complete" }, session.Operations);
    }

    [TestMethod]
    public async Task ScheduleRedelivery_WhenCancelled_DoesNotScheduleOrCompleteOriginal()
    {
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();
        var session = new FakeServiceBusSession();
        var context = CreateScheduledRedeliveryContext(session);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => context.ScheduleRedelivery(
                TimeSpan.FromSeconds(30),
                throttleRetryCount: 2,
                cancellationSource.Token));

        Assert.AreEqual(cancellationSource.Token, session.LastScheduleCancellationToken);
        Assert.AreEqual(1, session.ScheduleAttempts);
        Assert.AreEqual(0, session.ScheduledMessages.Count);
        Assert.AreEqual(0, session.CompleteCalls);
        CollectionAssert.AreEqual(new List<string> { "schedule" }, session.Operations);
    }

    private static MessageContext CreateScheduledRedeliveryContext(FakeServiceBusSession session)
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            messageId: "original-message-id",
            sessionId: "session-1",
            correlationId: "correlation-1");
        return new MessageContext(new NimBus.ServiceBus.ServiceBusMessage(received), session);
    }

    private static TestableMessageContext CreateMessageContext(
        FakeServiceBusSession? session = null,
        string from = "StorefrontEndpoint",
        string eventId = "evt-1",
        MessageType messageType = MessageType.EventRequest,
        MessageContent? content = null,
        bool isDeferred = false)
    {
        session ??= new FakeServiceBusSession();
        var msg = CreateDefaultMessage(from, eventId, messageType, content);
        var ctx = new TestableMessageContext(msg, session, isDeferred);
        return ctx;
    }

    private static FakeServiceBusMessage CreateDefaultMessage(
        string from = "StorefrontEndpoint",
        string eventId = "evt-1",
        MessageType messageType = MessageType.EventRequest,
        MessageContent? content = null)
    {
        content ??= new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        };
        var body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(content));
        var msg = new FakeServiceBusMessage
        {
            MessageId = "msg-1",
            SessionId = "session-1",
            CorrelationId = "corr-1",
            Body = body,
            EnqueuedTimeUtc = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
        };
        SetDefaultProperties(msg, from, eventId, messageType);
        return msg;
    }

    private static void SetDefaultProperties(
        FakeServiceBusMessage msg,
        string from = "StorefrontEndpoint",
        string eventId = "evt-1",
        MessageType messageType = MessageType.EventRequest)
    {
        msg.UserProperties[UserPropertyName.From.ToString()] = from;
        msg.UserProperties[UserPropertyName.To.ToString()] = "AnalyticsEndpoint";
        msg.UserProperties[UserPropertyName.EventId.ToString()] = eventId;
        msg.UserProperties[UserPropertyName.MessageType.ToString()] = messageType.ToString();
    }

    private sealed class NoopEventContextHandler : IEventContextHandler
    {
        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    // ── Testable subclass to expose SetUserProperty ─────────────────────

    private sealed class TestableMessageContext : MessageContext
    {
        private readonly FakeServiceBusMessage _msg;

        public TestableMessageContext(FakeServiceBusMessage msg, IServiceBusSession session, bool isDeferred = false)
            : base(msg, session, isDeferred)
        {
            _msg = msg;
        }

        public void SetUserProperty(UserPropertyName name, string value)
        {
            _msg.UserProperties[name.ToString()] = value;
        }
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    public sealed class LegacyBodyOnlyEvent : Event
    {
    }

    private sealed class RecordingLegacyEventHandler : IEventHandler<LegacyBodyOnlyEvent>
    {
        public int Calls { get; private set; }

        public string EventType { get; private set; } = string.Empty;

        public Task Handle(
            LegacyBodyOnlyEvent message,
            IEventHandlerContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            EventType = context.EventType;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServiceBusMessage : IServiceBusMessage
    {
        public Dictionary<string, string> UserProperties { get; } = new();

        private byte[] _body = Array.Empty<byte>();

        // Counts how many times the Body getter is read, so tests can assert
        // the message body is materialized/deserialized at most once per context.
        public int BodyReadCount { get; private set; }

        public byte[] Body
        {
            get { BodyReadCount++; return _body; }
            set => _body = value;
        }
        public string LockToken { get; set; } = "lock-1";
        public string SessionId { get; set; } = "session-1";
        public string MessageId { get; set; } = "msg-1";
        public string CorrelationId { get; set; } = "corr-1";
        public int DeliveryCount { get; set; } = 1;
        public long SequenceNumber { get; set; } = 1;
        public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow;

        // IServiceBusMessage.Message is internal — only used by ScheduleRedelivery.
        // Accessible via InternalsVisibleTo; returns null since we don't unit-test ScheduleRedelivery.
        ServiceBusReceivedMessage IServiceBusMessage.Message => null!;

        public string? GetUserProperty(UserPropertyName name) => GetUserProperty(name.ToString());

        public string? GetUserProperty(string name) =>
            UserProperties.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class FakeServiceBusSession : IServiceBusSession
    {
        public SessionState State { get; set; } = new();
        public Dictionary<long, IServiceBusMessage> DeferredMessages { get; } = new();
        public List<Azure.Messaging.ServiceBus.ServiceBusMessage> ScheduledMessages { get; } = new();
        public List<string> Operations { get; } = new();

        public int CompleteCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int DeferCalls { get; private set; }
        public int ScheduleAttempts { get; private set; }
        public string? LastDeadLetterReason { get; private set; }
        public string? LastDeadLetterDescription { get; private set; }
        public CancellationToken LastScheduleCancellationToken { get; private set; }

        public Exception? CompleteException { get; set; }
        public Exception? DeadLetterException { get; set; }
        public Exception? DeferException { get; set; }
        public Exception? ScheduleException { get; set; }
        public Exception? GetStateException { get; set; }

        public Task CompleteAsync(IServiceBusMessage message, CancellationToken ct = default)
        {
            Operations.Add("complete");
            ct.ThrowIfCancellationRequested();
            if (CompleteException != null) throw CompleteException;
            CompleteCalls++;
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(IServiceBusMessage message, string reason, string? description, CancellationToken ct = default)
        {
            if (DeadLetterException != null) throw DeadLetterException;
            DeadLetterCalls++;
            LastDeadLetterReason = reason;
            LastDeadLetterDescription = description;
            return Task.CompletedTask;
        }

        public Task SetStateAsync(SessionState state, CancellationToken ct = default)
        {
            State = state;
            return Task.CompletedTask;
        }

        public Task<SessionState> GetStateAsync(CancellationToken ct = default)
        {
            if (GetStateException != null) throw GetStateException;
            return Task.FromResult(State);
        }

        public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledTime, CancellationToken ct = default)
        {
            Operations.Add("schedule");
            ScheduleAttempts++;
            LastScheduleCancellationToken = ct;
            ct.ThrowIfCancellationRequested();
            if (ScheduleException != null) throw ScheduleException;
            ScheduledMessages.Add(message);
            return Task.CompletedTask;
        }
    }
}
