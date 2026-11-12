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
                () => emulator.DisposeAsync().AsTask()));

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
