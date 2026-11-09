using NimBus.Core.Messages;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Diagnostics;

/// <summary>
/// Owns the consumer-side <c>NimBus.Process</c> span and counters / histograms.
/// Transport adapters (e.g. <c>ServiceBusAdapter</c>) call <see cref="RunAsync"/>
/// once per inbound message; the helper opens the span parented to the message's
/// <see cref="IMessageContext.ParentTraceContext"/>, increments
/// <see cref="NimBusMeters.MessagesReceived"/>, runs the inner handler, then records
/// <see cref="NimBusMeters.MessagesProcessed"/> + <see cref="NimBusMeters.ProcessDuration"/>
/// with the outcome and closes the span. The span lifetime covers any broker
/// settle calls the handler makes (FR-012).
/// </summary>
public static class NimBusConsumerInstrumentation
{
    /// <summary>
    /// Wraps an inbound message handler invocation with the canonical consumer
    /// span + counters + duration histogram. <paramref name="messagingSystem"/> is
    /// the value from <see cref="MessagingSystem"/> (e.g. <c>"servicebus"</c>,
    /// <c>"nimbus.inmemory"</c>). Message and correlation identifiers are read from
    /// <paramref name="context"/>.
    /// </summary>
    public static async Task RunAsync(
        IMessageContext context,
        string messagingSystem,
        Func<CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);

        var destination = SafeRead(() => context.To) ?? "unknown";
        var eventType = SafeRead(() => context.EventTypeId) ?? "unknown";

        using var activity = NimBusActivitySources.Consumer.StartActivity(
            "process " + destination,
            ActivityKind.Consumer,
            context.ParentTraceContext);

        var hasParent = context.ParentTraceContext != default;

        if (activity is { IsAllDataRequested: true })
        {
            if (!string.IsNullOrEmpty(messagingSystem))
                activity.SetTag(MessagingAttributes.System, messagingSystem);
            activity.SetTag(MessagingAttributes.OperationType, "process");
            activity.SetTag(MessagingAttributes.DestinationName, destination);
            activity.SetTag(MessagingAttributes.NimBusEventType, eventType);
            var messageId = SafeRead(() => context.MessageId);
            if (!string.IsNullOrEmpty(messageId))
                activity.SetTag(MessagingAttributes.MessageId, messageId);
            var correlationId = SafeRead(() => context.CorrelationId);
            if (!string.IsNullOrEmpty(correlationId))
                activity.SetTag(MessagingAttributes.MessageConversationId, correlationId);
            var sessionId = SafeRead(() => context.SessionId);
            if (!string.IsNullOrEmpty(sessionId))
                activity.SetTag(MessagingAttributes.NimBusSessionKey, sessionId);
            activity.SetTag(MessagingAttributes.NimBusHasParentTrace, hasParent);
        }

        var receivedTags = new TagList
        {
            { MessagingAttributes.DestinationName, destination },
            { MessagingAttributes.NimBusEventType, eventType },
        };
        if (!string.IsNullOrEmpty(messagingSystem))
            receivedTags.Add(MessagingAttributes.System, messagingSystem);
        NimBusMeters.MessagesReceived.Add(1, receivedTags);

        var sw = Stopwatch.StartNew();
        try
        {
            await handler(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            RecordOutcome(activity, receivedTags, sw.Elapsed.TotalMilliseconds, "completed", exception: null);
            context.ProcessingTimeMs = sw.ElapsedMilliseconds;
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordOutcome(activity, receivedTags, sw.Elapsed.TotalMilliseconds, "failed", ex);
            context.ProcessingTimeMs = sw.ElapsedMilliseconds;
            throw;
        }
    }

    private static void RecordOutcome(Activity? activity, TagList baseTags, double elapsedMs, string outcome, Exception? exception)
    {
        var processedTags = baseTags;
        processedTags.Add(MessagingAttributes.NimBusOutcome, outcome);

        NimBusMeters.MessagesProcessed.Add(1, processedTags);
        NimBusMeters.ProcessDuration.Record(elapsedMs, processedTags);

        if (exception is not null && activity is { IsAllDataRequested: true })
        {
            activity.SetTag(MessagingAttributes.ErrorType, exception.GetType().FullName);
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity.AddEvent(new ActivityEvent("exception", default, new ActivityTagsCollection
            {
                { "exception.type", exception.GetType().FullName },
                { "exception.message", exception.Message },
                { "exception.stacktrace", exception.ToString() },
            }));
        }
    }

    // Property reads on IMessageContext can throw (e.g. MessageContext.From
    // throws InvalidMessageException when the user property is missing). The
    // helper must not surface those — telemetry is supposed to be a no-op on
    // malformed input. The properties we actually instrument are nullable in
    // intent; treat a throw as "not present".
    private static string? SafeRead(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}
