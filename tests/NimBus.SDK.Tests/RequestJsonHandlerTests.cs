#pragma warning disable CA1707, CA2007

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;
using NimBus.Testing;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.Tests;

[TestClass]
public class RequestJsonHandlerTests
{
    private sealed class PingRequest : Event
    {
        public string Text { get; set; } = null!;
    }

    private sealed class PongResponse
    {
        public string Echo { get; set; } = null!;
    }

    private sealed class EchoRequestHandler : IRequestHandler<PingRequest, PongResponse>
    {
        public Task<PongResponse> Handle(PingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new PongResponse { Echo = request.Text });
    }

    private sealed class ThrowingRequestHandler : IRequestHandler<PingRequest, PongResponse>
    {
        public Task<PongResponse> Handle(PingRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("handler blew up");
    }

    private static RequestTestMessageContext ContextFor(string json, string replyTo = "CrmEndpoint", string replySessionId = "reply-session-1")
        => new()
        {
            EventTypeId = nameof(PingRequest),
            ReplyTo = replyTo,
            ReplyToSessionId = replySessionId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = nameof(PingRequest), EventJson = json },
            },
        };

    [TestMethod]
    public async Task SuccessfulHandler_SendsSuccessReplyWithPayloadSessionAndCorrelation()
    {
        var dispatcher = new InMemoryReplyDispatcher();
        var handler = new RequestJsonHandler<PingRequest, PongResponse>(new EchoRequestHandler(), dispatcher);
        var context = ContextFor(JsonConvert.SerializeObject(new PingRequest { Text = "hello" }));
        context.CorrelationId = "corr-42";

        await handler.Handle(context);

        Assert.AreEqual(1, dispatcher.SentReplies.Count);
        var reply = dispatcher.SentReplies[0];
        Assert.AreEqual("CrmEndpoint", reply.ReplyTo);
        Assert.AreEqual("reply-session-1", reply.ReplySessionId);
        Assert.AreEqual("corr-42", reply.CorrelationId);
        Assert.IsFalse(reply.IsError);
        var payload = JsonConvert.DeserializeObject<PongResponse>(reply.PayloadJson!);
        Assert.AreEqual("hello", payload!.Echo);
    }

    [TestMethod]
    public async Task ThrowingHandler_SendsErrorReplyAndRethrows()
    {
        var dispatcher = new InMemoryReplyDispatcher();
        var handler = new RequestJsonHandler<PingRequest, PongResponse>(new ThrowingRequestHandler(), dispatcher);
        var context = ContextFor(JsonConvert.SerializeObject(new PingRequest { Text = "boom" }));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => handler.Handle(context));

        Assert.AreEqual(1, dispatcher.SentReplies.Count);
        var reply = dispatcher.SentReplies[0];
        Assert.IsTrue(reply.IsError);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, reply.ErrorType);
        Assert.AreEqual("handler blew up", reply.ErrorText);
    }

    [TestMethod]
    public async Task NoReplyTo_RunsHandlerWithoutDispatchingReply()
    {
        var dispatcher = new InMemoryReplyDispatcher();
        var handler = new RequestJsonHandler<PingRequest, PongResponse>(new EchoRequestHandler(), dispatcher);
        var context = ContextFor(JsonConvert.SerializeObject(new PingRequest { Text = "plain" }), replyTo: null!, replySessionId: null!);

        await handler.Handle(context);

        Assert.AreEqual(0, dispatcher.SentReplies.Count);
    }

    [TestMethod]
    public async Task MalformedJson_SendsErrorReplyAndThrowsPermanentFailure()
    {
        var dispatcher = new InMemoryReplyDispatcher();
        var handler = new RequestJsonHandler<PingRequest, PongResponse>(new EchoRequestHandler(), dispatcher);
        var context = ContextFor("{ not json");

        await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => handler.Handle(context));

        Assert.AreEqual(1, dispatcher.SentReplies.Count);
        Assert.IsTrue(dispatcher.SentReplies[0].IsError);
    }

    [TestMethod]
    public async Task ThrowingHandler_ReplyDispatchFailure_OriginalExceptionWins()
    {
        var dispatcher = new FailingReplyDispatcher();
        var handler = new RequestJsonHandler<PingRequest, PongResponse>(new ThrowingRequestHandler(), dispatcher);
        var context = ContextFor(JsonConvert.SerializeObject(new PingRequest { Text = "boom" }));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => handler.Handle(context));
    }

    private sealed class FailingReplyDispatcher : IReplyDispatcher
    {
        public Task SendReplyAsync(ReplyMessage reply, CancellationToken cancellationToken = default)
            => throw new TimeoutException("send failed");
    }
}

// Minimal IMessageContext fake for request dispatch tests. Only the members the
// RequestJsonHandler touches carry meaningful values.
internal sealed class RequestTestMessageContext : IMessageContext
{
    public string EventId { get; set; } = "evt-1";
    public string To { get; set; } = "PingRequest";
    public string SessionId { get; set; } = "session-1";
    public string CorrelationId { get; set; } = "corr-1";
    public string MessageId { get; set; } = "msg-1";
    public MessageType MessageType { get; set; } = MessageType.EventRequest;
    public MessageContent MessageContent { get; set; } = new();
    public string ParentMessageId { get; set; } = string.Empty;
    public string OriginatingMessageId { get; set; } = string.Empty;
    public int? RetryCount { get; set; }
    public string OriginatingFrom { get; set; } = string.Empty;
    public string EventTypeId { get; set; } = "PingRequest";
    public string OriginalSessionId { get; set; } = string.Empty;
    public int? DeferralSequence { get; set; }
    public string? ReplyTo { get; set; }
    public string? ReplyToSessionId { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow;
    public string From { get; set; } = string.Empty;
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }
    public string? HandoffReason { get; set; }
    public string? ExternalJobId { get; set; }
    public DateTime? ExpectedBy { get; set; }
    public bool IsDeferred { get; set; }
    public int ThrottleRetryCount { get; set; }
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public DateTime? HandlerStartedAtUtc { get; set; }
    public HandlerOutcome HandlerOutcome { get; set; }
    public HandoffMetadata? HandoffMetadata { get; set; }

    public Task Complete(CancellationToken ct = default) => Task.CompletedTask;
    public Task Abandon(NimBus.Core.Messages.Exceptions.TransientException ex) => Task.CompletedTask;
    public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task BlockSession(CancellationToken ct = default) => Task.CompletedTask;
    public Task UnblockSession(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetBlockedByEventId(CancellationToken ct = default) => Task.FromResult<string?>(string.Empty);
    public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
    public Task IncrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
    public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(0);
    public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(false);
    public Task ResetDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
}
