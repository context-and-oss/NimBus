using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers;

/// <summary>
/// Dispatches a request message to an <see cref="IRequestHandler{TRequest,TResponse}"/>
/// and sends the JSON-serialized response to the requester's reply subscription.
/// When the inbound message carries no <c>ReplyTo</c> (the request type was published
/// as a plain event), the handler still runs but the response is discarded.
/// Handler failures send a best-effort error reply — so the requester fails fast
/// with <see cref="RequestReplyException"/> instead of timing out — and then
/// rethrow, leaving the normal Resolver/retry failure path untouched.
/// </summary>
/// <typeparam name="TRequest">The request event type.</typeparam>
/// <typeparam name="TResponse">The response type (serialized as JSON).</typeparam>
public class RequestJsonHandler<TRequest, TResponse> : IEventJsonHandler
    where TRequest : IEvent
    where TResponse : class
{
    private readonly IRequestHandler<TRequest, TResponse> _requestHandler;
    private readonly IReplyDispatcher _replyDispatcher;

    /// <summary>Creates the dispatch wrapper around a request handler.</summary>
    /// <param name="requestHandler">The user's request handler.</param>
    /// <param name="replyDispatcher">The transport used to send replies.</param>
    public RequestJsonHandler(IRequestHandler<TRequest, TResponse> requestHandler, IReplyDispatcher replyDispatcher)
    {
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        _replyDispatcher = replyDispatcher ?? throw new ArgumentNullException(nameof(replyDispatcher));
    }

    /// <inheritdoc />
    public async Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));

        var replyTo = context.ReplyTo;
        var replySessionId = context.ReplyToSessionId;
        var canReply = !string.IsNullOrEmpty(replyTo) && !string.IsNullOrEmpty(replySessionId);

        TRequest request;
        try
        {
            var eventJson = context.MessageContent?.EventContent?.EventJson
                ?? throw new JsonSerializationException(
                    $"Request payload for '{context.EventTypeId}' is missing.");
            request = JsonConvert.DeserializeObject<TRequest>(
                    eventJson,
                    Constants.CreateSafeJsonSettings())
                ?? throw new JsonSerializationException(
                    $"Request payload for '{context.EventTypeId}' deserialized to null.");
        }
        catch (JsonException exception)
        {
            if (canReply)
            {
                await TrySendErrorReplyAsync(replyTo!, replySessionId!, context.CorrelationId, exception, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Invalid wire payloads cannot become valid through retry — same
            // permanent-failure normalization as EventJsonHandler.
            throw new PermanentFailureException(exception);
        }

        TResponse response;
        try
        {
            response = await _requestHandler.Handle(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (canReply)
        {
            await TrySendErrorReplyAsync(replyTo!, replySessionId!, context.CorrelationId, exception, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        if (canReply)
        {
            await _replyDispatcher.SendReplyAsync(
                new ReplyMessage(replyTo!, replySessionId!)
                {
                    CorrelationId = context.CorrelationId,
                    PayloadJson = JsonConvert.SerializeObject(response),
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySendErrorReplyAsync(
        string replyTo,
        string replySessionId,
        string? correlationId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _replyDispatcher.SendReplyAsync(
                new ReplyMessage(replyTo, replySessionId)
                {
                    CorrelationId = correlationId,
                    IsError = true,
                    ErrorType = exception.GetType().FullName,
                    ErrorText = exception.Message,
                },
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort: the original handler failure must win over reply-send failures.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Swallow — the requester will time out instead of failing fast,
            // and the original exception still drives the failure path.
        }
    }
}
