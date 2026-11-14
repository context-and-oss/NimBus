using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Core.Diagnostics;
using NimBus.Core.Endpoints;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.Hubs;
using CoreConstants = NimBus.Core.Messages.Constants;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;
using SignalNames = NimBus.WebApp.Constants.EventSignalNames;
using StoreHeartbeat = NimBus.MessageStore.States.Heartbeat;

namespace NimBus.WebApp.Services.Heartbeat;

/// <summary>
/// Sends platform heartbeat probes and reads back what they settled to.
/// </summary>
/// <remarks>
/// Probes go straight to each endpoint's topic (and the Resolver's, for liveness)
/// rather than through the Manager topic: endpoint topics already carry the
/// Resolver subscription that brings the answers home, so nothing in the topology
/// changes.
/// </remarks>
public sealed partial class HeartbeatService : IHeartbeatService
{
    private const int MinimumIntervalSeconds = 30;
    private const int MinimumTimeoutSeconds = 5;
    private const int HeartbeatHistoryRetentionDays = 90;
    private const string HeartbeatSessionId = "Heartbeat";

    // Resolver subscriptions are session-enabled, so a probe sharing the endpoint
    // session would queue behind every endpoint heartbeat reply and report an
    // inflated round-trip. Give it its own session.
    private const string ResolverProbeSessionId = "Heartbeat-Resolver";

    private readonly IPlatform _platform;
    private readonly IEndpointMetadataStore _store;
    private readonly IServiceHealthStore _serviceHealthStore;
    private readonly IHeartbeatMessageSender _sender;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly IHubContext<GridEventsHub>? _hubContext;
    private readonly IHeartbeatHistoryStore? _historyStore;

    /// <summary>Creates the heartbeat service.</summary>
    /// <param name="platform">The compile-time endpoint catalog that defines who gets probed.</param>
    /// <param name="metadataStore">Heartbeat and endpoint metadata storage.</param>
    /// <param name="serviceHealthStore">Service health storage.</param>
    /// <param name="sender">Transport seam for the probes.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="hubContext">SignalR hub for live operator updates; optional so the service is testable headless.</param>
    /// <param name="historyStore">Durable history capability; optional for backward-compatible third-party stores.</param>
    public HeartbeatService(
        IPlatform platform,
        IEndpointMetadataStore metadataStore,
        IServiceHealthStore serviceHealthStore,
        IHeartbeatMessageSender sender,
        ILogger<HeartbeatService> logger,
        IHubContext<GridEventsHub>? hubContext = null,
        IHeartbeatHistoryStore? historyStore = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(serviceHealthStore);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(logger);

        _platform = platform;
        _store = metadataStore;
        _serviceHealthStore = serviceHealthStore;
        _sender = sender;
        _logger = logger;
        _hubContext = hubContext;
        _historyStore = historyStore;
    }

    /// <inheritdoc />
    public Task<HeartbeatSettings> GetSettingsAsync() => _store.GetHeartbeatSettings();

    /// <inheritdoc />
    public async Task<HeartbeatSettings> SetSettingsAsync(HeartbeatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.IntervalSeconds = Math.Max(settings.IntervalSeconds, MinimumIntervalSeconds);
        // A timeout longer than the interval could never elapse between probes.
        settings.TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, MinimumTimeoutSeconds, settings.IntervalSeconds);

        await _store.SetHeartbeatSettings(settings);
        await BroadcastHeartbeatUpdateAsync();
        // Read back rather than echoing the request: LastSentAtUtc is owned by the
        // send claim and the store keeps its own value when the write carries none.
        return await _store.GetHeartbeatSettings();
    }

    // A probe is answered by the endpoint's running subscriber, so only endpoints
    // that consume something can answer one. Probing a producer-only endpoint —
    // which by design runs no subscriber — records a miss every interval, reports
    // it as an outage and drags fleet uptime down over an endpoint that was never
    // reachable in the first place. Producers stay visible everywhere else; they
    // are simply not part of the reachability fleet.
    private static bool IsProbeable(IEndpoint endpoint) => endpoint.EventTypesConsumed.Any();

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeartbeatOverviewItem>> GetOverviewAsync()
    {
        var rows = await _store.GetHeartbeatOverview();
        // Keep the most recently probed row per endpoint. The store deliberately
        // does not deduplicate — see IndexByEndpointId for why duplicates exist.
        var byEndpoint = IndexByEndpointId(
            rows,
            row => row.EndpointId,
            (winner, candidate) => (candidate.LastStartTime ?? DateTime.MinValue) > (winner.LastStartTime ?? DateTime.MinValue)
                ? candidate
                : winner,
            "heartbeat overview");

        return _platform.Endpoints
            .Where(IsProbeable)
            .OrderBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint =>
            {
                if (byEndpoint.TryGetValue(endpoint.Id, out var row))
                {
                    row.EndpointId = endpoint.Id;
                    return row;
                }

                return new HeartbeatOverviewItem
                {
                    EndpointId = endpoint.Id,
                    Status = HeartbeatStatus.Unknown,
                };
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int> SweepTimeoutsAsync()
    {
        var settings = await _store.GetHeartbeatSettings();
        // One cutoff for both sweeps: endpoints and platform services time out on
        // the same clock, so a split cutoff would settle them at different ages.
        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(settings.TimeoutSeconds, MinimumTimeoutSeconds));

        var sweptEndpoints = await _store.SweepTimedOutHeartbeats(cutoff);
        if (sweptEndpoints.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var endpoints = string.Join(", ", sweptEndpoints);
                LogSweptEndpoints(sweptEndpoints.Count, endpoints);
            }
            await BroadcastHeartbeatUpdateAsync();
        }

        var sweptServices = await _serviceHealthStore.SweepTimedOutServiceProbes(cutoff);
        if (sweptServices.Count > 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                var services = string.Join(", ", sweptServices);
                LogSweptServices(sweptServices.Count, services);
            }
            await BroadcastServiceHealthUpdateAsync();
        }

        return sweptEndpoints.Count + sweptServices.Count;
    }

    /// <inheritdoc />
    public async Task SetEndpointEnabledAsync(string endpointId, bool enabled)
    {
        await _store.EnableHeartbeatOnEndpoint(endpointId, enabled);
        await BroadcastHeartbeatUpdateAsync();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ServiceHealth>> GetServiceHealthAsync()
    {
        var rows = await _serviceHealthStore.GetServiceHealth();
        if (rows.Exists(row => CoreConstants.ResolverId.Equals(row.ServiceId, StringComparison.OrdinalIgnoreCase)))
        {
            return rows;
        }

        // Nothing probed yet on a store seeded before this feature.
        rows.Add(new ServiceHealth { ServiceId = CoreConstants.ResolverId });
        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> ProbeResolverAsync()
    {
        // Deliberately not gated on settings.Enabled: that switch governs the
        // per-endpoint fan-out (N messages per interval). This is one message, and
        // an operator asking "is the Resolver up?" should not have to turn on
        // endpoint probing to find out.
        var settings = await _store.GetHeartbeatSettings();
        var intervalSeconds = Math.Max(settings.IntervalSeconds, MinimumIntervalSeconds);
        var dueBefore = DateTime.UtcNow.AddSeconds(-intervalSeconds);

        var messageId = Guid.NewGuid().ToString("N");
        if (!await _serviceHealthStore.TryClaimServiceProbe(CoreConstants.ResolverId, dueBefore, messageId))
        {
            return false;
        }

        var message = CreateHeartbeatMessage(
            CoreConstants.ResolverId,
            messageId,
            DateTime.UtcNow,
            ResolverProbeSessionId);
        await _sender.SendAsync(CoreConstants.ResolverId, message);

        LogSentResolverProbe(messageId);
        await BroadcastServiceHealthUpdateAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<int> SendHeartbeatsAsync(bool force = false)
    {
        var settings = await _store.GetHeartbeatSettings();
        if (!force && !settings.Enabled)
        {
            return 0;
        }

        // The fan-out is opt-OUT: it walks the compile-time catalog and skips only
        // an explicit IsHeartbeatEnabled == false. Driving it from the store's
        // opted-in query instead would silently skip every endpoint an operator
        // never touched — which, out of the box, is all of them.
        var probeable = _platform.Endpoints.Where(IsProbeable).ToList();
        var endpointIds = probeable.Select(endpoint => endpoint.Id).ToList();
        var metadataList = await _store.GetMetadatas(endpointIds) ?? new List<EndpointMetadata>();
        // An explicit opt-out wins over a duplicate that does not carry one: losing
        // it would start probing an endpoint an operator deliberately excluded.
        var metadataByEndpoint = IndexByEndpointId(
            metadataList,
            metadata => metadata.EndpointId,
            (winner, candidate) => winner.IsHeartbeatEnabled == false ? winner : candidate,
            "endpoint metadata");

        var sent = 0;
        foreach (var endpoint in probeable.OrderBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (metadataByEndpoint.TryGetValue(endpoint.Id, out var metadata) && metadata.IsHeartbeatEnabled == false)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            var messageId = Guid.NewGuid().ToString("N");
            var heartbeat = new StoreHeartbeat
            {
                MessageId = messageId,
                StartTime = now,
                ReceivedTime = now,
                EndTime = now,
                IntervalSeconds = Math.Max(settings.IntervalSeconds, MinimumIntervalSeconds),
                EndpointHeartbeatStatus = HeartbeatStatus.Pending,
            };

            // Pending row first: the answer can come back before the send call
            // returns, and it settles the row this write creates.
            await _store.SetHeartbeat(heartbeat, endpoint.Id);
            await _sender.SendAsync(endpoint.Id, CreateHeartbeatMessage(endpoint.Id, messageId, now, HeartbeatSessionId));
            sent++;
        }

        if (sent > 0)
        {
            LogSentHeartbeats(sent);
            await BroadcastHeartbeatUpdateAsync();
        }

        return sent;
    }

    /// <inheritdoc />
    public async Task<bool> RunScheduledTickAsync(CancellationToken cancellationToken = default)
    {
        // Sweep on every tick, independent of the claim and the Enabled flag:
        // timed-out probes must settle to Off even when scheduled sending is
        // disabled or between claims (e.g. after a manual "Send now").
        await SweepTimeoutsAsync();

        await FoldHeartbeatHistoryAsync();

        // Resolver liveness is deliberately outside the Enabled gate below.
        await ProbeResolverAsync();

        var settings = await _store.GetHeartbeatSettings();
        var intervalSeconds = Math.Max(settings.IntervalSeconds, MinimumIntervalSeconds);
        var dueBefore = DateTime.UtcNow.AddSeconds(-intervalSeconds);
        // The claim is where Enabled is enforced for the schedule, and where
        // scaled-out instances agree on exactly one sender per interval.
        if (!await _store.TryClaimHeartbeatSend(dueBefore))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await SendHeartbeatsAsync(force: true);
        return true;
    }

    private async Task FoldHeartbeatHistoryAsync()
    {
        if (_historyStore is null) return;

        try
        {
            var settings = await _store.GetHeartbeatSettings();
            var intervalSeconds = Math.Max(settings.IntervalSeconds, MinimumIntervalSeconds);
            var now = DateTime.UtcNow;
            if (!await _historyStore.TryClaimHeartbeatHistoryFold(now.AddSeconds(-intervalSeconds)))
            {
                return;
            }

            var historyStartUtc = now.AddDays(-HeartbeatHistoryRetentionDays);
            var endpointIds = _platform.Endpoints.Select(endpoint => endpoint.Id).ToList();
            var metadatas = await _store.GetMetadatas(endpointIds) ?? [];
            var metadataByEndpoint = IndexByEndpointId(
                metadatas,
                metadata => metadata.EndpointId,
                (winner, candidate) => winner.IsHeartbeatEnabled == false ? winner : candidate,
                "heartbeat history metadata");
            var existingDays = await _historyStore.GetHeartbeatUptimeDays(historyStartUtc.Date);
            var existingGaps = await _historyStore.GetHeartbeatGaps(historyStartUtc);
            var changedDays = new List<HeartbeatUptimeDay>();
            var changedGaps = new List<HeartbeatGap>();

            foreach (var endpoint in _platform.Endpoints)
            {
                if (!metadataByEndpoint.TryGetValue(endpoint.Id, out var metadata) ||
                    metadata.IsHeartbeatEnabled == false)
                {
                    continue;
                }

                var result = HeartbeatHistoryFolder.Fold(
                    endpoint.Id,
                    metadata.Heartbeats,
                    existingDays.Where(day => string.Equals(day.EndpointId, endpoint.Id, StringComparison.OrdinalIgnoreCase)),
                    existingGaps
                        .Where(gap => gap.ToUtc is null && string.Equals(gap.EndpointId, endpoint.Id, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(gap => gap.FromUtc)
                        .FirstOrDefault(),
                    historyStartUtc,
                    intervalSeconds);
                changedDays.AddRange(result.Days);
                changedGaps.AddRange(result.Gaps);
            }

            await _historyStore.UpsertHeartbeatGaps(changedGaps);
            await _historyStore.UpsertHeartbeatUptimeDays(changedDays);
            if (!_historyStore.PrunesHeartbeatHistoryAutomatically)
            {
                await _historyStore.PruneHeartbeatHistory(historyStartUtc);
            }
        }
        catch (Exception exception)
        {
            NimBusMeters.StoreOperationFailed.Add(
                1,
                new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreOperation, "heartbeat_history_fold"),
                new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, exception.GetType().FullName));
            LogHeartbeatHistoryFoldFailed(exception);
        }
    }

    /// <summary>
    /// Indexes <paramref name="items"/> by endpoint id, resolving duplicates with
    /// <paramref name="resolveConflict"/> instead of throwing.
    /// </summary>
    /// <remarks>
    /// Nothing enforces one metadata record per endpoint: the id is a stored field
    /// rather than a key, and Cosmos ids are case-sensitive while every lookup here
    /// is case-insensitive, so "OrderEndpoint" and "orderendpoint" are two records
    /// that collide on read. A plain <c>ToDictionary</c> throws ArgumentException
    /// and takes out the Platform services card and Heartbeat probing, "Send now"
    /// included, for a data condition that is merely untidy. Duplicates are logged
    /// so the stray records can be cleaned up.
    /// </remarks>
    private Dictionary<string, T> IndexByEndpointId<T>(
        IEnumerable<T> items,
        Func<T, string> endpointIdSelector,
        Func<T, T, T> resolveConflict,
        string what)
    {
        var indexed = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();

        foreach (var item in items ?? Enumerable.Empty<T>())
        {
            var endpointId = endpointIdSelector(item);
            if (string.IsNullOrWhiteSpace(endpointId)) continue;

            if (indexed.TryGetValue(endpointId, out var existing))
            {
                duplicates.Add(endpointId);
                indexed[endpointId] = resolveConflict(existing, item);
            }
            else
            {
                indexed[endpointId] = item;
            }
        }

        if (duplicates.Count > 0)
        {
            LogDuplicateEndpointRows(what, string.Join(", ", duplicates.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        return indexed;
    }

    private static Message CreateHeartbeatMessage(string endpointId, string messageId, DateTime forwardSendTime, string sessionId)
        => new()
        {
            From = CoreConstants.ManagerId,
            To = endpointId,
            EventId = Guid.NewGuid().ToString("N"),
            MessageId = messageId,
            CorrelationId = messageId,
            SessionId = sessionId,
            ParentMessageId = CoreConstants.Self,
            OriginatingMessageId = CoreConstants.Self,
            OriginatingFrom = CoreConstants.ManagerId,
            MessageType = MessageType.EventRequest,
            // On the message for routing rules, and in the content because that is
            // where the SDK's auto-answer reads it back from.
            EventTypeId = CoreHeartbeat.EventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = CoreHeartbeat.EventTypeId,
                    EventJson = JsonConvert.SerializeObject(new CoreHeartbeat
                    {
                        ForwardSendTime = forwardSendTime,
                    }),
                },
            },
        };

    // The hub has no endpoint groups, so both signals go to every connected
    // operator and the client re-reads the affected view.
    private Task BroadcastHeartbeatUpdateAsync()
        => _hubContext is null
            ? Task.CompletedTask
            : _hubContext.Clients.All.SendAsync(SignalNames.HeartbeatUpdate);

    private Task BroadcastServiceHealthUpdateAsync()
        => _hubContext is null
            ? Task.CompletedTask
            : _hubContext.Clients.All.SendAsync(SignalNames.ServiceHealthUpdate);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Marked timed-out heartbeats Off for {Count} endpoint(s): {Endpoints}")]
    private partial void LogSweptEndpoints(int count, string endpoints);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Marked timed-out liveness probes Off for {Count} service(s): {Services}")]
    private partial void LogSweptServices(int count, string services);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Sent liveness probe to the Resolver. MessageId:{MessageId}")]
    private partial void LogSentResolverProbe(string messageId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Sent heartbeat to {Count} endpoint(s).")]
    private partial void LogSentHeartbeats(int count);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Duplicate {What} rows for endpoint(s) {Endpoints} — endpoint ids must be unique (case-insensitively); using one row per endpoint and ignoring the rest.")]
    private partial void LogDuplicateEndpointRows(string what, string endpoints);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "Heartbeat history fold failed; retained probes will be retried on the next interval.")]
    private partial void LogHeartbeatHistoryFoldFailed(Exception exception);
}
