#pragma warning disable CA1707
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using System;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusAdapterTests
{
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

        await sut.Handle(message, ServiceBusTestDoubles.CreateSessionActions());

        Assert.AreEqual(1, handler.CallCount);
        Assert.IsNotNull(handler.LastContext);
        Assert.AreEqual("session-1", handler.LastContext.SessionId);
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
