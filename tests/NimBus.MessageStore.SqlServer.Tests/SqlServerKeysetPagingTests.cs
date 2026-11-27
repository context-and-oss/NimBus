#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// Keyset paging for the SQL Server list queries: paging across concurrent inserts
/// neither skips nor repeats rows, and a page boundary that falls inside a group of
/// rows sharing one sub-millisecond DATETIME2(7) timestamp keeps exact-once order.
/// SQL-specific because keyset paging is a SQL implementation detail. Skipped
/// automatically when <c>NIMBUS_SQL_TEST_CONNECTION</c> is unset.
/// </summary>
[TestClass]
public sealed class SqlServerKeysetPagingTests
{
    // The 100 ns digit is nonzero: truncating or rounding to µs, ms or DATETIME's
    // 1/300 s changes the value, so a lossy cursor shifts off the boundary.
    private static readonly DateTime Boundary = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(1_234_567);

    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerKeysetPagingTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerKeysetPagingTests));

    private static INimBusMessageStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerKeysetPagingTests));

    private static string Schema => SqlServerStoreTestHarness.GetSchema(typeof(SqlServerKeysetPagingTests));

    private static string Scope(string value) => $"{value}-{Guid.NewGuid():N}"[..24];

    // ───── Method under test: seeding, paging and the terminal value ─────

    public enum Method
    {
        GetEventsByFilter,
        GetFailedEventsAcrossEndpoints,
        SearchMessages,
        SearchAudits,
        DownloadEndpointStatePaging,
    }

    private sealed record Page(IReadOnlyList<string> EventIds, string? Token);

    private static (string Table, string Column) SortKey(Method method) => method switch
    {
        Method.SearchMessages => ("Messages", "EnqueuedTimeUtc"),
        Method.SearchAudits => ("MessageAudits", "CreatedAtUtc"),
        _ => ("UnresolvedEvents", "UpdatedAtUtc"),
    };

    private static bool UsesLookAhead(Method method) => method == Method.GetFailedEventsAcrossEndpoints;

    private static string? Terminal(Method method) => method == Method.DownloadEndpointStatePaging ? string.Empty : null;

    private static async Task SeedAsync(INimBusMessageStore store, Method method, string endpointId, string eventId)
    {
        switch (method)
        {
            case Method.SearchMessages:
                await store.StoreMessage(new MessageEntity
                {
                    EventId = eventId,
                    MessageId = eventId,
                    EndpointId = endpointId,
                    SessionId = "s1",
                    CorrelationId = "corr-1",
                    EventTypeId = "KeysetTest",
                    MessageType = MessageType.EventRequest,
                    EndpointRole = EndpointRole.Subscriber,
                    EnqueuedTimeUtc = DateTime.UtcNow,
                    From = "publisher",
                    To = endpointId,
                    MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "KeysetTest", EventJson = "{}" } },
                });
                break;
            case Method.SearchAudits:
                await store.StoreMessageAudit(eventId, new MessageAuditEntity
                {
                    AuditorName = "keyset-test",
                    AuditTimestamp = DateTime.UtcNow,
                    AuditType = MessageAuditType.Comment,
                    Comment = "keyset",
                }, endpointId, "KeysetTest");
                break;
            default:
                await store.UploadFailedMessage(eventId, eventId, endpointId, new UnresolvedEvent
                {
                    EventId = eventId,
                    SessionId = eventId,
                    EndpointId = endpointId,
                    EnqueuedTimeUtc = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CorrelationId = "corr-1",
                    EndpointRole = EndpointRole.Subscriber,
                    MessageType = MessageType.EventRequest,
                    EventTypeId = "KeysetTest",
                    To = endpointId,
                    From = "publisher",
                    MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "KeysetTest", EventJson = "{\"p\":1}" } },
                });
                break;
        }
    }

    private static async Task<Page> ReadPageAsync(INimBusMessageStore store, Method method, string endpointId, string? token, int pageSize)
    {
        switch (method)
        {
            case Method.GetEventsByFilter:
            {
                var r = await store.GetEventsByFilter(new EventFilter { EndPointId = endpointId }, token!, pageSize);
                return new Page(r.Events.Select(e => e.EventId).ToList(), r.ContinuationToken);
            }
            case Method.GetFailedEventsAcrossEndpoints:
            {
                var r = await store.GetFailedEventsAcrossEndpoints(new EventFilter(), new[] { endpointId }, token, pageSize);
                return new Page(r.Events.Select(e => e.EventId).ToList(), r.ContinuationToken);
            }
            case Method.SearchMessages:
            {
                var r = await store.SearchMessages(new MessageFilter { EndpointId = endpointId }, token, pageSize);
                return new Page(r.Messages.Select(m => m.EventId).ToList(), r.ContinuationToken);
            }
            case Method.SearchAudits:
            {
                var r = await store.SearchAudits(new AuditFilter { EndpointId = endpointId, EndpointIdExact = true }, token, pageSize);
                return new Page(r.Audits.Select(a => a.EventId).ToList(), r.ContinuationToken);
            }
            default:
            {
                var r = await store.DownloadEndpointStatePaging(endpointId, pageSize, token!);
                return new Page(r!.EnrichedUnresolvedEvents.Select(e => e.EventId).ToList(), r.ContinuationToken);
            }
        }
    }

    // ───── Direct SQL: set and read the sort timestamp, read the identity ─────

    private static async Task SetSortTimestampAsync(Method method, string eventId, DateTime value)
    {
        var (table, column) = SortKey(method);
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"UPDATE [{Schema}].[{table}] SET [{column}] = @Value WHERE EventId = @EventId", conn);
        cmd.Parameters.Add(new SqlParameter("@Value", SqlDbType.DateTime2) { Value = value });
        cmd.Parameters.Add(new SqlParameter("@EventId", SqlDbType.NVarChar, 200) { Value = eventId });
        Assert.AreEqual(1, await cmd.ExecuteNonQueryAsync(), $"Exactly one {table} row must carry EventId {eventId}.");
    }

    private static async Task<(DateTime Timestamp, long Id)> ReadSortKeyAsync(Method method, string eventId)
    {
        var (table, column) = SortKey(method);
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT [{column}], [Id] FROM [{Schema}].[{table}] WHERE EventId = @EventId", conn);
        cmd.Parameters.Add(new SqlParameter("@EventId", SqlDbType.NVarChar, 200) { Value = eventId });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync(), $"No {table} row for EventId {eventId}.");
        return (reader.GetDateTime(0), reader.GetInt64(1));
    }

    private static async Task<(List<string> Seen, List<Page> Pages)> PageToEndAsync(
        INimBusMessageStore store, Method method, string endpointId, int pageSize, Page first)
    {
        var pages = new List<Page> { first };
        var seen = first.EventIds.ToList();
        var token = first.Token;
        while (!string.IsNullOrEmpty(token))
        {
            var page = await ReadPageAsync(store, method, endpointId, token, pageSize);
            Assert.IsTrue(page.EventIds.Count <= pageSize, "A page never exceeds the requested size.");
            pages.Add(page);
            seen.AddRange(page.EventIds);
            token = page.Token;
            Assert.IsTrue(pages.Count <= 20, "Paging must terminate.");
        }

        Assert.AreEqual(Terminal(method), pages[^1].Token, "The last page carries the method's terminal value.");
        return (seen, pages);
    }

    // ───── Inserts during paging ─────

    [TestMethod]
    [DataRow(Method.GetEventsByFilter)]
    [DataRow(Method.GetFailedEventsAcrossEndpoints)]
    [DataRow(Method.SearchMessages)]
    [DataRow(Method.SearchAudits)]
    [DataRow(Method.DownloadEndpointStatePaging)]
    public async Task Paging_across_inserts_returns_every_seeded_row_exactly_once_in_order(Method method)
    {
        var store = CreateStore();
        var endpointId = Scope("ep-ins");
        const int pageSize = 3;

        // Seven rows with distinct past timestamps, newest first in `expected`.
        var expected = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var eventId = Scope($"ins{i}");
            await SeedAsync(store, method, endpointId, eventId);
            await SetSortTimestampAsync(method, eventId, Boundary.AddMinutes(-i));
            expected.Add(eventId);
        }

        var first = await ReadPageAsync(store, method, endpointId, null, pageSize);
        CollectionAssert.AreEqual(expected.Take(pageSize).ToList(), first.EventIds.ToList(), "The first page is the newest rows.");
        Assert.IsFalse(string.IsNullOrEmpty(first.Token), "A full first page with more rows hands out a token.");

        // Newer rows land ahead of the reader; OFFSET paging would repeat rows here.
        var inserted = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var eventId = Scope($"new{i}");
            await SeedAsync(store, method, endpointId, eventId);
            await SetSortTimestampAsync(method, eventId, Boundary.AddDays(1).AddMinutes(i));
            inserted.Add(eventId);
        }

        var (seen, _) = await PageToEndAsync(store, method, endpointId, pageSize, first);

        CollectionAssert.AreEqual(expected, seen,
            "Every seeded row is returned exactly once, newest first, with no duplicates or gaps; rows inserted ahead of the cursor are not returned.");
        Assert.IsFalse(seen.Intersect(inserted).Any());
    }

    // ───── Sub-millisecond equal timestamps at the page boundary ─────

    [TestMethod]
    [DataRow(Method.GetEventsByFilter)]
    [DataRow(Method.GetFailedEventsAcrossEndpoints)]
    [DataRow(Method.SearchMessages)]
    [DataRow(Method.SearchAudits)]
    [DataRow(Method.DownloadEndpointStatePaging)]
    public async Task Page_boundary_inside_an_equal_sub_millisecond_timestamp_group_keeps_exact_once_order(Method method)
    {
        var store = CreateStore();
        var endpointId = Scope("ep-eq");
        const int pageSize = 3;

        // Two newer rows, then four rows sharing the boundary timestamp. Page 1 holds
        // the two newer rows plus one boundary row, so the cut falls inside the group
        // and the last row of page 1 carries the boundary timestamp.
        var seeded = new List<(string EventId, DateTime Timestamp, long Id)>();
        for (var i = 0; i < 6; i++)
        {
            var eventId = Scope($"eq{i}");
            var timestamp = i switch { 0 => Boundary.AddTicks(2), 1 => Boundary.AddTicks(1), _ => Boundary };
            await SeedAsync(store, method, endpointId, eventId);
            await SetSortTimestampAsync(method, eventId, timestamp);

            var stored = await ReadSortKeyAsync(method, eventId);
            Assert.AreEqual(timestamp.Ticks, stored.Timestamp.Ticks, "The fixture must be stored at full DATETIME2(7) precision.");
            seeded.Add((eventId, timestamp, stored.Id));
        }

        var expected = seeded
            .OrderByDescending(r => r.Timestamp.Ticks)
            .ThenByDescending(r => r.Id)
            .Select(r => r.EventId)
            .ToList();

        var first = await ReadPageAsync(store, method, endpointId, null, pageSize);
        CollectionAssert.AreEqual(expected.Take(pageSize).ToList(), first.EventIds.ToList());
        var lastOfFirst = seeded.Single(r => r.EventId == first.EventIds[^1]);
        Assert.AreEqual(Boundary.Ticks, lastOfFirst.Timestamp.Ticks, "The last row of page 1 must carry the boundary timestamp.");

        var (seen, pages) = await PageToEndAsync(store, method, endpointId, pageSize, first);

        CollectionAssert.AreEqual(expected, seen, "Every seeded row exactly once in (timestamp DESC, Id DESC) order.");

        // Six rows, page size three: the final page is exactly full.
        if (UsesLookAhead(method))
        {
            Assert.AreEqual(2, pages.Count, "The look-ahead finds no further row, so the exactly-full final page is terminal.");
        }
        else
        {
            Assert.AreEqual(3, pages.Count, "An exactly-full final page still hands out a token.");
            Assert.AreEqual(0, pages[^1].EventIds.Count, "That token returns an empty terminal page.");
        }
    }

    // ───── Tokens ─────

    [TestMethod]
    [DataRow(Method.GetEventsByFilter)]
    [DataRow(Method.GetFailedEventsAcrossEndpoints)]
    [DataRow(Method.SearchMessages)]
    [DataRow(Method.SearchAudits)]
    [DataRow(Method.DownloadEndpointStatePaging)]
    public async Task Malformed_or_legacy_token_returns_the_first_page(Method method)
    {
        var store = CreateStore();
        var endpointId = Scope("ep-tok");
        for (var i = 0; i < 4; i++)
        {
            var eventId = Scope($"tok{i}");
            await SeedAsync(store, method, endpointId, eventId);
            await SetSortTimestampAsync(method, eventId, Boundary.AddMinutes(-i));
        }

        var first = await ReadPageAsync(store, method, endpointId, null, 2);
        var empty = await ReadPageAsync(store, method, endpointId, string.Empty, 2);
        CollectionAssert.AreEqual(first.EventIds.ToList(), empty.EventIds.ToList());

        var legacyOffset = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("2"));
        foreach (var token in new[] { "not base64 !!", legacyOffset, "azE6YWJjOjE=" })
        {
            var page = await ReadPageAsync(store, method, endpointId, token, 2);
            CollectionAssert.AreEqual(first.EventIds.ToList(), page.EventIds.ToList(), $"Token '{token}' must fall back to the first page.");
        }
    }

    [TestMethod]
    [DataRow(Method.GetEventsByFilter)]
    [DataRow(Method.GetFailedEventsAcrossEndpoints)]
    [DataRow(Method.SearchMessages)]
    [DataRow(Method.SearchAudits)]
    [DataRow(Method.DownloadEndpointStatePaging)]
    public async Task Short_or_empty_page_returns_the_terminal_value(Method method)
    {
        var store = CreateStore();
        var endpointId = Scope("ep-short");

        var none = await ReadPageAsync(store, method, endpointId, null, 3);
        Assert.AreEqual(0, none.EventIds.Count);
        Assert.AreEqual(Terminal(method), none.Token);

        await SeedAsync(store, method, endpointId, Scope("short0"));
        var shortPage = await ReadPageAsync(store, method, endpointId, null, 3);
        Assert.AreEqual(1, shortPage.EventIds.Count);
        Assert.AreEqual(Terminal(method), shortPage.Token);
    }
}
