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
