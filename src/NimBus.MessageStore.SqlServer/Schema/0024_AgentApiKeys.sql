-- Spec 039: agent API keys. The issued keys live in one row as JSON (secrets hashed),
-- fenced by Revision so a stale editor cannot overwrite a newer save. Last-used times are
-- a separate per-key table so recording use never contends with an Owner's edit.
IF OBJECT_ID('[$schema$].[AgentApiKeys]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[AgentApiKeys] (
        [Id]            NVARCHAR(50)  NOT NULL PRIMARY KEY,
        [Revision]      NVARCHAR(36)  NOT NULL,
        [SettingsJson]  NVARCHAR(MAX) NOT NULL,
        [UpdatedAtUtc]  DATETIME2     NOT NULL CONSTRAINT [DF_AgentApiKeys_UpdatedAtUtc] DEFAULT (SYSUTCDATETIME())
    );
END
GO

IF OBJECT_ID('[$schema$].[AgentApiKeyUsage]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[AgentApiKeyUsage] (
        [KeyId]          NVARCHAR(64) NOT NULL PRIMARY KEY,
        [LastUsedAtUtc]  DATETIME2    NOT NULL
    );
END
GO
