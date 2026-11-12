using System.Diagnostics;
using System.Text.Json;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>
/// An agent's answers to the current question set for one failed attempt (Spec 044 §6.4). Every
/// value is untrusted input: the recorder validates, bounds and scrubs it before anything is stored.
/// </summary>
public sealed record FailureClassificationRecording
{
    /// <summary>The endpoint the failed attempt belongs to.</summary>
    public required string EndpointId { get; init; }

    /// <summary>The message's event id.</summary>
    public required string EventId { get; init; }

    /// <summary>The failed attempt, as the evidence returned it. Required, so an answer never attaches to a newer attempt.</summary>
    public required string MessageId { get; init; }

    /// <summary>A GUID. Repeating it returns the same stored result.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Must equal <see cref="FailureClassificationQuestionSet.Version"/>.</summary>
    public required int QuestionSetVersion { get; init; }

    /// <summary>One of <see cref="FailureClassificationQuestionSet.Categories"/>.</summary>
    public required string Category { get; init; }

    /// <summary>Confidence in <see cref="Category"/>, 0 to 1.</summary>
    public required double CategoryConfidence { get; init; }

    /// <summary>Answer to <c>retry_likely_to_succeed_unchanged</c>, 0 to 1.</summary>
    public required double RetryLikelihood { get; init; }

    /// <summary>Answer to <c>change_required_before_success</c>, 0 to 1.</summary>
    public required double ChangeRequiredLikelihood { get; init; }

    /// <summary>Answer to <c>external_dependency_involved</c>, 0 to 1.</summary>
    public required double ExternalDependencyLikelihood { get; init; }

    /// <summary>One of <see cref="FailureClassificationQuestionSet.FaultOrigins"/>.</summary>
    public required string FaultOrigin { get; init; }

    /// <summary>Confidence in <see cref="FaultOrigin"/>, 0 to 1.</summary>
    public required double FaultOriginConfidence { get; init; }

    /// <summary>What the agent says it runs on, at most 100 characters. Stored as stated, never verified.</summary>
    public string? Model { get; init; }

    /// <summary>Plain text, at most 1,000 characters. Scrubbed before it is stored.</summary>
    public string? Rationale { get; init; }

    /// <summary>When false and a result exists, that result is returned and no revision is written.</summary>
    public bool Supersede { get; init; }

    /// <summary>The calling client application (<c>azp</c>, else <c>appid</c>), supplied by the host.</summary>
    public string? ClientId { get; init; }
}

/// <summary>The evidence an agent classifies, and the latest saved classification of the same attempt.</summary>
/// <param name="Input">The bounded, redacted evidence the provider would have received, with the sending endpoints.</param>
/// <param name="Latest">The latest saved classification, or null.</param>
public sealed record FailureEvidence(FailureClassificationInput Input, FailureClassification? Latest);

/// <summary>
/// Records classifications that agents make over MCP (Spec 044 §6.4). It checks Contributor and
/// eligibility, scrubs the rationale like evidence text, composes guidance with the saved
/// thresholds, and stores one revision through the same idempotent operation path as the
/// deprecated provider analysis. It calls no model.
/// </summary>
public sealed class FailureClassificationRecorder
{
    /// <summary>Longest declared model name.</summary>
    public const int MaxModelLength = 100;

    /// <summary>Longest rationale.</summary>
    public const int MaxRationaleLength = 1000;

    /// <summary>The model stored when the agent declares none.</summary>
    public const string UnspecifiedModel = "unspecified";

    private readonly FailureClassificationOptions _options;
    private readonly IIntegrationIntelligenceHost _host;
    private readonly IFailureClassificationStore _store;
    private readonly FailureEvidenceBuilder _evidence;
    private readonly IMessageTrackingStore _messages;
    private readonly FailureClassificationReader _reader;
    private readonly TimeProvider _clock;

    /// <summary>Creates the recorder.</summary>
    public FailureClassificationRecorder(
        FailureClassificationOptions options,
        IIntegrationIntelligenceHost host,
        IFailureClassificationStore store,
        FailureEvidenceBuilder evidence,
        IMessageTrackingStore messages,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(messages);
        _options = options;
        _host = host;
        _store = store;
        _evidence = evidence;
        _messages = messages;
        _clock = clock ?? TimeProvider.System;
        _reader = new FailureClassificationReader(host, store, messages);
    }

    /// <summary>
    /// Builds the evidence for one failed attempt for a caller with Reader on its endpoint, with
    /// the latest saved classification. Answers 404 when the attempt is missing or on another
    /// endpoint, 403 without Reader or outside the allow-list, and 409 when it is no longer
    /// Failed or DeadLettered.
    /// </summary>
    public async Task<FailureEvidence> GetEvidenceAsync(string endpointId, string eventId, string messageId, CancellationToken cancellationToken)
    {
        var message = await _reader.LoadAuthorizedMessageAsync(eventId, messageId, requireContributor: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        RequireEndpoint(message, endpointId);
        var input = await _evidence.BuildAsync(eventId, messageId, cancellationToken).ConfigureAwait(false)
            ?? throw new ClassificationServiceException("FailureNotEligible", 409);
        FailureClassification? latest;
        try
        {
            latest = await _store.GetLatestAsync(input.MessageId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not ClassificationServiceException)
        {
            throw new ClassificationServiceException("ClassificationUnavailable", 503, inner: exception);
        }
        return new FailureEvidence(input, latest);
    }

    /// <summary>
    /// Records one classification. Returns the stored result and whether it already existed (a
    /// repeated idempotency key, or <see cref="FailureClassificationRecording.Supersede"/> false
    /// on a classified failure). Refusals throw <see cref="ClassificationServiceException"/> and
    /// store nothing. Every call is audited as <c>FailureClassified</c>.
    /// </summary>
    public async Task<(FailureClassification Result, bool Existing)> RecordAsync(FailureClassificationRecording request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        using var activity = NimBusIntelligenceTelemetry.ActivitySource.StartActivity("NimBus.Intelligence.FailureClassification.Record");
        FailureClassification? result = null;
        var existing = false;
        var outcome = "rejected";
        var denied = false;
        var endpointId = request.EndpointId ?? string.Empty;
        var eventTypeId = string.Empty;
        try
        {
            var (key, model, rationale) = Validate(request);
            var message = await _reader.LoadAuthorizedMessageAsync(request.EventId, request.MessageId, requireContributor: true, loaded =>
            {
                endpointId = loaded.EndpointId;
                eventTypeId = loaded.EventTypeId ?? string.Empty;
            }, cancellationToken).ConfigureAwait(false);
            RequireEndpoint(message, request.EndpointId);

            // Eligibility is checked again, on the evidence NimBus would build now.
            var input = await _evidence.BuildAsync(request.EventId, request.MessageId, cancellationToken).ConfigureAwait(false)
                ?? throw new ClassificationServiceException("FailureNotEligible", 409);
            if (rationale is not null)
            {
                rationale = _evidence.ScrubAgentText(message, rationale, MaxRationaleLength)
                    ?? throw Invalid("rationale could not be checked for sensitive values; leave it out or rephrase it.");
            }

            (result, existing) = await StoreAsync(request, input, key, model, rationale, cancellationToken).ConfigureAwait(false);
            outcome = existing ? "cached" : "ok";
            return (result, existing);
        }
        catch (ClassificationServiceException exception)
        {
            outcome = exception.TelemetryOutcome ?? exception.Code switch
            {
                "AnalysisInProgress" => "busy",
                "AnalysisOutcomeUnknown" => "unknown",
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
            NimBusIntelligenceTelemetry.RecordRequest(started, activity, outcome, FailureClassificationProviders.Agent,
                result?.Model, result?.Category, endpointId, eventTypeId);
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await AuditAsync(request, endpointId, result, existing, outcome, denied, auditTimeout.Token).ConfigureAwait(false);
        }
    }

    private async Task<(FailureClassification Result, bool Existing)> StoreAsync(
        FailureClassificationRecording request, FailureClassificationInput input, string key, string model, string? rationale,
        CancellationToken cancellationToken)
    {
        ClassificationReservation reservation;
        try
        {
            reservation = await _store.ReserveAsync(input.MessageId, key, request.Supersede, _clock.GetUtcNow(),
                new ClassificationScope(input.MessageId, input.EventId, input.EndpointId, input.SessionId), cancellationToken).ConfigureAwait(false);
        }
        catch (ClassificationServiceException exception) when (exception.StatusCode >= 500)
        {
            throw new ClassificationServiceException(exception.Code, exception.StatusCode, inner: exception, telemetryOutcome: "store_error");
        }
        catch (ClassificationServiceException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new ClassificationServiceException("ClassificationUnavailable", 503, inner: exception, telemetryOutcome: "store_error");
        }

        if (reservation.CachedResult is not null)
            return (reservation.CachedResult, true);
        if (!reservation.IsOwner)
        {
            var code = reservation.ErrorCode ?? "AnalysisInProgress";
            throw new ClassificationServiceException(code, code is "AnalysisInProgress" or "AnalysisOutcomeUnknown" ? 409 : 503);
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
            Provider = FailureClassificationProviders.Agent,
            Model = model,
            QuestionSetVersion = FailureClassificationQuestionSet.Version,
            Category = request.Category,
            CategoryConfidence = request.CategoryConfidence,
            CategoryProbabilities = new Dictionary<string, double>(StringComparer.Ordinal) { [request.Category] = request.CategoryConfidence },
            RetryLikelihood = request.RetryLikelihood,
            ChangeRequiredLikelihood = request.ChangeRequiredLikelihood,
            ExternalDependencyLikelihood = request.ExternalDependencyLikelihood,
            Guidance = FailureGuidanceRules.Compose(request.Category, request.CategoryConfidence, request.RetryLikelihood, request.ChangeRequiredLikelihood, _options),
            InputTokens = null,
            OutputTokens = null,
            EventPayloadIncluded = input.EventPayloadJson is not null,
            RequestedBy = _host.CurrentActor ?? "unknown",
            CreatedAtUtc = _clock.GetUtcNow(),
            FaultOrigin = request.FaultOrigin,
            FaultOriginConfidence = request.FaultOriginConfidence,
            ClientId = string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim(),
            Rationale = rationale,
        };

        if (await _messages.GetEvent(input.EndpointId, input.EventId).ConfigureAwait(false) is null)
        {
            await MarkFailedAsync(reservation, "FailureNotFound").ConfigureAwait(false);
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
            // A lost acknowledgement may have committed: read it back before calling it unknown.
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var saved = await _store.GetLatestOperationAsync(input.MessageId, readTimeout.Token).ConfigureAwait(false);
                if (saved?.OperationId == reservation.OperationId && saved.Result is not null) return (saved.Result, false);
            }
            catch { /* Leave the reservation unknown while storage stays unavailable. */ }
            await MarkFailedAsync(reservation, "AnalysisOutcomeUnknown").ConfigureAwait(false);
            throw new ClassificationServiceException("AnalysisOutcomeUnknown", 503, inner: exception, telemetryOutcome: "unknown");
        }
        return (result, false);
    }

    /// <summary>Validates every answer before anything is read or stored; returns the normalized key, model and rationale.</summary>
    private static (string Key, string Model, string? Rationale) Validate(FailureClassificationRecording request)
    {
        if (string.IsNullOrWhiteSpace(request.EndpointId) || string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.MessageId))
            throw Invalid("endpointId, eventId and messageId are required.");
        if (!Guid.TryParse(request.IdempotencyKey, out var key))
            throw new ClassificationServiceException("InvalidIdempotencyKey", 400);
        if (request.QuestionSetVersion != FailureClassificationQuestionSet.Version)
            throw new ClassificationServiceException("QuestionSetOutdated", 400,
                $"questionSetVersion must be {FailureClassificationQuestionSet.Version}; read the evidence again.");
        if (request.Category is null || !FailureClassificationQuestionSet.Categories.Contains(request.Category))
            throw Invalid("category is not one of the question set's categories.");
        if (request.FaultOrigin is null || !FailureClassificationQuestionSet.FaultOrigins.Contains(request.FaultOrigin))
            throw Invalid("faultOrigin is not one of the question set's origins.");
        foreach (var (name, value) in new[]
        {
            ("categoryConfidence", request.CategoryConfidence),
            ("retryLikelihood", request.RetryLikelihood),
            ("changeRequiredLikelihood", request.ChangeRequiredLikelihood),
            ("externalDependencyLikelihood", request.ExternalDependencyLikelihood),
            ("faultOriginConfidence", request.FaultOriginConfidence),
        })
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
                throw Invalid($"{name} must be a number from 0 to 1.");
        }

        var model = string.IsNullOrWhiteSpace(request.Model) ? UnspecifiedModel : request.Model.Trim();
        if (model.Length > MaxModelLength || model.Any(char.IsControl))
            throw Invalid($"model must be at most {MaxModelLength} characters without control characters.");

        string? rationale = null;
        if (!string.IsNullOrWhiteSpace(request.Rationale))
        {
            rationale = request.Rationale.Trim();
            if (rationale.Length > MaxRationaleLength)
                throw Invalid($"rationale must be at most {MaxRationaleLength} characters.");
            if (rationale.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
                throw Invalid("rationale must be plain text without control characters.");
            if (IntelligenceDataRedactor.ScrubText(rationale, MaxRationaleLength) is null)
                throw Invalid("rationale must not contain a stack trace.");
        }

        return (key.ToString("D"), model, rationale);
    }

    private void RequireEndpoint(MessageEntity message, string? endpointId)
    {
        if (!string.Equals(message.EndpointId, endpointId?.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ClassificationServiceException("FailureNotFound", 404);
        if (_options.AllowedEndpoints.Length > 0 && !_options.AllowedEndpoints.Contains(message.EndpointId, StringComparer.OrdinalIgnoreCase))
            throw new ClassificationServiceException("EndpointNotAllowed", 403);
    }

    private static ClassificationServiceException Invalid(string detail) => new("InvalidRecording", 400, detail);

    private async Task MarkFailedAsync(ClassificationReservation reservation, string code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await _store.FailAsync(reservation, code, timeout.Token).ConfigureAwait(false); }
        catch { /* Expiry reads as an unknown outcome. */ }
    }

    private async Task AuditAsync(FailureClassificationRecording request, string endpointId, FailureClassification? result, bool existing,
        string outcome, bool accessDenied, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            messageId = request.MessageId,
            provider = FailureClassificationProviders.Agent,
            model = result?.Model,
            clientId = string.IsNullOrWhiteSpace(request.ClientId) ? null : request.ClientId.Trim(),
            questionSetVersion = result?.QuestionSetVersion,
            category = result?.Category,
            categoryConfidence = result?.CategoryConfidence,
            faultOrigin = result?.FaultOrigin,
            guidance = result?.Guidance.ToString(),
            eventPayloadIncluded = result?.EventPayloadIncluded,
            revision = result?.Revision,
            cached = existing,
            outcome = outcome switch
            {
                "ok" => "Completed",
                "cached" => "Cached",
                "unknown" => "UnknownOutcome",
                "store_error" => "StoreError",
                "busy" => "Busy",
                _ => "Rejected",
            },
        });
        try
        {
            await _host.AuditAsync(MessageAuditType.FailureClassified, request.EventId, endpointId, data, accessDenied, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit is best-effort here, as for provider analysis: a recording changes no message.
        }
    }
}
