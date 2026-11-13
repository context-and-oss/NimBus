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

public partial class ManagerClient : IManagerClient, IManagerCommandClient
{
    /// <summary>Longest Service Bus message id.</summary>
    private const int MaximumMessageIdLength = 128;

    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger _logger;

    public ManagerClient(ServiceBusClient serviceBusClient, ILogger<ManagerClient>? logger = null)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger ?? NullLogger<ManagerClient>.Instance;
    }

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

        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
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

        await using var sender = _serviceBusClient.CreateSender(endpoint);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message));
    }


    [LoggerMessage(Level = LogLevel.Trace, Message = "MANAGER RESUBMIT EVENT: EventId: {EventId} EventTypeId: {EventTypeId}")]
    private static partial void LogManagerResubmitEventEventId(ILogger logger, string eventId, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Trace, Message = "MANAGER SKIP EVENT: SessionId: {SessionId} EventId: {EventId} From: {From}")]
    private static partial void LogManagerSkipEventSessionId(ILogger logger, string sessionId, string eventId, string from);
}
