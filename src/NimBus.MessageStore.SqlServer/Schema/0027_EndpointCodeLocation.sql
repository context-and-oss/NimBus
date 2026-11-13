-- Endpoint ownership and code location (Spec 044 Phase 3): the source system the adapter
-- integrates, the adapter's repository and the issue tracker's link template.
IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'SourceSystem') IS NULL
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [SourceSystem] NVARCHAR(200) NULL;
GO
IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'RepositoryUrl') IS NULL
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [RepositoryUrl] NVARCHAR(400) NULL;
GO
IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'TicketLinkTemplate') IS NULL
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [TicketLinkTemplate] NVARCHAR(400) NULL;
GO
