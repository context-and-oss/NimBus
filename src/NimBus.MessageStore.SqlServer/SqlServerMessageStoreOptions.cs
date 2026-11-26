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
    /// Configuration key the schema-upgrade timeout binds from (environment form:
    /// <c>NimBus__SqlServer__SchemaUpgradeTimeoutSeconds</c>). A delegate passed to
    /// <c>AddSqlServerMessageStore(configure)</c> runs after binding and overrides it.
    /// </summary>
    public const string SchemaUpgradeTimeoutKey = "NimBus:SqlServer:SchemaUpgradeTimeoutSeconds";

    /// <summary>The default of <see cref="SchemaUpgradeTimeoutSeconds"/>: 30 minutes.</summary>
    public const int DefaultSchemaUpgradeTimeoutSeconds = 1800;

    /// <summary>
    /// The longest one schema migration script may run at startup (<c>AutoApply</c>), in seconds.
    /// Defaults to 30 minutes, so an index build on a large <c>Messages</c> table, or on a small
    /// database tier, completes instead of failing at SqlClient's 30-second default. <c>0</c> sets
    /// no limit. It is also how long a second instance that starts during an upgrade waits for the
    /// upgrade lock (at least a minute; without a limit when this is <c>0</c>).
    /// </summary>
    public int SchemaUpgradeTimeoutSeconds { get; set; } = DefaultSchemaUpgradeTimeoutSeconds;

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
