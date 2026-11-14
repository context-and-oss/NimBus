#pragma warning disable CA1707, CA2007

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Testing;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

/// <summary>
/// End-to-end SDK behaviour of the platform heartbeat through
/// <see cref="NimBusTestFixture"/>: a probe reaches an adapter that has registered no
/// handler for it and still comes back as a <see cref="MessageType.ResolutionResponse"/>
/// carrying the responder's SDK version.
/// </summary>
[TestClass]
public class HeartbeatAutoAnswerTests
{
    [TestMethod]
    public async Task Heartbeat_WithNoRegisteredHandler_IsAnsweredWithResolutionResponse()
    {
        var fixture = new NimBusTestFixture();
        var forwardSendTime = DateTime.UtcNow;
        await fixture.PublishBus.Send(new Message
        {
            To = "AnalyticsEndpoint",
            From = "Manager",
            OriginatingFrom = "Manager",
            SessionId = "Heartbeat",
            EventId = "heartbeat-event-1",
            MessageId = "heartbeat-message-1",
            CorrelationId = "heartbeat-message-1",
            ParentMessageId = Constants.Self,
            OriginatingMessageId = Constants.Self,
            MessageType = MessageType.EventRequest,
            EventTypeId = Heartbeat.EventTypeId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = Heartbeat.EventTypeId,
                    EventJson = JsonConvert.SerializeObject(new Heartbeat
                    {
                        ForwardSendTime = forwardSendTime,
                        Endpoint = "AnalyticsEndpoint",
                    }),
                },
            },
        });

        await fixture.DeliverAll();

        var response = fixture.ResponseBus.SentMessages.Single();
        Assert.AreEqual(MessageType.ResolutionResponse, response.MessageType,
            "An unregistered heartbeat must auto-answer, not fall through to UnsupportedResponse");
        Assert.AreEqual(Constants.ResolverId, response.To);
        Assert.AreEqual("AnalyticsEndpoint", response.From);
        Assert.AreEqual(Heartbeat.EventTypeId, response.MessageContent.EventContent!.EventTypeId);

        var heartbeat = JsonConvert.DeserializeObject<Heartbeat>(response.MessageContent.EventContent.EventJson!);
        Assert.IsNotNull(heartbeat);
        Assert.IsFalse(string.IsNullOrWhiteSpace(heartbeat.SdkVersion), "The responding adapter must report its SDK version");
        Assert.AreEqual("AnalyticsEndpoint", heartbeat.Endpoint);
        Assert.AreNotEqual(default(DateTime), heartbeat.BackwardSendTime);
    }
}
