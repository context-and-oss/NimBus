using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.MessageStore.States;
using System.Net;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    public async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var container = await _getEndpointContainer(endpointId);
        const string sqlQuery =
            "SELECT COUNT(1) AS EventCount, MIN(c.event.UpdatedAt) AS OldestUpdatedAt, c.status FROM c WHERE (NOT IS_DEFINED(c.deleted) or c.deleted != true) " +
            "AND c.status IN (@pendingStatus, @deferredStatus, @failedStatus, @deadletterStatus, @unsupportedStatus) GROUP BY c.status";
        var queryDefinition = new QueryDefinition(sqlQuery)
            .WithParameter("@pendingStatus", PendingStatus)
            .WithParameter("@deferredStatus", DeferredStatus)
            .WithParameter("@failedStatus", FailedStatus)
            .WithParameter("@deadletterStatus", DLQStatus)
            .WithParameter("@unsupportedStatus", UnsupportedStatus);

        var result = container.GetItemQueryIterator<StatusQueryResult>(queryDefinition);
        var resultDict = new Dictionary<string, int>();
        DateTime? oldestFailure = null;
        while (result.HasMoreResults)
        {
            var currentResultSet = await result.ReadNextAsync();
            foreach (var queryResult in currentResultSet)
            {
                resultDict.Add(queryResult.Status, queryResult.EventCount);
                if (queryResult.Status is FailedStatus or DLQStatus && queryResult.OldestUpdatedAt is { } oldest)
                {
                    oldest = AsUtc(oldest);
                    if (oldestFailure is null || oldest < oldestFailure)
                        oldestFailure = oldest;
                }
            }
        }

        // MIN compares the stored strings (see CosmosDateBounds): it finds the second that holds
        // the oldest failure, but within that second it can pick a later row.
        if (oldestFailure is { } inOldestSecond)
            oldestFailure = await ExactOldestFailureAt(container, inOldestSecond);

        return new EndpointStateCount
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            OldestFailureAt = oldestFailure,
            DeferredCount = resultDict.ContainsKey(DeferredStatus) ? resultDict[DeferredStatus] : 0,
            PendingCount = resultDict.ContainsKey(PendingStatus) ? resultDict[PendingStatus] : 0,
            FailedCount = resultDict.ContainsKey(FailedStatus) ? resultDict[FailedStatus] : 0,
            DeadletterCount = resultDict.ContainsKey(DLQStatus) ? resultDict[DLQStatus] : 0,
            UnsupportedCount = resultDict.ContainsKey(UnsupportedStatus) ? resultDict[UnsupportedStatus] : 0,
        };
    }

    // Reads the failures up to the widened end of the second that holds inOldestSecond and
    // takes their minimum as instants. None are older than that second, so this is exact.
    private static async Task<DateTime> ExactOldestFailureAt(ICosmosContainerAdapter container, DateTime inOldestSecond)
    {
        var queryDefinition = new QueryDefinition(
                "SELECT VALUE c.event.UpdatedAt FROM c WHERE (NOT IS_DEFINED(c.deleted) or c.deleted != true) " +
                "AND c.status IN (@failedStatus, @deadletterStatus) AND c.event.UpdatedAt <= @to")
            .WithParameter("@failedStatus", FailedStatus)
            .WithParameter("@deadletterStatus", DLQStatus)
            .WithParameter("@to", CosmosDateBounds.WidenTo(inOldestSecond));

        // The read returns the MIN row too, unless it was resolved after the count query.
        var oldest = inOldestSecond;
        var result = container.GetItemQueryIterator<DateTime>(queryDefinition);
        while (result.HasMoreResults)
        {
            foreach (var updatedAt in await result.ReadNextAsync())
            {
                var candidate = AsUtc(updatedAt);
                if (candidate < oldest)
                    oldest = candidate;
            }
        }

        return oldest;
    }

    // UpdatedAt is written from DateTime.UtcNow and round-trips with a "Z" suffix; guard
    // documents written without one so the API never emits a local-offset instant.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    public async Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId)
    {
        var container = await _getEndpointContainer(endpointId);
        var queryDefinition = new QueryDefinition(
            "SELECT c.id, c.status FROM c WHERE c.status IN (@pendingStatus, @deferredStatus) AND c.sessionId = @sessionId AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@pendingStatus", PendingStatus)
            .WithParameter("@deferredStatus", DeferredStatus)
            .WithParameter("@sessionId", sessionId);

        var result = container.GetItemQueryIterator<SessionCountQueryResult>(queryDefinition);

        var sessionResults = new List<SessionCountQueryResult>();

        while (result.HasMoreResults)
        {
            var currentResultSet = await result.ReadNextAsync();
            foreach (var queryResult in currentResultSet)
            {
                sessionResults.Add(queryResult);
            }
        }

        return new SessionStateCount
        {
            SessionId = sessionId,
            DeferredEvents = sessionResults
                .Where(se => se.Status.Equals(DeferredStatus, StringComparison.OrdinalIgnoreCase))
                .Select(se => se.Id),
            PendingEvents = sessionResults
                .Where(se => se.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase))
                .Select(se => se.Id)
        };
    }

    public async Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
    {
        var sessionIdList = sessionIds.ToList();
        if (sessionIdList.Count == 0)
            return Enumerable.Empty<SessionStateCount>();

        var container = await _getEndpointContainer(endpointId);
        var queryDefinition = new QueryDefinition(
            "SELECT c.id, c.status, c.sessionId FROM c WHERE c.status IN (@pendingStatus, @deferredStatus) AND ARRAY_CONTAINS(@sessionIds, c.sessionId) AND (NOT IS_DEFINED(c.deleted) or c.deleted != true)")
            .WithParameter("@pendingStatus", PendingStatus)
            .WithParameter("@deferredStatus", DeferredStatus)
            .WithParameter("@sessionIds", sessionIdList);

        var result = container.GetItemQueryIterator<BatchSessionQueryResult>(queryDefinition);
        var allResults = new List<BatchSessionQueryResult>();

        while (result.HasMoreResults)
        {
            var currentResultSet = await result.ReadNextAsync();
            foreach (var queryResult in currentResultSet)
            {
                allResults.Add(queryResult);
            }
        }

        return allResults
            .GroupBy(r => r.SessionId)
            .Select(g => new SessionStateCount
            {
                SessionId = g.Key,
                DeferredEvents = g.Where(r => r.Status.Equals(DeferredStatus, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id),
                PendingEvents = g.Where(r => r.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase)).Select(r => r.Id)
            });
    }

    public async Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize,
        string continuationToken)
    {
        var container = await _getEndpointContainer(endpointId);

        var requestOptions = new QueryRequestOptions
        {
            MaxItemCount = pageSize
        };

        try
        {
            FeedIterator<EventDbo> result = container.GetItemLinqQueryable<EventDbo>(
                    true,
                    String.IsNullOrEmpty(continuationToken) ? null : continuationToken,
                    requestOptions)
                .Where(e => e.Status.Equals(PendingStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(DeferredStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(FailedStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(DLQStatus, StringComparison.OrdinalIgnoreCase)
                            || e.Status.Equals(UnsupportedStatus, StringComparison.OrdinalIgnoreCase))
                .Where(e => !e.Deleted.HasValue || !e.Deleted.Value)
                .OrderByDescending(e => e.Event.UpdatedAt)
                // Server-side projection: every UnresolvedEvent property EXCEPT the
                // heavy EventJson payload, which dominates the response size and is
                // never surfaced by the endpoint page list (the detail view fetches
                // it on demand). Same shape as GetEventsByFilter — keep in sync with
                // that projection and the UnresolvedEvent drift guard.
                .Select(x => new EventDbo
                {
                    Id = x.Id,
                    Status = x.Status,
                    EventType = x.EventType,
                    SessionId = x.SessionId,
                    Deleted = x.Deleted,
                    Event = new UnresolvedEvent
                    {
                        UpdatedAt = x.Event.UpdatedAt,
                        EnqueuedTimeUtc = x.Event.EnqueuedTimeUtc,
                        EventId = x.Event.EventId,
                        SessionId = x.Event.SessionId,
                        CorrelationId = x.Event.CorrelationId,
                        ResolutionStatus = x.Event.ResolutionStatus,
                        EndpointRole = x.Event.EndpointRole,
                        EndpointId = x.Event.EndpointId,
                        RetryCount = x.Event.RetryCount,
                        RetryLimit = x.Event.RetryLimit,
                        MessageType = x.Event.MessageType,
                        DeadLetterReason = x.Event.DeadLetterReason,
                        DeadLetterErrorDescription = x.Event.DeadLetterErrorDescription,
                        LastMessageId = x.Event.LastMessageId,
                        OriginatingMessageId = x.Event.OriginatingMessageId,
                        ParentMessageId = x.Event.ParentMessageId,
                        Reason = x.Event.Reason,
                        OriginatingFrom = x.Event.OriginatingFrom,
                        EventTypeId = x.Event.EventTypeId,
                        To = x.Event.To,
                        From = x.Event.From,
                        MessageContent = new MessageContent
                        {
                            // EventJson deliberately omitted — the sole purpose of
                            // this projection.
                            EventContent = new EventContent
                            {
                                EventTypeId = x.Event.MessageContent.EventContent!.EventTypeId,
                            },
                            ErrorContent = x.Event.MessageContent.ErrorContent,
                        },
                        QueueTimeMs = x.Event.QueueTimeMs,
                        ProcessingTimeMs = x.Event.ProcessingTimeMs,
                        PendingSubStatus = x.Event.PendingSubStatus,
                        HandoffReason = x.Event.HandoffReason,
                        ExternalJobId = x.Event.ExternalJobId,
                        ExpectedBy = x.Event.ExpectedBy,
                        CloudEventId = x.Event.CloudEventId,
                        CloudEventSource = x.Event.CloudEventSource,
                        CloudEventType = x.Event.CloudEventType,
                        CloudEventSubject = x.Event.CloudEventSubject,
                    },
                })
                .ToFeedIterator();
            result = CosmosExceptionTranslation.Wrap(result, _logger);

            var pendingEvents = new List<string>();
            var failedEvents = new List<string>();
            var deferredEvents = new List<string>();
            var deadletteredEvents = new List<string>();
            var unsupportedEvents = new List<string>();
            var unresolvedEvents = new List<UnresolvedEvent>();
            var token = "";
            if (result.HasMoreResults)
            {
                var feed = await result.ReadNextAsync();
                token = feed.ContinuationToken;
                foreach (var eventDbo in feed)
                {
                    unresolvedEvents.Add(eventDbo.Event);
                    var status = eventDbo.Status;
                    switch (status)
                    {
                        case FailedStatus:
                            failedEvents.Add(eventDbo.Id);
                            break;
                        case PendingStatus:
                            pendingEvents.Add(eventDbo.Id);
                            break;
                        case DeferredStatus:
                            deferredEvents.Add(eventDbo.Id);
                            break;
                        case DLQStatus:
                            deadletteredEvents.Add(eventDbo.Id);
                            break;
                        case UnsupportedStatus:
                            unsupportedEvents.Add(eventDbo.Id);
                            break;
                        default:
                            break;
                    }
                }
            }

            var endpointState = new EndpointState
            {
                EndpointId = endpointId,
                DeferredEvents = deferredEvents,
                PendingEvents = pendingEvents,
                FailedEvents = failedEvents,
                DeadletteredEvents = deadletteredEvents,
                UnsupportedEvents = unsupportedEvents,
                EnrichedUnresolvedEvents = unresolvedEvents,
                EventTime = DateTime.UtcNow,
                ContinuationToken = token
            };

            return endpointState;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("COSMOS PAGING: Endpoint container not found for '{EndpointId}'", endpointId);
            return null!;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS PAGING-ERROR: Failed to download endpoint state for '{EndpointId}'", endpointId);
            throw;
        }
    }
}
