#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

// The receiver loop driven by the real EndpointCircuitBreaker. The tests in the main file use
// a hand-driven breaker, which cannot show that a paused receiver resumes by itself.
public partial class NimBusReceiverHostedServiceTests
{
    private static readonly DateTimeOffset CircuitStart = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan CircuitBreak = TimeSpan.FromMinutes(1);

    [TestMethod]
    public async Task Paused_receiver_resumes_by_itself_when_the_break_ends_and_restores_concurrency_after_a_probe()
    {
        var clock = new ManualClock(CircuitStart);
        var breaker = CreateRealBreaker(clock);
        var client = new RecordingServiceBusClient();
        var service = CreateCircuitReceiver(client, breaker, maxConcurrentSessions: 8);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");

            breaker.RecordFailure(new TransientException("down"));
            await WaitUntilAsync(() => client.Processors[0].StopCalls == 1, () => $"StopCalls={client.Processors[0].StopCalls}");
            // The paused loop waits on the breaker; that wait owns the only break timer.
            await WaitUntilAsync(() => clock.ActiveTimers == 1, () => $"ActiveTimers={clock.ActiveTimers}");
            Assert.AreEqual(1, client.Processors.Count, "No processor may run while the circuit is open.");

            // Nothing reads the breaker here (no metrics gauge, no traffic): the break timer alone wakes the loop.
            clock.Advance(CircuitBreak);
            await WaitUntilAsync(() => client.Processors.Count == 2, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(1, client.ProcessorOptions[1].MaxConcurrentSessions);

            breaker.RecordSuccess();
            await WaitUntilAsync(() => client.Processors.Count == 3, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(1, client.Processors[1].StopCalls);
            Assert.AreEqual(8, client.ProcessorOptions[2].MaxConcurrentSessions);
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    [TestMethod]
    public async Task Break_that_ends_while_the_processor_is_still_stopping_starts_a_single_probe_processor()
    {
        // Stopping waits for in-flight handlers, so it can outlast a short break. The loop then
        // comes back to a breaker whose wait performs the Open->HalfOpen advance itself and
        // completes at once. Acting on that transition after starting the half-open processor
        // stopped it again straight away, cancelling whatever it had just received.
        var clock = new ManualClock(CircuitStart);
        var breaker = CreateRealBreaker(clock);
        var client = new RecordingServiceBusClient();
        var service = CreateCircuitReceiver(client, breaker, maxConcurrentSessions: 8);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            client.Processors[0].BlockStop();

            breaker.RecordFailure(new TransientException("down"));
            await WaitUntilAsync(() => client.Processors[0].StopCalls == 1, () => $"StopCalls={client.Processors[0].StopCalls}");
            clock.Advance(CircuitBreak);
            client.Processors[0].ReleaseStop();

            await WaitUntilAsync(() => client.Processors.Count >= 2, () => $"ProcessorCount={client.Processors.Count}");
            await Task.Delay(200);
            Assert.AreEqual(2, client.Processors.Count, "The half-open processor must not be recreated.");
            Assert.AreEqual(1, client.ProcessorOptions[1].MaxConcurrentSessions);
            Assert.AreEqual(0, client.Processors[1].StopCalls);

            breaker.RecordSuccess();
            await WaitUntilAsync(() => client.Processors.Count == 3, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(8, client.ProcessorOptions[2].MaxConcurrentSessions);
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    [TestMethod]
    public async Task Failed_probe_pauses_the_receiver_again_for_a_fresh_break()
    {
        var clock = new ManualClock(CircuitStart);
        var breaker = CreateRealBreaker(clock);
        var client = new RecordingServiceBusClient();
        var service = CreateCircuitReceiver(client, breaker, maxConcurrentSessions: 8);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            breaker.RecordFailure(new TransientException("down"));
            await WaitUntilAsync(() => clock.ActiveTimers == 1, () => $"ActiveTimers={clock.ActiveTimers}");
            clock.Advance(CircuitBreak);
            await WaitUntilAsync(() => client.Processors.Count == 2, () => $"ProcessorCount={client.Processors.Count}");

            breaker.RecordFailure(new TransientException("still down"));
            await WaitUntilAsync(() => client.Processors[1].StopCalls == 1, () => $"StopCalls={client.Processors[1].StopCalls}");
            await WaitUntilAsync(() => clock.ActiveTimers == 1, () => $"ActiveTimers={clock.ActiveTimers}");

            clock.Advance(CircuitBreak - TimeSpan.FromSeconds(1));
            await Task.Delay(50);
            Assert.AreEqual(2, client.Processors.Count, "The failed probe must start a full break.");

            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => client.Processors.Count == 3, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(1, client.ProcessorOptions[2].MaxConcurrentSessions);
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    [TestMethod]
    public async Task Receivers_sharing_one_breaker_pause_probe_and_resume_together()
    {
        var clock = new ManualClock(CircuitStart);
        var breaker = CreateRealBreaker(clock);
        var ordersClient = new RecordingServiceBusClient();
        var ingressClient = new RecordingServiceBusClient();
        var orders = CreateCircuitReceiver(ordersClient, breaker, maxConcurrentSessions: 8);
        var ingress = CreateCircuitReceiver(ingressClient, breaker, maxConcurrentSessions: 4);
        RecordingServiceBusClient[] clients = [ordersClient, ingressClient];
        string Describe() => string.Join(", ", clients.Select(client => $"Processors={client.Processors.Count}"));

        using var cts = new CancellationTokenSource();
        var ordersTask = orders.RunProcessorLoopAsync(cts.Token);
        var ingressTask = ingress.RunProcessorLoopAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => clients.All(client => client.Processors.Count == 1), Describe);

            breaker.RecordFailure(new TransientException("down"));
            await WaitUntilAsync(() => clients.All(client => client.Processors[0].StopCalls == 1), Describe);
            await WaitUntilAsync(() => clock.ActiveTimers == 2, () => $"ActiveTimers={clock.ActiveTimers}");

            clock.Advance(CircuitBreak);
            await WaitUntilAsync(() => clients.All(client => client.Processors.Count == 2), Describe);
            Assert.AreEqual(1, ordersClient.ProcessorOptions[1].MaxConcurrentSessions);
            Assert.AreEqual(1, ingressClient.ProcessorOptions[1].MaxConcurrentSessions);

            breaker.RecordSuccess();
            await WaitUntilAsync(() => clients.All(client => client.Processors.Count == 3), Describe);
            Assert.AreEqual(8, ordersClient.ProcessorOptions[2].MaxConcurrentSessions);
            Assert.AreEqual(4, ingressClient.ProcessorOptions[2].MaxConcurrentSessions);
        }
        finally
        {
            cts.Cancel();
            await Task.WhenAll(ordersTask, ingressTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task Starting_while_the_circuit_is_open_completes_startup_and_waits_out_the_break()
    {
        var clock = new ManualClock(CircuitStart);
        var breaker = CreateRealBreaker(clock);
        breaker.RecordFailure(new TransientException("down"));
        var client = new RecordingServiceBusClient();
        var service = CreateCircuitReceiver(client, breaker, maxConcurrentSessions: 8);

        await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.AreEqual(0, client.Processors.Count);

            await WaitUntilAsync(() => clock.ActiveTimers == 1, () => $"ActiveTimers={clock.ActiveTimers}");
            clock.Advance(CircuitBreak);
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(1, client.ProcessorOptions[0].MaxConcurrentSessions);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task Infrastructure_restart_while_half_open_keeps_probe_concurrency()
    {
        var breaker = new ManualCircuitBreaker("orders");
        breaker.TransitionTo(CircuitState.HalfOpen);
        var client = new RecordingServiceBusClient();
        var service = new TestableNimBusReceiverHostedService(
            client,
            new NoopServiceBusAdapter(),
            new NimBusReceiverOptions
            {
                TopicName = "orders",
                SubscriptionName = "orders",
                MaxConcurrentSessions = 8,
                RecoverableErrorRestartThreshold = 2,
                RecoverableErrorDelay = TimeSpan.Zero,
                ProcessorRestartDelay = TimeSpan.Zero,
            },
            NullLogger<NimBusReceiverHostedService>.Instance,
            breaker);

        using var cts = new CancellationTokenSource();
        var runTask = service.RunProcessorLoopAsync(cts.Token);
        try
        {
            await WaitUntilAsync(() => client.Processors.Count == 1, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(1, client.ProcessorOptions[0].MaxConcurrentSessions);

            for (var i = 0; i < 2; i++)
            {
                await service.HandleProcessorErrorAsync(CreateErrorArgs(
                    new ObjectDisposedException("connection"),
                    ServiceBusErrorSource.AcceptSession));
            }

            await WaitUntilAsync(() => client.Processors.Count == 2, () => $"ProcessorCount={client.Processors.Count}");
            Assert.AreEqual(1, client.ProcessorOptions[1].MaxConcurrentSessions, "A connection restart must not lift the probe limit.");
        }
        finally
        {
            await StopServiceAsync(cts, runTask);
        }
    }

    private static EndpointCircuitBreaker CreateRealBreaker(TimeProvider clock) => new(
        "orders",
        new CircuitBreakerOptions
        {
            MinimumThroughput = 1,
            FailurePercentageThreshold = 100,
            BreakDuration = CircuitBreak,
            HalfOpenProbeCount = 1,
        },
        clock);

    private static TestableNimBusReceiverHostedService CreateCircuitReceiver(
        ServiceBusClient client,
        IEndpointCircuitBreaker breaker,
        int maxConcurrentSessions) => new(
        client,
        new NoopServiceBusAdapter(),
        new NimBusReceiverOptions
        {
            TopicName = "orders",
            SubscriptionName = "orders",
            MaxConcurrentSessions = maxConcurrentSessions,
            ProcessorRestartDelay = TimeSpan.Zero,
        },
        NullLogger<NimBusReceiverHostedService>.Instance,
        breaker);

    /// <summary>Clock whose timers fire only when the test advances it, so break expiry is deterministic.</summary>
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
