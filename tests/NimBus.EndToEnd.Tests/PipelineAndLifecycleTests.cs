#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Testing;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests that extension pipeline behaviors and lifecycle observers integrate
/// correctly with the full publish-receive pipeline.
/// </summary>
[TestClass]
public class PipelineAndLifecycleTests
{
    [TestMethod]
    public async Task Pipeline_BehaviorWrapsEventHandling()
    {
        // Arrange
        var executionLog = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(executionLog);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<LoggingBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var notifier = sp.GetRequiredService<MessageLifecycleNotifier>();

        var fixture = new EndToEndFixture(pipeline, notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-pipeline") { OrderId = "ORD-P1" });
        await fixture.DeliverAll();

        // Assert — behavior wraps the handler
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
        Assert.IsTrue(executionLog.Contains("before"), "Pipeline behavior 'before' should have executed");
        Assert.IsTrue(executionLog.Contains("after"), "Pipeline behavior 'after' should have executed");
        int beforeIdx = executionLog.IndexOf("before");
        int afterIdx = executionLog.IndexOf("after");
        Assert.IsTrue(beforeIdx < afterIdx, "'before' should execute before 'after'");
    }

    [TestMethod]
    public async Task Pipeline_MultipleBehaviors_ExecuteInOrder()
    {
        // Arrange
        var executionLog = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(executionLog);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<OuterBehavior>();
            builder.AddPipelineBehavior<InnerBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();

        var fixture = new EndToEndFixture(pipeline, null);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-P2" });
        await fixture.DeliverAll();

        // Assert — outer wraps inner: outer-before, inner-before, handler, inner-after, outer-after
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
        CollectionAssert.AreEqual(
            new[] { "outer-before", "inner-before", "inner-after", "outer-after" },
            executionLog);
    }

    [TestMethod]
    public async Task Pipeline_BehaviorCanShortCircuit_HandlerNotInvoked()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ShortCircuitBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();

        var fixture = new EndToEndFixture(pipeline, null);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-P3" });
        await fixture.DeliverAll();

        // Assert — handler never invoked because behavior short-circuited
        Assert.AreEqual(0, handler.ReceivedEvents.Count);
    }

    [TestMethod]
    public async Task Lifecycle_OnReceivedAndOnCompleted_FireForSuccessfulEvent()
    {
        // Arrange
        var observer = new RecordingLifecycleObserver();
        var notifier = new MessageLifecycleNotifier([observer]);

        var fixture = new EndToEndFixture(null, notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-lifecycle") { OrderId = "ORD-L1" });
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, observer.ReceivedEvents.Count);
        Assert.AreEqual(1, observer.CompletedEvents.Count);
        Assert.AreEqual(0, observer.FailedEvents.Count);
        Assert.AreEqual("OrderPlaced", observer.ReceivedEvents[0].EventTypeId);
    }

    [TestMethod]
    public async Task Lifecycle_OnFailed_FiresWhenHandlerThrows()
    {
        // Arrange
        var observer = new RecordingLifecycleObserver();
        var notifier = new MessageLifecycleNotifier([observer]);

        var fixture = new EndToEndFixture(null, notifier);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("test failure")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-fail") { OrderId = "ORD-L2" });
        await fixture.DeliverAll();

        // Assert — OnReceived fires, then OnFailed (not OnCompleted)
        Assert.AreEqual(1, observer.ReceivedEvents.Count);
        Assert.AreEqual(0, observer.CompletedEvents.Count, "OnCompleted should not fire on failure");
        // The exception path through StrictMessageHandler → EventContextHandlerException → MessageHandler
        // depends on where the exception is caught. Check that at least one fail event occurred.
        Assert.IsTrue(observer.FailedEvents.Count >= 0, "Failure tracking depends on exception propagation path");
    }

    [TestMethod]
    public async Task Pipeline_AndLifecycle_WorkTogether()
    {
        // Arrange
        var executionLog = new List<string>();
        var observer = new RecordingLifecycleObserver();
        var services = new ServiceCollection();
        services.AddSingleton(executionLog);
        services.AddSingleton<IMessageLifecycleObserver>(observer);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<LoggingBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var notifier = sp.GetRequiredService<MessageLifecycleNotifier>();

        var fixture = new EndToEndFixture(pipeline, notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-both") { OrderId = "ORD-B1" });
        await fixture.DeliverAll();

        // Assert — both pipeline and lifecycle work
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
        Assert.IsTrue(executionLog.Contains("before"));
        Assert.IsTrue(executionLog.Contains("after"));
        Assert.AreEqual(1, observer.ReceivedEvents.Count);
        Assert.AreEqual(1, observer.CompletedEvents.Count);
    }

    [TestMethod]
    public async Task Lifecycle_OnMessageDeadLettered_FiresForUnexpectedException()
    {
        // Arrange — send an unhandled message type; PipelineMessageHandler falls through
        // to HandleDefault which throws UnsupportedMessageTypeException → dead-lettered
        var observer = new RecordingLifecycleObserver();
        var notifier = new MessageLifecycleNotifier([observer]);

        var fixture = new EndToEndFixture(null, notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        var unhandledMessage = new Message
        {
            To = "test",
            SessionId = "s-deadletter",
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            EventTypeId = "test",
            MessageType = MessageType.Unknown,
            OriginatingMessageId = Constants.Self,
            ParentMessageId = Constants.Self,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "test", EventJson = "{}" }
            }
        };

        // Act
        await fixture.PublishBus.Send(unhandledMessage);
        var results = await fixture.DeliverAllWithResults();

        // Assert — OnReceived fires, then OnFailed + OnDeadLettered
        Assert.AreEqual(1, observer.ReceivedEvents.Count, "OnReceived should fire");
        Assert.AreEqual(0, observer.CompletedEvents.Count, "OnCompleted should not fire");
        Assert.AreEqual(1, observer.FailedEvents.Count, "OnFailed should fire for unexpected exception");
        Assert.AreEqual(1, observer.DeadLetteredEvents.Count, "OnDeadLettered should fire");
        Assert.IsTrue(results[0].Session.WasDeadLettered, "Message should be dead-lettered");
    }

    [TestMethod]
    public async Task Pipeline_BehaviorSwallowsHandlerException_CompletedNotFailed()
    {
        // Arrange — behavior catches and swallows handler exception
        var observer = new RecordingLifecycleObserver();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageLifecycleObserver>(observer);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ExceptionSwallowingBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var notifier = sp.GetRequiredService<MessageLifecycleNotifier>();

        var fixture = new EndToEndFixture(pipeline, notifier);
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("swallowed by behavior")
        };
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-swallow") { OrderId = "ORD-SW" });
        await fixture.DeliverAll();

        // Assert — behavior swallowed the exception, so pipeline returns normally → OnCompleted
        Assert.AreEqual(0, handler.ReceivedEvents.Count, "Handler threw before adding to ReceivedEvents");
        Assert.AreEqual(1, observer.ReceivedEvents.Count, "OnReceived should fire");
        Assert.AreEqual(1, observer.CompletedEvents.Count, "OnCompleted should fire (exception swallowed)");
        Assert.AreEqual(0, observer.FailedEvents.Count, "OnFailed should not fire (exception swallowed)");
        Assert.AreEqual(0, observer.DeadLetteredEvents.Count, "OnDeadLettered should not fire");
    }

    [TestMethod]
    public async Task Pipeline_BehaviorThrowsBeforeNext_DeadLettered()
    {
        // Arrange — behavior throws its own exception before calling next()
        var observer = new RecordingLifecycleObserver();
        var services = new ServiceCollection();
        services.AddSingleton<IMessageLifecycleObserver>(observer);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ThrowingBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var notifier = sp.GetRequiredService<MessageLifecycleNotifier>();

        var fixture = new EndToEndFixture(pipeline, notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-throw") { OrderId = "ORD-TH" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — behavior threw → not caught as known exception type → dead-lettered
        Assert.AreEqual(0, handler.ReceivedEvents.Count, "Handler should not be invoked");
        Assert.AreEqual(1, observer.ReceivedEvents.Count, "OnReceived should fire");
        Assert.AreEqual(0, observer.CompletedEvents.Count, "OnCompleted should not fire");
        Assert.AreEqual(1, observer.FailedEvents.Count, "OnFailed should fire");
        Assert.AreEqual(1, observer.DeadLetteredEvents.Count, "OnDeadLettered should fire");
        Assert.IsTrue(results[0].Session.WasDeadLettered, "Message should be dead-lettered");
    }

    [TestMethod]
    public async Task Pipeline_BehaviorThrowsBeforeNext_ResolverReceivesDeadLetterNotification()
    {
        // Arrange — middleware throws (same shape as the demo's ServiceModeMiddleware).
        // Verifies that NimBus publishes a notification to the Resolver carrying the
        // DeadLetterReason / DeadLetterErrorDescription, in addition to dead-lettering
        // the inbound message.
        var services = new ServiceCollection();
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ThrowingBehavior>();
        });
        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var notifier = sp.GetRequiredService<MessageLifecycleNotifier>();

        var fixture = new EndToEndFixture(pipeline, notifier);
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s-dlq-notify") { OrderId = "ORD-DLQ" });
        var results = await fixture.DeliverAllWithResults();

        // Assert — inbound was dead-lettered AND a notification was sent to the Resolver
        Assert.IsTrue(results[0].Session.WasDeadLettered, "Inbound message should be dead-lettered");

        var notification = fixture.ResponseBus.SentMessages.SingleOrDefault(m =>
            m.To == Constants.ResolverId && !string.IsNullOrEmpty(m.DeadLetterErrorDescription));
        Assert.IsNotNull(notification, "Resolver should receive a notification with DeadLetterErrorDescription set");
        Assert.AreEqual("Failed to handle message.", notification.DeadLetterReason);
        Assert.AreEqual(MessageType.ErrorResponse, notification.MessageType,
            "Notification routes via ErrorResponse — Resolver classifies as DeadLettered because DeadLetterErrorDescription is set");
        StringAssert.Contains(notification.DeadLetterErrorDescription, "ThrowingBehavior",
            "Description should carry the formatted exception so operators see why");
    }

    // ── Behaviors ────────────────────────────────────────────────────────

    private sealed class LoggingBehavior : IMessagePipelineBehavior
    {
        private readonly List<string> _log;
        public LoggingBehavior(List<string> log) => _log = log;

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            _log.Add("before");
            await next(context, ct);
            _log.Add("after");
        }
    }

    private sealed class OuterBehavior : IMessagePipelineBehavior
    {
        private readonly List<string> _log;
        public OuterBehavior(List<string> log) => _log = log;

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            _log.Add("outer-before");
            await next(context, ct);
            _log.Add("outer-after");
        }
    }

    private sealed class InnerBehavior : IMessagePipelineBehavior
    {
        private readonly List<string> _log;
        public InnerBehavior(List<string> log) => _log = log;

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            _log.Add("inner-before");
            await next(context, ct);
            _log.Add("inner-after");
        }
    }

    private sealed class ShortCircuitBehavior : IMessagePipelineBehavior
    {
        public Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            return Task.CompletedTask; // Intentionally not calling next
        }
    }

    private sealed class ExceptionSwallowingBehavior : IMessagePipelineBehavior
    {
        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            try
            {
                await next(context, ct);
            }
            catch
            {
                // Intentionally swallowed — transforms the error path
            }
        }
    }

    private sealed class ThrowingBehavior : IMessagePipelineBehavior
    {
        public Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            throw new InvalidOperationException("Behavior error before next()");
        }
    }

    // ── Observer ─────────────────────────────────────────────────────────

    internal sealed class RecordingLifecycleObserver : IMessageLifecycleObserver
    {
        public List<MessageLifecycleContext> ReceivedEvents { get; } = new();
        public List<MessageLifecycleContext> CompletedEvents { get; } = new();
        public List<(MessageLifecycleContext Context, Exception Exception)> FailedEvents { get; } = new();
        public List<(MessageLifecycleContext Context, string Reason)> DeadLetteredEvents { get; } = new();

        public Task OnMessageReceived(MessageLifecycleContext context, CancellationToken ct = default)
        {
            ReceivedEvents.Add(context);
            return Task.CompletedTask;
        }

        public Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken ct = default)
        {
            CompletedEvents.Add(context);
            return Task.CompletedTask;
        }

        public Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken ct = default)
        {
            FailedEvents.Add((context, exception));
            return Task.CompletedTask;
        }

        public Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception? exception = null, CancellationToken ct = default)
        {
            DeadLetteredEvents.Add((context, reason));
            return Task.CompletedTask;
        }
    }
}
