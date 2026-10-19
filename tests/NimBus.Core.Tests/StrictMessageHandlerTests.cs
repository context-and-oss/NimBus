#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;

namespace NimBus.Core.Tests;

[TestClass]
public class StrictMessageHandlerTests
{
    // ── HandleEventRequest ──────────────────────────────────────────────

    [TestMethod]
    public async Task HandleEventRequest_HandlerFailure_NotifiesObserverWithOriginalException()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var failure = new InvalidOperationException("handler failure");
        var observer = new RecordingLifecycleObserver();
        var response = new FakeResponseService();
        var sut = new StrictMessageHandler(
            new FakeEventContextHandler { ThrowOnHandle = failure }, response, NullLogger.Instance,
            retryPolicyProvider: null, pipeline: null, lifecycleNotifier: new MessageLifecycleNotifier([observer]));

        await sut.Handle(ctx);

        Assert.AreEqual(1, observer.FailedCalls);
        Assert.AreSame(failure, observer.LastFailure);
        Assert.AreEqual(0, observer.CompletedCalls);
        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.BlockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_NormalEvent_HandlesAndCompletes()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        ctx.Trace = trace;
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder("event-1.handler", "event-1.response-resolution", "event-1.complete");
    }

    [TestMethod]
    public async Task HandleEventRequest_Duplicate_SendsDuplicateResponseAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.DuplicateDetected,
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, response.PendingHandoffCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_EventHandlerNotFound_SendsUnsupportedAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler { ThrowOnHandle = new EventHandlerNotFoundException("not found") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.UnsupportedCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_SessionBlocked_SendsDeferralAndDefersToSubscription()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        ctx.BlockedByEventId = "other-event";
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        // SessionBlockedException is caught by base MessageHandler (swallowed)
        await sut.Handle(ctx);

        Assert.AreEqual(1, response.DeferralCalls);
        Assert.AreEqual(1, response.SendToDeferredSubscriptionCalls);
        Assert.AreEqual(1, ctx.IncrementDeferredCountCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.response-deferral",
            "event-1.response-deferred-forward",
            "event-1.deferred-count-increment",
            "event-1.complete");
        trace.AssertAbsent("event-1.handler", "event-1.response-resolution");
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrows_SendsErrorBlocksSessionAndCompletes()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        var handler = new FakeEventContextHandler(trace) { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        // EventContextHandlerException is caught by base MessageHandler (swallowed)
        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.BlockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder("event-1.handler", "event-1.response-error", "event-1.block", "event-1.complete");
        trace.AssertAbsent("event-1.unblock", "event-1.response-resolution");
    }

    [TestMethod]
    public async Task HandleEventRequest_TransientException_AbandonsMessage()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler { ThrowOnHandle = new TransientException("transient") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.AbandonCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_CallerCancellation_PropagatesWithoutFailureSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new OperationCanceledException(cancellation.Token),
        };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => sut.Handle(ctx, cancellation.Token));

        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, response.DeadLetterCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
        Assert.AreEqual(0, ctx.AbandonCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_PermanentPayloadFailure_DeadLettersWithoutRetry()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new PermanentFailureException(new FormatException("Malformed payload.")),
        };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(1, response.DeadLetterCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Discard_CompletesWithoutDeadLetterOrRetryAndNotifiesLifecycle()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var failure = new InvalidOperationException("Known bad event version.");
        var handler = new FakeEventContextHandler { ThrowOnHandle = failure };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var classifier = new FakeFailureDispositionClassifier(FailureDisposition.Discard);
        var observer = new RecordingLifecycleObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryProvider,
            pipeline: null,
            lifecycleNotifier: notifier,
            failureDispositionClassifier: classifier);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls);
        Assert.AreEqual(1, response.DiscardCalls);
        Assert.AreSame(failure, response.LastDiscardException);
        Assert.AreEqual(typeof(FakeFailureDispositionClassifier).FullName, response.LastDiscardClassifierName);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
        Assert.AreSame(failure, classifier.LastException);
        Assert.AreEqual(ctx.EventTypeId, classifier.LastEventTypeId);
        Assert.AreEqual(ctx.To, classifier.LastEndpointName);
        Assert.AreEqual(1, observer.ReceivedCalls);
        Assert.AreEqual(1, observer.CompletedCalls);
        Assert.AreEqual(0, observer.FailedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_Discard_DoesNotBlockNextMessageInSession()
    {
        var first = CreateContext(messageType: MessageType.EventRequest, eventId: "event-1");
        var second = CreateContext(messageType: MessageType.EventRequest, eventId: "event-2");
        var attempts = 0;
        var handler = new FakeEventContextHandler
        {
            OnHandle = _ =>
            {
                attempts++;
                if (attempts == 1)
                    throw new InvalidOperationException("discard first");
            },
        };
        var response = new FakeResponseService();
        var classifier = new FakeFailureDispositionClassifier(FailureDisposition.Discard);
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            failureDispositionClassifier: classifier);

        await sut.Handle(first);
        await sut.Handle(second);

        Assert.AreEqual(first.SessionId, second.SessionId);
        Assert.AreEqual(2, handler.HandleCalls);
        Assert.AreEqual(1, first.CompletedCalls);
        Assert.AreEqual(0, first.BlockSessionCalls);
        Assert.AreEqual(1, second.CompletedCalls);
        Assert.AreEqual(1, response.DiscardCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_Discard_UnblocksSessionAndContinuesDeferredMessages()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 1;
        var handler = new FakeEventContextHandler(trace)
        {
            ThrowOnHandle = new InvalidOperationException("discard retry"),
        };
        var response = new FakeResponseService(trace);
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            failureDispositionClassifier: new FakeFailureDispositionClassifier(FailureDisposition.Discard));

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
        Assert.AreEqual(1, response.DiscardCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.unblock",
            "event-1.response-process-deferred",
            "event-1.response-discard",
            "event-1.complete");
    }

    [TestMethod]
    public async Task HandleEventRequest_DeadLetterDisposition_DeadLettersWithoutRetry()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            ThrowOnHandle = new InvalidOperationException("permanent"),
        };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 3 },
        };
        var sut = new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryProvider,
            failureDispositionClassifier: new FakeFailureDispositionClassifier(FailureDisposition.DeadLetter));

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(1, response.DeadLetterCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, response.RetryCalls);
        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_UnsupportedDisposition_DeadLettersWithOriginalExceptionAsInner()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var failure = new InvalidOperationException("handler failure");
        var observer = new RecordingLifecycleObserver();
        var sut = new StrictMessageHandler(
            new FakeEventContextHandler { ThrowOnHandle = failure },
            new FakeResponseService(),
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: new MessageLifecycleNotifier([observer]),
            failureDispositionClassifier: new FakeFailureDispositionClassifier((FailureDisposition)42));

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.IsInstanceOfType<InvalidOperationException>(observer.LastFailure);
        Assert.AreSame(failure, observer.LastFailure.InnerException);
    }

    // ── PendingHandoff outcome ──────────────────────────────────────────

    [TestMethod]
    public async Task HandleEventRequest_HandlerSignalsPendingHandoff_SendsHandoffResponseAndBlocksSession()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        var metadata = new HandoffMetadata("DMF import in flight", "JOB-42", TimeSpan.FromMinutes(5));
        var handler = new FakeEventContextHandler(trace)
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = metadata;
            },
        };
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.PendingHandoffCalls, "PendingHandoffResponse should fire");
        Assert.AreEqual(0, response.ResolutionCalls, "ResolutionResponse must NOT fire when handler signals PendingHandoff");
        Assert.AreSame(metadata, response.LastPendingHandoffMetadata);
        Assert.AreEqual(1, ctx.BlockSessionCalls, "Session must be blocked so siblings defer");
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.response-pending-handoff",
            "event-1.block",
            "event-1.complete");
        trace.AssertAbsent("event-1.response-resolution", "event-1.unblock");
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerSignalsPendingHandoffThenThrows_FailurePathWins()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = new HandoffMetadata("about to fail", null!, null);
            },
            ThrowOnHandle = new InvalidOperationException("boom"),
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.PendingHandoffCalls, "PendingHandoff must not fire when handler throws");
        Assert.AreEqual(1, response.ErrorCalls, "Failure path wins");
        Assert.AreEqual(1, ctx.BlockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerCallsMarkPendingHandoffTwice_LastCallWins()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var first = new HandoffMetadata("first", "JOB-1", TimeSpan.FromMinutes(1));
        var second = new HandoffMetadata("second", "JOB-2", TimeSpan.FromMinutes(10));
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = first;
                // Simulate a second MarkPendingHandoff call — last write wins.
                c.HandoffMetadata = second;
            },
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.PendingHandoffCalls);
        Assert.AreSame(second, response.LastPendingHandoffMetadata, "Second MarkPendingHandoff call must overwrite the first");
        Assert.AreSame(second, ctx.HandoffMetadata);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_HandlerSignalsPendingHandoff_ParksAndKeepsSessionBlocked()
    {
        // Resubmitting a failed handoff message must park the event as
        // Pending+Handoff ("Awaiting External"), not flip it to Completed.
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var metadata = new HandoffMetadata("Awaiting external batch", "DMF-42", TimeSpan.FromMinutes(5));
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = metadata;
            },
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.PendingHandoffCalls, "PendingHandoffResponse should fire");
        Assert.AreSame(metadata, response.LastPendingHandoffMetadata);
        Assert.AreEqual(1, ctx.BlockSessionCalls, "Session must be blocked so siblings defer");
        Assert.AreEqual(1, ctx.CompletedCalls);
        // The session must stay blocked so the Manager's settlement can land.
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Session must stay blocked for the later settlement");
        Assert.AreEqual(0, response.ResolutionCalls, "ResolutionResponse must NOT fire when handler signals PendingHandoff");
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_HandlerSignalsPendingHandoff_DoesNotReleaseDeferredSiblings()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 3;
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = new HandoffMetadata("r", null!, null);
            },
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.ProcessDeferredCalls, "FIFO siblings must keep deferring while the handoff is pending");
        Assert.AreEqual(0, response.ContinuationCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_PendingHandoffWhileBlockedByOtherEvent_DoesNotStealSessionBlock()
    {
        // A stale resubmission for event A while event B owns the session must
        // not overwrite BlockedByEventId (that would strand B's settlement and
        // deferred work). The row still goes Pending+Handoff — the external
        // work is genuinely in flight — but the block is left alone.
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager", eventId: "event-a");
        ctx.Trace = trace;
        ctx.BlockedByEventId = "event-b";
        ctx.IsSessionBlockedByThisResult = false;
        var handler = new FakeEventContextHandler(trace)
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = new HandoffMetadata("r", null!, null);
            },
        };
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.PendingHandoffCalls);
        Assert.AreEqual(0, ctx.BlockSessionCalls, "Must not steal the other event's session block");
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder("event-a.response-pending-handoff", "event-a.complete");
        trace.AssertAbsent(
            "event-a.block",
            "event-a.unblock",
            "event-a.response-process-deferred",
            "event-a.response-resolution");
    }

    [TestMethod]
    public async Task HandleRetryRequest_HandlerSignalsPendingHandoff_ParksAndKeepsSessionBlocked()
    {
        // Same gap as resubmission: an automatic retry of a handoff-style
        // handler must not false-complete.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var metadata = new HandoffMetadata("Awaiting external batch", "DMF-42", TimeSpan.FromMinutes(5));
        var handler = new FakeEventContextHandler
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = metadata;
            },
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.PendingHandoffCalls, "PendingHandoffResponse should fire");
        Assert.AreSame(metadata, response.LastPendingHandoffMetadata);
        Assert.AreEqual(1, ctx.BlockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Session must stay blocked for the later settlement");
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, response.ProcessDeferredCalls);
    }

    // ── HandleHandoffCompletedRequest ───────────────────────────────────

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_AuthorizedAndBlockedByThis_UnblocksAndSendsResolution()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ResolutionCalls, "Resolver-bound ResolutionResponse flips Pending → Completed");
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.verify-owner",
            "event-1.unblock",
            "event-1.response-resolution",
            "event-1.complete");
        trace.AssertAbsent("event-1.handler", "event-1.response-error");
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "SomeEndpoint");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // UnauthorizedAccessException flows through the base MessageHandler -> dead-letters
        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_MismatchedEventId_SendsResolutionAndCompletes()
    {
        // Settlement's EventId ≠ BlockedByEventId — a misaddressed or duplicate
        // settlement. VerifySessionIsBlockedByThis throws SessionBlockedException.
        // Rather than silently dead-lettering, surface it as resolved in the Flow.
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ResolutionCalls, "Unmatched settlement is surfaced as resolved, not silently dropped");
        Assert.AreEqual(1, ctx.CompletedCalls, "Message must be completed, not left for redelivery");
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Must NOT unblock — this settlement does not own the block");
        Assert.AreEqual(0, ctx.DeadLetterCalls, "Must not silently dead-letter");
    }

    [TestMethod]
    [DataRow(null, 1)]
    [DataRow("newer-blocking-event", 0)]
    public async Task HandleHandoffCompletedRequest_RedeliveryAfterUnblock_ResumesDeferredDrainOnlyWithoutANewBlock(
        string? blockedBy, int expectedDrainCalls)
    {
        // The first delivery lost its lock after clearing the block but before
        // sending ProcessDeferredRequest. Redelivery must repair that gap.
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = blockedBy;
        ctx.DeferredCountResult = 2;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();

        await CreateHandler(handler, response).Handle(ctx);

        Assert.AreEqual(expectedDrainCalls, response.ProcessDeferredCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_SessionNotBlockedAtAll_SendsResolutionAndCompletes()
    {
        // Session isn't blocked at all (already resolved / wrong session).
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null!;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_AuthorizedAndBlockedByThis_SendsErrorAndKeepsSessionBlocked()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        ctx.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            ErrorContent = new ErrorContent
            {
                ErrorText = "DMF rejected: invalid postal code",
                ErrorType = "DmfValidationError",
            },
        };
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls, "Resolver-bound ErrorResponse flips Pending → Failed");
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Session stays blocked — operator decides Resubmit/Skip");
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.IsNotNull(response.LastErrorException);
        var errorMessage = response.LastErrorException.InnerException?.Message ?? response.LastErrorException.Message;
        StringAssert.Contains(errorMessage, "DMF rejected: invalid postal code", "Operator-supplied errorText must be preserved verbatim");
        trace.AssertInOrder("event-1.verify-owner", "event-1.response-error", "event-1.complete");
        trace.AssertAbsent(
            "event-1.handler",
            "event-1.unblock",
            "event-1.response-process-deferred",
            "event-1.response-resolution");
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_MismatchedEventId_CompletesWithoutErrorResponse()
    {
        // Settlement's EventId ≠ BlockedByEventId. There is no matching blocked
        // event to flip to Failed, and a SendErrorResponse here could mis-target a
        // different event — so log an Error and Complete so it doesn't silently
        // dead-letter. No error response is sent (it would mis-target).
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        ctx.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            ErrorContent = new ErrorContent { ErrorText = "DMF rejected", ErrorType = "DmfValidationError" },
        };
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls, "Message must be completed, not left to silently dead-letter");
        Assert.AreEqual(0, response.ErrorCalls, "Must NOT send an ErrorResponse — it could mis-target a different event");
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Must not touch the block — no matching blocked event");
        Assert.AreEqual(0, ctx.DeadLetterCalls, "Must not silently dead-letter");
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_SessionNotBlockedAtAll_CompletesWithoutErrorResponse()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null!;
        ctx.MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            ErrorContent = new ErrorContent { ErrorText = "DMF rejected", ErrorType = "DmfValidationError" },
        };
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "SomeEndpoint");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // UnauthorizedAccessException flows through the base MessageHandler -> dead-letters
        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, response.ErrorCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleHandoffCompletedRequest_DoesNotInvokeUserHandler()
    {
        var ctx = CreateContext(messageType: MessageType.HandoffCompletedRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "HandoffCompleted must NOT re-invoke the user handler");
    }

    // ── Dead-letter Resolver notification ───────────────────────────────

    [TestMethod]
    public async Task HandleResubmissionRequest_FromNonManager_NotifiesResolverOfDeadLetter()
    {
        // UnauthorizedAccessException flows through the base MessageHandler's
        // unexpected-exception catch — should both DeadLetter and notify the Resolver.
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(1, response.DeadLetterCalls);
        Assert.AreEqual("Failed to handle message.", response.LastDeadLetterReason);
        Assert.IsInstanceOfType(response.LastDeadLetterException, typeof(UnauthorizedAccessException));
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrows_NoProvider_NoRetryResponse()
    {
        // With no IRetryPolicyProvider registered there is NO retry — the legacy
        // RetryDefinitions fallback (hardcoded demo rules like
        // "AliceSaidHelloWithRetry") is gone. Using that exact event type id
        // proves the demo rule no longer fires.
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "AliceSaidHelloWithRetry");
        ctx.RetryCount = 0;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.RetryCalls, "No provider means no retry — the demo-rule fallback must not fire");
        Assert.AreEqual(1, response.ErrorCalls, "The failure must surface as an error response instead");
    }

    [TestMethod]
    public async Task HandleEventRequest_RetryPolicy_SchedulesWithSubMinutePrecision()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var sender = new InMemoryMessageBus();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy
            {
                MaxRetries = 1,
                BaseDelay = TimeSpan.FromSeconds(90),
            },
        };
        var sut = new StrictMessageHandler(handler, new ResponseService(sender), NullLogger.Instance, retryProvider);
        var before = DateTimeOffset.UtcNow;

        await sut.Handle(ctx);

        var after = DateTimeOffset.UtcNow;
        var scheduled = sender.ScheduledMessages.Single();
        Assert.AreEqual(Constants.RetryId, scheduled.Message.To);
        Assert.IsTrue(scheduled.ScheduledTime >= before.AddSeconds(90));
        Assert.IsTrue(scheduled.ScheduledTime <= after.AddSeconds(90));
    }

    [TestMethod]
    [DataRow(30, 120, null, 120, DisplayName = "A longer hint wins")]
    [DataRow(90, 20, null, 90, DisplayName = "A shorter hint is ignored")]
    [DataRow(30, 600, 180, 180, DisplayName = "A hint is capped at MaxDelay")]
    [DataRow(30, 0, null, 30, DisplayName = "A zero hint is ignored")]
    public async Task HandleEventRequest_RetryAfterHint_LengthensThePolicyDelay(
        int policySeconds, int hintSeconds, int? maxDelaySeconds, int expectedSeconds)
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new HintedException(TimeSpan.FromSeconds(hintSeconds)) };
        var sender = new InMemoryMessageBus();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy
            {
                MaxRetries = 1,
                BaseDelay = TimeSpan.FromSeconds(policySeconds),
                MaxDelay = maxDelaySeconds is int max ? TimeSpan.FromSeconds(max) : null,
            },
        };
        var sut = new StrictMessageHandler(handler, new ResponseService(sender), NullLogger.Instance, retryProvider);
        var before = DateTimeOffset.UtcNow;

        await sut.Handle(ctx);

        var after = DateTimeOffset.UtcNow;
        var scheduled = sender.ScheduledMessages.Single();
        Assert.IsTrue(scheduled.ScheduledTime >= before.AddSeconds(expectedSeconds));
        Assert.IsTrue(scheduled.ScheduledTime <= after.AddSeconds(expectedSeconds));
    }

    [TestMethod]
    public async Task HandleEventRequest_RetryAfterHintOnAnInnerException_IsUsed()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var thrown = new InvalidOperationException("outer", new HintedException(TimeSpan.FromSeconds(300)));
        var handler = new FakeEventContextHandler { ThrowOnHandle = thrown };
        var sender = new InMemoryMessageBus();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 1, BaseDelay = TimeSpan.FromSeconds(10) },
        };
        var sut = new StrictMessageHandler(handler, new ResponseService(sender), NullLogger.Instance, retryProvider);
        var before = DateTimeOffset.UtcNow;

        await sut.Handle(ctx);

        Assert.IsTrue(sender.ScheduledMessages.Single().ScheduledTime >= before.AddSeconds(300));
    }

    [TestMethod]
    public async Task HandleEventRequest_RetryAfterHint_DoesNotRetryWhenThePolicyIsExhausted()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        ctx.RetryCount = 2;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new HintedException(TimeSpan.FromSeconds(30)) };
        var sender = new InMemoryMessageBus();
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy { MaxRetries = 2, BaseDelay = TimeSpan.FromSeconds(10) },
        };
        var sut = new StrictMessageHandler(handler, new ResponseService(sender), NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(0, sender.ScheduledMessages.Count);
    }

    [TestMethod]
    public async Task HandleEventRequest_RetryLookupReceivesTheHandlerException()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        var thrown = new HintedException(TimeSpan.FromSeconds(5));
        var handler = new FakeEventContextHandler { ThrowOnHandle = thrown };
        var retryProvider = new ExceptionRecordingRetryPolicyProvider();
        var sut = new StrictMessageHandler(handler, new FakeResponseService(), NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.IsNotNull(retryProvider.LastException);
        Assert.AreSame(thrown, retryProvider.LastException.InnerException,
            "The provider must see the exception chain, not just its text");
    }

    [TestMethod]
    public async Task HandleEventRequest_LegacyResponseService_RoundsPrecisePolicyDelay()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "OrderPlaced");
        ctx.Trace = trace;
        var handler = new FakeEventContextHandler(trace) { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService(trace);
        var retryProvider = new FakeRetryPolicyProvider
        {
            PolicyToReturn = new RetryPolicy
            {
                MaxRetries = 1,
                BaseDelay = TimeSpan.FromSeconds(90),
            },
        };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.RetryCalls);
        Assert.AreEqual(2, response.LastRetryDelayMinutes);
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.response-error",
            "event-1.block",
            "event-1.complete",
            "event-1.response-retry");
    }

    [TestMethod]
    public async Task HandleEventRequest_HandlerThrows_RetryLookupUsesUserPropertyEventTypeId()
    {
        // EventTypeId is backed by a message user property (no body deserialization
        // required). CheckForRetry must key the retry-policy lookup off that
        // authoritative value, NOT off the deserialized body's
        // MessageContent.EventContent.EventTypeId. Here the two intentionally differ.
        var ctx = CreateContext(messageType: MessageType.EventRequest, eventTypeId: "UserPropEventType");
        ctx.MessageContent.EventContent.EventTypeId = "BodyEventType"; // must NOT be used
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var retryProvider = new FakeRetryPolicyProvider();
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(1, retryProvider.GetRetryPolicyCalls, "CheckForRetry must consult the retry policy provider");
        Assert.AreEqual("UserPropEventType", retryProvider.LastEventTypeId,
            "Retry lookup must use the user-property EventTypeId, not the deserialized body value");
    }

    // ── HandleRetryRequest ──────────────────────────────────────────────

    [TestMethod]
    public async Task HandleRetryRequest_BlockedByThis_HandlesUnblocksAndCompletes()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.verify-owner",
            "event-1.handler",
            "event-1.unblock",
            "event-1.response-resolution",
            "event-1.complete");
    }

    [TestMethod]
    public async Task HandleRetryRequest_Duplicate_UnblocksAndSendsDuplicateResponse()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.DuplicateDetected,
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_BlockedByThis_WithNewDeferred_SendsProcessDeferredRequest()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 3;  // but new-style deferred exist
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, response.ContinuationCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_NotBlockedByThis_SendsResolutionAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "Should not handle when not blocked by this");
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_HandlerThrows_SendsErrorAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls, "Should not unblock on failure");
    }

    // ── HandleResubmissionRequest ───────────────────────────────────────

    [TestMethod]
    public async Task HandleResubmissionRequest_FromManager_HandlesAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_Duplicate_SendsDuplicateResponse()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler
        {
            OnHandle = context => context.HandlerOutcome = HandlerOutcome.DuplicateDetected,
        };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_FromManager_BlockedByThis_Unblocks()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_FromManager_NotBlockedByThis_SkipsUnblock()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(0, ctx.UnblockSessionCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // UnauthorizedAccessException is caught by base MessageHandler -> dead-letters
        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, handler.HandleCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_EventHandlerNotFound_SendsUnsupportedAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new EventHandlerNotFoundException("not found") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.UnsupportedCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_HandlerThrows_SendsErrorAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        var handler = new FakeEventContextHandler { ThrowOnHandle = new InvalidOperationException("boom") };
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.ErrorCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    // ── HandleSkipRequest ───────────────────────────────────────────────

    [TestMethod]
    public async Task HandleSkipRequest_FromManager_BlockedByThis_UnblocksAndSendsSkip()
    {
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.SkipRequest, from: "Manager");
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.SkipCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.verify-owner",
            "event-1.unblock",
            "event-1.response-skip",
            "event-1.complete");
    }

    [TestMethod]
    public async Task HandleSkipRequest_NotBlockedByThis_SendsSkipAndCompletes()
    {
        var ctx = CreateContext(messageType: MessageType.SkipRequest, from: "Manager");
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "different-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, response.SkipCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleSkipRequest_FromNonManager_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.SkipRequest, from: "SomeEndpoint");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.DeadLetterCalls);
        Assert.AreEqual(0, response.SkipCalls);
    }

    // ── HandleContinuationRequest ───────────────────────────────────────

    [TestMethod]
    [DataRow("Continuation")]
    [DataRow("SomeEndpoint")]
    public async Task HandleContinuationRequest_CompletesLegacyRequestWithoutProcessing(string from)
    {
        // Only the legacy Service Bus defer drain produced ContinuationRequests, and v4 removed
        // it. One still in flight at upgrade is completed, never dispatched or dead-lettered.
        var ctx = CreateContext(messageType: MessageType.ContinuationRequest, from: from, eventId: "event-1");
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, response.ErrorCalls);
    }

    // HandleProcessDeferredRequest tests removed — deferred processing
    // is now handled by a separate DeferredProcessorFunction in subscriber apps,
    // not by StrictMessageHandler.

    // ── MessageType routing ─────────────────────────────────────────────

    [TestMethod]
    public async Task Handle_UnsupportedResponse_RoutesToHandleEventRequest()
    {
        // UnsupportedResponse is routed to HandleEventRequest in the base MessageHandler
        var ctx = CreateContext(messageType: MessageType.UnsupportedResponse);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
    }

    [TestMethod]
    public async Task Handle_UnhandledMessageType_DeadLetters()
    {
        var ctx = CreateContext(messageType: MessageType.ErrorResponse);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response);

        await sut.Handle(ctx);

        // ErrorResponse falls through to HandleDefault which throws UnsupportedMessageTypeException -> dead-letter
        Assert.AreEqual(1, ctx.DeadLetterCalls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    // ── Inbox pre-check (duplicate detection before session guards) ──────

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithUnblockedSession_SendsDuplicateResponse()
    {
        // A successfully handled RetryRequest leaves its session unblocked; without the
        // pre-check its redelivery would fail VerifySessionIsBlockedByThis and complete
        // with a normal resolution response, hiding the duplicate.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(HandlerOutcome.DuplicateDetected, ctx.HandlerOutcome);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithSessionStillBlockedByThis_UnblocksBeforeCompleting()
    {
        // Crash window: the first attempt recorded the inbox entry but crashed before
        // unblocking. The duplicate path must still release the session and drain deferred
        // siblings, or the session would stay blocked forever.
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore(trace) { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "inbox.check",
            "event-1.verify-owner",
            "event-1.unblock",
            "event-1.response-duplicate",
            "event-1.complete");
        trace.AssertAbsent("event-1.handler", "event-1.response-resolution");
    }

    [TestMethod]
    public async Task HandleEventRequest_RecordedDuplicate_WhileSessionBlockedByOther_SkipsInsteadOfDeferring()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.BlockedByEventId = "other-event";
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.DeferralCalls);
        Assert.AreEqual(0, response.SendToDeferredSubscriptionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_PreCheckStoreFailure_AbandonsWithoutRunningHandler()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(
            handler,
            response,
            inboxStore: new FakeInboxStore { CheckException = new InvalidOperationException("provider details") });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, ctx.AbandonCalls);
        Assert.AreEqual(0, ctx.CompletedCalls);
        Assert.AreEqual(0, ctx.DeadLetterCalls);
    }

    [TestMethod]
    public async Task HandleEventRequest_NotRecorded_PreCheckAllowsNormalProcessing()
    {
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore());

        await sut.Handle(ctx);

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(0, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithUnblockedSession_DrainsDeferredSiblings()
    {
        // Crash window: the first attempt recorded and unblocked, then died before the
        // deferred drain. The redelivered duplicate is the only remaining drain trigger —
        // completing it without draining would park deferred siblings indefinitely.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = null!;
        ctx.DeferredCountResult = 2;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleRetryRequest_RecordedDuplicate_WithSessionBlockedByOther_LeavesBlockerAndSkipsDrain()
    {
        // An unrelated later event owns the session and its eventual settlement owns the
        // drain; the duplicate must neither unblock nor trigger it early.
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.IsSessionBlockedByThisResult = false;
        ctx.BlockedByEventId = "other-event";
        ctx.DeferredCountResult = 2;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(0, response.ProcessDeferredCalls);
        Assert.AreEqual(0, response.ContinuationCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_RecordedDuplicate_PreCheckSkipsHandler()
    {
        // The handler-seam decorator is record-only in hosted compositions, so the
        // resubmission entry point must run the pre-check itself.
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.DeferredCountResult = 1;
        var handler = new FakeEventContextHandler();
        var response = new FakeResponseService();
        var sut = CreateHandler(handler, response, inboxStore: new FakeInboxStore { HasProcessed = true });

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls);
        Assert.AreEqual(1, response.DuplicateCalls);
        Assert.AreEqual(0, response.ResolutionCalls);
        // Mirrors the normal resubmission path: no unblock when not blocked by this,
        // and the unconditional deferred drain still runs.
        Assert.AreEqual(0, ctx.UnblockSessionCalls);
        Assert.AreEqual(1, response.ProcessDeferredCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
    }

    // ── Processor stop during settlement ─────────────────────────────────
    //
    // The receiver stops its processor when the endpoint circuit opens, which cancels the
    // token of every in-flight delivery, including deliveries in sessions that never failed.
    // Once a delivery's outcome is decided its settlement must still run to the end: stopping
    // half-way left an ErrorResponse sent and the session blocked, but the message neither
    // completed nor its retry scheduled, so its redelivery deferred behind its own block.

    [TestMethod]
    [DataRow("event-1.response-error")]
    [DataRow("event-1.block")]
    [DataRow("event-1.complete")]
    public async Task HandleEventRequest_HandlerFailure_ProcessorStopsDuringSettlement_StillCompletesAndSchedulesRetry(string stopAfter)
    {
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter(stopAfter, processorStop);
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        var handler = new FakeEventContextHandler(trace) { ThrowOnHandle = new InvalidOperationException("503 Service Unavailable") };
        var response = new FakeResponseService(trace);
        var retryProvider = new FakeRetryPolicyProvider { PolicyToReturn = new RetryPolicy { MaxRetries = 3 } };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx, processorStop.Token);

        Assert.IsTrue(processorStop.IsCancellationRequested);
        Assert.AreEqual(1, ctx.CompletedCalls, "The failed delivery must still be completed");
        Assert.AreEqual(1, response.RetryCalls, "The RetryRequest must still be scheduled");
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.response-error",
            "event-1.block",
            "event-1.complete",
            "event-1.response-retry");
    }

    [TestMethod]
    public async Task HandleEventRequest_ProcessorStopsWhileHandlerRuns_PropagatesCancellationWithoutSettling()
    {
        // A handler that honours the stop produced no outcome, so nothing is settled: the
        // delivery stays on the subscription and is redelivered when the receiver restarts.
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        var handler = new FakeEventContextHandler(trace)
        {
            OnHandle = _ =>
            {
                processorStop.Cancel();
                processorStop.Token.ThrowIfCancellationRequested();
            },
        };
        var response = new FakeResponseService(trace);
        var retryProvider = new FakeRetryPolicyProvider { PolicyToReturn = new RetryPolicy { MaxRetries = 3 } };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => sut.Handle(ctx, processorStop.Token));

        Assert.AreEqual(0, retryProvider.GetRetryPolicyCalls);
        trace.AssertAbsent(
            "event-1.response-error",
            "event-1.response-resolution",
            "event-1.block",
            "event-1.complete",
            "event-1.response-retry");
    }

    [TestMethod]
    public async Task HandleEventRequest_ProcessorStopsAfterHandlerSucceeds_StillRespondsAndCompletes()
    {
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter("event-1.handler", processorStop);
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        var response = new FakeResponseService(trace);
        var sut = CreateHandler(new FakeEventContextHandler(trace), response);

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, response.ResolutionCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder("event-1.handler", "event-1.response-resolution", "event-1.complete");
    }

    [TestMethod]
    [DataRow("event-1.response-pending-handoff")]
    [DataRow("event-1.block")]
    public async Task HandleEventRequest_PendingHandoff_ProcessorStopsWhileParking_StillBlocksAndCompletes(string stopAfter)
    {
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter(stopAfter, processorStop);
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        var handler = new FakeEventContextHandler(trace)
        {
            OnHandle = c =>
            {
                c.HandlerOutcome = HandlerOutcome.PendingHandoff;
                c.HandoffMetadata = new HandoffMetadata("DMF import in flight", "JOB-42", TimeSpan.FromMinutes(5));
            },
        };
        var sut = CreateHandler(handler, new FakeResponseService(trace));

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.response-pending-handoff",
            "event-1.block",
            "event-1.complete");
    }

    [TestMethod]
    [DataRow("event-1.response-deferral")]
    [DataRow("event-1.response-deferred-forward")]
    [DataRow("event-1.deferred-count-increment")]
    public async Task HandleEventRequest_SessionBlocked_ProcessorStopsWhileParking_StillParksAndCompletes(string stopAfter)
    {
        // A parked copy whose DeferredCount increment is lost is never drained: an unblock
        // only sends a ProcessDeferredRequest while the count is above zero.
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter(stopAfter, processorStop);
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        ctx.BlockedByEventId = "other-event";
        var sut = CreateHandler(new FakeEventContextHandler(trace), new FakeResponseService(trace));

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.IncrementDeferredCountCalls);
        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.session-guard",
            "event-1.response-deferral",
            "event-1.deferral-sequence",
            "event-1.response-deferred-forward",
            "event-1.deferred-count-increment",
            "event-1.complete");
        trace.AssertAbsent("event-1.handler");
    }

    [TestMethod]
    [DataRow("event-1.response-error")]
    [DataRow("event-1.complete")]
    public async Task HandleRetryRequest_HandlerFailure_ProcessorStopsDuringSettlement_StillCompletesAndSchedulesNextRetry(string stopAfter)
    {
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter(stopAfter, processorStop);
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        ctx.RetryCount = 1;
        var handler = new FakeEventContextHandler(trace) { ThrowOnHandle = new InvalidOperationException("503 Service Unavailable") };
        var response = new FakeResponseService(trace);
        var retryProvider = new FakeRetryPolicyProvider { PolicyToReturn = new RetryPolicy { MaxRetries = 3 } };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(1, response.RetryCalls, "The next RetryRequest must still be scheduled");
        trace.AssertInOrder(
            "event-1.verify-owner",
            "event-1.handler",
            "event-1.response-error",
            "event-1.complete",
            "event-1.response-retry");
        trace.AssertAbsent("event-1.unblock");
    }

    [TestMethod]
    [DataRow("event-1.handler")]
    [DataRow("event-1.unblock")]
    public async Task HandleRetryRequest_Success_ProcessorStopsDuringSettlement_StillUnblocksDrainsAndCompletes(string stopAfter)
    {
        // Stopping between the unblock and the drain would strand the parked siblings: the
        // redelivered RetryRequest no longer owns the block, so it is answered without a drain.
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter(stopAfter, processorStop);
        var ctx = CreateContext(messageType: MessageType.RetryRequest);
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 2;
        var sut = CreateHandler(new FakeEventContextHandler(trace), new FakeResponseService(trace));

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.unblock",
            "event-1.response-process-deferred",
            "event-1.response-resolution",
            "event-1.complete");
    }

    [TestMethod]
    public async Task HandleResubmissionRequest_ProcessorStopsAfterHandlerSucceeds_StillUnblocksDrainsAndCompletes()
    {
        // The ownership check after the handler is part of the settlement: a cancelled read
        // there would leave the session blocked by an event that has already succeeded.
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter("event-1.handler", processorStop);
        var ctx = CreateContext(messageType: MessageType.ResubmissionRequest, from: "Manager");
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 1;
        var sut = CreateHandler(new FakeEventContextHandler(trace), new FakeResponseService(trace));

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.handler",
            "event-1.verify-owner",
            "event-1.unblock",
            "event-1.response-process-deferred",
            "event-1.response-resolution",
            "event-1.complete");
    }

    [TestMethod]
    [DataRow(MessageType.SkipRequest, "event-1.response-skip")]
    [DataRow(MessageType.HandoffCompletedRequest, "event-1.response-resolution")]
    public async Task ManagerSettlement_ProcessorStopsAfterUnblock_StillDrainsRespondsAndCompletes(
        MessageType messageType,
        string expectedResponse)
    {
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter("event-1.unblock", processorStop);
        var ctx = CreateContext(messageType: messageType, from: "Manager");
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        ctx.DeferredCountResult = 2;
        var sut = CreateHandler(new FakeEventContextHandler(trace), new FakeResponseService(trace));

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder(
            "event-1.verify-owner",
            "event-1.unblock",
            "event-1.response-process-deferred",
            expectedResponse,
            "event-1.complete");
    }

    [TestMethod]
    public async Task HandleHandoffFailedRequest_ProcessorStopsAfterErrorResponse_StillCompletes()
    {
        var trace = new OperationTrace();
        using var processorStop = new CancellationTokenSource();
        trace.StopProcessorAfter("event-1.response-error", processorStop);
        var ctx = CreateContext(messageType: MessageType.HandoffFailedRequest, from: "Manager");
        ctx.Trace = trace;
        ctx.IsSessionBlockedByThisResult = true;
        var sut = CreateHandler(new FakeEventContextHandler(trace), new FakeResponseService(trace));

        await sut.Handle(ctx, processorStop.Token);

        Assert.AreEqual(1, ctx.CompletedCalls);
        trace.AssertInOrder("event-1.verify-owner", "event-1.response-error", "event-1.complete");
    }

    // ── Redelivery into a session blocked by its own event ───────────────

    [TestMethod]
    [DataRow("event-1")]
    [DataRow("EVENT-1")]
    public async Task HandleEventRequest_SessionBlockedByItsOwnEvent_CompletesWithoutRunningOrParking(string blockedByEventId)
    {
        // An earlier delivery of this event sent its ErrorResponse or PendingHandoffResponse
        // and blocked the session, but was never completed (a crash or a lost session lock),
        // so the broker redelivered it. Its outcome is already recorded and its block keeps
        // its own way out, so the redelivery is completed as is. Running the handler again
        // could repeat a handed-off external job; parking the copy behind its own block would
        // replay it once the block clears, running the event a second time — even after an
        // operator skipped it.
        var trace = new OperationTrace();
        var ctx = CreateContext(messageType: MessageType.EventRequest);
        ctx.Trace = trace;
        ctx.BlockedByEventId = blockedByEventId;
        var handler = new FakeEventContextHandler(trace);
        var response = new FakeResponseService(trace);
        var retryProvider = new FakeRetryPolicyProvider { PolicyToReturn = new RetryPolicy { MaxRetries = 3 } };
        var sut = new StrictMessageHandler(handler, response, NullLogger.Instance, retryProvider);

        await sut.Handle(ctx);

        Assert.AreEqual(0, handler.HandleCalls, "The handler must not run again");
        Assert.AreEqual(1, ctx.CompletedCalls);
        Assert.AreEqual(HandlerOutcome.NotDispatched, ctx.HandlerOutcome);
        trace.AssertInOrder("event-1.session-guard", "event-1.complete");
        trace.AssertAbsent(
            "event-1.response-deferral",
            "event-1.response-deferred-forward",
            "event-1.deferred-count-increment",
            // No response at all: the Resolver already holds the event's outcome, and a
            // duplicate response is recorded as Skipped, which would overwrite a Failed row.
            "event-1.response-duplicate",
            "event-1.response-resolution",
            "event-1.response-error",
            "event-1.response-retry",
            "event-1.block",
            "event-1.unblock");
    }

    private static StrictMessageHandler CreateHandler(
        FakeEventContextHandler handler,
        FakeResponseService response,
        FakeDeferredMessageProcessor? processor = null,
        string? topicName = null,
        NimBus.Core.Inbox.IInboxStore? inboxStore = null)
    {
        if (inboxStore is null)
            return new StrictMessageHandler(handler, response, NullLogger.Instance);

        return new StrictMessageHandler(
            handler,
            response,
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: null,
            failureDispositionClassifier: null,
            inboxDuplicateDetector: new NimBus.Core.Inbox.InboxDuplicateDetector(inboxStore));
    }

    private sealed class FakeInboxStore : NimBus.Core.Inbox.IInboxStore
    {
        private readonly OperationTrace? _trace;

        public FakeInboxStore(OperationTrace? trace = null)
        {
            _trace = trace;
        }

        public bool HasProcessed { get; set; }
        public Exception CheckException { get; set; }
        public Exception RecordException { get; set; }

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            _trace?.Record("inbox.check", cancellationToken);
            if (CheckException != null)
                throw CheckException;
            return Task.FromResult(HasProcessed);
        }

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            _trace?.Record("inbox.record", cancellationToken);
            if (RecordException != null)
                throw RecordException;
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private static FakeMessageContext CreateContext(
        MessageType messageType,
        string from = "StorefrontEndpoint",
        string eventId = "event-1",
        string eventTypeId = "OrderPlaced")
    {
        return new FakeMessageContext
        {
            EventId = eventId,
            MessageId = "message-1",
            CorrelationId = "correlation-1",
            SessionId = "session-1",
            ParentMessageId = "self",
            OriginatingMessageId = "self",
            OriginatingFrom = from,
            From = from,
            To = "AnalyticsEndpoint",
            MessageType = messageType,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = "{}",
                },
            },
            EventTypeId = eventTypeId,
            TraceName = eventId,
            EnqueuedTimeUtc = new DateTime(2026, 3, 9, 12, 0, 0, DateTimeKind.Utc),
        };
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class OperationTrace
    {
        private readonly List<string> _entries = [];
        private string? _stopProcessorAfter;
        private CancellationTokenSource? _processorStop;

        // Simulates the receiver stopping its processor mid-sequence, as it does when the
        // endpoint circuit opens: once the named operation has run, the processor's token is
        // cancelled, and from then on a call made with a cancelled token fails without effect,
        // as it does against the Service Bus SDK.
        public void StopProcessorAfter(string operation, CancellationTokenSource processorStop)
        {
            _stopProcessorAfter = operation;
            _processorStop = processorStop;
        }

        public void Record(string operation) => Record(operation, CancellationToken.None);

        public void Record(string operation, CancellationToken cancellationToken)
        {
            if (_processorStop is not null)
                cancellationToken.ThrowIfCancellationRequested();

            _entries.Add(operation);

            if (operation == _stopProcessorAfter)
                _processorStop!.Cancel();
        }

        public void AssertInOrder(params string[] expected)
        {
            var searchIndex = 0;
            foreach (var operation in expected)
            {
                var index = _entries.FindIndex(searchIndex, entry => entry == operation);
                Assert.IsTrue(
                    index >= 0,
                    $"Expected '{operation}' after trace index {searchIndex - 1}. Actual trace: {string.Join(" -> ", _entries)}");
                searchIndex = index + 1;
            }
        }

        public void AssertAbsent(params string[] unexpected)
        {
            foreach (var operation in unexpected)
            {
                CollectionAssert.DoesNotContain(
                    _entries,
                    operation,
                    $"Did not expect '{operation}'. Actual trace: {string.Join(" -> ", _entries)}");
            }
        }
    }

    private static string GetTraceName(IMessageContext context) =>
        context is FakeMessageContext fake ? fake.TraceName : context.EventId;

    private sealed class FakeEventContextHandler : IEventContextHandler
    {
        private readonly OperationTrace? _trace;

        public FakeEventContextHandler(OperationTrace? trace = null)
        {
            _trace = trace;
        }

        public int HandleCalls { get; private set; }
        public Exception ThrowOnHandle { get; set; }
        public Action<IMessageContext> OnHandle { get; set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            _trace?.Record($"{GetTraceName(context)}.handler", cancellationToken);
            HandleCalls++;
            OnHandle?.Invoke(context);
            if (ThrowOnHandle != null)
                throw ThrowOnHandle;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResponseService : IResponseService
    {
        private readonly OperationTrace? _trace;

        public FakeResponseService(OperationTrace? trace = null)
        {
            _trace = trace;
        }

        public int ResolutionCalls { get; private set; }
        public int ErrorCalls { get; private set; }
        public int DeferralCalls { get; private set; }
        public int SkipCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public int UnsupportedCalls { get; private set; }
        public int ContinuationCalls { get; private set; }
        public int SendToDeferredSubscriptionCalls { get; private set; }
        public int ProcessDeferredCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public int PendingHandoffCalls { get; private set; }
        public int DuplicateCalls { get; private set; }
        public HandoffMetadata LastPendingHandoffMetadata { get; private set; }
        public string LastDeadLetterReason { get; private set; }
        public Exception LastDeadLetterException { get; private set; }
        public Exception LastErrorException { get; private set; }
        public Exception LastDiscardException { get; private set; }
        public string LastDiscardClassifierName { get; private set; }
        public int? LastRetryDelayMinutes { get; private set; }

        public Task SendResolutionResponse(IMessageContext mc, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-resolution", ct); ResolutionCalls++; return Task.CompletedTask; }
        public Task SendSkipResponse(IMessageContext mc, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-skip", ct); SkipCalls++; return Task.CompletedTask; }
        public Task SendDuplicateResponse(IMessageContext mc, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-duplicate", ct); DuplicateCalls++; return Task.CompletedTask; }
        public Task SendDiscardResponse(IMessageContext mc, Exception ex, string classifierName, CancellationToken ct = default)
        {
            _trace?.Record($"{GetTraceName(mc)}.response-discard", ct);
            DiscardCalls++;
            LastDiscardException = ex;
            LastDiscardClassifierName = classifierName;
            return Task.CompletedTask;
        }
        public Task SendErrorResponse(IMessageContext mc, Exception ex, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-error", ct); ErrorCalls++; LastErrorException = ex; return Task.CompletedTask; }
        public Task SendDeadLetterResponse(IMessageContext mc, string reason, Exception? ex, CancellationToken ct = default)
        {
            _trace?.Record($"{GetTraceName(mc)}.response-dead-letter", ct);
            DeadLetterCalls++;
            LastDeadLetterReason = reason;
            LastDeadLetterException = ex;
            return Task.CompletedTask;
        }
        public Task SendDeferralResponse(IMessageContext mc, SessionBlockedException ex, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-deferral", ct); DeferralCalls++; return Task.CompletedTask; }
        public Task SendRetryResponse(IMessageContext mc, int delay, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-retry", ct); RetryCalls++; LastRetryDelayMinutes = delay; return Task.CompletedTask; }
        public Task SendUnsupportedResponse(IMessageContext mc, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-unsupported", ct); UnsupportedCalls++; return Task.CompletedTask; }
        public Task SendContinuationRequestToSelf(IMessageContext mc, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-continuation", ct); ContinuationCalls++; return Task.CompletedTask; }
        public Task SendToDeferredSubscription(IMessageContext mc, int seq, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-deferred-forward", ct); SendToDeferredSubscriptionCalls++; return Task.CompletedTask; }
        public Task SendProcessDeferredRequest(IMessageContext mc, CancellationToken ct = default) { _trace?.Record($"{GetTraceName(mc)}.response-process-deferred", ct); ProcessDeferredCalls++; return Task.CompletedTask; }
        public Task SendPendingHandoffResponse(IMessageContext mc, HandoffMetadata handoff, CancellationToken ct = default)
        {
            _trace?.Record($"{GetTraceName(mc)}.response-pending-handoff", ct);
            PendingHandoffCalls++;
            LastPendingHandoffMetadata = handoff;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRetryPolicyProvider : IRetryPolicyProvider
    {
        public int GetRetryPolicyCalls { get; private set; }
        public string LastEventTypeId { get; private set; }
        public RetryPolicy PolicyToReturn { get; set; }

        public RetryPolicy GetRetryPolicy(string eventTypeId, string exceptionMessage, string? endpoint = null)
        {
            GetRetryPolicyCalls++;
            LastEventTypeId = eventTypeId;
            return PolicyToReturn;
        }
    }

    private sealed class HintedException(TimeSpan? retryAfter) : Exception("throttled"), IRetryAfterHint
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private sealed class ExceptionRecordingRetryPolicyProvider : IRetryPolicyProvider
    {
        public Exception? LastException { get; private set; }

        public RetryPolicy GetRetryPolicy(string eventTypeId, string exceptionMessage, string? endpoint = null) => null!;

        public RetryPolicy GetRetryPolicy(string eventTypeId, Exception exception, string? endpoint = null)
        {
            LastException = exception;
            return null!;
        }
    }

    private sealed class FakeFailureDispositionClassifier : IFailureDispositionClassifier
    {
        private readonly FailureDisposition _disposition;

        public FakeFailureDispositionClassifier(FailureDisposition disposition)
        {
            _disposition = disposition;
        }

        public Exception LastException { get; private set; }
        public string LastEventTypeId { get; private set; }
        public string? LastEndpointName { get; private set; }

        public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName)
        {
            LastException = exception;
            LastEventTypeId = eventTypeId;
            LastEndpointName = endpointName;
            return _disposition;
        }
    }

    private sealed class RecordingLifecycleObserver : IMessageLifecycleObserver
    {
        public int ReceivedCalls { get; private set; }
        public int CompletedCalls { get; private set; }
        public int FailedCalls { get; private set; }
        public Exception? LastFailure { get; private set; }

        public Task OnMessageReceived(MessageLifecycleContext context, CancellationToken cancellationToken = default)
        {
            ReceivedCalls++;
            return Task.CompletedTask;
        }

        public Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken cancellationToken = default)
        {
            CompletedCalls++;
            return Task.CompletedTask;
        }

        public Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken cancellationToken = default)
        {
            FailedCalls++;
            LastFailure = exception;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDeferredMessageProcessor : IDeferredMessageProcessor
    {
        public int ProcessCalls { get; private set; }
        public string LastSessionId { get; private set; }
        public string LastTopicName { get; private set; }

        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken ct = default)
        {
            ProcessCalls++;
            LastSessionId = sessionId;
            LastTopicName = topicName;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMessageContext : IMessageContext
    {
        public OperationTrace? Trace { get; set; }
        public string TraceName { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public MessageType MessageType { get; set; }
        public MessageContent MessageContent { get; set; } = new();
        public string ParentMessageId { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public int? RetryCount { get; set; }
        public string OriginatingFrom { get; set; } = string.Empty;
        public string EventTypeId { get; set; } = string.Empty;
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        public string From { get; set; } = string.Empty;
        public string DeadLetterReason { get; set; }
        public string DeadLetterErrorDescription { get; set; }
        public string HandoffReason { get; set; }
        public string ExternalJobId { get; set; }
        public DateTime? ExpectedBy { get; set; }
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata HandoffMetadata { get; set; }

        // Configurable behavior
        public string BlockedByEventId { get; set; }
        public bool IsSessionBlockedByThisResult { get; set; }
        public int DeferredCountResult { get; set; }

        // Call counters
        public int CompletedCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }
        public int AbandonCalls { get; private set; }
        public int BlockSessionCalls { get; private set; }
        public int UnblockSessionCalls { get; private set; }
        public int IncrementDeferredCountCalls { get; private set; }
        public int ResetDeferredCountCalls { get; private set; }

        public Task Complete(CancellationToken ct = default) { Trace?.Record($"{TraceName}.complete", ct); CompletedCalls++; return Task.CompletedTask; }
        public Task Abandon(TransientException ex) { Trace?.Record($"{TraceName}.abandon"); AbandonCalls++; return Task.CompletedTask; }
        public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) { Trace?.Record($"{TraceName}.dead-letter", ct); DeadLetterCalls++; return Task.CompletedTask; }
        public Task BlockSession(CancellationToken ct = default) { Trace?.Record($"{TraceName}.block", ct); BlockSessionCalls++; return Task.CompletedTask; }
        public Task UnblockSession(CancellationToken ct = default) { Trace?.Record($"{TraceName}.unblock", ct); UnblockSessionCalls++; return Task.CompletedTask; }
        public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(BlockedByEventId));
        public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) { Trace?.Record($"{TraceName}.verify-owner", ct); return Task.FromResult(IsSessionBlockedByThisResult); }
        public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(!string.IsNullOrEmpty(BlockedByEventId));
        public Task<string> GetBlockedByEventId(CancellationToken ct = default) { Trace?.Record($"{TraceName}.session-guard", ct); return Task.FromResult(BlockedByEventId); }
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) { Trace?.Record($"{TraceName}.deferral-sequence", ct); return Task.FromResult(0); }
        public Task IncrementDeferredCount(CancellationToken ct = default) { Trace?.Record($"{TraceName}.deferred-count-increment", ct); IncrementDeferredCountCalls++; return Task.CompletedTask; }
        public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken ct = default) { Trace?.Record($"{TraceName}.deferred-count-read", ct); return Task.FromResult(DeferredCountResult); }
        public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(DeferredCountResult > 0);
        public Task ResetDeferredCount(CancellationToken ct = default) { ResetDeferredCountCalls++; return Task.CompletedTask; }
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
    }
}
