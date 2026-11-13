using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.CircuitBreaker;

/// <summary>Records terminal handler outcomes without changing pipeline semantics.</summary>
public sealed partial class CircuitBreakerRecorderBehavior : IMessagePipelineBehavior
{
    private readonly IEndpointCircuitBreaker _circuitBreaker;
    private readonly ILogger _logger;

    /// <summary>Initializes the recorder without logging.</summary>
    /// <param name="circuitBreaker">The endpoint circuit breaker that receives the outcomes.</param>
    public CircuitBreakerRecorderBehavior(IEndpointCircuitBreaker circuitBreaker)
        : this(circuitBreaker, NullLogger<CircuitBreakerRecorderBehavior>.Instance)
    {
    }

    /// <summary>Initializes the recorder.</summary>
    /// <param name="circuitBreaker">The endpoint circuit breaker that receives the outcomes.</param>
    /// <param name="logger">Logs an outcome that could not be recorded.</param>
    public CircuitBreakerRecorderBehavior(
        IEndpointCircuitBreaker circuitBreaker,
        ILogger<CircuitBreakerRecorderBehavior> logger)
    {
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (IsHeartbeat(context))
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RecordFailure(exception);
            throw;
        }

        // A failed RetryRequest or resubmission, or a discarded failure, is settled by the
        // handler, which returns normally and reports the failure on the context. Recording a
        // success there would let a retry that fails during an outage close a half-open circuit.
        if (context.HandledFailure is { } handledFailure)
            RecordFailure(handledFailure);
        else if (ReachedHandler(context))
            RecordSuccess();
    }

    // Recording is a sidecar and must never change how a message is handled. An exclusion
    // predicate is application code and can throw; escaping from the catch above, its exception
    // would replace the handler's failure, and the message handler would dead-letter an event
    // that was already answered and completed. The outcome is logged and left unrecorded.
    private void RecordFailure(Exception failure)
    {
        try
        {
            _circuitBreaker.RecordFailure(failure);
        }
        catch (Exception recordingFailure)
        {
            LogOutcomeNotRecorded(_logger, recordingFailure, _circuitBreaker.Endpoint);
        }
    }

    private void RecordSuccess()
    {
        try
        {
            _circuitBreaker.RecordSuccess();
        }
        catch (Exception recordingFailure)
        {
            LogOutcomeNotRecorded(_logger, recordingFailure, _circuitBreaker.Endpoint);
        }
    }

    // A delivery answered without running a handler (a skip, a handoff settlement, an inbox
    // duplicate, a stale RetryRequest) never reached the endpoint's dependencies, so it is
    // neither a success nor a failure.
    private static bool ReachedHandler(IMessageContext context) =>
        context.HandlerOutcome is not (HandlerOutcome.DuplicateDetected or HandlerOutcome.NotDispatched);

    private static bool IsHeartbeat(IMessageContext context)
    {
        try
        {
            return string.Equals(context.EventTypeId, Heartbeat.EventTypeId, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidMessageException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The circuit breaker for endpoint {Endpoint} could not record a handler outcome; the outcome is not counted. Check the configured exclusions.")]
    private static partial void LogOutcomeNotRecorded(ILogger logger, Exception exception, string endpoint);
}
