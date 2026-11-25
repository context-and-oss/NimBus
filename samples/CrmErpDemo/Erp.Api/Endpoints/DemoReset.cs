using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Endpoints;

/// <summary>
/// Empties the demo's tables for the "Delete all data" reset. A DELETE of every row is
/// fully logged and takes minutes on the Azure demo's Basic (5 DTU) database, long enough
/// that the reset looks stuck; TRUNCATE TABLE deallocates the pages at once. The demo
/// tables have no foreign keys, which TRUNCATE requires.
/// </summary>
internal static partial class DemoReset
{
    /// <summary>
    /// Counts the rows of <paramref name="table"/> (a constant, bracket-quoted name) and
    /// truncates it. A table that doesn't exist counts as empty: the NimBus inbox and
    /// outbox tables appear only once the adapter has created them.
    /// </summary>
    public static async Task<int> TruncateAsync(DbContext db, string table)
    {
        // TRUNCATE takes no parameters, so the name goes into the SQL text; accept only a
        // plain [schema].[table] name.
        if (!QuotedTableName().IsMatch(table))
        {
            throw new ArgumentException($"Not a [schema].[table] name: {table}", nameof(table));
        }

        var exists = await db.Database
            .SqlQueryRaw<int>("SELECT CASE WHEN OBJECT_ID({0}) IS NULL THEN 0 ELSE 1 END AS [Value]", table)
            .SingleAsync();
        if (exists == 0)
        {
            return 0;
        }

#pragma warning disable EF1003 // The table name is checked against QuotedTableName above.
        var rows = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM " + table).SingleAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE " + table);
#pragma warning restore EF1003
        return rows;
    }

    [GeneratedRegex(@"^\[\w+\]\.\[\w+\]$")]
    private static partial Regex QuotedTableName();
}
