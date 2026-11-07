using NimBus.Extensions.IntegrationIntelligence;

namespace NimBus.WebApp.Mcp.Operations;

/// <summary>Reads stored AI failure classifications for the operator tools.</summary>
public interface IOperatorClassificationSource
{
    /// <summary>Whether saved failure classifications can be read in this deployment (ready or read-only mode).</summary>
    bool IsAvailable { get; }

    /// <summary>Whether a new analysis can be requested: a provider is configured and ready.</summary>
    bool CanAnalyze => IsAvailable;

    /// <summary>
    /// The latest classification of one failed attempt, or null when there is none or the
    /// caller cannot read it. The classification service applies its own endpoint Reader check.
    /// </summary>
    Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Requests or reuses an AI classification of one failed attempt through the classification
    /// service, which checks Contributor, eligibility and the idempotency key and audits the
    /// request. Returns the classification and whether a stored one was reused.
    /// </summary>
    Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(string endpointId, string eventId, string messageId,
        string idempotencyKey, bool force, CancellationToken cancellationToken);
}

/// <summary>
/// Reads through <see cref="FailureClassificationReader"/>, registered whenever classification
/// storage is readable (spec 043 read-only mode included), and analyzes through
/// <see cref="FailureClassificationService"/>, registered only when a provider is ready. Reading
/// never starts an analysis or calls a provider.
/// </summary>
public sealed class OperatorClassificationSource : IOperatorClassificationSource
{
    private readonly FailureClassificationReader? _reader;
    private readonly FailureClassificationService? _service;

    /// <summary>Creates the source; either sequence is empty when its mode is not active.</summary>
    public OperatorClassificationSource(IEnumerable<FailureClassificationReader> readers, IEnumerable<FailureClassificationService> services)
    {
        _reader = readers.FirstOrDefault();
        _service = services.FirstOrDefault();
    }

    /// <inheritdoc />
    public bool IsAvailable => _reader is not null;

    /// <inheritdoc />
    public bool CanAnalyze => _service is not null;

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
    public async Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(string endpointId, string eventId, string messageId,
        string idempotencyKey, bool force, CancellationToken cancellationToken)
    {
        if (_service is null)
            throw OperatorToolErrors.FeatureUnavailable("AI failure classification is not enabled in this deployment.");

        try
        {
            return await _service.AnalyzeAsync(eventId, messageId, idempotencyKey, force, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception)
        {
            throw exception.Code switch
            {
                "FailureNotFound" or "FailureDeleted" => OperatorToolErrors.MessageNotFound(endpointId, eventId),
                "Forbidden" or "EndpointNotAllowed" => OperatorToolErrors.PermissionDenied(
                    "Classifying a failure requires the Contributor role on the endpoint."),
                "FailureNotEligible" => OperatorToolErrors.ActionNotAllowed(
                    "Only a Failed or DeadLettered message's error can be classified."),
                "AnalysisInProgress" => OperatorToolErrors.ActionNotAllowed(
                    "A classification of this failure is already running. Read it with nimbus_get_classification shortly."),
                "InvalidIdempotencyKey" or "IdempotencyConflict" => OperatorToolErrors.InvalidArgument(
                    "idempotencyKey must be a new GUID, or the one you used before with the same force value."),
                "AnalysisOutcomeUnknown" => OperatorToolErrors.OutcomeUnknown("The classification may or may not have been stored."),
                _ => OperatorToolErrors.SourceUnavailable($"Failure classification is unavailable ({exception.Code})."),
            };
        }
    }
}
