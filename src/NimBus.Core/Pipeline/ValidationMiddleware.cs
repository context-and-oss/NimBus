using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;

namespace NimBus.Core.Pipeline;

/// <summary>
/// Middleware that validates message context before processing.
/// Rejects messages with missing critical fields by dead-lettering them.
/// </summary>
public sealed partial class ValidationMiddleware : IMessagePipelineBehavior
{
    private readonly ILogger<ValidationMiddleware> _logger;

    public ValidationMiddleware(ILogger<ValidationMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.EventId))
        {
            LogMessageRejectedMissingEventId(_logger, context.MessageId);
            var reason = "Validation failed: EventId is required";
            await context.DeadLetter(reason, new InvalidOperationException(reason), cancellationToken);
            throw new MessageAlreadyDeadLetteredException(reason);
        }

        if (context.MessageType == MessageType.EventRequest && string.IsNullOrEmpty(context.EventTypeId))
        {
            LogEventRequestRejectedMissingEventTypeId(_logger, context.EventId);
            var reason = "Validation failed: EventTypeId is required for EventRequest";
            await context.DeadLetter(reason, new InvalidOperationException(reason), cancellationToken);
            throw new MessageAlreadyDeadLetteredException(reason);
        }

        await next(context, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message rejected: missing EventId (MessageId={MessageId})")]
    private static partial void LogMessageRejectedMissingEventId(ILogger logger, string messageId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "EventRequest rejected: missing EventTypeId (EventId={EventId})")]
    private static partial void LogEventRequestRejectedMissingEventTypeId(ILogger logger, string eventId);
}

/// <summary>
/// Thrown by middleware that has already dead-lettered a message.
/// MessageHandler recognizes this and fires lifecycle notifications
/// without attempting to dead-letter again.
/// </summary>
public sealed class MessageAlreadyDeadLetteredException : Exception
{
    public MessageAlreadyDeadLetteredException(string reason) : base(reason) { }
}
