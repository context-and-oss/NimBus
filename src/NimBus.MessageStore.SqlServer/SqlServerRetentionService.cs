using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Core.Diagnostics;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Deletes SQL Server rows whose retention period ran out (Spec 050 §5.4), once per
/// <see cref="SqlServerMessageStoreOptions.RetentionInterval"/>. The periods come from
/// <see cref="RetentionPolicy"/>, read at the start of every run; with every period unlimited (the
/// SQL Server default in 4.x) a run deletes nothing. The first run starts once the schema
/// initializer has finished. A failed run is logged and the next run still happens. Registered
/// only by <see cref="SqlServerMessageStoreBuilderExtensions.AddSqlServerMessageStoreRetention"/>,
/// which the WebApp calls, so a deployment deletes in one place.
/// </summary>
internal sealed partial class SqlServerRetentionService : BackgroundService
{
    /// <summary>Rows one statement deletes at most, per period.</summary>
    internal const int BatchSize = 5000;

    private readonly SqlServerMessageStoreOptions _options;
    private readonly SqlServerSchemaReadiness _readiness;
    private readonly IMessageTrackingStore _store;
    private readonly RetentionPolicy _policy;
    private readonly TimeProvider _time;
    private readonly ILogger<SqlServerRetentionService> _logger;

    public SqlServerRetentionService(
        IOptions<SqlServerMessageStoreOptions> options,
        SqlServerSchemaReadiness readiness,
        IMessageTrackingStore store,
        RetentionPolicy policy,
        ILogger<SqlServerRetentionService> logger)
        : this(options, readiness, store, policy, logger, TimeProvider.System)
    {
    }

    internal SqlServerRetentionService(
        IOptions<SqlServerMessageStoreOptions> options,
        SqlServerSchemaReadiness readiness,
        IMessageTrackingStore store,
        RetentionPolicy policy,
        ILogger<SqlServerRetentionService> logger,
        TimeProvider time)
    {
        _options = options.Value;
        _readiness = readiness;
        _store = store;
        _policy = policy;
        _logger = logger;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.RetentionInterval, _time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed run must never stop the host or the later runs.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogRunFailed(_logger, ex, _options.RetentionInterval);
            }
        }
        while (await WaitForNextRun(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Runs the cleanup once with the current periods and returns what it deleted.</summary>
    internal async Task<RetentionPurgeResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var periods = _policy.Current;
        if (periods.IsAllUnlimited)
        {
            LogNothingExpires(_logger);
            return RetentionPurgeResult.None;
        }

        var started = _time.GetTimestamp();
        var deleted = await PurgeAsync(_store, periods, _time.GetUtcNow().UtcDateTime, BatchSize, cancellationToken).ConfigureAwait(false);

        Record("open_events", deleted.OpenEvents);
        Record("resolved_events", deleted.ResolvedEvents);
        Record("message_history", deleted.MessageHistory);
        Record("audit_trail", deleted.AuditTrail);
        LogRunCompleted(_logger, (long)_time.GetElapsedTime(started).TotalMilliseconds,
            deleted.OpenEvents, deleted.ResolvedEvents, deleted.MessageHistory, deleted.AuditTrail);
        return deleted;
    }

    /// <summary>
    /// Calls <see cref="IMessageTrackingStore.PurgeExpiredData"/> until every period's batch comes
    /// back short. A period that came back short is not asked again, so the run ends once each
    /// period has nothing older than its cutoff left.
    /// </summary>
    internal static async Task<RetentionPurgeResult> PurgeAsync(
        IMessageTrackingStore store,
        RetentionPeriods periods,
        DateTime nowUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var total = RetentionPurgeResult.None;
        var remaining = periods;
        while (!remaining.IsAllUnlimited)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await store.PurgeExpiredData(remaining, nowUtc, batchSize, cancellationToken).ConfigureAwait(false);
            total = total.Add(batch);
            remaining = new RetentionPeriods(
                batch.OpenEvents < batchSize ? RetentionPeriods.Unlimited : remaining.OpenEventsDays,
                batch.ResolvedEvents < batchSize ? RetentionPeriods.Unlimited : remaining.ResolvedEventsDays,
                batch.MessageHistory < batchSize ? RetentionPeriods.Unlimited : remaining.MessageHistoryDays,
                batch.AuditTrail < batchSize ? RetentionPeriods.Unlimited : remaining.AuditTrailDays);
        }

        return total;
    }

    private static void Record(string period, int deleted)
    {
        if (deleted > 0)
            NimBusMeters.RetentionDeleted.Add(deleted, new KeyValuePair<string, object?>("nimbus.retention.period", period));
    }

    private static async Task<bool> WaitForNextRun(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "SQL Server retention run completed in {DurationMs} ms: deleted {OpenEvents} open events, {ResolvedEvents} resolved events, {MessageHistory} history messages and {AuditTrail} audit entries.")]
    private static partial void LogRunCompleted(ILogger logger, long durationMs, int openEvents, int resolvedEvents, int messageHistory, int auditTrail);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "SQL Server retention: every retention period is unlimited, so nothing expires.")]
    private static partial void LogNothingExpires(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "SQL Server retention run failed; the next run is in {RetentionInterval}.")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, TimeSpan retentionInterval);
}
