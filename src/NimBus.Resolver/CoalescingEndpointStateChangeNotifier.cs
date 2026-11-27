#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Resolver;

/// <summary>
/// Takes endpoint state-change notifications off the Resolver's message path and coalesces
/// them. <see cref="NotifyEndpointStateChangedAsync"/> only marks the endpoint dirty and
/// returns, so a message is completed without waiting on the WebApp. A background flush
/// sends one notification per dirty endpoint, at most once per interval: the first change
/// after a quiet interval goes out at once, and changes that arrive inside the interval are
/// sent together when it ends, so the WebApp always hears about the last change.
/// </summary>
/// <remarks>
/// <para>Without this, every applied write cost one HTTP call, one endpoint recount and one
/// SignalR broadcast, and the message waited for the call (up to its 10 s timeout).</para>
/// <para>At most <c>maxPendingEndpoints</c> endpoints wait at a time; a change for another
/// endpoint is dropped and logged, which only delays that endpoint's live update until the
/// pages poll. Failures of the inner notifier are logged, never thrown. Disposing it
/// asynchronously (as the host does on shutdown) sends what is still pending, bounded by a
/// short timeout. Heartbeat and service-health notifications are rare and pass straight
/// through.</para>
/// </remarks>
internal sealed partial class CoalescingEndpointStateChangeNotifier : IMessageStateChangeNotifier, IAsyncDisposable, IDisposable
{
    /// <summary>The default flush interval: at most one notification per endpoint per second.</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>The default number of endpoints that may wait for a flush.</summary>
    internal const int DefaultMaxPendingEndpoints = 10_000;

    /// <summary>How long shutdown waits for the last flush.</summary>
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IMessageStateChangeNotifier _inner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly int _maxPendingEndpoints;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private readonly ITimer _timer;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Cancelled on dispose, never disposed: see DisposeInner.")]
    private readonly CancellationTokenSource _shutdown = new();

    private FlushState _state;
    private long? _lastFlushStarted;
    private Task _flush = Task.CompletedTask;
    private bool _disposed;

    /// <param name="inner">The notifier that delivers each coalesced notification; disposed with this one.</param>
    /// <param name="interval">The least time between two flushes; zero flushes as soon as the previous one ends.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="maxPendingEndpoints">How many endpoints may wait for a flush.</param>
    public CoalescingEndpointStateChangeNotifier(
        IMessageStateChangeNotifier inner,
        TimeSpan interval,
        TimeProvider? timeProvider = null,
        ILogger<CoalescingEndpointStateChangeNotifier>? logger = null,
        int maxPendingEndpoints = DefaultMaxPendingEndpoints)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingEndpoints, 1);
        _inner = inner;
        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CoalescingEndpointStateChangeNotifier>.Instance;
        _maxPendingEndpoints = maxPendingEndpoints;
        _timer = _timeProvider.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The notifier that delivers the coalesced notifications.</summary>
    internal IMessageStateChangeNotifier Inner => _inner;

    /// <summary>The least time between two flushes.</summary>
    internal TimeSpan Interval => _interval;

    /// <summary>The flush that is running or last ran; tests await it.</summary>
    internal Task CurrentFlush
    {
        get { lock (_gate) return _flush; }
    }

    /// <summary>
    /// Marks <paramref name="endpointId"/> changed and returns without waiting for delivery.
    /// </summary>
    /// <param name="endpointId">The endpoint whose state changed.</param>
    /// <param name="cancellationToken">Unused: delivery does not belong to the caller's message.</param>
    public Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(endpointId))
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;

            if (!_dirty.Contains(endpointId))
            {
                if (_dirty.Count >= _maxPendingEndpoints)
                {
                    LogPendingEndpointsFull(_logger, _maxPendingEndpoints, endpointId);
                    return Task.CompletedTask;
                }

                _dirty.Add(endpointId);
            }

            if (_state == FlushState.Idle)
                ScheduleLocked();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        => _inner.NotifyHeartbeatChangedAsync(endpointId, cancellationToken);

    /// <inheritdoc />
    public Task NotifyServiceHealthChangedAsync(string serviceId, CancellationToken cancellationToken = default)
        => _inner.NotifyServiceHealthChangedAsync(serviceId, cancellationToken);

    /// <summary>
    /// Stops the flush timer, waits for the running flush, sends what is still pending (both
    /// bounded by <see cref="ShutdownFlushTimeout"/>) and disposes the inner notifier.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            running = _flush;
        }

        await _timer.DisposeAsync().ConfigureAwait(false);
        try
        {
            await FinalFlushAsync(running).WaitAsync(ShutdownFlushTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LogShutdownFlushTimedOut(_logger, ShutdownFlushTimeout.TotalSeconds);
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        DisposeInner();
    }

    /// <summary>
    /// Stops the flush timer and disposes the inner notifier without sending what is pending;
    /// hosts dispose asynchronously, which flushes first.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _timer.Dispose();
        _shutdown.Cancel();
        DisposeInner();
    }

    // The shutdown source is cancelled, not disposed: a flush still running after a synchronous
    // Dispose may yet read its token, and a source without a timer holds nothing to release.
    private void DisposeInner() => (_inner as IDisposable)?.Dispose();

    private async Task FinalFlushAsync(Task running)
    {
        await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        string[] batch;
        lock (_gate)
        {
            batch = [.. _dirty];
            _dirty.Clear();
        }

        await SendAsync(batch).ConfigureAwait(false);
    }

    // Under _gate, with _state Idle and endpoints pending: flush now when the interval since
    // the last flush has passed, otherwise arm the timer for the rest of it.
    private void ScheduleLocked()
    {
        var due = _lastFlushStarted is { } started
            ? _interval - _timeProvider.GetElapsedTime(started)
            : TimeSpan.Zero;

        if (due <= TimeSpan.Zero)
        {
            StartFlushLocked();
        }
        else
        {
            _state = FlushState.Armed;
            _timer.Change(due, Timeout.InfiniteTimeSpan);
        }
    }

    private void StartFlushLocked()
    {
        _state = FlushState.Flushing;
        _lastFlushStarted = _timeProvider.GetTimestamp();
        string[] batch = [.. _dirty];
        _dirty.Clear();
        // Off the caller's thread: the caller is the Resolver's message path.
        _flush = Task.Run(() => FlushAsync(batch));
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed || _state != FlushState.Armed)
                return;
            StartFlushLocked();
        }
    }

    private async Task FlushAsync(string[] batch)
    {
        await SendAsync(batch).ConfigureAwait(false);

        lock (_gate)
        {
            _state = FlushState.Idle;
            if (!_disposed && _dirty.Count > 0)
                ScheduleLocked();
        }
    }

    private async Task SendAsync(IEnumerable<string> batch)
    {
        foreach (var endpointId in batch.OrderBy(id => id, StringComparer.Ordinal))
        {
            try
            {
                await _inner.NotifyEndpointStateChangedAsync(endpointId, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Best-effort: a lost push only delays a live update until the pages poll.
                LogNotificationFailed(_logger, ex, endpointId);
            }
        }
    }

    private enum FlushState
    {
        Idle,
        Armed,
        Flushing,
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint state notifications: {MaxPending} endpoints are already waiting; the change for {EndpointId} is not pushed and shows when the pages poll.")]
    private static partial void LogPendingEndpointsFull(ILogger logger, int maxPending, string endpointId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Endpoint state notifications: failed to notify the change for {EndpointId}.")]
    private static partial void LogNotificationFailed(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint state notifications: the shutdown flush did not finish within {TimeoutSeconds} s; pending changes show when the pages poll.")]
    private static partial void LogShutdownFlushTimedOut(ILogger logger, double timeoutSeconds);
}
