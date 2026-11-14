using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Worker-side host for the deferred-processor trigger. Wraps a non-session
/// <see cref="ServiceBusProcessor"/> on the <c>deferredprocessor</c>
/// subscription and delegates each trigger to
/// <see cref="DeferredMessageDispatcher"/>. Registered by
/// <c>AddNimBusDeferredProcessorHostedService</c> via <c>TryAddEnumerable</c>;
/// Azure Functions hosts that own the trigger through a
/// <c>[ServiceBusTrigger]</c> function class do not register it.
///
/// <para>Hosts that run several endpoints in one process (for example the
/// WebApp's traffic simulator) can construct it directly, one instance per
/// endpoint, and drive <see cref="BackgroundService.StartAsync"/> /
/// <see cref="BackgroundService.StopAsync"/> themselves. Keep
/// <see cref="DeferredMessageProcessorHostedServiceOptions.MaxConcurrentCalls"/>
/// at 1 unless out-of-order replay is acceptable: the trigger subscription is
/// non-session, so serial processing is its only ordering mechanism.</para>
/// </summary>
public sealed partial class DeferredMessageProcessorHostedService : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IDeferredMessageProcessor _deferredMessageProcessor;
    private readonly DeferredMessageProcessorHostedServiceOptions _options;
    private readonly ILogger<DeferredMessageProcessorHostedService> _logger;

    /// <summary>
    /// Creates the hosted service.
    /// </summary>
    /// <param name="serviceBusClient">Client used to create the trigger processor.</param>
    /// <param name="deferredMessageProcessor">Processor that replays the deferred session.</param>
    /// <param name="options">Topic, trigger subscription and concurrency.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><c>TopicName</c> or <c>SubscriptionName</c> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>MaxConcurrentCalls</c> is less than 1.</exception>
    public DeferredMessageProcessorHostedService(
        ServiceBusClient serviceBusClient,
        IDeferredMessageProcessor deferredMessageProcessor,
        DeferredMessageProcessorHostedServiceOptions options,
        ILogger<DeferredMessageProcessorHostedService> logger)
    {
        _serviceBusClient = serviceBusClient ?? throw new ArgumentNullException(nameof(serviceBusClient));
        _deferredMessageProcessor = deferredMessageProcessor ?? throw new ArgumentNullException(nameof(deferredMessageProcessor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(options.TopicName))
            throw new ArgumentException("TopicName must be specified.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.SubscriptionName))
            throw new ArgumentException("SubscriptionName must be specified.", nameof(options));
        if (options.MaxConcurrentCalls < 1)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrentCalls, "MaxConcurrentCalls must be at least 1.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogDeferredProcessorHostedServiceEnabledOn(_logger, _options.TopicName, _options.SubscriptionName, _options.MaxConcurrentCalls);

        var processor = _serviceBusClient.CreateProcessor(
            _options.TopicName,
            _options.SubscriptionName,
            new ServiceBusProcessorOptions
            {
                // Default 1: the trigger subscription is non-session, so
                // serial processing is the only ordering mechanism. See
                // DeferredMessageProcessorHostedServiceOptions.MaxConcurrentCalls.
                MaxConcurrentCalls = _options.MaxConcurrentCalls,
                AutoCompleteMessages = false,
            });

        processor.ProcessMessageAsync += args => OnMessageAsync(args, stoppingToken);
        processor.ProcessErrorAsync += OnErrorAsync;

        await processor.StartProcessingAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        finally
        {
            // Use CancellationToken.None so the stop completes cleanly even when
            // stoppingToken is already cancelled.
            await processor.StopProcessingAsync(CancellationToken.None).ConfigureAwait(false);
            await processor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args, CancellationToken stoppingToken)
    {
        try
        {
            var outcome = await DeferredMessageDispatcher.ProcessAsync(
                args.Message, _deferredMessageProcessor, _options.TopicName, args.CancellationToken)
                .ConfigureAwait(false);

            if (outcome.Action == DeferredMessageDispatchAction.DeadLetter)
            {
                await args.DeadLetterMessageAsync(args.Message, outcome.DeadLetterReason, cancellationToken: args.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await args.CompleteMessageAsync(args.Message, args.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down. Do not abandon — let the lock expire
            // naturally and exit the message pump. Abandoning here would
            // treat shutdown as a failure and trigger redelivery on the
            // next host start.
        }
        catch (Exception ex)
        {
            LogFailedToProcessDeferredProcessorTrigger(_logger, ex, _options.TopicName, _options.SubscriptionName, args.Message.SessionId);

            // CancellationToken.None: stoppingToken may already be cancelled
            // mid-shutdown if a real error tripped us. We still want the
            // abandon to complete so redelivery is correct.
            try
            {
                await args.AbandonMessageAsync(args.Message, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception abandonEx)
            {
                LogFailedToAbandonDeferredProcessorTrigger(_logger, abandonEx, _options.TopicName, _options.SubscriptionName);
            }
        }
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        LogDeferredProcessorServiceBusError(_logger, args.Exception, _options.TopicName, _options.SubscriptionName, args.ErrorSource);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deferred-processor hosted service enabled on topic '{Topic}' (subscription '{Subscription}', MaxConcurrentCalls {MaxConcurrentCalls}). " +
            "Set NimBusSubscriberOptions.DisableDeferredProcessorHostedService = true to opt out.")]
    private static partial void LogDeferredProcessorHostedServiceEnabledOn(ILogger logger, string topic, string subscription, int maxConcurrentCalls);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to process deferred-processor trigger on {Topic}/{Subscription} for session {SessionId}")]
    private static partial void LogFailedToProcessDeferredProcessorTrigger(ILogger logger, Exception exception, string topic, string subscription, string sessionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to abandon deferred-processor trigger after handler exception on {Topic}/{Subscription}")]
    private static partial void LogFailedToAbandonDeferredProcessorTrigger(ILogger logger, Exception exception, string topic, string subscription);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deferred-processor service-bus error on {Topic}/{Subscription} (source: {ErrorSource})")]
    private static partial void LogDeferredProcessorServiceBusError(ILogger logger, Exception exception, string topic, string subscription, ServiceBusErrorSource errorSource);
}
