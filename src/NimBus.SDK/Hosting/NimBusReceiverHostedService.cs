using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.CircuitBreaker;
using NimBus.ServiceBus;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Background service that receives messages from a Service Bus topic/subscription
/// using a <see cref="ServiceBusSessionProcessor"/> and delegates to <see cref="IServiceBusAdapter"/>.
/// </summary>
public partial class NimBusReceiverHostedService : BackgroundService
{
    private readonly ServiceBusClient _client;
    private readonly IServiceBusAdapter _adapter;
    private readonly NimBusReceiverOptions _options;
    private readonly ILogger<NimBusReceiverHostedService> _logger;
    private readonly IEndpointCircuitBreaker? _circuitBreaker;
    private readonly object _errorGate = new object();
    private ServiceBusSessionProcessor? _processor;
    private readonly TaskCompletionSource<bool> _startupCompletion =
        new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<ProcessorRecoveryRequest> _processorRecoveryRequested =
        new TaskCompletionSource<ProcessorRecoveryRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _recoverableErrorCount;
    private DateTimeOffset _firstRecoverableErrorAt;

    public NimBusReceiverHostedService(
        ServiceBusClient client,
        IServiceBusAdapter adapter,
        NimBusReceiverOptions options,
        ILogger<NimBusReceiverHostedService> logger,
        IEndpointCircuitBreaker? circuitBreaker = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _circuitBreaker = circuitBreaker;
        ValidateOptions(_options);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        await _startupCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        RunProcessorLoopAsync(stoppingToken);

    internal async Task RunProcessorLoopAsync(CancellationToken stoppingToken)
    {
        var restartCount = 0;

        // One long-lived stopping sentinel for the whole loop. A
        // per-iteration Task.Delay leaked one CancellationTokenRegistration
        // per circuit transition / infrastructure restart for the host's
        // lifetime — routine traffic once circuit-driven iterations exist.
        var stopping = Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Arm the state-change wait BEFORE sampling State: the wait
            // snapshots the current signal under the breaker's lock, so a
            // transition firing between these two statements completes the
            // armed task instead of being lost — unobserved, a missed
            // Closed->Open ran the processor at full concurrency through
            // the entire break, and a missed HalfOpen->Closed stranded the
            // receiver at probe concurrency until the next open.
            //
            // A wait that has already completed when State is sampled holds a
            // transition the sample reflects (or one that fired right after
            // it). Arming performs a due Open->HalfOpen advance itself, so
            // this is the normal case when stopping the processor outlasted
            // the break. Acting on that transition later would stop the
            // processor about to be started for the very state it led to,
            // cancelling whatever it had received. Arm and sample again until
            // the wait is pending: every later transition then completes it.
            using var circuitWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task<CircuitStateChange>? circuitStateChanged = null;
            var circuitState = CircuitState.Closed;
            if (_circuitBreaker is not null)
            {
                do
                {
                    circuitStateChanged = _circuitBreaker.WaitForStateChangeAsync(circuitWaitCancellation.Token);
                    circuitState = _circuitBreaker.State;
                }
                while (circuitStateChanged.IsCompletedSuccessfully);
            }

            if (circuitState == CircuitState.Open)
            {
                _startupCompletion.TrySetResult(true);
                try
                {
                    await circuitStateChanged!.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            ResetRecoverableErrors();
            ResetRecoverySignal();

            var maxConcurrentSessions = circuitState == CircuitState.HalfOpen
                ? 1
                : _options.MaxConcurrentSessions;
            _processor = CreateProcessor(maxConcurrentSessions);
            var infrastructureRestart = false;

            try
            {
                LogStartingNimBusReceiver(_logger, _options.TopicName, _options.SubscriptionName, maxConcurrentSessions, _options.PrefetchCount);

                await _processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);
                _startupCompletion.TrySetResult(true);

                var completed = circuitStateChanged is null
                    ? await Task.WhenAny(_processorRecoveryRequested.Task, stopping).ConfigureAwait(false)
                    : await Task.WhenAny(_processorRecoveryRequested.Task, circuitStateChanged, stopping).ConfigureAwait(false);

                if (completed == stopping)
                {
                    break;
                }

                if (circuitStateChanged is not null && completed == circuitStateChanged)
                {
                    var change = await circuitStateChanged.ConfigureAwait(false);
                    if (change.To == CircuitState.Open)
                    {
                        LogPausingNimBusReceiver(_logger, _options.TopicName, _options.SubscriptionName, change.Endpoint, change.Reason);
                    }
                    else
                    {
                        LogReconfiguringNimBusReceiver(_logger, _options.TopicName, _options.SubscriptionName, change.Endpoint, change.From, change.To);
                    }

                    continue;
                }

                var recovery = await _processorRecoveryRequested.Task.ConfigureAwait(false);
                infrastructureRestart = true;
                restartCount++;

                LogRestartingNimBusReceiverForAfterConsecutive(_logger, recovery.Exception, _options.TopicName, _options.SubscriptionName, recovery.ErrorCount, restartCount, recovery.ErrorSource);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _startupCompletion.TrySetCanceled(stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                _startupCompletion.TrySetException(ex);
                throw;
            }
            finally
            {
                await circuitWaitCancellation.CancelAsync().ConfigureAwait(false);
                await StopAndDisposeProcessorForLoopAsync(stoppingToken).ConfigureAwait(false);
            }

            if (infrastructureRestart
                && !stoppingToken.IsCancellationRequested
                && _options.ProcessorRestartDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.ProcessorRestartDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        LogStoppingNimBusReceiver(_logger, _options.TopicName, _options.SubscriptionName);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await StopAndDisposeProcessorAsync(cancellationToken).ConfigureAwait(false);
    }

    private ServiceBusSessionProcessor CreateProcessor(int maxConcurrentSessions)
    {
        var processorOptions = new ServiceBusSessionProcessorOptions
        {
            MaxConcurrentSessions = maxConcurrentSessions,
            MaxAutoLockRenewalDuration = _options.MaxAutoLockRenewalDuration,
            SessionIdleTimeout = _options.SessionIdleTimeout,
            PrefetchCount = _options.PrefetchCount,
            AutoCompleteMessages = false,
        };

        var processor = _client.CreateSessionProcessor(
            _options.TopicName,
            _options.SubscriptionName,
            processorOptions);

        processor.ProcessMessageAsync += OnMessageAsync;
        processor.ProcessErrorAsync += OnErrorAsync;

        return processor;
    }

    internal async Task StopAndDisposeProcessorAsync(CancellationToken cancellationToken)
    {
        var processor = Interlocked.Exchange(ref _processor, null);
        if (processor == null)
        {
            return;
        }

        OperationCanceledException? cancellationException = null;
        Task? stopTask = null;
        try
        {
            try
            {
                stopTask = processor.StopProcessingAsync(cancellationToken);
                await stopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                cancellationException = ex;
                if (stopTask is not null)
                {
                    _ = ObserveProcessorStopAsync(stopTask);
                }
            }
            catch (Exception ex)
            {
                LogErrorWhileStoppingNimBusReceiver(_logger, ex, _options.TopicName, _options.SubscriptionName);
            }
        }
        finally
        {
            Task? disposeTask = null;
            try
            {
                disposeTask = DisposeProcessorAsync(processor).AsTask();
                await disposeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                cancellationException ??= ex;
                if (disposeTask is not null)
                {
                    _ = ObserveProcessorDisposalAsync(disposeTask);
                }
            }
            catch (Exception ex) when (cancellationException is not null)
            {
                // Preserve cooperative shutdown as the primary outcome even if
                // best-effort cleanup also fails. The cleanup failure remains
                // observable without replacing the cancellation exception.
                LogErrorWhileDisposingNimBusReceiver(_logger, ex, _options.TopicName, _options.SubscriptionName);
            }
            catch (Exception ex)
            {
                LogErrorWhileDisposingNimBusReceiverFor2(_logger, ex, _options.TopicName, _options.SubscriptionName);
            }
        }

        if (cancellationException is not null)
        {
            ExceptionDispatchInfo.Capture(cancellationException).Throw();
        }
    }

    private async Task StopAndDisposeProcessorForLoopAsync(CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StopAndDisposeProcessorAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // The loop has already observed host shutdown. Cleanup was
                // initiated and any incomplete task is observed in the background.
            }

            return;
        }

        using var cleanupCancellation = new CancellationTokenSource(_options.ProcessorShutdownTimeout);
        try
        {
            await StopAndDisposeProcessorAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cleanupCancellation.IsCancellationRequested)
        {
            LogTimedOutAfterWhileStopping(_logger, _options.ProcessorShutdownTimeout, _options.TopicName, _options.SubscriptionName);
        }
    }

    private async Task ObserveProcessorStopAsync(Task stopTask)
    {
        try
        {
            await stopTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogNimBusReceiverStopCompleted(_logger, ex, _options.TopicName, _options.SubscriptionName);
        }
    }

    private async Task ObserveProcessorDisposalAsync(Task disposeTask)
    {
        try
        {
            await disposeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogNimBusReceiverDisposalCompleted(_logger, ex, _options.TopicName, _options.SubscriptionName);
        }
    }

    protected virtual ValueTask DisposeProcessorAsync(ServiceBusSessionProcessor processor) =>
        processor.DisposeAsync();

    private async Task OnMessageAsync(ProcessSessionMessageEventArgs args)
    {
        await _adapter.Handle(args, args.CancellationToken).ConfigureAwait(false);
        ResetRecoverableErrors();
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args) =>
        HandleProcessorErrorAsync(args);

    internal async Task HandleProcessorErrorAsync(ProcessErrorEventArgs args)
    {
        LogErrorProcessingMessage(_logger, args.Exception, args.ErrorSource, args.FullyQualifiedNamespace, args.EntityPath);

        if (!IsRecoverableProcessorInfrastructureError(args))
        {
            return;
        }

        var state = RecordRecoverableError(args);

        if (state.ShouldRestart)
        {
            _processorRecoveryRequested.TrySetResult(new ProcessorRecoveryRequest(
                args.Exception,
                args.ErrorSource,
                state.ErrorCount));
        }

        if (state.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(state.Delay, args.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The processor is stopping or restarting.
            }
        }
    }

    private RecoverableErrorState RecordRecoverableError(ProcessErrorEventArgs args)
    {
        lock (_errorGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_recoverableErrorCount == 0 || now - _firstRecoverableErrorAt > _options.RecoverableErrorWindow)
            {
                _firstRecoverableErrorAt = now;
                _recoverableErrorCount = 0;
            }

            _recoverableErrorCount++;

            return new RecoverableErrorState(
                _recoverableErrorCount,
                CalculateRecoverableErrorDelay(_recoverableErrorCount),
                _recoverableErrorCount >= _options.RecoverableErrorRestartThreshold);
        }
    }

    private TimeSpan CalculateRecoverableErrorDelay(int errorCount)
    {
        if (_options.RecoverableErrorDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Min(errorCount - 1, 5));
        var delayTicks = _options.RecoverableErrorDelay.Ticks * multiplier;
        if (delayTicks >= _options.MaxRecoverableErrorDelay.Ticks)
        {
            return _options.MaxRecoverableErrorDelay;
        }

        return TimeSpan.FromTicks((long)delayTicks);
    }

    private void ResetRecoverableErrors()
    {
        lock (_errorGate)
        {
            _recoverableErrorCount = 0;
            _firstRecoverableErrorAt = default;
        }
    }

    private void ResetRecoverySignal()
    {
        _processorRecoveryRequested = new TaskCompletionSource<ProcessorRecoveryRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsRecoverableProcessorInfrastructureError(ProcessErrorEventArgs args)
    {
        if (args.ErrorSource == ServiceBusErrorSource.ProcessMessageCallback)
        {
            return false;
        }

        if (args.Exception is ObjectDisposedException)
        {
            return true;
        }

        if (args.Exception is ServiceBusException serviceBusException)
        {
            return serviceBusException.Reason == ServiceBusFailureReason.ServiceCommunicationProblem
                || serviceBusException.Reason == ServiceBusFailureReason.ServiceTimeout
                || serviceBusException.Reason == ServiceBusFailureReason.ServiceBusy
                || serviceBusException.Reason == ServiceBusFailureReason.GeneralError;
        }

        return ContainsException<SocketException>(args.Exception)
            || ContainsException<WebSocketException>(args.Exception)
            || ContainsException<IOException>(args.Exception);
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }

    [SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly", Justification = "ParamName names the invalid option property, which callers and tests rely on.")]
    private static void ValidateOptions(NimBusReceiverOptions options)
    {
        if (options.MaxConcurrentSessions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrentSessions), "MaxConcurrentSessions must be greater than zero.");
        }

        if (options.MaxAutoLockRenewalDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxAutoLockRenewalDuration), "MaxAutoLockRenewalDuration cannot be negative.");
        }

        if (options.SessionIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SessionIdleTimeout), "SessionIdleTimeout must be greater than zero.");
        }

        if (options.PrefetchCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PrefetchCount), "PrefetchCount cannot be negative.");
        }

        if (options.RecoverableErrorRestartThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecoverableErrorRestartThreshold), "RecoverableErrorRestartThreshold must be greater than zero.");
        }

        if (options.RecoverableErrorWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecoverableErrorWindow), "RecoverableErrorWindow must be greater than zero.");
        }

        if (options.MaxRecoverableErrorDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxRecoverableErrorDelay), "MaxRecoverableErrorDelay cannot be negative.");
        }

        if (options.RecoverableErrorDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RecoverableErrorDelay), "RecoverableErrorDelay cannot be negative.");
        }

        if (options.ProcessorRestartDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ProcessorRestartDelay), "ProcessorRestartDelay cannot be negative.");
        }

        if (options.ProcessorShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ProcessorShutdownTimeout), "ProcessorShutdownTimeout must be greater than zero.");
        }
    }

    private readonly struct RecoverableErrorState
    {
        public RecoverableErrorState(int errorCount, TimeSpan delay, bool shouldRestart)
        {
            ErrorCount = errorCount;
            Delay = delay;
            ShouldRestart = shouldRestart;
        }

        public int ErrorCount { get; }
        public TimeSpan Delay { get; }
        public bool ShouldRestart { get; }
    }

    private readonly struct ProcessorRecoveryRequest
    {
        public ProcessorRecoveryRequest(Exception exception, ServiceBusErrorSource errorSource, int errorCount)
        {
            Exception = exception;
            ErrorSource = errorSource;
            ErrorCount = errorCount;
        }

        public Exception Exception { get; }
        public ServiceBusErrorSource ErrorSource { get; }
        public int ErrorCount { get; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting NimBus receiver for {Topic}/{Subscription} (MaxConcurrentSessions={MaxSessions}, PrefetchCount={PrefetchCount})")]
    private static partial void LogStartingNimBusReceiver(ILogger logger, string topic, string subscription, int maxSessions, int prefetchCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pausing NimBus receiver for {Topic}/{Subscription}; endpoint circuit {Endpoint} opened. Reason: {Reason}")]
    private static partial void LogPausingNimBusReceiver(ILogger logger, string topic, string subscription, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconfiguring NimBus receiver for {Topic}/{Subscription}; endpoint circuit {Endpoint} changed from {FromState} to {ToState}")]
    private static partial void LogReconfiguringNimBusReceiver(ILogger logger, string topic, string subscription, string endpoint, CircuitState fromState, CircuitState toState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restarting NimBus receiver for {Topic}/{Subscription} after {ErrorCount} consecutive recoverable processor errors. RestartCount={RestartCount}, LastErrorSource={ErrorSource}")]
    private static partial void LogRestartingNimBusReceiverForAfterConsecutive(ILogger logger, Exception exception, string topic, string subscription, int errorCount, int restartCount, ServiceBusErrorSource errorSource);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping NimBus receiver for {Topic}/{Subscription}")]
    private static partial void LogStoppingNimBusReceiver(ILogger logger, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error while stopping NimBus receiver for {Topic}/{Subscription}")]
    private static partial void LogErrorWhileStoppingNimBusReceiver(ILogger logger, Exception exception, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error while disposing NimBus receiver for {Topic}/{Subscription} after cancelled shutdown")]
    private static partial void LogErrorWhileDisposingNimBusReceiver(ILogger logger, Exception exception, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error while disposing NimBus receiver for {Topic}/{Subscription}")]
    private static partial void LogErrorWhileDisposingNimBusReceiverFor2(ILogger logger, Exception exception, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timed out after {Timeout} while stopping or disposing NimBus receiver for {Topic}/{Subscription}; continuing processor recovery")]
    private static partial void LogTimedOutAfterWhileStopping(ILogger logger, TimeSpan timeout, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NimBus receiver stop completed with an error after recovery stopped waiting for {Topic}/{Subscription}")]
    private static partial void LogNimBusReceiverStopCompleted(ILogger logger, Exception exception, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NimBus receiver disposal completed with an error after shutdown stopped waiting for {Topic}/{Subscription}")]
    private static partial void LogNimBusReceiverDisposalCompleted(ILogger logger, Exception exception, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing message. Source: {ErrorSource}, Namespace: {Namespace}, EntityPath: {EntityPath}")]
    private static partial void LogErrorProcessingMessage(ILogger logger, Exception exception, ServiceBusErrorSource errorSource, string @namespace, string entityPath);
}
