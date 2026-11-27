using Azure.Messaging.ServiceBus;
using Grpc.Core;
using NimBus.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// Returns <paramref name="message"/> to its session after a transient failure, so that it is
    /// the first message delivered when the session is next accepted and later messages in the
    /// session cannot overtake it. The default implementation does nothing, which leaves the
    /// message unsettled under the session lock.
    /// </summary>
    /// <param name="message">The message whose delivery failed transiently.</param>
    /// <param name="cancellationToken">A token that cuts short the retry backoff. The message is
    /// still abandoned when it is cancelled.</param>
    /// <returns>A task that completes once the message has been abandoned and the session
    /// release requested.</returns>
    Task AbandonAsync(IServiceBusMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class ServiceBusSession : IServiceBusSession
{
    private readonly ServiceBusMessageActions? _messageActions;
    private readonly ServiceBusSessionMessageActions? _sessionActions;
    private readonly ServiceBusSessionReceiver? _sessionReceiver;
    private readonly ProcessSessionMessageEventArgs? _processSessionArgs;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly string? _entityPath;

    /// <summary>
    /// The owner-held cache that <see cref="SendScheduledMessageAsync"/> takes its sender from.
    /// <see cref="ServiceBusAdapter"/> sets it so every session it builds shares one sender per
    /// topic. When null, each scheduled send creates and disposes its own sender.
    /// </summary>
    internal ServiceBusSenderCache? SenderCache { get; init; }

    /// <summary>
    /// Creates a session backed only by <see cref="ServiceBusSessionMessageActions"/>. A session built
    /// this way supports session state and scheduled sends, but <see cref="CompleteAsync"/> and
    /// <see cref="DeadLetterAsync"/> throw <see cref="InvalidOperationException"/> because session
    /// actions cannot settle messages. For settlement, use
    /// <see cref="ServiceBusSession(ServiceBusMessageActions, ServiceBusSessionMessageActions, ServiceBusClient?, string?, string?)"/>.
    /// </summary>
    /// <param name="sessionActions">The session actions used for session state.</param>
    /// <param name="serviceBusClient">Optional client used to send scheduled messages.</param>
    /// <param name="entityPath">Optional entity path that scheduled messages are sent to.</param>
    /// <param name="sessionId">The session id (currently unused).</param>
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
            return FromReceiver(() => _sessionReceiver.CompleteMessageAsync(message.Message, cancellationToken));
        }
        if (_processSessionArgs != null)
        {
            return FromReceiver(() => _processSessionArgs.CompleteMessageAsync(message.Message, cancellationToken));
        }

        throw new InvalidOperationException(
            "Cannot complete message: no ServiceBusMessageActions, ServiceBusSessionReceiver, or ProcessSessionMessageEventArgs available.");
    }

    [SuppressMessage("Naming", "CA1725:Parameter names should match base declaration", Justification = "Public API; renaming the interface or the implementation parameters would break callers that pass them by name.")]
    public Task DeadLetterAsync(IServiceBusMessage message, string deadLetterReason, string? deadLetterErrorDescription, CancellationToken cancellationToken = default)
    {
        if (_messageActions != null)
        {
            return FromHost(() => _messageActions.DeadLetterMessageAsync(message.Message, null, deadLetterReason, deadLetterErrorDescription, cancellationToken));
        }
        if (_sessionReceiver != null)
        {
            return FromReceiver(() => _sessionReceiver.DeadLetterMessageAsync(message.Message, deadLetterReason, deadLetterErrorDescription, cancellationToken));
        }
        if (_processSessionArgs != null)
        {
            return FromReceiver(() => _processSessionArgs.DeadLetterMessageAsync(message.Message, new System.Collections.Generic.Dictionary<string, object>(), deadLetterReason, deadLetterErrorDescription, cancellationToken));
        }

        throw new InvalidOperationException(
            "Cannot dead-letter message: no ServiceBusMessageActions, ServiceBusSessionReceiver, or ProcessSessionMessageEventArgs available.");
    }

    /// <summary>
    /// Returns <paramref name="message"/> to the head of its session after a transient failure.
    /// </summary>
    /// <remarks>
    /// <para>A session message has no lock of its own: an unsettled message stays locked to the
    /// receiver that holds the session, and that receiver goes on to deliver the messages after
    /// it. On the session processor and the Functions isolated worker this method therefore waits
    /// a short backoff while it still holds the session (see <see cref="TransientRetryDelay"/>),
    /// abandons the message, and releases the session, so the receiver closes after the callback
    /// and drops anything it prefetched. The failed message is then the head when the session is
    /// next accepted. The session is released even when the abandon fails.</para>
    /// <para>A caller-owned <see cref="ServiceBusSessionReceiver"/> cannot be released from here,
    /// so the message is only abandoned, after the same backoff, and the caller's next receive
    /// returns it unless it prefetched later messages. A session built from
    /// <see cref="ServiceBusSessionMessageActions"/> alone cannot settle, so it does nothing.</para>
    /// <para>The backoff observes <paramref name="cancellationToken"/> and, on the processor, the
    /// processor's own token, so host shutdown and an opening circuit cut it short. The message
    /// is abandoned without a token either way.</para>
    /// </remarks>
    /// <param name="message">The message whose delivery failed transiently.</param>
    /// <param name="cancellationToken">A token that cuts short the retry backoff.</param>
    /// <returns>A task that completes once the message has been abandoned and the session
    /// release requested.</returns>
    public async Task AbandonAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_processSessionArgs != null)
        {
            await WaitBeforeRetry(message, cancellationToken, _processSessionArgs.CancellationToken).ConfigureAwait(false);
            try
            {
                await FromReceiver(() => _processSessionArgs.AbandonMessageAsync(message.Message, propertiesToModify: null, CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                _processSessionArgs.ReleaseSession();
            }

            return;
        }

        if (_messageActions != null)
        {
            await WaitBeforeRetry(message, cancellationToken, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await FromHost(() => _messageActions.AbandonMessageAsync(message.Message, propertiesToModify: null, CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                // The two-actions constructor requires session actions.
                await FromHost(() => _sessionActions!.ReleaseSession(CancellationToken.None)).ConfigureAwait(false);
            }

            return;
        }

        if (_sessionReceiver != null)
        {
            await WaitBeforeRetry(message, cancellationToken, CancellationToken.None).ConfigureAwait(false);
            await FromReceiver(() => _sessionReceiver.AbandonMessageAsync(message.Message, propertiesToModify: null, CancellationToken.None)).ConfigureAwait(false);
        }

        // Session actions alone cannot settle a message: leave it unsettled, as before.
    }

    /// <summary>The longest backoff <see cref="AbandonAsync"/> applies.</summary>
    internal static readonly TimeSpan MaxTransientRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="AbandonAsync"/> holds the session before it abandons a message that
    /// failed transiently on its <paramref name="deliveryCount"/>th delivery: one second, doubling
    /// with each delivery, capped at <see cref="MaxTransientRetryDelay"/>.
    /// </summary>
    /// <param name="deliveryCount">The failed delivery's <c>DeliveryCount</c> (1 for the first).</param>
    /// <returns>The backoff before the abandon.</returns>
    internal static TimeSpan TransientRetryDelay(int deliveryCount)
    {
        var delay = TimeSpan.FromSeconds(1 << Math.Clamp(deliveryCount - 1, 0, 5));
        return delay < MaxTransientRetryDelay ? delay : MaxTransientRetryDelay;
    }

    /// <summary>
    /// Replaces <see cref="TransientRetryDelay"/> for this session. Tests use it to avoid real waits.
    /// </summary>
    internal Func<int, TimeSpan> TransientRetryDelayPolicy { get; set; } = TransientRetryDelay;

    private async Task WaitBeforeRetry(IServiceBusMessage message, CancellationToken cancellationToken, CancellationToken processorToken)
    {
        var delay = TransientRetryDelayPolicy(message.DeliveryCount);
        if (delay <= TimeSpan.Zero)
            return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, processorToken);
        try
        {
            await Task.Delay(delay, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Shutting down or the circuit opened: abandon straight away so the message is the
            // session's head for whichever receiver accepts it next.
        }
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
            sessionData = await FromReceiver(() => _processSessionArgs.GetSessionStateAsync(cancellationToken));
        }
        else
        {
            sessionData = await FromReceiver(() => _sessionReceiver!.GetSessionStateAsync(cancellationToken));
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
            await FromReceiver(() => _processSessionArgs.SetSessionStateAsync(sessionData, cancellationToken));
        }
        else
        {
            await FromReceiver(() => _sessionReceiver!.SetSessionStateAsync(sessionData, cancellationToken));
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
        if (SenderCache is not null)
        {
            // The cache owns the sender: it lives as long as the adapter, not this message.
            await SenderCache.GetSender(topicName).ScheduleMessageAsync(message, scheduledEnqueueTime, cancellationToken);
            return;
        }

        // A session built outside an adapter has no owner to hold a sender, so it keeps the
        // original create-per-call behavior.
        var sender = _serviceBusClient.CreateSender(topicName);
        try
        {
            await sender.ScheduleMessageAsync(message, scheduledEnqueueTime, cancellationToken);
        }
        finally
        {
            await sender.DisposeAsync();
        }
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

    // From Azure.Messaging.ServiceBus 7.21, a session receiver whose link was lost reports itself
    // as closed, so settlement and session-state calls fail locally with ObjectDisposedException
    // instead of the SessionLockLost ServiceBusException that earlier versions threw. Restore
    // SessionLockLost so a lost lock is retried (see MessageContext) instead of dead-lettering a
    // message that the broker redelivers to the next session owner.
    private static async Task FromReceiver(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (ObjectDisposedException e) when (IsClosedReceiver(e))
        {
            throw SessionLockLost(e);
        }
    }

    private static async Task<T> FromReceiver<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ObjectDisposedException e) when (IsClosedReceiver(e))
        {
            throw SessionLockLost(e);
        }
    }

    private static bool IsClosedReceiver(ObjectDisposedException exception) =>
        exception.ObjectName is nameof(ServiceBusReceiver) or nameof(ServiceBusSessionReceiver);

    private static ServiceBusException SessionLockLost(ObjectDisposedException exception) =>
        new("The session receiver is closed, so its session lock is lost.", ServiceBusFailureReason.SessionLockLost, innerException: exception);

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
        var separatorIndex = _entityPath!.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            return (_entityPath.Substring(0, separatorIndex), _entityPath.Substring(separatorIndex + 1));
        }
        return (_entityPath, null);
    }
}
