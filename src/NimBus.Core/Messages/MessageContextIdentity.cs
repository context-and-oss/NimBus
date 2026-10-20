using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.Messages;

/// <summary>
/// Non-throwing accessors for message identity fields. Transport contexts such as the
/// Service Bus <c>MessageContext</c> throw <see cref="InvalidMessageException"/> when a
/// field is absent on the wire. Lifecycle notification, diagnostic logging, and inbox
/// deduplication must keep processing such messages, so they read identity through these
/// helpers instead of the throwing properties.
/// </summary>
public static class MessageContextIdentity
{
    /// <summary>
    /// Gets the message identifier, or <see langword="null"/> when the context is
    /// <see langword="null"/> or the identifier is not defined on the message.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The message identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? GetMessageIdOrDefault(this IMessageContext? context)
    {
        if (context is null) return null;
        try { return context.MessageId; }
        catch (InvalidMessageException) { return null; }
    }

    /// <summary>
    /// Gets the receiving endpoint identifier, or <see langword="null"/> when the context is
    /// <see langword="null"/> or the endpoint is not defined on the message.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The endpoint identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? GetEndpointIdOrDefault(this IMessageContext? context)
    {
        if (context is null) return null;
        try { return context.To; }
        catch (InvalidMessageException) { return null; }
    }

    /// <summary>
    /// Gets the sending endpoint identifier, or <see langword="null"/> when the message is
    /// <see langword="null"/> or the sender is not defined on the message. <c>From</c> is
    /// stamped by the topology's forward rule, so a message put on a topic by hand (e.g.
    /// resubmitted from a dead-letter queue with a broker tool) arrives without it.
    /// The <see cref="Constants.DeferredSubscriptionName"/> marker that parking stamps on
    /// such a request only keeps the forward rules from matching its replay; it names no
    /// sender, so it reads as unavailable too.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The sending endpoint identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? GetFromOrDefault(this IMessage? message)
    {
        if (message is null) return null;
        string? from;
        try { from = message.From; }
        catch (InvalidMessageException) { return null; }

        return string.IsNullOrEmpty(from) || from.Equals(Constants.DeferredSubscriptionName, System.StringComparison.OrdinalIgnoreCase)
            ? null
            : from;
    }

    /// <summary>
    /// Gets the session identifier, or <see langword="null"/> when the context is
    /// <see langword="null"/> or the identifier is not defined on the message.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The session identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? GetSessionIdOrDefault(this IMessageContext? context)
    {
        if (context is null) return null;
        try { return context.SessionId; }
        catch (InvalidMessageException) { return null; }
    }

    /// <summary>
    /// Gets the <see cref="IMessage.InboxMessageId"/> a RetryRequest or a parked copy carries,
    /// or <see langword="null"/> when the message is <see langword="null"/>, carries none, or
    /// carries a blank one.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The carried inbox message identifier, or <see langword="null"/>.</returns>
    public static string? GetInboxMessageIdOrDefault(this IMessage? message)
    {
        if (message is null) return null;
        string? inboxMessageId;
        try { inboxMessageId = message.InboxMessageId; }
        catch (InvalidMessageException) { return null; }

        return string.IsNullOrWhiteSpace(inboxMessageId) ? null : inboxMessageId;
    }

    /// <summary>
    /// Gets the MessageId the source delivered this message's event with: the carried
    /// <see cref="IMessage.InboxMessageId"/> of a stand-in (a RetryRequest or a parked copy),
    /// otherwise the message's own identifier. A new stand-in carries this value forward.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The source message identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? GetSourceMessageIdOrDefault(this IMessageContext? context) =>
        context.GetInboxMessageIdOrDefault() ?? context.GetMessageIdOrDefault();

    /// <summary>
    /// Gets the event identifier, or <see langword="null"/> when the context is
    /// <see langword="null"/> or the identifier is not defined on the message.
    /// </summary>
    /// <param name="context">The message context.</param>
    /// <returns>The event identifier, or <see langword="null"/> when unavailable.</returns>
    public static string? GetEventIdOrDefault(this IMessageContext? context)
    {
        if (context is null) return null;
        try { return context.EventId; }
        catch (InvalidMessageException) { return null; }
    }
}
