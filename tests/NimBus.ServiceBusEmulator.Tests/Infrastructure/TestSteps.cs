#pragma warning disable CA1707, CA2007

using System.Diagnostics;

namespace NimBus.ServiceBusEmulator.Tests;

/// <summary>
/// Runs each awaited step of an emulator test under its own deadline, so a hang fails
/// with a message naming the step (and the emulator's output) instead of an anonymous
/// MSTest <c>[Timeout]</c>. Every step's duration is written to the test output.
/// </summary>
internal sealed class TestSteps(EmulatorProcess emulator)
{
    private readonly Stopwatch _total = Stopwatch.StartNew();

    /// <summary>Runs <paramref name="step"/> and fails if it takes longer than <paramref name="budget"/>.</summary>
    public async Task RunAsync(string name, TimeSpan budget, Func<Task> step)
    {
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
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await step().WaitAsync(budget);
            Console.WriteLine($"[step] {name}: {watch.ElapsedMilliseconds} ms (total {_total.ElapsedMilliseconds} ms)");
            return result;
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Step '{name}' did not finish within {budget.TotalSeconds:0.#} s (total {_total.ElapsedMilliseconds} ms). " +
                $"Emulator output:{System.Environment.NewLine}{emulator.DumpOutput()}",
                exception);
        }
    }

    /// <summary>Disposes <paramref name="resource"/> as a bounded, named step.</summary>
    public Task DisposeAsync(string name, IAsyncDisposable resource, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return RunAsync($"dispose {name}", budget, () => resource.DisposeAsync().AsTask());
    }
}
