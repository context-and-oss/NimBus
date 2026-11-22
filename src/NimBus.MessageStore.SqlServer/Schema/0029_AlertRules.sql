-- Alert rules (Spec 053): one row per rule, keyed by endpoint and rule. The definition and the
-- evaluator's state are JSON; the state's transitions live in their own column so the evaluator's
-- list never reads them. Version is the logical version the store bumps on every write, compared
-- by every conditional write. Enabled, HasPendingNotices and Status mirror the JSON for the
-- evaluator's and the overview's queries.
IF OBJECT_ID('[$schema$].[AlertRules]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[AlertRules] (
        [EndpointId]        NVARCHAR(200) NOT NULL,
        [RuleId]            NVARCHAR(64)  NOT NULL,
        [Name]              NVARCHAR(100) NOT NULL,
        [Enabled]           BIT           NOT NULL,
        [Status]            NVARCHAR(16)  NOT NULL,
        [HasPendingNotices] BIT           NOT NULL,
        [Version]           BIGINT        NOT NULL,
        [DefinitionJson]    NVARCHAR(MAX) NOT NULL,
        [StateJson]         NVARCHAR(MAX) NOT NULL,
        [TransitionsJson]   NVARCHAR(MAX) NOT NULL,
        [CreatedAtUtc]      DATETIME2     NOT NULL,
        [UpdatedAtUtc]      DATETIME2     NOT NULL,
        CONSTRAINT [PK_AlertRules] PRIMARY KEY ([EndpointId], [RuleId])
    );

    -- The evaluator's list: enabled rules and rules with notices still to deliver.
    CREATE INDEX [IX_AlertRules_Enabled_PendingNotices] ON [$schema$].[AlertRules] ([Enabled], [HasPendingNotices]);
    -- Endpoints with a firing rule.
    CREATE INDEX [IX_AlertRules_Status] ON [$schema$].[AlertRules] ([Status]) INCLUDE ([EndpointId]);
END
GO

-- The evaluator lease: one row (Id = 1) that scaled-out WebApp instances claim one tick at a time.
IF OBJECT_ID('[$schema$].[AlertRuleEvaluator]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[AlertRuleEvaluator] (
        [Id]                 TINYINT       NOT NULL,
        [LastClaimedAtUtc]   DATETIME2     NULL,
        [LastClaimedBy]      NVARCHAR(200) NULL,
        [LastCompletedAtUtc] DATETIME2     NULL,
        CONSTRAINT [PK_AlertRuleEvaluator] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_AlertRuleEvaluator_SingleRow] CHECK ([Id] = 1)
    );
END
GO
