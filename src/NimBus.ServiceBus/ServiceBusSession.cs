using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;

public interface IServiceBusSession
{
    Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default);
    Task DeadLetterAsync(IServiceBusMessage message, string reason, string? v, CancellationToken cancellationToken = default);
    Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default);
    Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default);
    Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default);
}

public class ServiceBusSession : IServiceBusSession
{
    private readonly ServiceBusMessageActions? _messageActions;
    private readonly ServiceBusSessionMessageActions? _sessionActions;
    private readonly ServiceBusSessionReceiver? _sessionReceiver;
    private readonly ProcessSessionMessageEventArgs? _processSessionArgs;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly string? _entityPath;

    public ServiceBusSession(ServiceBusSessionMessageActions sessionActions, ServiceBusClient? serviceBusClient = null, string? entityPath = null, string? sessionId = null)
    {
        _sessionActions = sessionActions ?? throw new ArgumentNullException(nameof(sessionActions));
        _serviceBusClient = serviceBusClient;
        _entityPath = entityPath;
    }

    public ServiceBusSession(ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, ServiceBusClient? serviceBusClient, string? entityPath, string? sessionId)
    {
        _messageActions = messageActions ?? throw new ArgumentNullException(nameof(messageActions));
        _sessionActions = sessionActions ?? throw new ArgumentNullException(nameof(sessionActions));
        _serviceBusClient = serviceBusClient;
        _entityPath = entityPath;
    }

    public ServiceBusSession(ServiceBusSessionReceiver sessionReceiver)
    {
        _sessionReceiver = sessionReceiver ?? throw new ArgumentNullException(nameof(sessionReceiver));
    }

    public ServiceBusSession(ProcessSessionMessageEventArgs processSessionArgs, ServiceBusClient? serviceBusClient = null, string? entityPath = null)
    {
        _processSessionArgs = processSessionArgs ?? throw new ArgumentNullException(nameof(processSessionArgs));
        _serviceBusClient = serviceBusClient;
        _entityPath = entityPath;
    }

    public Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        if (_messageActions != null)
        {
            return _messageActions.CompleteMessageAsync(message.Message, cancellationToken);
        }
        if (_sessionReceiver != null)
        {
            return _sessionReceiver.CompleteMessageAsync(message.Message, cancellationToken);
        }
        if (_processSessionArgs != null)
        {
            return _processSessionArgs.CompleteMessageAsync(message.Message, cancellationToken);
        }

        throw new InvalidOperationException(
            "Cannot complete message: no ServiceBusMessageActions, ServiceBusSessionReceiver, or ProcessSessionMessageEventArgs available.");
    }

    public Task DeadLetterAsync(IServiceBusMessage message, string deadLetterReason, string? deadLetterErrorDescription, CancellationToken cancellationToken = default)
    {
        if (_messageActions != null)
        {
            return _messageActions.DeadLetterMessageAsync(message.Message, null, deadLetterReason, deadLetterErrorDescription, cancellationToken);
        }
        if (_sessionReceiver != null)
        {
            return _sessionReceiver.DeadLetterMessageAsync(message.Message, deadLetterReason, deadLetterErrorDescription, cancellationToken);
        }
        if (_processSessionArgs != null)
        {
            return _processSessionArgs.DeadLetterMessageAsync(message.Message, new System.Collections.Generic.Dictionary<string, object>(), deadLetterReason, deadLetterErrorDescription, cancellationToken);
        }

        throw new InvalidOperationException(
            "Cannot dead-letter message: no ServiceBusMessageActions, ServiceBusSessionReceiver, or ProcessSessionMessageEventArgs available.");
    }

    public async Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        System.BinaryData sessionData;
        if (_sessionActions != null)
        {
            sessionData = await _sessionActions.GetSessionStateAsync(cancellationToken);
        }
        else if (_processSessionArgs != null)
        {
            sessionData = await _processSessionArgs.GetSessionStateAsync(cancellationToken);
        }
        else
        {
            sessionData = await _sessionReceiver!.GetSessionStateAsync(cancellationToken);
        }

        return SessionState.FromBinaryData(sessionData);
    }

    public async Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default)
    {
        BinaryData sessionData = sessionState.ToBinaryData();

        if (_sessionActions != null)
        {
            await _sessionActions.SetSessionStateAsync(sessionData, cancellationToken);
        }
        else if (_processSessionArgs != null)
        {
            await _processSessionArgs.SetSessionStateAsync(sessionData, cancellationToken);
        }
        else
        {
            await _sessionReceiver!.SetSessionStateAsync(sessionData, cancellationToken);
        }
    }

    public async Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        if (_serviceBusClient == null || string.IsNullOrEmpty(_entityPath))
        {
            throw new InvalidOperationException(
                "SendScheduledMessageAsync requires a ServiceBusClient and entityPath to be provided. " +
                "Inject ServiceBusClient via dependency injection and pass it to the ServiceBusAdapter.");
        }

        var (topicName, _) = ParseEntityPath();
        await using var sender = _serviceBusClient.CreateSender(topicName);
        await sender.ScheduleMessageAsync(message, scheduledEnqueueTime, cancellationToken);
    }

    private (string topicName, string? subscriptionName) ParseEntityPath()
    {
        var separatorIndex = _entityPath!.IndexOf('/');
        if (separatorIndex >= 0)
        {
            return (_entityPath.Substring(0, separatorIndex), _entityPath.Substring(separatorIndex + 1));
        }
        return (_entityPath, null);
    }
}
