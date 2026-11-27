using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;

/// <summary>
/// Caches one <see cref="ServiceBusSender"/> per queue or topic name, so repeated sends to the
/// same entity reuse one AMQP link instead of attaching and detaching a link per call.
/// </summary>
/// <remarks>
/// <para>
/// Entity names are compared with ordinal equality. Concurrent first calls for the same entity
/// create exactly one sender. When <see cref="ServiceBusClient.CreateSender(string)"/> throws,
/// the exception reaches the caller and nothing is cached, so the next call tries again.
/// </para>
/// <para>
/// Disposing the cache disposes every cached sender once; disposing it again does nothing. After
/// disposal <see cref="GetSender"/> throws <see cref="ObjectDisposedException"/>. The cache does
/// not dispose the <see cref="ServiceBusClient"/> it was given. <see cref="Dispose"/> exists so a
/// container that is disposed synchronously can still release the cache; it blocks on
/// <see cref="DisposeAsync"/>.
/// </para>
/// </remarks>
public sealed class ServiceBusSenderCache : IAsyncDisposable, IDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Creates a cache over an existing <see cref="ServiceBusClient"/>.</summary>
    /// <param name="client">The client that creates the senders; not disposed by this cache.</param>
    public ServiceBusSenderCache(ServiceBusClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Returns the cached sender for <paramref name="queueOrTopicName"/>, creating it on first use.
    /// The caller must not dispose the returned sender: the cache owns it.
    /// </summary>
    /// <param name="queueOrTopicName">The queue or topic the sender sends to.</param>
    /// <returns>The sender for the entity.</returns>
    /// <exception cref="ObjectDisposedException">The cache has been disposed.</exception>
    public ServiceBusSender GetSender(string queueOrTopicName)
    {
        ArgumentNullException.ThrowIfNull(queueOrTopicName);

        // Volatile read on the fast path; the gate below makes creation and disposal exclusive.
        if (!System.Threading.Volatile.Read(ref _disposed) && _senders.TryGetValue(queueOrTopicName, out var cached))
            return cached;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_senders.TryGetValue(queueOrTopicName, out cached))
                return cached;

            // CreateSender does no I/O (the link opens on first send), so holding the gate is cheap.
            // A throw leaves nothing cached.
            var sender = _client.CreateSender(queueOrTopicName);
            _senders[queueOrTopicName] = sender;
            return sender;
        }
    }

    /// <summary>Disposes every cached sender once. Calling it again does nothing.</summary>
    /// <returns>A task that completes once the senders are disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        List<ServiceBusSender> senders;
        lock (_gate)
        {
            if (_disposed)
                return;

            System.Threading.Volatile.Write(ref _disposed, true);
            senders = new List<ServiceBusSender>(_senders.Values);
            _senders.Clear();
        }

        foreach (var sender in senders)
        {
            await sender.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes every cached sender once, blocking until they are closed. Prefer
    /// <see cref="DisposeAsync"/>; this exists for synchronously disposed containers.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
