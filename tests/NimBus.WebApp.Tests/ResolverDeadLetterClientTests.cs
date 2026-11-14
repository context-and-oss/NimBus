#pragma warning disable CA1707, CA2007

using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class ResolverDeadLetterClientTests
{
    [TestMethod]
    public void DeadLetterResubmitRequest_DefaultScopeIsInvalid()
    {
        var request = new DeadLetterResubmitRequest();

        Assert.AreNotEqual(DeadLetterResubmitRequestScope.All, request.Scope);
        Assert.AreNotEqual(DeadLetterResubmitRequestScope.Reason, request.Scope);
    }

    [TestMethod]
    public void CloneForReplay_PreservesSendableMetadataAndReplacesDeadLetterFields()
    {
        var source = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            messageId: "original-id",
            partitionKey: "partition",
            viaPartitionKey: "via",
            sessionId: "session",
            replyToSessionId: "reply-session",
            timeToLive: TimeSpan.FromMinutes(5),
            correlationId: "correlation",
            subject: "subject",
            to: "destination",
            contentType: "application/json",
            replyTo: "reply",
            properties: new Dictionary<string, object>
            {
                ["ordinary"] = "kept",
                ["DeadLetterReason"] = "CosmosDbThrottled",
                ["DeadLetterErrorDescription"] = "sensitive broker detail",
            });

        var replay = ResolverDeadLetterClient.CloneForReplay(source);

        // Spec 030 §5.7: a replayed control request must stay identifiable as the same message,
        // or the tracking store's stale-write guard cannot tell it from a genuinely new one and
        // it reopens the settled row permanently.
        Assert.AreEqual(source.MessageId, replay.MessageId);
        Assert.AreEqual("payload", replay.Body.ToString());
        Assert.AreEqual("session", replay.SessionId);
        Assert.AreEqual("reply-session", replay.ReplyToSessionId);
        Assert.AreEqual("correlation", replay.CorrelationId);
        Assert.AreEqual("subject", replay.Subject);
        Assert.AreEqual("application/json", replay.ContentType);
        Assert.AreEqual("destination", replay.To);
        Assert.AreEqual("reply", replay.ReplyTo);
        Assert.AreEqual("partition", replay.PartitionKey);
        Assert.AreEqual("via", replay.TransactionPartitionKey);
        Assert.AreEqual(TimeSpan.FromMinutes(5), replay.TimeToLive);
        Assert.AreEqual("kept", replay.ApplicationProperties["ordinary"]);
        Assert.IsFalse(replay.ApplicationProperties.ContainsKey("DeadLetterReason"));
        Assert.IsFalse(replay.ApplicationProperties.ContainsKey("DeadLetterErrorDescription"));
        Assert.AreEqual("original-id", replay.ApplicationProperties["DeadLetterOriginalMessageId"]);
        Assert.AreEqual("CosmosDbThrottled", replay.ApplicationProperties["DeadLetterOriginalReason"]);
    }

    [TestMethod]
    public void CloneForReplay_StartsAFreshThrottleRetryBudget()
    {
        // A message is dead-lettered as CosmosDbThrottled once ThrottleRetryCount + DeliveryCount
        // reaches the delivery budget, so it carries a count of about nine. Copied onto the
        // replay, that count would dead-letter it again on its first throttle, without backoff.
        var source = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData("payload"),
            messageId: "original-id",
            sessionId: "session",
            properties: new Dictionary<string, object>
            {
                ["ordinary"] = "kept",
                ["ThrottleRetryCount"] = "9",
                ["DeadLetterReason"] = "CosmosDbThrottled",
            });

        var replay = ResolverDeadLetterClient.CloneForReplay(source);

        Assert.IsFalse(replay.ApplicationProperties.ContainsKey("ThrottleRetryCount"));
        Assert.AreEqual("kept", replay.ApplicationProperties["ordinary"]);
    }
}
