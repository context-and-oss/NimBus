using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>A running simulated subscriber for one endpoint.</summary>
public interface ISimulatedEndpointHost
{
    /// <summary>The endpoint hosted.</summary>
    string EndpointId { get; }

    /// <summary>Starts the session receiver and the deferred processor.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Lets receivers finish what they are handling: returns once the handler has been idle
    /// briefly, or when <paramref name="maxDuration"/> passes.
    /// </summary>
    Task DrainAsync(TimeSpan maxDuration, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels in-flight handlers and stops both processors. Returns when stopped or when
    /// <paramref name="cancellationToken"/> fires, whichever is first.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Creates <see cref="ISimulatedEndpointHost"/> instances. A seam for tests.</summary>
public interface ISimulatedEndpointHostFactory
{
    /// <summary>Creates a host for <paramref name="endpointId"/> handling <paramref name="eventTypeIds"/>.</summary>
    ISimulatedEndpointHost Create(string endpointId, IReadOnlyCollection<string> eventTypeIds, SimulatedHandlerBehavior behavior);
}

/// <summary>
/// Hosts a simulated subscriber in the WebApp process (plan Decision 1). It composes the
/// chain <c>AddNimBusSubscriber</c> builds — <see cref="EventHandlerProvider"/> →
/// <see cref="StrictMessageHandler"/> → <see cref="ResponseService"/> →
/// <see cref="ServiceBusAdapter"/> → <see cref="NimBusReceiverHostedService"/> — plus a
/// <see cref="DeferredMessageProcessorHostedService"/>, so resubmits and skips replay deferred
/// messages. Nothing here is registered as a host service: the simulation starts and stops it.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Lifetime ends at StopAsync, which cancels the token source. A stop that times out abandons loops that still observe the token, so it is deliberately never disposed.")]
public sealed class SimulatedEndpointHost : ISimulatedEndpointHost
{
    /// <summary>Trigger subscription of the deferred processor, as provisioned.</summary>
    public const string DeferredProcessorSubscription = "deferredprocessor";

    private static readonly TimeSpan IdleBeforeDrained = TimeSpan.FromSeconds(2);

    private readonly ServiceBusClient _client;
    private readonly IReadOnlyCollection<string> _eventTypeIds;
    private readonly SimulatedHandlerBehavior _behavior;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private ServiceBusSender? _responseSender;
    private NimBusReceiverHostedService? _receiver;
    private DeferredMessageProcessorHostedService? _deferred;

    /// <summary>Creates the host. Call <see cref="StartAsync"/> to begin receiving.</summary>
    public SimulatedEndpointHost(
        ServiceBusClient client,
        string endpointId,
        IReadOnlyCollection<string> eventTypeIds,
        SimulatedHandlerBehavior behavior,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        EndpointId = string.IsNullOrWhiteSpace(endpointId) ? throw new ArgumentException("Endpoint id is required.", nameof(endpointId)) : endpointId;
        _eventTypeIds = eventTypeIds ?? throw new ArgumentNullException(nameof(eventTypeIds));
        _behavior = behavior ?? throw new ArgumentNullException(nameof(behavior));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string EndpointId { get; }

    /// <summary>Linked into every delivery; cancelled by <see cref="StopAsync"/>.</summary>
    internal CancellationToken StoppingToken => _stopping.Token;

    /// <summary>
    /// Builds the message handler a simulated subscriber runs: the production composition,
    /// used both by <see cref="StartAsync"/> and, over the in-memory transport, by tests.
    /// </summary>
    /// <param name="endpointId">The endpoint handled.</param>
    /// <param name="eventTypeIds">Event types to register handlers for.</param>
    /// <param name="behavior">What each handler does.</param>
    /// <param name="responseSender">Sender for Resolver responses (the endpoint's topic).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="stopping">
    /// Linked into every delivery's token, so stopping the host cancels in-flight handlers as a
    /// cooperative cancellation rather than as a handler failure.
    /// </param>
    public static IMessageHandler BuildMessageHandler(
        string endpointId,
        IEnumerable<string> eventTypeIds,
        SimulatedHandlerBehavior behavior,
        ISender responseSender,
        ILoggerFactory loggerFactory,
        CancellationToken stopping = default)
    {
        ArgumentNullException.ThrowIfNull(eventTypeIds);
        ArgumentNullException.ThrowIfNull(behavior);
        ArgumentNullException.ThrowIfNull(responseSender);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (!string.Equals(endpointId, behavior.EndpointId, StringComparison.Ordinal))
            throw new ArgumentException("The behavior belongs to a different endpoint.", nameof(behavior));

        var provider = new EventHandlerProvider();
        foreach (var eventTypeId in eventTypeIds.Distinct(StringComparer.Ordinal))
        {
            provider.RegisterHandler(eventTypeId, () => new DelegateEventJsonHandler(behavior.HandleAsync));
        }

        var retryPolicies = new DefaultRetryPolicyProvider().SetDefaultPolicy(new RetryPolicy
        {
            MaxRetries = SimulationLimits.SimulatorMaxRetries,
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = SimulationLimits.SimulatorRetryDelay,
        });

        IMessageHandler handler = new StrictMessageHandler(
            provider,
            new ResponseService(responseSender),
            loggerFactory.CreateLogger<StrictMessageHandler>(),
            retryPolicies,
            pipeline: null,
            lifecycleNotifier: null,
            failureDispositionClassifier: new SimulatedFailureClassifier(),
            inboxDuplicateDetector: null);

        return stopping.CanBeCanceled ? new StoppingAwareHandler(handler, stopping) : handler;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_receiver is not null)
            throw new InvalidOperationException("A simulated endpoint host starts once; create a new host to start again.");

        _responseSender = _client.CreateSender(EndpointId);
        var sender = NimBusOpenTelemetryDecorators.InstrumentSender(new Sender(_responseSender), MessagingSystem.ServiceBus);
        var handler = BuildMessageHandler(EndpointId, _eventTypeIds, _behavior, sender, _loggerFactory, _stopping.Token);

        _receiver = new NimBusReceiverHostedService(
            _client,
            new ServiceBusAdapter(handler, _client),
            new NimBusReceiverOptions
            {
                TopicName = EndpointId,
                SubscriptionName = EndpointId,
                ProcessorShutdownTimeout = TimeSpan.FromSeconds(10),
            },
            _loggerFactory.CreateLogger<NimBusReceiverHostedService>());

        _deferred = new DeferredMessageProcessorHostedService(
            _client,
            new DeferredMessageProcessor(_client),
            new DeferredMessageProcessorHostedServiceOptions(EndpointId, DeferredProcessorSubscription),
            _loggerFactory.CreateLogger<DeferredMessageProcessorHostedService>());

        await _receiver.StartAsync(cancellationToken).ConfigureAwait(false);
        await _deferred.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DrainAsync(TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        if (maxDuration <= TimeSpan.Zero)
            return;

        var deadline = _timeProvider.GetUtcNow() + maxDuration;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastActivity = _behavior.LastActivity;
            var idle = _behavior.InFlight == 0
                && (lastActivity is null || _timeProvider.GetUtcNow() - lastActivity.Value >= IdleBeforeDrained);
            if (idle)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(200), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);

        var stops = new List<Task>();
        if (_receiver is not null)
            stops.Add(_receiver.StopAsync(cancellationToken));
        if (_deferred is not null)
            stops.Add(_deferred.StopAsync(cancellationToken));

        try
        {
            await Task.WhenAll(stops).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_responseSender is not null)
            {
                // Abandon rather than wait on a slow close: the sender is private to this host.
                _ = _responseSender.DisposeAsync().AsTask();
            }
        }
    }

    // Links the host's stopping token into each delivery so StrictMessageHandler sees a
    // cancelled delivery token and treats the stop as cooperative shutdown.
    private sealed class StoppingAwareHandler(IMessageHandler inner, CancellationToken stopping) : IMessageHandler
    {
        public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            await inner.Handle(messageContext, linked.Token).ConfigureAwait(false);
        }
    }
}

/// <summary>Creates <see cref="SimulatedEndpointHost"/> instances over the WebApp's <see cref="ServiceBusClient"/>.</summary>
public sealed class SimulatedEndpointHostFactory : ISimulatedEndpointHostFactory
{
    private readonly ServiceBusClient _client;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the factory.</summary>
    public SimulatedEndpointHostFactory(ServiceBusClient client, ILoggerFactory loggerFactory, TimeProvider timeProvider)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public ISimulatedEndpointHost Create(string endpointId, IReadOnlyCollection<string> eventTypeIds, SimulatedHandlerBehavior behavior) =>
        new SimulatedEndpointHost(_client, endpointId, eventTypeIds, behavior, _loggerFactory, _timeProvider);
}
