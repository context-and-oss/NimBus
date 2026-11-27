using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Defines how a failed message should be retried.
/// </summary>
public class RetryPolicy
{
    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    public int MaxRetries { get; set; }

    /// <summary>
    /// The backoff strategy to use between retries.
    /// </summary>
    public BackoffStrategy Strategy { get; set; } = BackoffStrategy.Fixed;

    /// <summary>
    /// The base delay between retries.
    /// For Fixed: used as-is. For Linear: multiplied by attempt number. For Exponential: doubled per attempt.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum delay cap (optional). If null, delay grows without bound.
    /// </summary>
    public TimeSpan? MaxDelay { get; set; }

    /// <summary>
    /// Gets or sets the jitter mode applied to the calculated backoff delay.
    /// </summary>
    public JitterMode Jitter { get; set; } = JitterMode.None;

    /// <summary>
    /// Gets or sets the non-negative, finite maximum proportional increase used by <see cref="JitterMode.Bounded"/>.
    /// </summary>
    public double BoundedJitterFactor { get; set; } = 0.25;

    /// <summary>
    /// Calculates the delay for a given retry attempt (0-based).
    /// </summary>
    public TimeSpan GetDelay(int retryAttempt)
    {
        return GetDelay(retryAttempt, rng: null);
    }

    /// <summary>
    /// Calculates the delay for a given retry attempt (0-based) using the supplied random source for jitter.
    /// </summary>
    /// <param name="retryAttempt">The zero-based retry attempt.</param>
    /// <param name="rng">The random source, or <see langword="null"/> to use <see cref="Random.Shared"/>.</param>
    /// <returns>The calculated retry delay.</returns>
    public TimeSpan GetDelay(int retryAttempt, Random? rng = null)
    {
        var delay = Strategy switch
        {
            BackoffStrategy.Fixed => BaseDelay,
            BackoffStrategy.Linear => TimeSpan.FromTicks(BaseDelay.Ticks * (retryAttempt + 1)),
            BackoffStrategy.Exponential => GetExponentialDelay(retryAttempt),
            _ => BaseDelay
        };

        delay = Jitter switch
        {
            JitterMode.Full => ApplyJitter(delay, rng ?? Random.Shared, 1, MaxDelay),
            JitterMode.Bounded => ApplyJitter(delay, rng ?? Random.Shared, GetBoundedJitterFactor(), MaxDelay),
            _ => delay
        };

        if (MaxDelay.HasValue && delay > MaxDelay.Value)
            delay = MaxDelay.Value;

        return delay;
    }

    private TimeSpan GetExponentialDelay(int retryAttempt)
    {
        // Saturate at MaxDelay before multiplying: BaseDelay * 2^n exceeds MaxDelay exactly when
        // 2^n > floor(MaxDelay / BaseDelay), so the comparison cannot overflow and the product is
        // only computed when it fits within MaxDelay.
        if (MaxDelay.HasValue && BaseDelay.Ticks > 0 && retryAttempt >= 0
            && (retryAttempt >= 63 || (1L << retryAttempt) > MaxDelay.Value.Ticks / BaseDelay.Ticks))
            return MaxDelay.Value;

        return TimeSpan.FromTicks(BaseDelay.Ticks * (long)Math.Pow(2, retryAttempt));
    }

    private double GetBoundedJitterFactor()
    {
        if (!double.IsFinite(BoundedJitterFactor) || BoundedJitterFactor < 0)
            throw new ArgumentOutOfRangeException(nameof(BoundedJitterFactor), BoundedJitterFactor, "The bounded jitter factor must be finite and non-negative.");

        return BoundedJitterFactor;
    }

    private static TimeSpan ApplyJitter(TimeSpan delay, Random rng, double maximumFactor, TimeSpan? maxDelay)
    {
        var maximumJitterTicks = delay.Ticks * maximumFactor;
        var jitterTicks = (long)(maximumJitterTicks * rng.NextDouble());
        if (maximumJitterTicks > 0 && jitterTicks >= maximumJitterTicks)
            jitterTicks = (long)Math.Ceiling(maximumJitterTicks) - 1;

        if (maxDelay.HasValue && (decimal)delay.Ticks + jitterTicks > maxDelay.Value.Ticks)
            return maxDelay.Value;

        return TimeSpan.FromTicks(checked(delay.Ticks + jitterTicks));
    }

    /// <summary>
    /// Gets the delay in minutes for a given retry attempt, for use with ISender.Send().
    /// </summary>
    public int GetDelayMinutes(int retryAttempt)
    {
        return (int)Math.Ceiling(GetDelay(retryAttempt).TotalMinutes);
    }
}

/// <summary>
/// Backoff strategy for retry delays.
/// </summary>
public enum BackoffStrategy
{
    /// <summary>
    /// Same delay between each retry.
    /// </summary>
    Fixed,

    /// <summary>
    /// Delay increases linearly: baseDelay * (attempt + 1).
    /// </summary>
    Linear,

    /// <summary>
    /// Delay doubles each retry: baseDelay * 2^attempt.
    /// </summary>
    Exponential
}

/// <summary>
/// Jitter mode for spreading retry delays.
/// </summary>
public enum JitterMode
{
    /// <summary>
    /// No jitter. The calculated backoff delay is used unchanged.
    /// </summary>
    None,

    /// <summary>
    /// Uniformly increases the calculated backoff delay by up to 100 percent.
    /// </summary>
    Full,

    /// <summary>
    /// Uniformly increases the calculated backoff delay by up to the configured bounded factor.
    /// </summary>
    Bounded
}
