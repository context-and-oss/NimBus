#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.Resolver.Alerts;
using NimBus.Resolver.Services;
using static NimBus.Resolver.Tests.ResolverTestMessages;
using FakeCosmosDbClient = NimBus.Resolver.Tests.ResolverServiceTests.FakeCosmosDbClient;

namespace NimBus.Resolver.Tests;

/// <summary>
/// When the Resolver hands an outcome to the endpoint alert notifier: only failures and
/// dead-letters that changed the endpoint's state, only after the message is settled, and
/// never in a way that changes the settlement.
/// </summary>
[TestClass]
public class ResolverAlertTests
{
    private const string Endpoint = "AnalyticsEndpoint";

    private static FakeMessageContext ErrorResponse(string errorText = "Handler threw")
    {
        var message = CreateMessageContext(
            MessageType.ErrorResponse, to: Constants.ResolverId, from: Endpoint,
            messageId: "rsp-1", parentMessageId: "req-1");
        message.MessageContent.ErrorContent = new ErrorContent { ErrorText = errorText };
        return message;
    }

    [TestMethod]
    public async Task An_applied_failure_notifies_the_alert_subscribers_once()
    {
        var alerts = new RecordingAlertNotifier();
        var service = new ResolverService(new FakeCosmosDbClient(), alertNotifier: alerts);
        var message = ErrorResponse();

        await service.Handle(message);

        var context = alerts.Contexts.Single();
        Assert.AreEqual(Endpoint, context.EndpointId);
        Assert.AreEqual("event-1", context.EventId);
        Assert.AreEqual("OrderPlaced", context.EventTypeId);
        Assert.AreEqual("session-1", context.SessionId);
        Assert.AreEqual(ResolutionStatus.Failed, context.Status);
        Assert.AreEqual("Handler threw", context.ErrorText);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task A_dead_letter_notifies_with_the_dead_letter_description()
    {
        var alerts = new RecordingAlertNotifier();
        var service = new ResolverService(new FakeCosmosDbClient(), alertNotifier: alerts);
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        message.DeadLetterReason = "MaxDeliveryCountExceeded";
        message.DeadLetterErrorDescription = "Delivered 10 times without being settled.";

        await service.Handle(message);

        var context = alerts.Contexts.Single();
        Assert.AreEqual(ResolutionStatus.DeadLettered, context.Status);
        Assert.AreEqual(Endpoint, context.EndpointId);
        Assert.AreEqual("Delivered 10 times without being settled.", context.ErrorText);
    }

    [TestMethod]
    [DataRow(MessageType.EventRequest)]
    [DataRow(MessageType.ResolutionResponse)]
    [DataRow(MessageType.DeferralResponse)]
    [DataRow(MessageType.SkipResponse)]
    [DataRow(MessageType.UnsupportedResponse)]
    public async Task Other_outcomes_do_not_notify(MessageType messageType)
    {
        var alerts = new RecordingAlertNotifier();
        var service = new ResolverService(new FakeCosmosDbClient(), alertNotifier: alerts);
        var message = messageType == MessageType.EventRequest
            ? CreateMessageContext(messageType, to: Endpoint)
            : CreateMessageContext(messageType, to: Constants.ResolverId, from: Endpoint,
                messageId: "rsp-1", parentMessageId: "req-1");

        await service.Handle(message);

        Assert.AreEqual(0, alerts.Contexts.Count);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task A_stale_failure_that_changed_nothing_does_not_notify()
    {
        var alerts = new RecordingAlertNotifier();
        var store = new FakeCosmosDbClient { FailedUploadResult = false };
        var service = new ResolverService(store, alertNotifier: alerts);
        var message = ErrorResponse();

        await service.Handle(message);

        Assert.AreEqual(0, alerts.Contexts.Count);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task The_message_is_completed_before_the_subscribers_are_notified()
    {
        var message = ErrorResponse();
        var completedWhenNotified = -1;
        var alerts = new RecordingAlertNotifier { OnNotify = () => completedWhenNotified = message.CompletedCalls };
        var service = new ResolverService(new FakeCosmosDbClient(), alertNotifier: alerts);

        await service.Handle(message);

        Assert.AreEqual(1, completedWhenNotified);
    }

    [TestMethod]
    public async Task A_throwing_notifier_does_not_change_the_settlement()
    {
        var alerts = new RecordingAlertNotifier { Exception = new InvalidOperationException("SMTP down") };
        var service = new ResolverService(new FakeCosmosDbClient(), alertNotifier: alerts);
        var message = ErrorResponse();

        await service.Handle(message);

        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        Assert.AreEqual(0, message.AbandonCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
    }

    [TestMethod]
    public async Task Without_a_notifier_a_failure_is_handled_as_before()
    {
        var service = new ResolverService(new FakeCosmosDbClient());
        var message = ErrorResponse();

        await service.Handle(message);

        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    internal sealed class RecordingAlertNotifier : IEndpointAlertNotifier
    {
        public List<EndpointAlertContext> Contexts { get; } = new();

        public Exception? Exception { get; set; }

        public Action? OnNotify { get; set; }

        public Task NotifyAsync(EndpointAlertContext context, CancellationToken cancellationToken = default)
        {
            OnNotify?.Invoke();
            Contexts.Add(context);
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }
}
