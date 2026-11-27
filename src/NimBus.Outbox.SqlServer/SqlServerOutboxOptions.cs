using System;

namespace NimBus.Outbox.SqlServer;

/// <summary>
/// Configuration options for the SQL Server outbox.
/// </summary>
public class SqlServerOutboxOptions
{
    /// <summary>
    /// The SQL Server connection string.
    /// </summary>
    public string ConnectionString { get; set; } = null!;

    /// <summary>
    /// The schema for the outbox table. Default: "nimbus".
    /// </summary>
    public string Schema { get; set; } = "nimbus";

    /// <summary>
    /// The name of the outbox table. Default: "OutboxMessages".
    /// </summary>
    public string TableName { get; set; } = "OutboxMessages";

    /// <summary>
    /// Whether to automatically create the outbox table on startup. Default: true.
    /// </summary>
    public bool AutoCreateTable { get; set; } = true;

    /// <summary>
    /// Maximum number of rows one <c>DELETE</c> statement removes in
    /// <see cref="SqlServerOutbox.PurgeDispatchedAsync"/>; the purge repeats until no more rows
    /// qualify. Keep it below 5,000 so a batch does not escalate to a table lock.
    /// Default: 1,000.
    /// </summary>
    public int PurgeBatchSize
    {
        get => _purgeBatchSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _purgeBatchSize = value;
        }
    }

    /// <summary>
    /// How long <see cref="SqlServerOutbox.ClaimPendingAsync"/> waits for another dispatcher's
    /// claim to finish before it gives up and claims nothing on this poll. Default: 10 seconds.
    /// </summary>
    public TimeSpan ClaimLockTimeout
    {
        get => _claimLockTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _claimLockTimeout = value;
        }
    }

    private int _purgeBatchSize = 1000;
    private TimeSpan _claimLockTimeout = TimeSpan.FromSeconds(10);

    internal string FullTableName => $"[{Schema}].[{TableName}]";
}
