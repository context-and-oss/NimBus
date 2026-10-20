#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Testing;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

[TestClass]
public class MessagePipelineTests
{
    // ── Pipeline execution order ─────────────────────────────────────────

    [TestMethod]
    public async Task Pipeline_ExecutesBehaviorsInRegistrationOrder()
    {
        var executionLog = new List<string>();
        var services = new ServiceCollection();

        services.AddSingleton(executionLog);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<FirstBehavior>();
            builder.AddPipelineBehavior<SecondBehavior>();
        });

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var context = CreateContext();

        await pipeline.Execute(context, (ctx, ct) =>
        {
            executionLog.Add("handler");
            return Task.CompletedTask;
        });

        CollectionAssert.AreEqual(
            new[] { "first-before", "second-before", "handler", "second-after", "first-after" },
            executionLog);
    }

    [TestMethod]
    public async Task Pipeline_WithNoBehaviors_CallsTerminalHandlerDirectly()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b => b.AddInMemoryMessageStore());

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var context = CreateContext();
        var called = false;

        await pipeline.Execute(context, (ctx, ct) =>
        {
            called = true;
            return Task.CompletedTask;
        });

        Assert.IsTrue(called);
        Assert.IsFalse(pipeline.HasBehaviors);
    }

    [TestMethod]
    public async Task Pipeline_BehaviorCanShortCircuit()
    {
        var services = new ServiceCollection();
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ShortCircuitBehavior>();
        });

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();
        var context = CreateContext();
        var handlerCalled = false;

        await pipeline.Execute(context, (ctx, ct) =>
        {
            handlerCalled = true;
            return Task.CompletedTask;
        });

        Assert.IsFalse(handlerCalled);
    }

    // ── Behaviors ────────────────────────────────────────────────────────

    private class FirstBehavior : IMessagePipelineBehavior
    {
        private readonly List<string> _log;
        public FirstBehavior(List<string> log) => _log = log;

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            _log.Add("first-before");
            await next(context, ct);
            _log.Add("first-after");
        }
    }

    private class SecondBehavior : IMessagePipelineBehavior
    {
        private readonly List<string> _log;
        public SecondBehavior(List<string> log) => _log = log;

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            _log.Add("second-before");
            await next(context, ct);
            _log.Add("second-after");
        }
    }

    private class ShortCircuitBehavior : IMessagePipelineBehavior
    {
        public Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            // Intentionally not calling next
            return Task.CompletedTask;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    internal static FakeMessageContext CreateContext() => new();

    internal sealed class FakeMessageContext : IMessageContext
    {
        public string EventId { get; set; } = "evt-1";
        public string To { get; set; } = "test-endpoint";
        public string SessionId { get; set; } = "session-1";
        public string CorrelationId { get; set; } = "corr-1";
        public string MessageId { get; set; } = "msg-1";
        public MessageType MessageType { get; set; } = MessageType.EventRequest;
        public MessageContent MessageContent { get; set; } = new();
        public string ParentMessageId { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public int? RetryCount { get; set; }
        public string OriginatingFrom { get; set; } = string.Empty;
        public string EventTypeId { get; set; } = "TestEvent";
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow;
        public string From { get; set; } = string.Empty;
        public string? DeadLetterReason { get; set; }
        public string? DeadLetterErrorDescription { get; set; }
        public string? HandoffReason { get; set; }
        public string? ExternalJobId { get; set; }
        public DateTime? ExpectedBy { get; set; }
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata? HandoffMetadata { get; set; }

        public Task Complete(CancellationToken ct = default) => Task.CompletedTask;
        public Task Abandon(TransientException ex) => Task.CompletedTask;
        public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task BlockSession(CancellationToken ct = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> GetBlockedByEventId(CancellationToken ct = default) => Task.FromResult<string?>(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
    }
}

[TestClass]
public class MessageLifecycleNotifierTests
{
    [TestMethod]
    public async Task NotifyReceived_BroadcastsToAllObservers()
    {
        var observer1 = new RecordingObserver();
        var observer2 = new RecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer1, observer2]);
        var context = CreateContext();

        await notifier.NotifyReceived(context);

        Assert.AreEqual(1, observer1.ReceivedEvents.Count);
        Assert.AreEqual(1, observer2.ReceivedEvents.Count);
    }

    [TestMethod]
    public async Task NotifyFailed_IncludesExceptionInEvent()
    {
        var observer = new RecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var context = CreateContext();
        var exception = new InvalidOperationException("test error");

        await notifier.NotifyFailed(context, exception);

        Assert.AreEqual(1, observer.FailedEvents.Count);
        Assert.AreSame(exception, observer.FailedEvents[0].Exception);
    }

    [TestMethod]
    public async Task NotifyDeadLettered_IncludesReasonAndException()
    {
        var observer = new RecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var context = CreateContext();
        var exception = new InvalidOperationException("test");

        await notifier.NotifyDeadLettered(context, "Max retries exceeded", exception);

        Assert.AreEqual(1, observer.DeadLetteredEvents.Count);
        Assert.AreEqual("Max retries exceeded", observer.DeadLetteredEvents[0].Reason);
    }

    [TestMethod]
    public async Task NotifyDuplicateDetected_IncludesEndpointFromMessageDestination()
    {
        var observer = new RecordingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var context = CreateContext();
        context.To = "billing";

        await notifier.NotifyDuplicateDetected(context);

        Assert.AreEqual(1, observer.DuplicateEvents.Count);
        Assert.AreEqual("billing", observer.DuplicateEvents[0].EndpointId);
    }

    [TestMethod]
    public async Task NoObservers_DoesNotThrow()
    {
        var notifier = new MessageLifecycleNotifier([]);
        var context = CreateContext();

        await notifier.NotifyReceived(context);
        await notifier.NotifyCompleted(context);
        await notifier.NotifyFailed(context, new Exception());
        await notifier.NotifyDeadLettered(context, "test");

        Assert.IsFalse(notifier.HasObservers);
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private class RecordingObserver : IMessageLifecycleObserver
    {
        public List<MessageLifecycleContext> ReceivedEvents { get; } = [];
        public List<MessageLifecycleContext> CompletedEvents { get; } = [];
        public List<(MessageLifecycleContext Context, Exception Exception)> FailedEvents { get; } = [];
        public List<(MessageLifecycleContext Context, string Reason)> DeadLetteredEvents { get; } = [];
        public List<MessageLifecycleContext> DuplicateEvents { get; } = [];

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

        public Task OnDuplicateDetected(MessageLifecycleContext context, CancellationToken ct = default)
        {
            DuplicateEvents.Add(context);
            return Task.CompletedTask;
        }
    }

    private static MessagePipelineTests.FakeMessageContext CreateContext() => new();
}

[TestClass]
public class NimBusBuilderTests
{
    [TestMethod]
    public void AddNimBus_RegistersCoreServices()
    {
        var services = new ServiceCollection();
        services.AddNimBus(b => b.AddInMemoryMessageStore());

        var sp = services.BuildServiceProvider();

        Assert.IsNotNull(sp.GetService<MessagePipeline>());
        Assert.IsNotNull(sp.GetService<MessageLifecycleNotifier>());
    }

    [TestMethod]
    public void AddNimBus_WithoutMessageStore_RegistersCoreServices()
    {
        // Adapters and lightweight hosts can use AddNimBus without ever registering
        // a message-store provider. The pipeline + lifecycle notifier should still
        // be wired up so middleware and observers work.
        var services = new ServiceCollection();
        services.AddNimBus();

        var sp = services.BuildServiceProvider();

        Assert.IsNotNull(sp.GetService<MessagePipeline>());
        Assert.IsNotNull(sp.GetService<MessageLifecycleNotifier>());
    }

    [TestMethod]
    public void AddExtension_CallsConfigureOnExtension()
    {
        var extension = new TestExtension();
        var services = new ServiceCollection();

        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddExtension(extension);
        });

        Assert.IsTrue(extension.WasConfigured);
    }

    [TestMethod]
    public void AddExtension_Generic_CreatesAndConfiguresExtension()
    {
        var services = new ServiceCollection();

        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddExtension<TestExtension>();
        });

        var sp = services.BuildServiceProvider();
        Assert.IsNotNull(sp.GetService<MessagePipeline>());
    }

    [TestMethod]
    public void MultipleExtensions_CanBeRegistered()
    {
        var services = new ServiceCollection();
        var ext1 = new TestExtension();
        var ext2 = new TestExtension();

        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddExtension(ext1);
            builder.AddExtension(ext2);
        });

        Assert.IsTrue(ext1.WasConfigured);
        Assert.IsTrue(ext2.WasConfigured);
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private class TestExtension : INimBusExtension
    {
        public bool WasConfigured { get; private set; }

        public void Configure(INimBusBuilder builder)
        {
            WasConfigured = true;
        }
    }
}
