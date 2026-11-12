using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NimBus.Core.Diagnostics;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Outbox;

namespace NimBus.OpenTelemetry.Instrumentation;

/// <summary>
/// Hosted service that polls registered metrics-query providers on a fixed
/// cadence (<see cref="NimBusOpenTelemetryOptions.GaugePollInterval"/>) and
/// caches the latest value so the OTel observable-gauge callbacks return
/// synchronously without blocking the export thread (FR-044).
///
/// Skip-when-missing semantics (FR-052): if no provider for a given gauge is
/// registered, the corresponding instruments are not observed, and a single
/// INFO log line records the skip on first poll. Hosts that *want* the gauge
/// register the provider (<see cref="IOutboxMetricsQuery"/>,
/// <see cref="IDeferredMessageMetricsQuery"/>); hosts that don't pay nothing.
/// </summary>
internal sealed partial class NimBusGaugeBackgroundService : BackgroundService
{
    private readonly IOutboxMetricsQuery? _outboxQuery;
    private readonly IDeferredMessageMetricsQuery? _deferredQuery;
    private readonly IOptionsMonitor<NimBusOpenTelemetryOptions> _options;
    private readonly ILogger<NimBusGaugeBackgroundService> _logger;
    private readonly IEndpointCircuitBreaker? _circuitBreaker;
    private readonly ConcurrentDictionary<string, long> _outboxCache = new();
    private readonly ConcurrentDictionary<(string Endpoint, string Metric), long> _deferredCache = new();
    private bool _outboxSkipLogged;
    private bool _deferredSkipLogged;
    // ObservableGauge callbacks live for the lifetime of the static Meter; we
    // can't unregister them. Once the service is disposed (or stops), the
    // callbacks must observe nothing so they don't interfere with later
    // process-wide consumers (e.g. test isolation, reload scenarios).
    private volatile bool _stopped;

    public NimBusGaugeBackgroundService(
        IOptionsMonitor<NimBusOpenTelemetryOptions> options,
        IOutboxMetricsQuery? outboxQuery = null,
        IDeferredMessageMetricsQuery? deferredQuery = null,
        ILogger<NimBusGaugeBackgroundService>? logger = null,
        IEndpointCircuitBreaker? circuitBreaker = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _outboxQuery = outboxQuery;
        _deferredQuery = deferredQuery;
        _logger = logger ?? NullLogger<NimBusGaugeBackgroundService>.Instance;
        _circuitBreaker = circuitBreaker;

        RegisterGauges();
    }

    private void RegisterGauges()
    {
        // Outbox depth + lag.
        NimBusMeters.Outbox.CreateObservableGauge(
            "nimbus.outbox.pending",
            ObserveOutboxPending,
            unit: "{messages}",
            description: "Outbox rows waiting to be dispatched.");

        NimBusMeters.Outbox.CreateObservableGauge(
            "nimbus.outbox.dispatch_lag",
            ObserveOutboxDispatchLag,
            unit: "s",
            description: "Seconds since the oldest pending outbox row was enqueued.");

        // Deferred depth + blocked sessions (per endpoint).
        NimBusMeters.DeferredProcessor.CreateObservableGauge(
            "nimbus.deferred.pending",
            ObserveDeferredPending,
            unit: "{messages}",
            description: "Messages currently parked on the deferred subscription.");

        NimBusMeters.DeferredProcessor.CreateObservableGauge(
            "nimbus.deferred.blocked_sessions",
            ObserveBlockedSessions,
            unit: "{sessions}",
            description: "Distinct sessions currently blocked.");

        NimBusMeters.Consumer.CreateObservableGauge(
            "nimbus.circuit_breaker.state",
            ObserveCircuitBreakerState,
            description: "Endpoint circuit state: Closed=0, Open=1, HalfOpen=2.");
    }

    private IEnumerable<Measurement<long>> ObserveOutboxPending() =>
        !_stopped && _outboxCache.TryGetValue("pending", out var v)
            ? new[] { new Measurement<long>(v) }
            : Array.Empty<Measurement<long>>();

    private IEnumerable<Measurement<long>> ObserveOutboxDispatchLag() =>
        !_stopped && _outboxCache.TryGetValue("dispatch_lag_s", out var v)
            ? new[] { new Measurement<long>(v) }
            : Array.Empty<Measurement<long>>();

    private IEnumerable<Measurement<long>> ObserveDeferredPending() =>
        EmitDeferred("pending");

    private IEnumerable<Measurement<long>> ObserveBlockedSessions() =>
        EmitDeferred("blocked_sessions");

    private IEnumerable<Measurement<int>> ObserveCircuitBreakerState()
    {
        if (_stopped || _circuitBreaker is null)
            return Array.Empty<Measurement<int>>();

        return
        [
            new Measurement<int>(
                (int)_circuitBreaker.State,
                new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, _circuitBreaker.Endpoint)),
        ];
    }

    private IEnumerable<Measurement<long>> EmitDeferred(string metric)
    {
        if (_stopped) return Array.Empty<Measurement<long>>();
        var output = new List<Measurement<long>>();
        foreach (var ((endpoint, m), value) in _deferredCache.Select(kv => (kv.Key, kv.Value)))
        {
            if (!string.Equals(m, metric, StringComparison.Ordinal))
                continue;
            output.Add(new Measurement<long>(value, new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint)));
        }
        return output;
    }

    public override void Dispose()
    {
        _stopped = true;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the cache before the first OTel collection cycle so callbacks
        // don't return empty for an entire export interval.
        await PollAsync(stoppingToken).ConfigureAwait(false);

        // The poll interval is read from IOptionsMonitor on every tick so a
        // reload via `IOptionsMonitor.OnChange` retunes the cadence — the timer
        // is recreated whenever the configured interval changes.
        var currentInterval = _options.CurrentValue.GaugePollInterval;
        PeriodicTimer timer = new(currentInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PollAsync(stoppingToken).ConfigureAwait(false);

                var nextInterval = _options.CurrentValue.GaugePollInterval;
                if (nextInterval != currentInterval)
                {
                    timer.Dispose();
                    currentInterval = nextInterval;
                    timer = new PeriodicTimer(currentInterval);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        await PollOutboxAsync(cancellationToken).ConfigureAwait(false);
        await PollDeferredAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PollOutboxAsync(CancellationToken cancellationToken)
    {
        if (_outboxQuery is null)
        {
            if (!_outboxSkipLogged)
            {
                LogNimbusOutboxPendingAndNimbusOutbox(_logger);
                _outboxSkipLogged = true;
            }
            return;
        }

        try
        {
            var pending = await _outboxQuery.GetPendingCountAsync(cancellationToken).ConfigureAwait(false);
            _outboxCache["pending"] = pending;

            var oldest = await _outboxQuery.GetOldestPendingEnqueuedAtUtcAsync(cancellationToken).ConfigureAwait(false);
            if (oldest.HasValue)
            {
                var lag = (long)Math.Max(0, (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds);
                _outboxCache["dispatch_lag_s"] = lag;
            }
            else
            {
                _outboxCache["dispatch_lag_s"] = 0;
            }
        }
        catch (Exception ex)
        {
            LogOutboxGaugePollFailed(_logger, ex);
        }
    }

    private async Task PollDeferredAsync(CancellationToken cancellationToken)
    {
        if (_deferredQuery is null)
        {
            if (!_deferredSkipLogged)
            {
                LogNimbusDeferredPendingAndNimbusDeferred(_logger);
                _deferredSkipLogged = true;
            }
            return;
        }

        IReadOnlyCollection<string> endpoints;
        try
        {
            endpoints = _deferredQuery.GetEndpointIds();
        }
        catch (Exception ex)
        {
            // Endpoint discovery itself failed — retain previous cache and log.
            // Without this guard the exception faults ExecuteAsync, which (depending
            // on host settings) can stop the entire background service permanently.
            LogDeferredGaugeEndpointDiscoveryFailed(_logger, ex);
            return;
        }

        // Track which keys this poll cycle observed so we can prune the cache
        // of endpoints / metrics that have stopped being reported. Without
        // pruning, a removed endpoint (or a blocked-sessions value that flips
        // from non-null to null) leaves stale measurements exporting forever.
        var observed = new HashSet<(string Endpoint, string Metric)>();

        foreach (var endpoint in endpoints)
        {
            try
            {
                var pending = await _deferredQuery.GetDeferredPendingCountAsync(endpoint, cancellationToken).ConfigureAwait(false);
                _deferredCache[(endpoint, "pending")] = pending;
                observed.Add((endpoint, "pending"));

                var blocked = await _deferredQuery.GetBlockedSessionCountAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (blocked.HasValue)
                {
                    _deferredCache[(endpoint, "blocked_sessions")] = blocked.Value;
                    observed.Add((endpoint, "blocked_sessions"));
                }
            }
            catch (Exception ex)
            {
                // Preserve the previous cache entries for this endpoint on transient
                // failure — but also mark them observed so we don't prune them as
                // if the endpoint had gone away.
                observed.Add((endpoint, "pending"));
                observed.Add((endpoint, "blocked_sessions"));
                LogDeferredGaugePollFailedForEndpoint(_logger, ex, endpoint);
            }
        }

        // Prune anything in the cache that wasn't observed this cycle.
        foreach (var stale in _deferredCache.Keys.Where(k => !observed.Contains(k)).ToList())
        {
            _deferredCache.TryRemove(stale, out _);
        }
    }

    /// <summary>
    /// Test-only synchronous poll. Production code drives polling via
    /// <see cref="ExecuteAsync(CancellationToken)"/>.
    /// </summary>
    internal Task PollNowAsync(CancellationToken cancellationToken = default) => PollAsync(cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "nimbus.outbox.pending and nimbus.outbox.dispatch_lag will not be reported because no IOutboxMetricsQuery is registered.")]
    private static partial void LogNimbusOutboxPendingAndNimbusOutbox(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox gauge poll failed; previous cached values retained.")]
    private static partial void LogOutboxGaugePollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "nimbus.deferred.pending and nimbus.deferred.blocked_sessions will not be reported because no IDeferredMessageMetricsQuery is registered.")]
    private static partial void LogNimbusDeferredPendingAndNimbusDeferred(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred gauge endpoint discovery failed; previous cached values retained.")]
    private static partial void LogDeferredGaugeEndpointDiscoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred gauge poll failed for endpoint {Endpoint}; previous cached values retained.")]
    private static partial void LogDeferredGaugePollFailedForEndpoint(ILogger logger, Exception exception, string endpoint);
}
