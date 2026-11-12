using System.Collections.Concurrent;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services.DashboardTokens;

/// <summary>Records when dashboard tokens are used, for the Settings page.</summary>
public interface IDashboardTokenUsageRecorder
{
    /// <summary>Records a use of the token. Never throws.</summary>
    Task RecordAsync(string tokenId);
}

/// <summary>
/// Writes a token's last use at most once per <see cref="Interval"/> per instance, so a wall
/// polling every few seconds costs one store write every few minutes. Best effort: a failed write
/// is logged and only leaves "last used" stale.
/// </summary>
public sealed partial class DashboardTokenUsageRecorder : IDashboardTokenUsageRecorder
{
    /// <summary>Minimum time between two writes for the same token.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IDashboardTokenStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger<DashboardTokenUsageRecorder> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);

    /// <summary>Creates the recorder.</summary>
    public DashboardTokenUsageRecorder(TimeProvider time, ILogger<DashboardTokenUsageRecorder> logger, IDashboardTokenStore? store = null)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(string tokenId)
    {
        if (_store is null)
            return;

        var now = _time.GetUtcNow();
        if (_lastWritten.TryGetValue(tokenId, out var last) && now - last < Interval)
            return;
        _lastWritten[tokenId] = now;

        try
        {
            await _store.RecordDashboardTokenUse(tokenId, now.UtcDateTime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRecordFailed(_logger, ex, tokenId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording the use of dashboard token {TokenId} failed")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string tokenId);
}
