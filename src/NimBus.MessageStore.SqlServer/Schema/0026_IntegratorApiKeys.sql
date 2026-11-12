-- Integrator API keys (Spec 045): role- and endpoint-scoped credentials for REST API callers. The issued
-- keys live in one row as JSON (secrets hashed), fenced by Revision so a stale editor cannot
-- overwrite a newer save. Last-used times are a separate per-key table so recording use never
-- contends with an Owner's edit. Same shape as 0024_AgentApiKeys and 0025_DashboardTokens.
IF OBJECT_ID('[$schema$].[IntegratorApiKeys]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[IntegratorApiKeys] (
        [Id]            NVARCHAR(50)  NOT NULL PRIMARY KEY,
        [Revision]      NVARCHAR(36)  NOT NULL,
        [SettingsJson]  NVARCHAR(MAX) NOT NULL,
        [UpdatedAtUtc]  DATETIME2     NOT NULL CONSTRAINT [DF_IntegratorApiKeys_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF OBJECT_ID('[$schema$].[IntegratorApiKeyUsage]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[IntegratorApiKeyUsage] (
        [KeyId]          NVARCHAR(64) NOT NULL PRIMARY KEY,
        [LastUsedAtUtc]  DATETIME2    NOT NULL
    );
END
GO
