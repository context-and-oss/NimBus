using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Services.Operations;

/// <summary>
/// Visits non-terminal journaled operations (Spec 044 Phase 2): repairs commands a crashed
/// request left behind, confirms dispatch from the Resolver's history, records outcomes and
/// purges expired operations. Every write is optimistic, so several WebApp instances can run it
/// at once. It never republishes a command.
/// </summary>
public sealed partial class OperatorOperationReconciler : BackgroundService
{
    /// <summary>How often due operations are visited.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>The most operations visited per pass.</summary>
    public const int BatchSize = 50;

    private const int PurgeEveryPasses = 20;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OperatorOperationReconciler> _logger;
    private readonly TimeProvider _time;

    /// <summary>Creates the reconciler.</summary>
    public OperatorOperationReconciler(IServiceScopeFactory scopes, ILogger<OperatorOperationReconciler> logger, TimeProvider? time = null)
    {
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pass = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(purge: pass % PurgeEveryPasses == 0).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogPassFailed(_logger, ex);
            }

            pass++;
            try
            {
                await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass: reconcile up to <see cref="BatchSize"/> due operations, then optionally purge.</summary>
    /// <param name="purge">Whether to purge expired operations too.</param>
    /// <returns>The number of operations visited.</returns>
    public async Task<int> RunOnceAsync(bool purge = false)
    {
        using var scope = _scopes.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<OperatorOperationJournal>();
        if (!journal.IsAvailable)
            return 0;

        var audit = scope.ServiceProvider.GetRequiredService<IAuditLogService>();
        var due = await journal.ListDueAsync(BatchSize).ConfigureAwait(false);
        foreach (var operation in due)
        {
            try
            {
                await journal.ReconcileAsync(operation, repair: true, audit).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogOperationFailed(_logger, ex, operation.OperationId);
            }
        }

        if (purge)
            await journal.PurgeAsync(500).ConfigureAwait(false);

        return due.Count;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An operation reconciler pass failed")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciling operation {OperationId} failed")]
    private static partial void LogOperationFailed(ILogger logger, Exception exception, string operationId);
}
