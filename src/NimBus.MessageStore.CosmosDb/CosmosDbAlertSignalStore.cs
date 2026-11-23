using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IAlertSignalStore"/> (Spec 053 Phases 2 and 3). Failed attempts
/// and completions are read from the shared <c>messages</c> container, blocking events from the
/// endpoint's own container.
/// </summary>
/// <remarks>
/// Cosmos compares the stored DateTime strings, which Json.NET writes with trailing fractional
/// zeros trimmed, so within one second their order is not chronological (see
/// <see cref="CosmosDateBounds"/>). A bound written as a whole second with all seven fractional
/// digits (<c>"…T10:00:00.0000000Z"</c>) sorts before every stored value in that second and after
/// every value before it, so a query can select whole seconds exactly. The reads below select the
/// whole seconds strictly inside a window exactly, read the rows of the seconds holding a bound,
/// and compare those as instants in code.
/// </remarks>
internal sealed class CosmosDbAlertSignalStore : IAlertSignalStore
{
    private const string FailedStatus = "Failed";
    private const string PendingStatus = "Pending";

    private const string EnqueuedTime = "c.message.EnqueuedTimeUtc";
    private const string ErrorText = "c.message.MessageContent.ErrorContent.ErrorText";

    // The minute prefix of a stored value, as SUBSTRING(…, 0, 16) returns it.
    private const string MinuteFormat = "yyyy-MM-dd'T'HH:mm";

    // ErrorContent.ErrorText when it is a non-empty string, else the dead-letter description.
    private const string ErrorTextCondition =
        $" AND ((IS_STRING({ErrorText}) AND {ErrorText} != '' AND CONTAINS({ErrorText}, @errorText, true))" +
        $" OR (NOT (IS_STRING({ErrorText}) AND {ErrorText} != '') AND CONTAINS(c.message.DeadLetterErrorDescription, @errorText, true)))";

    private const string BlockingCondition =
        "(NOT IS_DEFINED(c.deleted) OR c.deleted != true) " +
        "AND (c.status = @failedStatus OR (c.status = @pendingStatus AND c.event.PendingSubStatus = 'Handoff'))";

    private readonly Func<Task<ICosmosContainerAdapter>> _getMessagesContainer;
    private readonly Func<string, Task<ICosmosContainerAdapter>> _getEndpointContainer;

    public CosmosDbAlertSignalStore(
        Func<Task<ICosmosContainerAdapter>> getMessagesContainer,
        Func<string, Task<ICosmosContainerAdapter>> getEndpointContainer)
    {
        _getMessagesContainer = getMessagesContainer;
        _getEndpointContainer = getEndpointContainer;
    }

    public async Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        if (toUtc <= fromUtc)
            return 0;

        var (filter, parameters) = MessageFilter(endpointId, MessageType.ErrorResponse, eventTypes, errorTextContains);
        var container = await _getMessagesContainer();
        var firstSecond = WholeSecond(fromUtc);
        // The second holding the last instant in the window; toUtc itself is excluded.
        var lastSecond = WholeSecond(toUtc.AddTicks(-1));

        // The whole seconds strictly between the two bound seconds are inside the window.
        var count = 0;
        var innerFrom = firstSecond.AddSeconds(1);
        if (innerFrom < lastSecond)
        {
            var inner = WithParameters(
                    new QueryDefinition($"SELECT TOP @cap VALUE c.id FROM c WHERE {filter} AND {EnqueuedTime} >= @innerFrom AND {EnqueuedTime} < @innerTo"),
                    parameters)
                .WithParameter("@cap", cap)
                .WithParameter("@innerFrom", SecondBound(innerFrom))
                .WithParameter("@innerTo", SecondBound(lastSecond));
            count = await CountRows<string>(container, inner, cap);
            if (count >= cap)
                return cap;
        }

        // The seconds that hold a bound: every row is read and compared as an instant, since rows
        // just outside the window would otherwise use up the cap.
        var borderSql = lastSecond == firstSecond
            ? $"SELECT VALUE {EnqueuedTime} FROM c WHERE {filter} AND {EnqueuedTime} >= @firstFrom AND {EnqueuedTime} < @firstTo"
            : $"SELECT VALUE {EnqueuedTime} FROM c WHERE {filter} AND (({EnqueuedTime} >= @firstFrom AND {EnqueuedTime} < @firstTo) " +
              $"OR ({EnqueuedTime} >= @lastFrom AND {EnqueuedTime} < @lastTo))";
        var border = WithParameters(new QueryDefinition(borderSql), parameters)
            .WithParameter("@firstFrom", SecondBound(firstSecond))
            .WithParameter("@firstTo", SecondBound(firstSecond.AddSeconds(1)));
        if (lastSecond != firstSecond)
        {
            border = border
                .WithParameter("@lastFrom", SecondBound(lastSecond))
                .WithParameter("@lastTo", SecondBound(lastSecond.AddSeconds(1)));
        }

        using var iterator = container.GetItemQueryIterator<DateTime>(border);
        while (iterator.HasMoreResults)
        {
            foreach (var enqueued in await iterator.ReadNextAsync())
            {
                var instant = AsUtc(enqueued);
                if (instant >= fromUtc && instant < toUtc && ++count >= cap)
                    return cap;
            }
        }

        return count;
    }

    public async Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        try
        {
            var container = await _getEndpointContainer(endpointId);

            // TOP 1 by the stored string finds the second that holds the oldest event; within that
            // second the string order can pick a later row, so every row of that second is read.
            var first = new QueryDefinition($"SELECT TOP 1 VALUE c.event.UpdatedAt FROM c WHERE {BlockingCondition} ORDER BY c.event.UpdatedAt ASC")
                .WithParameter("@failedStatus", FailedStatus)
                .WithParameter("@pendingStatus", PendingStatus);
            DateTime? inOldestSecond = null;
            using (var iterator = container.GetItemQueryIterator<DateTime>(first))
            {
                while (iterator.HasMoreResults && inOldestSecond is null)
                {
                    foreach (var updatedAt in await iterator.ReadNextAsync())
                    {
                        inOldestSecond = AsUtc(updatedAt);
                        break;
                    }
                }
            }

            if (inOldestSecond is not { } oldestSecondValue)
                return null;

            var candidates = new QueryDefinition(
                    "SELECT c.id, c.sessionId, c.status, c.event.EventId AS eventId, c.event.UpdatedAt AS updatedAt " +
                    $"FROM c WHERE {BlockingCondition} AND c.event.UpdatedAt < @to")
                .WithParameter("@failedStatus", FailedStatus)
                .WithParameter("@pendingStatus", PendingStatus)
                .WithParameter("@to", SecondBound(WholeSecond(oldestSecondValue).AddSeconds(1)));
            BlockingRow? oldest = null;
            using (var iterator = container.GetItemQueryIterator<BlockingRow>(candidates))
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var row in await iterator.ReadNextAsync())
                    {
                        row.UpdatedAt = AsUtc(row.UpdatedAt);
                        if (oldest is null
                            || row.UpdatedAt < oldest.UpdatedAt
                            || (row.UpdatedAt == oldest.UpdatedAt && string.CompareOrdinal(row.Id, oldest.Id) < 0))
                        {
                            oldest = row;
                        }
                    }
                }
            }

            // The row read first was resolved in between, and nothing else blocks in its second.
            if (oldest is null)
                return null;

            return new BlockingEvent(
                oldest.EventId ?? string.Empty,
                oldest.SessionId ?? string.Empty,
                Enum.Parse<ResolutionStatus>(oldest.Status),
                oldest.UpdatedAt);
        }
        catch (EndpointNotFoundException)
        {
            return null;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        var container = await _getMessagesContainer();
        const string Filter = "c.endpointId = @endpointId AND c.message.MessageType = 'ResolutionResponse'";
        var sinceSecond = WholeSecond(sinceUtc);

        // Any completion from the next whole second on is at or after sinceUtc.
        var later = new QueryDefinition($"SELECT TOP 1 VALUE c.id FROM c WHERE {Filter} AND {EnqueuedTime} >= @from")
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@from", SecondBound(sinceSecond.AddSeconds(1)));
        if (await CountRows<string>(container, later, 1) > 0)
            return true;

        // In the second that holds sinceUtc, compare as instants.
        var border = new QueryDefinition($"SELECT VALUE {EnqueuedTime} FROM c WHERE {Filter} AND {EnqueuedTime} >= @from AND {EnqueuedTime} < @to")
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@from", SecondBound(sinceSecond))
            .WithParameter("@to", SecondBound(sinceSecond.AddSeconds(1)));
        using var iterator = container.GetItemQueryIterator<DateTime>(border);
        while (iterator.HasMoreResults)
        {
            foreach (var enqueued in await iterator.ReadNextAsync())
            {
                if (AsUtc(enqueued) >= sinceUtc)
                    return true;
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<MinuteCount>> CountMessagesPerMinute(string endpointId, MessageType type, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ValidateMinuteBounds(fromUtc, toUtc);

        // Whole-minute bounds are whole seconds, which select exactly (see the remarks above), and the
        // first 16 characters of every stored value ("yyyy-MM-ddTHH:mm") name its minute.
        var (filter, parameters) = MessageFilter(endpointId, type, eventTypes, errorTextContains);
        var query = WithParameters(
                new QueryDefinition(
                    $"SELECT SUBSTRING({EnqueuedTime}, 0, 16) AS bucket, COUNT(1) AS count FROM c " +
                    $"WHERE {filter} AND {EnqueuedTime} >= @from AND {EnqueuedTime} < @to " +
                    $"GROUP BY SUBSTRING({EnqueuedTime}, 0, 16)"),
                parameters)
            .WithParameter("@from", SecondBound(fromUtc))
            .WithParameter("@to", SecondBound(toUtc));

        var container = await _getMessagesContainer();
        // A cross-partition GROUP BY can return a minute more than once, with partial counts.
        var counts = new SortedDictionary<DateTime, int>();
        using var iterator = container.GetItemQueryIterator<MinuteRow>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var row in await iterator.ReadNextAsync())
            {
                if (row.Bucket is null)
                    continue;
                var minute = DateTime.ParseExact(row.Bucket, MinuteFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                counts[minute] = (counts.TryGetValue(minute, out var partial) ? partial : 0) + row.Count;
            }
        }

        return counts.Select(kv => new MinuteCount(kv.Key, kv.Value)).ToList();
    }

    public async Task<FailedAttemptPage> ListFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (toUtc <= fromUtc)
            return new FailedAttemptPage([], false);

        var (filter, parameters) = MessageFilter(endpointId, MessageType.ErrorResponse, eventTypes, errorTextContains);
        var container = await _getMessagesContainer();
        var firstSecond = WholeSecond(fromUtc);
        // The second holding the last instant in the window; toUtc itself is excluded.
        var lastSecond = WholeSecond(toUtc.AddTicks(-1));
        // One more than the limit tells whether more matched.
        var take = limit == int.MaxValue ? limit : limit + 1;
        const string Projection = $"SELECT c.id, c.eventId, {EnqueuedTime} AS enqueuedTimeUtc FROM c";

        // The seconds that hold a bound: every row is read and compared as an instant.
        var borderSql = lastSecond == firstSecond
            ? $"{Projection} WHERE {filter} AND {EnqueuedTime} >= @firstFrom AND {EnqueuedTime} < @firstTo"
            : $"{Projection} WHERE {filter} AND (({EnqueuedTime} >= @firstFrom AND {EnqueuedTime} < @firstTo) " +
              $"OR ({EnqueuedTime} >= @lastFrom AND {EnqueuedTime} < @lastTo))";
        var border = WithParameters(new QueryDefinition(borderSql), parameters)
            .WithParameter("@firstFrom", SecondBound(firstSecond))
            .WithParameter("@firstTo", SecondBound(firstSecond.AddSeconds(1)));
        if (lastSecond != firstSecond)
        {
            border = border
                .WithParameter("@lastFrom", SecondBound(lastSecond))
                .WithParameter("@lastTo", SecondBound(lastSecond.AddSeconds(1)));
        }

        var rows = (await ReadAttempts(container, border)).Where(r => r.EnqueuedTimeUtc >= fromUtc && r.EnqueuedTimeUtc < toUtc).ToList();

        // The whole seconds strictly between the two bound seconds are inside the window. Their
        // newest rows by the stored string: that order is chronological between seconds but not
        // within one, so the oldest second reached may be cut short, or be missing its newest rows.
        var innerFrom = firstSecond.AddSeconds(1);
        if (innerFrom < lastSecond)
        {
            var newest = WithParameters(
                    new QueryDefinition($"SELECT TOP @take c.id, c.eventId, {EnqueuedTime} AS enqueuedTimeUtc FROM c " +
                        $"WHERE {filter} AND {EnqueuedTime} >= @innerFrom AND {EnqueuedTime} < @innerTo ORDER BY {EnqueuedTime} DESC"),
                    parameters)
                .WithParameter("@take", take)
                .WithParameter("@innerFrom", SecondBound(innerFrom))
                .WithParameter("@innerTo", SecondBound(lastSecond));
            var inner = await ReadAttempts(container, newest, take);
            if (inner.Count >= take)
            {
                // Every second after the oldest one reached is complete; read that one whole.
                var cut = WholeSecond(inner.Min(r => r.EnqueuedTimeUtc));
                inner.RemoveAll(r => WholeSecond(r.EnqueuedTimeUtc) == cut);
                var cutSecond = WithParameters(
                        new QueryDefinition($"{Projection} WHERE {filter} AND {EnqueuedTime} >= @cutFrom AND {EnqueuedTime} < @cutTo"),
                        parameters)
                    .WithParameter("@cutFrom", SecondBound(cut))
                    .WithParameter("@cutTo", SecondBound(cut.AddSeconds(1)));
                inner.AddRange(await ReadAttempts(container, cutSecond));
            }

            rows.AddRange(inner);
        }

        var ordered = rows
            .OrderByDescending(r => r.EnqueuedTimeUtc)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ThenBy(r => r.EventId, StringComparer.Ordinal)
            .ToList();
        var items = ordered
            .Take(limit)
            .Select(r => new FailedAttempt(r.Id, r.EventId ?? string.Empty, r.EnqueuedTimeUtc))
            .ToList();
        return new FailedAttemptPage(items, ordered.Count > limit);
    }

    // The endpoint's messages of one type that pass the event-type and error-text filters.
    private static (string Filter, List<(string Name, object Value)> Parameters) MessageFilter(
        string endpointId, MessageType type, IReadOnlyCollection<string>? eventTypes, string? errorTextContains)
    {
        var filter = "c.endpointId = @endpointId AND c.message.MessageType = @messageType";
        var parameters = new List<(string Name, object Value)> { ("@endpointId", endpointId), ("@messageType", type.ToString()) };
        if (eventTypes is { Count: > 0 })
        {
            filter += " AND ARRAY_CONTAINS(@eventTypes, c.message.EventTypeId)";
            parameters.Add(("@eventTypes", eventTypes.Distinct(StringComparer.Ordinal).ToList()));
        }

        if (!string.IsNullOrEmpty(errorTextContains))
        {
            filter += ErrorTextCondition;
            parameters.Add(("@errorText", errorTextContains));
        }

        return (filter, parameters);
    }

    private static void ValidateMinuteBounds(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("The start must be a whole minute.", nameof(fromUtc));
        if (toUtc.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("The end must be a whole minute.", nameof(toUtc));
        if (toUtc <= fromUtc)
            throw new ArgumentException("The end must be after the start.", nameof(toUtc));
    }

    private static async Task<List<AttemptRow>> ReadAttempts(ICosmosContainerAdapter container, QueryDefinition query, int max = int.MaxValue)
    {
        var rows = new List<AttemptRow>();
        using var iterator = container.GetItemQueryIterator<AttemptRow>(query);
        while (iterator.HasMoreResults && rows.Count < max)
        {
            foreach (var row in await iterator.ReadNextAsync())
            {
                row.EnqueuedTimeUtc = AsUtc(row.EnqueuedTimeUtc);
                rows.Add(row);
            }
        }

        return rows;
    }

    private static QueryDefinition WithParameters(QueryDefinition query, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
            query = query.WithParameter(name, value);
        return query;
    }

    private static async Task<int> CountRows<T>(ICosmosContainerAdapter container, QueryDefinition query, int cap)
    {
        var count = 0;
        using var iterator = container.GetItemQueryIterator<T>(query);
        while (iterator.HasMoreResults && count < cap)
            count += (await iterator.ReadNextAsync()).Count;
        return Math.Min(count, cap);
    }

    // A bound's ticks are read as UTC, as on the other providers.
    private static DateTime WholeSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    // All seven fractional digits: sorts before every stored value in the second, after every
    // value before it (see the remarks above).
    private static string SecondBound(DateTime wholeSecond) =>
        wholeSecond.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private sealed class BlockingRow
    {
        [JsonProperty("id")] public string Id { get; set; } = null!;
        [JsonProperty("sessionId")] public string? SessionId { get; set; }
        [JsonProperty("status")] public string Status { get; set; } = null!;
        [JsonProperty("eventId")] public string? EventId { get; set; }
        [JsonProperty("updatedAt")] public DateTime UpdatedAt { get; set; }
    }

    private sealed class MinuteRow
    {
        [JsonProperty("bucket")] public string? Bucket { get; set; }
        [JsonProperty("count")] public int Count { get; set; }
    }

    private sealed class AttemptRow
    {
        [JsonProperty("id")] public string Id { get; set; } = null!;
        [JsonProperty("eventId")] public string? EventId { get; set; }
        [JsonProperty("enqueuedTimeUtc")] public DateTime EnqueuedTimeUtc { get; set; }
    }
}
