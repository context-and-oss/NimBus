#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Testing;
using NimBus.Core.Messages;
using NimBus.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

// ── Shared test context ────────────────────────────────────────────

file sealed class TestMessageContext : IMessageContext
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

    public int DeadLetterCalls { get; private set; }
    public int CompleteCalls { get; private set; }

    public Task Complete(CancellationToken ct = default) { CompleteCalls++; return Task.CompletedTask; }
    public Task Abandon(NimBus.Core.Messages.Exceptions.TransientException ex) => Task.CompletedTask;
    public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) { DeadLetterCalls++; return Task.CompletedTask; }
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

file sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add(formatter(state, exception));
    }
}

// ── LoggingMiddleware Tests ────────────────────────────────────────

[TestClass]
public class LoggingMiddlewareTests
{
    [TestMethod]
    public async Task Handle_LogsProcessingAndCompletion()
    {
        var logger = new RecordingLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = new TestMessageContext();
        var handlerCalled = false;

        await middleware.Handle(context, (ctx, ct) => { handlerCalled = true; return Task.CompletedTask; });

        Assert.IsTrue(handlerCalled);
        Assert.IsTrue(logger.Entries.Any(e => e.Contains("Processing")));
        Assert.IsTrue(logger.Entries.Any(e => e.Contains("Completed")));
    }

    [TestMethod]
    public async Task Handle_LogsFailureOnException()
    {
        var logger = new RecordingLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = new TestMessageContext();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            middleware.Handle(context, (ctx, ct) => throw new InvalidOperationException("boom")));

        Assert.IsTrue(logger.Entries.Any(e => e.Contains("Failed")));
        Assert.IsFalse(logger.Entries.Any(e => e.Contains("Completed")));
    }

    [TestMethod]
    public async Task Handle_IncludesEventMetadata()
    {
        var logger = new RecordingLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = new TestMessageContext { EventTypeId = "OrderPlaced", EventId = "evt-42" };

        await middleware.Handle(context, (ctx, ct) => Task.CompletedTask);

        var allText = string.Join(" ", logger.Entries);
        Assert.IsTrue(allText.Contains("OrderPlaced"));
        Assert.IsTrue(allText.Contains("evt-42"));
    }

    [TestMethod]
    public async Task Handle_RethrowsOriginalException()
    {
        var logger = new RecordingLogger<LoggingMiddleware>();
        var middleware = new LoggingMiddleware(logger);
        var context = new TestMessageContext();

        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            middleware.Handle(context, (ctx, ct) => throw new ArgumentException("original")));

        Assert.AreEqual("original", ex.Message);
    }
}

// ── ValidationMiddleware Tests ─────────────────────────────────────

[TestClass]
public class ValidationMiddlewareTests
{
    [TestMethod]
    public async Task Handle_ValidMessage_PassesThrough()
    {
        var middleware = CreateMiddleware();
        var context = new TestMessageContext { EventId = "evt-1", EventTypeId = "OrderPlaced" };
        var called = false;

        await middleware.Handle(context, (ctx, ct) => { called = true; return Task.CompletedTask; });

        Assert.IsTrue(called);
        Assert.AreEqual(0, context.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_MissingEventId_DeadLettersAndThrows()
    {
        var middleware = CreateMiddleware();
        var context = new TestMessageContext { EventId = "" };
        var called = false;

        await Assert.ThrowsExactlyAsync<MessageAlreadyDeadLetteredException>(() =>
            middleware.Handle(context, (ctx, ct) => { called = true; return Task.CompletedTask; }));

        Assert.IsFalse(called, "Handler should not be called");
        Assert.AreEqual(1, context.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_NullEventId_DeadLettersAndThrows()
    {
        var middleware = CreateMiddleware();
        var context = new TestMessageContext { EventId = null! };
        var called = false;

        await Assert.ThrowsExactlyAsync<MessageAlreadyDeadLetteredException>(() =>
            middleware.Handle(context, (ctx, ct) => { called = true; return Task.CompletedTask; }));

        Assert.IsFalse(called);
        Assert.AreEqual(1, context.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_EventRequestMissingEventTypeId_DeadLettersAndThrows()
    {
        var middleware = CreateMiddleware();
        var context = new TestMessageContext
        {
            EventId = "evt-1",
            EventTypeId = "",
            MessageType = MessageType.EventRequest
        };
        var called = false;

        await Assert.ThrowsExactlyAsync<MessageAlreadyDeadLetteredException>(() =>
            middleware.Handle(context, (ctx, ct) => { called = true; return Task.CompletedTask; }));

        Assert.IsFalse(called);
        Assert.AreEqual(1, context.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_NonEventRequestMissingEventTypeId_PassesThrough()
    {
        var middleware = CreateMiddleware();
        var context = new TestMessageContext
        {
            EventId = "evt-1",
            EventTypeId = "",
            MessageType = MessageType.ResolutionResponse
        };
        var called = false;

        await middleware.Handle(context, (ctx, ct) => { called = true; return Task.CompletedTask; });

        Assert.IsTrue(called, "Non-EventRequest should pass without EventTypeId");
        Assert.AreEqual(0, context.DeadLetterCalls);
    }

    private static ValidationMiddleware CreateMiddleware()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        return new ValidationMiddleware(loggerFactory.CreateLogger<ValidationMiddleware>());
    }
}

// ── Pipeline wiring integration test ───────────────────────────────

[TestClass]
public class PipelineWiringTests
{
    [TestMethod]
    public async Task AddNimBus_WithBehaviors_BehaviorsExecuteInPipeline()
    {
        var log = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(log);
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<TrackingBehavior>();
        });

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();

        Assert.IsTrue(pipeline.HasBehaviors);

        var context = new TestMessageContext();
        await pipeline.Execute(context, (ctx, ct) =>
        {
            log.Add("handler");
            return Task.CompletedTask;
        });

        CollectionAssert.AreEqual(new[] { "track-before", "handler", "track-after" }, log);
    }

    [TestMethod]
    public async Task BuiltInMiddleware_CanBeRegisteredViaAddNimBus()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<LoggingMiddleware>();
            builder.AddPipelineBehavior<ValidationMiddleware>();
        });

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();

        Assert.IsTrue(pipeline.HasBehaviors);

        var context = new TestMessageContext { EventId = "evt-1", EventTypeId = "Test" };
        var called = false;

        await pipeline.Execute(context, (ctx, ct) => { called = true; return Task.CompletedTask; });

        Assert.IsTrue(called, "All 3 middleware should pass a valid message through");
    }

    [TestMethod]
    public async Task BuiltInMiddleware_ValidationRejectsInvalidMessage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNimBus(builder =>
        {
            builder.AddInMemoryMessageStore();
            builder.AddPipelineBehavior<ValidationMiddleware>();
        });

        var sp = services.BuildServiceProvider();
        var pipeline = sp.GetRequiredService<MessagePipeline>();

        var context = new TestMessageContext { EventId = "" };
        var called = false;

        await Assert.ThrowsExactlyAsync<MessageAlreadyDeadLetteredException>(() =>
            pipeline.Execute(context, (ctx, ct) => { called = true; return Task.CompletedTask; }));

        Assert.IsFalse(called, "Validation should reject message with empty EventId");
        Assert.AreEqual(1, context.DeadLetterCalls);
    }

    private sealed class TrackingBehavior : IMessagePipelineBehavior
    {
        private readonly List<string> _log;
        public TrackingBehavior(List<string> log) => _log = log;

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken ct = default)
        {
            _log.Add("track-before");
            await next(context, ct);
            _log.Add("track-after");
        }
    }
}
