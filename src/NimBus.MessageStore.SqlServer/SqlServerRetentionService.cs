using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Hosted service that deletes old SQL Server rows once per
/// <see cref="SqlServerMessageStoreOptions.RetentionInterval"/> (GH#291). The first run starts
/// as soon as the schema initializer has finished. A failing run is logged and the next run
/// still happens; shutdown cancels a run in progress. Registered only by
/// <see cref="SqlServerMessageStoreBuilderExtensions.AddSqlServerMessageStoreRetention"/>.
/// </summary>
internal sealed class SqlServerRetentionService : BackgroundService
{
    private readonly SqlServerMessageStoreOptions _options;
    private readonly SqlServerSchemaReadiness _readiness;
    private readonly ILogger<SqlServerRetentionService> _logger;

    public SqlServerRetentionService(
        IOptions<SqlServerMessageStoreOptions> options,
        SqlServerSchemaReadiness readiness,
        ILogger<SqlServerRetentionService> logger)
    {
        _options = options.Value;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.RetentionEnabled)
        {
            _logger.LogInformation("SQL Server message-store retention is disabled (RetentionEnabled = false); no rows will be deleted.");
            return;
        }

        try
        {
            await _readiness.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var runner = new SqlServerRetentionRunner(_options);
        using var timer = new PeriodicTimer(_options.RetentionInterval);
        do
        {
            try
            {
                var result = await runner.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "SQL Server message-store retention run completed in {DurationMs} ms: deleted {MessagesDeleted} Messages, {AuditsDeleted} MessageAudits, {ArchivedTrackingDeleted} archived/removed UnresolvedEvents and {UnresolvedTrackingDeleted} non-archived UnresolvedEvents.",
                    (long)result.Duration.TotalMilliseconds,
                    result.MessagesDeleted,
                    result.AuditsDeleted,
                    result.ArchivedTrackingDeleted,
                    result.UnresolvedTrackingDeleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed run must never stop the host or later runs.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "SQL Server message-store retention run failed; the next run is in {RetentionInterval}.", _options.RetentionInterval);
            }
        }
        while (await WaitForNextRun(timer, stoppingToken).ConfigureAwait(false));
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
}
