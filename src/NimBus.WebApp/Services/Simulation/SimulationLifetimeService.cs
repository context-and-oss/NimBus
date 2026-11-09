using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>
/// Drives the simulation's auto-stop and stops a run when the WebApp shuts down. The shutdown
/// stop skips the drain and is capped by the host's shutdown token, so it cannot hold up the
/// WebApp's own shutdown (plan Decision 10).
/// </summary>
public sealed partial class SimulationLifetimeService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly ISimulationService _simulation;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SimulationLifetimeService> _logger;

    /// <summary>Creates the service.</summary>
    public SimulationLifetimeService(ISimulationService simulation, TimeProvider timeProvider, ILogger<SimulationLifetimeService> logger)
    {
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _simulation.CheckAutoStopAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogSimulationAutoStopCheckFailed(_logger, ex);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _simulation.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogStoppingTheSimulationDuringShutdownDid(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulation auto-stop check failed.")]
    private static partial void LogSimulationAutoStopCheckFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping the simulation during shutdown did not complete cleanly.")]
    private static partial void LogStoppingTheSimulationDuringShutdownDid(ILogger logger, Exception exception);
}
