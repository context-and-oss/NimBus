using System.Data;
using System.Globalization;
using System.Text;
using Dapper;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Keyset continuation for the SQL Server list queries: the last returned row's
/// sort timestamp and <c>Id</c>, in the queries' <c>ORDER BY &lt;ts&gt; DESC, Id DESC</c>
/// order. Encoded as an opaque base64 token; the timestamp travels as ticks so it
/// round-trips at full DATETIME2(7) precision.
/// </summary>
internal readonly record struct SqlKeysetCursor(DateTime Timestamp, long Id)
{
    private const string Prefix = "k1:";

    /// <summary>Encodes the cursor as an opaque continuation token.</summary>
    public string Encode()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{Prefix}{Timestamp.Ticks}:{Id}")));

    /// <summary>
    /// Decodes a token produced by <see cref="Encode"/>. A null, empty, malformed or
    /// unrecognised token (including a legacy integer-offset token) returns <c>false</c>.
    /// </summary>
    public static bool TryDecode(string? token, out SqlKeysetCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(token)) return false;

        string text;
        try { text = Encoding.UTF8.GetString(Convert.FromBase64String(token)); }
        catch (FormatException) { return false; }

        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = text[Prefix.Length..].Split(':');
        if (parts.Length != 2
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTime.MaxValue.Ticks
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        cursor = new SqlKeysetCursor(new DateTime(ticks, DateTimeKind.Unspecified), id);
        return true;
    }

    /// <summary>
    /// When <paramref name="token"/> decodes, restricts the query to rows that sort
    /// strictly after the cursor. <paramref name="timestampColumn"/> must be a trusted
    /// column name, never user input. An undecodable token leaves the query on the
    /// first page.
    /// </summary>
    public static void Apply(string? token, string timestampColumn, List<string> where, DynamicParameters parameters)
    {
        if (!TryDecode(token, out var cursor)) return;

        where.Add($"({timestampColumn} < @CursorTs OR ({timestampColumn} = @CursorTs AND Id < @CursorId))");
        // Bind as DateTime2 explicitly: Dapper's default DbType.DateTime rounds to 1/300 s.
        parameters.Add("CursorTs", cursor.Timestamp, DbType.DateTime2);
        parameters.Add("CursorId", cursor.Id, DbType.Int64);
    }
}
