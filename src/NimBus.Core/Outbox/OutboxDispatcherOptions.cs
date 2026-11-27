using System;
using System.Globalization;

namespace NimBus.Core.Outbox;

/// <summary>
/// Retry, parking and claiming settings for <see cref="OutboxDispatcher"/>. Retry backoff,
/// parking and leases apply only when the outbox implements <see cref="IOutboxClaiming"/>.
/// </summary>
public sealed class OutboxDispatcherOptions
{
    /// <summary>
    /// Number of failed attempts after which a row is parked: it is no longer dispatched, and it
    /// keeps blocking the rest of its session so per-session order is never broken, until an
    /// operator requeues it. Default: 20, which with the default delays is about an hour of
    /// retrying. Use <see cref="int.MaxValue"/> to retry forever.
    /// </summary>
    public int MaxAttempts { get; set; } = 20;

    /// <summary>
    /// Delay before the first retry of a failed row. Each further failure doubles the delay, up
    /// to <see cref="MaxRetryDelay"/>. Default: 1 second.
    /// </summary>
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound for the retry delay. Default: 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a claimed batch stays leased to this dispatcher. Another replica can claim the
    /// rows once the lease expires, for example after this process crashed mid-batch. The
    /// dispatcher stops starting new sends once half of the lease has elapsed, cancels a send
    /// that is still running when only a tenth of the lease is left, and checkpoints each row
    /// only while it still holds the claim, so keep the lease well above the time one batch
    /// takes to send. Default: 2 minutes.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Identifier recorded as the lease owner. Must be unique per running dispatcher. Default:
    /// <c>null</c>, which generates one from the machine name, process id and a random suffix.
    /// </summary>
    public string? DispatcherId { get; set; }

    /// <summary>
    /// Returns the retry delay after the given failed attempt (1-based).
    /// </summary>
    /// <param name="attempt">The number of failed attempts so far, including this one.</param>
    /// <returns><c>min(InitialRetryDelay * 2^(attempt - 1), MaxRetryDelay)</c>.</returns>
    public TimeSpan GetRetryDelay(int attempt)
    {
        var delay = Min(InitialRetryDelay, MaxRetryDelay);
        // Saturating doubling: stop at the cap instead of clamping the exponent, so the delay
        // reaches MaxRetryDelay for any InitialRetryDelay and MaxAttempts. A positive delay
        // passes half the cap within 63 doublings, so the loop is short.
        for (var failed = 1; failed < attempt && delay < MaxRetryDelay; failed++)
        {
            delay = delay.Ticks > MaxRetryDelay.Ticks / 2 ? MaxRetryDelay : TimeSpan.FromTicks(delay.Ticks * 2);
        }

        return delay;
    }

    /// <summary>
    /// Throws when a setting is out of range.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A setting is out of range.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1, nameof(MaxAttempts));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialRetryDelay, TimeSpan.Zero, nameof(InitialRetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, InitialRetryDelay, nameof(MaxRetryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseDuration, TimeSpan.FromSeconds(1), nameof(LeaseDuration));
        if (DispatcherId is { Length: > 256 })
            throw new ArgumentOutOfRangeException(nameof(DispatcherId), "DispatcherId must be at most 256 characters.");
    }

    internal string ResolveDispatcherId()
    {
        if (!string.IsNullOrWhiteSpace(DispatcherId))
            return DispatcherId;

        var suffix = Guid.NewGuid().ToString("N")[..12];
        var processId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        var machine = Environment.MachineName;
        var id = $"{machine}:{processId}:{suffix}";
        return id.Length <= 256 ? id : id[^256..];
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a <= b ? a : b;
}
