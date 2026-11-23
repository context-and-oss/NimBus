#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IAlertSignalStore"/> (Spec 053 Phase 2). The
/// store under test also implements <see cref="IMessageTrackingStore"/>, which writes the messages
/// and events read back. Every test scopes its endpoints by a unique id, so a shared backend may
/// hold other rows.
/// </summary>
/// <remarks>
/// Window bounds are taken from the stored <c>EnqueuedTimeUtc</c> read back from the store, since a
/// provider may round it on write, and compared by ticks.
/// </remarks>
[TestClass]
public abstract class AlertSignalStoreConformanceTests
{
    // A fraction of a second with a different number of significant digits from the times below,
    // so a provider that compares stored time strings instead of instants gets the order wrong.
    private static readonly DateTime T0 = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _scope = Guid.NewGuid().ToString("N")[..12];
    private int _sequence;

    /// <summary>A freshly initialized store that also implements <see cref="IMessageTrackingStore"/>.</summary>
    protected abstract IAlertSignalStore CreateStore();

    private string Endpoint(string suffix = "a") => $"signal-ep-{_scope}-{suffix}";

    private string NextId(string prefix) => $"{prefix}-{_scope}-{++_sequence}";

    private static IMessageTrackingStore Tracking(IAlertSignalStore store) => (IMessageTrackingStore)store;

    // ───────── CountFailedAttempts ─────────

    [TestMethod]
    public async Task CountFailedAttempts_counts_the_window_from_inclusive_to_exclusive()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        var before = await Store(store, endpoint, MessageType.ErrorResponse, T0.AddMinutes(-5));
        // Earlier in the same second as the window's start: outside the window.
        var sameSecondBefore = await Store(store, endpoint, MessageType.ErrorResponse, T0.AddTicks(-734567));
        var start = await Store(store, endpoint, MessageType.ErrorResponse, T0);
        var inside = await Store(store, endpoint, MessageType.ErrorResponse, T0.AddMinutes(1).AddTicks(4000000));
        var end = await Store(store, endpoint, MessageType.ErrorResponse, T0.AddMinutes(2));
        var after = await Store(store, endpoint, MessageType.ErrorResponse, T0.AddMinutes(2).AddTicks(5000000));

        Assert.IsTrue(before < sameSecondBefore && sameSecondBefore < start && start < inside && inside < end && end < after);
        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint, start, end, null, null, 100), "[start, end) holds start and inside.");
        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint, start.AddTicks(1), end.AddTicks(1), null, null, 100), "(start, end] holds inside and end.");
        Assert.AreEqual(6, await store.CountFailedAttempts(endpoint, before, after.AddTicks(1), null, null, 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, sameSecondBefore, start, null, null, 100));
        Assert.AreEqual(0, await store.CountFailedAttempts(endpoint, start, start, null, null, 100), "An empty window counts nothing.");
        Assert.AreEqual(0, await store.CountFailedAttempts(endpoint, end, start, null, null, 100), "A reversed window counts nothing.");
    }

    [TestMethod]
    public async Task CountFailedAttempts_matches_the_endpoint_exactly()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Store(store, endpoint, MessageType.ErrorResponse, T0);
        await Store(store, endpoint + "-b", MessageType.ErrorResponse, T0);
        await Store(store, endpoint + "-b", MessageType.ErrorResponse, T0.AddSeconds(1));

        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, T0.AddHours(-1), T0.AddHours(1), null, null, 100));
        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint + "-b", T0.AddHours(-1), T0.AddHours(1), null, null, 100));
        Assert.AreEqual(0, await store.CountFailedAttempts(Endpoint("unknown"), T0.AddHours(-1), T0.AddHours(1), null, null, 100));
    }

    [TestMethod]
    public async Task CountFailedAttempts_counts_only_error_responses()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Store(store, endpoint, MessageType.EventRequest, T0);
        await Store(store, endpoint, MessageType.ResolutionResponse, T0.AddSeconds(1));
        await Store(store, endpoint, MessageType.DeferralResponse, T0.AddSeconds(2));
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(3));
        // A dead-letter is stored as an ErrorResponse carrying the dead-letter description.
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(4), deadLetterDescription: "Max delivery count exceeded");

        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint, T0.AddHours(-1), T0.AddHours(1), null, null, 100));
    }

    [TestMethod]
    public async Task CountFailedAttempts_filters_by_event_type()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Store(store, endpoint, MessageType.ErrorResponse, T0, eventTypeId: "OrderPlaced");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(1), eventTypeId: "OrderPlaced");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(2), eventTypeId: "OrderCancelled");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(3), eventTypeId: "CustomerChanged");
        var from = T0.AddHours(-1);
        var to = T0.AddHours(1);

        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint, from, to, ["OrderPlaced"], null, 100));
        Assert.AreEqual(3, await store.CountFailedAttempts(endpoint, from, to, ["OrderPlaced", "OrderCancelled"], null, 100));
        Assert.AreEqual(0, await store.CountFailedAttempts(endpoint, from, to, ["Unknown"], null, 100));
        Assert.AreEqual(4, await store.CountFailedAttempts(endpoint, from, to, [], null, 100), "An empty list counts every type.");
        Assert.AreEqual(4, await store.CountFailedAttempts(endpoint, from, to, null, null, 100));
    }

    [TestMethod]
    public async Task CountFailedAttempts_filters_by_a_case_insensitive_error_text_substring()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Store(store, endpoint, MessageType.ErrorResponse, T0, errorText: "Downstream TIMEOUT after 30 s");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(1), errorText: "Validation failed: name is required");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(2));
        var from = T0.AddHours(-1);
        var to = T0.AddHours(1);

        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "timeout", 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "TimeOut After", 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "NAME IS", 100));
        Assert.AreEqual(0, await store.CountFailedAttempts(endpoint, from, to, null, "deadlock", 100));
        Assert.AreEqual(3, await store.CountFailedAttempts(endpoint, from, to, null, null, 100));
        Assert.AreEqual(3, await store.CountFailedAttempts(endpoint, from, to, null, "", 100), "An empty filter matches everything.");
    }

    [TestMethod]
    public async Task CountFailedAttempts_reads_the_dead_letter_description_only_when_the_error_text_is_empty()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        // No error content: the dead-letter description is the error text.
        await Store(store, endpoint, MessageType.ErrorResponse, T0, deadLetterDescription: "Lock lost: TIMEOUT");
        // Empty error text: the same.
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(1), errorText: "", deadLetterDescription: "timeout while settling");
        // An error text that doesn't match hides a dead-letter description that would.
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(2), errorText: "Validation failed", deadLetterDescription: "timeout");
        // An error text that matches counts whatever the description says.
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(3), errorText: "Gateway timeout", deadLetterDescription: "Validation failed");
        var from = T0.AddHours(-1);
        var to = T0.AddHours(1);

        Assert.AreEqual(3, await store.CountFailedAttempts(endpoint, from, to, null, "timeout", 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "validation", 100));
    }

    [TestMethod]
    public async Task CountFailedAttempts_treats_wildcard_characters_literally()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Store(store, endpoint, MessageType.ErrorResponse, T0, errorText: "Disk 50% full");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(1), errorText: "Disk 500 full");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(2), errorText: "Column order_id is null");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(3), errorText: "Column orderXid is null");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(4), errorText: @"Path C:\data\[x] not found");
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(5), errorText: "Path x not found");
        var from = T0.AddHours(-1);
        var to = T0.AddHours(1);

        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "50%", 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "order_id", 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, "[x]", 100));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, @"\data\", 100));
    }

    [TestMethod]
    public async Task CountFailedAttempts_stops_at_the_cap()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        for (var i = 0; i < 5; i++)
            await Store(store, endpoint, MessageType.ErrorResponse, T0.AddMilliseconds(150 * i));
        var from = T0.AddHours(-1);
        var to = T0.AddHours(1);

        Assert.AreEqual(3, await store.CountFailedAttempts(endpoint, from, to, null, null, 3));
        Assert.AreEqual(1, await store.CountFailedAttempts(endpoint, from, to, null, null, 1));
        Assert.AreEqual(5, await store.CountFailedAttempts(endpoint, from, to, null, null, 5));
        Assert.AreEqual(5, await store.CountFailedAttempts(endpoint, from, to, null, null, 50));
    }

    [TestMethod]
    public async Task CountFailedAttempts_applies_the_cap_after_the_exact_window()
    {
        // Attempts just outside the window, in the seconds that hold its bounds, must not use up
        // the cap: a provider that reads whole seconds and filters afterwards has to read past them.
        var store = CreateStore();
        var endpoint = Endpoint();
        var start = await Store(store, endpoint, MessageType.ErrorResponse, T0);
        for (var i = 1; i <= 4; i++)
            await Store(store, endpoint, MessageType.ErrorResponse, T0.AddTicks(-200000 * i));
        var end = await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(3));
        for (var i = 1; i <= 4; i++)
            await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(3).AddTicks(150000 * i));
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddSeconds(1.5));

        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint, start, end, null, null, 2));
        Assert.AreEqual(2, await store.CountFailedAttempts(endpoint, start, end, null, null, 3));
    }

    [TestMethod]
    public async Task CountFailedAttempts_rejects_a_cap_below_one()
    {
        var store = CreateStore();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            () => store.CountFailedAttempts(Endpoint(), T0, T0.AddHours(1), null, null, 0));
    }

    // ───────── GetOldestBlockingEvent ─────────

    [TestMethod]
    public async Task GetOldestBlockingEvent_returns_the_oldest_failed_or_handoff_event()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        var failedId = NextId("evt");
        var handoffId = NextId("evt");
        await Upload(store, endpoint, failedId, "s-failed", ResolutionStatus.Failed);
        await Task.Delay(30);
        await Upload(store, endpoint, handoffId, "s-handoff", ResolutionStatus.Pending, handoff: true);
        await Task.Delay(30);
        await Upload(store, endpoint, NextId("evt"), "s-later", ResolutionStatus.Failed);

        var oldest = await store.GetOldestBlockingEvent(endpoint);

        Assert.IsNotNull(oldest);
        Assert.AreEqual(failedId, oldest.EventId);
        Assert.AreEqual("s-failed", oldest.SessionId);
        Assert.AreEqual(ResolutionStatus.Failed, oldest.Status);
        var stored = await Tracking(store).GetFailedEvent(endpoint, failedId, "s-failed");
        Assert.AreEqual(Utc(stored!.UpdatedAt).Ticks, oldest.UpdatedAtUtc.Ticks);
        Assert.AreEqual(DateTimeKind.Utc, oldest.UpdatedAtUtc.Kind);

        // Once the failure is gone, the pending hand-off is the oldest.
        await Tracking(store).RemoveMessage(failedId, "s-failed", endpoint);
        oldest = await store.GetOldestBlockingEvent(endpoint);

        Assert.IsNotNull(oldest);
        Assert.AreEqual(handoffId, oldest.EventId);
        Assert.AreEqual("s-handoff", oldest.SessionId);
        Assert.AreEqual(ResolutionStatus.Pending, oldest.Status);
    }

    [TestMethod]
    public async Task GetOldestBlockingEvent_leaves_out_events_that_do_not_block()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Upload(store, endpoint, NextId("evt"), "s-pending", ResolutionStatus.Pending);
        await Upload(store, endpoint, NextId("evt"), "s-deferred", ResolutionStatus.Deferred);
        await Upload(store, endpoint, NextId("evt"), "s-dead", ResolutionStatus.DeadLettered);
        await Upload(store, endpoint, NextId("evt"), "s-unsupported", ResolutionStatus.Unsupported);
        await Upload(store, endpoint, NextId("evt"), "s-completed", ResolutionStatus.Completed);
        var deletedId = NextId("evt");
        await Upload(store, endpoint, deletedId, "s-deleted", ResolutionStatus.Failed);
        await Tracking(store).RemoveMessage(deletedId, "s-deleted", endpoint);
        // Another endpoint's failure, under an id this endpoint's id is a prefix of.
        await Upload(store, endpoint + "-b", NextId("evt"), "s-other", ResolutionStatus.Failed);

        Assert.IsNull(await store.GetOldestBlockingEvent(endpoint));
        Assert.IsNotNull(await store.GetOldestBlockingEvent(endpoint + "-b"));
    }

    [TestMethod]
    public async Task GetOldestBlockingEvent_returns_null_for_an_endpoint_with_no_events()
    {
        var store = CreateStore();

        Assert.IsNull(await store.GetOldestBlockingEvent(Endpoint("empty")));
    }

    // ───────── HasCompletedMessageSince ─────────

    [TestMethod]
    public async Task HasCompletedMessageSince_includes_a_completion_at_the_bound()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        var completed = await Store(store, endpoint, MessageType.ResolutionResponse, T0);

        Assert.IsTrue(await store.HasCompletedMessageSince(endpoint, completed));
        Assert.IsTrue(await store.HasCompletedMessageSince(endpoint, completed.AddMinutes(-10)));
        Assert.IsFalse(await store.HasCompletedMessageSince(endpoint, completed.AddTicks(1)));
        Assert.IsFalse(await store.HasCompletedMessageSince(endpoint, completed.AddMinutes(10)));
    }

    [TestMethod]
    public async Task HasCompletedMessageSince_counts_only_this_endpoints_completions()
    {
        var store = CreateStore();
        var endpoint = Endpoint();
        await Store(store, endpoint, MessageType.ResolutionResponse, T0.AddMinutes(-30));
        await Store(store, endpoint, MessageType.EventRequest, T0.AddMinutes(1));
        await Store(store, endpoint, MessageType.ErrorResponse, T0.AddMinutes(2));
        await Store(store, endpoint, MessageType.SkipResponse, T0.AddMinutes(3));
        await Store(store, endpoint + "-b", MessageType.ResolutionResponse, T0.AddMinutes(4));

        Assert.IsFalse(await store.HasCompletedMessageSince(endpoint, T0));
        Assert.IsTrue(await store.HasCompletedMessageSince(endpoint + "-b", T0));
        Assert.IsFalse(await store.HasCompletedMessageSince(Endpoint("unknown"), T0.AddHours(-1)));
    }

    // ───────── Helpers ─────────

    // Stores a message and returns its EnqueuedTimeUtc as the store holds it.
    private async Task<DateTime> Store(
        IAlertSignalStore store,
        string endpointId,
        MessageType messageType,
        DateTime enqueuedTimeUtc,
        string eventTypeId = "OrderPlaced",
        string? errorText = null,
        string? deadLetterDescription = null)
    {
        var eventId = NextId("evt");
        var messageId = NextId("msg");
        await Tracking(store).StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = messageId,
            EndpointId = endpointId,
            SessionId = "session-1",
            CorrelationId = "corr-1",
            EventTypeId = eventTypeId,
            EnqueuedTimeUtc = enqueuedTimeUtc,
            MessageType = messageType,
            EndpointRole = EndpointRole.Subscriber,
            From = "publisher",
            To = endpointId,
            MessageContent = new MessageContent
            {
                ErrorContent = errorText == null ? null : new ErrorContent { ErrorText = errorText },
            },
            DeadLetterErrorDescription = deadLetterDescription,
        });

        var stored = await Tracking(store).GetMessage(eventId, messageId);
        Assert.IsNotNull(stored, "The message was not stored.");
        return Utc(stored.EnqueuedTimeUtc);
    }

    private static async Task Upload(IAlertSignalStore store, string endpointId, string eventId, string sessionId, ResolutionStatus status, bool handoff = false)
    {
        var content = new UnresolvedEvent
        {
            EventId = eventId,
            SessionId = sessionId,
            EndpointId = endpointId,
            EnqueuedTimeUtc = DateTime.UtcNow,
            // Providers that keep the caller's UpdatedAt see the write order too.
            UpdatedAt = DateTime.UtcNow,
            CorrelationId = "corr-1",
            EndpointRole = EndpointRole.Subscriber,
            MessageType = status == ResolutionStatus.Failed ? MessageType.ErrorResponse : MessageType.EventRequest,
            EventTypeId = "OrderPlaced",
            To = endpointId,
            From = "publisher",
            ResolutionStatus = status,
            PendingSubStatus = handoff ? "Handoff" : null,
            MessageContent = new MessageContent(),
        };

        var tracking = Tracking(store);
        var written = status switch
        {
            ResolutionStatus.Pending => await tracking.UploadPendingMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Deferred => await tracking.UploadDeferredMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Failed => await tracking.UploadFailedMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.DeadLettered => await tracking.UploadDeadletteredMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Unsupported => await tracking.UploadUnsupportedMessage(eventId, sessionId, endpointId, content),
            ResolutionStatus.Completed => await tracking.UploadCompletedMessage(eventId, sessionId, endpointId, content),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        Assert.IsTrue(written, $"The {status} event was not written.");
    }

    // SQL Server reads datetime2 back as DateTimeKind.Unspecified; the value is UTC.
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
