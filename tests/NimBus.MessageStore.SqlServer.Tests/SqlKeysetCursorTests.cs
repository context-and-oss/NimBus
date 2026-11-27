#pragma warning disable CA1707, CA2007
using System;
using System.Text;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// Unit tests for the opaque keyset continuation token used by the SQL Server
/// list queries. Runs without a database.
/// </summary>
[TestClass]
public sealed class SqlKeysetCursorTests
{
    // 100 ns digit is nonzero: any rounding to µs, ms or DATETIME's 1/300 s changes it.
    private static readonly DateTime Boundary = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);

    [TestMethod]
    public void Encode_then_decode_roundtrips_timestamp_to_the_tick_and_id()
    {
        var token = new SqlKeysetCursor(Boundary, 9_007_199_254_740_993L).Encode();

        Assert.IsTrue(SqlKeysetCursor.TryDecode(token, out var cursor));
        Assert.AreEqual(Boundary.Ticks, cursor.Timestamp.Ticks);
        Assert.AreEqual(9_007_199_254_740_993L, cursor.Id);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not base64 !!")]
    [DataRow("MjA=")] // legacy integer-offset token: base64("20")
    [DataRow("azE6YWJjOjE=")] // base64("k1:abc:1")
    [DataRow("azE6MTIz")] // base64("k1:123") — missing id
    [DataRow("azE6LTE6MQ==")] // base64("k1:-1:1") — ticks out of range
    [DataRow("azE6OTk5OTk5OTk5OTk5OTk5OTk5OTox")] // ticks beyond DateTime.MaxValue
    public void Malformed_or_unrecognised_token_does_not_decode(string? token)
    {
        Assert.IsFalse(SqlKeysetCursor.TryDecode(token, out _));
    }

    [TestMethod]
    public void Legacy_offset_token_is_not_recognised()
    {
        var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes("100"));
        Assert.IsFalse(SqlKeysetCursor.TryDecode(legacy, out _));
    }

    [TestMethod]
    public void Apply_with_cursor_adds_strictly_after_predicate()
    {
        var where = new System.Collections.Generic.List<string> { "1 = 1" };
        var p = new DynamicParameters();
        var token = new SqlKeysetCursor(Boundary, 42).Encode();

        SqlKeysetCursor.Apply(token, "UpdatedAtUtc", where, p);

        CollectionAssert.Contains(where, "(UpdatedAtUtc < @CursorTs OR (UpdatedAtUtc = @CursorTs AND Id < @CursorId))");
        Assert.AreEqual(Boundary.Ticks, p.Get<DateTime>("CursorTs").Ticks);
        Assert.AreEqual(42L, p.Get<long>("CursorId"));

    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("garbage")]
    public void Apply_without_a_valid_cursor_leaves_the_query_on_the_first_page(string? token)
    {
        var where = new System.Collections.Generic.List<string> { "1 = 1" };
        var p = new DynamicParameters();

        SqlKeysetCursor.Apply(token, "UpdatedAtUtc", where, p);

        CollectionAssert.AreEqual(new[] { "1 = 1" }, where);
        Assert.AreEqual(0, System.Linq.Enumerable.Count(p.ParameterNames));
    }
}
