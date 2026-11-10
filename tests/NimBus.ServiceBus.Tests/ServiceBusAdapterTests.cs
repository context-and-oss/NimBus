#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using NimBus.Testing;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusAdapterTests
{
    private const string ExpectedDeprecationMessage =
        "ServiceBusSessionMessageActions cannot complete or dead-letter messages. Bind ServiceBusMessageActions in the trigger and call Handle(message, messageActions, sessionActions, cancellationToken). This overload will be removed in v5.";

    [TestMethod]
    public void TwoArgumentHandle_IsObsoleteWithReplacementGuidance()
    {
        var twoArgumentSignature = new[] { typeof(ServiceBusReceivedMessage), typeof(ServiceBusSessionMessageActions), typeof(CancellationToken) };

        foreach (var type in new[] { typeof(IServiceBusAdapter), typeof(ServiceBusAdapter), typeof(NimBus.SDK.SubscriberClient) })
        {
            var method = type.GetMethod(nameof(IServiceBusAdapter.Handle), twoArgumentSignature);
            Assert.IsNotNull(method, $"{type.Name} should declare the two-argument Handle overload.");

            var obsolete = method.GetCustomAttribute<ObsoleteAttribute>();
            Assert.IsNotNull(obsolete, $"{type.Name}.Handle(message, sessionActions, ct) should be [Obsolete].");
            Assert.AreEqual(ExpectedDeprecationMessage, obsolete.Message, $"{type.Name} deprecation message");
            Assert.IsFalse(obsolete.IsError, $"{type.Name} deprecation must be a warning, not an error, in v4.x.");
        }

        var threeArgument = typeof(IServiceBusAdapter).GetMethod(
            nameof(IServiceBusAdapter.Handle),
            new[] { typeof(ServiceBusReceivedMessage), typeof(ServiceBusMessageActions), typeof(ServiceBusSessionMessageActions), typeof(CancellationToken) });
        Assert.IsNotNull(threeArgument);
        Assert.IsNull(threeArgument.GetCustomAttribute<ObsoleteAttribute>(), "The three-argument overload is the supported replacement.");
    }

    [TestMethod]
    public async Task Handle_WithMessageAndSessionActions_BuildsContextAndInvokesHandler()
    {
        var handler = new RecordingMessageHandler();
        var sut = new ServiceBusAdapter(handler, new RecordingServiceBusClient(), "orders/subscription-a");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "message-1", sessionId: "session-1");

        await sut.Handle(message, ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions());

        Assert.AreEqual(1, handler.CallCount);
        Assert.IsNotNull(handler.LastContext);
        Assert.AreEqual("session-1", handler.LastContext.SessionId);
    }

    [TestMethod]
    public async Task Handle_WithSessionActionsOnly_BuildsContextAndInvokesHandler()
    {
        var handler = new RecordingMessageHandler();
        var sut = new ServiceBusAdapter(handler, new RecordingServiceBusClient(), "orders/subscription-a");
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "message-1", sessionId: "session-1");

#pragma warning disable CS0618 // Exercises the obsolete two-argument overload, kept until v5.
        await sut.Handle(message, ServiceBusTestDoubles.CreateSessionActions());
#pragma warning restore CS0618

        Assert.AreEqual(1, handler.CallCount);
        Assert.IsNotNull(handler.LastContext);
        Assert.AreEqual("session-1", handler.LastContext.SessionId);
    }

    // Characterization test: pins the defective v4 behaviour that is the reason the two-argument
    // overload is deprecated. ServiceBusSessionMessageActions cannot complete the message, so a
    // successful handler run is followed by a dead-letter ErrorResponse and the failure is
    // swallowed. Replaced when the overload is removed in v5.
    [TestMethod]
    public async Task Handle_WithSessionActionsOnly_CannotSettle_ReportsSuccessfulMessageAsDeadLettered()
    {
        var bus = new InMemoryMessageBus();
        var eventHandler = new CountingEventContextHandler();
        var messageHandler = new StrictMessageHandler(eventHandler, new ResponseService(bus), NullLogger.Instance);
        var sut = new ServiceBusAdapter(messageHandler, new RecordingServiceBusClient(), "orders/subscription-a");
        var message = CreateValidEventRequest();

#pragma warning disable CS0618 // Pins the behaviour of the obsolete two-argument overload, kept until v5.
        await sut.Handle(message, new StateCapableSessionActions());
#pragma warning restore CS0618

        Assert.AreEqual(1, eventHandler.HandleCalls);
        Assert.HasCount(2, bus.SentMessages);
        Assert.AreEqual(MessageType.ResolutionResponse, bus.SentMessages[0].MessageType);
        Assert.IsNull(bus.SentMessages[0].DeadLetterReason);
        Assert.AreEqual(MessageType.ErrorResponse, bus.SentMessages[1].MessageType);
        Assert.AreEqual("Failed to handle message.", bus.SentMessages[1].DeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_WithSessionReceiver_BuildsContextAndInvokesHandler()
    {
        var handler = new RecordingMessageHandler();
        var sut = new ServiceBusAdapter(handler);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "message-1", sessionId: "session-1");

        await sut.Handle(message, new RecordingServiceBusSessionReceiver());

        Assert.AreEqual(1, handler.CallCount);
        Assert.IsNotNull(handler.LastContext);
        Assert.AreEqual("session-1", handler.LastContext.SessionId);
    }

    [TestMethod]
    public async Task Handle_WhenHandlerThrows_PropagatesException()
    {
        var sut = new ServiceBusAdapter(new ThrowingMessageHandler());
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: "message-1", sessionId: "session-1");

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            sut.Handle(message, new RecordingServiceBusSessionReceiver()));

        Assert.AreEqual("boom", exception.Message);
    }

    private static ServiceBusReceivedMessage CreateValidEventRequest()
    {
        var content = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" } };

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(JsonConvert.SerializeObject(content)),
            messageId: "msg-1",
            sessionId: "session-1",
            properties: new Dictionary<string, object>
            {
                { "EventId", "event-1" },
                { "To", "orders" },
                { "From", "StorefrontEndpoint" },
                { "MessageType", "EventRequest" },
                { "OriginatingMessageId", "self" },
                { "ParentMessageId", "self" },
                { "OriginatingFrom", "StorefrontEndpoint" },
                { "EventTypeId", "OrderPlaced" },
                { "RetryCount", 0 },
            });
    }

    private sealed class CountingEventContextHandler : IEventContextHandler
    {
        public int HandleCalls { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMessageHandler : IMessageHandler
    {
        public int CallCount { get; private set; }
        public IMessageContext? LastContext { get; private set; }

        public Task Handle(IMessageContext messageContext, System.Threading.CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastContext = messageContext;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingMessageHandler : IMessageHandler
    {
        public Task Handle(IMessageContext messageContext, System.Threading.CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
