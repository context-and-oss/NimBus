#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.Resolver.Services;
using static NimBus.Resolver.Tests.ResolverServiceTests;
using static NimBus.Resolver.Tests.ResolverTestMessages;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The Resolver's endpoint state-change pushes leave the message path and are coalesced per
/// endpoint: a burst costs the WebApp one leading and one trailing call per interval, the last
/// change is always pushed, and a failing WebApp never fails a message.
/// </summary>
[TestClass]
public class CoalescingEndpointStateChangeNotifierTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // Guards awaits on background work so a regression fails instead of hanging; never a sleep.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private const string Endpoint = "BillingEndpoint";

    [TestMethod]
    public async Task Handle_completes_the_message_without_waiting_for_the_webapp_push()
    {
        var handler = new BlockingHandler();
        var http = new HttpEndpointStateChangeNotifier(
            new HttpClient(handler) { BaseAddress = new Uri("http://webapp.local/") });
        await using var notifier = new CoalescingEndpointStateChangeNotifier(http, Interval, new FakeTimeProvider());
        var store = new FakeCosmosDbClient();
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        var service = new ResolverService(store, notifier);

        // The WebApp never answers until released; the message must be completed regardless.
        await service.Handle(message).WaitAsync(Guard);

        Assert.AreEqual(1, message.CompletedCalls);
        await handler.Started.Task.WaitAsync(Guard);
        handler.Release.SetResult();
        await notifier.CurrentFlush.WaitAsync(Guard);
        Assert.AreEqual($"/api/storagehook/cosmos/{Endpoint}", handler.RequestUri?.AbsolutePath);
    }

    [TestMethod]
    public async Task Handle_with_a_failing_webapp_still_completes_the_message()
    {
        var inner = new RecordingInner { Failure = new HttpRequestException("connection refused") };
        await using var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, new FakeTimeProvider());
        var store = new FakeCosmosDbClient();
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        var service = new ResolverService(store, notifier);

        await service.Handle(message);
        await notifier.CurrentFlush.WaitAsync(Guard);

        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
        CollectionAssert.AreEqual(new[] { Endpoint }, inner.Calls.ToArray());
    }

    [TestMethod]
    public async Task A_burst_for_one_endpoint_sends_one_leading_and_one_trailing_notification()
    {
        var time = new FakeTimeProvider();
        var inner = new RecordingInner();
        await using var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, time);

        await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        await notifier.CurrentFlush.WaitAsync(Guard);
        Assert.AreEqual(1, inner.Calls.Count, "The first change after a quiet interval goes out at once.");

        for (var i = 1; i < 100; i++)
        {
            await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        }

        await notifier.CurrentFlush.WaitAsync(Guard);
        Assert.AreEqual(1, inner.Calls.Count, "Changes inside the interval wait for it to end.");

        time.Advance(Interval);
        await notifier.CurrentFlush.WaitAsync(Guard);

        CollectionAssert.AreEqual(new[] { Endpoint, Endpoint }, inner.Calls.ToArray(),
            "100 changes inside one interval cost a leading and a trailing push, the trailing one after the last change.");

        time.Advance(Interval * 5);
        await notifier.CurrentFlush.WaitAsync(Guard);
        Assert.AreEqual(2, inner.Calls.Count, "Nothing is pushed once every change has been sent.");
    }

    [TestMethod]
    public async Task Changes_during_a_running_push_are_sent_when_the_interval_ends()
    {
        var time = new FakeTimeProvider();
        var inner = new RecordingInner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, time);

        await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        var running = notifier.CurrentFlush;
        await inner.FirstCall.Task.WaitAsync(Guard);
        await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        await notifier.NotifyEndpointStateChangedAsync("ErpEndpoint");
        inner.Gate.SetResult();
        await running.WaitAsync(Guard);
        Assert.AreEqual(1, inner.Calls.Count);

        time.Advance(Interval);
        await notifier.CurrentFlush.WaitAsync(Guard);

        CollectionAssert.AreEquivalent(new[] { Endpoint, Endpoint, "ErpEndpoint" }, inner.Calls.ToArray());
    }

    [TestMethod]
    public async Task A_failing_push_is_logged_not_thrown_and_later_changes_are_still_pushed()
    {
        var time = new FakeTimeProvider();
        var inner = new RecordingInner { Failure = new InvalidOperationException("hub down") };
        await using var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, time);

        await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        await notifier.CurrentFlush.WaitAsync(Guard);
        inner.Failure = null;
        await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        time.Advance(Interval);
        await notifier.CurrentFlush.WaitAsync(Guard);

        Assert.AreEqual(2, inner.Calls.Count);
        Assert.AreEqual(1, inner.Delivered);
    }

    [TestMethod]
    public async Task DisposeAsync_sends_the_changes_still_waiting_for_their_interval()
    {
        var time = new FakeTimeProvider();
        var inner = new RecordingInner();
        var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, time);

        await notifier.NotifyEndpointStateChangedAsync(Endpoint);
        await notifier.CurrentFlush.WaitAsync(Guard);
        await notifier.NotifyEndpointStateChangedAsync("ErpEndpoint");
        Assert.AreEqual(1, inner.Calls.Count);

        await notifier.DisposeAsync().AsTask().WaitAsync(Guard);

        CollectionAssert.AreEqual(new[] { Endpoint, "ErpEndpoint" }, inner.Calls.ToArray());
        Assert.IsTrue(inner.Disposed, "The coalescer owns the inner notifier.");

        await notifier.NotifyEndpointStateChangedAsync("LateEndpoint");
        Assert.AreEqual(2, inner.Calls.Count, "Nothing is sent after shutdown.");
    }

    [TestMethod]
    public async Task Endpoints_beyond_the_pending_limit_are_dropped()
    {
        var time = new FakeTimeProvider();
        var inner = new RecordingInner { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, time, maxPendingEndpoints: 1);

        await notifier.NotifyEndpointStateChangedAsync("A");
        var running = notifier.CurrentFlush;
        await inner.FirstCall.Task.WaitAsync(Guard);
        await notifier.NotifyEndpointStateChangedAsync("B");
        await notifier.NotifyEndpointStateChangedAsync("B");
        await notifier.NotifyEndpointStateChangedAsync("C");
        inner.Gate.SetResult();
        await running.WaitAsync(Guard);
        time.Advance(Interval);
        await notifier.CurrentFlush.WaitAsync(Guard);

        CollectionAssert.AreEqual(new[] { "A", "B" }, inner.Calls.ToArray());
    }

    [TestMethod]
    public async Task Heartbeat_and_service_health_pass_straight_through()
    {
        var inner = new RecordingInner();
        await using var notifier = new CoalescingEndpointStateChangeNotifier(inner, Interval, new FakeTimeProvider());

        await notifier.NotifyHeartbeatChangedAsync(Endpoint);
        await notifier.NotifyServiceHealthChangedAsync(Constants.ResolverId);

        CollectionAssert.AreEqual(new[] { $"heartbeat:{Endpoint}", $"servicehealth:{Constants.ResolverId}" }, inner.Calls.ToArray());
    }

    private sealed class RecordingInner : IMessageStateChangeNotifier, IDisposable
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public int Delivered;
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Gate { get; init; }
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public async Task NotifyEndpointStateChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(endpointId);
            FirstCall.TrySetResult();
            if (Gate is not null)
                await Gate.Task;
            if (Failure is not null)
                throw Failure;
            Interlocked.Increment(ref Delivered);
        }

        public Task NotifyHeartbeatChangedAsync(string endpointId, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue($"heartbeat:{endpointId}");
            return Task.CompletedTask;
        }

        public Task NotifyServiceHealthChangedAsync(string serviceId, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue($"servicehealth:{serviceId}");
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Started.TrySetResult();
            await Release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
