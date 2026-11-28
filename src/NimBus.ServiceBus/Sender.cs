using NimBus.Core.Messages;
using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;


/// <summary>
/// Sends NimBus messages to one Service Bus entity through a <see cref="ServiceBusSender"/>.
/// </summary>
/// <remarks>
/// Also implements <see cref="IBatchingSender"/> on top of <see cref="ServiceBusMessageBatch"/>,
/// so the outbox dispatcher can send a run of messages in one broker call.
/// </remarks>
public abstract class SenderBase : ISender, IBatchingSender
{
    private readonly ServiceBusSender _serviceBusSender;

    public SenderBase(ServiceBusSender serviceBusSender)
    {
        _serviceBusSender = serviceBusSender ?? throw new ArgumentNullException(nameof(serviceBusSender)); ;
    }

    public string TopicName => _serviceBusSender.EntityPath;

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
         _serviceBusSender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message, messageEnqueueDelay), cancellationToken);

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
        _serviceBusSender.SendMessagesAsync(messages.Select(message => MessageHelper.ToServiceBusMessage(message, messageEnqueueDelay)).ToList(), cancellationToken);

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
        _serviceBusSender.ScheduleMessageAsync(MessageHelper.ToServiceBusMessage(message), scheduledEnqueueTime, cancellationToken);

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
        _serviceBusSender.CancelScheduledMessageAsync(sequenceNumber, cancellationToken);

    /// <summary>
    /// Creates an empty <see cref="ServiceBusMessageBatch"/> for this sender's entity, wrapped as an
    /// <see cref="ISenderBatch"/>.
    /// </summary>
    /// <remarks>
    /// Each message added to the batch is built with
    /// <see cref="MessageHelper.ToServiceBusMessage(IMessage, int)"/>, so it is identical to the
    /// message a single <see cref="Send(IMessage, int, CancellationToken)"/> would produce. The
    /// batch accepts only messages with the session (and hence partition key) of its first
    /// message, because a partitioned entity rejects a batch that mixes them; a <c>null</c> and
    /// an empty session count as the same key.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the creation.</param>
    /// <returns>An empty batch that the caller must dispose.</returns>
    public async Task<ISenderBatch> CreateBatchAsync(CancellationToken cancellationToken = default)
    {
        var batch = await _serviceBusSender.CreateMessageBatchAsync(cancellationToken).ConfigureAwait(false);
        return new ServiceBusSenderBatch(_serviceBusSender, batch);
    }

    /// <summary>
    /// An <see cref="ISenderBatch"/> over a <see cref="ServiceBusMessageBatch"/> that holds the
    /// messages of a single session.
    /// </summary>
    private sealed class ServiceBusSenderBatch(ServiceBusSender serviceBusSender, ServiceBusMessageBatch batch) : ISenderBatch
    {
        private string? _routingKey;

        /// <inheritdoc />
        public int Count => batch.Count;

        /// <inheritdoc />
        /// <exception cref="ArgumentException">The message's session differs from the session of the batch's first message.</exception>
        public bool TryAdd(IMessage message, int messageEnqueueDelay)
        {
            ArgumentNullException.ThrowIfNull(message);
            var routingKey = message.SessionId ?? string.Empty;
            if (_routingKey is not null && !string.Equals(_routingKey, routingKey, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The message's session '{routingKey}' differs from the batch's session '{_routingKey}'; a batch holds the messages of one session.",
                    nameof(message));
            }

            if (!batch.TryAddMessage(MessageHelper.ToServiceBusMessage(message, messageEnqueueDelay)))
                return false;

            _routingKey ??= routingKey;
            return true;
        }

        /// <inheritdoc />
        public Task SendAsync(CancellationToken cancellationToken = default) =>
            serviceBusSender.SendMessagesAsync(batch, cancellationToken);

        /// <inheritdoc />
        public void Dispose() => batch.Dispose();
    }
}

public class Sender : SenderBase
{
    public Sender(ServiceBusSender serviceBusSender) : base(serviceBusSender) { }
}

public class SenderManager : SenderBase
{
    public SenderManager(ServiceBusSender serviceBusSender) : base(serviceBusSender) { }
}
