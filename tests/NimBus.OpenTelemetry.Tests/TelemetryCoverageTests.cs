#pragma warning disable CA1707, CA2007
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Azure.Messaging.ServiceBus;
using AzureServiceBusMessage = Azure.Messaging.ServiceBus.ServiceBusMessage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Pipeline;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NimBus.OpenTelemetry.Tests;

[TestClass]
public sealed class W3CMessagePropagatorTests
{
    [TestMethod]
    public void TryParse_InvalidTraceParent_ReturnsDefault()
    {
        var context = W3CMessagePropagator.TryParse("not-a-traceparent", "state=value");

        Assert.AreEqual(default, context);
    }

    [TestMethod]
    public void CaptureCurrent_NoActivity_ReturnsNullHeaders()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var (traceParent, traceState) = W3CMessagePropagator.CaptureCurrent();

            Assert.IsNull(traceParent);
            Assert.IsNull(traceState);
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    [TestMethod]
    public void Capture_And_TryParse_RoundTripsTraceContext()
    {
        using var source = new ActivitySource("nimbus.test.propagator");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("nimbus.test.propagator")
            .Build()!;

        using var activity = source.StartActivity("outer", ActivityKind.Internal);
        Assert.IsNotNull(activity);
        activity.TraceStateString = "vendor=value";

        var (traceParent, traceState) = W3CMessagePropagator.Capture(activity);
        var parsed = W3CMessagePropagator.TryParse(traceParent, traceState);

        Assert.AreEqual(activity.TraceId, parsed.TraceId);
        Assert.AreEqual(activity.SpanId, parsed.SpanId);
        Assert.AreEqual("vendor=value", parsed.TraceState);
    }
}

[TestClass]
public sealed class MessageHelperTracePropagationTests
{
    [TestMethod]
    public void ToServiceBusMessage_WritesTraceParentAndTraceStateFromCurrentActivity()
    {
        using var source = new ActivitySource("nimbus.test.messagehelper");
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("nimbus.test.messagehelper")
            .Build()!;

        using var activity = source.StartActivity("outer", ActivityKind.Producer);
        Assert.IsNotNull(activity);
        activity.TraceStateString = "vendor=value";

        var result = MessageHelper.ToServiceBusMessage(CreateMessage());

        Assert.AreEqual(activity.Id, result.ApplicationProperties[W3CMessagePropagator.TraceParentHeader]);
        Assert.AreEqual("vendor=value", result.ApplicationProperties[W3CMessagePropagator.TraceStateHeader]);
        Assert.IsFalse(result.ApplicationProperties.ContainsKey("Diagnostic-Id"));
    }

    [TestMethod]
    public void ToServiceBusMessage_NoCurrentActivity_DoesNotWriteTraceHeaders()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var result = MessageHelper.ToServiceBusMessage(CreateMessage());

            Assert.IsFalse(result.ApplicationProperties.ContainsKey(W3CMessagePropagator.TraceParentHeader));
            Assert.IsFalse(result.ApplicationProperties.ContainsKey(W3CMessagePropagator.TraceStateHeader));
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    private static Message CreateMessage() => new()
    {
        To = "orders",
        From = "publisher",
        EventId = "event-1",
        MessageId = "message-1",
        SessionId = "session-1",
        CorrelationId = "conversation-1",
        EventTypeId = "OrderPlaced",
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent
            {
                EventTypeId = "OrderPlaced",
                EventJson = "{}",
            },
        },
    };
}

[TestClass]
public sealed class ConsumerInstrumentationTests
{
    [TestMethod]
    public async Task Handle_Success_EmitsProcessSpanMetricsAndCurrentActivity()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);
        var context = new TestMessageContext
        {
            ParentTraceContext = parent,
        };
        Activity? activityDuringHandler = null;

        await NimBusConsumerInstrumentation.RunAsync(context, MessagingSystem.ServiceBus, _ =>
        {
            activityDuringHandler = Activity.Current;
            Activity.Current?.SetTag("tenant.id", "tenant-1");
            return Task.CompletedTask;
        });
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        var span = activities.Single(a => a.Source.Name == NimBusInstrumentation.ConsumerActivitySourceName);
        Assert.AreEqual("process endpoint-1", span.DisplayName);
        Assert.AreEqual(ActivityKind.Consumer, span.Kind);
        Assert.AreEqual(parent.TraceId, span.TraceId);
        Assert.AreEqual(parent.SpanId, span.ParentSpanId);
        Assert.AreSame(activityDuringHandler, span);

        var tags = span.TagObjects.ToDictionary(t => t.Key, t => t.Value);
        Assert.AreEqual("process", tags[MessagingAttributes.OperationType]);
        Assert.AreEqual("endpoint-1", tags[MessagingAttributes.DestinationName]);
        Assert.AreEqual("OrderPlaced", tags[MessagingAttributes.NimBusEventType]);
        Assert.AreEqual("message-1", tags[MessagingAttributes.MessageId]);
        Assert.AreEqual("conversation-1", tags[MessagingAttributes.MessageConversationId]);
        Assert.AreEqual("session-1", tags[MessagingAttributes.NimBusSessionKey]);
        Assert.AreEqual(true, tags[MessagingAttributes.NimBusHasParentTrace]);
        Assert.AreEqual("tenant-1", tags["tenant.id"]);

        CollectionAssert.Contains(metrics.Select(m => m.Name).ToList(), "nimbus.message.received");
        CollectionAssert.Contains(metrics.Select(m => m.Name).ToList(), "nimbus.message.processed");
        CollectionAssert.Contains(metrics.Select(m => m.Name).ToList(), "nimbus.message.process.duration");
        AssertNoHighCardinalityMetricTags(metrics);
    }

    [TestMethod]
    public async Task Handle_Failure_RecordsErrorStatusAndExceptionEvent()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            NimBusConsumerInstrumentation.RunAsync(
                new TestMessageContext(), MessagingSystem.ServiceBus,
                _ => throw new InvalidOperationException("boom")));
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        Assert.AreEqual("boom", exception.Message);
        var span = activities.Single(a => a.Source.Name == NimBusInstrumentation.ConsumerActivitySourceName);
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);

        var tags = span.TagObjects.ToDictionary(t => t.Key, t => t.Value?.ToString());
        Assert.AreEqual(typeof(InvalidOperationException).FullName, tags[MessagingAttributes.ErrorType]);
        Assert.IsTrue(span.Events.Any(e => e.Name == "exception"));
        AssertNoHighCardinalityMetricTags(metrics);
    }

    private static void AssertNoHighCardinalityMetricTags(IEnumerable<Metric> metrics)
    {
        var denied = new[]
        {
            MessagingAttributes.MessageId,
            MessagingAttributes.MessageConversationId,
            MessagingAttributes.NimBusSessionKey,
        };

        var keys = MetricTagKeys(metrics).ToList();
        foreach (var key in denied)
        {
            CollectionAssert.DoesNotContain(keys, key);
        }
    }

    private static IEnumerable<string> MetricTagKeys(IEnumerable<Metric> metrics)
    {
        foreach (var metric in metrics)
        {
            foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            {
                foreach (var tag in metricPoint.Tags)
                {
                    yield return tag.Key;
                }
            }
        }
    }
}

[TestClass]
public sealed class SenderDecoratorCoverageTests
{
    [TestMethod]
    public async Task Send_Batch_EmitsSinglePublishSpanAndAggregatedMetrics()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var inner = new RecordingSender();
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);

        await sut.Send(new[]
        {
            CreateMessage("message-1"),
            CreateMessage("message-2"),
        });
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        Assert.AreEqual(2, inner.SendCount);
        var span = activities.Single(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        Assert.AreEqual("publish endpoint-1", span.DisplayName);

        var published = metrics.Single(m => m.Name == "nimbus.message.published");
        Assert.AreEqual(2, SumLong(published));
        AssertNoHighCardinalityMetricTags(metrics);

        // P1.9 from the PR #42 review: assert the duration histogram by name.
        // Without this assertion a future regression deleting the
        // PublishDuration.Record call would pass CI silently.
        var publishDuration = metrics.Single(m => m.Name == "nimbus.message.publish.duration");
        long observations = 0;
        foreach (ref readonly var point in publishDuration.GetMetricPoints())
            observations += point.GetHistogramCount();
        Assert.IsTrue(observations >= 1, "nimbus.message.publish.duration must record at least one observation per Send");
    }

    [TestMethod]
    public async Task ScheduleMessage_EmitsPublishSpanAndDelegatesToInnerSender()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var inner = new RecordingSender();
        var sut = NimBusOpenTelemetryDecorators.InstrumentSender(inner, MessagingSystem.InMemory);

        var sequence = await sut.ScheduleMessage(CreateMessage("message-1"), DateTimeOffset.UtcNow.AddMinutes(5));
        tracer.ForceFlush();

        Assert.AreEqual(42L, sequence);
        Assert.AreEqual(1, inner.ScheduleCount);
        Assert.IsTrue(activities.Any(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName));
    }

    private static long SumLong(Metric metric)
    {
        long sum = 0;
        foreach (ref readonly var metricPoint in metric.GetMetricPoints())
        {
            sum += metricPoint.GetSumLong();
        }

        return sum;
    }

    private static void AssertNoHighCardinalityMetricTags(IEnumerable<Metric> metrics)
    {
        var denied = new[]
        {
            MessagingAttributes.MessageId,
            MessagingAttributes.MessageConversationId,
            MessagingAttributes.NimBusSessionKey,
        };

        var keys = new List<string>();
        foreach (var metric in metrics)
        {
            foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            {
                foreach (var tag in metricPoint.Tags)
                {
                    keys.Add(tag.Key);
                }
            }
        }

        foreach (var key in denied)
        {
            CollectionAssert.DoesNotContain(keys, key);
        }
    }

    private static Message CreateMessage(string messageId) => new()
    {
        To = "endpoint-1",
        EventId = "event-1",
        MessageId = messageId,
        SessionId = "session-1",
        CorrelationId = "conversation-1",
        EventTypeId = "OrderPlaced",
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent(),
    };
}

[TestClass]
public sealed class ServiceBusAdapterInstrumentationBoundaryTests
{
    [TestMethod]
    public async Task Handle_ExtractsParentTraceContextAndRecordsTransportMetrics()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;
        using var source = new ActivitySource("nimbus.test.adapter");
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource("nimbus.test.adapter")
            .Build()!;
        using var parent = source.StartActivity("publisher", ActivityKind.Producer);
        Assert.IsNotNull(parent);

        var properties = new Dictionary<string, object>
        {
            [UserPropertyName.To.ToString()] = "endpoint-1",
            [UserPropertyName.From.ToString()] = "publisher",
            [UserPropertyName.MessageType.ToString()] = MessageType.EventRequest.ToString(),
            [UserPropertyName.EventId.ToString()] = "event-1",
            [UserPropertyName.EventTypeId.ToString()] = "OrderPlaced",
            [UserPropertyName.OriginatingMessageId.ToString()] = Constants.Self,
            [UserPropertyName.ParentMessageId.ToString()] = Constants.Self,
            [UserPropertyName.RetryCount.ToString()] = 0,
            [UserPropertyName.OriginatingFrom.ToString()] = "publisher",
            [W3CMessagePropagator.TraceParentHeader] = parent.Id!,
        };
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: "message-1",
            sessionId: "session-1",
            correlationId: "conversation-1",
            properties: properties,
            enqueuedTime: DateTimeOffset.UtcNow.AddSeconds(-5));
        var handler = new RecordingMessageHandler();
        var adapter = new ServiceBusAdapter(handler);

        await adapter.Handle(received, new RecordingServiceBusSessionReceiver());
        meterProvider.ForceFlush();

        Assert.IsNotNull(handler.LastContext);
        Assert.AreEqual(parent.TraceId, handler.LastContext.ParentTraceContext.TraceId);
        Assert.AreEqual(parent.SpanId, handler.LastContext.ParentTraceContext.SpanId);
        Assert.IsTrue(handler.LastContext.QueueTimeMs >= 0);
        Assert.IsNotNull(handler.LastContext.HandlerStartedAtUtc);
        CollectionAssert.Contains(metrics.Select(m => m.Name).ToList(), "nimbus.message.queue_wait");
        CollectionAssert.Contains(metrics.Select(m => m.Name).ToList(), "nimbus.message.e2e_latency");
    }
}

[TestClass]
public sealed class OpenTelemetryDiIntegrationTests
{
    [TestMethod]
    public void AddNimBusInstrumentation_BindsConfigurationOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NimBus:Otel:Verbose"] = "true",
                ["NimBus:Otel:IncludeMessageHeaders"] = "true",
                ["NimBus:Otel:GaugePollInterval"] = "00:00:05",
                ["NimBus:Otel:OutboxLagWarnThreshold"] = "00:02:00",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddNimBusInstrumentation(options => options.Verbose = false);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<NimBusOpenTelemetryOptions>>().Value;

        Assert.IsFalse(options.Verbose, "explicit callback should override configuration");
        Assert.IsTrue(options.IncludeMessageHeaders);
        Assert.AreEqual(TimeSpan.FromSeconds(5), options.GaugePollInterval);
        Assert.AreEqual(TimeSpan.FromMinutes(2), options.OutboxLagWarnThreshold);
    }

    [TestMethod]
    public async Task AddNimBusPublisher_UsesInstrumentedSender()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var client = new RecordingServiceBusClient();
        var services = new ServiceCollection();
        services.AddSingleton<ServiceBusClient>(client);
        services.AddNimBusPublisher("orders");

        using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<IPublisherClient>();

        await publisher.Publish(new TestEvent { SessionIdValue = "session-42" }, "session-42", "conversation-42", "message-42");
        tracer.ForceFlush();

        Assert.AreEqual("orders", client.LastSenderEntityPath);
        Assert.AreEqual(1, client.Sender.SentMessages.Count);
        Assert.AreEqual(1, activities.Count(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName));
        Assert.IsTrue(client.Sender.SentMessages[0].ApplicationProperties.ContainsKey(W3CMessagePropagator.TraceParentHeader));
    }
}

[TestClass]
public sealed class PublishConsumeTraceIntegrationTests
{
    [TestMethod]
    public async Task InstrumentedPublish_ToConsumerMiddleware_ProducesSingleTrace()
    {
        var activities = new List<Activity>();
        var metrics = new List<Metric>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var sender = new CapturingServiceBusSender();
        var instrumentedSender = NimBusOpenTelemetryDecorators.InstrumentSender(sender, MessagingSystem.InMemory);

        await instrumentedSender.Send(new Message
        {
            To = "endpoint-1",
            EventId = "event-1",
            MessageId = "message-1",
            SessionId = "session-1",
            CorrelationId = "conversation-1",
            EventTypeId = "OrderPlaced",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent(),
        });

        var sent = sender.SentMessages.Single();
        var parentContext = W3CMessagePropagator.TryParse(
            sent.ApplicationProperties[W3CMessagePropagator.TraceParentHeader]?.ToString(),
            sent.ApplicationProperties.TryGetValue(W3CMessagePropagator.TraceStateHeader, out var state) ? state?.ToString() : null);

        await NimBusConsumerInstrumentation.RunAsync(
            new TestMessageContext { ParentTraceContext = parentContext },
            MessagingSystem.ServiceBus,
            _ => Task.CompletedTask);
        meterProvider.ForceFlush();
        tracer.ForceFlush();

        var publish = activities.Single(a => a.Source.Name == NimBusInstrumentation.PublisherActivitySourceName);
        var process = activities.Single(a => a.Source.Name == NimBusInstrumentation.ConsumerActivitySourceName);
        Assert.AreEqual(publish.TraceId, process.TraceId);
        Assert.AreEqual(publish.SpanId, process.ParentSpanId);
        AssertNoHighCardinalityMetricTags(metrics);
    }

    private static void AssertNoHighCardinalityMetricTags(IEnumerable<Metric> metrics)
    {
        var denied = new[]
        {
            MessagingAttributes.MessageId,
            MessagingAttributes.MessageConversationId,
            MessagingAttributes.NimBusSessionKey,
        };

        var keys = new List<string>();
        foreach (var metric in metrics)
        {
            foreach (ref readonly var metricPoint in metric.GetMetricPoints())
            {
                foreach (var tag in metricPoint.Tags)
                {
                    keys.Add(tag.Key);
                }
            }
        }

        foreach (var key in denied)
        {
            CollectionAssert.DoesNotContain(keys, key);
        }
    }
}

internal sealed class TestMessageContext : IMessageContext
{
    public string EventId { get; set; } = "event-1";
    public string To { get; set; } = "endpoint-1";
    public string SessionId { get; set; } = "session-1";
    public string CorrelationId { get; set; } = "conversation-1";
    public string MessageId { get; set; } = "message-1";
    public MessageType MessageType { get; set; } = MessageType.EventRequest;
    public MessageContent MessageContent { get; set; } = new();
    public string ParentMessageId { get; set; } = Constants.Self;
    public string OriginatingMessageId { get; set; } = Constants.Self;
    public int? RetryCount { get; set; }
    public string OriginatingFrom { get; set; } = "publisher";
    public string EventTypeId { get; set; } = "OrderPlaced";
    public string OriginalSessionId { get; set; } = string.Empty;
    public int? DeferralSequence { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow.AddSeconds(-1);
    public string From { get; set; } = "publisher";
    public string DeadLetterReason { get; set; } = string.Empty;
    public string DeadLetterErrorDescription { get; set; } = string.Empty;
    public string HandoffReason { get; set; } = string.Empty;
    public string ExternalJobId { get; set; } = string.Empty;
    public DateTime? ExpectedBy { get; set; }
    public bool IsDeferred { get; set; }
    public int ThrottleRetryCount { get; set; }
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

internal sealed class RecordingSender : ISender
{
    public int SendCount { get; private set; }
    public int ScheduleCount { get; private set; }

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        SendCount++;
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        SendCount += messages.Count();
        return Task.CompletedTask;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        ScheduleCount++;
        return Task.FromResult(42L);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class CapturingServiceBusSender : ISender
{
    public List<AzureServiceBusMessage> SentMessages { get; } = new();

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(MessageHelper.ToServiceBusMessage(message, messageEnqueueDelay));
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        SentMessages.AddRange(messages.Select(m => MessageHelper.ToServiceBusMessage(m, messageEnqueueDelay)));
        return Task.CompletedTask;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(MessageHelper.ToServiceBusMessage(message));
        return Task.FromResult(1L);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RecordingServiceBusClient : ServiceBusClient
{
    public RecordingServiceBusSender Sender { get; } = new();
    public string? LastSenderEntityPath { get; private set; }

    public override ServiceBusSender CreateSender(string queueOrTopicName)
    {
        LastSenderEntityPath = queueOrTopicName;
        return Sender;
    }

    public override ServiceBusSender CreateSender(string queueOrTopicName, ServiceBusSenderOptions options)
    {
        LastSenderEntityPath = queueOrTopicName;
        return Sender;
    }
}

[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class RecordingServiceBusSender : ServiceBusSender
{
    public List<AzureServiceBusMessage> SentMessages { get; } = new();

    public override Task SendMessageAsync(AzureServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }

    public override Task SendMessagesAsync(IEnumerable<AzureServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        SentMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public override Task<long> ScheduleMessageAsync(AzureServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        return Task.FromResult(1L);
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class RecordingServiceBusSessionReceiver : ServiceBusSessionReceiver
{
    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingMessageHandler : IMessageHandler
{
    public IMessageContext? LastContext { get; private set; }

    public Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        LastContext = messageContext;
        return Task.CompletedTask;
    }
}

internal sealed class TestEvent : Event
{
    public string SessionIdValue { get; set; } = "session-1";

    public override string GetSessionId() => SessionIdValue;
}
