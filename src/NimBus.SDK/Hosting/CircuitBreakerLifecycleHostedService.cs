using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;

namespace NimBus.SDK.Hosting;

/// <summary>Dispatches synchronous circuit transitions to asynchronous lifecycle observers.</summary>
internal sealed partial class CircuitBreakerLifecycleHostedService : BackgroundService
{
    private readonly IEndpointCircuitBreaker _circuitBreaker;
    private readonly MessageLifecycleNotifier _notifier;
    private readonly ILogger<CircuitBreakerLifecycleHostedService> _logger;
    private readonly Channel<CircuitStateChange> _changes = Channel.CreateUnbounded<CircuitStateChange>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public CircuitBreakerLifecycleHostedService(
        IEndpointCircuitBreaker circuitBreaker,
        MessageLifecycleNotifier notifier,
        ILogger<CircuitBreakerLifecycleHostedService> logger)
    {
        _circuitBreaker = circuitBreaker ?? throw new ArgumentNullException(nameof(circuitBreaker));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _circuitBreaker.StateChanged += OnStateChanged;
    }

    public override void Dispose()
    {
        _circuitBreaker.StateChanged -= OnStateChanged;
        _changes.Writer.TryComplete();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _notifier.NotifyCircuitStateChanged(change, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogCircuitStateLifecycleObserverFailed(_logger, exception, change.Endpoint, change.From, change.To);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected host shutdown.
        }
    }

    private void OnStateChanged(CircuitStateChange change) => _changes.Writer.TryWrite(change);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Circuit state lifecycle observer failed for endpoint {Endpoint} transition {FromState}->{ToState}")]
    private static partial void LogCircuitStateLifecycleObserverFailed(ILogger logger, Exception exception, string endpoint, CircuitState fromState, CircuitState toState);
}
