using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Core.Events;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.SDK;
using NimBus.ServiceBus;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>A publisher client together with the sender it owns.</summary>
public sealed class SimulationPublisherLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private int _disposed;

    /// <summary>Creates the lease.</summary>
    public SimulationPublisherLease(IPublisherClient client, Func<ValueTask> dispose)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));
    }

    /// <summary>The publisher.</summary>
    public IPublisherClient Client { get; }

    /// <summary>Disposes the owned sender once, which aborts sends still in flight.</summary>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? _dispose() : ValueTask.CompletedTask;
}

/// <summary>Creates a publisher that owns its sender (plan Decision 10). A seam for tests.</summary>
public interface ISimulationPublisherFactory
{
    /// <summary>Creates a publisher for <paramref name="endpointId"/>'s topic.</summary>
    SimulationPublisherLease Create(string endpointId);
}

/// <summary>
/// Publisher over the WebApp's shared <see cref="ServiceBusClient"/> with a private
/// <see cref="ServiceBusSender"/>, so disposing it aborts in-flight sends without touching
/// the shared client.
/// </summary>
public sealed class ServiceBusSimulationPublisherFactory : ISimulationPublisherFactory
{
    private readonly ServiceBusClient _client;

    /// <summary>Creates the factory.</summary>
    public ServiceBusSimulationPublisherFactory(ServiceBusClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <inheritdoc />
    public SimulationPublisherLease Create(string endpointId)
    {
        var serviceBusSender = _client.CreateSender(endpointId);
        var sender = NimBusOpenTelemetryDecorators.InstrumentSender(new Sender(serviceBusSender), MessagingSystem.ServiceBus);
        return new SimulationPublisherLease(new PublisherClient(sender, endpointId), serviceBusSender.DisposeAsync);
    }
}

/// <summary>Builds a schema-valid event for an event type. A seam for tests.</summary>
public interface ISimulationEventFactory
{
    /// <summary>Creates a valid event, or throws when none can be generated.</summary>
    IEvent Create(IEventType eventType);
}

/// <summary>
/// Generates payloads with <see cref="FakeEventPayloadGenerator"/> and validates them exactly
/// like Compose does: deserialize to the CLR type, then <see cref="IEvent.TryValidate"/>.
/// </summary>
public sealed class FakePayloadSimulationEventFactory : ISimulationEventFactory
{
    private readonly FakeEventPayloadGenerator _generator;
    private readonly IPlatform? _platform;

    /// <summary>Creates the factory.</summary>
    public FakePayloadSimulationEventFactory(FakeEventPayloadGenerator generator)
        : this(generator, platform: null)
    {
    }

    /// <summary>
    /// Creates the factory. With a <paramref name="platform"/>, an event type routed by key
    /// (Spec 045) gets one of the keys its consumers claim, so simulated traffic reaches the
    /// instance endpoints rather than only the unmatched one.
    /// </summary>
    public FakePayloadSimulationEventFactory(FakeEventPayloadGenerator generator, IPlatform? platform)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _platform = platform;
    }

    /// <inheritdoc />
    public IEvent Create(IEventType eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        var json = _generator.Generate(eventType)
            ?? throw new InvalidOperationException($"No payload could be generated for event type '{eventType.Id}'.");
        var @event = JsonConvert.DeserializeObject(json, eventType.GetEventClassType()) as IEvent
            ?? throw new InvalidOperationException($"The generated payload for '{eventType.Id}' did not deserialize to an event.");
        ApplyClaimedRoutingKey(eventType, @event);
        var validation = @event.TryValidate();
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"The generated payload for '{eventType.Id}' failed validation: " +
                string.Join(", ", validation.ValidationResults.Select(r => r.ErrorMessage)));
        }

        return @event;
    }

    private void ApplyClaimedRoutingKey(IEventType eventType, IEvent @event)
    {
        if (_platform is null || string.IsNullOrEmpty(eventType.RoutingKeyProperty))
            return;

        var claimed = _platform.GetClaimedRoutingKeys(eventType).ToList();
        var property = @event.GetType().GetProperty(eventType.RoutingKeyProperty);
        if (claimed.Count == 0 || property is null || property.PropertyType != typeof(string) || !property.CanWrite)
            return;

#pragma warning disable CA5394 // Picking a simulated key needs no cryptographic randomness.
        property.SetValue(@event, claimed[Random.Shared.Next(claimed.Count)]);
#pragma warning restore CA5394
    }
}

/// <summary>Receives publish outcomes from simulated publishers.</summary>
public interface ISimulationPublishSink
{
    /// <summary>One event was published.</summary>
    void Published(string endpointId, string eventTypeId);

    /// <summary>Generating, validating or sending an event failed.</summary>
    void PublishFailed(string endpointId, string eventTypeId, Exception error);

    /// <summary>Loops were abandoned at a deadline with sends still in flight.</summary>
    void SendsAbandoned(string endpointId, int count);
}

/// <summary>
/// A simulated publisher for one endpoint: one loop per event type the endpoint produces, all
/// sharing the endpoint's owned sender, a rotating session pool and the global
/// <see cref="SimulationRateLimiter"/>. Each loop reads the live config every iteration, so
/// rate, speed and enabled changes apply on the next publish.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Lifetime ends at StopAsync, which cancels the token source. A stop that times out abandons loops that still observe the token, so it is deliberately never disposed.")]
public sealed partial class SimulatedPublisher
{
    private static readonly TimeSpan DisabledRecheck = TimeSpan.FromSeconds(1);

    private readonly string _endpointId;
    private readonly IReadOnlyList<IEventType> _eventTypes;
    private readonly SimulationPublisherLease _lease;
    private readonly SimulationRateLimiter _limiter;
    private readonly Func<SimulationConfig> _config;
    private readonly ISimulationEventFactory _eventFactory;
    private readonly ISimulationPublishSink _sink;
    private readonly string _sessionPrefix;
    private readonly int _sessionPoolSize;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Random _random;
    private readonly object _randomLock = new();
    private readonly CancellationTokenSource _cts = new();
    private List<Task> _loops = new();
    private int _sessionCursor = -1;
    private int _waiting;

    /// <summary>Creates the publisher. Call <see cref="Start"/> to begin.</summary>
    public SimulatedPublisher(
        string endpointId,
        IReadOnlyList<IEventType> eventTypes,
        SimulationPublisherLease lease,
        SimulationRateLimiter limiter,
        Func<SimulationConfig> config,
        ISimulationEventFactory eventFactory,
        ISimulationPublishSink sink,
        string sessionPrefix,
        int sessionPoolSize,
        TimeProvider timeProvider,
        ILogger logger,
        Random? random = null)
    {
        _endpointId = endpointId ?? throw new ArgumentNullException(nameof(endpointId));
        _eventTypes = eventTypes ?? throw new ArgumentNullException(nameof(eventTypes));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _sessionPrefix = sessionPrefix ?? throw new ArgumentNullException(nameof(sessionPrefix));
        _sessionPoolSize = sessionPoolSize < 1 ? throw new ArgumentOutOfRangeException(nameof(sessionPoolSize)) : sessionPoolSize;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _random = random ?? new Random();
    }

    /// <summary>The endpoint published from.</summary>
    public string EndpointId => _endpointId;

    /// <summary>Number of loops started.</summary>
    public int LoopCount => _loops.Count;

    /// <summary>Loops currently parked on a timer. Lets tests step a fake clock deterministically.</summary>
    internal int WaitingLoops => Volatile.Read(ref _waiting);

    /// <summary>Starts one loop per event type.</summary>
    public void Start()
    {
        if (_loops.Count > 0)
            throw new InvalidOperationException("A simulated publisher starts once; create a new one to start again.");
        _loops = _eventTypes.Select(eventType => Task.Run(() => LoopAsync(eventType, _cts.Token))).ToList();
    }

    /// <summary>
    /// Cancels every loop and waits up to <paramref name="deadline"/>. Loops still running then
    /// (typically in a send that ignores cancellation) are abandoned, counted as abandoned sends,
    /// and the owned sender is disposed so the stuck sends abort. Returns the abandoned count.
    /// </summary>
    public async Task<int> StopAsync(TimeSpan deadline, CancellationToken cancellationToken = default)
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        var abandoned = 0;
        try
        {
            await Task.WhenAll(_loops).WaitAsync(deadline, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            abandoned = _loops.Count(loop => !loop.IsCompleted);
        }
        catch (Exception ex)
        {
            LogSimulatedPublisherLoopForEnded(_logger, ex, _endpointId);
        }

        if (abandoned > 0)
        {
            LogAbandonedSimulatedPublishLoop(_logger, abandoned, _endpointId, deadline);
            _sink.SendsAbandoned(_endpointId, abandoned);
        }

        var dispose = DisposeLeaseAsync();
        if (abandoned == 0)
        {
            // Nothing is stuck, so the close is quick; after an abandonment it runs on its own
            // so the caller's deadline holds.
            await dispose.ConfigureAwait(false);
        }

        return abandoned;
    }

    private async Task DisposeLeaseAsync()
    {
        try
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDisposingTheSimulatedSenderForDid(_logger, ex, _endpointId);
        }
    }

    private async Task LoopAsync(IEventType eventType, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var (enabled, perMinute) = CurrentRate(eventType.Id);
                if (!enabled)
                {
                    await WaitAsync(DisabledRecheck, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await WaitAsync(Jittered(TimeSpan.FromMinutes(1) / perMinute), cancellationToken).ConfigureAwait(false);
                await WaitAsync(_limiter.Reserve(), cancellationToken).ConfigureAwait(false);
                await PublishOneAsync(eventType, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Pause or Stop.
        }
    }

    private async Task PublishOneAsync(IEventType eventType, CancellationToken cancellationToken)
    {
        IEvent @event;
        try
        {
            @event = _eventFactory.Create(eventType);
        }
        catch (Exception ex)
        {
            LogCouldNotGenerateASimulated(_logger, ex, eventType.Id, _endpointId);
            _sink.PublishFailed(_endpointId, eventType.Id, ex);
            return;
        }

        var sessionId = NextSessionId();
        var correlationId = _sessionPrefix + Guid.NewGuid().ToString("N");
        try
        {
            await _lease.Client.Publish(@event, sessionId, correlationId, Guid.NewGuid().ToString(), cancellationToken).ConfigureAwait(false);
            _sink.Published(_endpointId, eventType.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSimulatedPublishOfFromFailed(_logger, ex, eventType.Id, _endpointId);
            _sink.PublishFailed(_endpointId, eventType.Id, ex);
        }
    }

    private (bool Enabled, double PerMinute) CurrentRate(string eventTypeId)
    {
        var config = _config();
        var entry = config.Publishers
            .FirstOrDefault(p => string.Equals(p.EndpointId, _endpointId, StringComparison.Ordinal))?
            .EventTypes.FirstOrDefault(e => string.Equals(e.EventTypeId, eventTypeId, StringComparison.Ordinal));
        if (entry is null || !entry.Enabled || entry.RatePerMinute < 1 || config.Speed <= 0)
            return (false, 0);
        return (true, entry.RatePerMinute * config.Speed);
    }

    private string NextSessionId()
    {
        var slot = (int)((uint)Interlocked.Increment(ref _sessionCursor) % (uint)_sessionPoolSize);
        return string.Create(CultureInfo.InvariantCulture, $"{_sessionPrefix}{_endpointId}-{slot:D3}");
    }

    private TimeSpan Jittered(TimeSpan interval)
    {
        double factor;
        lock (_randomLock)
        {
            factor = 0.8 + (_random.NextDouble() * 0.4);
        }

        return interval * factor;
    }

    // A timer-backed wait that updates the waiting count from the timer callback itself, so a
    // fake clock's Advance leaves the count accurate the moment it returns.
    private async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay <= TimeSpan.Zero)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 0 = arming, 1 = counted as waiting, 2 = released. The loop counts as waiting only once
        // its timer is armed, so a stepped clock never advances past a timer not yet created.
        var state = 0;
        void Release()
        {
            if (Interlocked.Exchange(ref state, 2) == 1)
                Interlocked.Decrement(ref _waiting);
        }

        using var timer = _timeProvider.CreateTimer(_ => { Release(); completion.TrySetResult(); }, null, delay, Timeout.InfiniteTimeSpan);
        using var registration = cancellationToken.Register(() => { Release(); completion.TrySetCanceled(cancellationToken); });
        if (Interlocked.CompareExchange(ref state, 1, 0) == 0)
            Interlocked.Increment(ref _waiting);
        await completion.Task.ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulated publisher loop for {EndpointId} ended with an error.")]
    private static partial void LogSimulatedPublisherLoopForEnded(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abandoned {Count} simulated publish loop(s) for {EndpointId} after {Deadline}; disposing their sender.")]
    private static partial void LogAbandonedSimulatedPublishLoop(ILogger logger, int count, string endpointId, TimeSpan deadline);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disposing the simulated sender for {EndpointId} did not complete cleanly.")]
    private static partial void LogDisposingTheSimulatedSenderForDid(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not generate a simulated {EventTypeId} for {EndpointId}.")]
    private static partial void LogCouldNotGenerateASimulated(ILogger logger, Exception exception, string eventTypeId, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulated publish of {EventTypeId} from {EndpointId} failed.")]
    private static partial void LogSimulatedPublishOfFromFailed(ILogger logger, Exception exception, string eventTypeId, string endpointId);
}
