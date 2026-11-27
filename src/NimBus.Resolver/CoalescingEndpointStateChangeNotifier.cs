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
/// them per endpoint. <see cref="NotifyEndpointStateChangedAsync"/> only marks the endpoint
/// dirty and returns, so a message is completed without waiting on the WebApp. Each endpoint
/// is throttled on its own: its first change after a quiet interval is sent at once, it is
/// sent at most once per interval after that, and a change that arrives inside the interval
/// (or while its previous notification is still being sent) is sent when the interval ends,
/// so the WebApp always hears about the last change.
/// </summary>
/// <remarks>
/// <para>Without this, every applied write cost one HTTP call, one endpoint recount and one
/// SignalR broadcast, and the message waited for the call (up to its 10 s timeout).</para>
/// <para>Different endpoints are sent concurrently, at most <c>maxConcurrentSends</c> at a
/// time, so one slow WebApp call does not hold up the other endpoints. One endpoint never has
/// more than one notification in flight.</para>
/// <para>At most <c>maxPendingEndpoints</c> endpoints wait at a time; a change for another
/// endpoint is dropped and logged, which only delays that endpoint's live update until the
/// pages poll. Failures of the inner notifier are logged, never thrown. Disposing it
/// asynchronously (as the host does on shutdown) sends what is still pending, bounded by a
/// short timeout. Heartbeat and service-health notifications are rare and pass straight
/// through.</para>
/// </remarks>
internal sealed partial class CoalescingEndpointStateChangeNotifier : IMessageStateChangeNotifier, IAsyncDisposable, IDisposable
{
    /// <summary>The default interval: at most one notification per endpoint per second.</summary>
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>The default number of endpoints that may wait for their notification.</summary>
    internal const int DefaultMaxPendingEndpoints = 10_000;

    /// <summary>The default number of endpoints whose notifications may be in flight at once.</summary>
    internal const int DefaultMaxConcurrentSends = 4;

    /// <summary>How long shutdown waits for the last notifications.</summary>
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly IMessageStateChangeNotifier _inner;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly int _maxPendingEndpoints;
    private readonly int _maxConcurrentSends;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    // An endpoint has a slot while it is queued, being sent, or inside the interval after its
    // last send; a slot whose interval ends with nothing to send is removed.
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Queue<Slot> _ready = new();
    private readonly HashSet<Task> _sends = [];
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Cancelled on dispose, never disposed: see DisposeInner.")]
    private readonly CancellationTokenSource _shutdown = new();

    private int _pendingCount;
    private bool _disposed;

    /// <param name="inner">The notifier that delivers each coalesced notification; disposed with this one.</param>
    /// <param name="interval">The least time between the starts of two notifications for one endpoint; zero sends a change as soon as the endpoint's previous notification ends.</param>
    /// <param name="timeProvider">The clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="maxPendingEndpoints">How many endpoints may wait for their notification.</param>
    /// <param name="maxConcurrentSends">How many endpoints' notifications may be in flight at once.</param>
    public CoalescingEndpointStateChangeNotifier(
        IMessageStateChangeNotifier inner,
        TimeSpan interval,
        TimeProvider? timeProvider = null,
        ILogger<CoalescingEndpointStateChangeNotifier>? logger = null,
        int maxPendingEndpoints = DefaultMaxPendingEndpoints,
        int maxConcurrentSends = DefaultMaxConcurrentSends)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingEndpoints, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentSends, 1);
        _inner = inner;
        _interval = interval;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CoalescingEndpointStateChangeNotifier>.Instance;
        _maxPendingEndpoints = maxPendingEndpoints;
        _maxConcurrentSends = maxConcurrentSends;
    }

    /// <summary>The notifier that delivers the coalesced notifications.</summary>
    internal IMessageStateChangeNotifier Inner => _inner;

    /// <summary>The least time between two notifications for one endpoint.</summary>
    internal TimeSpan Interval => _interval;

    /// <summary>How many endpoints' notifications may be in flight at once.</summary>
    internal int MaxConcurrentSends => _maxConcurrentSends;

    /// <summary>
    /// Completes when no notification is in flight, including notifications that start while
    /// it waits (a queued endpoint, or a change already due when its endpoint's send ends);
    /// tests await it. Notifications still waiting for their interval are not awaited.
    /// </summary>
    internal async Task WhenSendsCompleteAsync()
    {
        while (true)
        {
            Task[] sends;
            lock (_gate)
                sends = [.. _sends];
            if (sends.Length == 0)
                return;
            await Task.WhenAll(sends).ConfigureAwait(false);
        }
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

            _slots.TryGetValue(endpointId, out var slot);
            if (slot is { Dirty: true })
                return Task.CompletedTask;

            if (_pendingCount >= _maxPendingEndpoints)
            {
                LogPendingEndpointsFull(_logger, _maxPendingEndpoints, endpointId);
                return Task.CompletedTask;
            }

            _pendingCount++;
            if (slot is null)
            {
                // A quiet endpoint: its change is sent as soon as a send slot is free.
                slot = new Slot(endpointId) { Dirty = true };
                _slots.Add(endpointId, slot);
                EnqueueLocked(slot);
            }
            else
            {
                // In flight or inside its interval: the endpoint's trailing send carries this change.
                slot.Dirty = true;
            }
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
    /// Stops the interval timers, waits for the notifications in flight, sends what is still
    /// pending (both bounded by <see cref="ShutdownFlushTimeout"/>) and disposes the inner notifier.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task[] running;
        List<ITimer> timers;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            running = [.. _sends];
            timers = TimersLocked();
        }

        foreach (var timer in timers)
            await timer.DisposeAsync().ConfigureAwait(false);

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
    /// Stops the interval timers and disposes the inner notifier without sending what is
    /// pending; hosts dispose asynchronously, which flushes first.
    /// </summary>
    public void Dispose()
    {
        List<ITimer> timers;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            timers = TimersLocked();
        }

        foreach (var timer in timers)
            timer.Dispose();
        _shutdown.Cancel();
        DisposeInner();
    }

    // The shutdown source is cancelled, not disposed: a send still running after a synchronous
    // Dispose may yet read its token, and a source without a timer holds nothing to release.
    private void DisposeInner() => (_inner as IDisposable)?.Dispose();

    private List<ITimer> TimersLocked()
    {
        List<ITimer> timers = [];
        foreach (var slot in _slots.Values)
        {
            if (slot.Timer is { } timer)
                timers.Add(timer);
        }

        return timers;
    }

    private async Task FinalFlushAsync(Task[] running)
    {
        // Sends in flight finish first: an endpoint changed during its send is sent once more.
        await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        string[] pending;
        lock (_gate)
        {
            pending = [.. _slots.Values.Where(slot => slot.Dirty).Select(slot => slot.EndpointId).Order(StringComparer.Ordinal)];
            foreach (var slot in _slots.Values)
                slot.Dirty = false;
            _pendingCount = 0;
        }

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentSends },
            async (endpointId, _) => await SendOneAsync(endpointId).ConfigureAwait(false)).ConfigureAwait(false);
    }

    // Under _gate, with the slot dirty and neither queued nor in flight.
    private void EnqueueLocked(Slot slot)
    {
        slot.State = SlotState.Queued;
        _ready.Enqueue(slot);
        PumpLocked();
    }

    // Under _gate: start queued endpoints while fewer than the maximum are in flight.
    private void PumpLocked()
    {
        while (!_disposed && _sends.Count < _maxConcurrentSends && _ready.TryDequeue(out var slot))
        {
            slot.State = SlotState.Sending;
            slot.Dirty = false;
            _pendingCount--;
            slot.LastStarted = _timeProvider.GetTimestamp();
            // Off the caller's thread: the caller is the Resolver's message path. The send's
            // completion takes _gate, so it cannot finish before it is recorded here.
            var send = Task.Run(() => RunSendAsync(slot));
            slot.Send = send;
            _sends.Add(send);
        }
    }

    private async Task RunSendAsync(Slot slot)
    {
        await SendOneAsync(slot.EndpointId).ConfigureAwait(false);

        lock (_gate)
        {
            if (slot.Send is { } send)
                _sends.Remove(send);
            slot.Send = null;
            slot.State = SlotState.Waiting;
            if (_disposed)
                return;
            WaitOutIntervalLocked(slot);
            PumpLocked();
        }
    }

    // Under _gate, with the slot's send finished: queue its trailing change once the interval
    // since that send began has passed; with nothing to send, drop the slot when it ends.
    private void WaitOutIntervalLocked(Slot slot)
    {
        var due = slot.LastStarted is { } started
            ? _interval - _timeProvider.GetElapsedTime(started)
            : TimeSpan.Zero;

        if (due > TimeSpan.Zero)
        {
            slot.State = SlotState.Waiting;
            slot.Timer ??= _timeProvider.CreateTimer(OnTimer, slot, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            slot.Timer.Change(due, Timeout.InfiniteTimeSpan);
        }
        else if (slot.Dirty)
        {
            EnqueueLocked(slot);
        }
        else
        {
            RemoveLocked(slot);
        }
    }

    private void OnTimer(object? state)
    {
        var slot = (Slot)state!;
        lock (_gate)
        {
            if (_disposed
                || slot.State != SlotState.Waiting
                || !_slots.TryGetValue(slot.EndpointId, out var current)
                || !ReferenceEquals(current, slot))
            {
                return;
            }

            if (slot.Dirty)
                EnqueueLocked(slot);
            else
                RemoveLocked(slot);
        }
    }

    private void RemoveLocked(Slot slot)
    {
        _slots.Remove(slot.EndpointId);
        slot.Timer?.Dispose();
        slot.Timer = null;
    }

    private async Task SendOneAsync(string endpointId)
    {
        try
        {
            await _inner.NotifyEndpointStateChangedAsync(endpointId, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutting down: what is still pending shows when the pages poll.
        }
        catch (Exception ex)
        {
            // Best-effort: a lost push only delays a live update until the pages poll.
            LogNotificationFailed(_logger, ex, endpointId);
        }
    }

    private enum SlotState
    {
        Queued,
        Sending,
        Waiting,
    }

    private sealed class Slot(string endpointId)
    {
        public string EndpointId { get; } = endpointId;
        public SlotState State { get; set; }
        public bool Dirty { get; set; }
        public long? LastStarted { get; set; }
        public ITimer? Timer { get; set; }
        public Task? Send { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint state notifications: {MaxPending} endpoints are already waiting; the change for {EndpointId} is not pushed and shows when the pages poll.")]
    private static partial void LogPendingEndpointsFull(ILogger logger, int maxPending, string endpointId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Endpoint state notifications: failed to notify the change for {EndpointId}.")]
    private static partial void LogNotificationFailed(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint state notifications: the shutdown flush did not finish within {TimeoutSeconds} s; pending changes show when the pages poll.")]
    private static partial void LogShutdownFlushTimedOut(ILogger logger, double timeoutSeconds);
}
