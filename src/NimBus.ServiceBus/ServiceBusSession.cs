using Azure.Messaging.ServiceBus;
using Grpc.Core;
using NimBus.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Text.RegularExpressions;
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
            return FromHost(() => _messageActions.CompleteMessageAsync(message.Message, cancellationToken));
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
            return FromHost(() => _messageActions.DeadLetterMessageAsync(message.Message, null, deadLetterReason, deadLetterErrorDescription, cancellationToken));
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
            sessionData = await FromHost(() => _sessionActions.GetSessionStateAsync(cancellationToken));
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
            await FromHost(() => _sessionActions.SetSessionStateAsync(sessionData, cancellationToken));
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

    // The Functions isolated worker settles messages and reads session state through the host
    // over gRPC, so a Service Bus failure there reaches the worker as an RpcException that
    // carries the host's ServiceBusException only as text. Rebuild the ServiceBusException
    // from that text so a lost session lock or a transient fault is classified the same way
    // as on the other transports (see MessageContext), instead of dead-lettering the message.
    private static async Task FromHost(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (RpcException e) when (TryRebuildHostException(e, out var serviceBusException))
        {
            throw serviceBusException;
        }
    }

    private static async Task<T> FromHost<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (RpcException e) when (TryRebuildHostException(e, out var serviceBusException))
        {
            throw serviceBusException;
        }
    }

    // The host formats the outermost exception first, as "<type>: <message> (<reason>). For
    // troubleshooting information, see ...", followed by any inner exceptions after " ---> ".
    private static readonly Regex HostServiceBusException = new(
        @"^Azure\.Messaging\.ServiceBus\.ServiceBusException: (?<message>.*?) \((?<reason>[A-Za-z]+)\)(?:\.|$)",
        RegexOptions.CultureInvariant);

    private static bool TryRebuildHostException(RpcException exception, out ServiceBusException serviceBusException)
    {
        var outermost = exception.Status.Detail ?? string.Empty;
        var innerStart = outermost.IndexOf(" ---> ", StringComparison.Ordinal);
        if (innerStart >= 0)
            outermost = outermost.Substring(0, innerStart);

        var match = HostServiceBusException.Match(outermost);
        if (match.Success
            && Enum.TryParse(match.Groups["reason"].Value, out ServiceBusFailureReason reason)
            && Enum.IsDefined(reason))
        {
            serviceBusException = new ServiceBusException(match.Groups["message"].Value, reason, innerException: exception);
            return true;
        }

        serviceBusException = null!;
        return false;
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
