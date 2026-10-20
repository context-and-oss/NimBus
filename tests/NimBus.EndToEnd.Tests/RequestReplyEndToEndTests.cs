#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.Testing;
using Newtonsoft.Json;

namespace NimBus.EndToEnd.Tests;

[TestClass]
public sealed class RequestReplyEndToEndTests
{
    [SessionKey(nameof(AccountId))]
    private sealed class CreditCheckRequest : Event
    {
        public string AccountId { get; set; } = "acc-1";
    }

    private sealed class CreditCheckResult
    {
        public string AccountId { get; set; } = null!;
        public bool Approved { get; set; }
    }

    private sealed class ApprovingHandler : IRequestHandler<CreditCheckRequest, CreditCheckResult>
    {
        public int Invocations { get; private set; }

        public Task<CreditCheckResult> Handle(CreditCheckRequest request, CancellationToken cancellationToken = default)
        {
            Invocations++;
            return Task.FromResult(new CreditCheckResult { AccountId = request.AccountId, Approved = true });
        }
    }

    private sealed class FailingHandler : IRequestHandler<CreditCheckRequest, CreditCheckResult>
    {
        public Task<CreditCheckResult> Handle(CreditCheckRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("credit system down");
    }

    private static async Task<Message> PublishRequestAsync(EndToEndFixture fixture, string messageId)
    {
        await fixture.Publisher.Publish(new CreditCheckRequest(), "acc-1", "corr-e2e", messageId);
        var message = (Message)fixture.PublishBus.SentMessages[^1];
        message.ReplyTo = "RequesterEndpoint";
        message.ReplyToSessionId = "reply-session-e2e";
        return message;
    }

    [TestMethod]
    public async Task Request_RoundTrip_SendsReplyAndResolvesNormally()
    {
        var fixture = new EndToEndFixture();
        var handler = new ApprovingHandler();
        fixture.RegisterRequestHandler<CreditCheckRequest, CreditCheckResult>(() => handler);

        await PublishRequestAsync(fixture, "req-e2e-1");
        var results = await fixture.DeliverAllWithResults();

        Assert.IsNull(results.Single().Exception);
        Assert.IsTrue(results.Single().Session.WasCompleted);
        Assert.AreEqual(1, handler.Invocations);

        var reply = fixture.ReplyDispatcher.SentReplies.Single();
        Assert.AreEqual("RequesterEndpoint", reply.ReplyTo);
        Assert.AreEqual("reply-session-e2e", reply.ReplySessionId);
        Assert.AreEqual("corr-e2e", reply.CorrelationId);
        Assert.IsFalse(reply.IsError);
        var payload = JsonConvert.DeserializeObject<CreditCheckResult>(reply.PayloadJson!)!;
        Assert.IsTrue(payload.Approved);
        Assert.AreEqual("acc-1", payload.AccountId);

        // The request is a normal EventRequest: it still resolves through the Resolver path.
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.ResolutionResponse));
    }

    [TestMethod]
    public async Task FailingHandler_SendsErrorReplyAndTakesNormalFailurePath()
    {
        var fixture = new EndToEndFixture();
        fixture.RegisterRequestHandler<CreditCheckRequest, CreditCheckResult>(() => new FailingHandler());

        await PublishRequestAsync(fixture, "req-e2e-err");
        var results = await fixture.DeliverAllWithResults();

        var reply = fixture.ReplyDispatcher.SentReplies.Single();
        Assert.IsTrue(reply.IsError);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, reply.ErrorType);
        Assert.AreEqual("credit system down", reply.ErrorText);

        // The failure still drives the normal error path (session blocked / error response).
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.ErrorResponse));
        Assert.IsFalse(results.Single().Session.WasCompleted && fixture.ResponseBus.SentMessages.Count == 0);
    }

    [TestMethod]
    public async Task PlainPublishedRequest_NoReplyTo_RunsHandlerWithoutReply()
    {
        var fixture = new EndToEndFixture();
        var handler = new ApprovingHandler();
        fixture.RegisterRequestHandler<CreditCheckRequest, CreditCheckResult>(() => handler);

        await fixture.Publisher.Publish(new CreditCheckRequest(), "acc-1", "corr-plain", "req-plain-1");
        await fixture.DeliverAll();

        Assert.AreEqual(1, handler.Invocations);
        Assert.AreEqual(0, fixture.ReplyDispatcher.SentReplies.Count);
    }

    [TestMethod]
    public async Task InboxDuplicateRequest_SkipsHandlerAndSendsNoSecondReply()
    {
        var store = new InMemoryInboxStore();
        var notifier = new MessageLifecycleNotifier(Array.Empty<Core.Extensions.IMessageLifecycleObserver>());
        var fixture = EndToEndFixture.CreateWithHandlerDecorator(
            inner => new InboxMiddleware(inner, store, notifier),
            notifier);
        var handler = new ApprovingHandler();
        fixture.RegisterRequestHandler<CreditCheckRequest, CreditCheckResult>(() => handler);

        var message = await PublishRequestAsync(fixture, "req-e2e-dup");
        await fixture.DeliverAll();
        Assert.AreEqual(1, handler.Invocations);
        Assert.AreEqual(1, fixture.ReplyDispatcher.SentReplies.Count);

        // Redeliver the same message (same broker MessageId): the inbox skips the
        // handler, so no second reply is sent — the requester of a duplicate times
        // out. This documents the at-most-one-reply contract.
        await fixture.PublishBus.Send(message);
        await fixture.DeliverAll();

        Assert.AreEqual(1, handler.Invocations, "Duplicate must not re-execute the request handler.");
        Assert.AreEqual(1, fixture.ReplyDispatcher.SentReplies.Count, "Duplicate must not produce a second reply.");
    }
}
