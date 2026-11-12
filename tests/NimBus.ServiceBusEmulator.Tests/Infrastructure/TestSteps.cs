#pragma warning disable CA1707, CA2007

using System.Diagnostics;

namespace NimBus.ServiceBusEmulator.Tests;

/// <summary>
/// Runs each awaited step of an emulator test under its own deadline, so a hang fails
/// with a message naming the step (and the emulator's protocol trace) instead of an
/// anonymous MSTest <c>[Timeout]</c>. Each step is also capped by what is left of an
/// overall budget, so a hang late in a slow run still fails before the test timeout.
/// Every step's duration is written to the test output.
/// </summary>
internal sealed class TestSteps
{
    private readonly Func<string> _dumpOutput;
    private readonly Stopwatch _clock;
    private readonly TimeSpan _overallBudget;

    /// <summary>Creates steps whose failures carry <paramref name="emulator"/>'s output.</summary>
    public TestSteps(EmulatorProcess emulator, Stopwatch clock, TimeSpan overallBudget)
        : this(emulator.DumpOutput, clock, overallBudget)
    {
    }

    /// <summary>Creates steps with no overall budget beyond each step's own.</summary>
    public TestSteps(Func<string> dumpOutput)
        : this(dumpOutput, Stopwatch.StartNew(), Timeout.InfiniteTimeSpan)
    {
    }

    /// <summary>Creates steps capped by <paramref name="overallBudget"/> measured on <paramref name="clock"/>.</summary>
    public TestSteps(Func<string> dumpOutput, Stopwatch clock, TimeSpan overallBudget)
    {
        ArgumentNullException.ThrowIfNull(dumpOutput);
        ArgumentNullException.ThrowIfNull(clock);
        _dumpOutput = dumpOutput;
        _clock = clock;
        _overallBudget = overallBudget;
    }

    /// <summary>Runs <paramref name="step"/> and fails if it takes longer than <paramref name="budget"/>.</summary>
    public async Task RunAsync(string name, TimeSpan budget, Func<Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        await RunAsync<object?>(name, budget, async () =>
        {
            await step();
            return null;
        });
    }

    /// <summary>Runs <paramref name="step"/> and fails if it takes longer than <paramref name="budget"/>.</summary>
    public async Task<T> RunAsync<T>(string name, TimeSpan budget, Func<Task<T>> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var bound = Bound(budget);
        if (bound <= TimeSpan.Zero)
        {
            throw Failure($"Step '{name}' had no time left in the {_overallBudget.TotalSeconds:0.#} s overall budget", null);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var result = await step().WaitAsync(bound);
            Console.WriteLine($"[step] {name}: {watch.ElapsedMilliseconds} ms (total {_clock.ElapsedMilliseconds} ms)");
            return result;
        }
        catch (TimeoutException exception)
        {
            throw Failure($"Step '{name}' did not finish within {bound.TotalSeconds:0.###} s", exception);
        }
        catch (OperationCanceledException exception)
        {
            throw Failure($"Step '{name}' was cancelled after {watch.ElapsedMilliseconds} ms", exception);
        }
    }

    /// <summary>Disposes <paramref name="resource"/> as a bounded, named step.</summary>
    public Task DisposeAsync(string name, IAsyncDisposable resource, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return RunAsync($"dispose {name}", budget, () => resource.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Runs <paramref name="body"/>, then always runs <paramref name="cleanup"/>, keeping the
    /// first failure as the reported one. If only the body fails, its exception is rethrown
    /// unchanged. If both fail, an <see cref="AggregateException"/> carries the body's
    /// failure first and the cleanup's second. If only the cleanup fails, its exception is
    /// thrown as the step's own failure.
    /// </summary>
    public static async Task RunWithCleanupAsync(Func<Task> body, Func<Task> cleanup)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            await body();
        }
        catch (Exception first)
        {
            try
            {
                await cleanup();
            }
            catch (Exception second)
            {
                throw new AggregateException(first, second);
            }

            throw;
        }

        await cleanup();
    }

    private TimeSpan Bound(TimeSpan budget)
    {
        if (_overallBudget == Timeout.InfiniteTimeSpan)
        {
            return budget;
        }

        var remaining = _overallBudget - _clock.Elapsed;
        return remaining < budget ? remaining : budget;
    }

    private TimeoutException Failure(string what, Exception? inner)
    {
        return new TimeoutException(
            $"{what} (total {_clock.ElapsedMilliseconds} ms). Emulator output:{System.Environment.NewLine}{_dumpOutput()}",
            inner);
    }
}
