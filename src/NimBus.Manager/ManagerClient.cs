using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.ServiceBus;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;

namespace NimBus.Manager;
public interface IManagerClient
{
    /// <summary>
    /// Resolve failed event request by resubmitting/replacing it.
    /// </summary>
    /// <param name="errorResponse">ErrorResponse received from endpoint, representing the error that needs to be resolved.</param>
    /// <param name="eventTypeId">Event type that should be processed before resolving the error.</param>
    /// <param name="eventJson">Event data of that should be processed before resolving the error.</param>

    public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson);

    /// <summary>
    /// Resolve failed event request by ignoring it.
    /// </summary>
    /// <param name="errorResponse">ErrorResponse received from endpoint, representing the error that needs to be resolved.</param>
    Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId);

}

/// <summary>
/// Sends Manager commands under a message id the caller chooses, so the caller can find the
/// command and its response in the Resolver's history afterwards (Spec 044 Phase 2). A
/// separate interface, so that existing <see cref="IManagerClient"/> implementations keep
/// compiling; <see cref="ManagerClient"/> implements both.
/// </summary>
public interface IManagerCommandClient
{
    /// <summary>Resubmits like <see cref="IManagerClient.Resubmit"/>, publishing under <paramref name="commandMessageId"/>.</summary>
    /// <param name="errorResponse">ErrorResponse received from the endpoint, representing the error to resolve.</param>
    /// <param name="endpoint">The endpoint the command is sent to.</param>
    /// <param name="eventTypeId">Event type of the resubmitted event.</param>
    /// <param name="eventJson">Event data to process.</param>
    /// <param name="commandMessageId">The Service Bus message id of the command; at most 128 characters.</param>
    Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson, string commandMessageId);

    /// <summary>Skips like <see cref="IManagerClient.Skip"/>, publishing under <paramref name="commandMessageId"/>.</summary>
    /// <param name="errorResponse">ErrorResponse received from the endpoint, representing the error to resolve.</param>
    /// <param name="endpoint">The endpoint the command is sent to.</param>
    /// <param name="eventTypeId">Event type of the skipped event.</param>
    /// <param name="commandMessageId">The Service Bus message id of the command; at most 128 characters.</param>
    Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId, string commandMessageId);
}

/// <summary>
/// Sends Manager commands (resubmit, skip) to endpoints. One sender is cached per endpoint and
/// disposed in <see cref="DisposeAsync"/>; the injected <see cref="ServiceBusClient"/> is not
/// disposed.
/// </summary>
public partial class ManagerClient : IManagerClient, IManagerCommandClient, IAsyncDisposable, IDisposable
{
    /// <summary>Longest Service Bus message id.</summary>
    private const int MaximumMessageIdLength = 128;

    private readonly ServiceBusSenderCache? _senders;
    private readonly ILogger _logger;

    /// <summary>Creates a client that sends commands over <paramref name="serviceBusClient"/>.</summary>
    /// <param name="serviceBusClient">The Service Bus client; not disposed by this instance.</param>
    /// <param name="logger">Optional logger.</param>
    public ManagerClient(ServiceBusClient serviceBusClient, ILogger<ManagerClient>? logger = null)
    {
        // A null client still constructs, as before; sending through it then fails.
        _senders = serviceBusClient is null ? null : new ServiceBusSenderCache(serviceBusClient);
        _logger = logger ?? NullLogger<ManagerClient>.Instance;
    }

    /// <summary>
    /// Disposes the cached senders. Calling it again does nothing; a command sent afterwards
    /// throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <returns>A task that completes once the senders are disposed.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_senders is not null)
            await _senders.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="DisposeAsync"/>, for containers disposed
    /// synchronously; it blocks until the senders are closed.
    /// </summary>
    public void Dispose()
    {
        _senders?.Dispose();
        GC.SuppressFinalize(this);
    }

    private ServiceBusSender SenderFor(string endpoint) =>
        (_senders ?? throw new InvalidOperationException("ManagerClient was created without a ServiceBusClient."))
            .GetSender(endpoint);

    public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
        => SendResubmit(errorResponse, endpoint, eventTypeId, eventJson, commandMessageId: null);

    /// <inheritdoc />
    public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson, string commandMessageId)
        => SendResubmit(errorResponse, endpoint, eventTypeId, eventJson, RequireMessageId(commandMessageId));

    public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        => SendSkip(errorResponse, endpoint, eventTypeId, commandMessageId: null);

    /// <inheritdoc />
    public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId, string commandMessageId)
        => SendSkip(errorResponse, endpoint, eventTypeId, RequireMessageId(commandMessageId));

    private static string RequireMessageId(string commandMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandMessageId);
        if (commandMessageId.Length > MaximumMessageIdLength)
            throw new ArgumentException($"A Service Bus message id is at most {MaximumMessageIdLength} characters.", nameof(commandMessageId));
        return commandMessageId;
    }

    // A null id leaves the message id to the broker, as before.
    private async Task SendResubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson, string? commandMessageId)
    {
        LogManagerResubmitEventEventId(_logger, errorResponse.EventId, eventTypeId);
        var message = new Message
        {
            MessageId = commandMessageId,
            CorrelationId = errorResponse.CorrelationId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId,
            To = endpoint,
            From = Constants.ManagerId,
            OriginatingMessageId = errorResponse.OriginatingMessageId ?? errorResponse.MessageId,
            ParentMessageId = errorResponse.MessageId,
            MessageType = MessageType.ResubmissionRequest,
            EventTypeId = eventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = eventJson
                }
            },
        };

        await SenderFor(endpoint).SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }

    private async Task SendSkip(MessageEntity errorResponse, string endpoint, string eventTypeId, string? commandMessageId)
    {
        LogManagerSkipEventSessionId(_logger, errorResponse.SessionId, errorResponse.EventId, errorResponse.To);
        var message = new Message()
        {
            MessageId = commandMessageId,
            CorrelationId = errorResponse.MessageId,
            EventId = errorResponse.EventId,
            SessionId = errorResponse.SessionId,
            To = endpoint,
            From = Constants.ManagerId,
            MessageType = MessageType.SkipRequest,
            MessageContent = new MessageContent(),
            ParentMessageId = errorResponse.MessageId,
            EventTypeId = eventTypeId,
            OriginatingMessageId = errorResponse.OriginatingMessageId ?? errorResponse.MessageId
        };

        await SenderFor(endpoint).SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }


    [LoggerMessage(Level = LogLevel.Trace, Message = "MANAGER RESUBMIT EVENT: EventId: {EventId} EventTypeId: {EventTypeId}")]
    private static partial void LogManagerResubmitEventEventId(ILogger logger, string eventId, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "MANAGER SKIP EVENT: SessionId: {SessionId} EventId: {EventId} From: {From}")]
    private static partial void LogManagerSkipEventSessionId(ILogger logger, string sessionId, string eventId, string from);
}
