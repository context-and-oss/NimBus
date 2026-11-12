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
        };

        var history = _options.Data.IncludeRecentFailureHistory
            ? await _messages.GetEventHistory(eventId).ConfigureAwait(false) : [];
        var (source, originating) = Senders(target, current, history);
        input = input with { SourceEndpoint = source, OriginatingEndpoint = originating };
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
        // Fail closed, as for error text: without a payload whose sensitive values can be read,
        // nothing proves the text is free of them, so it is not stored.
        var payload = row.MessageContent?.EventContent?.EventJson;
        if (string.IsNullOrWhiteSpace(payload) || !_masker.TryCollectSensitiveValues(row.EventTypeId, payload, out var values))
            return null;
        var scrubbed = text;
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).OrderByDescending(value => value.Length))
            scrubbed = scrubbed.Replace(value, EventJsonMasker.DefaultRedactToken, StringComparison.Ordinal);
        return IntelligenceDataRedactor.ScrubText(scrubbed, maximumLength);
    }

    /// <summary>
    /// The endpoint that sent the failed event and, when it differs, the one it originated from.
    /// A failure row's <c>From</c> is the subscriber that answered with the error, so the sender
    /// is read from the event request that reached this endpoint, else from the publisher
    /// captured at publish time (<c>OriginatingFrom</c>).
    /// </summary>
    private static (string? Source, string? Originating) Senders(MessageEntity target, UnresolvedEvent current, IEnumerable<MessageEntity> history)
    {
        var originating = Endpoint(current.OriginatingFrom) ?? Endpoint(target.OriginatingFrom);
        var request = history
            .Where(row => row.MessageType == MessageType.EventRequest
                && string.Equals(row.EndpointId, target.EndpointId, StringComparison.OrdinalIgnoreCase)
                && row.EnqueuedTimeUtc <= target.EnqueuedTimeUtc)
            .OrderByDescending(row => row.EnqueuedTimeUtc)
            .Select(row => Endpoint(row.From))
            .FirstOrDefault(from => from is not null);
        var source = request ?? originating;
        return (source, string.Equals(originating, source, StringComparison.OrdinalIgnoreCase) ? null : originating);
    }

    private static string? Endpoint(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, Constants.Self, StringComparison.OrdinalIgnoreCase) ? null : value;

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
