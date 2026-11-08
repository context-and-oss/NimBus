#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.SDK;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Heartbeat;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>The simulation state machine (plan Task 6) over fake hosts and publishers.</summary>
[TestClass]
public sealed class SimulationServiceTests
{
    [TestMethod]
    public async Task Walks_stopped_running_pausing_paused_running_stopping_stopped()
    {
        var sut = new Sut();

        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
        Assert.AreEqual(SimulationCommandStatus.Ok, (await sut.Service.StartAsync()).Status);
        Assert.AreEqual(SimulationState.Running, sut.Service.State);
        Assert.AreEqual(1, sut.Hosts.Created.Count);

        sut.Hosts.BlockStops();
        var pause = sut.Service.PauseAsync();
        await WaitUntilAsync(() => sut.Service.State == SimulationState.Pausing);
        sut.Hosts.ReleaseStops();
        Assert.AreEqual(SimulationCommandStatus.Ok, (await pause).Status);
        Assert.AreEqual(SimulationState.Paused, sut.Service.State);

        Assert.AreEqual(SimulationCommandStatus.Ok, (await sut.Service.StartAsync()).Status);
        Assert.AreEqual(SimulationState.Running, sut.Service.State);
        Assert.AreEqual(2, sut.Hosts.Created.Count, "Resume builds a fresh host.");

        sut.Hosts.BlockStops();
        var stop = sut.Service.StopAsync();
        await WaitUntilAsync(() => sut.Service.State == SimulationState.Stopping);
        sut.Hosts.ReleaseStops();
        Assert.AreEqual(SimulationCommandStatus.Ok, (await stop).Status);
        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
    }

    [TestMethod]
    public async Task Start_is_refused_when_disabled()
    {
        var sut = new Sut(configure: o => o.EnabledByDefault = false);

        var result = await sut.Service.StartAsync();

        Assert.AreEqual(SimulationCommandStatus.Conflict, result.Status);
        Assert.AreEqual(0, sut.Hosts.Created.Count);
    }

    [TestMethod]
    [DataRow("prod")]
    [DataRow("")]
    [DataRow("test")]
    public async Task Start_is_refused_when_the_environment_is_not_allowed(string environment)
    {
        var sut = new Sut(environment: environment);

        Assert.AreEqual(SimulationCommandStatus.Conflict, (await sut.Service.StartAsync()).Status);
        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
    }

    [TestMethod]
    public async Task Start_during_a_stop_is_refused_immediately()
    {
        var sut = new Sut();
        await sut.Service.StartAsync();
        sut.Hosts.BlockStops();
        var stop = sut.Service.StopAsync();
        await WaitUntilAsync(() => sut.Service.State == SimulationState.Stopping);

        var start = sut.Service.StartAsync();
        var completed = await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.AreSame(start, completed, "Start must not queue behind a Stop.");
        Assert.AreEqual(SimulationCommandStatus.Conflict, (await start).Status);
        sut.Hosts.ReleaseStops();
        await stop;
    }

    [TestMethod]
    public async Task Concurrent_start_and_stop_calls_are_serialized()
    {
        var sut = new Sut();

        var calls = Enumerable.Range(0, 20)
            .Select(i => i % 2 == 0 ? sut.Service.StartAsync() : sut.Service.StopAsync())
            .ToList();
        await Task.WhenAll(calls);

        Assert.AreEqual(0, sut.Hosts.MaxConcurrentTransitions > 1 ? 1 : 0, "Host starts and stops never overlap.");
        Assert.IsTrue(sut.Service.State is SimulationState.Running or SimulationState.Stopped);
        await sut.Service.StopAsync();
        Assert.IsTrue(sut.Hosts.Created.All(h => h.StartCalls <= 1 && h.StopCalls <= 1), "A host is started and stopped at most once.");
    }

    [TestMethod]
    public async Task Disabling_while_running_stops_the_run()
    {
        var sut = new Sut();
        await sut.Service.StartAsync();

        var result = await sut.Service.UpdateSettingsAsync(sut.Service.Settings with { Enabled = false });

        Assert.AreEqual(SimulationCommandStatus.Ok, result.Status);
        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
        Assert.AreEqual(1, sut.Hosts.Created[0].StopCalls);
    }

    [TestMethod]
    public async Task No_endpoint_is_simulated_without_ownership_and_a_heartbeat_never_grants_it()
    {
        var sut = new Sut(owned: Array.Empty<string>(), heartbeatAnsweredBy: SimulationTestPlatform.Billing);

        await sut.Service.StartAsync();
        var status = await sut.Service.GetStatusAsync();

        Assert.AreEqual(0, sut.Hosts.Created.Count);
        Assert.IsTrue(status.Endpoints.Where(e => e.Consumes.Count > 0).All(e => !e.Owned));
        await sut.Service.StopAsync();
    }

    [TestMethod]
    public async Task A_recent_heartbeat_on_an_owned_endpoint_warns_but_does_not_change_ownership()
    {
        var sut = new Sut(heartbeatAnsweredBy: SimulationTestPlatform.Billing);

        var status = await sut.Service.GetStatusAsync();

        var billing = status.Endpoints.Single(e => e.EndpointId == SimulationTestPlatform.Billing);
        Assert.IsTrue(billing.Owned);
        Assert.IsTrue(billing.LiveInstanceWarning);
    }

    [TestMethod]
    public async Task Ownership_change_while_running_is_refused()
    {
        var sut = new Sut();
        await sut.Service.StartAsync();

        var result = await sut.Service.UpdateSettingsAsync(sut.Service.Settings with
        {
            OwnedEndpoints = new[] { SimulationTestPlatform.Billing, SimulationTestPlatform.Warehouse },
        });

        Assert.AreEqual(SimulationCommandStatus.Conflict, result.Status);
        CollectionAssert.AreEqual(new[] { SimulationTestPlatform.Billing }, sut.Service.Settings.OwnedEndpoints.ToArray());
        await sut.Service.StopAsync();
    }

    [TestMethod]
    public async Task Stop_drains_then_gives_up_on_a_stalled_host_at_its_deadline()
    {
        var sut = new Sut(configure: o =>
        {
            o.DrainSeconds = 1;
            o.StopDeadlineSeconds = 1;
        });
        await sut.Service.StartAsync();
        sut.Hosts.StallDrains();
        sut.Hosts.StallStopsIgnoringCancellation();

        var watch = Stopwatch.StartNew();
        var result = await sut.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(SimulationCommandStatus.Ok, result.Status);
        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromSeconds(1.8), $"Drain and stop deadlines should both elapse, took {watch.Elapsed}.");
        var host = sut.Hosts.Created.Single();
        Assert.AreEqual(1, host.DrainCalls);
        Assert.AreEqual(1, host.StopCalls);
        Assert.IsTrue(host.DrainStartedAt < host.StopStartedAt, "The drain runs before the stop.");
    }

    [TestMethod]
    public async Task Shutdown_with_a_stalled_host_completes_within_the_host_token()
    {
        var sut = new Sut();
        await sut.Service.StartAsync();
        sut.Hosts.StallDrains();
        sut.Hosts.StallStopsIgnoringCancellation();

        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();
        await sut.Service.ShutdownAsync(shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(3), $"Shutdown took {watch.Elapsed}.");
        Assert.AreEqual(0, sut.Hosts.Created.Single().DrainCalls, "Shutdown skips the drain.");
        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
    }

    [TestMethod]
    public async Task Start_after_a_pause_uses_fresh_publishers_and_senders()
    {
        var sut = new Sut();
        await sut.Service.StartAsync();
        var first = sut.Publishers.Created;
        await sut.Service.PauseAsync();
        Assert.IsTrue(sut.Publishers.Leases.All(l => l.Disposed), "Pause disposes every owned sender.");

        await sut.Service.StartAsync();

        Assert.AreEqual(first * 2, sut.Publishers.Created);
        await sut.Service.StopAsync();
    }

    [TestMethod]
    public async Task Auto_stop_stops_the_run_when_its_time_has_passed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new Sut(clock: clock, configure: o => o.AutoStopMinutes = 30);
        await sut.Service.StartAsync();

        clock.Advance(TimeSpan.FromMinutes(29));
        await sut.Service.CheckAutoStopAsync();
        Assert.AreEqual(SimulationState.Running, sut.Service.State);

        clock.Advance(TimeSpan.FromMinutes(1));
        await sut.Service.CheckAutoStopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(SimulationState.Stopped, sut.Service.State);
    }

    [TestMethod]
    public void Config_validation_lists_every_violation()
    {
        var sut = new Sut();
        var config = new SimulationConfig(
            3,
            new[]
            {
                new SimulationPublisherConfig(SimulationTestPlatform.Storefront, new[]
                {
                    new SimulationEventTypeConfig("OrderPlaced", true, 0),
                    new SimulationEventTypeConfig("NotProduced", true, 10),
                }),
            },
            new[]
            {
                new SimulationSubscriberConfig(SimulationTestPlatform.Warehouse, new SimulatedFailure()),
                new SimulationSubscriberConfig(SimulationTestPlatform.Billing, new SimulatedFailure
                {
                    Mode = SimulatedFailureMode.Transient,
                    FailAttempts = 5,
                    LatencyMinMs = 900,
                    LatencyMaxMs = 100,
                    SessionPattern = "sim-[0-9]+",
                }),
            });

        var result = sut.Service.UpdateConfig(config);

        Assert.AreEqual(SimulationCommandStatus.Invalid, result.Status);
        var errors = string.Join("\n", result.Errors);
        StringAssert.Contains(errors, "speed", StringComparison.Ordinal);
        StringAssert.Contains(errors, "ratePerMinute", StringComparison.Ordinal);
        StringAssert.Contains(errors, "NotProduced", StringComparison.Ordinal);
        StringAssert.Contains(errors, "does not own", StringComparison.Ordinal);
        StringAssert.Contains(errors, "failAttempts", StringComparison.Ordinal);
        StringAssert.Contains(errors, "latencyMinMs must not exceed", StringComparison.Ordinal);
        StringAssert.Contains(errors, "sessionPattern", StringComparison.Ordinal);
        Assert.AreEqual(7, result.Errors.Count, errors);
    }

    [TestMethod]
    public async Task A_config_change_reaches_the_running_handler()
    {
        var sut = new Sut();
        await sut.Service.StartAsync();

        var result = sut.Service.UpdateConfig(sut.Service.Config with
        {
            Subscribers = new[] { new SimulationSubscriberConfig(SimulationTestPlatform.Billing, new SimulatedFailure { Mode = SimulatedFailureMode.Poison }) },
        });

        Assert.AreEqual(SimulationCommandStatus.Ok, result.Status);
        Assert.AreEqual(SimulatedFailureMode.Poison, sut.Hosts.Created.Single().Behavior.EffectiveMode);
        var status = await sut.Service.GetStatusAsync();
        Assert.AreEqual(SimulatedFailureMode.Poison, status.Endpoints.Single(e => e.EndpointId == SimulationTestPlatform.Billing).EffectiveMode);
        await sut.Service.StopAsync();
    }

    [TestMethod]
    public async Task Capped_is_reported_when_the_summed_rate_exceeds_the_ceiling()
    {
        var sut = new Sut();
        Assert.IsFalse((await sut.Service.GetStatusAsync()).Capped);

        sut.Service.UpdateConfig(sut.Service.Config with { Speed = 20 });

        Assert.IsTrue((await sut.Service.GetStatusAsync()).Capped, "3 types x 10/min x speed 20 = 600/min against a ceiling of 100.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
                Assert.Fail("Timed out waiting for condition.");
            await Task.Delay(5);
        }
    }

    internal sealed class Sut
    {
        public Sut(
            string environment = "dev",
            string[]? owned = null,
            Action<SimulationOptions>? configure = null,
            string? heartbeatAnsweredBy = null,
            TimeProvider? clock = null)
        {
            var options = new SimulationOptions
            {
                EnabledByDefault = true,
                OwnedEndpoints = (owned ?? new[] { SimulationTestPlatform.Billing }).ToList(),
                RateCeilingPerMinute = 100,
                PauseDeadlineSeconds = 1,
                StopDeadlineSeconds = 2,
                DrainSeconds = 1,
            };
            configure?.Invoke(options);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Environment", environment) })
                .Build();

            var services = new ServiceCollection();
            services.AddScoped<IHeartbeatService>(_ => new FakeHeartbeatService(heartbeatAnsweredBy));
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            Service = new SimulationService(
                SimulationTestPlatform.Create(),
                Options.Create(options),
                configuration,
                Hosts,
                Publishers,
                new FakePayloadSimulationEventFactory(new FakeEventPayloadGenerator()),
                clock ?? TimeProvider.System,
                NullLoggerFactory.Instance,
                scopeFactory);
        }

        public FakeHostFactory Hosts { get; } = new();

        public FakePublisherFactory Publishers { get; } = new();

        public SimulationService Service { get; }
    }

    internal sealed class FakeHostFactory : ISimulatedEndpointHostFactory
    {
        private int _inTransition;
        private int _maxConcurrent;
        private TaskCompletionSource? _stopGate;
        private bool _stallDrains;
        private bool _stallStopsHard;

        public List<FakeHost> Created { get; } = new();

        public int MaxConcurrentTransitions => Volatile.Read(ref _maxConcurrent);

        public void BlockStops() => _stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStops() => _stopGate?.TrySetResult();

        public void StallDrains() => _stallDrains = true;

        public void StallStopsIgnoringCancellation() => _stallStopsHard = true;

        public ISimulatedEndpointHost Create(string endpointId, IReadOnlyCollection<string> eventTypeIds, SimulatedHandlerBehavior behavior)
        {
            var host = new FakeHost(this, endpointId, behavior);
            lock (Created) { Created.Add(host); }
            return host;
        }

        public sealed class FakeHost(FakeHostFactory factory, string endpointId, SimulatedHandlerBehavior behavior) : ISimulatedEndpointHost
        {
            public string EndpointId { get; } = endpointId;
            public SimulatedHandlerBehavior Behavior { get; } = behavior;
            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }
            public int DrainCalls { get; private set; }
            public DateTimeOffset DrainStartedAt { get; private set; }
            public DateTimeOffset StopStartedAt { get; private set; }

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                StartCalls++;
                Enter();
                await Task.Yield();
                Exit();
            }

            public async Task DrainAsync(TimeSpan maxDuration, CancellationToken cancellationToken)
            {
                DrainCalls++;
                DrainStartedAt = DateTimeOffset.UtcNow;
                if (factory._stallDrains)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            public async Task StopAsync(CancellationToken cancellationToken)
            {
                StopCalls++;
                StopStartedAt = DateTimeOffset.UtcNow;
                Enter();
                try
                {
                    if (factory._stallStopsHard)
                        await new TaskCompletionSource().Task;
                    if (factory._stopGate is { } gate)
                        await gate.Task;
                }
                finally
                {
                    Exit();
                }
            }

            private void Enter()
            {
                var now = Interlocked.Increment(ref factory._inTransition);
                int max;
                while (now > (max = Volatile.Read(ref factory._maxConcurrent)) && Interlocked.CompareExchange(ref factory._maxConcurrent, now, max) != max)
                {
                }
            }

            private void Exit() => Interlocked.Decrement(ref factory._inTransition);
        }
    }

    internal sealed class FakePublisherFactory : ISimulationPublisherFactory
    {
        private int _created;

        public int Created => Volatile.Read(ref _created);

        public ConcurrentBag<FakeLease> Leases { get; } = new();

        public SimulationPublisherLease Create(string endpointId)
        {
            Interlocked.Increment(ref _created);
            var lease = new FakeLease();
            Leases.Add(lease);
            return new SimulationPublisherLease(
                new PublisherClient(new SimulatedPublisherTests.CapturingSender(), endpointId),
                () =>
                {
                    lease.Disposed = true;
                    return ValueTask.CompletedTask;
                });
        }

        public sealed class FakeLease
        {
            public bool Disposed { get; set; }
        }
    }

    internal sealed class FakeHeartbeatService(string? answeredBy) : IHeartbeatService
    {
        public Task<IReadOnlyList<HeartbeatOverviewItem>> GetOverviewAsync() =>
            Task.FromResult<IReadOnlyList<HeartbeatOverviewItem>>(answeredBy is null
                ? Array.Empty<HeartbeatOverviewItem>()
                : new[] { new HeartbeatOverviewItem { EndpointId = answeredBy, LastReceivedTime = DateTime.UtcNow.AddMinutes(-1) } });

        public Task<HeartbeatSettings> GetSettingsAsync() => throw new NotSupportedException();
        public Task<HeartbeatSettings> SetSettingsAsync(HeartbeatSettings settings) => throw new NotSupportedException();
        public Task<int> SweepTimeoutsAsync() => throw new NotSupportedException();
        public Task<int> SendHeartbeatsAsync(bool force = false) => throw new NotSupportedException();
        public Task SetEndpointEnabledAsync(string endpointId, bool enabled) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceHealth>> GetServiceHealthAsync() => throw new NotSupportedException();
        public Task<bool> ProbeResolverAsync() => throw new NotSupportedException();
        public Task<bool> RunScheduledTickAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
