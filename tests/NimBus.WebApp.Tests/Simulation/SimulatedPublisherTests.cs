#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Events.Customers;
using NimBus.Events.Orders;
using NimBus.SDK;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>
/// Publisher loops on a fake clock (plan Task 5): per-loop rate, the global ceiling shared
/// fairly across loops, disabled types, the session pool, and bounded stops when sends stall.
/// </summary>
[TestClass]
public sealed class SimulatedPublisherTests
{
    private static readonly IEventType[] StorefrontTypes =
    {
        new EventType(typeof(OrderPlaced)),
        new EventType(typeof(OrderDeliveryDetailsCaptured)),
        new EventType(typeof(PlaceCustomerOnCreditHold)),
    };

    [TestMethod]
    public async Task One_loop_publishes_at_rate_times_speed()
    {
        var run = new Run(Config(speed: 2, (nameof(OrderPlaced), true, 30)), ceiling: 600, eventTypes: StorefrontTypes.Take(1));

        await run.AdvanceAsync(TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20));
        await run.StopAsync();

        // 30/min x speed 2 = 60/min, so about 300 in five minutes (jitter is +-20 % per interval;
        // a fine clock step keeps timer quantization from stretching the interval).
        Assert.IsTrue(run.Sink.PublishedCount is >= 270 and <= 330, $"published {run.Sink.PublishedCount}, failed {run.Sink.FailedCount}");
    }

    [TestMethod]
    public async Task Concurrent_loops_over_the_ceiling_share_it_without_starving()
    {
        var run = new Run(
            Config(speed: 1, (nameof(OrderPlaced), true, 600), (nameof(OrderDeliveryDetailsCaptured), true, 600), (nameof(PlaceCustomerOnCreditHold), true, 600)),
            ceiling: 600);

        await run.AdvanceAsync(TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(50));
        await run.StopAsync();

        // 1800/min requested against a 600/min ceiling: two minutes allow 1200 plus a one-second burst.
        Assert.IsTrue(run.Sink.PublishedCount <= 1200 + 10 + 3, $"total {run.Sink.PublishedCount} exceeds the ceiling");
        Assert.IsTrue(run.Sink.PublishedCount >= 1100, $"total {run.Sink.PublishedCount} is far under the ceiling");
        foreach (var eventType in StorefrontTypes)
        {
            var share = run.Sink.PublishedFor(eventType.Id);
            Assert.IsTrue(share >= 300, $"{eventType.Id} published only {share}; the ceiling must be shared fairly.");
        }
    }

    [TestMethod]
    public async Task Summed_rate_below_the_ceiling_is_not_throttled()
    {
        var run = new Run(
            Config(speed: 1, (nameof(OrderPlaced), true, 60), (nameof(OrderDeliveryDetailsCaptured), true, 60), (nameof(PlaceCustomerOnCreditHold), true, 60)),
            ceiling: 600);

        await run.AdvanceAsync(TimeSpan.FromMinutes(2), TimeSpan.FromMilliseconds(250));
        await run.StopAsync();

        foreach (var eventType in StorefrontTypes)
        {
            var count = run.Sink.PublishedFor(eventType.Id);
            Assert.IsTrue(count is >= 105 and <= 135, $"{eventType.Id} published {count}, expected about 120");
        }
    }

    [TestMethod]
    public async Task Disabled_types_do_not_publish()
    {
        var run = new Run(Config(speed: 1, (nameof(OrderPlaced), true, 60), (nameof(OrderDeliveryDetailsCaptured), false, 60)));

        await run.AdvanceAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(250));
        await run.StopAsync();

        Assert.IsTrue(run.Sink.PublishedFor(nameof(OrderPlaced)) > 0);
        Assert.AreEqual(0, run.Sink.PublishedFor(nameof(OrderDeliveryDetailsCaptured)));
        Assert.AreEqual(0, run.Sink.PublishedFor(nameof(PlaceCustomerOnCreditHold)), "A type missing from the config is disabled.");
    }

    [TestMethod]
    public async Task Sessions_stay_within_the_pool_and_carry_the_prefix()
    {
        var run = new Run(Config(speed: 1, (nameof(OrderPlaced), true, 600), (nameof(OrderDeliveryDetailsCaptured), true, 600)), poolSize: 5);

        await run.AdvanceAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        await run.StopAsync();

        var messages = run.Sender.Sent;
        Assert.IsTrue(messages.Count > 50);
        Assert.IsTrue(messages.All(m => m.SessionId.StartsWith("sim-", StringComparison.Ordinal)));
        Assert.IsTrue(messages.All(m => m.CorrelationId!.StartsWith("sim-", StringComparison.Ordinal)));
        Assert.AreEqual(5, messages.Select(m => m.SessionId).Distinct().Count(), "Both event types rotate through one pool of five.");
    }

    [TestMethod]
    public async Task Pause_returns_promptly_when_the_send_honours_cancellation()
    {
        var run = new Run(Config(speed: 1, (nameof(OrderPlaced), true, 600)), eventTypes: StorefrontTypes.Take(1), sender: new StallingSender(honourCancellation: true));
        await run.AdvanceUntilStuckAsync();

        var abandoned = await run.Publisher.StopAsync(TimeSpan.FromSeconds(10)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, abandoned);
        Assert.AreEqual(0, run.Sink.AbandonedCount);
        Assert.IsTrue(run.LeaseDisposed);
    }

    [TestMethod]
    public async Task Pause_abandons_a_send_that_ignores_cancellation_at_its_deadline()
    {
        var sender = new StallingSender(honourCancellation: false);
        var run = new Run(Config(speed: 1, (nameof(OrderPlaced), true, 600), (nameof(OrderDeliveryDetailsCaptured), true, 600)), eventTypes: StorefrontTypes.Take(2), sender: sender);
        await run.AdvanceUntilStuckAsync();

        var stop = run.Publisher.StopAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.IsFalse(stop.IsCompleted, "The stop must wait for its deadline before abandoning.");
        run.Clock.Advance(TimeSpan.FromSeconds(10));
        var abandoned = await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, abandoned);
        Assert.AreEqual(2, run.Sink.AbandonedCount);
        Assert.IsTrue(run.LeaseDisposed, "The owned sender is disposed so the stuck sends abort.");

        // A fresh publisher over a fresh sender works, which is what the next Start builds.
        var next = new Run(Config(speed: 1, (nameof(OrderPlaced), true, 600)), eventTypes: StorefrontTypes.Take(1));
        await next.AdvanceAsync(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
        await next.StopAsync();
        Assert.IsTrue(next.Sink.PublishedCount > 0);
        sender.Release();
    }

    [TestMethod]
    public void Limiter_allows_a_one_second_burst_then_spaces_reservations()
    {
        var clock = new FakeTimeProvider();
        var limiter = new SimulationRateLimiter(600, clock);

        var waits = Enumerable.Range(0, 12).Select(_ => limiter.Reserve()).ToList();

        Assert.AreEqual(10, waits.Count(w => w == TimeSpan.Zero), "600/min is 10/s: a one-second burst.");
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), waits[10]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), waits[11]);
    }

    [TestMethod]
    public void Limiter_rejects_a_non_positive_ceiling()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SimulationRateLimiter(0));
    }

    private static SimulationConfig Config(double speed, params (string EventTypeId, bool Enabled, int Rate)[] eventTypes) =>
        new(
            speed,
            new[]
            {
                new SimulationPublisherConfig(
                    SimulationTestPlatform.Storefront,
                    eventTypes.Select(e => new SimulationEventTypeConfig(e.EventTypeId, e.Enabled, e.Rate)).ToList()),
            },
            Array.Empty<SimulationSubscriberConfig>());

    private sealed class Run
    {
        private bool _leaseDisposed;

        public Run(
            SimulationConfig config,
            int ceiling = 6000,
            IEnumerable<IEventType>? eventTypes = null,
            int poolSize = 40,
            CapturingSender? sender = null)
        {
            Sender = sender ?? new CapturingSender();
            var lease = new SimulationPublisherLease(
                new PublisherClient(Sender, SimulationTestPlatform.Storefront),
                () =>
                {
                    _leaseDisposed = true;
                    return ValueTask.CompletedTask;
                });
            Publisher = new SimulatedPublisher(
                SimulationTestPlatform.Storefront,
                (eventTypes ?? StorefrontTypes).ToList(),
                lease,
                new SimulationRateLimiter(ceiling, Clock),
                () => config,
                new FakePayloadSimulationEventFactory(new FakeEventPayloadGenerator()),
                Sink,
                "sim-",
                poolSize,
                Clock,
                NullLogger.Instance,
                new Random(42));
            Publisher.Start();
        }

        public FakeTimeProvider Clock { get; } = new();

        public RecordingPublishSink Sink { get; } = new();

        public CapturingSender Sender { get; }

        public SimulatedPublisher Publisher { get; }

        public bool LeaseDisposed => _leaseDisposed;

        public async Task AdvanceAsync(TimeSpan total, TimeSpan step)
        {
            Settle(() => Publisher.WaitingLoops == Publisher.LoopCount);
            for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += step)
            {
                Clock.Advance(step);
                Settle(() => Publisher.WaitingLoops == Publisher.LoopCount);
            }

            await Task.CompletedTask;
        }

        public async Task AdvanceUntilStuckAsync()
        {
            var stalling = (StallingSender)Sender;
            Settle(() => Publisher.WaitingLoops + stalling.InFlight == Publisher.LoopCount);
            for (var i = 0; i < 1000 && stalling.InFlight < Publisher.LoopCount; i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(50));
                Settle(() => Publisher.WaitingLoops + stalling.InFlight == Publisher.LoopCount);
            }

            Assert.AreEqual(Publisher.LoopCount, stalling.InFlight, "Every loop should be stuck in a send.");
            await Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            var stop = Publisher.StopAsync(TimeSpan.FromSeconds(10));
            var completed = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(stop, completed, "A publisher whose loops are parked must stop without a clock advance.");
        }

        // Thread.Yield rather than SpinWait.SpinUntil: the latter falls back to Sleep(1), which
        // costs a timer tick (~15 ms on Windows) per clock step.
        private static void Settle(Func<bool> condition)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!condition())
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(5))
                    Assert.Fail("Publisher loops did not settle.");
                Thread.Yield();
            }
        }
    }

    internal class CapturingSender : ISender
    {
        private readonly ConcurrentQueue<IMessage> _sent = new();

        public IReadOnlyList<IMessage> Sent => _sent.ToArray();

        public virtual Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            _sent.Enqueue(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    internal sealed class StallingSender(bool honourCancellation) : CapturingSender
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        public int InFlight => Volatile.Read(ref _inFlight);

        public void Release() => _release.TrySetResult();

        public override async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (honourCancellation)
                    await _release.Task.WaitAsync(cancellationToken);
                else
                    await _release.Task;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
