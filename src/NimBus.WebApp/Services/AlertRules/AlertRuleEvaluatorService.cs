namespace NimBus.WebApp.Services.AlertRules;

/// <summary>
/// Drives the alert rule evaluator (Spec 053): every <see cref="AlertRuleEvaluator.Interval"/> it
/// opens a scope and runs one tick. Scaled-out instances all tick; the store's lease lets one of
/// them evaluate per interval. Modelled on <c>HeartbeatBackgroundService</c>.
/// </summary>
public sealed partial class AlertRuleEvaluatorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertRuleEvaluatorService> _logger;

    /// <summary>Creates the scheduler.</summary>
    /// <param name="scopeFactory">Opens the per-tick scope the message store needs.</param>
    /// <param name="time">The clock the timer runs on.</param>
    /// <param name="logger">Diagnostics.</param>
    public AlertRuleEvaluatorService(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<AlertRuleEvaluatorService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AlertRuleEvaluator.Interval, _time);
        do
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one tick in its own scope. A failed tick is logged and swallowed: letting it escape
    /// would stop the scheduler for the lifetime of the process.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var evaluator = scope.ServiceProvider.GetRequiredService<AlertRuleEvaluator>();
            await evaluator.RunTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogTickFailed(exception);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Alert rule evaluator tick failed.")]
    private partial void LogTickFailed(Exception exception);
}
