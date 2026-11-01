using System;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Configuration for the SQL Server message store provider. Options are bound from
/// configuration at registration time and validated when the schema initializer runs.
/// </summary>
public sealed class SqlServerMessageStoreOptions
{
    /// <summary>
    /// SQL Server connection string. Required. Reads from configuration key
    /// <c>SqlConnection</c> or connection-string named <c>"sqlserver"</c> by default.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Schema name for all NimBus tables. Defaults to <c>"nimbus"</c>. Used in DbUp
    /// script substitution (<c>$schema$</c>) and in every emitted SQL statement.
    /// </summary>
    public string Schema { get; set; } = "nimbus";

    /// <summary>
    /// How the provider applies its schema scripts at startup.
    /// </summary>
    public SchemaProvisioningMode ProvisioningMode { get; set; } = SchemaProvisioningMode.AutoApply;

    /// <summary>
    /// Default command timeout for storage operations, in seconds.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Configuration section the retention settings bind from (environment form:
    /// <c>NimBus__SqlServer__UnresolvedRetentionDays</c>). A delegate passed to
    /// <c>AddSqlServerMessageStore(configure)</c> runs after binding and overrides it.
    /// </summary>
    public const string SectionName = "NimBus:SqlServer";

    /// <summary>Sentinel meaning "never delete" — the default for <see cref="UnresolvedRetentionDays"/>.</summary>
    public const int UnlimitedRetentionDays = -1;

    /// <summary>Largest supported <see cref="UnresolvedRetentionDays"/>; the same bound as the Cosmos DB provider.</summary>
    public const int MaxRetentionDays = 365;

    /// <summary>
    /// Retention, in whole days, for <c>UnresolvedEvents</c> rows that are not archived or removed
    /// (<c>Deleted = 0</c>, any status). Valid values are <see cref="UnlimitedRetentionDays"/>
    /// (the default: never delete) or 1 to <see cref="MaxRetentionDays"/>. A row deleted by
    /// retention can no longer be resubmitted or skipped. Applied by the retention job that
    /// <c>AddSqlServerMessageStoreRetention()</c> registers.
    /// </summary>
    public int UnresolvedRetentionDays { get; set; } = UnlimitedRetentionDays;

    /// <summary>
    /// Whether the retention job deletes old rows. Defaults to <see langword="true"/>; when
    /// <see langword="false"/> the job issues no deletes. Has no effect unless the host registers
    /// the job with <c>AddSqlServerMessageStoreRetention()</c>.
    /// </summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>
    /// Time between the start of one retention run and the start of the next. Defaults to one
    /// hour; must be greater than zero and at most 49 days.
    /// </summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    // PeriodicTimer's upper bound (uint.MaxValue - 1 ms, about 49.7 days); rounded down to 49 days.
    internal static readonly TimeSpan MaxRetentionInterval = TimeSpan.FromDays(49);

    internal bool IsUnresolvedRetentionDaysValid =>
        UnresolvedRetentionDays == UnlimitedRetentionDays
        || (UnresolvedRetentionDays >= 1 && UnresolvedRetentionDays <= MaxRetentionDays);

    internal static string DescribeInvalidUnresolvedRetentionDays(int value) =>
        $"{nameof(SqlServerMessageStoreOptions)}.{nameof(UnresolvedRetentionDays)} must be "
        + $"{UnlimitedRetentionDays} (unlimited) or between 1 and {MaxRetentionDays} days; was {value}.";
}

public enum SchemaProvisioningMode
{
    /// <summary>
    /// Apply embedded DbUp scripts on startup. Suitable for development and for
    /// production environments that allow runtime DDL.
    /// </summary>
    AutoApply,

    /// <summary>
    /// Verify on startup that all expected DbUp scripts are recorded as already
    /// applied (consult the journal table). Fail fast otherwise. Suitable for
    /// production environments where DDL is performed by the deployment pipeline.
    /// </summary>
    VerifyOnly,
}
