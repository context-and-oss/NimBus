using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <inheritdoc />
public sealed partial class MonitorAcknowledgementService : IMonitorAcknowledgementService
{
    /// <summary>How long an acknowledgement silences an endpoint.</summary>
    public static readonly TimeSpan AcknowledgementTtl = TimeSpan.FromHours(4);

    /// <summary>Longest accepted reason, in characters.</summary>
    public const int MaxReasonLength = 500;

    private readonly IEndpointAcknowledgementStore _store;
    private readonly IMessageTrackingStore _trackingStore;
    private readonly IStoreResultCache _storeResultCache;
    private readonly ILogger<MonitorAcknowledgementService> _logger;
    private readonly TimeProvider _timeProvider;

    public MonitorAcknowledgementService(
        IEndpointAcknowledgementStore store,
        IMessageTrackingStore trackingStore,
        IStoreResultCache storeResultCache,
        ILogger<MonitorAcknowledgementService> logger,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _trackingStore = trackingStore;
        _storeResultCache = storeResultCache;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<EndpointAcknowledgement>> GetActiveAsync()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var active = new List<EndpointAcknowledgement>();
        foreach (var acknowledgement in await _store.GetEndpointAcknowledgements())
        {
            if (now >= acknowledgement.ExpiresAtUtc || await HasRecoveredAsync(acknowledgement))
            {
                // Token-guarded: an operator may have re-acknowledged since the read.
                await _store.RemoveEndpointAcknowledgement(acknowledgement.EndpointId, acknowledgement.AcknowledgementId);
                continue;
            }

            active.Add(acknowledgement);
        }

        return active;
    }

    public async Task<EndpointAcknowledgement> AcknowledgeAsync(string endpointId, string? reason, string? acknowledgedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length > MaxReasonLength)
            throw new ArgumentException($"Reason must be at most {MaxReasonLength} characters.", nameof(reason));

        var counts = await _trackingStore.DownloadEndpointStateCount(endpointId);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var acknowledgement = new EndpointAcknowledgement
        {
            EndpointId = endpointId,
            AcknowledgementId = Guid.NewGuid().ToString("N"),
            Reason = trimmed,
            AcknowledgedBy = string.IsNullOrWhiteSpace(acknowledgedBy) ? null : acknowledgedBy,
            AcknowledgedAtUtc = now,
            ExpiresAtUtc = now + AcknowledgementTtl,
            FailedCountAtAcknowledgement = FailedTotal(counts),
        };
        await _store.SetEndpointAcknowledgement(acknowledgement);
        return acknowledgement;
    }

    public Task ClearAsync(string endpointId) => _store.RemoveEndpointAcknowledgement(endpointId);

    /// <summary>
    /// Recovered = acknowledged while failing, and a status count taken after the
    /// acknowledgement shows no failed, dead-lettered or unsupported messages. The count comes from
    /// the status API's short-lived cache; comparing its query time to the
    /// acknowledgement time keeps a pre-acknowledgement snapshot from clearing it.
    /// </summary>
    private async Task<bool> HasRecoveredAsync(EndpointAcknowledgement acknowledgement)
    {
        if (acknowledgement.FailedCountAtAcknowledgement <= 0) return false;

        try
        {
            var counts = await _storeResultCache.GetEndpointStateCountAsync(_trackingStore, acknowledgement.EndpointId);
            return counts.EventTime > acknowledgement.AcknowledgedAtUtc
                && FailedTotal(counts) == 0;
        }
#pragma warning disable CA1031 // One endpoint's unavailable storage must not fail the whole list.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCouldNotReadStatusForAcknowledged(_logger, ex, acknowledgement.EndpointId);
            return false;
        }
    }

    // Matches the status API's failedCount (Mapper.EndpointStatusCountFromEndpointStateCount),
    // which the Monitor compares this baseline with.
    private static int FailedTotal(EndpointStateCount counts)
        => counts.FailedCount + counts.DeadletterCount + counts.UnsupportedCount;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read status for acknowledged endpoint {EndpointId}; keeping its acknowledgement")]
    private static partial void LogCouldNotReadStatusForAcknowledged(ILogger logger, Exception exception, string endpointId);
}
