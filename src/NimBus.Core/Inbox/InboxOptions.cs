using System;

namespace NimBus.Core.Inbox;

/// <summary>
/// Configures subscriber inbox deduplication and cleanup.
/// </summary>
public sealed class InboxOptions
{
    private TimeSpan _retentionPeriod = TimeSpan.FromDays(7);
    private TimeSpan _cleanupInterval = TimeSpan.FromHours(1);
    private TimeSpan _cleanupTimeBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the keyed inbox provider to use. A provider must be selected explicitly.
    /// </summary>
    public InboxStore? DeduplicationStore { get; set; }

    /// <summary>
    /// Gets or sets how long successfully processed message identifiers are retained.
    /// Defaults to seven days.
    /// </summary>
    public TimeSpan RetentionPeriod
    {
        get => _retentionPeriod;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Inbox retention period must be greater than zero.");
            _retentionPeriod = value;
        }
    }

    /// <summary>
    /// Gets or sets how often expired inbox records are purged. Defaults to one hour.
    /// </summary>
    public TimeSpan CleanupInterval
    {
        get => _cleanupInterval;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Inbox cleanup interval must be greater than zero.");
            _cleanupInterval = value;
        }
    }

    /// <summary>
    /// Gets or sets how long one cleanup pass keeps issuing purge batches. Each pass purges
    /// bounded batches until the expired backlog is cleared, this budget runs out, or the host
    /// stops. The budget is checked between batches, so a batch that is already running is
    /// allowed to finish. A pass that runs out of budget logs a warning, and the remaining
    /// expired records are purged on the next tick. Defaults to five minutes.
    /// </summary>
    public TimeSpan CleanupTimeBudget
    {
        get => _cleanupTimeBudget;
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Inbox cleanup time budget must be greater than zero.");
            _cleanupTimeBudget = value;
        }
    }
}
