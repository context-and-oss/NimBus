#pragma warning disable CA1707, CA2007

using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.Hosting;
using NimBus.ServiceBus;

namespace NimBus.ServiceBusEmulator.Tests;

/// <summary>
/// A transient failure must not let later messages in the same session overtake the failed one.
/// Runs the production receive path: <see cref="NimBusReceiverHostedService"/> drives a
/// <see cref="ServiceBusSessionProcessor"/> into <see cref="ServiceBusAdapter"/> and
/// <see cref="MessageHandler"/>, whose transient branch calls <see cref="IMessageContext.Abandon"/>.
/// </summary>
[TestClass]
public sealed class TransientFailureSessionOrderTests
{
    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(90_000)]
    [DataRow(0, DisplayName = "No prefetch")]
    [DataRow(10, DisplayName = "Prefetch holds E2 locally")]
    public async Task Transient_failure_redelivers_the_failed_message_before_the_rest_of_its_session(int prefetchCount)
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var entityName = $"transient-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await using (var sender = client.CreateSender(entityName))
        {
            await sender.SendMessagesAsync(new[] { Event("E1"), Event("E2") }, timeout.Token);
        }

        var handler = new FailFirstDeliveryHandler("E1", expectedCalls: 3);
        var adapter = new ServiceBusAdapter(handler, client, $"{entityName}/consumer");
        var receiver = new NimBusReceiverHostedService(
            client,
            adapter,
            new NimBusReceiverOptions
            {
                TopicName = entityName,
                SubscriptionName = "consumer",
                MaxConcurrentSessions = 1,
                PrefetchCount = prefetchCount,
                // Left unsettled, E1 comes back only once the session goes idle, after E2. A
                // short idle timeout makes that regression fail fast instead of timing out.
                SessionIdleTimeout = TimeSpan.FromSeconds(3),
            },
            NullLogger<NimBusReceiverHostedService>.Instance);

        await receiver.StartAsync(timeout.Token);
        try
        {
            await handler.AllCalls.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await receiver.StopAsync(CancellationToken.None);
            receiver.Dispose();
        }

        CollectionAssert.AreEqual(
            new[] { "E1", "E1", "E2" },
            handler.Observed.ToArray(),
            $"Observed delivery order: {string.Join(", ", handler.Observed)}");
        await admin.DeleteTopicAsync(entityName, CancellationToken.None);
    }

    private static Azure.Messaging.ServiceBus.ServiceBusMessage Event(string eventId) =>
        new(BinaryData.FromString("{}"))
        {
            MessageId = eventId,
            SessionId = "S",
            ApplicationProperties =
            {
                [nameof(UserPropertyName.MessageType)] = nameof(MessageType.EventRequest),
                [nameof(UserPropertyName.EventId)] = eventId,
            },
        };

    private sealed class FailFirstDeliveryHandler : MessageHandler
    {
        private readonly string _failingEventId;
        private readonly int _expectedCalls;
        private int _failures;

        public FailFirstDeliveryHandler(string failingEventId, int expectedCalls)
            : base(NullLogger.Instance)
        {
            _failingEventId = failingEventId;
            _expectedCalls = expectedCalls;
        }

        public ConcurrentQueue<string> Observed { get; } = new();

        public TaskCompletionSource AllCalls { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            var eventId = messageContext.EventId;
            Observed.Enqueue(eventId);
            if (Observed.Count >= _expectedCalls)
            {
                AllCalls.TrySetResult();
            }

            if (eventId == _failingEventId && Interlocked.Increment(ref _failures) == 1)
            {
                throw new TransientException("Simulated transient failure.");
            }

            await messageContext.Complete(cancellationToken);
        }
    }
}
