using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.ServiceBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace NimBus.SDK;

/// <summary>
/// Creates <see cref="IHandoffClient"/> instances for endpoints resolved at
/// runtime. <c>AddNimBusHandoffClient(endpoint)</c> covers processes whose
/// endpoint set is known at registration time; this factory covers processes
/// that settle handoffs for arbitrary endpoints (e.g. the management WebApp,
/// where the endpoint arrives as a route parameter or agent zone).
/// </summary>
public interface IHandoffClientFactory
{
    /// <summary>
    /// Returns the settlement client bound to <paramref name="endpointId"/>.
    /// Clients (and their underlying senders) are created once per endpoint
    /// and cached for the factory's lifetime.
    /// </summary>
    IHandoffClient ForEndpoint(string endpointId);
}

/// <summary>
/// Default <see cref="IHandoffClientFactory"/>: builds each endpoint's client
/// exactly like the keyed <c>AddNimBusHandoffClient</c> registration does — an
/// OpenTelemetry-instrumented <see cref="Sender"/> over
/// <see cref="ServiceBusClient.CreateSender(string)"/> — and caches it per
/// endpoint. Senders live as long as the factory; the injected
/// <see cref="ServiceBusClient"/> owns their disposal.
/// </summary>
public sealed class HandoffClientFactory : IHandoffClientFactory
{
    private readonly ServiceBusClient _client;
    private readonly ILoggerFactory? _loggerFactory;

    // Lazy values so concurrent first use of an endpoint creates exactly one
    // long-lived ServiceBusSender: GetOrAdd's value factory can run more than
    // once under a race (only one result wins the cache, but every invocation
    // would have created a sender). ExecutionAndPublication makes creation
    // single-flight.
    private readonly ConcurrentDictionary<string, Lazy<IHandoffClient>> _clients = new(StringComparer.Ordinal);

    public HandoffClientFactory(ServiceBusClient client, ILoggerFactory? loggerFactory = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _loggerFactory = loggerFactory;
    }

    public IHandoffClient ForEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
            throw new ArgumentException("Endpoint must be specified.", nameof(endpointId));

        var lazyClient = _clients.GetOrAdd(endpointId, endpoint => new Lazy<IHandoffClient>(
            () =>
            {
                ISender sender = NimBusOpenTelemetryDecorators.InstrumentSender(
                    new Sender(_client.CreateSender(endpoint)), MessagingSystem.ServiceBus);
                return new HandoffClient(
                    sender,
                    new HandoffClientOptions { Endpoint = endpoint },
                    _loggerFactory?.CreateLogger<HandoffClient>());
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazyClient.Value;
        }
        catch
        {
            // Lazy caches creation exceptions. Remove only this failed instance
            // so a transient Service Bus failure can be retried by the next call
            // without deleting a concurrently installed replacement.
            _clients.TryRemove(new KeyValuePair<string, Lazy<IHandoffClient>>(endpointId, lazyClient));
            throw;
        }
    }
}
