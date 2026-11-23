using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// In-memory implementation of <see cref="IAlertSignalStore"/> (Spec 053 Phase 2): LINQ over the
/// stored messages and live events of <see cref="InMemoryMessageStore"/>, which hands in its
/// collections. Archived and removed events are not in the live collection.
/// </summary>
internal sealed class InMemoryAlertSignalStore : IAlertSignalStore
{
    private const string HandoffSubStatus = "Handoff";

    private readonly Func<IEnumerable<MessageEntity>> _messages;
    private readonly Func<IEnumerable<UnresolvedEvent>> _events;

    public InMemoryAlertSignalStore(Func<IEnumerable<MessageEntity>> messages, Func<IEnumerable<UnresolvedEvent>> events)
    {
        _messages = messages;
        _events = events;
    }

    public Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        if (toUtc <= fromUtc)
            return Task.FromResult(0);

        var types = eventTypes is { Count: > 0 } ? new HashSet<string>(eventTypes, StringComparer.Ordinal) : null;
        var count = _messages()
            .Where(m => string.Equals(m.EndpointId, endpointId, StringComparison.Ordinal)
                && m.MessageType == MessageType.ErrorResponse
                && m.EnqueuedTimeUtc >= fromUtc
                && m.EnqueuedTimeUtc < toUtc
                && (types is null || (m.EventTypeId is not null && types.Contains(m.EventTypeId)))
                && MatchesErrorText(m, errorTextContains))
            .Take(cap)
            .Count();
        return Task.FromResult(count);
    }

    public Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        var oldest = _events()
            .Where(e => string.Equals(e.EndpointId, endpointId, StringComparison.Ordinal)
                && (e.ResolutionStatus == ResolutionStatus.Failed
                    || (e.ResolutionStatus == ResolutionStatus.Pending
                        && string.Equals(e.PendingSubStatus, HandoffSubStatus, StringComparison.Ordinal))))
            .OrderBy(e => e.UpdatedAt)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ThenBy(e => e.SessionId, StringComparer.Ordinal)
            .FirstOrDefault();

        return Task.FromResult(oldest is null
            ? null
            : new BlockingEvent(oldest.EventId, oldest.SessionId ?? string.Empty, oldest.ResolutionStatus, DateTime.SpecifyKind(oldest.UpdatedAt, DateTimeKind.Utc)));
    }

    public Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        return Task.FromResult(_messages().Any(m =>
            string.Equals(m.EndpointId, endpointId, StringComparison.Ordinal)
            && m.MessageType == MessageType.ResolutionResponse
            && m.EnqueuedTimeUtc >= sinceUtc));
    }

    // The error text is ErrorContent.ErrorText, else (when that is null or empty) the
    // dead-letter description.
    private static bool MatchesErrorText(MessageEntity message, string? errorTextContains)
    {
        if (string.IsNullOrEmpty(errorTextContains))
            return true;

        var errorText = message.MessageContent?.ErrorContent?.ErrorText;
        if (string.IsNullOrEmpty(errorText))
            errorText = message.DeadLetterErrorDescription;

        return errorText is not null && errorText.Contains(errorTextContains, StringComparison.OrdinalIgnoreCase);
    }
}
