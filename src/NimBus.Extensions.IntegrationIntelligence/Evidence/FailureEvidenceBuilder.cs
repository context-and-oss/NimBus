using System.Text.Json;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence.Evidence;

/// <summary>Builds occurrence-specific, bounded evidence from the NimBus message store.</summary>
public sealed class FailureEvidenceBuilder
{
    private readonly IMessageTrackingStore _messages;
    private readonly IEventJsonMasker _masker;
    private readonly IEventJsonRedactor? _redactor;
    private readonly IntelligenceDataRedactor _secrets;
    private readonly FailureClassificationOptions _options;

    /// <summary>Creates an evidence builder.</summary>
    public FailureEvidenceBuilder(
        IMessageTrackingStore messages,
        IEventJsonMasker masker,
        IEventJsonRedactor? redactor,
        IntelligenceDataRedactor secrets,
        FailureClassificationOptions options)
    {
        _messages = messages;
        _masker = masker;
        _redactor = redactor;
        _secrets = secrets;
        _options = options;
    }

    /// <summary>Loads and redacts the requested failure occurrence.</summary>
    public async Task<FailureClassificationInput?> BuildAsync(
        string eventId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var target = await _messages.GetMessage(eventId, messageId).ConfigureAwait(false);
        if (target is null || string.IsNullOrWhiteSpace(target.EndpointId)) return null;

        var current = await _messages.GetEvent(target.EndpointId, eventId).ConfigureAwait(false);
        if (current is null) return null;

        var error = target.MessageContent?.ErrorContent;
        var isFailureMessage = target.MessageType == MessageType.ErrorResponse
            || !string.IsNullOrWhiteSpace(target.DeadLetterErrorDescription)
            || !string.IsNullOrWhiteSpace(target.DeadLetterReason);
        if (!isFailureMessage || (current.ResolutionStatus != ResolutionStatus.Failed && current.ResolutionStatus != ResolutionStatus.DeadLettered))
        {
            return null;
        }

        var input = new FailureClassificationInput
        {
            MessageId = target.MessageId,
            EventId = target.EventId,
            EventTypeId = target.EventTypeId ?? current.EventTypeId ?? string.Empty,
            EndpointId = target.EndpointId,
            SessionId = target.SessionId ?? current.SessionId,
            ResolutionStatus = current.ResolutionStatus.ToString(),
            RetryCount = target.RetryCount ?? current.RetryCount,
            RetryLimit = target.RetryLimit ?? current.RetryLimit,
            Exception = new FailureExceptionInfo(
                error?.ErrorType,
                ScrubSourceText(target, error?.ErrorText),
                CleanSource(error?.ExceptionSource)),
            DeadLetterReason = ScrubSourceText(target, target.DeadLetterReason),
            SourceEndpoint = Blank(current.From) ?? Blank(target.From),
            OriginatingEndpoint = OriginatingEndpoint(current.OriginatingFrom ?? target.OriginatingFrom, current.From ?? target.From),
        };

        var history = _options.Data.IncludeRecentFailureHistory
            ? await _messages.GetEventHistory(eventId).ConfigureAwait(false) : [];
        var priorFailures = history
            .Where(row => string.Equals(row.EndpointId, target.EndpointId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(row.SessionId, target.SessionId, StringComparison.Ordinal)
                && row.MessageId != target.MessageId
                && row.EnqueuedTimeUtc < target.EnqueuedTimeUtc
                && (row.MessageType == MessageType.ErrorResponse
                    || !string.IsNullOrWhiteSpace(row.DeadLetterReason)
                    || !string.IsNullOrWhiteSpace(row.DeadLetterErrorDescription)))
            .OrderBy(row => row.EnqueuedTimeUtc)
            .ThenBy(row => row.MessageId, StringComparer.Ordinal)
            .Select((row, index) => new FailureHistoryItem(
                index + 1,
                row.MessageType.ToString(),
                row.MessageContent?.ErrorContent?.ErrorType,
                ScrubSourceText(row, row.MessageContent?.ErrorContent?.ErrorText, 500),
                row.EnqueuedTimeUtc))
            .TakeLast(Math.Max(0, _options.MaximumHistoryItems))
            .ToArray();

        input = input with { RecentHistory = priorFailures };
        if (_options.IncludeEventPayload && _redactor is not null)
        {
            var payload = target.MessageContent?.EventContent?.EventJson;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                var redacted = _redactor.Redact(input.EventTypeId, payload);
                if (!redacted.StartsWith("[REDACTED:", StringComparison.Ordinal))
                {
                    input = input with { EventPayloadJson = _secrets.ScrubJson(redacted) };
                }
            }
        }

        return Bound(input);
    }

    private string? ScrubSourceText(MessageEntity row, string? text, int? maximumLength = null)
    {
        var payload = row.MessageContent?.EventContent?.EventJson;
        if (string.IsNullOrWhiteSpace(payload) || text is null
            || !_masker.TryCollectSensitiveValues(row.EventTypeId, payload, out var values)) return null;
        var scrubbed = text;
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(value => value.Length))
        {
            scrubbed = scrubbed.Replace(value, EventJsonMasker.DefaultRedactToken, StringComparison.Ordinal);
        }
        if (scrubbed.TrimStart().StartsWith('{') || scrubbed.TrimStart().StartsWith('['))
            scrubbed = _secrets.ScrubJson(scrubbed) ?? string.Empty;
        return IntelligenceDataRedactor.ScrubText(scrubbed, maximumLength ?? _options.MaximumErrorTextLength);
    }

    /// <summary>
    /// Scrubs free text an agent wrote about <paramref name="row"/> the way error text is scrubbed
    /// for the provider (Spec 044 §6.4): every value the event's metadata marks sensitive is
    /// replaced, then credential-shaped values are redacted. Returns null when the text cannot be
    /// verified (the payload's sensitive values could not be read) or holds an exception dump.
    /// </summary>
    public string? ScrubAgentText(MessageEntity row, string text, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(text);
        var payload = row.MessageContent?.EventContent?.EventJson;
        var scrubbed = text;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            // Fail closed: text about a payload whose sensitive values can't be read is not stored.
            if (!_masker.TryCollectSensitiveValues(row.EventTypeId, payload, out var values)) return null;
            foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(value => value.Length))
                scrubbed = scrubbed.Replace(value, EventJsonMasker.DefaultRedactToken, StringComparison.Ordinal);
        }
        return IntelligenceDataRedactor.ScrubText(scrubbed, maximumLength);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? OriginatingEndpoint(string? originating, string? from)
        => string.IsNullOrWhiteSpace(originating) || string.Equals(originating, from, StringComparison.OrdinalIgnoreCase) ? null : originating;

    private static string? CleanSource(string? source)
        => string.IsNullOrWhiteSpace(source) ? null : source.Split([',', '\n', '\r'], 2)[0].Trim();

    private FailureClassificationInput Bound(FailureClassificationInput input)
    {
        while (JsonSerializer.Serialize(OutboundFailureState.Create(input)).Length > _options.MaximumStateCharacters && input.RecentHistory.Count > 0)
        {
            input = input with { RecentHistory = input.RecentHistory.Skip(1).ToArray() };
        }

        if (JsonSerializer.Serialize(OutboundFailureState.Create(input)).Length > _options.MaximumStateCharacters && input.EventPayloadJson is not null)
        {
            input = input with { EventPayloadJson = null };
        }

        if (JsonSerializer.Serialize(OutboundFailureState.Create(input)).Length > _options.MaximumStateCharacters)
            throw new ClassificationServiceException("EvidenceTooLarge", 503);
        return input;
    }
}
