-- Operations agent support (Spec 051 §5, §6): the address of the adapter's read-only MCP server
-- and the record bindings per event type, as JSON.
IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'AdapterMcpUrl') IS NULL
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [AdapterMcpUrl] NVARCHAR(400) NULL;
GO
IF COL_LENGTH('[$schema$].[EndpointMetadata]', 'RecordBindingsJson') IS NULL
    ALTER TABLE [$schema$].[EndpointMetadata] ADD [RecordBindingsJson] NVARCHAR(MAX) NULL;
GO
