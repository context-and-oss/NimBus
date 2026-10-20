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

    internal string FullTableName => $"[{Schema}].[{TableName}]";
}
