using System.Diagnostics.Metrics;

namespace NimBus.Core.Diagnostics;

/// <summary>
/// Holds the framework-level <see cref="Meter"/> instances and pre-created instruments used by
/// every NimBus instrumentation site. Public so transport providers and the
/// <c>NimBus.OpenTelemetry</c> package can record measurements on the canonical meters;
/// external consumers should normally subscribe via the names exposed on
/// <see cref="NimBusInstrumentation"/> rather than referencing these instances.
/// </summary>
public static class NimBusMeters
{
    public static readonly Meter Publisher = new(NimBusInstrumentation.PublisherMeterName);

    public static readonly Counter<long> MessagesPublished = Publisher.CreateCounter<long>(
        "nimbus.message.published", "{messages}", "Messages handed to a transport sender.");

    public static readonly Histogram<double> PublishDuration = Publisher.CreateHistogram<double>(
        "nimbus.message.publish.duration", "ms", "Time spent in the publish path, including transport send.");

    public static readonly Counter<long> PublishFailed = Publisher.CreateCounter<long>(
        "nimbus.message.publish.failed", "{messages}", "Publish attempts that threw.");

    public static readonly Meter Consumer = new(NimBusInstrumentation.ConsumerMeterName);

    public static readonly Counter<long> MessagesReceived = Consumer.CreateCounter<long>(
        "nimbus.message.received", "{messages}", "Messages dispatched to the consumer pipeline.");

    public static readonly Counter<long> MessagesProcessed = Consumer.CreateCounter<long>(
        "nimbus.message.processed", "{messages}", "Messages whose pipeline run completed (any outcome).");

    public static readonly Histogram<double> ProcessDuration = Consumer.CreateHistogram<double>(
        "nimbus.message.process.duration", "ms", "Time spent processing a message through the consumer pipeline.");

    public static readonly Histogram<double> QueueWait = Consumer.CreateHistogram<double>(
        "nimbus.message.queue_wait", "ms", "Time the message spent in the broker before consumer entry.");

    public static readonly Histogram<double> EndToEndLatency = Consumer.CreateHistogram<double>(
        "nimbus.message.e2e_latency", "ms", "End-to-end broker-enqueue to handler-completion latency.");

    public static readonly Counter<long> InboxDuplicatesDetected = Consumer.CreateCounter<long>(
        "nimbus.inbox.duplicate_detected", "{messages}", "Messages skipped because their inbox record already exists.");

    public static readonly Counter<long> InboxOperationFailed = Consumer.CreateCounter<long>(
        "nimbus.inbox.operation.failed", "{operations}", "Inbox store operations that failed.");

    public static readonly Counter<long> CircuitBreakerTransitions = Consumer.CreateCounter<long>(
        "nimbus.circuit_breaker.transitions_total", "{transitions}", "Endpoint circuit breaker state transitions.");

    public static readonly Meter Outbox = new(NimBusInstrumentation.OutboxMeterName);

    public static readonly Counter<long> OutboxEnqueued = Outbox.CreateCounter<long>(
        "nimbus.outbox.enqueued", "{messages}", "Messages persisted to the transactional outbox.");

    public static readonly Counter<long> OutboxDispatched = Outbox.CreateCounter<long>(
        "nimbus.outbox.dispatched", "{messages}", "Outbox rows successfully forwarded by the dispatcher.");

    public static readonly Histogram<double> OutboxDispatchDuration = Outbox.CreateHistogram<double>(
        "nimbus.outbox.dispatch.duration", "ms", "Time the dispatcher took to forward a single outbox row.");

    /// <summary>
    /// <c>nimbus.outbox.parked</c>: rows the dispatcher parked, tagged with
    /// <c>nimbus.endpoint</c> and <c>error.type</c>. Any increment needs operator attention.
    /// </summary>
    public static readonly Counter<long> OutboxParked =Outbox.CreateCounter<long>(
        "nimbus.outbox.parked", "{messages}", "Outbox rows parked after exhausting their dispatch attempts or failing deterministically.");

    public static readonly Meter DeferredProcessor = new(NimBusInstrumentation.DeferredProcessorMeterName);

    public static readonly Counter<long> DeferredParked = DeferredProcessor.CreateCounter<long>(
        "nimbus.deferred.parked", "{messages}", "Messages parked by the session-block deferred path.");

    public static readonly Counter<long> DeferredReplayed = DeferredProcessor.CreateCounter<long>(
        "nimbus.deferred.replayed", "{messages}", "Messages replayed from the deferred subscription on session unblock.");

    public static readonly Histogram<double> DeferredReplayDuration = DeferredProcessor.CreateHistogram<double>(
        "nimbus.deferred.replay.duration", "ms", "Time taken to replay one batch of deferred messages.");

    public static readonly Meter Resolver = new(NimBusInstrumentation.ResolverMeterName);

    public static readonly Counter<long> ResolverOutcomeWritten = Resolver.CreateCounter<long>(
        "nimbus.resolver.outcome_written", "{records}", "Outcome rows written by the resolver.");

    // Spec 030: a non-terminal write the store refused because the row already held a later
    // outcome. Together with outcome_written this is one per handled message, so a dashboard
    // that used outcome_written as throughput should sum the two.
    public static readonly Counter<long> ResolverOutcomeIgnored = Resolver.CreateCounter<long>(
        "nimbus.resolver.outcome_ignored", "{records}", "Outcome writes the store refused as stale.");

    public static readonly Counter<long> ResolverAuditWritten = Resolver.CreateCounter<long>(
        "nimbus.resolver.audit_written", "{records}", "Audit rows written by the resolver.");

    public static readonly Histogram<double> ResolverWriteDuration = Resolver.CreateHistogram<double>(
        "nimbus.resolver.write.duration", "ms", "Time the resolver took to persist a single outcome or audit row.");

    /// <summary>
    /// One event per message the Resolver could not persist on this attempt, tagged with
    /// <see cref="MessagingAttributes.NimBusEndpoint"/>, <see cref="MessagingAttributes.NimBusStoreReason"/>
    /// (<see cref="StoreRetryReason"/>) and <see cref="MessagingAttributes.NimBusRetryAction"/>
    /// (<see cref="RetryAction"/>). Recorded after the settlement call succeeded. A sustained
    /// <see cref="StoreRetryReason.Throttled"/> rate is the direct signal that the store is pushing back.
    /// </summary>
    public static readonly Counter<long> ResolverStoreRetry = Resolver.CreateCounter<long>(
        "nimbus.resolver.store_retry", "{events}", "Resolver messages rescheduled, dead-lettered or abandoned because the store could not persist them.");

    /// <summary>
    /// The delay the Resolver actually applied when it rescheduled a message, tagged with
    /// <see cref="MessagingAttributes.NimBusEndpoint"/> and <see cref="MessagingAttributes.NimBusDelaySource"/>
    /// (<see cref="DelaySource"/>). Recorded only after the scheduled re-send succeeded, so it is how
    /// far the copy was pushed behind its session, not the store's raw hint.
    /// </summary>
    public static readonly Histogram<double> ResolverRetryDelay = Resolver.CreateHistogram<double>(
        "nimbus.resolver.retry.delay", "s", "Applied delay before a rescheduled Resolver message is redelivered.");

    public static readonly Meter Store = new(NimBusInstrumentation.StoreMeterName);

    public static readonly Histogram<double> StoreOperationDuration = Store.CreateHistogram<double>(
        "nimbus.store.operation.duration", "ms", "Time taken by a single message-store operation.");

    public static readonly Counter<long> StoreOperationFailed = Store.CreateCounter<long>(
        "nimbus.store.operation.failed", "{ops}", "Message-store operations that threw.");

    /// <summary>
    /// Rows the message store deleted because their retention period ran out (Spec 050), tagged with
    /// <c>nimbus.retention.period</c>: <c>open_events</c>, <c>resolved_events</c>, <c>message_history</c> or
    /// <c>audit_trail</c>. Recorded by the SQL Server cleanup; Cosmos DB expires data by TTL and records nothing.
    /// </summary>
    public static readonly Counter<long> RetentionDeleted = Store.CreateCounter<long>(
        "nimbus.retention.deleted", "{rows}", "Rows deleted because their retention period ran out.");
}
