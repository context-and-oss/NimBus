-- GH#291: indexes for the retention job's cutoff predicates, so each
-- DELETE TOP (5000) batch seeks instead of scanning the table.
--   R1 Messages          WHERE CreatedAtUtc < @Cutoff
--   R3 UnresolvedEvents  WHERE Deleted = 1 AND UpdatedAtUtc < @Cutoff
-- R2 (MessageAudits.CreatedAtUtc) and R4 (UnresolvedEvents Deleted = 0, UpdatedAtUtc)
-- are already served by IX_MessageAudits_CreatedAtUtc and IX_UnresolvedEvents_UpdatedAtUtc (0014).

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'Messages'
      AND i.name = 'IX_Messages_CreatedAtUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_Messages_CreatedAtUtc]
        ON [$schema$].[Messages] ([CreatedAtUtc]);
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    INNER JOIN sys.tables t ON i.object_id = t.object_id
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE s.name = '$schema$'
      AND t.name = 'UnresolvedEvents'
      AND i.name = 'IX_UnresolvedEvents_Archived_UpdatedAtUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_UnresolvedEvents_Archived_UpdatedAtUtc]
        ON [$schema$].[UnresolvedEvents] ([UpdatedAtUtc])
        WHERE [Deleted] = 1;
END
GO
