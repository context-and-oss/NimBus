// The provider analysis path is deprecated as a whole and stays working until v5.0.0 (Spec 044 §6.10).
#pragma warning disable CS0618
using System.Diagnostics;
using System.Text.Json;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Providers;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Coordinates authorization, evidence, provider calls and durable completion.</summary>
public sealed class FailureClassificationService
{
    private readonly FailureClassificationOptions _options;
    private readonly IIntegrationIntelligenceHost _host;
    private readonly IFailureClassificationStore _store;
    private readonly FailureEvidenceBuilder _evidence;
    private readonly IFailureIntelligenceProvider _provider;
    private readonly TimeProvider _clock;
    private readonly IMessageTrackingStore _messages;
    private readonly FailureClassificationReader _reader;

    /// <summary>Creates the service.</summary>
    public FailureClassificationService(
        FailureClassificationOptions options,
        IIntegrationIntelligenceHost host,
        IFailureClassificationStore store,
        FailureEvidenceBuilder evidence,
        IFailureIntelligenceProvider provider,
        IMessageTrackingStore messages,
        TimeProvider? clock = null)
    {
        _options = options;
        _host = host;
        _store = store;
        _evidence = evidence;
        _provider = provider;
        _messages = messages;
        _clock = clock ?? TimeProvider.System;
        _reader = new FailureClassificationReader(host, store, messages);
    }

    /// <summary>Loads the latest authorized classification. Same as <see cref="FailureClassificationReader.GetLatestAsync"/>.</summary>
    public Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
        => _reader.GetLatestAsync(eventId, messageId, cancellationToken);

    /// <summary>Loads all authorized revisions. Same as <see cref="FailureClassificationReader.GetHistoryAsync"/>.</summary>
    public Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string eventId, string messageId, CancellationToken cancellationToken)
        => _reader.GetHistoryAsync(eventId, messageId, cancellationToken);

    /// <summary>Runs one idempotent analysis request with the deprecated provider. Removed in v5.0.0; use <see cref="FailureClassificationRecorder.RecordAsync"/>.</summary>
    [Obsolete(IntegrationIntelligenceDeprecation.Provider)]
    public async Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(
        string eventId, string messageId, string idempotencyKey, bool force, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = NimBusIntelligenceTelemetry.ActivitySource.StartActivity("NimBus.Intelligence.FailureClassification");
        FailureClassification? result = null;
        var cached = false;
        var outcome = "rejected";
        var denied = false;
        var endpointId = string.Empty;
        var eventTypeId = string.Empty;
        try
        {
            await LoadAuthorizedMessageAsync(eventId, messageId, true, input =>
            {
                endpointId = input.EndpointId;
                eventTypeId = input.EventTypeId ?? string.Empty;
            }, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(idempotencyKey, out var key)) throw new ClassificationServiceException("InvalidIdempotencyKey", 400);
            idempotencyKey = key.ToString("D");
            (result, cached) = await AnalyzeCoreAsync(eventId, messageId, idempotencyKey, force, cancellationToken).ConfigureAwait(false);
            outcome = cached ? "cached" : "ok";
            return (result, cached);
        }
        catch (ClassificationServiceException exception)
        {
            outcome = exception.TelemetryOutcome ?? exception.Code switch
            {
                "AnalysisInProgress" => "busy",
                "AnalysisOutcomeUnknown" => "unknown",
                "ProviderUnavailable" => "provider_error",
                _ when exception.StatusCode >= 500 => "store_error",
                _ => "rejected",
            };
            denied = exception.StatusCode == 403;
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            outcome = "store_error";
            throw new ClassificationServiceException("ClassificationUnavailable", 503, inner: exception, telemetryOutcome: "store_error");
        }
        finally
        {
            NimBusIntelligenceTelemetry.RecordRequest(
                started,
                activity,
                outcome,
                result?.Provider ?? _provider.Name,
                result?.Model ?? _options.Model,
                result?.Category,
                endpointId,
                eventTypeId,
                result?.InputTokens);
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AuditAsync(eventId, endpointId, messageId, result, cached, AuditOutcome(outcome), denied, auditTimeout.Token).ConfigureAwait(false);
        }
    }

    private static string AuditOutcome(string telemetryOutcome)
        => telemetryOutcome switch
        {
            "ok" => "Completed",
            "cached" => "Cached",
            "provider_error" => "ProviderError",
            "unknown" => "UnknownOutcome",
            "store_error" => "StoreError",
            "busy" => "Busy",
            "rejected" => "Rejected",
            _ => telemetryOutcome,
        };

    private async Task<(FailureClassification Result, bool Cached)> AnalyzeCoreAsync(
        string eventId,
        string messageId,
        string idempotencyKey,
        bool force,
        CancellationToken cancellationToken)
    {
        var input = await _evidence.BuildAsync(eventId, messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new ClassificationServiceException("FailureNotEligible", 409);
        if (_options.AllowedEndpoints.Length > 0 && !_options.AllowedEndpoints.Contains(input.EndpointId, StringComparer.OrdinalIgnoreCase))
        {
            throw new ClassificationServiceException("EndpointNotAllowed", 403);
        }

        ClassificationReservation reservation;
        try
        {
            reservation = await _store.ReserveAsync(input.MessageId, idempotencyKey, force, _clock.GetUtcNow(),
                new ClassificationScope(input.MessageId, eventId, input.EndpointId, input.SessionId), cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception) when (exception.StatusCode >= 500)
        {
            throw new ClassificationServiceException(exception.Code, exception.StatusCode, inner: exception, telemetryOutcome: "store_error");
        }
        catch (Exception exception)
        {
            throw new ClassificationServiceException("ClassificationUnavailable", 503, inner: exception, telemetryOutcome: "store_error");
        }
        if (reservation.CachedResult is not null)
        {
            return (reservation.CachedResult, true);
        }

        if (!reservation.IsOwner)
        {
            var code = reservation.ErrorCode ?? "AnalysisInProgress";
            throw new ClassificationServiceException(code, code is "AnalysisInProgress" or "AnalysisOutcomeUnknown" ? 409 : 503);
        }

        FailureIntelligenceProviderResult providerResult;
        try
        {
            providerResult = await _provider.ClassifyAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (IntelligenceProviderException exception)
        {
            var errorCode = exception.OutcomeUnknown ? "AnalysisOutcomeUnknown" : "ProviderUnavailable";
            await MarkFailedAsync(reservation, errorCode).ConfigureAwait(false);
            throw new ClassificationServiceException(errorCode, 503, inner: exception,
                telemetryOutcome: exception.OutcomeUnknown ? "unknown" : "provider_error");
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(reservation, "AnalysisOutcomeUnknown").ConfigureAwait(false);
            throw new ClassificationServiceException("AnalysisOutcomeUnknown", 503, inner: exception, telemetryOutcome: "unknown");
        }

        var result = new FailureClassification
        {
            Id = $"{input.MessageId}:{reservation.Revision}",
            FailureMessageId = input.MessageId,
            Revision = reservation.Revision,
            EventId = input.EventId,
            EventTypeId = input.EventTypeId,
            EndpointId = input.EndpointId,
            SessionId = input.SessionId,
            Provider = _provider.Name,
            Model = providerResult.Model,
            QuestionSetVersion = FailureClassificationQuestionSet.ProviderVersion,
            Category = providerResult.Category,
            CategoryConfidence = providerResult.CategoryConfidence,
            CategoryProbabilities = providerResult.CategoryProbabilities,
            RetryLikelihood = providerResult.RetryLikelihood,
            ChangeRequiredLikelihood = providerResult.ChangeRequiredLikelihood,
            ExternalDependencyLikelihood = providerResult.ExternalDependencyLikelihood,
            Guidance = FailureGuidanceRules.Compose(providerResult, _options),
            InputTokens = providerResult.InputTokens,
            OutputTokens = providerResult.OutputTokens,
            EventPayloadIncluded = input.EventPayloadJson is not null,
            RequestedBy = _host.CurrentActor ?? "unknown",
            CreatedAtUtc = _clock.GetUtcNow(),
        };

        if (await _messages.GetEvent(input.EndpointId, eventId).ConfigureAwait(false) is null)
        {
            if (_store is IClassificationRetentionStore retention)
                await retention.DeleteFailureAsync(messageId, cancellationToken).ConfigureAwait(false);
            throw new ClassificationServiceException("FailureNotFound", 404);
        }
        try
        {
            await _store.CompleteAsync(reservation.OperationId, result, cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception)
        {
            throw new ClassificationServiceException(exception.Code, exception.StatusCode, inner: exception, telemetryOutcome: "store_error");
        }
        catch (Exception exception)
        {
            // A lost acknowledgement must not trigger another paid call or overwrite completion.
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var saved = await _store.GetLatestOperationAsync(messageId, readTimeout.Token).ConfigureAwait(false);
                if (saved?.OperationId == reservation.OperationId && saved.Result is not null) return (saved.Result, false);
            }
            catch { /* Leave the durable reservation unknown if storage remains unavailable. */ }
            await MarkFailedAsync(reservation, "AnalysisOutcomeUnknown").ConfigureAwait(false);
            throw new ClassificationServiceException("AnalysisOutcomeUnknown", 503, inner: exception, telemetryOutcome: "unknown");
        }
        return (result, false);
    }

    private async Task MarkFailedAsync(ClassificationReservation reservation, string code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _store.FailAsync(reservation, code, timeout.Token).ConfigureAwait(false); }
        catch { /* Expiry is an unknown outcome, never permission to replay. */ }
    }

    private Task<MessageEntity> LoadAuthorizedMessageAsync(string eventId, string messageId, bool requireContributor, Action<MessageEntity>? loadedMessage = null, CancellationToken cancellationToken = default)
        => _reader.LoadAuthorizedMessageAsync(eventId, messageId, requireContributor, loadedMessage, cancellationToken);

    private async Task AuditAsync(string eventId, string endpointId, string messageId, FailureClassification? result, bool cached, string outcome, bool accessDenied, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            messageId,
            provider = result?.Provider,
            model = result?.Model,
            questionSetVersion = result?.QuestionSetVersion,
            category = result?.Category,
            categoryConfidence = result?.CategoryConfidence,
            guidance = result?.Guidance.ToString(),
            inputTokens = result?.InputTokens,
            eventPayloadIncluded = result?.EventPayloadIncluded,
            revision = result?.Revision,
            cached,
            outcome,
        });
        try
        {
            await _host.AuditAsync(MessageStore.MessageAuditType.FailureClassified, eventId, endpointId, data, accessDenied, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit is deliberately best-effort and must never replay a paid request.
        }
    }
}

/// <summary>Machine-readable service failure returned by the API layer.</summary>
public sealed class ClassificationServiceException : Exception
{
    /// <summary>Creates a service exception.</summary>
    public ClassificationServiceException(string code, int statusCode, string? message = null, Exception? inner = null, string? telemetryOutcome = null)
        : base(message ?? code, inner)
    {
        Code = code;
        StatusCode = statusCode;
        TelemetryOutcome = telemetryOutcome;
    }

    /// <summary>Error code.</summary>
    public string Code { get; }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Optional low-cardinality telemetry outcome for this failure.</summary>
    public string? TelemetryOutcome { get; }
}
