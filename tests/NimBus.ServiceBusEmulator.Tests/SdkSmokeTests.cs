#pragma warning disable CA1707, CA2007

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.ServiceBus.Provisioning;

namespace NimBus.ServiceBusEmulator.Tests;

[TestClass]
public sealed class SdkSmokeTests
{
    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_can_enumerate_an_empty_namespace()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        var topics = new List<TopicProperties>();

        await foreach (var topic in admin.GetTopicsAsync(timeout.Token))
        {
            topics.Add(topic);
        }

        Assert.HasCount(0, topics);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_admin_and_data_planes_share_one_port()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entityName = $"sdk-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        var topicOptions = new CreateTopicOptions(entityName)
        {
            SupportOrdering = true,
            EnableBatchedOperations = true,
        };
        var createdTopic = await admin.CreateTopicAsync(topicOptions, timeout.Token);
        Assert.IsTrue(createdTopic.Value.SupportOrdering);

        var createdSubscription = await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer")
            {
                LockDuration = TimeSpan.FromSeconds(30),
                MaxDeliveryCount = 3,
            }, timeout.Token);
        Assert.AreEqual(3, createdSubscription.Value.MaxDeliveryCount);
        Assert.IsTrue(await admin.RuleExistsAsync(entityName, "consumer", "$Default", timeout.Token));

        var client = new ServiceBusClient(emulator.ConnectionString);
        ServiceBusSender sender = client.CreateSender(entityName);
        ServiceBusReceiver receiver = client.CreateReceiver(entityName, "consumer");
        await sender.SendMessageAsync(new ServiceBusMessage("hello") { MessageId = "m1" }, timeout.Token)
            .WaitAsync(TimeSpan.FromSeconds(10));
        ServiceBusReceivedMessage? peeked = await receiver.PeekMessageAsync(cancellationToken: timeout.Token);
        Assert.IsNotNull(peeked);
        Assert.AreEqual("m1", peeked.MessageId);
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10))
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsNotNull(received);
        Assert.AreEqual("hello", received.Body.ToString());
        Assert.AreEqual("m1", received.MessageId);
        Assert.AreEqual(1, received.DeliveryCount);
        Assert.IsTrue(Guid.TryParse(received.LockToken, out var lockToken));
        Assert.AreEqual(16, lockToken.ToByteArray().Length);
        await receiver.CompleteMessageAsync(received);
        await admin.DeleteRuleAsync(entityName, "consumer", "$Default", timeout.Token);
        Assert.IsFalse(await admin.RuleExistsAsync(entityName, "consumer", "$Default", timeout.Token));
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_subscription_created_with_an_initial_rule_has_only_that_rule_and_receives_nothing()
    {
        // Every awaited step is bounded, and the bounds sum to less than the 60 s [Timeout]:
        // startup has a 20 s deadline, each later step 10 s, all capped by a 50 s overall
        // budget, and emulator disposal waits at most 5 s for the process to exit. A hang
        // therefore fails naming its step, with the emulator's protocol trace (AF-9).
        // The protocol order is the original one: send, receive, then delete the topic while
        // the SDK links are still open, and only then dispose receiver, sender and client (the
        // reverse `await using` order), then the emulator. The disposals run as cleanups that
        // always run, even after an earlier failure; a failing cleanup is reported after the
        // body's first failure, never instead of it.
        var clock = Stopwatch.StartNew();
        var emulator = await EmulatorProcess.StartAsync(startupDeadline: TimeSpan.FromSeconds(20));
        var steps = new TestSteps(emulator, clock, TimeSpan.FromSeconds(50));
        var step = TimeSpan.FromSeconds(10);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entityName = $"initial-{Guid.NewGuid():N}";
        ServiceBusClient? client = null;
        ServiceBusSender? sender = null;
        ServiceBusReceiver? receiver = null;
        await TestSteps.RunWithCleanupsAsync(
            async () =>
            {
                var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
                await steps.RunAsync("create topic", step, () => admin.CreateTopicAsync(entityName, timeout.Token));

                await steps.RunAsync("create subscription with initial rule", step, () => admin.CreateSubscriptionAsync(
                    new CreateSubscriptionOptions(entityName, "closed"),
                    new CreateRuleOptions("$Default", new FalseRuleFilter()),
                    timeout.Token));

                var rules = await steps.RunAsync("list rules", step, async () =>
                {
                    var list = new List<RuleProperties>();
                    await foreach (var rule in admin.GetRulesAsync(entityName, "closed", timeout.Token))
                    {
                        list.Add(rule);
                    }

                    return list;
                });

                Assert.HasCount(1, rules);
                Assert.AreEqual("$Default", rules[0].Name);
                Assert.AreEqual("1=0", ((SqlRuleFilter)rules[0].Filter).SqlExpression);
                Assert.IsFalse(rules.Any(rule => rule.Filter is SqlRuleFilter { SqlExpression: "1=1" }));

                // A publish straight after creation lands in the provisioning window.
                client = new ServiceBusClient(emulator.ConnectionString);
                var createdSender = sender = client.CreateSender(entityName);
                await steps.RunAsync("send", step, () => createdSender.SendMessageAsync(new ServiceBusMessage("window"), timeout.Token));

                var createdReceiver = receiver = client.CreateReceiver(entityName, "closed");
                var received = await steps.RunAsync("receive", step, () => createdReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), timeout.Token));
                Assert.IsNull(received);

                // Deleted while the sender and receiver links are still open, as at 1821c42e.
                await steps.RunAsync("delete topic", step, () => admin.DeleteTopicAsync(entityName, timeout.Token));
            },
            () => receiver is null ? Task.CompletedTask : steps.DisposeAsync("receiver", receiver, step),
            () => sender is null ? Task.CompletedTask : steps.DisposeAsync("sender", sender, step),
            () => client is null ? Task.CompletedTask : steps.DisposeAsync("client", client, step),
            () => emulator.DisposeOrThrowAsync().AsTask());
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Receiver_disposes_when_its_topic_is_deleted_while_a_drain_is_outstanding()
    {
        // After an empty receive leaves credit on the link, the receiver's close drains that
        // credit and waits for the drain. When the topic was deleted before the pump answered
        // it, the pump failed and detached without completing the drain, so DisposeAsync
        // hung. The hook holds the pump until the drain arrives; the delete completes before
        // the close sends it, so the pump always reads the deleted topic while draining.
        await using var emulator = await EmulatorProcess.StartAsync(
            environment: new Dictionary<string, string> { ["NIMBUS_SBEMULATOR_TEST_ACQUIRE_HOLD_UNTIL_DRAIN_MS"] = "15000" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entityName = $"deleted-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(entityName, "consumer", timeout.Token);
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        var receiver = client.CreateReceiver(entityName, "consumer");

        Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500), timeout.Token));
        await admin.DeleteTopicAsync(entityName, timeout.Token);

        await receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);

        // The emulator's output reaches DumpOutput through asynchronous pipe callbacks.
        const string drainPathDiagnostic = "Pump failed: draining=True";
        var outputDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!emulator.DumpOutput().Contains(drainPathDiagnostic, StringComparison.Ordinal) && DateTime.UtcNow < outputDeadline)
        {
            await Task.Delay(50, timeout.Token);
        }

        StringAssert.Contains(emulator.DumpOutput(), drainPathDiagnostic, StringComparison.Ordinal);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_subscription_created_without_a_rule_gets_true_default_and_receives_messages()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entityName = $"open-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);

        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(entityName, "open"), timeout.Token);

        var rules = new List<RuleProperties>();
        await foreach (var rule in admin.GetRulesAsync(entityName, "open", timeout.Token))
        {
            rules.Add(rule);
        }

        Assert.HasCount(1, rules);
        Assert.AreEqual("$Default", rules[0].Name);
        Assert.AreEqual("1=1", ((SqlRuleFilter)rules[0].Filter).SqlExpression);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await using var sender = client.CreateSender(entityName);
        await sender.SendMessageAsync(new ServiceBusMessage("open") { MessageId = "m1" }, timeout.Token).WaitAsync(TimeSpan.FromSeconds(10));
        await using var receiver = client.CreateReceiver(entityName, "open");
        var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), timeout.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.IsNotNull(received);
        Assert.AreEqual("m1", received.MessageId);
        await receiver.CompleteMessageAsync(received, timeout.Token);
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_session_receiver_preserves_fifo_under_an_explicit_lock()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var entityName = $"session-{Guid.NewGuid():N}";
            var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
            await admin.CreateTopicAsync(entityName, timeout.Token);
            await admin.CreateSubscriptionAsync(
                new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
                timeout.Token);

            var client = new ServiceBusClient(emulator.ConnectionString);
            ServiceBusSender sender = client.CreateSender(entityName);
            await sender.SendMessagesAsync(
                [
                    new ServiceBusMessage("first") { SessionId = "S" },
                    new ServiceBusMessage("second") { SessionId = "S" },
                ],
                timeout.Token);

            ServiceBusSessionReceiver receiver = await client.AcceptSessionAsync(
                entityName,
                "consumer",
                "S",
                cancellationToken: timeout.Token);
            await receiver.SetSessionStateAsync(BinaryData.FromString("state"), timeout.Token);
            BinaryData? state = await receiver.GetSessionStateAsync(timeout.Token);
            Assert.AreEqual("state", state?.ToString());
            // ReceiveMessagesAsync may return fewer than maxMessages (the SDK only
            // waits a short batch window after the first message), so accumulate
            // until both arrive; the outer timeout token bounds the loop.
            var messages = new List<ServiceBusReceivedMessage>();
            while (messages.Count < 2)
            {
                messages.AddRange(await receiver.ReceiveMessagesAsync(2 - messages.Count, TimeSpan.FromSeconds(5), timeout.Token));
            }
            Assert.HasCount(2, messages);
            Assert.AreEqual("first", messages[0].Body.ToString());
            Assert.AreEqual("second", messages[1].Body.ToString());
            await receiver.CompleteMessageAsync(messages[0], timeout.Token);
            await receiver.CompleteMessageAsync(messages[1], timeout.Token);
            await admin.DeleteTopicAsync(entityName, timeout.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"FIFO test failed; emulator protocol trace:{System.Environment.NewLine}{emulator.DumpOutput()}",
                exception);
        }
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_can_lock_an_explicit_empty_session()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entityName = $"empty-session-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);
        await using var client = new ServiceBusClient(
            emulator.ConnectionString,
            new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    MaxRetries = 0,
                    TryTimeout = TimeSpan.FromSeconds(2),
                },
            });

        await using (ServiceBusSessionReceiver receiver = await client.AcceptSessionAsync(
                         entityName,
                         "consumer",
                         "empty",
                         cancellationToken: timeout.Token))
        {
            Assert.AreEqual("empty", receiver.SessionId);
        }

        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_can_relock_a_materialized_session_after_it_becomes_empty()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entityName = $"empty-materialized-session-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(
            new ServiceBusMessage("message") { SessionId = "materialized" },
            timeout.Token);
        await using (var first = await client.AcceptSessionAsync(
                         entityName,
                         "consumer",
                         "materialized",
                         cancellationToken: timeout.Token))
        {
            var message = await first.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Assert.IsNotNull(message);
            await first.CompleteMessageAsync(message, timeout.Token);
        }

        await using (var second = await client.AcceptSessionAsync(
                         entityName,
                         "consumer",
                         "materialized",
                         cancellationToken: timeout.Token))
        {
            Assert.AreEqual("materialized", second.SessionId);
        }

        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Stock_sdk_rejects_session_receiver_for_non_session_subscription_without_hanging()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var entityName = $"non-session-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(entityName, "consumer", timeout.Token);
        await using var client = new ServiceBusClient(
            emulator.ConnectionString,
            new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    MaxRetries = 0,
                    TryTimeout = TimeSpan.FromSeconds(2),
                },
            });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await client.AcceptSessionAsync(
                entityName,
                "consumer",
                "invalid",
                cancellationToken: timeout.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_contended_explicit_session_reports_session_cannot_be_locked()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"contended-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);

        await using var client = new ServiceBusClient(
            emulator.ConnectionString,
            new ServiceBusClientOptions
            {
                RetryOptions = new ServiceBusRetryOptions
                {
                    MaxRetries = 0,
                    TryTimeout = TimeSpan.FromSeconds(2),
                },
            });
        await client.CreateSender(entityName).SendMessageAsync(
            new ServiceBusMessage("held") { SessionId = "S" },
            timeout.Token);
        await using ServiceBusSessionReceiver first = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() =>
            client.AcceptSessionAsync(entityName, "consumer", "S", cancellationToken: timeout.Token));
        Assert.AreEqual(ServiceBusFailureReason.SessionCannotBeLocked, exception.Reason);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_stale_session_receiver_cannot_change_the_current_owners_state()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entityName = $"ownership-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer")
            {
                RequiresSession = true,
                LockDuration = TimeSpan.FromSeconds(5),
            },
            timeout.Token);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(
            new ServiceBusMessage("held") { SessionId = "S" },
            timeout.Token);
        await using ServiceBusSessionReceiver stale = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);
        await stale.SetSessionStateAsync(BinaryData.FromString("stale"), timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(6), timeout.Token);
        await using ServiceBusSessionReceiver current = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() =>
            stale.SetSessionStateAsync(BinaryData.FromString("overwritten"), timeout.Token));
        Assert.AreEqual(ServiceBusFailureReason.SessionLockLost, exception.Reason);
        Assert.AreEqual("stale", (await current.GetSessionStateAsync(timeout.Token))?.ToString());
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_schedules_and_cancels_messages()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"schedule-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(entityName, "consumer", timeout.Token);

        var client = new ServiceBusClient(emulator.ConnectionString);
        ServiceBusSender sender = client.CreateSender(entityName);
        ServiceBusReceiver receiver = client.CreateReceiver(entityName, "consumer");
        var due = DateTimeOffset.UtcNow.AddMilliseconds(500);
        long cancelled = await sender.ScheduleMessageAsync(new ServiceBusMessage("cancelled"), due, timeout.Token);
        await sender.CancelScheduledMessageAsync(cancelled, timeout.Token);
        await sender.ScheduleMessageAsync(new ServiceBusMessage("delivered"), due, timeout.Token);

        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(received);
        Assert.AreEqual("delivered", received.Body.ToString());
        await receiver.CompleteMessageAsync(received, timeout.Token);
        Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500), timeout.Token));
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(30_000)]
    public async Task Admin_plane_exposes_only_health_without_authentication()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var client = new HttpClient();
        using HttpResponseMessage health = await client.GetAsync(new Uri(emulator.HttpEndpoint, "health"));
        using HttpResponseMessage admin = await client.GetAsync(new Uri(emulator.HttpEndpoint, "$Resources/topics?api-version=2021-05"));
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, admin.StatusCode);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Stock_sdk_provisioner_second_apply_has_zero_mutations()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var mutations = new List<string>();
        var provisioner = new ServiceBusTopologyProvisioner(
            emulator.ConnectionString,
            static () => new TestPlatform(new TestEndpoint("orders")),
            mutations.Add);

        await provisioner.ApplyAsync(timeout.Token);
        Assert.IsNotEmpty(mutations);
        mutations.Clear();

        await provisioner.ApplyAsync(timeout.Token);

        Assert.HasCount(0, mutations);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Process_restart_replays_topology_but_not_messages()
    {
        var journalPath = Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-restart-{Guid.NewGuid():N}", "topology.json");
        var entityName = $"restart-{Guid.NewGuid():N}";
        await using (var first = await EmulatorProcess.StartAsync(journalPath, deleteJournalOnDispose: false))
        {
            var admin = new ServiceBusAdministrationClient(first.ConnectionString);
            await admin.CreateTopicAsync(entityName);
            await admin.CreateSubscriptionAsync(entityName, "consumer");
            await using var client = new ServiceBusClient(first.ConnectionString);
            await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("volatile"));
        }

        await using (var second = await EmulatorProcess.StartAsync(journalPath, deleteJournalOnDispose: true))
        {
            var admin = new ServiceBusAdministrationClient(second.ConnectionString);
            Assert.IsTrue(await admin.TopicExistsAsync(entityName));
            Assert.IsTrue(await admin.SubscriptionExistsAsync(entityName, "consumer"));
            await using var client = new ServiceBusClient(second.ConnectionString);
            var receiver = client.CreateReceiver(entityName, "consumer");
            Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(500)));
            await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("after-restart"));
            var received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(received);
            Assert.AreEqual("after-restart", received.Body.ToString());
        }
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Auto_forward_pause_accumulates_backlog_and_resume_drains_it()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        var source = $"source-{Guid.NewGuid():N}";
        var target = $"target-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(source);
        await admin.CreateTopicAsync(target);
        await admin.CreateSubscriptionAsync(target, "consumer");
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(source, "forward") { ForwardTo = target });

        SubscriptionProperties paused = (await admin.GetSubscriptionAsync(source, "forward")).Value;
        paused.ForwardTo = string.Empty;
        paused.Status = EntityStatus.ReceiveDisabled;
        await admin.UpdateSubscriptionAsync(paused);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        var sender = client.CreateSender(source);
        await sender.SendMessageAsync(new ServiceBusMessage("backlog") { SessionId = "S" });
        SubscriptionRuntimeProperties backlog = (await admin.GetSubscriptionRuntimePropertiesAsync(source, "forward")).Value;
        Assert.AreEqual(1, backlog.ActiveMessageCount);

        SubscriptionProperties resumed = (await admin.GetSubscriptionAsync(source, "forward")).Value;
        resumed.Status = EntityStatus.Active;
        resumed.ForwardTo = target;
        await admin.UpdateSubscriptionAsync(resumed);

        var receiver = client.CreateReceiver(target, "consumer");
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(received);
        Assert.AreEqual("backlog", received.Body.ToString());
        await receiver.CompleteMessageAsync(received);
        await admin.DeleteTopicAsync(source);
        await admin.DeleteTopicAsync(target);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(120_000)]
    public async Task Session_processor_activates_two_hundred_sessions_on_one_client()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        var entityName = $"sessions-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName);
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true });
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        var processor = client.CreateSessionProcessor(
            entityName,
            "consumer",
            new ServiceBusSessionProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentSessions = 200,
                MaxConcurrentCallsPerSession = 1,
                SessionIdleTimeout = TimeSpan.FromSeconds(2),
            });
        var sessions = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new ConcurrentQueue<Exception>();
        processor.ProcessMessageAsync += async args =>
        {
            sessions.TryAdd(args.Message.SessionId, 0);
            await args.CompleteMessageAsync(args.Message);
            if (sessions.Count == 200)
            {
                completed.TrySetResult();
            }
        };
        processor.ProcessErrorAsync += args =>
        {
            errors.Enqueue(args.Exception);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync();
        var messages = Enumerable.Range(0, 200)
            .Select(index => new ServiceBusMessage(index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                SessionId = $"S-{index}",
            })
            .ToArray();
        await client.CreateSender(entityName).SendMessagesAsync(messages);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await processor.StopProcessingAsync();

        Assert.HasCount(200, sessions);
        Assert.HasCount(0, errors, string.Join(System.Environment.NewLine, errors));
        await admin.DeleteTopicAsync(entityName);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Stock_sdk_in_flight_handler_settles_after_stop_cancels_its_token()
    {
        // NimBus stops the session processor when the endpoint circuit opens, which cancels the
        // token of every in-flight handler, and StrictMessageHandler then finishes the
        // delivery's settlement on a token of its own. That relies on StopProcessingAsync
        // keeping the session receiver and its lock until in-flight handlers return.
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var entityName = $"stop-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { RequiresSession = true },
            timeout.Token);
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(
            new ServiceBusMessage("in-flight") { SessionId = "S" },
            timeout.Token);

        var processor = client.CreateSessionProcessor(
            entityName,
            "consumer",
            new ServiceBusSessionProcessorOptions { AutoCompleteMessages = false, MaxConcurrentSessions = 1 });
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        processor.ProcessMessageAsync += async args =>
        {
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, args.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The stop cancelled the handler's token.
            }

            try
            {
                await args.SetSessionStateAsync(BinaryData.FromString("blocked"), CancellationToken.None);
                await args.CompleteMessageAsync(args.Message, CancellationToken.None);
                settled.TrySetResult();
            }
            catch (Exception exception)
            {
                settled.TrySetException(exception);
            }
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;

        await processor.StartProcessingAsync(timeout.Token);
        await handlerStarted.Task.WaitAsync(timeout.Token);
        await processor.StopProcessingAsync(timeout.Token);
        await settled.Task.WaitAsync(timeout.Token);
        await processor.DisposeAsync();

        await using var receiver = await client.AcceptSessionAsync(
            entityName,
            "consumer",
            "S",
            cancellationToken: timeout.Token);
        Assert.AreEqual("blocked", (await receiver.GetSessionStateAsync(timeout.Token))?.ToString());
        Assert.IsNull(
            await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), timeout.Token),
            "The message completed after the stop must not be redelivered.");
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Broker_assigns_a_message_id_when_the_sender_omits_it()
    {
        // NimBus's ResponseService.CreateResponse relies on the broker assigning the
        // outgoing response its own id when the sender leaves MessageId unset (the
        // response to a CloudEvents-ingested message without a native MessageId).
        // Without this, such responses poison-loop the Resolver with
        // InvalidMessageException("MessageId is not defined.").
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var entityName = $"noid-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName, timeout.Token);
        await admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(entityName, "consumer"), timeout.Token);

        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("no-id"), timeout.Token);

        ServiceBusReceiver receiver = client.CreateReceiver(entityName, "consumer");
        ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(10), timeout.Token);
        Assert.IsNotNull(received);
        Assert.IsFalse(string.IsNullOrEmpty(received.MessageId));
        await receiver.CompleteMessageAsync(received, timeout.Token);
        await admin.DeleteTopicAsync(entityName, timeout.Token);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Abandon_increments_delivery_count_and_max_delivery_moves_to_dlq()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        var entityName = $"delivery-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(entityName);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(entityName, "consumer") { MaxDeliveryCount = 3 });
        await using var client = new ServiceBusClient(emulator.ConnectionString);
        await client.CreateSender(entityName).SendMessageAsync(new ServiceBusMessage("retry"));
        var receiver = client.CreateReceiver(entityName, "consumer");

        for (var expectedDelivery = 1; expectedDelivery <= 3; expectedDelivery++)
        {
            ServiceBusReceivedMessage? received = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(received);
            Assert.AreEqual(expectedDelivery, received.DeliveryCount);
            await receiver.AbandonMessageAsync(received);
        }

        SubscriptionRuntimeProperties runtime = (await admin.GetSubscriptionRuntimePropertiesAsync(entityName, "consumer")).Value;
        Assert.AreEqual(0, runtime.ActiveMessageCount);
        Assert.AreEqual(1, runtime.DeadLetterMessageCount);
        Assert.AreEqual(1, runtime.TotalMessageCount);
        await admin.DeleteTopicAsync(entityName);
    }

    [TestMethod]
    [TestCategory("CommonFidelity")]
    [Timeout(60_000)]
    public async Task Resolver_dead_letter_can_be_peeked_and_atomically_replayed()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var topic = $"resolver-replay-{Guid.NewGuid():N}";
        const string subscription = "Resolver";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(topic, timeout.Token);
        await admin.CreateSubscriptionAsync(
            new CreateSubscriptionOptions(topic, subscription) { RequiresSession = true },
            timeout.Token);

        await using (var setup = new ServiceBusClient(emulator.ConnectionString))
        {
            await setup.CreateSender(topic).SendMessageAsync(
                new ServiceBusMessage("source")
                {
                    MessageId = "original",
                    SessionId = "session",
                },
                timeout.Token);
            await using var session = await setup.AcceptSessionAsync(
                topic, subscription, "session", cancellationToken: timeout.Token);
            var source = await session.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Assert.IsNotNull(source);
            await session.DeadLetterMessageAsync(source, "CosmosDbThrottled", cancellationToken: timeout.Token);
        }

        await using var client = new ServiceBusClient(
            emulator.ConnectionString,
            new ServiceBusClientOptions { EnableCrossEntityTransactions = true });
        await using var receiver = client.CreateReceiver(
            topic,
            subscription,
            new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
        var peeked = await receiver.PeekMessageAsync(cancellationToken: timeout.Token);
        Assert.IsNotNull(peeked);
        Assert.AreEqual("CosmosDbThrottled", peeked.DeadLetterReason);

        var deadLetter = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(deadLetter);
        await using var sender = client.CreateSender(topic);
        using (var transaction = new TransactionScope(
                   TransactionScopeOption.Required,
                   TransactionScopeAsyncFlowOption.Enabled))
        {
            await receiver.CompleteMessageAsync(deadLetter, timeout.Token);
            await sender.SendMessageAsync(
                new ServiceBusMessage(deadLetter) { MessageId = "replayed" },
                timeout.Token);
            transaction.Complete();
        }

        await using var verificationClient = new ServiceBusClient(emulator.ConnectionString);
        await using var replaySession = await verificationClient.AcceptSessionAsync(
            topic, subscription, "session", cancellationToken: timeout.Token);
        var replay = await replaySession.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(replay);
        Assert.AreEqual("replayed", replay.MessageId);
        await replaySession.CompleteMessageAsync(replay, timeout.Token);
        Assert.IsNull(await receiver.ReceiveMessageAsync(TimeSpan.FromMilliseconds(250), timeout.Token));

        await sender.SendMessageAsync(
            new ServiceBusMessage("rollback-source")
            {
                MessageId = "rollback-original",
                SessionId = "rollback-session",
            },
            timeout.Token);
        await using (var rollbackSession = await verificationClient.AcceptSessionAsync(
                         topic, subscription, "rollback-session", cancellationToken: timeout.Token))
        {
            var rollbackSource = await rollbackSession.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
            Assert.IsNotNull(rollbackSource);
            await rollbackSession.DeadLetterMessageAsync(
                rollbackSource,
                "CosmosDbThrottled",
                cancellationToken: timeout.Token);
        }

        var rollbackDeadLetter = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(rollbackDeadLetter);
        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            await receiver.CompleteMessageAsync(rollbackDeadLetter, timeout.Token);
            await sender.SendMessageAsync(
                new ServiceBusMessage(rollbackDeadLetter) { MessageId = "must-not-publish" },
                timeout.Token);
        }

        SubscriptionRuntimeProperties afterRollback =
            (await admin.GetSubscriptionRuntimePropertiesAsync(topic, subscription, timeout.Token)).Value;
        Assert.AreEqual(0, afterRollback.ActiveMessageCount);
        Assert.AreEqual(1, afterRollback.DeadLetterMessageCount);
        var retained = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(retained);
        Assert.AreEqual("rollback-original", retained.MessageId);
        await receiver.CompleteMessageAsync(retained, timeout.Token);
        await admin.DeleteTopicAsync(topic, timeout.Token);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Transactional_complete_on_regular_subscription_fails_without_hanging()
    {
        await using var emulator = await EmulatorProcess.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var topic = $"transaction-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);
        await admin.CreateTopicAsync(topic, timeout.Token);
        await admin.CreateSubscriptionAsync(topic, "consumer", timeout.Token);
        await using var client = new ServiceBusClient(
            emulator.ConnectionString,
            new ServiceBusClientOptions { EnableCrossEntityTransactions = true });
        await client.CreateSender(topic).SendMessageAsync(new ServiceBusMessage("message"), timeout.Token);
        await using var receiver = client.CreateReceiver(topic, "consumer");
        var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(5), timeout.Token);
        Assert.IsNotNull(message);

        using var transaction = new TransactionScope(
            TransactionScopeOption.Required,
            TransactionScopeAsyncFlowOption.Enabled);
        var exception = await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
            await receiver.CompleteMessageAsync(message, timeout.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        StringAssert.Contains(exception.Message, "transactional", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("EmulatorOnly")]
    [Timeout(60_000)]
    public async Task Failed_topology_journal_save_rolls_back_admin_mutation()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-invalid-journal-{Guid.NewGuid():N}");
        var journalPath = Path.Combine(temporaryRoot, "topology.json");
        Directory.CreateDirectory(journalPath);
        await using var emulator = await EmulatorProcess.StartAsync(journalPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var topic = $"not-durable-{Guid.NewGuid():N}";
        var admin = new ServiceBusAdministrationClient(emulator.ConnectionString);

        var exception = await Assert.ThrowsExactlyAsync<ServiceBusException>(() =>
            admin.CreateTopicAsync(topic, timeout.Token));

        Assert.AreEqual(ServiceBusFailureReason.GeneralError, exception.Reason);
        Assert.IsFalse(await admin.TopicExistsAsync(topic, timeout.Token));
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    private sealed class TestEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
