#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Resolver;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;
using NimBus.ServiceBus.Provisioning;
using NimBus.ServiceBusEmulator.Tests;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>
/// The simulator end to end on the local Service Bus emulator (plan Task 11): the real
/// <see cref="SimulationService"/> publishing from Storefront and hosting Billing and Warehouse,
/// with the Resolver hosted in-process over the in-memory message store. The emulator is a local
/// process and needs no secrets, so these run in the regular test pass.
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public sealed class SimulationEmulatorTests
{
    private const string WarehouseSession = "sim-StorefrontEndpoint-001";

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Happy_path_completes_simulated_sessions_on_both_owned_endpoints()
    {
        await using var fixture = await Fixture.StartAsync();

        Assert.AreEqual(SimulationCommandStatus.Ok, (await fixture.Service.StartAsync()).Status);

        await WaitUntilAsync(
            async () => await fixture.CompletedSimulatedCountAsync(SimulationTestPlatform.Billing) > 0
                && await fixture.CompletedSimulatedCountAsync(SimulationTestPlatform.Warehouse) > 0,
            TimeSpan.FromSeconds(60),
            fixture.Describe);

        var stop = Stopwatch.StartNew();
        Assert.AreEqual(SimulationCommandStatus.Ok, (await fixture.Service.StopAsync()).Status);
        Assert.IsTrue(stop.Elapsed < TimeSpan.FromSeconds(fixture.Options.DrainSeconds + fixture.Options.StopDeadlineSeconds + 10),
            $"Stop took {stop.Elapsed}.");
        Assert.AreEqual(SimulationState.Stopped, fixture.Service.State);
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Pause_freezes_publishing_and_leaves_the_backlog_until_resume()
    {
        await using var fixture = await Fixture.StartAsync();
        // A slow Billing handler behind a fast publisher guarantees a backlog at pause time.
        fixture.Configure(speed: 1, ratePerMinute: 200, billing: new SimulatedFailure { Mode = SimulatedFailureMode.Slow, LatencyMinMs = 1_500, LatencyMaxMs = 2_000 });
        await fixture.Service.StartAsync();
        await WaitUntilAsync(async () => (await fixture.Service.GetStatusAsync()).Counters.HandledOk >= 3, TimeSpan.FromSeconds(60), fixture.Describe);

        Assert.AreEqual(SimulationCommandStatus.Ok, (await fixture.Service.PauseAsync()).Status);
        Assert.AreEqual(SimulationState.Paused, fixture.Service.State);

        var published = (await fixture.Service.GetStatusAsync()).Counters.Published;
        var rows = await fixture.WaitForStableRowCountAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.AreEqual(published, (await fixture.Service.GetStatusAsync()).Counters.Published, "Publishing must stop while paused.");
        Assert.AreEqual(rows, await fixture.RowCountAsync(), "No new tracking rows may appear while paused.");
        var backlog = await fixture.ActiveMessagesAsync(SimulationTestPlatform.Billing);
        Assert.IsTrue(backlog > 0, "Messages published before the pause stay in Billing's subscription.");

        var handledBefore = (await fixture.Service.GetStatusAsync()).Counters.HandledOk;
        Assert.AreEqual(SimulationCommandStatus.Ok, (await fixture.Service.StartAsync()).Status);
        await WaitUntilAsync(
            async () =>
            {
                var counters = (await fixture.Service.GetStatusAsync()).Counters;
                return counters.Published > published && counters.HandledOk > handledBefore;
            },
            TimeSpan.FromSeconds(60),
            fixture.Describe);

        await fixture.Service.StopAsync();
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Resubmit_unblocks_a_failed_session_and_replays_its_deferred_messages()
    {
        await using var fixture = await Fixture.StartAsync();
        // Every Warehouse delivery on one session fails, exhausts the simulator's three
        // retries and blocks the session; later messages on it are deferred.
        fixture.Configure(speed: 1, ratePerMinute: 120, warehouse: new SimulatedFailure
        {
            Mode = SimulatedFailureMode.Random,
            Rate = 100,
            SessionPattern = WarehouseSession,
            ExceptionMessage = "simulated warehouse outage",
        });
        await fixture.Service.StartAsync();

        await WaitUntilAsync(
            async () =>
            {
                var events = await fixture.SessionEventsAsync(SimulationTestPlatform.Warehouse, WarehouseSession);
                var exhausted = (await fixture.Service.GetStatusAsync()).Recent.Any(d =>
                    d.EndpointId == SimulationTestPlatform.Warehouse && d.SessionId == WarehouseSession
                    && d.Attempt == SimulationLimits.SimulatorMaxRetries + 1);
                return exhausted
                    && events.Any(e => e.ResolutionStatus == ResolutionStatus.Failed)
                    && events.Any(e => e.ResolutionStatus == ResolutionStatus.Deferred);
            },
            TimeSpan.FromSeconds(90),
            fixture.Describe);

        // Stop new traffic and heal Warehouse, then resubmit like the WebApp does.
        fixture.Configure(speed: 1, ratePerMinute: 120, publishersEnabled: false);
        var failed = (await fixture.SessionEventsAsync(SimulationTestPlatform.Warehouse, WarehouseSession))
            .Where(e => e.ResolutionStatus == ResolutionStatus.Failed)
            .ToList();
        var manager = new ManagerClient(fixture.Client);
        foreach (var failedEvent in failed)
        {
            var errorResponse = await fixture.Store.GetFailedMessage(failedEvent.EventId, SimulationTestPlatform.Warehouse);
            Assert.IsNotNull(errorResponse, $"No failed message stored for {failedEvent.EventId}.");
            await manager.Resubmit(errorResponse, SimulationTestPlatform.Warehouse, errorResponse.EventTypeId,
                errorResponse.MessageContent.EventContent!.EventJson!);
        }

        await WaitUntilAsync(
            async () =>
            {
                var events = await fixture.SessionEventsAsync(SimulationTestPlatform.Warehouse, WarehouseSession);
                return events.Count >= 2 && events.All(e => e.ResolutionStatus == ResolutionStatus.Completed);
            },
            TimeSpan.FromSeconds(60),
            fixture.Describe);

        await fixture.Service.StopAsync();
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task Stop_drains_in_flight_deliveries_and_leaves_nothing_locked()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Configure(speed: 1, ratePerMinute: 120, billing: new SimulatedFailure { Mode = SimulatedFailureMode.Slow, LatencyMinMs = 1_500, LatencyMaxMs = 2_500 });
        await fixture.Service.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(fixture.Service.InFlightDeliveries > 0), TimeSpan.FromSeconds(60), fixture.Describe);

        var stopRequestedAt = DateTimeOffset.UtcNow;
        Assert.AreEqual(SimulationCommandStatus.Ok, (await fixture.Service.StopAsync()).Status);

        var status = await fixture.Service.GetStatusAsync();
        Assert.IsTrue(
            status.Recent.Any(d => d.EndpointId == SimulationTestPlatform.Billing && d.Outcome == SimulatedDeliveryOutcome.Completed && d.At > stopRequestedAt),
            "A delivery in flight at Stop must complete during the drain.");
        Assert.AreEqual(0, fixture.Service.InFlightDeliveries, "Nothing may still be inside a handler after Stop.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, Func<Task<string>> describe)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > timeout)
                Assert.Fail($"Timed out after {timeout}. {await describe()}");
            await Task.Delay(250);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly EmulatorProcess _emulator;
        private readonly ServiceProvider _resolverServices;
        private readonly NimBusReceiverHostedService _resolverReceiver;
        private readonly ServiceBusAdministrationClient _admin;

        private Fixture(
            EmulatorProcess emulator,
            ServiceBusClient client,
            InMemoryMessageStore store,
            ServiceProvider resolverServices,
            NimBusReceiverHostedService resolverReceiver,
            SimulationOptions options,
            SimulationService service)
        {
            _emulator = emulator;
            Client = client;
            Store = store;
            _resolverServices = resolverServices;
            _resolverReceiver = resolverReceiver;
            Options = options;
            Service = service;
            _admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        }

        public ServiceBusClient Client { get; }

        public InMemoryMessageStore Store { get; }

        public SimulationOptions Options { get; }

        public SimulationService Service { get; }

        public static async Task<Fixture> StartAsync()
        {
            var emulator = await EmulatorProcess.StartAsync();
            try
            {
                using var provisioning = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await new ServiceBusTopologyProvisioner(emulator.ConnectionString, SimulationTestPlatform.Create, _ => { })
                    .ApplyAsync(provisioning.Token);

                var client = new ServiceBusClient(emulator.ConnectionString);
                var store = new InMemoryMessageStore();

                // The Resolver, hosted the way samples/AspirePubSub/AspirePubSub.ResolverWorker does.
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new[]
                    {
                        new KeyValuePair<string, string?>("ResolverId", "Resolver"),
                        new KeyValuePair<string, string?>("ConnectionStrings:servicebus", emulator.ConnectionString),
                    })
                    .Build();
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton<IConfiguration>(configuration);
                services.AddSingleton<IMessageTrackingStore>(store);
                services.AddResolver();
                var resolverServices = services.BuildServiceProvider();
                var resolverReceiver = new NimBusReceiverHostedService(
                    resolverServices.GetRequiredService<ServiceBusClient>(),
                    resolverServices.GetRequiredService<IServiceBusAdapter>(),
                    new NimBusReceiverOptions { TopicName = "Resolver", SubscriptionName = "Resolver", MaxConcurrentSessions = 8 },
                    NullLogger<NimBusReceiverHostedService>.Instance);
                await resolverReceiver.StartAsync(provisioning.Token);

                var options = new SimulationOptions
                {
                    EnabledByDefault = true,
                    OwnedEndpoints = new List<string> { SimulationTestPlatform.Billing, SimulationTestPlatform.Warehouse },
                    SessionPoolSize = 4,
                    DrainSeconds = 15,
                    PauseDeadlineSeconds = 10,
                    StopDeadlineSeconds = 10,
                };
                var simulationConfiguration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Environment", "dev") })
                    .Build();
                var service = new SimulationService(
                    SimulationTestPlatform.Create(),
                    Microsoft.Extensions.Options.Options.Create(options),
                    simulationConfiguration,
                    new SimulatedEndpointHostFactory(client, NullLoggerFactory.Instance, TimeProvider.System),
                    new ServiceBusSimulationPublisherFactory(client),
                    new FakePayloadSimulationEventFactory(new FakeEventPayloadGenerator()),
                    TimeProvider.System,
                    NullLoggerFactory.Instance);

                return new Fixture(emulator, client, store, resolverServices, resolverReceiver, options, service);
            }
            catch
            {
                await emulator.DisposeAsync();
                throw;
            }
        }

        /// <summary>Replaces the config: Storefront's types at one rate, and the two owned failure modes.</summary>
        public void Configure(double speed, int ratePerMinute, SimulatedFailure? billing = null, SimulatedFailure? warehouse = null, bool publishersEnabled = true)
        {
            var config = Service.Config with
            {
                Speed = speed,
                Publishers = Service.Config.Publishers
                    .Select(p => p with { EventTypes = p.EventTypes.Select(e => e with { Enabled = publishersEnabled, RatePerMinute = ratePerMinute }).ToList() })
                    .ToList(),
                Subscribers = new[]
                {
                    new SimulationSubscriberConfig(SimulationTestPlatform.Billing, billing ?? new SimulatedFailure()),
                    new SimulationSubscriberConfig(SimulationTestPlatform.Warehouse, warehouse ?? new SimulatedFailure()),
                },
            };
            var result = Service.UpdateConfig(config);
            Assert.AreEqual(SimulationCommandStatus.Ok, result.Status, string.Join("\n", result.Errors));
        }

        public async Task<List<UnresolvedEvent>> SessionEventsAsync(string endpointId, string sessionId) =>
            (await Store.GetEventsByFilter(new EventFilter { EndPointId = endpointId, SessionId = sessionId }, null!, 10_000))
                .Events.Where(e => e.SessionId == sessionId).ToList();

        public async Task<int> CompletedSimulatedCountAsync(string endpointId) =>
            (await Store.GetCompletedEventsOnEndpoint(endpointId)).Count(e => e.SessionId?.StartsWith("sim-", StringComparison.Ordinal) == true);

        public async Task<int> RowCountAsync() =>
            (await Store.GetEventsByFilter(new EventFilter(), null!, 100_000)).Events.Count();

        public async Task<int> WaitForStableRowCountAsync()
        {
            var previous = -1;
            for (var probe = 0; probe < 40; probe++)
            {
                var current = await RowCountAsync();
                if (current == previous)
                    return current;
                previous = current;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            Assert.Fail("Tracking rows kept changing after the pause.");
            return previous;
        }

        public async Task<long> ActiveMessagesAsync(string endpointId) =>
            (await _admin.GetSubscriptionRuntimePropertiesAsync(endpointId, endpointId)).Value.ActiveMessageCount;

        public async Task<string> Describe()
        {
            var status = await Service.GetStatusAsync();
            var rows = (await Store.GetEventsByFilter(new EventFilter(), null!, 100_000)).Events
                .GroupBy(e => $"{e.EndpointId}/{e.ResolutionStatus}")
                .Select(g => $"{g.Key}={g.Count()}");
            return $"State={status.State} Published={status.Counters.Published} HandledOk={status.Counters.HandledOk} " +
                $"HandlerErrors={status.Counters.HandlerErrors} PublishErrors={status.Counters.PublishErrors} " +
                $"Rows=[{string.Join(", ", rows)}]{Environment.NewLine}{_emulator.DumpOutput()[^Math.Min(4000, _emulator.DumpOutput().Length)..]}";
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await Service.ShutdownAsync(shutdown.Token);
                await _resolverReceiver.StopAsync(shutdown.Token);
            }
            finally
            {
                Service.Dispose();
                await Client.DisposeAsync();
                await _resolverServices.DisposeAsync();
                await _emulator.DisposeAsync();
            }
        }
    }
}
