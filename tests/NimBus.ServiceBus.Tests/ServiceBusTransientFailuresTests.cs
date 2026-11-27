#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusTransientFailuresTests
{
    [TestMethod]
    [DataRow(ServiceBusFailureReason.ServiceBusy, true)]
    [DataRow(ServiceBusFailureReason.ServiceTimeout, true)]
    [DataRow(ServiceBusFailureReason.ServiceCommunicationProblem, true)]
    [DataRow(ServiceBusFailureReason.MessageSizeExceeded, false)]
    [DataRow(ServiceBusFailureReason.MessagingEntityNotFound, false)]
    [DataRow(ServiceBusFailureReason.QuotaExceeded, false)]
    public void IsTransient_ClassifiesByReason(ServiceBusFailureReason reason, bool expected)
    {
        Assert.AreEqual(expected, ServiceBusTransientFailures.IsTransient(new ServiceBusException("broker", reason)));
    }

    [TestMethod]
    public void IsTransient_HonoursTheSdkTransientFlag()
    {
        Assert.IsTrue(ServiceBusTransientFailures.IsTransient(new ServiceBusException(isTransient: true, "flagged")));
        Assert.IsFalse(ServiceBusTransientFailures.IsTransient(new InvalidOperationException("not a broker failure")));
    }

    [TestMethod]
    public async Task ClassifiedSender_TransientSendFailure_ThrowsTransientExceptionWithTheBrokerFailureInside()
    {
        var failure = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
        var sut = ServiceBusTransientFailures.ClassifyTransientFailures(new Sender(new FailingServiceBusSender(failure)));

        var thrown = await Assert.ThrowsExactlyAsync<TransientException>(() => sut.Send(CreateMessage(MessageType.ResolutionResponse)));
        Assert.AreSame(failure, thrown.InnerException);

        var batch = await Assert.ThrowsExactlyAsync<TransientException>(() => sut.Send(new[] { CreateMessage(MessageType.ResolutionResponse) }));
        Assert.AreSame(failure, batch.InnerException);

        var scheduled = await Assert.ThrowsExactlyAsync<TransientException>(() => sut.ScheduleMessage(CreateMessage(MessageType.RetryRequest), DateTimeOffset.UtcNow));
        Assert.AreSame(failure, scheduled.InnerException);
    }

    [TestMethod]
    public async Task ClassifiedSender_NonTransientSendFailure_PropagatesUnchanged()
    {
        var failure = new ServiceBusException("too large", ServiceBusFailureReason.MessageSizeExceeded);
        var sut = ServiceBusTransientFailures.ClassifyTransientFailures(new Sender(new FailingServiceBusSender(failure)));

        var thrown = await Assert.ThrowsExactlyAsync<ServiceBusException>(() => sut.Send(CreateMessage(MessageType.ResolutionResponse)));
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    public async Task Sender_WithoutClassification_StillThrowsTheServiceBusException()
    {
        // Publishers keep the exception they had: classification applies to the response sender only.
        var failure = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
        var sut = new Sender(new FailingServiceBusSender(failure));

        var thrown = await Assert.ThrowsExactlyAsync<ServiceBusException>(() => sut.Send(CreateMessage(MessageType.EventRequest)));
        Assert.AreSame(failure, thrown);
    }

    [TestMethod]
    [DataRow(MessageType.ResolutionResponse, false, false)]
    [DataRow(MessageType.ErrorResponse, true, false)]
    [DataRow(MessageType.DeferralResponse, false, true)]
    public async Task StrictMessageHandler_TransientBrokerFailureSendingAResponse_AbandonsInsteadOfDeadLettering(
        MessageType failingResponse, bool handlerFails, bool sessionBlocked)
    {
        var failure = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy);
        var serviceBusSender = new FailingServiceBusSender(failure, failingResponse);
        var responseService = new ResponseService(ServiceBusTransientFailures.ClassifyTransientFailures(new Sender(serviceBusSender)));
        var sessionState = new InMemorySessionState { BlockedByEventId = sessionBlocked ? "earlier-event" : null };
        var ctx = new InMemoryMessageContext(CreateMessage(MessageType.EventRequest), sessionState);
        var handler = new DelegateEventContextHandler(handlerFails ? new InvalidOperationException("handler failed") : null);
        var sut = new StrictMessageHandler(handler, responseService, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.IsFalse(ctx.IsDeadLettered, "A broker hiccup while sending a response must not dead-letter the delivery");
        Assert.AreEqual(0, serviceBusSender.DeadLetterNotifications);
        Assert.IsTrue(ctx.IsAbandoned);
        Assert.IsFalse(ctx.IsCompleted);
        Assert.AreEqual(sessionBlocked ? "earlier-event" : null, sessionState.BlockedByEventId, "The session block must be left as it was");
    }

    [TestMethod]
    public async Task StrictMessageHandler_NonTransientBrokerFailureSendingResolutionResponse_StillDeadLetters()
    {
        var failure = new ServiceBusException("too large", ServiceBusFailureReason.MessageSizeExceeded);
        var serviceBusSender = new FailingServiceBusSender(failure, MessageType.ResolutionResponse);
        var responseService = new ResponseService(ServiceBusTransientFailures.ClassifyTransientFailures(new Sender(serviceBusSender)));
        var ctx = new InMemoryMessageContext(CreateMessage(MessageType.EventRequest), new InMemorySessionState());
        var sut = new StrictMessageHandler(new DelegateEventContextHandler(null), responseService, NullLogger.Instance);

        await sut.Handle(ctx);

        Assert.IsTrue(ctx.IsDeadLettered);
        Assert.IsFalse(ctx.IsAbandoned);
    }

    private static Message CreateMessage(MessageType messageType) => new()
    {
        To = "Billing",
        From = "Storefront",
        SessionId = "session-1",
        CorrelationId = "correlation-1",
        EventId = "event-1",
        MessageId = "message-1",
        ParentMessageId = "self",
        OriginatingMessageId = "self",
        OriginatingFrom = "Storefront",
        EventTypeId = "OrderPlaced",
        MessageType = messageType,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        },
    };

    private sealed class DelegateEventContextHandler(Exception? failure) : IEventContextHandler
    {
        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default) =>
            failure is null ? Task.CompletedTask : Task.FromException(failure);
    }

    // Fails every send of the given message type (every send when none is given) with the given
    // broker failure, as the SDK does once its own retries are spent.
    // A dead-letter notification travels as an ErrorResponse that carries a DeadLetterReason; it
    // is never failed, so a test sees whether one was sent.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class FailingServiceBusSender(ServiceBusException failure, MessageType? failOn = null) : ServiceBusSender
    {
        public int DeadLetterNotifications { get; private set; }

        public override Task SendMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, CancellationToken cancellationToken = default) =>
            Attempt(message);

        public override async Task SendMessagesAsync(IEnumerable<Azure.Messaging.ServiceBus.ServiceBusMessage> messages, CancellationToken cancellationToken = default)
        {
            foreach (var message in messages)
                await Attempt(message);
        }

        public override async Task<long> ScheduleMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            await Attempt(message);
            return 1;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task Attempt(Azure.Messaging.ServiceBus.ServiceBusMessage message)
        {
            var messageType = message.ApplicationProperties.TryGetValue(UserPropertyName.MessageType.ToString(), out var value)
                ? value as string
                : null;
            if (message.ApplicationProperties.ContainsKey(UserPropertyName.DeadLetterReason.ToString()))
            {
                DeadLetterNotifications++;
                return Task.CompletedTask;
            }

            return failOn is null || messageType == failOn.ToString()
                ? Task.FromException(failure)
                : Task.CompletedTask;
        }
    }
}
