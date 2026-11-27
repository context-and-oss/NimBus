using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.WebApp.Services.Heartbeat;

/// <summary>
/// Sends heartbeat probes over the WebApp's shared <see cref="ServiceBusClient"/>. One sender is
/// cached per topic and disposed with this instance; the client itself is not disposed.
/// </summary>
public sealed class ServiceBusHeartbeatMessageSender : IHeartbeatMessageSender, IAsyncDisposable
{
    private readonly ServiceBusSenderCache _senders;

    /// <summary>Creates a sender over the shared Service Bus client.</summary>
    /// <param name="client">The WebApp's singleton Service Bus client.</param>
    public ServiceBusHeartbeatMessageSender(ServiceBusClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _senders = new ServiceBusSenderCache(client);
    }

    /// <inheritdoc />
    public async Task SendAsync(string topicName, Message message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        ArgumentNullException.ThrowIfNull(message);

        var sender = _senders.GetSender(topicName);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message), cancellationToken);
    }

    /// <summary>
    /// Disposes the cached senders. Calling it again does nothing; a send afterwards throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <returns>A task that completes once the senders are disposed.</returns>
    public ValueTask DisposeAsync() => _senders.DisposeAsync();
}
