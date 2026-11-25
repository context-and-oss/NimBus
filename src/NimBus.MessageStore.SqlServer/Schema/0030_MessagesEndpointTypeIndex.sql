-- The alert signal reads (Spec 053, SqlServerAlertSignalStore) filter Messages by endpoint, message
-- type and time: CountFailedAttempts, ListFailedAttempts and CountMessagesPerMinute by the type
-- asked for, HasCompletedMessageSince by ResolutionResponse. IX_Messages_EndpointId_EnqueuedTimeUtc
-- has no MessageType, so they did a key lookup for every message of the endpoint in the window, or
-- scanned the clustered index once the window held a few hundred messages. Measured for #442
-- (Spec 053 section 7) on 200,000 messages: a 24-hour failed-attempt count at cap 100 went from 2,009
-- logical reads to 5, and the completed-since check on an endpoint that only fails from 40,190 to 3.
-- The included columns make every read without an error-text filter covering: EventTypeId for the
-- event-type filter, MessageId and EventId for ListFailedAttempts.
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'Messages'
      AND i.name = 'IX_Messages_EndpointId_MessageType_EnqueuedTimeUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Messages_EndpointId_MessageType_EnqueuedTimeUtc]
        ON [$schema$].[Messages] ([EndpointId], [MessageType], [EnqueuedTimeUtc] DESC)
        INCLUDE ([EventTypeId], [MessageId], [EventId]);
END
GO
