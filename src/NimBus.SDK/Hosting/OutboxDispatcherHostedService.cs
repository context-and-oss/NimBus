using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Outbox;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Background service that polls the outbox and dispatches pending messages to Service Bus.
/// </summary>
public partial class OutboxDispatcherHostedService : BackgroundService
{
    private readonly OutboxDispatcher _dispatcher;
    private readonly TimeSpan _pollingInterval;
    private readonly int _batchSize;
    private readonly ILogger<OutboxDispatcherHostedService> _logger;

    public OutboxDispatcherHostedService(
        OutboxDispatcher dispatcher,
        TimeSpan pollingInterval,
        int batchSize,
        ILogger<OutboxDispatcherHostedService>? logger = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _pollingInterval = pollingInterval;
        _batchSize = batchSize;
        _logger = logger ?? NullLogger<OutboxDispatcherHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogOutboxDispatcherHostedServiceStarted(_logger, _pollingInterval, _batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await _dispatcher.DispatchPendingAsync(_batchSize, stoppingToken);

                // If we dispatched a full batch, immediately poll again (more may be waiting)
                if (dispatched >= _batchSize)
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Transient failures should not stop the dispatcher; log and continue.
                LogOutboxDispatcherPollFailed(_logger, ex, _pollingInterval);
            }

            await Task.Delay(_pollingInterval, stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "OutboxDispatcherHostedService started (polling interval {Interval}, batch size {BatchSize})")]
    private static partial void LogOutboxDispatcherHostedServiceStarted(ILogger logger, TimeSpan interval, int batchSize);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox dispatcher poll failed; will retry after {Interval}.")]
    private static partial void LogOutboxDispatcherPollFailed(ILogger logger, Exception exception, TimeSpan interval);
}
