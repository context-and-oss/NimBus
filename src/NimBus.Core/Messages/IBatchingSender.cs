using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

/// <summary>
/// Optional capability of an <see cref="ISender"/> that can send several messages to the broker
/// in one call, as an <see cref="ISenderBatch"/>.
/// </summary>
/// <remarks>
/// The <see cref="Outbox.OutboxDispatcher"/> uses this capability, when the sender implements it,
/// to send runs of claimed rows that share a session and a destination as one batch and to
/// checkpoint them together. A sender without it is dispatched one message at a time.
/// </remarks>
public interface IBatchingSender
{
    /// <summary>
    /// Creates an empty outbound batch. The caller owns the batch and must dispose it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the creation, for example while a connection is being established.</param>
    /// <returns>An empty batch.</returns>
    Task<ISenderBatch> CreateBatchAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A set of messages that is sent to the broker in a single call by <see cref="SendAsync"/>.
/// Created by <see cref="IBatchingSender.CreateBatchAsync"/>.
/// </summary>
public interface ISenderBatch : IDisposable
{
    /// <summary>
    /// The number of messages added to the batch.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Adds <paramref name="message"/> to the batch, to be enqueued after its own
    /// <paramref name="messageEnqueueDelay"/>.
    /// </summary>
    /// <remarks>
    /// The add is synchronous and cannot be interrupted, so it takes no cancellation token.
    /// An implementation may throw, without adding the message, when the message cannot be
    /// mapped or does not belong in the batch (for example, a different session than the
    /// messages already in it).
    /// </remarks>
    /// <param name="message">The message to add.</param>
    /// <param name="messageEnqueueDelay">The message's enqueue delay in minutes, as for <see cref="ISender.Send(IMessage, int, CancellationToken)"/>.</param>
    /// <returns><c>true</c> when the message was added; <c>false</c> when it does not fit into the batch.</returns>
    bool TryAdd(IMessage message, int messageEnqueueDelay);

    /// <summary>
    /// Sends every message in the batch to the broker in one call. The broker accepts or rejects
    /// the batch as a whole.
    /// </summary>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes once the broker accepted the batch.</returns>
    Task SendAsync(CancellationToken cancellationToken = default);
}
