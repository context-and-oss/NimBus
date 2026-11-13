#pragma warning disable CA1707, CA2007
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;

namespace NimBus.Core.Tests;

/// <summary>
/// Boundary, state-machine and concurrency cases of the endpoint circuit breaker that
/// <see cref="CircuitBreakerTests"/> does not reach.
/// </summary>
[TestClass]
public sealed class CircuitBreakerEdgeCaseTests
{
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Break = TimeSpan.FromMinutes(1);

    // ----- options and construction ------------------------------------------

    [TestMethod]
    [DataRow(nameof(CircuitBreakerOptions.MinimumThroughput))]
    [DataRow(nameof(CircuitBreakerOptions.FailurePercentageThreshold))]
    [DataRow(nameof(CircuitBreakerOptions.SamplingWindow))]
    [DataRow(nameof(CircuitBreakerOptions.BreakDuration))]
    [DataRow(nameof(CircuitBreakerOptions.HalfOpenProbeCount))]
    public void Zero_is_rejected_for_every_option(string option)
    {
        var options = CreateOptions();
        switch (option)
        {
            case nameof(CircuitBreakerOptions.MinimumThroughput): options.MinimumThroughput = 0; break;
            case nameof(CircuitBreakerOptions.FailurePercentageThreshold): options.FailurePercentageThreshold = 0; break;
            case nameof(CircuitBreakerOptions.SamplingWindow): options.SamplingWindow = TimeSpan.Zero; break;
            case nameof(CircuitBreakerOptions.BreakDuration): options.BreakDuration = TimeSpan.Zero; break;
            case nameof(CircuitBreakerOptions.HalfOpenProbeCount): options.HalfOpenProbeCount = 0; break;
            default: Assert.Fail($"Unknown option {option}."); break;
        }

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(options.Validate);

        Assert.AreEqual(option, exception.ParamName);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    public void Blank_endpoint_is_rejected(string endpoint)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new EndpointCircuitBreaker(endpoint, CreateOptions()));
    }

    // ----- closed-state window ------------------------------------------------

    [TestMethod]
    public void Failure_rate_below_the_threshold_stays_closed_at_minimum_throughput()
    {
        var breaker = CreateBreaker(new ManualClock(Start), minimumThroughput: 4, failurePercentage: 50);

        breaker.RecordFailure(new TransientException("down"));
        breaker.RecordSuccess();
        breaker.RecordSuccess();
        breaker.RecordSuccess();

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void Outcome_exactly_one_sampling_window_old_has_expired()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock, minimumThroughput: 2, samplingWindow: TimeSpan.FromMinutes(1));

        breaker.RecordFailure(new TransientException("old"));
        clock.Advance(TimeSpan.FromMinutes(1));
        breaker.RecordFailure(new TransientException("new"));

        Assert.AreEqual(CircuitState.Closed, breaker.State, "Only the new failure is inside the window.");
    }

    [TestMethod]
    public void Outcome_just_inside_the_sampling_window_still_counts()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock, minimumThroughput: 2, samplingWindow: TimeSpan.FromMinutes(1));

        breaker.RecordFailure(new TransientException("old"));
        clock.Advance(TimeSpan.FromSeconds(59));
        breaker.RecordFailure(new TransientException("new"));

        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    // ----- open and half-open -------------------------------------------------

    [TestMethod]
    public void Outcomes_recorded_while_open_change_nothing()
    {
        // Deliveries still in flight when the circuit opens finish afterwards. They must
        // neither shorten the break nor count as probes once it ends.
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock, halfOpenProbeCount: 2);
        var transitions = Observe(breaker);
        breaker.RecordFailure(new TransientException("down"));

        breaker.RecordSuccess();
        breaker.RecordSuccess();
        breaker.RecordFailure(new TransientException("still down"));
        clock.Advance(Break - TimeSpan.FromSeconds(1));

        Assert.AreEqual(CircuitState.Open, breaker.State);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State, "A failure recorded while open must not restart the break.");
        breaker.RecordSuccess();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State, "Successes recorded while open are not probes.");
        Assert.AreEqual(2, transitions.Count);
    }

    [TestMethod]
    public void Probe_successes_do_not_carry_over_into_the_next_half_open_period()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock, halfOpenProbeCount: 2);
        breaker.RecordFailure(new TransientException("down"));
        clock.Advance(Break);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        breaker.RecordSuccess();
        breaker.RecordFailure(new TransientException("still down"));
        Assert.AreEqual(CircuitState.Open, breaker.State);
        clock.Advance(Break);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        breaker.RecordSuccess();
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State, "The earlier probe success must not count again.");
        breaker.RecordSuccess();
        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public void Closing_starts_a_fresh_sampling_window()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock, minimumThroughput: 3);
        for (var i = 0; i < 3; i++)
            breaker.RecordFailure(new TransientException("down"));
        Assert.AreEqual(CircuitState.Open, breaker.State);
        clock.Advance(Break);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);
        breaker.RecordSuccess();
        Assert.AreEqual(CircuitState.Closed, breaker.State);

        // Still inside the sampling window of the three failures that opened the circuit.
        breaker.RecordFailure(new TransientException("blip"));
        breaker.RecordFailure(new TransientException("blip"));
        Assert.AreEqual(CircuitState.Closed, breaker.State, "Failures from before the break must not count again.");

        breaker.RecordFailure(new TransientException("down"));
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    // ----- what counts --------------------------------------------------------

    [TestMethod]
    public void Exclusion_predicate_inspects_the_inner_exception_chain()
    {
        var options = CreateOptions();
        options.Exclude(exception => exception is TimeoutException { Message: "throttled" });
        var breaker = new EndpointCircuitBreaker("billing", options, new ManualClock(Start));

        breaker.RecordFailure(new EventContextHandlerException(
            new InvalidOperationException("call failed", new TimeoutException("throttled"))));
        Assert.AreEqual(CircuitState.Closed, breaker.State);

        breaker.RecordFailure(new EventContextHandlerException(
            new InvalidOperationException("call failed", new TimeoutException("down"))));
        Assert.AreEqual(CircuitState.Open, breaker.State);
    }

    [TestMethod]
    public void Session_block_wrapped_in_a_counted_failure_is_not_counted()
    {
        var breaker = CreateBreaker(new ManualClock(Start));

        breaker.RecordFailure(new TransientException("wrapped", new SessionBlockedException()));

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    [TestMethod]
    public async Task Recorder_does_not_record_a_failing_heartbeat()
    {
        var breaker = CreateBreaker(new ManualClock(Start));
        var behavior = new CircuitBreakerRecorderBehavior(breaker);

        await Assert.ThrowsExactlyAsync<TransientException>(() =>
            behavior.Handle(CreateContext(Heartbeat.EventTypeId), (_, _) => throw new TransientException("down")));

        Assert.AreEqual(CircuitState.Closed, breaker.State);
    }

    // ----- observers ----------------------------------------------------------

    [TestMethod]
    public void Every_state_observer_runs_even_when_an_earlier_one_throws()
    {
        var breaker = CreateBreaker(new ManualClock(Start));
        breaker.StateChanged += _ => throw new InvalidOperationException("observer failed");
        var transitions = Observe(breaker);

        breaker.RecordFailure(new TransientException("down"));

        Assert.AreEqual(1, transitions.Count);
    }

    [TestMethod]
    public void Observers_see_every_transition_in_order_under_concurrent_recording()
    {
        // Transitions leave the state lock in order but are published outside it. Whatever the
        // interleaving, each observed transition must start where the previous one ended.
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock);
        var transitions = Observe(breaker);

        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 5_000; i++)
            {
                switch (random.Next(4))
                {
                    case 0: breaker.RecordFailure(new TransientException("down")); break;
                    case 1: breaker.RecordSuccess(); break;
                    case 2: clock.Advance(Break); break;
                    default: _ = breaker.State; break;
                }
            }
        });

        // Read the final state first: the read itself may perform a last Open->HalfOpen advance.
        var finalState = breaker.State;
        var observed = transitions.ToArray();
        Assert.IsTrue(observed.Length > 10, $"Expected many transitions, saw {observed.Length}.");
        var state = CircuitState.Closed;
        for (var i = 0; i < observed.Length; i++)
        {
            Assert.AreEqual(state, observed[i].From, $"Transition {i} ({observed[i].From}->{observed[i].To}) is out of order.");
            state = observed[i].To;
        }

        Assert.AreEqual(finalState, state);
    }

    // ----- waiting for a state change -----------------------------------------

    [TestMethod]
    public async Task Waiter_wakes_itself_when_the_break_elapses()
    {
        // Nothing else reads the breaker here: no metrics gauge, no message traffic. The
        // paused receiver depends on this wake-up alone to start probing.
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock);
        breaker.RecordFailure(new TransientException("down"));

        var wait = breaker.WaitForStateChangeAsync(CancellationToken.None);
        Assert.AreEqual(1, clock.ActiveTimers);
        clock.Advance(Break - TimeSpan.FromSeconds(1));
        Assert.IsFalse(wait.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        var change = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CircuitState.Open, change.From);
        Assert.AreEqual(CircuitState.HalfOpen, change.To);
        Assert.AreEqual(0, clock.ActiveTimers);
    }

    [TestMethod]
    public async Task Waiter_returns_the_transition_another_reader_performed_before_its_timer_fired()
    {
        // The metrics gauge reads State on its own schedule. When that read performs the
        // Open->HalfOpen advance first, the waiter must report it and release its timer.
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock);
        breaker.RecordFailure(new TransientException("down"));
        var wait = breaker.WaitForStateChangeAsync(CancellationToken.None);

        clock.AdvanceWithoutFiringTimers(Break);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        var change = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CircuitState.HalfOpen, change.To);
        Assert.AreEqual(0, clock.ActiveTimers);
    }

    [TestMethod]
    public async Task Waiter_in_a_half_open_circuit_reports_the_failed_probe_that_reopens_it()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock);
        breaker.RecordFailure(new TransientException("down"));
        clock.Advance(Break);
        Assert.AreEqual(CircuitState.HalfOpen, breaker.State);

        var wait = breaker.WaitForStateChangeAsync(CancellationToken.None);
        breaker.RecordFailure(new TransientException("still down"));

        var change = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(CircuitState.HalfOpen, change.From);
        Assert.AreEqual(CircuitState.Open, change.To);
    }

    [TestMethod]
    public void Wait_started_after_the_break_elapsed_completes_with_the_half_open_transition()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock);
        var transitions = Observe(breaker);
        breaker.RecordFailure(new TransientException("down"));
        clock.Advance(Break);

        var wait = breaker.WaitForStateChangeAsync(CancellationToken.None);

        Assert.IsTrue(wait.IsCompletedSuccessfully);
        Assert.AreEqual(CircuitState.HalfOpen, wait.Result.To);
        Assert.AreEqual(2, transitions.Count, "The advance performed by the wait is published like any other.");
    }

    [TestMethod]
    public async Task Wait_is_cancellable_in_every_state()
    {
        var clock = new ManualClock(Start);
        var breaker = CreateBreaker(clock);

        using (var closed = new CancellationTokenSource())
        {
            var wait = breaker.WaitForStateChangeAsync(closed.Token);
            await closed.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
        }

        breaker.RecordFailure(new TransientException("down"));
        using (var open = new CancellationTokenSource())
        {
            var wait = breaker.WaitForStateChangeAsync(open.Token);
            await open.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
            Assert.AreEqual(0, clock.ActiveTimers, "A cancelled wait must release its break timer.");
        }
    }

    // ----- helpers ------------------------------------------------------------

    private static ConcurrentQueue<CircuitStateChange> Observe(EndpointCircuitBreaker breaker)
    {
        var transitions = new ConcurrentQueue<CircuitStateChange>();
        breaker.StateChanged += transitions.Enqueue;
        return transitions;
    }

    private static EndpointCircuitBreaker CreateBreaker(
        TimeProvider clock,
        int minimumThroughput = 1,
        double failurePercentage = 100,
        TimeSpan? samplingWindow = null,
        int halfOpenProbeCount = 1)
    {
        var options = CreateOptions();
        options.MinimumThroughput = minimumThroughput;
        options.FailurePercentageThreshold = failurePercentage;
        options.SamplingWindow = samplingWindow ?? TimeSpan.FromMinutes(2);
        options.HalfOpenProbeCount = halfOpenProbeCount;
        return new EndpointCircuitBreaker("billing", options, clock);
    }

    private static CircuitBreakerOptions CreateOptions() => new()
    {
        MinimumThroughput = 1,
        FailurePercentageThreshold = 100,
        SamplingWindow = TimeSpan.FromMinutes(2),
        BreakDuration = Break,
        HalfOpenProbeCount = 1,
    };

    private static InMemoryMessageContext CreateContext(string eventTypeId) => new(
        new Message
        {
            EventId = "event-1",
            MessageId = "message-1",
            SessionId = "session-1",
            To = "billing",
            EventTypeId = eventTypeId,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = eventTypeId, EventJson = "{}" },
            },
        },
        new InMemorySessionState());

    /// <summary>
    /// Thread-safe clock whose timers fire only when the test advances it, so break expiry is
    /// deterministic. <see cref="AdvanceWithoutFiringTimers"/> moves time alone, which lets a
    /// test act in the gap between the break ending and the timer callback running.
    /// </summary>
    private sealed class ManualClock(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = utcNow;

        public int ActiveTimers
        {
            get
            {
                lock (_lock)
                {
                    return _timers.Count(timer => timer.IsActive);
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                var timer = new ManualTimer(callback, state, _utcNow + dueTime);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] due;
            lock (_lock)
            {
                _utcNow += delta;
                due = _timers.Where(timer => timer.IsActive && timer.Due <= _utcNow).ToArray();
            }

            foreach (var timer in due)
                timer.Fire();
        }

        public void AdvanceWithoutFiringTimers(TimeSpan delta)
        {
            lock (_lock)
            {
                _utcNow += delta;
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private int _done;

            public DateTimeOffset Due { get; } = due;

            public bool IsActive => Volatile.Read(ref _done) == 0;

            public void Fire()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose() => Interlocked.Exchange(ref _done, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
