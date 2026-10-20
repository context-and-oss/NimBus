#pragma warning disable CA1707, CA2007, CS8618, CS8625, CS8603
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="NimBusSubscriberBuilder.AddDynamicHandler(string, System.Func{NimBus.SDK.EventHandlers.IEventJsonHandler})"/>.
/// Verifies that a string-keyed handler registered through the builder reaches
/// the <see cref="EventHandlerProvider"/> and is dispatched the raw EventJson
/// from <c>context.MessageContent.EventContent</c>.
/// </summary>
[TestClass]
public class AddDynamicHandlerTests
{
    private const string EventTypeId = "crm.contact.enriched.v1";
    private const string ExpectedJson = "{\"contactId\":\"abc-123\"}";

    // ── Dispatch tests ────────────────────────────────────────────────────

    [TestMethod]
    public async Task AddDynamicHandler_Dispatch_InvokesHandlerWithCorrectEventJson()
    {
        // Arrange
        string? receivedJson = null;
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(
            EventTypeId,
            () => new DelegateEventJsonHandler((ctx, _) =>
            {
                receivedJson = ctx.MessageContent.EventContent.EventJson;
                return Task.CompletedTask;
            }));

        // Apply registrations to a fresh provider — mirrors what
        // ServiceCollectionExtensions does inside the ISubscriberClient factory.
        var provider = new EventHandlerProvider();
        var sp = new ServiceCollection().BuildServiceProvider();
        foreach (var reg in builder.HandlerRegistrations)
        {
            reg.Register(sp, provider);
        }

        var context = new StubMessageContext(EventTypeId, ExpectedJson);

        // Act
        await provider.Handle(context, CancellationToken.None);

        // Assert
        Assert.AreEqual(ExpectedJson, receivedJson,
            "The DelegateEventJsonHandler must receive the raw EventJson from the message context.");
    }

    [TestMethod]
    public async Task AddDynamicHandler_Dispatch_IsKeyedByExactEventTypeId()
    {
        // A second, unrelated handler registered for a different EventTypeId
        // must not be invoked when the message carries the first id.
        string? invokedId = null;
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(
            "other.event.v1",
            () => new DelegateEventJsonHandler((_, _) =>
            {
                invokedId = "wrong";
                return Task.CompletedTask;
            }));
        builder.AddDynamicHandler(
            EventTypeId,
            () => new DelegateEventJsonHandler((_, _) =>
            {
                invokedId = EventTypeId;
                return Task.CompletedTask;
            }));

        var provider = new EventHandlerProvider();
        var sp = new ServiceCollection().BuildServiceProvider();
        foreach (var reg in builder.HandlerRegistrations)
        {
            reg.Register(sp, provider);
        }

        await provider.Handle(new StubMessageContext(EventTypeId, "{}"), CancellationToken.None);

        Assert.AreEqual(EventTypeId, invokedId);
    }

    [TestMethod]
    public void AddDynamicHandler_Returns_This_ForChaining()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        var returned = builder.AddDynamicHandler(
            EventTypeId,
            () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask));

        Assert.AreSame(builder, returned, "AddDynamicHandler must return 'this' for fluent chaining.");
    }

    // ── Arg-validation tests ──────────────────────────────────────────────

    [TestMethod]
    public void AddDynamicHandler_NullEventTypeId_Throws()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentException>(() =>
            builder.AddDynamicHandler(
                null!,
                () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));
    }

    [TestMethod]
    public void AddDynamicHandler_EmptyEventTypeId_Throws()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentException>(() =>
            builder.AddDynamicHandler(
                "",
                () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));
    }

    [TestMethod]
    public void AddDynamicHandler_WhitespaceEventTypeId_Throws()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentException>(() =>
            builder.AddDynamicHandler(
                "   ",
                () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));
    }

    [TestMethod]
    public void AddDynamicHandler_NullFactory_Throws()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            builder.AddDynamicHandler(EventTypeId, (Func<IEventJsonHandler>)null!));
    }

    // ── Dynamic/typed EventTypeId collision tests ─────────────────────────
    //
    // A typed event's EventTypeId is its unqualified CLR type name (EventType.Id
    // => Type.Name). So a typed handler for `DynamicCollisionEvent` claims the
    // wire id "DynamicCollisionEvent"; registering a dynamic handler for that
    // same string must be rejected with a clear InvalidOperationException —
    // NOT a NullReferenceException on the dynamic entry's null EventType.

    [TestMethod]
    public void Dynamic_Then_Typed_SameEventTypeId_ThrowsInvalidOperation_NotNre()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(
            nameof(DynamicCollisionEvent),
            () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.AddHandler<DynamicCollisionEvent, DynamicCollisionEventHandler>());

        StringAssert.Contains(ex.Message, nameof(DynamicCollisionEvent));
        StringAssert.Contains(ex.Message, "dynamic");
    }

    [TestMethod]
    public void Typed_Then_Dynamic_SameEventTypeId_ThrowsInvalidOperation()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddHandler<DynamicCollisionEvent, DynamicCollisionEventHandler>();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.AddDynamicHandler(
                nameof(DynamicCollisionEvent),
                () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));

        StringAssert.Contains(ex.Message, nameof(DynamicCollisionEvent));
        StringAssert.Contains(ex.Message, "typed");
    }

    [TestMethod]
    public void Dynamic_Then_Dynamic_SameEventTypeId_ThrowsInvalidOperation()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(
            EventTypeId,
            () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.AddDynamicHandler(
                EventTypeId,
                () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));

        StringAssert.Contains(ex.Message, EventTypeId);
    }

    // ── SP-aware overload: AddDynamicHandler(string, Func<IServiceProvider, IEventJsonHandler>) ──
    //
    // The DI-aware overload resolves the handler from a per-message IServiceProvider.
    // Parameterless provider construction below retains the root-provider fallback
    // used by direct registration tests and older manual integrations.

    [TestMethod]
    public async Task AddDynamicHandler_SpFactory_Dispatch_InvokesResolvedHandler()
    {
        // Arrange — register the handler in DI so the SP-factory can resolve it.
        var services = new ServiceCollection();
        services.AddSingleton<SpResolvedHandler>();
        var sp = services.BuildServiceProvider();

        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(EventTypeId, p => p.GetRequiredService<SpResolvedHandler>());

        var provider = new EventHandlerProvider();
        foreach (var reg in builder.HandlerRegistrations)
        {
            reg.Register(sp, provider);
        }

        // Act
        await provider.Handle(new StubMessageContext(EventTypeId, ExpectedJson), CancellationToken.None);

        // Assert — the handler resolved via the SP factory received the raw EventJson.
        var resolved = sp.GetRequiredService<SpResolvedHandler>();
        Assert.AreEqual(ExpectedJson, resolved.ReceivedJson,
            "The handler resolved via the IServiceProvider factory must receive the raw EventJson.");
    }

    [TestMethod]
    public void AddDynamicHandler_SpFactory_Returns_This_ForChaining()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        var returned = builder.AddDynamicHandler(
            EventTypeId,
            p => new DelegateEventJsonHandler((_, _) => Task.CompletedTask));

        Assert.AreSame(builder, returned, "SP-aware AddDynamicHandler must return 'this' for fluent chaining.");
    }

    [TestMethod]
    public void AddDynamicHandler_SpFactory_NullFactory_Throws()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            builder.AddDynamicHandler(EventTypeId, (Func<IServiceProvider, IEventJsonHandler>)null!));
    }

    [TestMethod]
    public void AddDynamicHandler_SpFactory_EmptyEventTypeId_Throws()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentException>(() =>
            builder.AddDynamicHandler("", p => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));
    }

    [TestMethod]
    public void Typed_Then_SpDynamic_SameEventTypeId_ThrowsInvalidOperation_NotNre()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddHandler<DynamicCollisionEvent, DynamicCollisionEventHandler>();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.AddDynamicHandler(
                nameof(DynamicCollisionEvent),
                p => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));

        StringAssert.Contains(ex.Message, nameof(DynamicCollisionEvent));
        StringAssert.Contains(ex.Message, "typed");
    }

    [TestMethod]
    public void SpDynamic_Then_Typed_SameEventTypeId_ThrowsInvalidOperation_NotNre()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(
            nameof(DynamicCollisionEvent),
            p => new DelegateEventJsonHandler((_, _) => Task.CompletedTask));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.AddHandler<DynamicCollisionEvent, DynamicCollisionEventHandler>());

        StringAssert.Contains(ex.Message, nameof(DynamicCollisionEvent));
        StringAssert.Contains(ex.Message, "dynamic");
    }

    // ── Stub ──────────────────────────────────────────────────────────────

    // Handler resolved via the SP-aware AddDynamicHandler overload; records the
    // EventJson it was dispatched so the test can assert correct delivery.
    private sealed class SpResolvedHandler : IEventJsonHandler
    {
        public string? ReceivedJson { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            ReceivedJson = context.MessageContent.EventContent.EventJson;
            return Task.CompletedTask;
        }
    }

    private sealed class StubMessageContext : IMessageContext
    {
        public StubMessageContext(string eventTypeId, string eventJson)
        {
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = eventJson,
                }
            };
            EventTypeId = eventTypeId;
        }

        public MessageContent MessageContent { get; }
        public string EventTypeId { get; }

        // Minimal stubs — unused by dispatch path
        public string EventId => string.Empty;
        public string To => string.Empty;
        public string SessionId => string.Empty;
        public string CorrelationId => string.Empty;
        public string MessageId => string.Empty;
        public MessageType MessageType => MessageType.EventRequest;
        public string ParentMessageId => string.Empty;
        public string OriginatingMessageId => string.Empty;
        public int? RetryCount => null;
        public string From => string.Empty;
        public string OriginatingFrom => string.Empty;
        public string OriginalSessionId => string.Empty;
        public int? DeferralSequence => null;
        public DateTime EnqueuedTimeUtc => DateTime.UtcNow;
        public string DeadLetterReason => null;
        public string DeadLetterErrorDescription => null;
        public string HandoffReason => null;
        public string ExternalJobId => null;
        public DateTime? ExpectedBy => null;
        public bool IsDeferred => false;
        public int ThrottleRetryCount => 0;
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata? HandoffMetadata { get; set; }
        public ActivityContext ParentTraceContext { get; set; }

        public Task Complete(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Abandon(TransientException exception) => Task.CompletedTask;
        public Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task BlockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Typed event whose EventTypeId (== unqualified type name) is reused by a
    // dynamic registration in the collision tests above.
    public sealed class DynamicCollisionEvent : Event
    {
        public string Id { get; set; } = "";
    }

    public sealed class DynamicCollisionEventHandler : IEventHandler<DynamicCollisionEvent>
    {
        public Task Handle(DynamicCollisionEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
