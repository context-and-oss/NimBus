#pragma warning disable CA1707, CA2007

using System.Diagnostics;

namespace NimBus.ServiceBusEmulator.Tests;

/// <summary>
/// Covers the test-harness diagnostics that make an emulator hang fail fast and name the
/// step that hung: <see cref="TestSteps"/> and the bounded <see cref="EmulatorProcess"/> startup.
/// </summary>
[TestClass]
public sealed class TestDiagnosticsTests
{
    [TestMethod]
    [Timeout(10_000)]
    public async Task A_step_that_overruns_its_budget_fails_naming_the_step_and_carrying_the_output()
    {
        var steps = new TestSteps(() => "emulator trace line");
        var never = new TaskCompletionSource();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => steps.RunAsync("receive", TimeSpan.FromMilliseconds(100), () => never.Task));

        StringAssert.Contains(exception.Message, "Step 'receive' did not finish within");
        StringAssert.Contains(exception.Message, "emulator trace line");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_step_is_capped_by_the_remaining_overall_budget()
    {
        var steps = new TestSteps(() => string.Empty, Stopwatch.StartNew(), TimeSpan.FromMilliseconds(200));
        var never = new TaskCompletionSource();
        var watch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => steps.RunAsync("dispose client", TimeSpan.FromSeconds(30), () => never.Task));

        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds);
        StringAssert.Contains(exception.Message, "Step 'dispose client'");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_step_cancelled_by_the_test_token_fails_naming_the_step()
    {
        var steps = new TestSteps(() => "trace");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => steps.RunAsync("create topic", TimeSpan.FromSeconds(5), () => Task.Delay(Timeout.Infinite, cancelled.Token)));

        StringAssert.Contains(exception.Message, "Step 'create topic' was cancelled");
        StringAssert.Contains(exception.Message, "trace");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_step_that_completes_returns_its_result()
    {
        var steps = new TestSteps(() => string.Empty);

        var result = await steps.RunAsync("list rules", TimeSpan.FromSeconds(5), () => Task.FromResult(42));

        Assert.AreEqual(42, result);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_body_step_timeout_followed_by_a_failing_cleanup_still_reports_the_body_step_first()
    {
        var steps = new TestSteps(() => "trace");
        var never = new TaskCompletionSource();

        var exception = await Assert.ThrowsExactlyAsync<AggregateException>(() => TestSteps.RunWithCleanupAsync(
            () => steps.RunAsync("receive", TimeSpan.FromMilliseconds(100), () => never.Task),
            () => steps.RunAsync("dispose client", TimeSpan.FromMilliseconds(100), () => never.Task)));

        Assert.HasCount(2, exception.InnerExceptions);
        StringAssert.Contains(exception.InnerExceptions[0].Message, "Step 'receive' did not finish within");
        StringAssert.Contains(exception.InnerExceptions[1].Message, "Step 'dispose client' did not finish within");
        Assert.IsLessThan(
            exception.Message.IndexOf("Step 'dispose client'", StringComparison.Ordinal),
            exception.Message.IndexOf("Step 'receive'", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_body_failure_with_a_clean_cleanup_rethrows_the_body_failure_unchanged()
    {
        var steps = new TestSteps(() => "trace");
        var never = new TaskCompletionSource();
        var cleanedUp = false;

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() => TestSteps.RunWithCleanupAsync(
            () => steps.RunAsync("send", TimeSpan.FromMilliseconds(100), () => never.Task),
            () =>
            {
                cleanedUp = true;
                return Task.CompletedTask;
            }));

        Assert.IsTrue(cleanedUp);
        StringAssert.Contains(exception.Message, "Step 'send' did not finish within");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_failing_cleanup_after_a_successful_body_fails_as_its_own_named_step()
    {
        var steps = new TestSteps(() => "trace");
        var never = new TaskCompletionSource();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() => TestSteps.RunWithCleanupAsync(
            () => Task.CompletedTask,
            () => steps.RunAsync("dispose client", TimeSpan.FromMilliseconds(100), () => never.Task)));

        StringAssert.Contains(exception.Message, "Step 'dispose client' did not finish within");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task A_failing_emulator_dispose_after_a_failing_body_still_reports_the_body_failure_first()
    {
        if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 })
        {
            Assert.Inconclusive("An external emulator is configured; its process is not owned by the test.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-dispose-{Guid.NewGuid():N}");
        var journalPath = Path.Combine(directory, "topology.json");
        try
        {
            var emulator = await EmulatorProcess.StartAsync(journalPath, startupDeadline: TimeSpan.FromSeconds(20));

            // Simulate a process that does not exit within the bound after being killed.
            emulator.WaitForExitOverride = _ => false;
            var steps = new TestSteps(emulator.DumpOutput);
            var never = new TaskCompletionSource();

            var exception = await Assert.ThrowsExactlyAsync<AggregateException>(() => TestSteps.RunWithCleanupAsync(
                () => steps.RunAsync("receive", TimeSpan.FromMilliseconds(100), () => never.Task),
                () => emulator.DisposeOrThrowAsync().AsTask()));

            Assert.HasCount(2, exception.InnerExceptions);
            StringAssert.Contains(exception.InnerExceptions[0].Message, "Step 'receive' did not finish within");
            Assert.IsInstanceOfType<TimeoutException>(exception.InnerExceptions[1]);
            StringAssert.Contains(exception.InnerExceptions[1].Message, "Step 'dispose emulator'");
            StringAssert.Contains(exception.InnerExceptions[1].Message, "Emulator output:");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task The_default_emulator_dispose_logs_an_exit_timeout_and_does_not_throw()
    {
        if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 })
        {
            Assert.Inconclusive("An external emulator is configured; its process is not owned by the test.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-dispose-{Guid.NewGuid():N}");
        var journalPath = Path.Combine(directory, "topology.json");
        var original = Console.Out;
        using var captured = new StringWriter();
        try
        {
            var emulator = await EmulatorProcess.StartAsync(journalPath, startupDeadline: TimeSpan.FromSeconds(20));
            emulator.WaitForExitOverride = _ => false;

            Console.SetOut(captured);
            await emulator.DisposeAsync();
        }
        finally
        {
            Console.SetOut(original);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var log = captured.ToString();
        StringAssert.Contains(log, "[step] dispose emulator");
        StringAssert.Contains(log, "did not exit within");
        StringAssert.Contains(log, "Emulator output:");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Every_cleanup_runs_in_order_and_failures_are_reported_body_first()
    {
        var steps = new TestSteps(() => "trace");
        var never = new TaskCompletionSource();
        var ran = new List<string>();

        var exception = await Assert.ThrowsExactlyAsync<AggregateException>(() => TestSteps.RunWithCleanupsAsync(
            () => steps.RunAsync("delete topic", TimeSpan.FromMilliseconds(100), () => never.Task),
            () =>
            {
                ran.Add("receiver");
                return steps.RunAsync("dispose receiver", TimeSpan.FromMilliseconds(100), () => never.Task);
            },
            () =>
            {
                ran.Add("sender");
                return Task.CompletedTask;
            },
            () =>
            {
                ran.Add("client");
                return steps.RunAsync("dispose client", TimeSpan.FromMilliseconds(100), () => never.Task);
            },
            () =>
            {
                ran.Add("emulator");
                return Task.CompletedTask;
            }));

        CollectionAssert.AreEqual(new[] { "receiver", "sender", "client", "emulator" }, ran);
        Assert.HasCount(3, exception.InnerExceptions);
        StringAssert.Contains(exception.InnerExceptions[0].Message, "Step 'delete topic' did not finish within");
        StringAssert.Contains(exception.InnerExceptions[1].Message, "Step 'dispose receiver' did not finish within");
        StringAssert.Contains(exception.InnerExceptions[2].Message, "Step 'dispose client' did not finish within");
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task A_single_failure_among_cleanups_is_rethrown_unchanged()
    {
        var steps = new TestSteps(() => "trace");
        var never = new TaskCompletionSource();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(() => TestSteps.RunWithCleanupsAsync(
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => steps.RunAsync("dispose sender", TimeSpan.FromMilliseconds(100), () => never.Task),
            () => Task.CompletedTask));

        StringAssert.Contains(exception.Message, "Step 'dispose sender' did not finish within");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task A_failed_launch_attempt_leaves_a_supplied_journal_and_its_directory_in_place()
    {
        if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 })
        {
            Assert.Inconclusive("An external emulator is configured; startup is not exercised.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"nimbus-sbemulator-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var journalPath = Path.Combine(directory, "topology.json");
        await File.WriteAllTextAsync(journalPath, "{}");
        try
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => EmulatorProcess.StartAsync(
                journalPath,
                deleteJournalOnDispose: true,
                startupDeadline: TimeSpan.FromMilliseconds(1)));

            Assert.IsTrue(Directory.Exists(directory), "A failed launch attempt deleted the caller's journal directory.");
            Assert.IsTrue(File.Exists(journalPath), "A failed launch attempt deleted the caller's journal.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // A port probed free can be taken before the emulator binds it, for example by another
    // emulator's internal HTTP port. The emulator then exits, but the other process still
    // answers /health on that port; startup must not take that answer for its own emulator.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Emulator_startup_ignores_a_health_answer_from_another_process_on_its_port()
    {
        if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 })
        {
            Assert.Inconclusive("An external emulator is configured; startup is not exercised.");
        }

        using var impostor = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        impostor.Start();
        var stolenPort = ((System.Net.IPEndPoint)impostor.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var answering = AnswerHealthAsync(impostor, stop.Token);
        var launches = 0;

        try
        {
            await using var emulator = await EmulatorProcess.StartAsync(
                startupDeadline: TimeSpan.FromSeconds(40),
                portSource: () => Interlocked.Increment(ref launches) == 1 ? stolenPort : FreePort());

            Assert.AreNotEqual(stolenPort, emulator.Port, "Startup accepted another process's /health answer.");
            Assert.AreEqual(2, launches);
        }
        finally
        {
            await stop.CancelAsync();
            impostor.Stop();
            await answering.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Answers every request with a healthy emulator response for another instance.</summary>
    private static async Task AnswerHealthAsync(System.Net.Sockets.TcpListener listener, CancellationToken cancellationToken)
    {
        var body = $$"""{"status":"ok","instance":"{{Guid.NewGuid()}}"}""";
        var response = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        while (!cancellationToken.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer, cancellationToken);
            await stream.WriteAsync(response, cancellationToken);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Emulator_startup_past_its_overall_deadline_fails_with_a_timeout_naming_the_deadline()
    {
        if (System.Environment.GetEnvironmentVariable("NIMBUS_SBEMULATOR_TEST_CS") is { Length: > 0 })
        {
            Assert.Inconclusive("An external emulator is configured; startup is not exercised.");
        }

        var watch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => EmulatorProcess.StartAsync(startupDeadline: TimeSpan.FromMilliseconds(1)));

        Assert.IsLessThan(20_000, watch.ElapsedMilliseconds);
        StringAssert.Contains(exception.Message, "startup deadline");
        StringAssert.Contains(exception.Message, "Output of the last attempt:");
    }
}
