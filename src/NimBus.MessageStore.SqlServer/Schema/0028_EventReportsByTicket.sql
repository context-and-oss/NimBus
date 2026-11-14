-- Find an endpoint's reported events by ticket (Spec 044 Phase 3).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_EventReports_Endpoint_Ticket' AND object_id = OBJECT_ID('[$schema$].[EventReports]'))
    CREATE INDEX [IX_EventReports_Endpoint_Ticket] ON [$schema$].[EventReports] ([EndpointId], [TicketId]) INCLUDE ([IsReported], [ReportedAtUtc]);
GO
