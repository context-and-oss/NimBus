using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    public async Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
    {
        if (string.IsNullOrEmpty(endpointId)) throw new ArgumentNullException(nameof(endpointId));
        if (string.IsNullOrEmpty(eventId)) throw new ArgumentNullException(nameof(eventId));

        var report = new EventReport
        {
            Id = $"{endpointId}_{eventId}",
            EndpointId = endpointId,
            EventId = eventId,
            IsReported = isReported,
            ReportedBy = reportedBy,
            ReportedAtUtc = DateTime.UtcNow,
            // Only retain a ticket reference while the event is reported; clearing
            // the marker drops the ticket too.
            TicketId = isReported ? ticketId : null,
        };

        var container = await _getEventReportsContainer();
        await container.UpsertItemAsync(report, new PartitionKey(report.EndpointId));
    }

    public async Task<IReadOnlyList<EventReport>> FindEventReports(string endpointId, string? ticketId, int limit)
    {
        if (string.IsNullOrEmpty(endpointId) || limit <= 0)
            return [];

        // Single partition (EndpointId). Ordered in memory: a partition holds one endpoint's
        // markers, and ORDER BY would need a composite index the emulator ignores.
        var text = $"SELECT * FROM c WHERE c.{nameof(EventReport.EndpointId)} = @endpointId AND c.{nameof(EventReport.IsReported)} = true";
        var query = new QueryDefinition(ticketId is null ? text : text + $" AND c.{nameof(EventReport.TicketId)} = @ticketId")
            .WithParameter("@endpointId", endpointId);
        if (ticketId is not null)
            query = query.WithParameter("@ticketId", ticketId);

        var container = await _getEventReportsContainer();
        var results = new List<EventReport>();
        var iterator = container.GetItemQueryIterator<EventReport>(query, null, new QueryRequestOptions { PartitionKey = new PartitionKey(endpointId) });
        while (iterator.HasMoreResults)
        {
            foreach (var report in await iterator.ReadNextAsync())
            {
                if (report.ReportedAtUtc is { Kind: DateTimeKind.Unspecified } at)
                    report.ReportedAtUtc = DateTime.SpecifyKind(at, DateTimeKind.Utc);
                results.Add(report);
            }
        }

        return results
            .OrderByDescending(r => r.ReportedAtUtc)
            .ThenBy(r => r.EventId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, EventReport>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return result;

        // Single-partition batched read (EndpointId is the partition key).
        var query = new QueryDefinition(
                $"SELECT * FROM c WHERE c.{nameof(EventReport.EndpointId)} = @endpointId " +
                $"AND ARRAY_CONTAINS(@eventIds, c.{nameof(EventReport.EventId)})")
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@eventIds", ids);

        var container = await _getEventReportsContainer();
        var iterator = container.GetItemQueryIterator<EventReport>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var report in await iterator.ReadNextAsync())
            {
                if (!string.IsNullOrEmpty(report.EventId))
                    result[report.EventId] = report;
            }
        }

        return result;
    }
}
