using NimBus.Core.Messages;
using Microsoft.Azure.Cosmos;
using NimBus.MessageStore;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>
/// Configuration for the optional, advisory failure-classification feature. Agents record
/// classifications over MCP; no model runs in NimBus. The built-in provider and its settings
/// (<c>Provider</c>, <c>TypeSafe:*</c>, <c>ApiKey</c>, <c>BaseUrl</c>, <c>Model</c>,
/// <c>TimeoutSeconds</c>) were removed in v5.0.0 and are ignored when still configured.
/// </summary>
public sealed class FailureClassificationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FailureClassification";

    /// <summary>Enables the feature after a restart. Defaults to false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Enables the selected host integration. Defaults to false.</summary>
    public bool IntelligenceEnabled { get; set; }

    /// <summary>Minimum category confidence for deterministic guidance.</summary>
    public double MinimumCategoryConfidence { get; set; } = 0.60;

    /// <summary>Retry likelihood threshold for deterministic guidance.</summary>
    public double RetryLikely { get; set; } = 0.75;

    /// <summary>Change-required likelihood threshold for deterministic guidance.</summary>
    public double ChangeRequired { get; set; } = 0.75;

    /// <summary>Whether the event payload may be exported after full PII redaction.</summary>
    public bool IncludeEventPayload { get; set; }

    /// <summary>
    /// Whether agents may read a failure's scrubbed stack trace in its evidence (Spec 044 Phase 3).
    /// The trace is stored only by subscribers that opt in. Off by default.
    /// </summary>
    public bool IncludeStackTrace { get; set; }

    /// <summary>Maximum recent failure rows included in the evidence.</summary>
    public int MaximumHistoryItems { get; set; } = 5;

    /// <summary>Maximum error text characters included in the evidence.</summary>
    public int MaximumErrorTextLength { get; set; } = 4000;

    /// <summary>Maximum serialized evidence characters.</summary>
    public int MaximumStateCharacters { get; set; } = 24_000;

    /// <summary>Optional exact endpoint allow-list. Empty means all endpoints.</summary>
    public string[] AllowedEndpoints { get; set; } = [];

    /// <summary>Validates configuration without contacting a store. Same as <see cref="ValidateEvidence"/>.</summary>
    public IReadOnlyList<string> Validate() => ValidateEvidence();

    /// <summary>
    /// Validates what recording and saved results need: the feature switches, the guidance
    /// thresholds and the evidence limits.
    /// </summary>
    public IReadOnlyList<string> ValidateEvidence()
    {
        var errors = new List<string>();
        if (Enabled && !IntelligenceEnabled) errors.Add($"{nameof(IntelligenceEnabled)} must be true when {nameof(Enabled)} is true.");
        if (!double.IsFinite(MinimumCategoryConfidence) || MinimumCategoryConfidence is < 0 or > 1) errors.Add("MinimumCategoryConfidence must be between 0 and 1.");
        if (!double.IsFinite(RetryLikely) || RetryLikely is < 0 or > 1) errors.Add("RetryLikely must be between 0 and 1.");
        if (!double.IsFinite(ChangeRequired) || ChangeRequired is < 0 or > 1) errors.Add("ChangeRequired must be between 0 and 1.");
        if (MaximumHistoryItems is < 0 or > 50) errors.Add("MaximumHistoryItems must be between 0 and 50.");
        if (MaximumErrorTextLength is < 1 or > 100_000) errors.Add("MaximumErrorTextLength is out of range.");
        if (MaximumStateCharacters is < 1_000 or > 100_000) errors.Add("MaximumStateCharacters is out of range.");
        return errors;
    }

    /// <summary>Nested evidence settings used by the documented configuration shape.</summary>
    public FailureClassificationDataOptions Data { get; set; } = new();

    /// <summary>Nested guidance thresholds used by the documented configuration shape.</summary>
    public FailureClassificationThresholdOptions Thresholds { get; set; } = new();

    /// <summary>Copies nested settings into the execution snapshot.</summary>
    public void ApplyNestedSettings()
    {
        IncludeEventPayload = Data.IncludeEventPayload;
        IncludeStackTrace = Data.IncludeStackTrace;
        MaximumHistoryItems = Data.MaximumHistoryItems;
        MaximumErrorTextLength = Data.MaximumErrorTextLength;
        MaximumStateCharacters = Data.MaximumStateCharacters;
        MinimumCategoryConfidence = Thresholds.MinimumCategoryConfidence;
        RetryLikely = Thresholds.RetryLikely;
        ChangeRequired = Thresholds.ChangeRequired;
    }
}

/// <summary>Nested evidence configuration.</summary>
public sealed class FailureClassificationDataOptions
{
    public bool IncludeEventPayload { get; set; }
    public bool IncludeStackTrace { get; set; }
    public bool IncludeRecentFailureHistory { get; set; } = true;
    public int MaximumHistoryItems { get; set; } = 5;
    public int MaximumErrorTextLength { get; set; } = 4000;
    public int MaximumStateCharacters { get; set; } = 24_000;
    public string[] AdditionalRedactedKeys { get; set; } = [];
}

/// <summary>Nested guidance configuration.</summary>
public sealed class FailureClassificationThresholdOptions
{
    public double MinimumCategoryConfidence { get; set; } = 0.60;
    public double RetryLikely { get; set; } = 0.75;
    public double ChangeRequired { get; set; } = 0.75;
}

/// <summary>Documented parent configuration for the optional module.</summary>
public sealed class IntegrationIntelligenceOptions
{
    public bool Enabled { get; set; }
    public FailureClassificationOptions FailureClassification { get; set; } = new() { Enabled = true };
}

/// <summary>Stable machine-readable failure guidance.</summary>
public enum FailureGuidance
{
    Uncertain,
    RetryMayHelp,
    ChangeLikelyRequired,
    Investigate,
}

/// <summary>Bounded, redacted failure evidence that agents read before they record a classification.</summary>
public sealed record FailureClassificationInput
{
    public required string MessageId { get; init; }
    public required string EventId { get; init; }
    public required string EventTypeId { get; init; }
    public required string EndpointId { get; init; }
    public string? SessionId { get; init; }
    public required string ResolutionStatus { get; init; }
    public int? RetryCount { get; init; }
    public int? RetryLimit { get; init; }
    public required FailureExceptionInfo Exception { get; init; }
    public string? DeadLetterReason { get; init; }
    public IReadOnlyList<FailureHistoryItem> RecentHistory { get; init; } = [];
    public string? EventPayloadJson { get; init; }

    /// <summary>The endpoint that sent the event (<c>From</c>).</summary>
    public string? SourceEndpoint { get; init; }

    /// <summary>The endpoint the event originated from, when it differs from <see cref="SourceEndpoint"/>.</summary>
    public string? OriginatingEndpoint { get; init; }

    /// <summary>
    /// The failing handler's scrubbed stack trace, when the subscriber stored one and an Owner
    /// allows agents to read it (Spec 044 Phase 3). At most 8,000 characters.
    /// </summary>
    public string? StackTrace { get; init; }
}

/// <summary>Safe exception fields exported as evidence.</summary>
public sealed record FailureExceptionInfo(string? Type, string? Message, string? Source);

/// <summary>A prior failure occurrence in the same endpoint and session.</summary>
public sealed record FailureHistoryItem(int Attempt, string MessageType, string? ErrorType, string? ErrorMessage, DateTime EnqueuedTimeUtc);

/// <summary>
/// Persisted classification result. Results made by the built-in provider before v5.0.0 keep
/// <c>Provider: "TypeSafe"</c>, question set version 1 and their token counts, and stay readable.
/// </summary>
public sealed record FailureClassification
{
    public required string Id { get; init; }
    public required string FailureMessageId { get; init; }
    public required int Revision { get; init; }
    public required string EventId { get; init; }
    public required string EventTypeId { get; init; }
    public required string EndpointId { get; init; }
    public string? SessionId { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required int QuestionSetVersion { get; init; }
    public required string Category { get; init; }
    public required double CategoryConfidence { get; init; }
    public required IReadOnlyDictionary<string, double> CategoryProbabilities { get; init; }
    public required double RetryLikelihood { get; init; }
    public required double ChangeRequiredLikelihood { get; init; }
    public required double ExternalDependencyLikelihood { get; init; }
    public required FailureGuidance Guidance { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public required bool EventPayloadIncluded { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The agent's answer to <c>fault_origin</c> (question set version 2); null on version 1 records.</summary>
    public string? FaultOrigin { get; init; }

    /// <summary>The agent's confidence in <see cref="FaultOrigin"/>, 0 to 1.</summary>
    public double? FaultOriginConfidence { get; init; }

    /// <summary>The client application that recorded it (<c>azp</c>, else <c>appid</c>), for an agent result.</summary>
    public string? ClientId { get; init; }

    /// <summary>The agent's scrubbed, plain-text rationale. Untrusted: return it to other agents marked as such.</summary>
    public string? Rationale { get; init; }
}

/// <summary>The <see cref="FailureClassification.Provider"/> values of stored results.</summary>
public static class FailureClassificationProviders
{
    /// <summary>An agent recorded the result over MCP (Spec 044).</summary>
    public const string Agent = "Agent";

    /// <summary>The built-in provider made the result before it was removed in v5.0.0.</summary>
    public const string TypeSafe = "TypeSafe";
}

/// <summary>Outcome of an atomic operation reservation.</summary>
public sealed record ClassificationReservation(
    string OperationId,
    string FailureMessageId,
    int Revision,
    bool IsOwner,
    FailureClassification? CachedResult = null,
    string? ErrorCode = null);

/// <summary>Coordinates retained before a result exists.</summary>
public sealed record ClassificationScope(string FailureMessageId, string EventId, string EndpointId, string? SessionId);

/// <summary>Durable state used to coordinate one idempotent classification request.</summary>
public sealed record ClassificationOperation(
    string OperationId,
    string FailureMessageId,
    string IdempotencyKey,
    int Revision,
    bool Force,
    string Status,
    DateTimeOffset ExpiresAtUtc,
    FailureClassification? Result = null,
    string? ErrorCode = null);

/// <summary>Store contract shared by SQL, Cosmos, and test implementations.</summary>
public interface IFailureClassificationStore
{
    Task<FailureClassification?> GetLatestAsync(string failureMessageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string failureMessageId, CancellationToken cancellationToken = default);
    Task<ClassificationOperation?> GetLatestOperationAsync(string failureMessageId, CancellationToken cancellationToken = default);
    Task<ClassificationReservation> ReserveAsync(string failureMessageId, string idempotencyKey, bool force, DateTimeOffset now, ClassificationScope? scope = null, CancellationToken cancellationToken = default);
    Task CompleteAsync(string operationId, FailureClassification result, CancellationToken cancellationToken = default);
    Task FailAsync(ClassificationReservation reservation, string errorCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// The saved classification state of one failure as read in bulk: the latest completed result,
/// the latest operation's state (an expired active operation reads as failed with
/// <c>AnalysisOutcomeUnknown</c>), and whether the failure was tombstoned.
/// </summary>
/// <param name="FailureMessageId">The failed attempt's message id.</param>
/// <param name="Latest">The latest completed result, or null when none exists or the failure was tombstoned.</param>
/// <param name="LatestOperationStatus">The latest operation's status (Active, Completed or Failed), or null when there is none.</param>
/// <param name="LatestOperationErrorCode">The latest operation's error code, when it failed.</param>
/// <param name="Deleted">True when retention tombstoned the failure; no classification data may be exposed.</param>
public sealed record ClassificationSnapshot(
    string FailureMessageId,
    FailureClassification? Latest,
    string? LatestOperationStatus,
    string? LatestOperationErrorCode,
    bool Deleted);

/// <summary>
/// Optional bulk-read capability of a classification store (spec 043). Implementations read many
/// failures per call without one round trip per failure, and throw when storage is unavailable,
/// including a missing table or container; an id with no stored state is simply absent from the
/// result.
/// </summary>
public interface IFailureClassificationBulkReader
{
    /// <summary>Reads the saved state of each listed failure. Absent ids are omitted.</summary>
    Task<IReadOnlyDictionary<string, ClassificationSnapshot>> ReadLatestAsync(
        IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a bulk classification read.</summary>
public enum ClassificationBulkReadStatus
{
    /// <summary>Storage answered for every id; absent ids have no saved classification.</summary>
    Available,

    /// <summary>Storage failed or is missing; nothing may be inferred about absence.</summary>
    Unavailable,

    /// <summary>The registered store cannot read in bulk.</summary>
    Unsupported,
}

/// <summary>Result of <see cref="FailureClassificationReader.ReadLatestInBulkAsync"/>.</summary>
/// <param name="Status">Whether the snapshots are complete.</param>
/// <param name="Snapshots">Saved state by failure message id; empty unless <paramref name="Status"/> is Available.</param>
public sealed record ClassificationBulkReadResult(
    ClassificationBulkReadStatus Status,
    IReadOnlyDictionary<string, ClassificationSnapshot> Snapshots);

/// <summary>Limits for one bulk classification read.</summary>
public sealed record ClassificationBulkReadOptions
{
    /// <summary>Creates options; both limits must be positive.</summary>
    public ClassificationBulkReadOptions(int BatchSize = 500, int MaxConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
        this.BatchSize = BatchSize;
        this.MaxConcurrency = MaxConcurrency;
    }

    /// <summary>Failure ids per store call.</summary>
    public int BatchSize { get; }

    /// <summary>Store calls in flight at once.</summary>
    public int MaxConcurrency { get; }
}

/// <summary>Durable cleanup of classifications whose source event has been deleted.</summary>
public interface IClassificationRetentionStore
{
    IAsyncEnumerable<ClassificationScope> GetRetentionCandidatesAsync(CancellationToken cancellationToken = default);
    Task DeleteFailureAsync(string failureMessageId, CancellationToken cancellationToken = default);
}

/// <summary>Host-provided authorization and audit boundary.</summary>
public interface IIntegrationIntelligenceHost
{
    Task<bool> EndpointExistsAsync(string endpointId, CancellationToken cancellationToken = default);
    Task<bool> HasReaderAsync(string endpointId, CancellationToken cancellationToken = default);
    Task<bool> HasContributorAsync(string endpointId, CancellationToken cancellationToken = default);
    string? CurrentActor { get; }
    Task AuditAsync(MessageAuditType type, string? eventId, string? endpointId, string data, bool accessDenied, CancellationToken cancellationToken = default);
}

/// <summary>Resolved host storage settings supplied by the WebApp adapter.</summary>
public interface IIntegrationIntelligenceStorageSettings
{
    string Provider { get; }
    string? SqlConnectionString { get; }
    string SqlSchema { get; }
    string? CosmosDatabaseName { get; }
    CosmosClient? CosmosClient { get; }
}
