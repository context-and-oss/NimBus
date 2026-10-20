#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using EventImplementation = NimBus.WebApp.Controllers.ApiContract.EventImplementation;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Coverage of <c>EventImplementation.LatestRequestMessageWithPayload</c> — the
/// server-side selection of the "latest request message that carries the event
/// payload" used by resubmit-with-changes to resolve the event type from the
/// request history rather than the terminal ErrorResponse (which, for a failed
/// hand-off, carries neither payload nor event type).
/// </summary>
[TestClass]
public sealed class EventImplementationResubmitPayloadTests
{
    private static MessageEntity Entity(
        MessageType type,
        string enqueuedUtc,
        string? eventJson = null,
        string? eventTypeId = null) =>
        new()
        {
            MessageType = type,
            EnqueuedTimeUtc = DateTime.Parse(enqueuedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            EventTypeId = eventTypeId!,
            MessageContent = eventJson == null
                ? null!
                : new MessageContent { EventContent = new EventContent { EventJson = eventJson, EventTypeId = eventTypeId! } },
        };

    [TestMethod]
    public void Returns_original_EventRequest_for_a_failed_handoff()
    {
        // The terminal ErrorResponse / HandoffFailedRequest carry no event JSON;
        // the original EventRequest must win.
        var history = new List<MessageEntity>
        {
            Entity(MessageType.EventRequest, "2026-06-01T23:36:50Z", eventJson: "{\"Fail\":true}", eventTypeId: "Demo.Type"),
            Entity(MessageType.PendingHandoffResponse, "2026-06-01T23:36:51Z", eventJson: "{\"Fail\":true}"),
            Entity(MessageType.HandoffFailedRequest, "2026-06-01T23:37:02Z"),
            Entity(MessageType.ErrorResponse, "2026-06-01T23:37:02Z"),
        };

        var result = EventImplementation.LatestRequestMessageWithPayload(history);

        Assert.IsNotNull(result);
        Assert.AreEqual(MessageType.EventRequest, result!.MessageType);
        Assert.AreEqual("Demo.Type", result.EventTypeId);
    }

    [TestMethod]
    public void Prefers_the_latest_payload_carrying_request()
    {
        var history = new List<MessageEntity>
        {
            Entity(MessageType.EventRequest, "2026-06-01T10:00:00Z", eventJson: "{\"v\":1}"),
            Entity(MessageType.ResubmissionRequest, "2026-06-01T11:00:00Z", eventJson: "{\"v\":2}"),
        };

        var result = EventImplementation.LatestRequestMessageWithPayload(history);

        Assert.AreEqual(MessageType.ResubmissionRequest, result!.MessageType);
        Assert.AreEqual("{\"v\":2}", result.MessageContent.EventContent!.EventJson);
    }

    [TestMethod]
    public void Ignores_responses_even_when_they_carry_content()
    {
        var history = new List<MessageEntity>
        {
            Entity(MessageType.EventRequest, "2026-06-01T10:00:00Z", eventJson: "{\"req\":true}"),
            Entity(MessageType.ErrorResponse, "2026-06-01T10:00:01Z", eventJson: "{\"resp\":true}"),
        };

        var result = EventImplementation.LatestRequestMessageWithPayload(history);

        Assert.AreEqual(MessageType.EventRequest, result!.MessageType);
    }

    [TestMethod]
    public void Skips_payload_less_requests()
    {
        // A newer request without event JSON (e.g. a control retry) must not
        // shadow the older request that actually carries the payload.
        var history = new List<MessageEntity>
        {
            Entity(MessageType.EventRequest, "2026-06-01T10:00:00Z", eventJson: "{\"req\":true}"),
            Entity(MessageType.RetryRequest, "2026-06-01T10:05:00Z"),
        };

        var result = EventImplementation.LatestRequestMessageWithPayload(history);

        Assert.AreEqual(MessageType.EventRequest, result!.MessageType);
    }

    [TestMethod]
    public void Counts_ProcessDeferredRequest_as_a_payload_source()
    {
        var history = new List<MessageEntity>
        {
            Entity(MessageType.EventRequest, "2026-06-01T10:00:00Z", eventJson: "{\"v\":1}"),
            Entity(MessageType.ProcessDeferredRequest, "2026-06-01T10:10:00Z", eventJson: "{\"v\":2}"),
        };

        var result = EventImplementation.LatestRequestMessageWithPayload(history);

        Assert.AreEqual(MessageType.ProcessDeferredRequest, result!.MessageType);
    }

    [TestMethod]
    public void Returns_null_when_no_request_carries_a_payload()
    {
        var history = new List<MessageEntity>
        {
            Entity(MessageType.HandoffCompletedRequest, "2026-06-01T10:05:00Z", eventJson: "{\"batchId\":\"abc\"}"),
            Entity(MessageType.ResolutionResponse, "2026-06-01T10:05:01Z"),
        };

        Assert.IsNull(EventImplementation.LatestRequestMessageWithPayload(history));
        Assert.IsNull(EventImplementation.LatestRequestMessageWithPayload(new List<MessageEntity>()));
    }
}
