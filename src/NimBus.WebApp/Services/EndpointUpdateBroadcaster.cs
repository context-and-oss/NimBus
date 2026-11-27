using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Hubs;

namespace NimBus.WebApp.Services;

/// <summary>
/// Turns storage-hook calls (the Resolver write path and the Cosmos Change Feed → Event Grid
/// path) into <c>endpointupdate</c> broadcasts, throttled per endpoint.
/// </summary>
public interface IEndpointUpdateBroadcaster
{
    /// <summary>
    /// Records that <paramref name="endpointId"/> changed. The first change after a quiet
    /// interval is recounted and broadcast at once, and the returned task completes when that
    /// broadcast is sent. Changes inside the interval complete at once and are recounted and
    /// broadcast together when it ends, so the last state is always broadcast.
    /// </summary>
    /// <param name="endpointId">The endpoint whose state changed.</param>
    Task RequestAsync(string endpointId);
}

/// <summary>
/// Singleton <see cref="IEndpointUpdateBroadcaster"/>. Each endpoint gets at most one recount
/// and one broadcast to all clients per <see cref="DefaultInterval"/>, however many writes
/// the Resolver applies. The recount goes through
/// <see cref="IStoreResultCache.RefreshAsync{T}"/> under the endpoint status count's cache key,
/// so it also refreshes what the endpoint status API and the Monitor read, and it joins a
/// refresh another caller already queued instead of adding a query.
/// </summary>
public sealed partial class EndpointUpdateBroadcaster : IEndpointUpdateBroadcaster, IDisposable
{
    /// <summary>The least time between two broadcasts for one endpoint.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly IHubContext<GridEventsHub> _hubContext;
    private readonly IMessageTrackingStore _messageStore;
    private readonly IStoreResultCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EndpointUpdateBroadcaster> _logger;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the broadcaster with the <see cref="DefaultInterval"/>.</summary>
    /// <param name="hubContext">The SignalR hub the broadcasts go to.</param>
    /// <param name="messageStore">The store the endpoint is recounted from.</param>
    /// <param name="cache">The store-result cache that coalesces the recounts.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public EndpointUpdateBroadcaster(
        IHubContext<GridEventsHub> hubContext,
        IMessageTrackingStore messageStore,
        IStoreResultCache cache,
        TimeProvider timeProvider,
        ILogger<EndpointUpdateBroadcaster> logger)
        : this(hubContext, messageStore, cache, timeProvider, logger, DefaultInterval)
    {
    }

    internal EndpointUpdateBroadcaster(
        IHubContext<GridEventsHub> hubContext,
        IMessageTrackingStore messageStore,
        IStoreResultCache cache,
        TimeProvider timeProvider,
        ILogger<EndpointUpdateBroadcaster> logger,
        TimeSpan interval)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        _interval = interval;
    }

    /// <summary>The broadcast running or last run for <paramref name="endpointId"/>; tests await it.</summary>
    internal Task CurrentBroadcast(string endpointId)
    {
        lock (_gate)
            return _slots.TryGetValue(endpointId, out var slot) ? slot.Current : Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RequestAsync(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;

            if (!_slots.TryGetValue(endpointId, out var slot))
            {
                slot = new Slot(endpointId);
                _slots.Add(endpointId, slot);
            }

            switch (slot.State)
            {
                case SlotState.Idle:
                    return ScheduleLocked(slot);
                case SlotState.Running:
                    slot.Pending = true;
                    return Task.CompletedTask;
                default:
                    // Armed: the trailing broadcast already covers this change.
                    return Task.CompletedTask;
            }
        }
    }

    /// <summary>Stops the trailing-broadcast timers.</summary>
    public void Dispose()
    {
        List<ITimer> timers = [];
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var slot in _slots.Values)
            {
                if (slot.Timer is { } timer)
                    timers.Add(timer);
            }
        }

        foreach (var timer in timers)
            timer.Dispose();
    }

    // Under _gate, with the slot Idle: broadcast now when the interval since its last broadcast
    // has passed (the caller awaits that broadcast), otherwise arm its trailing timer.
    private Task ScheduleLocked(Slot slot)
    {
        var due = slot.LastStarted is { } started
            ? _interval - _timeProvider.GetElapsedTime(started)
            : TimeSpan.Zero;

        if (due <= TimeSpan.Zero)
            return StartLocked(slot);

        slot.State = SlotState.Armed;
        slot.Timer ??= _timeProvider.CreateTimer(OnTimer, slot, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        slot.Timer.Change(due, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    private Task StartLocked(Slot slot)
    {
        slot.State = SlotState.Running;
        slot.Pending = false;
        slot.LastStarted = _timeProvider.GetTimestamp();
        slot.Current = BroadcastAsync(slot);
        return slot.Current;
    }

    private void OnTimer(object? state)
    {
        var slot = (Slot)state!;
        lock (_gate)
        {
            if (_disposed || slot.State != SlotState.Armed)
                return;
            // A trailing broadcast has no caller to await it; BroadcastAsync never throws.
            _ = StartLocked(slot);
        }
    }

    private async Task BroadcastAsync(Slot slot)
    {
        // Leave the caller's lock before touching the store.
        await Task.Yield();
        try
        {
            var state = await _cache.RefreshAsync(
                EndpointStateCountCache.CacheKey(slot.EndpointId),
                EndpointStateCountCache.Ttl,
                () => _messageStore.DownloadEndpointStateCount(slot.EndpointId)).ConfigureAwait(false);
            var endpointStatus = Mapper.EndpointStatusCountFromEndpointStateCount(state);
            await _hubContext.Clients.All.SendAsync(Constants.EventSignalNames.EndpointUpdate, endpointStatus).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: a missed broadcast only delays a live update until the pages poll.
            LogEndpointUpdateBroadcastFailed(_logger, ex, slot.EndpointId);
        }
        finally
        {
            lock (_gate)
            {
                slot.State = SlotState.Idle;
                if (slot.Pending && !_disposed)
                    _ = ScheduleLocked(slot);
            }
        }
    }

    private enum SlotState
    {
        Idle,
        Armed,
        Running,
    }

    private sealed class Slot(string endpointId)
    {
        public string EndpointId { get; } = endpointId;
        public SlotState State { get; set; }
        public bool Pending { get; set; }
        public long? LastStarted { get; set; }
        public ITimer? Timer { get; set; }
        public Task Current { get; set; } = Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage hook: could not recount and broadcast endpoint {EndpointId}; the pages pick the change up when they poll.")]
    private static partial void LogEndpointUpdateBroadcastFailed(ILogger logger, Exception exception, string endpointId);
}
