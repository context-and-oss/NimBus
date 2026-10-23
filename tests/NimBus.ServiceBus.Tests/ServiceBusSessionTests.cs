#pragma warning disable CA1707, CS0618
using Azure.Messaging.ServiceBus;
using Grpc.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.ServiceBus;
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class ServiceBusSessionTests
{
    [TestMethod]
    public async Task SendScheduledMessageAsync_WithTopicSubscriptionEntityPath_UsesTopicSender()
    {
        var client = new RecordingServiceBusClient
        {
            CreateSenderException = new CreateSenderProbeException("sender-path-recorded")
        };
        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            client,
            "orders/subscription-a",
            "session-1");
        var message = new Azure.Messaging.ServiceBus.ServiceBusMessage("payload");

        await Assert.ThrowsExactlyAsync<CreateSenderProbeException>(() =>
            sut.SendScheduledMessageAsync(message, DateTimeOffset.UtcNow.AddMinutes(1)));

        Assert.AreEqual("orders", client.LastSenderEntityPath);
    }

    [TestMethod]
    public async Task SendScheduledMessageAsync_WithoutClientOrEntityPath_ThrowsInvalidOperationException()
    {
        var sut = new ServiceBusSession(
            ServiceBusTestDoubles.CreateMessageActions(),
            ServiceBusTestDoubles.CreateSessionActions(),
            serviceBusClient: null!,
            entityPath: null!,
            sessionId: "session-1");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            sut.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("payload"), DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    // ── Settlement failures relayed by the Functions host over gRPC ─────

    // The Status.Detail a CrmEndpoint delivery carried in production when the host lost the
    // session lock while the handler ran.
    private const string HostSessionLockLostDetail =
        "Azure.Messaging.ServiceBus.ServiceBusException: The session lock was lost. Request a new session receiver. (SessionLockLost). " +
        "For troubleshooting information, see https://aka.ms/azsdk/net/servicebus/exceptions/troubleshoot. " +
        "---> Azure.Messaging.ServiceBus.ServiceBusException: The connection was inactive for more than the allowed 60000 milliseconds " +
        "and is closed by container 'amqps://sb-eetdis-prod.servicebus.windows.net/-a7e5a1b8'. (GeneralError). " +
        "For troubleshooting information, see https://aka.ms/azsdk/net/servicebus/exceptions/troubleshoot. " +
        "--- End of inner exception stack trace --- at Azure.Messaging.ServiceBus.Amqp.AmqpReceiver.ExecuteRequest(TimeSpan timeout, AmqpRequestMessage amqpRequestMessage)";

    private static RpcException HostFailure(string detail) => new(new Status(StatusCode.Unknown, detail));

    [TestMethod]
    public async Task GetStateAsync_HostReportsSessionLockLost_ThrowsSessionLockLost()
    {
        var sut = new ServiceBusSession(new ThrowingSessionActions(HostFailure(HostSessionLockLostDetail)));

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() => sut.GetStateAsync());

        Assert.AreEqual(ServiceBusFailureReason.SessionLockLost, exception.Reason);
        Assert.IsInstanceOfType<RpcException>(exception.InnerException);
    }

    [TestMethod]
    public async Task SetStateAsync_HostReportsTransientFailure_ThrowsTransientServiceBusException()
    {
        var detail = "Azure.Messaging.ServiceBus.ServiceBusException: The operation did not complete within the allocated time. (ServiceTimeout). " +
            "For troubleshooting information, see https://aka.ms/azsdk/net/servicebus/exceptions/troubleshoot.";
        var sut = new ServiceBusSession(new ThrowingSessionActions(HostFailure(detail)));

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() => sut.SetStateAsync(new SessionState()));

        Assert.AreEqual(ServiceBusFailureReason.ServiceTimeout, exception.Reason);
        Assert.IsTrue(exception.IsTransient);
    }

    [TestMethod]
    public async Task CompleteAsync_HostReportsSessionLockLost_ThrowsSessionLockLost()
    {
        var sut = new ServiceBusSession(
            new ThrowingMessageActions(HostFailure(HostSessionLockLostDetail)),
            ServiceBusTestDoubles.CreateSessionActions(),
            serviceBusClient: null,
            entityPath: null,
            sessionId: "session-1");

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() => sut.CompleteAsync(new ServiceBusMessage(ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: "session-1"))));

        Assert.AreEqual(ServiceBusFailureReason.SessionLockLost, exception.Reason);
    }

    [TestMethod]
    public async Task GetStateAsync_HostReportsNonServiceBusFailure_RethrowsRpcException()
    {
        var failure = HostFailure("System.InvalidOperationException: something else (Oops).");
        var sut = new ServiceBusSession(new ThrowingSessionActions(failure));

        var exception = await Assert.ThrowsExactlyAsync<RpcException>(() => sut.GetStateAsync());

        Assert.AreSame(failure, exception);
    }

    [TestMethod]
    public async Task MessageContext_HostReportsSessionLockLost_ThrowsTransient()
    {
        var session = new ServiceBusSession(new ThrowingSessionActions(HostFailure(HostSessionLockLostDetail)));
        var ctx = new MessageContext(new ServiceBusMessage(ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: "session-1")), session);

        await Assert.ThrowsExactlyAsync<TransientException>(() => ctx.GetBlockedByEventId());
    }

    private sealed class ThrowingSessionActions : ServiceBusSessionMessageActions
    {
        private readonly Exception _exception;

        public ThrowingSessionActions(Exception exception) => _exception = exception;

        public override Task<BinaryData> GetSessionStateAsync(CancellationToken cancellationToken = default) => throw _exception;

        public override Task SetSessionStateAsync(BinaryData sessionState, CancellationToken cancellationToken = default) => throw _exception;
    }

    private sealed class ThrowingMessageActions : ServiceBusMessageActions
    {
        private readonly Exception _exception;

        public ThrowingMessageActions(Exception exception) => _exception = exception;

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) => throw _exception;
    }
}
