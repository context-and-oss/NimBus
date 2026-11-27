using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

/// <summary>
/// An <see cref="ISender"/> decorator that rethrows a send failure the transport classifies as
/// transient as a <see cref="TransientException"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use it for the sender behind a <see cref="ResponseService"/>. The message handlers settle a
/// delivery by sending responses (a ResolutionResponse after a handler succeeded, an
/// ErrorResponse before the session is blocked, a DeferralResponse and the parked copy of a
/// blocked event). A broker error such as a busy or timed-out namespace would otherwise reach
/// the handler's unexpected-failure path and dead-letter the delivery, even though the handler's
/// own work already committed. As a <see cref="TransientException"/> the delivery is abandoned
/// instead and redelivered.
/// </para>
/// <para>
/// The transport decides what is transient, because only it knows its exception types; for
/// Service Bus see <c>NimBus.ServiceBus.ServiceBusTransientFailures</c>. A failure the predicate
/// does not classify, a <see cref="TransientException"/> already thrown by the inner sender, and
/// an <see cref="OperationCanceledException"/> propagate unchanged.
/// </para>
/// </remarks>
public sealed class TransientFailureClassifyingSender : ISender
{
    private readonly ISender _inner;
    private readonly Func<Exception, bool> _isTransient;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransientFailureClassifyingSender"/> class.
    /// </summary>
    /// <param name="inner">The sender that sends the messages.</param>
    /// <param name="isTransient">Whether a failure of <paramref name="inner"/> is transient.</param>
    public TransientFailureClassifyingSender(ISender inner, Func<Exception, bool> isTransient)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _isTransient = isTransient ?? throw new ArgumentNullException(nameof(isTransient));
    }

    /// <inheritdoc />
    public async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.Send(message, messageEnqueueDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            throw Transient(exception);
        }
    }

    /// <inheritdoc />
    public async Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.Send(messages, messageEnqueueDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            throw Transient(exception);
        }
    }

    /// <inheritdoc />
    public async Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _inner.ScheduleMessage(message, scheduledEnqueueTime, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            throw Transient(exception);
        }
    }

    /// <inheritdoc />
    public async Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.CancelScheduledMessage(sequenceNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            throw Transient(exception);
        }
    }

    private bool IsTransient(Exception exception) =>
        exception is not TransientException
        && exception is not OperationCanceledException
        && _isTransient(exception);

    private static TransientException Transient(Exception exception) =>
        new($"Transient failure sending a message: {exception.Message}", exception);
}
