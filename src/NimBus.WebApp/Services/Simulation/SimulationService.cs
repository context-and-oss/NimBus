using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Core;
using NimBus.Core.Events;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services.Heartbeat;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>Outcome class of a simulation command.</summary>
public enum SimulationCommandStatus
{
    /// <summary>Applied.</summary>
    Ok,

    /// <summary>Refused in the current state (HTTP 409).</summary>
    Conflict,

    /// <summary>The request is invalid (HTTP 400); <see cref="SimulationCommandResult.Errors"/> lists every violation.</summary>
    Invalid,

    /// <summary>The command ran and failed (HTTP 500).</summary>
    Failed,
}

/// <summary>Result of a simulation command.</summary>
public sealed record SimulationCommandResult(SimulationCommandStatus Status, IReadOnlyList<string> Errors)
{
    /// <summary>Success.</summary>
    public static SimulationCommandResult Ok { get; } = new(SimulationCommandStatus.Ok, Array.Empty<string>());

    /// <summary>A 409 with one reason.</summary>
    public static SimulationCommandResult Conflict(string reason) => new(SimulationCommandStatus.Conflict, new[] { reason });

    /// <summary>A 400 listing every violation.</summary>
    public static SimulationCommandResult Invalid(IReadOnlyList<string> errors) => new(SimulationCommandStatus.Invalid, errors);

    /// <summary>A 500 with one reason.</summary>
    public static SimulationCommandResult Failed(string reason) => new(SimulationCommandStatus.Failed, new[] { reason });
}

/// <summary>The traffic simulator: settings, ownership, config, run state, counters and the feed.</summary>
public interface ISimulationService
{
    /// <summary>Evaluates the environment gate now.</summary>
    (bool Allowed, SimulationBlockedReason? Reason) EvaluateEnvironment();

    /// <summary>Current status, including the advisory live-instance warnings.</summary>
    Task<SimulationStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the settings. Ownership changes while running are refused; disabling stops a run.</summary>
    Task<SimulationCommandResult> UpdateSettingsAsync(SimulationSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Replaces the whole config. A running simulation picks it up on its next publish or delivery.</summary>
    SimulationCommandResult UpdateConfig(SimulationConfig config);

    /// <summary>Starts from Stopped, or resumes from Paused.</summary>
    Task<SimulationCommandResult> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Pauses a running simulation within the pause deadline.</summary>
    Task<SimulationCommandResult> PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops: publishers first, then a bounded drain and a bounded receiver stop.</summary>
    Task<SimulationCommandResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the run when its auto-stop time has passed.</summary>
    Task CheckAutoStopAsync(CancellationToken cancellationToken = default);

    /// <summary>Host shutdown: one bounded stop without a drain, capped by <paramref name="cancellationToken"/>.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Singleton implementation of <see cref="ISimulationService"/>. State is in memory and per
/// WebApp instance (plan Decision 3); a restart starts from configuration and stops any run.
/// Every transition runs under one semaphore (plan Decision 10).
/// </summary>
public sealed partial class SimulationService : ISimulationService, ISimulationFeedSink, ISimulationPublishSink, IDisposable
{
    private const int DefaultRatePerMinute = 10;
    private static readonly TimeSpan LiveInstanceWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HeartbeatCacheTtl = TimeSpan.FromSeconds(30);

    private readonly IPlatform _platform;
    private readonly SimulationOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ISimulatedEndpointHostFactory _hostFactory;
    private readonly ISimulationPublisherFactory _publisherFactory;
    private readonly ISimulationEventFactory _eventFactory;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SimulationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, SimulatedHandlerBehavior> _behaviors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Since, DateTimeOffset? Until)> _hostedWindows = new(StringComparer.Ordinal);
    private readonly Queue<SimulatedDelivery> _feed = new();
    private readonly Queue<DateTimeOffset> _publishTimes = new();
    private readonly object _feedLock = new();

    private volatile SimulationSettings _settings;
    private volatile SimulationConfig _config;
    private int _state = (int)SimulationState.Stopped;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _autoStopAt;
    private List<ISimulatedEndpointHost> _hosts = new();
    private List<SimulatedPublisher> _publishers = new();
    private long _published;
    private long _handledOk;
    private long _handlerErrors;
    private long _poisoned;
    private long _publishErrors;
    private long _abandonedSends;
    private volatile HeartbeatSnapshot? _heartbeatCache;

    /// <summary>Creates the service.</summary>
    public SimulationService(
        IPlatform platform,
        IOptions<SimulationOptions> options,
        IConfiguration configuration,
        ISimulatedEndpointHostFactory hostFactory,
        ISimulationPublisherFactory publisherFactory,
        ISimulationEventFactory eventFactory,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory? scopeFactory = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _hostFactory = hostFactory ?? throw new ArgumentNullException(nameof(hostFactory));
        _publisherFactory = publisherFactory ?? throw new ArgumentNullException(nameof(publisherFactory));
        _eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<SimulationService>();
        _scopeFactory = scopeFactory;

        _settings = new SimulationSettings(
            _options.EnabledByDefault,
            _options.AutoStopMinutes,
            _options.RateCeilingPerMinute,
            (_options.OwnedEndpoints ?? new List<string>()).Distinct(StringComparer.Ordinal).ToList());
        _config = DefaultConfig();
    }

    /// <summary>The current run state.</summary>
    public SimulationState State => (SimulationState)Volatile.Read(ref _state);

    /// <summary>The current settings.</summary>
    public SimulationSettings Settings => _settings;

    /// <summary>The current config.</summary>
    public SimulationConfig Config => _config;

    /// <summary>Deliveries currently inside a simulated handler, across endpoints.</summary>
    internal int InFlightDeliveries => _behaviors.Values.Sum(b => b.InFlight);

    /// <inheritdoc />
    public (bool Allowed, SimulationBlockedReason? Reason) EvaluateEnvironment() =>
        SimulationEnvironmentPolicy.Evaluate(_configuration["Environment"], _options.AllowedEnvironments);

    /// <inheritdoc />
    public async Task<SimulationStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var (allowed, reason) = EvaluateEnvironment();
        var settings = _settings;
        var config = _config;
        var owned = settings.OwnedEndpoints.ToHashSet(StringComparer.Ordinal);
        var heartbeats = owned.Count > 0 ? await ReadHeartbeatsAsync(cancellationToken).ConfigureAwait(false) : Array.Empty<HeartbeatOverviewItem>();

        var endpoints = _platform.Endpoints
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e =>
            {
                var consumes = e.EventTypesConsumed.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList();
                var isOwned = owned.Contains(e.Id) && consumes.Count > 0;
                return new SimulationEndpointInfo(
                    e.Id,
                    e.EventTypesProduced.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList(),
                    consumes,
                    isOwned,
                    isOwned && LiveInstanceSuspected(e.Id, heartbeats),
                    isOwned ? BehaviorFor(e.Id).EffectiveMode : null);
            })
            .ToList();

        return new SimulationStatusSnapshot(
            allowed,
            reason,
            _configuration["Environment"],
            settings.Enabled,
            State,
            _startedAt,
            _autoStopAt,
            IsCapped(config, settings),
            _options.MaxRateCeilingPerMinute,
            _options.SessionPrefix,
            _options.AllowedEnvironments.ToList(),
            settings,
            config,
            SnapshotCounters(),
            SnapshotFeed(),
            endpoints);
    }

    /// <inheritdoc />
    public async Task<SimulationCommandResult> UpdateSettingsAsync(SimulationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = ValidateSettings(settings);
        if (errors.Count > 0)
            return SimulationCommandResult.Invalid(errors);

        var normalized = settings with { OwnedEndpoints = settings.OwnedEndpoints.Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList() };
        var ownershipChanged = !normalized.OwnedEndpoints.ToHashSet(StringComparer.Ordinal)
            .SetEquals(_settings.OwnedEndpoints);
        var stopRun = false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ownershipChanged && State is SimulationState.Running or SimulationState.Pausing or SimulationState.Stopping)
                return SimulationCommandResult.Conflict("Endpoint ownership cannot change while the simulation is running. Stop or pause it first.");

            _settings = normalized;
            if (normalized.Enabled && _autoStopAt is not null && _startedAt is DateTimeOffset startedAt)
                _autoStopAt = startedAt.AddMinutes(normalized.AutoStopMinutes);
            _currentLimiter?.UpdateCeiling(normalized.RateCeilingPerMinute);
            stopRun = !normalized.Enabled && State is SimulationState.Running or SimulationState.Paused;
        }
        finally
        {
            _gate.Release();
        }

        if (stopRun)
        {
            LogSimulationDisabledWhileRunning(_logger);
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }

        return SimulationCommandResult.Ok;
    }

    /// <inheritdoc />
    public SimulationCommandResult UpdateConfig(SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = ValidateConfig(config, _settings);
        if (errors.Count > 0)
            return SimulationCommandResult.Invalid(errors);

        _config = config;
        ApplyFailures(config);
        return SimulationCommandResult.Ok;
    }

    /// <inheritdoc />
    public async Task<SimulationCommandResult> StartAsync(CancellationToken cancellationToken = default)
    {
        var refusal = StartRefusal();
        if (refusal is not null)
            return SimulationCommandResult.Conflict(refusal);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            refusal = StartRefusal();
            if (refusal is not null)
                return SimulationCommandResult.Conflict(refusal);
            if (State is not (SimulationState.Stopped or SimulationState.Paused))
                return SimulationCommandResult.Conflict($"The simulation cannot start while {State}.");

            var resuming = State == SimulationState.Paused;
            var settings = _settings;
            var now = _timeProvider.GetUtcNow();
            if (!resuming)
            {
                ResetCounters();
                _startedAt = now;
                _autoStopAt = now.AddMinutes(settings.AutoStopMinutes);
            }

            // Always fresh hosts, publishers and senders: never reuse ones a deadline abandoned.
            var hosts = new List<ISimulatedEndpointHost>();
            try
            {
                foreach (var endpoint in OwnedConsumers(settings))
                {
                    var eventTypeIds = endpoint.EventTypesConsumed.Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList();
                    var host = _hostFactory.Create(endpoint.Id, eventTypeIds, BehaviorFor(endpoint.Id));
                    hosts.Add(host);
                    await host.StartAsync(cancellationToken).ConfigureAwait(false);
                    _hostedWindows[endpoint.Id] = (_timeProvider.GetUtcNow(), null);
                }
            }
            catch (Exception ex)
            {
                LogStartingASimulatedEndpointHostFailed(_logger, ex);
                await StopHostsAsync(hosts, TimeSpan.FromSeconds(_options.StopDeadlineSeconds), CancellationToken.None).ConfigureAwait(false);
                return SimulationCommandResult.Failed($"Starting a simulated subscriber failed: {ex.Message}");
            }

            var limiter = new SimulationRateLimiter(settings.RateCeilingPerMinute, _timeProvider);
            _currentLimiter = limiter;
            var publishers = new List<SimulatedPublisher>();
            foreach (var endpoint in _platform.Endpoints.Where(e => e.EventTypesProduced.Any()))
            {
                var eventTypes = PublishableTypes(endpoint.EventTypesProduced);
                if (eventTypes.Count == 0)
                    continue;

                var publisher = new SimulatedPublisher(
                    endpoint.Id,
                    eventTypes,
                    _publisherFactory.Create(endpoint.Id),
                    limiter,
                    () => _config,
                    _eventFactory,
                    this,
                    _options.SessionPrefix,
                    _options.SessionPoolSize,
                    _timeProvider,
                    _loggerFactory.CreateLogger<SimulatedPublisher>());
                publishers.Add(publisher);
                publisher.Start();
            }

            _hosts = hosts;
            _publishers = publishers;
            SetState(SimulationState.Running);
            LogSimulationPublisher(_logger, resuming ? "resumed" : "started", publishers.Count, hosts.Count);
            return SimulationCommandResult.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SimulationCommandResult> PauseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != SimulationState.Running)
                return SimulationCommandResult.Conflict($"Only a running simulation can pause (state: {State}).");

            SetState(SimulationState.Pausing);
            var deadline = TimeSpan.FromSeconds(_options.PauseDeadlineSeconds);
            await StopPublishersAsync(deadline, CancellationToken.None).ConfigureAwait(false);
            await StopHostsAsync(_hosts, deadline, CancellationToken.None).ConfigureAwait(false);
            SetState(SimulationState.Paused);
            return SimulationCommandResult.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<SimulationCommandResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SimulationState.Stopped)
                return SimulationCommandResult.Ok;
            if (State is not (SimulationState.Running or SimulationState.Paused))
                return SimulationCommandResult.Conflict($"The simulation cannot stop while {State}.");

            SetState(SimulationState.Stopping);
            await StopPublishersAsync(TimeSpan.FromSeconds(_options.PauseDeadlineSeconds), CancellationToken.None).ConfigureAwait(false);

            var hosts = _hosts;
            var drain = TimeSpan.FromSeconds(_options.DrainSeconds);
            if (hosts.Count > 0 && drain > TimeSpan.Zero)
            {
                using var drainCancellation = new CancellationTokenSource(drain, _timeProvider);
                try
                {
                    await Task.WhenAll(hosts.Select(h => DrainQuietlyAsync(h, drain, drainCancellation.Token)))
                        .WaitAsync(drain, _timeProvider, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    LogSimulationDrainDidNotFinishWithin(_logger, drain);
                }
            }

            await StopHostsAsync(hosts, TimeSpan.FromSeconds(_options.StopDeadlineSeconds), CancellationToken.None).ConfigureAwait(false);
            _autoStopAt = null;
            SetState(SimulationState.Stopped);
            return SimulationCommandResult.Ok;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task CheckAutoStopAsync(CancellationToken cancellationToken = default)
    {
        if (_autoStopAt is DateTimeOffset autoStopAt
            && State is SimulationState.Running or SimulationState.Paused
            && _timeProvider.GetUtcNow() >= autoStopAt)
        {
            LogSimulationAutoStopReached(_logger, autoStopAt);
            await StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogHostShutdownArrivedDuringASimulation(_logger);
            return;
        }

        try
        {
            if (State is SimulationState.Stopped)
                return;

            SetState(SimulationState.Stopping);
            await StopPublishersAsync(TimeSpan.FromSeconds(_options.PauseDeadlineSeconds), cancellationToken).ConfigureAwait(false);
            await StopHostsAsync(_hosts, TimeSpan.FromSeconds(_options.StopDeadlineSeconds), cancellationToken).ConfigureAwait(false);
            _autoStopAt = null;
            SetState(SimulationState.Stopped);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Record(SimulatedDelivery delivery)
    {
        switch (delivery.Outcome)
        {
            case SimulatedDeliveryOutcome.Completed:
                Interlocked.Increment(ref _handledOk);
                break;
            case SimulatedDeliveryOutcome.Poisoned:
                Interlocked.Increment(ref _poisoned);
                break;
            default:
                Interlocked.Increment(ref _handlerErrors);
                break;
        }

        lock (_feedLock)
        {
            _feed.Enqueue(delivery);
            while (_feed.Count > SimulationLimits.FeedSize)
                _feed.Dequeue();
        }
    }

    /// <inheritdoc />
    public void Published(string endpointId, string eventTypeId)
    {
        Interlocked.Increment(ref _published);
        var now = _timeProvider.GetUtcNow();
        lock (_feedLock)
        {
            _publishTimes.Enqueue(now);
            PrunePublishTimes(now);
        }
    }

    /// <inheritdoc />
    public void PublishFailed(string endpointId, string eventTypeId, Exception error) =>
        Interlocked.Increment(ref _publishErrors);

    /// <inheritdoc />
    public void SendsAbandoned(string endpointId, int count) =>
        Interlocked.Add(ref _abandonedSends, count);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Validates settings against Decision 11 and the platform; returns every violation.</summary>
    public IReadOnlyList<string> ValidateSettings(SimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();
        if (settings.AutoStopMinutes is < SimulationLimits.MinAutoStopMinutes or > SimulationLimits.MaxAutoStopMinutes)
            errors.Add($"autoStopMinutes must be between {SimulationLimits.MinAutoStopMinutes} and {SimulationLimits.MaxAutoStopMinutes}.");
        if (settings.RateCeilingPerMinute < 1 || settings.RateCeilingPerMinute > _options.MaxRateCeilingPerMinute)
            errors.Add($"rateCeilingPerMinute must be between 1 and {_options.MaxRateCeilingPerMinute}.");

        var consumers = SimulationTopology.ConsumingEndpointIds(_platform);
        foreach (var endpoint in settings.OwnedEndpoints ?? Array.Empty<string>())
        {
            if (!consumers.Contains(endpoint ?? string.Empty))
                errors.Add($"ownedEndpoints: '{endpoint}' is not an endpoint that consumes events.");
        }

        return errors;
    }

    /// <summary>Validates a config against Decision 11 and the platform; returns every violation.</summary>
    public IReadOnlyList<string> ValidateConfig(SimulationConfig config, SimulationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();

        if (!SimulationLimits.AllowedSpeeds.Contains(config.Speed))
            errors.Add($"speed must be one of {string.Join(", ", SimulationLimits.AllowedSpeeds)}.");

        var endpoints = _platform.Endpoints.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var seenPublishers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publisher in config.Publishers ?? Array.Empty<SimulationPublisherConfig>())
        {
            var prefix = $"publishers[{publisher.EndpointId}]";
            if (!seenPublishers.Add(publisher.EndpointId ?? string.Empty))
                errors.Add($"{prefix}: listed more than once.");
            if (publisher.EndpointId is null || !endpoints.TryGetValue(publisher.EndpointId, out var endpoint) || !endpoint.EventTypesProduced.Any())
            {
                errors.Add($"{prefix}: not an endpoint that produces events.");
                continue;
            }

            var produced = endpoint.EventTypesProduced.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var seenTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var eventType in publisher.EventTypes ?? Array.Empty<SimulationEventTypeConfig>())
            {
                if (!seenTypes.Add(eventType.EventTypeId ?? string.Empty))
                    errors.Add($"{prefix}.{eventType.EventTypeId}: listed more than once.");
                if (eventType.EventTypeId is null || !produced.Contains(eventType.EventTypeId))
                    errors.Add($"{prefix}.{eventType.EventTypeId}: not produced by {publisher.EndpointId}.");
                if (eventType.RatePerMinute < 1 || eventType.RatePerMinute > settings.RateCeilingPerMinute)
                    errors.Add($"{prefix}.{eventType.EventTypeId}: ratePerMinute must be between 1 and {settings.RateCeilingPerMinute}.");
            }
        }

        var owned = settings.OwnedEndpoints.ToHashSet(StringComparer.Ordinal);
        var seenSubscribers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subscriber in config.Subscribers ?? Array.Empty<SimulationSubscriberConfig>())
        {
            var prefix = $"subscribers[{subscriber.EndpointId}]";
            if (!seenSubscribers.Add(subscriber.EndpointId ?? string.Empty))
                errors.Add($"{prefix}: listed more than once.");
            if (subscriber.EndpointId is null || !endpoints.TryGetValue(subscriber.EndpointId, out var endpoint) || !endpoint.EventTypesConsumed.Any())
            {
                errors.Add($"{prefix}: not an endpoint that consumes events.");
                continue;
            }

            if (!owned.Contains(subscriber.EndpointId))
                errors.Add($"{prefix}: the simulator does not own this endpoint; take ownership in Settings → Simulation first.");

            var consumed = endpoint.EventTypesConsumed.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            errors.AddRange(ValidateFailure(prefix, subscriber.Failure, consumed));
        }

        return errors;
    }

    private static IEnumerable<string> ValidateFailure(string prefix, SimulatedFailure? failure, HashSet<string> consumed)
    {
        if (failure is null)
        {
            yield return $"{prefix}.failure: required.";
            yield break;
        }

        if (!Enum.IsDefined(failure.Mode))
            yield return $"{prefix}.failure.mode: unknown mode.";
        if (failure.Rate is < 1 or > 100)
            yield return $"{prefix}.failure.rate must be between 1 and 100.";
        if (failure.FailAttempts < 1 || failure.FailAttempts > SimulationLimits.SimulatorMaxRetries)
            yield return $"{prefix}.failure.failAttempts must be between 1 and {SimulationLimits.SimulatorMaxRetries}, so the last attempt can succeed.";
        if (failure.LatencyMinMs is < 0 or > SimulationLimits.MaxLatencyMs)
            yield return $"{prefix}.failure.latencyMinMs must be between 0 and {SimulationLimits.MaxLatencyMs}.";
        if (failure.LatencyMaxMs is < 0 or > SimulationLimits.MaxLatencyMs)
            yield return $"{prefix}.failure.latencyMaxMs must be between 0 and {SimulationLimits.MaxLatencyMs}.";
        if (failure.LatencyMinMs > failure.LatencyMaxMs)
            yield return $"{prefix}.failure.latencyMinMs must not exceed latencyMaxMs.";
        if (failure.ExceptionMessage is not null
            && (failure.ExceptionMessage.Trim().Length == 0 || failure.ExceptionMessage.Length > SimulationLimits.MaxExceptionMessageLength))
            yield return $"{prefix}.failure.exceptionMessage must be 1–{SimulationLimits.MaxExceptionMessageLength} characters.";
        if (failure.SessionPattern is not null && !IsValidSessionPattern(failure.SessionPattern))
            yield return $"{prefix}.failure.sessionPattern must be at most {SimulationLimits.MaxSessionPatternLength} characters of letters, digits, '-', '_', '.', ':' and the wildcards '*' and '?'.";
        foreach (var eventTypeId in failure.EventTypeIds ?? Array.Empty<string>())
        {
            if (!consumed.Contains(eventTypeId ?? string.Empty))
                yield return $"{prefix}.failure.eventTypeIds: '{eventTypeId}' is not consumed by this endpoint.";
        }

        if (failure.RevertAfterMinutes is int revert && (revert < 1 || revert > SimulationLimits.MaxRevertAfterMinutes))
            yield return $"{prefix}.failure.revertAfterMinutes must be between 1 and {SimulationLimits.MaxRevertAfterMinutes}, or null.";
    }

    private static bool IsValidSessionPattern(string pattern) =>
        pattern.Length is > 0 and <= SimulationLimits.MaxSessionPatternLength
        && pattern.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '*' or '?');

    private volatile SimulationRateLimiter? _currentLimiter;

    private string? StartRefusal()
    {
        var (allowed, reason) = EvaluateEnvironment();
        if (!allowed)
            return $"Simulation is not allowed in this environment ({reason}).";
        if (!_settings.Enabled)
            return "Simulate mode is disabled. Enable it in Settings → Simulation first.";
        if (State is SimulationState.Pausing or SimulationState.Stopping)
            return $"A {State} transition is in progress.";
        return null;
    }

    private void SetState(SimulationState state)
    {
        Volatile.Write(ref _state, (int)state);
        if (state is SimulationState.Paused or SimulationState.Stopped)
        {
            var now = _timeProvider.GetUtcNow();
            foreach (var host in _hosts)
            {
                if (_hostedWindows.TryGetValue(host.EndpointId, out var window) && window.Until is null)
                    _hostedWindows[host.EndpointId] = (window.Since, now);
            }
        }
    }

    private IEnumerable<NimBus.Core.Endpoints.IEndpoint> OwnedConsumers(SimulationSettings settings)
    {
        var owned = settings.OwnedEndpoints.ToHashSet(StringComparer.Ordinal);
        return _platform.Endpoints.Where(e => owned.Contains(e.Id) && e.EventTypesConsumed.Any());
    }

    private static List<IEventType> PublishableTypes(IEnumerable<IEventType> produced)
    {
        var result = new List<IEventType>();
        foreach (var eventType in produced.GroupBy(t => t.Id, StringComparer.Ordinal).Select(g => g.First()))
        {
            try
            {
                // Dynamically-typed events have no CLR class to generate a payload from.
                if (eventType.GetEventClassType() is not null)
                    result.Add(eventType);
            }
            catch (Exception)
            {
                // Same: not publishable by the simulator.
            }
        }

        return result;
    }

    private SimulationConfig DefaultConfig() =>
        new(
            1,
            _platform.Endpoints
                .Where(e => e.EventTypesProduced.Any())
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new SimulationPublisherConfig(
                    e.Id,
                    e.EventTypesProduced
                        .Select(t => t.Id)
                        .Distinct(StringComparer.Ordinal)
                        .Select(id => new SimulationEventTypeConfig(id, true, Math.Min(DefaultRatePerMinute, _options.RateCeilingPerMinute)))
                        .ToList()))
                .ToList(),
            Array.Empty<SimulationSubscriberConfig>());

    private SimulatedHandlerBehavior BehaviorFor(string endpointId) =>
        _behaviors.GetOrAdd(endpointId, id => new SimulatedHandlerBehavior(id, this, _timeProvider));

    private void ApplyFailures(SimulationConfig config)
    {
        var now = _timeProvider.GetUtcNow();
        var configured = (config.Subscribers ?? Array.Empty<SimulationSubscriberConfig>())
            .ToDictionary(s => s.EndpointId, s => s.Failure, StringComparer.Ordinal);

        foreach (var endpointId in SimulationTopology.ConsumingEndpointIds(_platform))
        {
            var failure = configured.TryGetValue(endpointId, out var f) ? f : new SimulatedFailure();
            var behavior = BehaviorFor(endpointId);
            if (!FailureEquals(behavior.Failure, failure))
                behavior.Update(failure, now);
        }
    }

    private static bool FailureEquals(SimulatedFailure a, SimulatedFailure b) =>
        a with { EventTypeIds = Array.Empty<string>() } == b with { EventTypeIds = Array.Empty<string>() }
        && a.EventTypeIds.SequenceEqual(b.EventTypeIds, StringComparer.Ordinal);

    private static bool IsCapped(SimulationConfig config, SimulationSettings settings)
    {
        var total = (config.Publishers ?? Array.Empty<SimulationPublisherConfig>())
            .SelectMany(p => p.EventTypes ?? Array.Empty<SimulationEventTypeConfig>())
            .Where(e => e.Enabled)
            .Sum(e => e.RatePerMinute * config.Speed);
        return total > settings.RateCeilingPerMinute;
    }

    private async Task StopPublishersAsync(TimeSpan deadline, CancellationToken cancellationToken)
    {
        var publishers = _publishers;
        _publishers = new List<SimulatedPublisher>();
        if (publishers.Count == 0)
            return;

        await Task.WhenAll(publishers.Select(p => p.StopAsync(deadline, cancellationToken))).ConfigureAwait(false);
    }

    private async Task StopHostsAsync(List<ISimulatedEndpointHost> hosts, TimeSpan deadline, CancellationToken cancellationToken)
    {
        if (hosts.Count == 0)
            return;

        using var deadlineCancellation = new CancellationTokenSource(deadline, _timeProvider);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(deadlineCancellation.Token, cancellationToken);
        var stops = hosts.Select(h => StopQuietlyAsync(h, bounded.Token)).ToList();
        try
        {
            await Task.WhenAll(stops).WaitAsync(deadline, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            LogSimulatedSubscriber(_logger, stops.Count(s => !s.IsCompleted), deadline);
        }
    }

    private async Task StopQuietlyAsync(ISimulatedEndpointHost host, CancellationToken cancellationToken)
    {
        try
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStoppingTheSimulatedSubscriberForDid(_logger, ex, host.EndpointId);
        }
    }

    private async Task DrainQuietlyAsync(ISimulatedEndpointHost host, TimeSpan drain, CancellationToken cancellationToken)
    {
        try
        {
            await host.DrainAsync(drain, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogDrainOfTheSimulatedSubscriber(_logger, ex, host.EndpointId);
        }
    }

    private void ResetCounters()
    {
        Interlocked.Exchange(ref _published, 0);
        Interlocked.Exchange(ref _handledOk, 0);
        Interlocked.Exchange(ref _handlerErrors, 0);
        Interlocked.Exchange(ref _poisoned, 0);
        Interlocked.Exchange(ref _publishErrors, 0);
        Interlocked.Exchange(ref _abandonedSends, 0);
        lock (_feedLock)
        {
            _feed.Clear();
            _publishTimes.Clear();
        }
    }

    private SimulationCounters SnapshotCounters()
    {
        int throughput;
        lock (_feedLock)
        {
            PrunePublishTimes(_timeProvider.GetUtcNow());
            throughput = _publishTimes.Count;
        }

        return new SimulationCounters(
            Interlocked.Read(ref _published),
            Interlocked.Read(ref _handledOk),
            Interlocked.Read(ref _handlerErrors),
            Interlocked.Read(ref _poisoned),
            Interlocked.Read(ref _publishErrors),
            Interlocked.Read(ref _abandonedSends),
            throughput);
    }

    private List<SimulatedDelivery> SnapshotFeed()
    {
        lock (_feedLock)
        {
            return _feed.Reverse().ToList();
        }
    }

    private void PrunePublishTimes(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-1);
        while (_publishTimes.Count > 0 && _publishTimes.Peek() < cutoff)
            _publishTimes.Dequeue();
    }

    private bool LiveInstanceSuspected(string endpointId, IReadOnlyList<HeartbeatOverviewItem> heartbeats)
    {
        var item = heartbeats.FirstOrDefault(h => string.Equals(h.EndpointId, endpointId, StringComparison.Ordinal));
        if (item?.LastReceivedTime is not DateTime received)
            return false;

        var receivedAt = new DateTimeOffset(DateTime.SpecifyKind(received, DateTimeKind.Utc));
        if (receivedAt < _timeProvider.GetUtcNow() - LiveInstanceWindow)
            return false;

        // Our own simulated subscriber answers heartbeats while it runs; only an answer
        // outside the window we hosted the endpoint suggests a competing process.
        if (_hostedWindows.TryGetValue(endpointId, out var window)
            && receivedAt >= window.Since
            && (window.Until is null || receivedAt <= window.Until.Value.AddSeconds(30)))
            return false;

        return true;
    }

    private async Task<IReadOnlyList<HeartbeatOverviewItem>> ReadHeartbeatsAsync(CancellationToken cancellationToken)
    {
        if (_scopeFactory is null)
            return Array.Empty<HeartbeatOverviewItem>();

        var now = _timeProvider.GetUtcNow();
        if (_heartbeatCache is { } cached && now - cached.At < HeartbeatCacheTtl)
            return cached.Items;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var heartbeat = scope.ServiceProvider.GetService<IHeartbeatService>();
            if (heartbeat is null)
                return Array.Empty<HeartbeatOverviewItem>();

            var items = await heartbeat.GetOverviewAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _heartbeatCache = new HeartbeatSnapshot(now, items);
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Advisory only: a heartbeat read failure must never break the status call.
            LogReadingHeartbeatDataForTheSimulation(_logger, ex);
            return Array.Empty<HeartbeatOverviewItem>();
        }
    }

    private sealed record HeartbeatSnapshot(DateTimeOffset At, IReadOnlyList<HeartbeatOverviewItem> Items);

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulation disabled while running; stopping the run.")]
    private static partial void LogSimulationDisabledWhileRunning(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Starting a simulated endpoint host failed; rolling back.")]
    private static partial void LogStartingASimulatedEndpointHostFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulation {Action}: {Publishers} publisher(s), {Hosts} simulated subscriber(s).")]
    private static partial void LogSimulationPublisher(ILogger logger, string action, int publishers, int hosts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulation drain did not finish within {Drain}; stopping receivers.")]
    private static partial void LogSimulationDrainDidNotFinishWithin(ILogger logger, TimeSpan drain);

    [LoggerMessage(Level = LogLevel.Information, Message = "Simulation auto-stop reached at {AutoStopAt}; stopping.")]
    private static partial void LogSimulationAutoStopReached(ILogger logger, DateTimeOffset autoStopAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Host shutdown arrived during a simulation transition; leaving it to the process exit.")]
    private static partial void LogHostShutdownArrivedDuringASimulation(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} simulated subscriber(s) did not stop within {Deadline}; abandoning them.")]
    private static partial void LogSimulatedSubscriber(ILogger logger, int count, TimeSpan deadline);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the simulated subscriber for {EndpointId} did not complete cleanly.")]
    private static partial void LogStoppingTheSimulatedSubscriberForDid(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Drain of the simulated subscriber for {EndpointId} ended early.")]
    private static partial void LogDrainOfTheSimulatedSubscriber(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reading heartbeat data for the simulation's live-instance warning failed.")]
    private static partial void LogReadingHeartbeatDataForTheSimulation(ILogger logger, Exception exception);
}
