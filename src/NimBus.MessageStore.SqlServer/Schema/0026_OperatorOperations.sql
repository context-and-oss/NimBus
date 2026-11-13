-- Operator operation journal (Spec 044 Phase 2): one row per operator command sent over MCP.
-- OperationId is derived from tenant, caller and idempotency key, so the primary key makes a
-- retry with the same key find the same row. Version is the optimistic-concurrency token.
IF OBJECT_ID('[$schema$].[OperatorOperations]', 'U') IS NULL
BEGIN
    CREATE TABLE [$schema$].[OperatorOperations] (
        [OperationId]       NVARCHAR(64)   NOT NULL,
        [TenantId]          NVARCHAR(64)   NOT NULL,
        [Caller]            NVARCHAR(256)  NOT NULL,
        [ClientId]          NVARCHAR(64)   NULL,
        [IsWorkload]        BIT            NOT NULL,
        [ActorName]         NVARCHAR(256)  NULL,
        [IdempotencyKey]    NVARCHAR(64)   NOT NULL,
        [ArgumentsHash]     NVARCHAR(64)   NOT NULL,
        [Action]            NVARCHAR(32)   NOT NULL,
        [EndpointId]        NVARCHAR(200)  NOT NULL,
        [EventId]           NVARCHAR(200)  NOT NULL,
        [SessionId]         NVARCHAR(200)  NOT NULL,
        [SourceMessageId]   NVARCHAR(200)  NOT NULL,
        [MessageVersion]    NVARCHAR(1000) NOT NULL,
        [Reason]            NVARCHAR(1000) NOT NULL,
        [Status]            NVARCHAR(32)   NOT NULL,
        [ObservedStatus]    NVARCHAR(64)   NULL,
        [EvidenceMessageId] NVARCHAR(200)  NULL,
        [ObservedAtUtc]     DATETIME2      NULL,
        [Detail]            NVARCHAR(1000) NULL,
        [LeaseOwner]        NVARCHAR(128)  NULL,
        [LeaseUntilUtc]     DATETIME2      NULL,
        [DispatchedAtUtc]   DATETIME2      NULL,
        [CreatedAtUtc]      DATETIME2      NOT NULL,
        [UpdatedAtUtc]      DATETIME2      NOT NULL,
        [ExpiresAtUtc]      DATETIME2      NOT NULL,
        [Version]           NVARCHAR(32)   NOT NULL,
        CONSTRAINT [PK_OperatorOperations] PRIMARY KEY ([OperationId])
    );

    -- Per-message cap: operations on one event.
    CREATE INDEX [IX_OperatorOperations_Endpoint_Event] ON [$schema$].[OperatorOperations] ([EndpointId], [EventId]);
    -- Per-agent budget: a client's operations in a window.
    CREATE INDEX [IX_OperatorOperations_Client_Created] ON [$schema$].[OperatorOperations] ([ClientId], [CreatedAtUtc]);
    -- Reconciler: due operations by status.
    CREATE INDEX [IX_OperatorOperations_Status_Updated] ON [$schema$].[OperatorOperations] ([Status], [UpdatedAtUtc]);
    -- Retention purge.
    CREATE INDEX [IX_OperatorOperations_Expires] ON [$schema$].[OperatorOperations] ([ExpiresAtUtc]);
END
GO
