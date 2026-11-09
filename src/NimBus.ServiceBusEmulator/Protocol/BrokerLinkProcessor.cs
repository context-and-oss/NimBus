using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Amqp;
using Amqp.Framing;
using Amqp.Listener;
using Amqp.Transactions;
using Amqp.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.ServiceBusEmulator.Broker;

namespace NimBus.ServiceBusEmulator.Protocol;

internal sealed class BrokerLinkProcessor(
    BrokerNamespace broker,
    SessionLinkRegistry sessionLinks,
    PendingLinkAttachRegistry pendingAttaches,
    int maxMessageSize = 262_144,
    ILogger<BrokerLinkProcessor>? logger = null) : ILinkProcessor
{
    private readonly ILogger _logger = logger ?? NullLogger<BrokerLinkProcessor>.Instance;
    private static readonly Symbol SessionFilter = new("com.microsoft:session-filter");
    private static readonly Symbol LockedUntilUtc = new("com.microsoft:locked-until-utc");
    private static readonly Symbol TimeoutProperty = new("com.microsoft:timeout");
    private readonly TransactionRegistry _transactions = new(broker);

    public void Process(AttachContext context)
    {
        context.Attach.MaxMessageSize = (ulong)maxMessageSize;
        if (!context.Attach.Role && context.Attach.Target is Coordinator)
        {
            context.Complete(new TransactionCoordinatorEndpoint(_transactions), 100);
            return;
        }

        var rawAddress = context.Attach.Role
            ? (context.Attach.Source as Source)?.Address
            : (context.Attach.Target as Target)?.Address;
        var address = rawAddress?.TrimStart('/');
        EmulatorDiagnostics.Write(context.Attach.Role ? "Receive attach" : "Send attach", address);
        if (string.IsNullOrWhiteSpace(address))
        {
            context.Complete(NotFound("The AMQP link address is empty."));
            return;
        }

        if (!context.Attach.Role)
        {
            AttachSender(context, address);
            return;
        }

        var pending = pendingAttaches.Register(context);
        _ = AttachReceiverAsync(pending, address).Observe(_logger, "receiver attach", context.Attach.LinkName, exception =>
        {
            if (!context.Link.IsClosed)
            {
                pending.Complete(ErrorFromException(exception));
            }
        });
    }

    private void AttachSender(AttachContext context, string topicName)
    {
        if (!broker.TopicExists(topicName))
        {
            context.Complete(NotFound($"Topic '{topicName}' does not exist."));
            return;
        }

        if (!broker.CanSend(topicName))
        {
            context.Complete(NotAllowed($"Topic '{topicName}' is send-disabled."));
            return;
        }

        context.Complete(new TargetLinkEndpoint(new TopicMessageProcessor(broker, _transactions, topicName, maxMessageSize), context.Link), 100);
    }

    private async Task AttachReceiverAsync(PendingLinkAttachRegistry.PendingAttach pending, string address)
    {
        var context = pending.Context;
        if (!TryParseSubscription(address, out var topicName, out var subscriptionName, out var isDeadLetter) ||
            !broker.SubscriptionExists(topicName, subscriptionName))
        {
            pending.Complete(NotFound($"Subscription '{address}' does not exist."));
            return;
        }
        if (!broker.CanReceive(topicName, subscriptionName))
        {
            pending.Complete(NotAllowed($"Subscription '{address}' is not available for receive."));
            return;
        }

        var source = (Source)context.Attach.Source;
        var owner = Guid.NewGuid().ToString("N");
        object? filterValue = null;
        var isSessionReceiver = source.FilterSet?.TryGetValue(SessionFilter, out filterValue) == true;
        if (isSessionReceiver && !broker.GetSubscriptionDefinition(topicName, subscriptionName).RequiresSession)
        {
            pending.Complete(NotAllowed($"Subscription '{address}' does not require sessions."));
            return;
        }

        string? sessionId = null;
        if (isSessionReceiver)
        {
            var requestedSession = filterValue switch
            {
                string value => value,
                DescribedValue { Value: string value } => value,
                _ => null,
            };
            var timeout = GetTimeout(context.Attach);
            var started = TimeProvider.System.GetTimestamp();
            AcceptedSession? accepted;
            do
            {
                accepted = null;
                try
                {
                    accepted = requestedSession is null
                        ? broker.TryAcceptNextSession(topicName, subscriptionName, owner)
                        : broker.TryAcceptSession(topicName, subscriptionName, requestedSession, owner);
                }
                catch (SessionCannotBeLockedException exception)
                {
                    EmulatorDiagnostics.Write("Session accept rejected", requestedSession);
                    pending.Complete(new Error("com.microsoft:session-cannot-be-locked")
                    {
                        Description = exception.Message,
                    });
                    return;
                }
                if (accepted is not null)
                {
                    sessionId = accepted.SessionId;
                    sessionLinks.Register(context.Link.Session.Connection, context.Attach.LinkName, owner);
                    source.FilterSet![SessionFilter] = sessionId;
                    context.Attach.Properties ??= new Fields();
                    context.Attach.Properties[LockedUntilUtc] = accepted.LockedUntil.UtcTicks;
                    break;
                }

                if (requestedSession is null && TimeProvider.System.GetElapsedTime(started) >= timeout)
                {
                    pending.Complete(new Error("com.microsoft:timeout") { Description = "No unlocked session is available." });
                    return;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }
            while (pending.IsPending);

            if (sessionId is null)
            {
                return;
            }
        }

        var messageSource = new SubscriptionMessageSource(
            broker,
            sessionLinks,
            _transactions,
            context.Link.Session.Connection,
            context.Attach.LinkName,
            topicName,
            subscriptionName,
            isDeadLetter,
            sessionId,
            owner);
        if (!pending.Complete(new ResilientSourceLinkEndpoint(messageSource, context.Link, _logger)))
        {
            messageSource.ReleaseSession();
        }
    }

    private static TimeSpan GetTimeout(Attach attach)
    {
        if (attach.Properties?.TryGetValue(TimeoutProperty, out var value) == true)
        {
            return value switch
            {
                uint milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                int milliseconds => TimeSpan.FromMilliseconds(milliseconds),
                _ => TimeSpan.FromSeconds(60),
            };
        }

        return TimeSpan.FromSeconds(60);
    }

    private static Error NotFound(string description) => new("amqp:not-found") { Description = description };

    private static Error NotAllowed(string description) => new("amqp:not-allowed") { Description = description };

    private static Error ErrorFromException(Exception exception) => exception switch
    {
        KeyNotFoundException => NotFound("The messaging entity no longer exists."),
        AmqpException amqpException => amqpException.Error,
        _ => new Error("amqp:internal-error") { Description = "The broker could not complete the link operation. See the broker logs." },
    };

    private static bool TryParseSubscription(
        string address,
        out string topicName,
        out string subscriptionName,
        out bool isDeadLetter)
    {
        const string separator = "/Subscriptions/";
        var index = address.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index <= 0 || index + separator.Length >= address.Length)
        {
            topicName = string.Empty;
            subscriptionName = string.Empty;
            isDeadLetter = false;
            return false;
        }

        topicName = address[..index];
        subscriptionName = address[(index + separator.Length)..];
        const string deadLetterSuffix = "/$DeadLetterQueue";
        isDeadLetter = subscriptionName.EndsWith(deadLetterSuffix, StringComparison.OrdinalIgnoreCase);
        if (isDeadLetter)
        {
            subscriptionName = subscriptionName[..^deadLetterSuffix.Length];
        }
        return true;
    }

    private sealed class TopicMessageProcessor(
        BrokerNamespace broker,
        TransactionRegistry transactions,
        string topicName,
        int maxMessageSize) : IMessageProcessor
    {
        public int Credit => 100;

        public void Process(MessageContext messageContext)
        {
            EmulatorDiagnostics.Write("Transfer", topicName);
            try
            {
                var messages = AmqpMessageConverter.FromTransfer(messageContext.Message);
                if (messages.Any(message => message.Body.Length > maxMessageSize))
                {
                    messageContext.Complete(new Error("amqp:link:message-size-exceeded")
                    {
                        Description = $"Message exceeds the configured {maxMessageSize}-byte limit.",
                    });
                    return;
                }

                foreach (var brokerMessage in messages)
                {
                    if (messageContext.DeliveryState is TransactionalState transaction)
                    {
                        transactions.StageSend(transaction.TxnId, topicName, brokerMessage);
                    }
                    else
                    {
                        broker.Publish(topicName, brokerMessage);
                    }
                }
                Complete(messageContext);
            }
            catch (BrokerQuotaExceededException exception)
            {
                messageContext.Complete(new Error("amqp:resource-limit-exceeded") { Description = exception.Message });
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                messageContext.Complete(new Error("amqp:not-allowed") { Description = exception.Message });
            }
        }

        private static void Complete(MessageContext context)
        {
            if (context.DeliveryState is TransactionalState transaction)
            {
                context.Link.DisposeMessage(context.Message, new TransactionalState
                {
                    TxnId = transaction.TxnId,
                    Outcome = new Accepted(),
                }, true);
                context.Message.Dispose();
                return;
            }

            context.Complete();
        }
    }

    private sealed class SubscriptionMessageSource(
        BrokerNamespace broker,
        SessionLinkRegistry sessionLinks,
        TransactionRegistry transactions,
        Connection connection,
        string linkName,
        string topicName,
        string subscriptionName,
        bool isDeadLetter,
        string? sessionId,
        string owner) : IMessageSource
    {
        public async Task<ReceiveContext?> GetMessageAsync(ListenerLink link)
        {
            var lastEcho = Environment.TickCount64;
            while (!link.IsClosed)
            {
                // Only acquire (and thereby lock) a message while the peer still
                // grants credit: the client rescinds credit the moment its receive
                // call returns, and a message locked after that would be sent
                // uncredited, dropped unsettled, and stranded until lock expiry --
                // for session subscriptions, until the session lock itself lapses.
                if (GetCredit(link) > 0)
                {
                    var delivery = isDeadLetter
                        ? broker.TryAcquireDeadLetter(topicName, subscriptionName, owner)
                        : broker.TryAcquire(topicName, subscriptionName, sessionId, owner);
                    if (delivery is not null)
                    {
                        // Test hook: widen the acquire-to-send window to make the
                        // credit-rescind race deterministic instead of CI-timing-dependent.
                        if (DeliveryDelay is { } delay)
                        {
                            await Task.Delay(delay).ConfigureAwait(false);
                        }

                        EmulatorDiagnostics.Write("Deliver", $"{topicName}/{subscriptionName} session={sessionId} lock={delivery.LockToken} credit={GetCredit(link)}");
                        return new ReceiveContext(link, AmqpMessageConverter.ToAmqp(delivery.Message))
                        {
                            UserToken = delivery.LockToken,
                        };
                    }
                }
                else if (!link.IsDraining && Environment.TickCount64 - lastEcho >= 500)
                {
                    // Deadlock breaker: when the client settles a surplus delivery
                    // as Released, Microsoft.Azure.Amqp restores that credit
                    // client-side WITHOUT sending a flow (its restoration is
                    // batched), so its next receive call believes credit is
                    // already outstanding while this side sees zero. Both peers
                    // then wait forever. An echo flow obliges the client to
                    // publish its flow state, re-syncing the credit windows.
                    lastEcho = Environment.TickCount64;
                    var hasMessages = isDeadLetter
                        ? broker.PeekDeadLetter(topicName, subscriptionName, 0, 1).Count > 0
                        : broker.Peek(topicName, subscriptionName, 0, 1, sessionId).Count > 0;
                    if (hasMessages)
                    {
                        EmulatorDiagnostics.Write("Echo flow", $"{topicName}/{subscriptionName} session={sessionId}");
                        SendEchoFlow(link);
                    }
                }

                if (link.IsDraining)
                {
                    return null;
                }

                await Task.Delay(25).ConfigureAwait(false);
            }

            return null;
        }

        // ListenerLink.deliveryCount is a private field of the internal struct
        // Amqp.SequenceNumber (single int inside), and Session.SendFlow is
        // internal -- reflection for the former, UnsafeAccessor for the latter.
        private static readonly System.Reflection.FieldInfo DeliveryCountField =
            typeof(ListenerLink).GetField("deliveryCount", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        private static readonly System.Reflection.FieldInfo SequenceNumberValueField =
            DeliveryCountField.FieldType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendFlow")]
        private static extern void SessionSendFlow(Session session, Flow flow);

        private static void SendEchoFlow(ListenerLink link)
        {
            var deliveryCount = SequenceNumberValueField.GetValue(DeliveryCountField.GetValue(link))!;
            SessionSendFlow(link.Session, new Flow
            {
                Handle = link.Handle,
                DeliveryCount = unchecked((uint)Convert.ToInt64(deliveryCount, System.Globalization.CultureInfo.InvariantCulture)),
                LinkCredit = 0,
                Echo = true,
            });
        }

        public void DisposeMessage(ReceiveContext receiveContext, DispositionContext dispositionContext)
        {
            if (receiveContext.UserToken is not Guid lockToken)
            {
                dispositionContext.Complete(new Error("amqp:not-found") { Description = "The delivery lock token is missing." });
                return;
            }

            EmulatorDiagnostics.Write("Disposition", $"{dispositionContext.DeliveryState?.GetType().Name} lock={lockToken}");
            try
            {
                switch (dispositionContext.DeliveryState)
                {
                    case TransactionalState { Outcome: Accepted } transaction:
                        if (!isDeadLetter)
                        {
                            throw new NotSupportedException("Only regular dead-letter completion is transactional.");
                        }

                        transactions.StageComplete(
                            transaction.TxnId,
                            topicName,
                            subscriptionName,
                            lockToken,
                            owner);
                        break;
                    case Accepted:
                        if (isDeadLetter)
                            broker.CompleteDeadLetter(topicName, subscriptionName, lockToken, owner);
                        else
                            broker.Complete(topicName, subscriptionName, lockToken, owner);
                        break;
                    case Released:
                        if (isDeadLetter)
                            broker.ReleaseDeadLetter(topicName, subscriptionName, lockToken, owner);
                        else
                            broker.Release(topicName, subscriptionName, lockToken, owner);
                        break;
                    case Modified modified when modified.DeliveryFailed:
                        if (isDeadLetter)
                            broker.ReleaseDeadLetter(topicName, subscriptionName, lockToken, owner);
                        else
                            broker.Abandon(topicName, subscriptionName, lockToken, owner);
                        break;
                    case Rejected rejected:
                        broker.DeadLetter(
                            topicName,
                            subscriptionName,
                            lockToken,
                            owner,
                            ErrorInfo(rejected.Error, "DeadLetterReason") ?? rejected.Error?.Condition,
                            ErrorInfo(rejected.Error, "DeadLetterErrorDescription") ?? rejected.Error?.Description);
                        break;
                    default:
                        if (isDeadLetter)
                            broker.ReleaseDeadLetter(topicName, subscriptionName, lockToken, owner);
                        else
                            broker.Abandon(topicName, subscriptionName, lockToken, owner);
                        break;
                }

                dispositionContext.Complete();
            }
            catch (KeyNotFoundException exception)
            {
                EmulatorDiagnostics.Write("Disposition failed", $"lock={lockToken} {exception.Message}");
                var condition = sessionId is null
                    ? "com.microsoft:message-lock-lost"
                    : "com.microsoft:session-lock-lost";
                dispositionContext.Complete(new Error(condition) { Description = exception.Message });
            }
            catch (NotSupportedException exception)
            {
                EmulatorDiagnostics.Write("Disposition rejected", $"lock={lockToken} {exception.Message}");
                dispositionContext.Complete(new Error("amqp:not-implemented") { Description = exception.Message });
            }
        }

        public void ReleaseSession()
        {
            if (sessionId is not null)
            {
                sessionLinks.Unregister(connection, linkName, owner);
                try
                {
                    broker.ReleaseSession(topicName, subscriptionName, sessionId, owner);
                }
                catch (KeyNotFoundException)
                {
                    // Deleting the entity already removed the session and its messages.
                }
            }
        }

        private static string? ErrorInfo(Error? error, string key) =>
            error?.Info?.TryGetValue(new Symbol(key), out var value) == true
                ? value?.ToString()
                : null;
    }

    private sealed class TransactionCoordinatorEndpoint(TransactionRegistry transactions) : LinkEndpoint
    {
        private readonly ConcurrentDictionary<string, byte[]> _declared = new(StringComparer.Ordinal);

        public override void OnFlow(FlowContext flowContext)
        {
        }

        public override void OnDisposition(DispositionContext dispositionContext)
        {
        }

        public override void OnMessage(MessageContext context)
        {
            try
            {
                switch (context.Message.Body)
                {
                    case Declare:
                        EmulatorDiagnostics.Write("Transaction", "declare");
                        var transactionId = transactions.Declare();
                        _declared.TryAdd(Convert.ToHexString(transactionId), transactionId);
                        Complete(context, new Declared { TxnId = transactionId });
                        break;
                    case Discharge discharge:
                        EmulatorDiagnostics.Write("Transaction", discharge.Fail ? "rollback" : "commit");
                        try
                        {
                            transactions.Discharge(discharge.TxnId, discharge.Fail);
                            Complete(context, new Accepted());
                        }
                        finally
                        {
                            _declared.TryRemove(Convert.ToHexString(discharge.TxnId), out _);
                        }
                        break;
                    default:
                        Complete(context, new Rejected
                        {
                            Error = new Error("amqp:not-implemented")
                            {
                                Description = "Only local declare and discharge are supported.",
                            },
                        });
                        break;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
            {
                Complete(context, new Rejected
                {
                    Error = new Error("amqp:transaction:rollback") { Description = exception.Message },
                });
            }
        }

        public override void OnLinkClosed(ListenerLink closedLink, Error error)
        {
            foreach (var transaction in _declared.Values)
            {
                try
                {
                    transactions.Discharge(transaction, fail: true);
                }
                catch (KeyNotFoundException)
                {
                    // A concurrent discharge already removed it.
                }
            }

            _declared.Clear();
            base.OnLinkClosed(closedLink, error);
        }

        private static void Complete(MessageContext context, DeliveryState outcome)
        {
            context.Link.DisposeMessage(context.Message, outcome, true);
            context.Message.Dispose();
        }
    }

    private sealed class TransactionRegistry(BrokerNamespace broker)
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, PendingTransaction> _pending = new(StringComparer.Ordinal);

        public byte[] Declare()
        {
            var id = Guid.NewGuid().ToByteArray();
            lock (_gate)
            {
                _pending.Add(Convert.ToHexString(id), new PendingTransaction());
            }

            return id;
        }

        public void StageComplete(
            byte[] transactionId,
            string topicName,
            string subscriptionName,
            Guid lockToken,
            string owner)
        {
            lock (_gate)
            {
                var transaction = Get(transactionId);
                if (transaction.Complete is not null)
                {
                    throw new InvalidOperationException("The emulator supports one completion per transaction.");
                }

                transaction.Complete = new PendingComplete(topicName, subscriptionName, lockToken, owner);
                EmulatorDiagnostics.Write("Transaction", $"stage complete {topicName}/{subscriptionName}");
            }
        }

        public void StageSend(byte[] transactionId, string topicName, BrokerMessage message)
        {
            lock (_gate)
            {
                var transaction = Get(transactionId);
                if (transaction.Send is not null)
                {
                    throw new InvalidOperationException("The emulator supports one send per transaction.");
                }

                transaction.Send = new PendingSend(topicName, message);
                EmulatorDiagnostics.Write("Transaction", $"stage send {topicName}");
            }
        }

        public void Discharge(byte[] transactionId, bool fail)
        {
            PendingTransaction transaction;
            lock (_gate)
            {
                var key = Convert.ToHexString(transactionId);
                if (!_pending.Remove(key, out transaction!))
                {
                    throw new KeyNotFoundException("The transaction is unknown.");
                }
            }

            if (fail)
            {
                ReleaseStagedCompletion(transaction);
                return;
            }

            if (transaction.Complete is not { } complete || transaction.Send is not { } send)
            {
                ReleaseStagedCompletion(transaction);
                throw new InvalidOperationException("A replay transaction requires one completion and one send.");
            }

            try
            {
                broker.CommitDeadLetterReplay(
                    complete.TopicName,
                    complete.SubscriptionName,
                    complete.LockToken,
                    complete.Owner,
                    send.TopicName,
                    send.Message);
            }
            catch
            {
                ReleaseStagedCompletion(transaction);
                throw;
            }
        }

        private void ReleaseStagedCompletion(PendingTransaction transaction)
        {
            if (transaction.Complete is not { } complete)
            {
                return;
            }

            try
            {
                broker.ReleaseDeadLetter(
                    complete.TopicName,
                    complete.SubscriptionName,
                    complete.LockToken,
                    complete.Owner);
            }
            catch (KeyNotFoundException)
            {
                // Lock expiry also restores the message to the regular dead-letter queue.
            }
        }

        private PendingTransaction Get(byte[] transactionId) =>
            _pending.TryGetValue(Convert.ToHexString(transactionId), out var transaction)
                ? transaction
                : throw new KeyNotFoundException("The transaction is unknown.");

        private sealed class PendingTransaction
        {
            public PendingComplete? Complete { get; set; }

            public PendingSend? Send { get; set; }
        }

        private sealed record PendingComplete(
            string TopicName,
            string SubscriptionName,
            Guid LockToken,
            string Owner);

        private sealed record PendingSend(string TopicName, BrokerMessage Message);
    }

    // ListenerLink.Credit is internal in AMQPNetLite.Core 2.5.1.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Credit")]
    private static extern uint GetCredit(ListenerLink link);

    private static readonly TimeSpan? DeliveryDelay =
        int.TryParse(Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_DELIVERY_DELAY_MS"), out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>
    /// Replacement for AMQPNetLite's <c>SourceLinkEndpoint</c>. The library's send
    /// pump has a terminal flaw: a transient send failure breaks the pump loop
    /// WITHOUT resetting its <c>receiving</c> flag, so every later flow performative
    /// is ignored and the link never delivers again (fatal for session receivers,
    /// whose locked messages only free when the session lock lapses). This pump
    /// releases the failed delivery back to the broker and resets the flag so the
    /// next credit restarts delivery.
    /// </summary>
    private sealed class ResilientSourceLinkEndpoint(SubscriptionMessageSource source, ListenerLink link, ILogger logger) : LinkEndpoint
    {
        // Message.Delivery and Amqp.Delivery are internal; the delivery's UserToken
        // carries the ReceiveContext that maps a disposition back to its lock token.
        private static readonly System.Reflection.PropertyInfo DeliveryProperty =
            typeof(Message).GetProperty("Delivery", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        private static readonly System.Reflection.PropertyInfo DeliveryUserTokenProperty =
            DeliveryProperty.PropertyType.GetProperty("UserToken")!;

        // ListenerLink.SendMessageInternal is the only send API that attaches a user
        // token to the outgoing delivery; the public SendMessage overloads do not.
        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SendMessageInternal")]
        private static extern uint SendMessageInternal(ListenerLink link, Message message, ByteBuffer? buffer, object? userToken);

        [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
        private static extern DispositionContext NewDispositionContext(ListenerLink link, Message message, DeliveryState state, bool settled);

        private readonly ReceivePumpGate _pumpGate = new();

        public override void OnFlow(FlowContext flowContext)
        {
            EmulatorDiagnostics.Write("Flow", $"messages={flowContext.Messages} credit={GetCredit(link)}");
            if (!_pumpGate.TryStart(GetCredit(link) > 0))
            {
                return;
            }

            StartPump();
        }

        public override void OnDisposition(DispositionContext dispositionContext)
        {
            if (DeliveryProperty.GetValue(dispositionContext.Message) is { } delivery &&
                DeliveryUserTokenProperty.GetValue(delivery) is ReceiveContext receiveContext)
            {
                source.DisposeMessage(receiveContext, dispositionContext);
            }
        }

        public override void OnLinkClosed(ListenerLink closedLink, Error error)
        {
            EmulatorDiagnostics.Write("Link closed", $"{closedLink.Name} error={error?.Condition}");
            source.ReleaseSession();
            base.OnLinkClosed(closedLink, error);
        }

        private async Task PumpAsync()
        {
            while (!link.IsClosed)
            {
                ReceiveContext? context = await source.GetMessageAsync(link).ConfigureAwait(false);
                if (context is null)
                {
                    if (link.IsDraining)
                    {
                        link.CompleteDrain();
                    }

                    if (_pumpGate.Complete(!link.IsClosed, () => GetCredit(link) > 0))
                    {
                        StartPump();
                    }

                    return;
                }

                try
                {
                    // Unlike the library pump, stay resident at zero credit:
                    // GetMessageAsync idles cheaply, and its stall detector
                    // (echo flow) must keep running while the peer grants none.
                    SendMessageInternal(link, context.Message, null, context);
                }
                catch (Exception exception)
                {
                    EmulatorDiagnostics.Write("Send failed", exception.Message);
                    source.DisposeMessage(context, NewDispositionContext(link, context.Message, new Released(), true));
                    if (_pumpGate.Complete(!link.IsClosed, () => GetCredit(link) > 0))
                    {
                        StartPump();
                    }

                    return;
                }
            }

            _pumpGate.Complete(linkOpen: false, static () => false);
        }

        private void StartPump() => _ = PumpAsync().Observe(logger, "receive pump", link.Name, exception =>
        {
            // GetMessageAsync and drain completion can fail too. Leaving the gate
            // running would strand future flow/drain requests on a dead pump.
            _pumpGate.Complete(linkOpen: false, static () => false);
            link.Close(TimeSpan.Zero, ErrorFromException(exception));
        });
    }
}

internal sealed class ReceivePumpGate
{
    private readonly object _gate = new();
    private bool _receiving;
    private bool _restartRequested;

    public bool TryStart(bool hasCredit)
    {
        lock (_gate)
        {
            if (_receiving)
            {
                _restartRequested |= hasCredit;
                return false;
            }

            if (!hasCredit)
            {
                return false;
            }

            _receiving = true;
            _restartRequested = false;
            return true;
        }
    }

    public bool Complete(bool linkOpen, Func<bool> hasCredit)
    {
        ArgumentNullException.ThrowIfNull(hasCredit);
        lock (_gate)
        {
            _receiving = false;
            if (!linkOpen || (!_restartRequested && !hasCredit()))
            {
                _restartRequested = false;
                return false;
            }

            _receiving = true;
            _restartRequested = false;
            return true;
        }
    }
}

internal static class TaskExtensions
{
    private static readonly Action<ILogger, string, string, Exception?> LogFailure = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(1, "AmqpBackgroundOperationFailed"), "AMQP {Operation} failed on link {LinkName}.");

    public static async Task Observe(this Task task, ILogger logger, string operation, string linkName, Action<Exception> onFailure)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogFailure(logger, operation, linkName, exception);
            try
            {
                onFailure(exception);
            }
            catch (Exception cleanupException)
            {
                LogFailure(logger, "failure cleanup", linkName, cleanupException);
            }
        }
    }
}
