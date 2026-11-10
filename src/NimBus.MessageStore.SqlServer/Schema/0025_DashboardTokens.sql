-- Dashboard tokens: read-only credentials for the WebApp's Monitor wall display. The issued
-- tokens live in one row as JSON (secrets hashed), fenced by Revision so a stale editor cannot
-- overwrite a newer save. Last-used times are a separate per-token table so recording use never
-- contends with an Owner's edit. Same shape as 0024_AgentApiKeys.
IF OBJECT_ID('[$schema$].[DashboardTokens]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[DashboardTokens] (
        [Id]            NVARCHAR(50)  NOT NULL PRIMARY KEY,
        [Revision]      NVARCHAR(36)  NOT NULL,
        [SettingsJson]  NVARCHAR(MAX) NOT NULL,
        [UpdatedAtUtc]  DATETIME2     NOT NULL CONSTRAINT [DF_DashboardTokens_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF OBJECT_ID('[$schema$].[DashboardTokenUsage]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[DashboardTokenUsage] (
        [TokenId]        NVARCHAR(64) NOT NULL PRIMARY KEY,
        [LastUsedAtUtc]  DATETIME2    NOT NULL
    );
END
GO
