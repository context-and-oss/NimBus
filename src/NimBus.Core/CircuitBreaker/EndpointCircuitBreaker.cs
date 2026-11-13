using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using NimBus.Core.Diagnostics;

namespace NimBus.Core.CircuitBreaker;

/// <summary>Thread-safe sliding-window circuit breaker for a subscriber endpoint.</summary>
public sealed class EndpointCircuitBreaker : IEndpointCircuitBreaker
{
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly ConcurrentQueue<CircuitStateChange> _pendingPublications = new();
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<Outcome> _outcomes = [];
    private TaskCompletionSource<CircuitStateChange> _stateChangeSignal = CreateSignal();
    private CircuitState _state;
    private DateTimeOffset _openedAt;
    private int _halfOpenSuccesses;

    /// <summary>Initializes an endpoint circuit breaker.</summary>
    public EndpointCircuitBreaker(string endpoint, CircuitBreakerOptions options, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("Endpoint must be specified.", nameof(endpoint));

        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Endpoint = endpoint;
    }

    /// <inheritdoc />
    public event Action<CircuitStateChange>? StateChanged;

    /// <inheritdoc />
    public string Endpoint { get; }

    /// <inheritdoc />
    public CircuitState State
    {
        get
        {
            CircuitStateChange? change;
            CircuitState state;
            lock (_gate)
            {
                change = AdvanceOpenCircuitLocked(_timeProvider.GetUtcNow());
                state = _state;
            }

            Publish(change);
            return state;
        }
    }

    /// <inheritdoc />
    public void RecordSuccess()
    {
        CircuitStateChange? change;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            change = AdvanceOpenCircuitLocked(now);
            if (change is null && _state == CircuitState.Closed)
            {
                // Recorded but not evaluated: a success lowers the failure rate and can only
                // reach the threshold by bringing the window up to MinimumThroughput. A failed
                // event blocks its session, so after a short blip the failures stop while other
                // sessions keep succeeding; opening on one of those successes would pause an
                // endpoint whose dependency has already recovered. Only a failure opens.
                _outcomes.Enqueue(new Outcome(now, Failed: false));
                TrimLocked(now);
            }
            else if (change is null && _state == CircuitState.HalfOpen)
            {
                _halfOpenSuccesses++;
                if (_halfOpenSuccesses >= _options.HalfOpenProbeCount)
                {
                    _outcomes.Clear();
                    change = TransitionLocked(CircuitState.Closed, "Half-open probes succeeded.", now);
                }
            }
        }

        Publish(change);
    }

    /// <inheritdoc />
    public void RecordFailure(Exception exception)
    {
        if (!_options.ShouldCount(exception))
            return;

        CircuitStateChange? change;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            change = AdvanceOpenCircuitLocked(now);
            if (change is null && _state == CircuitState.Closed)
            {
                _outcomes.Enqueue(new Outcome(now, Failed: true));
                TrimLocked(now);
                change = EvaluateClosedWindowLocked(now);
            }
            else if (change is null && _state == CircuitState.HalfOpen)
            {
                _openedAt = now;
                change = TransitionLocked(CircuitState.Open, "Half-open probe failed.", now);
            }
        }

        Publish(change);
    }

    /// <inheritdoc />
    public async Task<CircuitStateChange> WaitForStateChangeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<CircuitStateChange> signal;
            TimeSpan? openDelay = null;
            CircuitStateChange? immediateChange;

            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                immediateChange = AdvanceOpenCircuitLocked(now);
                signal = _stateChangeSignal.Task;
                if (immediateChange is null && _state == CircuitState.Open)
                {
                    var elapsed = now - _openedAt;
                    openDelay = elapsed >= _options.BreakDuration ? TimeSpan.Zero : _options.BreakDuration - elapsed;
                }
            }

            if (immediateChange is not null)
            {
                Publish(immediateChange);
                return immediateChange;
            }

            if (openDelay is null)
                return await signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(openDelay.Value, _timeProvider, delayCancellation.Token);
            var completed = await Task.WhenAny(signal, delay).ConfigureAwait(false);
            if (completed == signal)
            {
                await delayCancellation.CancelAsync().ConfigureAwait(false);
                return await signal.ConfigureAwait(false);
            }

            await delay.ConfigureAwait(false);

            // The delay elapsing and a concurrent reader (e.g. the metrics
            // gauge) advancing Open->HalfOpen are synchronized by construction
            // at openedAt + BreakDuration. If that racer won, the captured
            // signal already holds the transition — return it instead of
            // re-binding the fresh signal, which would drop the change and
            // strand the caller until the next transition (with no processor
            // running, none would ever come).
            if (signal.IsCompleted)
                return await signal.ConfigureAwait(false);
        }
    }

    private CircuitStateChange? EvaluateClosedWindowLocked(DateTimeOffset now)
    {
        if (_outcomes.Count < _options.MinimumThroughput)
            return null;

        var failures = _outcomes.Count(outcome => outcome.Failed);
        var failurePercentage = failures * 100d / _outcomes.Count;
        if (failurePercentage < _options.FailurePercentageThreshold)
            return null;

        _openedAt = now;
        return TransitionLocked(
            CircuitState.Open,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Failure rate {failurePercentage:F1}% ({failures}/{_outcomes.Count}) reached the configured threshold."),
            now);
    }

    private CircuitStateChange? AdvanceOpenCircuitLocked(DateTimeOffset now)
    {
        if (_state != CircuitState.Open || now - _openedAt < _options.BreakDuration)
            return null;

        _halfOpenSuccesses = 0;
        return TransitionLocked(CircuitState.HalfOpen, "Break duration elapsed; starting half-open probes.", now);
    }

    private CircuitStateChange TransitionLocked(CircuitState next, string reason, DateTimeOffset now)
    {
        var change = new CircuitStateChange(Endpoint, _state, next, reason, now);
        _state = next;
        // Enqueued under _gate so publication order matches transition order;
        // the drain outside the lock preserves it (see Publish).
        _pendingPublications.Enqueue(change);
        var previousSignal = _stateChangeSignal;
        _stateChangeSignal = CreateSignal();
        previousSignal.TrySetResult(change);
        return change;
    }

    private void TrimLocked(DateTimeOffset now)
    {
        var cutoff = now - _options.SamplingWindow;
        while (_outcomes.TryPeek(out var outcome) && outcome.Timestamp <= cutoff)
            _outcomes.Dequeue();
    }

    /// <remarks>
    /// Two threads can leave <c>_gate</c> in transition order yet reach the
    /// observers inverted (a Closed->Open published before the HalfOpen->Closed
    /// that preceded it would tell operators "recovered" during an active
    /// outage). Transitions are therefore enqueued under <c>_gate</c> and
    /// drained FIFO under a dedicated publish gate: whichever thread drains
    /// first emits every pending change in order, and the loser finds an empty
    /// queue.
    /// </remarks>
    private void Publish(CircuitStateChange? change)
    {
        if (change is null)
            return;

        lock (_publishGate)
        {
            while (_pendingPublications.TryDequeue(out var pending))
                Emit(pending);
        }
    }

    private void Emit(CircuitStateChange change)
    {
        var tags = new TagList
        {
            { MessagingAttributes.NimBusEndpoint, change.Endpoint },
            { "nimbus.circuit_breaker.from", change.From.ToString() },
            { "nimbus.circuit_breaker.to", change.To.ToString() },
        };
        NimBusMeters.CircuitBreakerTransitions.Add(1, tags);
        var handlers = StateChanged;
        if (handlers is null)
            return;

        foreach (Action<CircuitStateChange> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch
            {
                // State observers are diagnostic sidecars and must never alter message handling.
            }
        }
    }

    private static TaskCompletionSource<CircuitStateChange> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly record struct Outcome(DateTimeOffset Timestamp, bool Failed);
}
