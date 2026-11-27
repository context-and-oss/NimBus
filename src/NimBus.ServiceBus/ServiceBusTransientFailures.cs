using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using System;

namespace NimBus.ServiceBus;

/// <summary>
/// Classifies Service Bus send failures as transient, so that a response that could not be sent
/// abandons the delivery for redelivery instead of dead-lettering it.
/// </summary>
public static class ServiceBusTransientFailures
{
    /// <summary>
    /// Whether <paramref name="exception"/> is a Service Bus failure worth retrying by
    /// redelivery: one the SDK marks <see cref="ServiceBusException.IsTransient"/>, or one with a
    /// reason that signals a busy, slow or unreachable namespace (the SDK's own retries were
    /// already spent on it).
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> when the failure is transient.</returns>
    public static bool IsTransient(Exception exception) =>
        exception is ServiceBusException serviceBusException
        && (serviceBusException.IsTransient
            || serviceBusException.Reason is ServiceBusFailureReason.ServiceBusy
                or ServiceBusFailureReason.ServiceTimeout
                or ServiceBusFailureReason.ServiceCommunicationProblem);

    /// <summary>
    /// Wraps <paramref name="sender"/> so that a transient Service Bus failure (see
    /// <see cref="IsTransient"/>) is thrown as a
    /// <see cref="NimBus.Core.Messages.Exceptions.TransientException"/>. Use it for the sender
    /// behind a <see cref="ResponseService"/>.
    /// </summary>
    /// <param name="sender">The sender to wrap.</param>
    /// <returns>The classifying sender.</returns>
    public static ISender ClassifyTransientFailures(ISender sender) =>
        new TransientFailureClassifyingSender(sender, IsTransient);
}
