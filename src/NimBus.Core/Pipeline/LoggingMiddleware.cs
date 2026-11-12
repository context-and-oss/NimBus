using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Core.Pipeline;

/// <summary>
/// Middleware that logs message processing with timing, event metadata, and outcome.
/// </summary>
public sealed partial class LoggingMiddleware : IMessagePipelineBehavior
{
    private readonly ILogger<LoggingMiddleware> _logger;

    public LoggingMiddleware(ILogger<LoggingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        LogProcessingEventType(_logger, context.MessageType, context.EventTypeId, context.EventId, context.SessionId);

        try
        {
            await next(context, cancellationToken);
            sw.Stop();

            LogCompletedInMsEventType(_logger, context.MessageType, sw.ElapsedMilliseconds, context.EventTypeId, context.EventId);
        }
        catch (SessionBlockedException)
        {
            LogDeferredSessionBlockedEventId(_logger, context.MessageType, context.EventId, context.SessionId);
            throw;
        }
        catch (EventContextHandlerException)
        {
            // StrictMessageHandler already logged and settled this handler failure.
            LogHandlerFailureRecordedForAfterMs(_logger, context.MessageType, sw.ElapsedMilliseconds, context.EventId);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();

            LogFailedAfterMsEventType(_logger, ex, context.MessageType, sw.ElapsedMilliseconds, context.EventTypeId, context.EventId);

            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {MessageType} | EventType={EventTypeId} EventId={EventId} SessionId={SessionId}")]
    private static partial void LogProcessingEventType(ILogger logger, MessageType messageType, string eventTypeId, string eventId, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed {MessageType} in {ElapsedMs}ms | EventType={EventTypeId} EventId={EventId}")]
    private static partial void LogCompletedInMsEventType(ILogger logger, MessageType messageType, long elapsedMs, string eventTypeId, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deferred {MessageType}: session blocked | EventId={EventId} SessionId={SessionId}")]
    private static partial void LogDeferredSessionBlockedEventId(ILogger logger, MessageType messageType, string eventId, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handler failure recorded for {MessageType} after {ElapsedMs}ms | EventId={EventId}")]
    private static partial void LogHandlerFailureRecordedForAfterMs(ILogger logger, MessageType messageType, long elapsedMs, string eventId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed {MessageType} after {ElapsedMs}ms | EventType={EventTypeId} EventId={EventId}")]
    private static partial void LogFailedAfterMsEventType(ILogger logger, Exception exception, MessageType messageType, long elapsedMs, string eventTypeId, string eventId);
}
