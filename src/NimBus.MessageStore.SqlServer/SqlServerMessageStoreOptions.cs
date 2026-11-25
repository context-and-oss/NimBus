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
    /// Configuration key the retention interval binds from (environment form:
    /// <c>NimBus__SqlServer__RetentionInterval</c>). A delegate passed to
    /// <c>AddSqlServerMessageStore(configure)</c> runs after binding and overrides it.
    /// </summary>
    public const string RetentionIntervalKey = "NimBus:SqlServer:RetentionInterval";

    /// <summary>
    /// Time between the start of one retention run and the start of the next (Spec 050). Defaults to
    /// one hour; must be greater than zero and at most 49 days. Used only when the host registers the
    /// cleanup with <c>AddSqlServerMessageStoreRetention()</c>. The periods themselves are not SQL
    /// Server settings: they come from <c>NimBus:Retention:*</c> (see <c>RetentionPolicy</c>).
    /// </summary>
    public TimeSpan RetentionInterval { get; set; } = TimeSpan.FromHours(1);

    // PeriodicTimer's upper bound (uint.MaxValue - 1 ms, about 49.7 days), rounded down to 49 days.
    internal static readonly TimeSpan MaxRetentionInterval = TimeSpan.FromDays(49);
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
