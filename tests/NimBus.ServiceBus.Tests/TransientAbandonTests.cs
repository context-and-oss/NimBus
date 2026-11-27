#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// A transient failure returns the message to the head of its session: the session waits a
/// backoff, abandons the message and releases the session, so later messages in the session
/// cannot overtake it (see TransientFailureSessionOrderTests in the emulator suite).
/// </summary>
[TestClass]
public class TransientAbandonTests
{
    [TestMethod]
    public async Task MessageHandler_TransientException_OnProcessor_AbandonsThenReleasesTheSession()
    {
        var args = new RecordingSessionArgs(Message(deliveryCount: 1));
        var session = NoWait(new ServiceBusSession(args));
        var context = new MessageContext(new ServiceBusMessage(args.Message), session);

        await new ThrowingHandler(new TransientException("transient")).Handle(context);

        CollectionAssert.AreEqual(new[] { "abandon", "release" }, args.Operations);
        Assert.AreSame(args.Message, args.AbandonedMessage);
    }

    [TestMethod]
    public async Task Abandon_OnProcessor_ReleasesTheSessionWhenTheLockIsAlreadyLost()
    {
        var args = new RecordingSessionArgs(Message(deliveryCount: 1))
        {
            AbandonException = new ServiceBusException("lock lost", ServiceBusFailureReason.SessionLockLost),
        };
        var context = new MessageContext(new ServiceBusMessage(args.Message), NoWait(new ServiceBusSession(args)));

        // The broker already returns a message whose lock is lost; that is not a failure.
        await context.Abandon(new TransientException("transient"));

        CollectionAssert.AreEqual(new[] { "abandon", "release" }, args.Operations);
    }

    [TestMethod]
    public async Task Abandon_OnProcessor_WaitsTheBackoffForTheDeliveryBeforeAbandoning()
    {
        var args = new RecordingSessionArgs(Message(deliveryCount: 3));
        var session = new ServiceBusSession(args);
        int? requestedFor = null;
        session.TransientRetryDelayPolicy = deliveryCount =>
        {
            requestedFor = deliveryCount;
            args.Operations.Add("wait");
            return TimeSpan.FromMilliseconds(1);
        };

        await new MessageContext(new ServiceBusMessage(args.Message), session).Abandon(new TransientException("transient"));

        Assert.AreEqual(3, requestedFor);
        CollectionAssert.AreEqual(new[] { "wait", "abandon", "release" }, args.Operations);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Abandon_OnProcessor_StoppingProcessorCutsTheBackoffShort()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var args = new RecordingSessionArgs(Message(deliveryCount: 1), stopping.Token);
        var session = new ServiceBusSession(args) { TransientRetryDelayPolicy = _ => Timeout.InfiniteTimeSpan };

        await new MessageContext(new ServiceBusMessage(args.Message), session).Abandon(new TransientException("transient"));

        CollectionAssert.AreEqual(new[] { "abandon", "release" }, args.Operations);
        Assert.AreEqual(CancellationToken.None, args.AbandonCancellationToken, "The abandon itself must not observe the stopping token.");
    }

    [TestMethod]
    public async Task Abandon_OnFunctionsWorker_AbandonsThenReleasesTheSession()
    {
        var operations = new List<string>();
        var messageActions = new RecordingMessageActions(operations);
        var sessionActions = new RecordingSessionActions(operations);
        var received = Message(deliveryCount: 1);
        var session = NoWait(new ServiceBusSession(messageActions, sessionActions, null, null, "session-1"));

        await new MessageContext(new ServiceBusMessage(received), session).Abandon(new TransientException("transient"));

        CollectionAssert.AreEqual(new[] { "abandon", "release" }, operations);
        Assert.AreSame(received, messageActions.AbandonedMessage);
    }

    [TestMethod]
    public async Task Abandon_OnCallerOwnedReceiver_AbandonsWithoutReleasing()
    {
        var receiver = new RecordingServiceBusSessionReceiver();
        var received = Message(deliveryCount: 1);
        var session = NoWait(new ServiceBusSession(receiver));

        await new MessageContext(new ServiceBusMessage(received), session).Abandon(new TransientException("transient"));

        CollectionAssert.AreEqual(new[] { received }, receiver.AbandonedMessages);
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(5, 16)]
    [DataRow(6, 30)]
    [DataRow(10, 30)]
    [DataRow(0, 1)]
    public void TransientRetryDelay_DoublesFromOneSecondUpToThirtySeconds(int deliveryCount, int expectedSeconds)
    {
        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), ServiceBusSession.TransientRetryDelay(deliveryCount));
    }

    private static ServiceBusSession NoWait(ServiceBusSession session)
    {
        session.TransientRetryDelayPolicy = _ => TimeSpan.Zero;
        return session;
    }

    private static ServiceBusReceivedMessage Message(int deliveryCount) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            messageId: "m-1",
            sessionId: "session-1",
            deliveryCount: deliveryCount,
            properties: new Dictionary<string, object>
            {
                [nameof(UserPropertyName.MessageType)] = nameof(MessageType.EventRequest),
                [nameof(UserPropertyName.EventId)] = "E1",
            });

    private sealed class ThrowingHandler : MessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHandler(Exception exception)
            : base(NullLogger.Instance) => _exception = exception;

        public override Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            throw _exception;
    }

    private sealed class RecordingSessionArgs : ProcessSessionMessageEventArgs
    {
        public RecordingSessionArgs(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
            : base(message, receiver: null!, identifier: "processor-1", cancellationToken)
        {
        }

        public List<string> Operations { get; } = new();

        public ServiceBusReceivedMessage? AbandonedMessage { get; private set; }

        public CancellationToken AbandonCancellationToken { get; private set; }

        public Exception? AbandonException { get; set; }

        public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default)
        {
            Operations.Add("abandon");
            AbandonedMessage = message;
            AbandonCancellationToken = cancellationToken;
            return AbandonException is null ? Task.CompletedTask : Task.FromException(AbandonException);
        }

        public override void ReleaseSession() => Operations.Add("release");
    }

    private sealed class RecordingMessageActions : ServiceBusMessageActions
    {
        private readonly List<string> _operations;

        public RecordingMessageActions(List<string> operations) => _operations = operations;

        public ServiceBusReceivedMessage? AbandonedMessage { get; private set; }

        public override Task AbandonMessageAsync(ServiceBusReceivedMessage message, IDictionary<string, object>? propertiesToModify = null, CancellationToken cancellationToken = default)
        {
            _operations.Add("abandon");
            AbandonedMessage = message;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionActions : ServiceBusSessionMessageActions
    {
        private readonly List<string> _operations;

        public RecordingSessionActions(List<string> operations) => _operations = operations;

        public override Task ReleaseSession(CancellationToken cancellationToken = default)
        {
            _operations.Add("release");
            return Task.CompletedTask;
        }
    }
}
