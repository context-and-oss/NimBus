-- Spec 050 (GH#291): indexes for the retention cleanup's cutoff predicates, so each
-- DELETE TOP (5000) batch seeks instead of scanning the table.
--   Message history   Messages          WHERE CreatedAtUtc < @Cutoff
--   Resolved events   UnresolvedEvents  WHERE Deleted = 1 AND UpdatedAtUtc < @Cutoff
-- The other rules are already served by indexes from 0014: open events and visible Completed
-- or Skipped rows (Deleted = 0) by IX_UnresolvedEvents_UpdatedAtUtc, and the audit trail by
-- IX_MessageAudits_CreatedAtUtc.

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
      AND i.name = 'IX_UnresolvedEvents_Deleted_UpdatedAtUtc'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_UnresolvedEvents_Deleted_UpdatedAtUtc]
        ON [$schema$].[UnresolvedEvents] ([UpdatedAtUtc])
        WHERE [Deleted] = 1;
END
GO
