using NimBus.Extensions.IntegrationIntelligence;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>Reads, and lets agents record, failure classifications for the operator tools.</summary>
public interface IOperatorClassificationSource
{
    /// <summary>Whether saved failure classifications can be read in this deployment (recordable or read-only mode).</summary>
    bool IsAvailable { get; }

    /// <summary>Whether agents can read evidence and record classifications (Spec 044 §6.7: recordable mode).</summary>
    bool CanRecord => false;

    /// <summary>
    /// The latest classification of one failed attempt, or null when there is none or the
    /// caller cannot read it. The classification service applies its own endpoint Reader check.
    /// </summary>
    Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken);

    /// <summary>The evidence an agent classifies, after the recorder's Reader and eligibility checks.</summary>
    Task<FailureEvidence> GetEvidenceAsync(string endpointId, string eventId, string messageId, CancellationToken cancellationToken)
        => throw OperatorToolErrors.FeatureUnavailable(OperatorClassificationSource.NotRecordable);

    /// <summary>Records an agent's classification through the recorder, which checks Contributor, validates, scrubs and audits.</summary>
    Task<(FailureClassification Result, bool Existing)> RecordAsync(FailureClassificationRecording request, CancellationToken cancellationToken)
        => throw OperatorToolErrors.FeatureUnavailable(OperatorClassificationSource.NotRecordable);

    /// <summary>
    /// The saved state of many failed attempts in one bounded read. The caller must already hold
    /// Reader on every listed attempt's endpoint.
    /// </summary>
    Task<ClassificationBulkReadResult> ReadLatestInBulkAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken)
        => Task.FromResult(new ClassificationBulkReadResult(ClassificationBulkReadStatus.Unsupported,
            new Dictionary<string, ClassificationSnapshot>(StringComparer.Ordinal)));
}

/// <summary>
/// Reads through <see cref="FailureClassificationReader"/>, registered whenever classification
/// storage is readable (spec 043 read-only mode included), and records through
/// <see cref="FailureClassificationRecorder"/>, registered in recordable mode (Spec 044).
/// </summary>
public sealed class OperatorClassificationSource : IOperatorClassificationSource
{
    internal const string NotRecordable =
        "Agents can't record failure classifications in this deployment: classification is off, or its evidence or threshold settings are invalid.";

    private readonly FailureClassificationReader? _reader;
    private readonly FailureClassificationRecorder? _recorder;

    /// <summary>Creates the source; a sequence is empty when its mode is not active.</summary>
    public OperatorClassificationSource(IEnumerable<FailureClassificationReader> readers, IEnumerable<FailureClassificationRecorder> recorders)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(recorders);
        _reader = readers.FirstOrDefault();
        _recorder = recorders.FirstOrDefault();
    }

    /// <inheritdoc />
    public bool IsAvailable => _reader is not null;

    /// <inheritdoc />
    public bool CanRecord => _recorder is not null;

    /// <inheritdoc />
    public async Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
    {
        if (_reader is null)
            return null;

        try
        {
            return await _reader.GetLatestAsync(eventId, messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception) when (exception.StatusCode is 403 or 404)
        {
            // Not readable or gone: indistinguishable from "not classified" by design.
            return null;
        }
        catch (ClassificationServiceException exception)
        {
            throw OperatorToolErrors.SourceUnavailable($"Failure classification is unavailable ({exception.Code}).");
        }
    }

    /// <inheritdoc />
    public async Task<FailureEvidence> GetEvidenceAsync(string endpointId, string eventId, string messageId, CancellationToken cancellationToken)
    {
        if (_recorder is null)
            throw OperatorToolErrors.FeatureUnavailable(NotRecordable);

        try
        {
            return await _recorder.GetEvidenceAsync(endpointId, eventId, messageId, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception)
        {
            throw Map(exception, endpointId, eventId, "Reading the evidence requires the Reader role on the endpoint.");
        }
    }

    /// <inheritdoc />
    public async Task<(FailureClassification Result, bool Existing)> RecordAsync(FailureClassificationRecording request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_recorder is null)
            throw OperatorToolErrors.FeatureUnavailable(NotRecordable);

        try
        {
            return await _recorder.RecordAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception)
        {
            throw Map(exception, request.EndpointId, request.EventId, "Recording a classification requires the Contributor role on the endpoint.");
        }
    }

    /// <inheritdoc />
    public async Task<ClassificationBulkReadResult> ReadLatestInBulkAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken)
    {
        if (_reader is null)
            return new ClassificationBulkReadResult(ClassificationBulkReadStatus.Unsupported, new Dictionary<string, ClassificationSnapshot>(StringComparer.Ordinal));

        return await _reader.ReadLatestInBulkAsync(failureMessageIds, new ClassificationBulkReadOptions(), cancellationToken).ConfigureAwait(false);
    }

    private static ModelContextProtocol.McpException Map(ClassificationServiceException exception, string endpointId, string eventId, string forbidden)
        => exception.Code switch
        {
            "FailureNotFound" or "FailureDeleted" => OperatorToolErrors.MessageNotFound(endpointId, eventId),
            "Forbidden" => OperatorToolErrors.PermissionDenied(forbidden),
            "EndpointNotAllowed" => OperatorToolErrors.PermissionDenied(
                $"Failure classification is not enabled for endpoint '{endpointId}' in this deployment."),
            "FailureNotEligible" => OperatorToolErrors.ActionNotAllowed(
                "Only a Failed or DeadLettered message's failure can be classified. The message may have been resolved since you read it."),
            "AnalysisInProgress" => OperatorToolErrors.ActionNotAllowed(
                "Another classification of this failure is being stored. Read it with nimbus_get_classification shortly."),
            "AnalysisOutcomeUnknown" when exception.StatusCode == 409 => OperatorToolErrors.ActionNotAllowed(
                "The latest classification of this failure has an unknown outcome. Pass supersede=true to record yours as a new revision."),
            "AnalysisOutcomeUnknown" => OperatorToolErrors.OutcomeUnknown(
                "The classification may or may not have been stored. Read it with nimbus_get_classification."),
            "InvalidRecording" or "QuestionSetOutdated" => OperatorToolErrors.InvalidArgument(exception.Message),
            "InvalidIdempotencyKey" or "IdempotencyConflict" => OperatorToolErrors.InvalidArgument(
                "idempotencyKey must be a new GUID, or the one you used before with the same supersede value."),
            "EvidenceTooLarge" => OperatorToolErrors.SourceUnavailable("The evidence for this failure exceeds the configured size limit."),
            _ => OperatorToolErrors.SourceUnavailable($"Failure classification is unavailable ({exception.Code})."),
        };
}
