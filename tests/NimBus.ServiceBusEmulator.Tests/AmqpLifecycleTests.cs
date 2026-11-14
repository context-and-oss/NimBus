#pragma warning disable CA1707, CA2007

using System.Net;
using System.Net.Sockets;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.ServiceBusEmulator.Broker;
using NimBus.ServiceBusEmulator.Protocol;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class AmqpLifecycleTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Worker_session_deadletter_reaches_the_broker_with_its_reason()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var processor = client.CreateSessionProcessor("events", "consumer", new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false,
        });
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.ProcessMessageAsync += async args =>
        {
            var session = new NimBus.ServiceBus.ServiceBusSession(args);
            await session.DeadLetterAsync(new NimBus.ServiceBus.ServiceBusMessage(args.Message), "rejected", "invalid business input");
            settled.TrySetResult();
        };
        processor.ProcessErrorAsync += args => { settled.TrySetException(args.Exception); return Task.CompletedTask; };
        await processor.StartProcessingAsync();
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("poison") { SessionId = "S" });
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using var deadletters = client.CreateReceiver("events", "consumer", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var deadletter = await deadletters.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(deadletter);
        Assert.AreEqual("rejected", deadletter.DeadLetterReason);
        Assert.AreEqual("invalid business input", deadletter.DeadLetterErrorDescription);
        await deadletters.CompleteMessageAsync(deadletter);
        await processor.StopProcessingAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Unsettled_session_message_redelivers_after_lock_expiry()
    {
        var broker = CreateBroker();
        broker.UpdateSubscription("events", new SubscriptionDefinition("consumer")
        {
            RequiresSession = true, LockDuration = TimeSpan.FromSeconds(1), MaxDeliveryCount = 3,
        });
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var processor = client.CreateSessionProcessor("events", "consumer", new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false, MaxAutoLockRenewalDuration = TimeSpan.Zero,
            SessionIdleTimeout = TimeSpan.FromSeconds(2), MaxConcurrentSessions = 2,
        });
        var delivered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        processor.ProcessMessageAsync += async args =>
        {
            if (Interlocked.Increment(ref attempts) == 1) return;
            await args.CompleteMessageAsync(args.Message);
            delivered.TrySetResult(args.Message.DeliveryCount);
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync();
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("retry") { SessionId = "S" });
        Assert.AreEqual(2, await delivered.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        await processor.StopProcessingAsync();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Closing_a_pending_session_attach_keeps_other_links_on_the_connection_usable()
    {
        var broker = CreateBroker();
        using var failures = new FailureLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(failures));
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker, loggerFactory: loggerFactory);
        frontend.Start();
        var factory = new Amqp.ConnectionFactory();
        factory.SASL.Profile = new MssbcbsSaslProfile();
        var connection = await factory.CreateAsync(new Amqp.Address($"amqp://127.0.0.1:{port}"));
        try
        {
            var session = new Amqp.Session(connection);
            var sender = new Amqp.SenderLink(session, "sender", "events");
            var pending = new Amqp.ReceiverLink(session, "pending", new Amqp.Framing.Source
            {
                Address = "events/Subscriptions/consumer",
                FilterSet = new Amqp.Types.Map { [new Amqp.Types.Symbol("com.microsoft:session-filter")] = null },
            }, null);
            // No session is available. Detach before the asynchronous attach completes.
            pending.Close(TimeSpan.Zero);
            var message = new Amqp.Message
            {
                BodySection = new Amqp.Framing.Data { Binary = "connection remains usable"u8.ToArray() },
                Properties = new Amqp.Framing.Properties { GroupId = "S" },
            };
            await sender.SendAsync(message).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNull(connection.Error);
            Assert.IsFalse(failures.Failure.Task.IsCompleted, "Cancelling a pending attach is normal link cleanup.");
        }
        finally
        {
            await connection.CloseAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Deleting_subscription_while_receiving_closes_the_link_promptly()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var failures = new FailureLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(failures));
        using var frontend = new AmqpFrontend(port, broker, loggerFactory: loggerFactory);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var receiver = await client.AcceptSessionAsync("events", "consumer", "S");
        // Complete a delivery first, proving the receive pump is attached.
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("first") { SessionId = "S" });
        var first = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(first);
        await receiver.CompleteMessageAsync(first);

        var receive = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        broker.DeleteSubscription("events", "consumer");
        // The SDK completes an outstanding receive with no message on remote detach.
        Assert.IsNull(await receive.WaitAsync(TimeSpan.FromSeconds(4)));
        // From SDK 7.21 the lost session link closes the receiver itself, so later calls fail
        // locally with ObjectDisposedException rather than SessionLockLost.
        Assert.IsTrue(receiver.IsClosed);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await receiver.RenewSessionLockAsync());
        Assert.IsInstanceOfType<KeyNotFoundException>(await failures.Failure.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Lost_session_settlement_through_NimBus_reports_session_lock_lost()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var receiver = await client.AcceptSessionAsync("events", "consumer", "S");
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("held") { SessionId = "S" });
        var held = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(held);

        var receive = receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10));
        broker.DeleteSubscription("events", "consumer");
        Assert.IsNull(await receive.WaitAsync(TimeSpan.FromSeconds(4)));

        // MessageContext retries a lost session lock; any other failure dead-letters the message
        // and reports it to the Resolver, although the broker redelivers it to the next session owner.
        var session = new NimBus.ServiceBus.ServiceBusSession(receiver);
        var message = new NimBus.ServiceBus.ServiceBusMessage(held);
        await AssertSessionLockLost(() => session.CompleteAsync(message));
        await AssertSessionLockLost(() => session.DeadLetterAsync(message, "reason", null));
        await AssertSessionLockLost(() => session.GetStateAsync());
        await AssertSessionLockLost(() => session.SetStateAsync(new NimBus.Core.Messages.SessionState()));
        await Assert.ThrowsExactlyAsync<NimBus.Core.Messages.Exceptions.TransientException>(() =>
            new NimBus.ServiceBus.MessageContext(message, session).Complete());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Empty_session_accept_returns_service_timeout_and_keeps_connection_usable()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);

        var exception = await Assert.ThrowsAsync<ServiceBusException>(async () =>
            await client.AcceptNextSessionAsync("events", "consumer"));
        Assert.AreEqual(ServiceBusFailureReason.ServiceTimeout, exception.Reason);

        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("after timeout") { SessionId = "S" });
        await using var receiver = await client.AcceptNextSessionAsync("events", "consumer");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(message);
        await receiver.CompleteMessageAsync(message);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Empty_receives_drain_promptly_and_session_can_renew_and_receive_again()
    {
        var broker = CreateBroker();
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var receiver = await client.AcceptSessionAsync("events", "consumer", "S");

        for (var iteration = 0; iteration < 3; iteration++)
        {
            Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(3)));
        }

        await receiver.RenewSessionLockAsync();
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("after drain") { SessionId = "S" });
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
        Assert.IsNotNull(message);
        Assert.AreEqual("after drain", message.Body.ToString());
        await receiver.CompleteMessageAsync(message);
    }

    private static async Task AssertSessionLockLost(Func<Task> operation)
    {
        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(operation);
        Assert.AreEqual(ServiceBusFailureReason.SessionLockLost, exception.Reason);
    }

    // Regression: the nightly CrmErpDemo suite. The deferred-processor trigger subscription is
    // read by a processor with one concurrent call. While its handler ran for five seconds
    // with another trigger queued behind it, the receive link closed after 500 ms with "The
    // broker could not complete the link operation", the trigger's lock was lost, and the
    // trigger was redelivered in a loop that starved every other session.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Processor_keeps_its_lock_while_a_second_message_waits_behind_a_slow_handler()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("triggers"));
        using var failures = new FailureLogger();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(failures));
        var port = GetPort();
        using var frontend = new AmqpFrontend(port, broker, loggerFactory: loggerFactory);
        frontend.Start();
        await using var client = CreateClient(port);
        await using var sender = client.CreateSender("events");
        await sender.SendMessageAsync(new ServiceBusMessage("first"));
        await sender.SendMessageAsync(new ServiceBusMessage("second"));

        var handled = new List<(string Body, int DeliveryCount)>();
        var errors = new List<Exception>();
        var bothHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var processor = client.CreateProcessor("events", "triggers", new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false,
        });
        processor.ProcessMessageAsync += async args =>
        {
            // Longer than the pump's 500 ms zero-credit check, with the other message waiting.
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            await args.CompleteMessageAsync(args.Message);
            lock (handled)
            {
                handled.Add((args.Message.Body.ToString(), args.Message.DeliveryCount));
                if (handled.Count == 2) bothHandled.TrySetResult();
            }
        };
        processor.ProcessErrorAsync += args =>
        {
            lock (errors) errors.Add(args.Exception);
            return Task.CompletedTask;
        };
        await processor.StartProcessingAsync();

        await Task.WhenAny(bothHandled.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        await processor.StopProcessingAsync();

        var pumpFailure = failures.Failure.Task.IsCompleted ? failures.Failure.Task.Result.ToString() : "none";
        Assert.AreEqual(0, errors.Count, $"Processor errors: {string.Join(" | ", errors.Select(e => e.Message))}. Emulator pump failure: {pumpFailure}");
        CollectionAssert.AreEqual(
            new[] { ("first", 1), ("second", 1) },
            handled,
            $"Each message is handled once, in order, on its first delivery. Emulator pump failure: {pumpFailure}");
    }

    private static BrokerNamespace CreateBroker()
    {
        var broker = new BrokerNamespace(new BrokerOptions());
        broker.CreateTopic(new TopicDefinition("events"));
        broker.CreateSubscription("events", new SubscriptionDefinition("consumer") { RequiresSession = true });
        return broker;
    }

    private static ServiceBusClient CreateClient(int port) => new(
        $"Endpoint=sb://127.0.0.1:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=local;UseDevelopmentEmulator=true;",
        new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { MaxRetries = 0, TryTimeout = TimeSpan.FromSeconds(2) },
        });

    private static int GetPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FailureLogger : ILoggerProvider, ILogger
    {
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => this;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                Failure.TrySetResult(exception);
            }
        }

        public void Dispose()
        {
        }
    }
}
