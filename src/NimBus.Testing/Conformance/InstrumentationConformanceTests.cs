#pragma warning disable CA1707, CA2007
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Abstract conformance suite for FR-085 — every transport-and-storage
/// combination NimBus supports MUST emit identical instrumentation surface.
/// Concrete subclasses override <see cref="MessagingSystem"/> and the
/// virtual <see cref="PublishAsync"/> hook to plug in their transport.
///
/// The default <see cref="PublishAsync"/> is in-memory: subclasses for real
/// brokers either override the hook or, in the Service Bus case, mark the
/// test <see cref="Assert.Inconclusive(string)"/> when the broker
/// connection-string environment variable is absent.
/// </summary>
[TestClass]
public abstract class InstrumentationConformanceTests
{
    /// <summary>
    /// The expected <c>messaging.system</c> attribute value (e.g.
    /// <c>nimbus.inmemory</c>, <c>servicebus</c>).
    /// </summary>
    protected abstract string MessagingSystem { get; }

    /// <summary>
    /// Sends <paramref name="message"/> through the transport's
    /// instrumented sender path and returns the W3C trace context that the
    /// consumer side would extract from the broker frame.
    /// </summary>
    protected abstract Task<ActivityContext> PublishAsync(IMessage message);

    [TestMethod]
    public virtual async Task Publish_to_consume_produces_single_trace_with_parent_child_spans()
    {
        var activities = new List<Activity>();
        using var listener = StartActivityListener(activities);

        var message = NewMessage("event-1");
        var parentContext = await PublishAsync(message);
        Assert.AreNotEqual(default, parentContext, "PublishAsync must return a real ActivityContext.");

        // Consumer leg: drive NimBusConsumerInstrumentation directly — same helper
        // the transport adapters call. This keeps the conformance harness
        // transport-neutral while still exercising the real consumer span +
        // counter surface.
        var consumerContext = new ConformanceMessageContext(message, parentContext);
        await NimBusConsumerInstrumentation.RunAsync(
            consumerContext, MessagingSystem, _ => Task.CompletedTask);

        var publishSpan = activities.Single(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        var processSpan = activities.Single(a => a.Source.Name == NimBusInstrumentation.ConsumerActivitySourceName);

        Assert.AreEqual(publishSpan.TraceId, processSpan.TraceId,
            $"Publish ({MessagingSystem}) and process must share a trace id (W3C propagation).");
        Assert.AreEqual(publishSpan.SpanId, processSpan.ParentSpanId,
            "Process span must have the publish span as its parent.");
        Assert.AreEqual(ActivityKind.Producer, publishSpan.Kind);
        Assert.AreEqual(ActivityKind.Consumer, processSpan.Kind);
    }

    [TestMethod]
    public virtual async Task Both_legs_emit_documented_messaging_attributes()
    {
        var activities = new List<Activity>();
        using var listener = StartActivityListener(activities);

        var message = NewMessage("event-2");
        var parentContext = await PublishAsync(message);

        var consumerContext = new ConformanceMessageContext(message, parentContext);
        await NimBusConsumerInstrumentation.RunAsync(
            consumerContext, MessagingSystem, _ => Task.CompletedTask);

        var publishSpan = activities.Single(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        var processSpan = activities.Single(a => a.Source.Name == NimBusInstrumentation.ConsumerActivitySourceName);

        // Publisher leg: must carry messaging.system (the InstrumentingSenderDecorator
        // is constructed with that value).
        var publishTags = publishSpan.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.AreEqual(MessagingSystem, publishTags[MessagingAttributes.System],
            $"Publish span must carry messaging.system={MessagingSystem}.");
        AssertCommonMessagingAttributes(publishSpan, "publish");

        // Consumer leg: NimBusConsumerInstrumentation now stamps messaging.system
        // from the caller-supplied value. Assert both legs.
        var processTags = processSpan.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.AreEqual(MessagingSystem, processTags[MessagingAttributes.System],
            $"Process span must carry messaging.system={MessagingSystem}.");
        AssertCommonMessagingAttributes(processSpan, "process");
    }

    private static void AssertCommonMessagingAttributes(Activity span, string operationType)
    {
        var tags = span.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.AreEqual(operationType, tags[MessagingAttributes.OperationType]);
        Assert.IsTrue(tags.ContainsKey(MessagingAttributes.DestinationName), "messaging.destination.name must be set.");
        Assert.IsTrue(tags.ContainsKey(MessagingAttributes.MessageId), "messaging.message.id must be set.");
    }

    private static ActivityListener StartActivityListener(List<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = src =>
                src.Name == NimBusInstrumentation.PublisherActivitySourceName ||
                src.Name == NimBusInstrumentation.ConsumerActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => activities.Add(a),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static Message NewMessage(string suffix) => new()
    {
        To = "endpoint-1",
        EventId = $"event-{suffix}",
        MessageId = $"message-{suffix}",
        SessionId = "session-1",
        CorrelationId = $"conversation-{suffix}",
        EventTypeId = "OrderPlaced",
        MessageType = MessageType.EventRequest,
        OriginatingMessageId = "self",
        ParentMessageId = "self",
        From = "publisher",
        OriginatingFrom = "publisher",
        OriginalSessionId = "session-1",
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        },
    };

    private sealed class ConformanceMessageContext : IMessageContext
    {
        private readonly IMessage _message;

        public ConformanceMessageContext(IMessage message, ActivityContext parentTraceContext)
        {
            _message = message;
            ParentTraceContext = parentTraceContext;
            EnqueuedTimeUtc = DateTime.UtcNow.AddMilliseconds(-10);
        }

        public string EventId => _message.EventId;
        public string To => _message.To;
        public string SessionId => _message.SessionId;
        public string CorrelationId => _message.CorrelationId;
        public string MessageId => _message.MessageId;
        public MessageType MessageType => _message.MessageType;
        public MessageContent MessageContent => _message.MessageContent;
        public string ParentMessageId => _message.ParentMessageId;
        public string OriginatingMessageId => _message.OriginatingMessageId;
        public int? RetryCount => _message.RetryCount;
        public string OriginatingFrom => _message.OriginatingFrom;
        public string EventTypeId => _message.EventTypeId;
        public string OriginalSessionId => _message.OriginalSessionId;
        public int? DeferralSequence => _message.DeferralSequence;
        public DateTime EnqueuedTimeUtc { get; }
        public string From => _message.From;
        public string DeadLetterReason { get; } = string.Empty;
        public string DeadLetterErrorDescription { get; } = string.Empty;
        public string HandoffReason => _message.HandoffReason;
        public string ExternalJobId => _message.ExternalJobId;
        public DateTime? ExpectedBy => _message.ExpectedBy;
        public bool IsDeferred => false;
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata? HandoffMetadata { get; set; }
        public ActivityContext ParentTraceContext { get; }

        public Task Complete(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Abandon(NimBus.Core.Messages.Exceptions.TransientException exception) => Task.CompletedTask;
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
}
