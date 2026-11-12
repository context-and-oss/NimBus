using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using CoreConstants = NimBus.Core.Messages.Constants;
using CoreSessionState = NimBus.Core.Messages.SessionState;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Services;

/// <summary>
/// Operations → Session state and Clean up session state (plan 2026-11-03). Every read and write
/// holds the session's lock on the endpoint subscription, so it can't interleave with the
/// endpoint processing the session; a session a receiver holds reports as locked.
/// </summary>
public sealed partial class SessionStateService : ISessionStateService
{
    /// <summary>Most messages peeked per session and subscription.</summary>
    internal const int PeekLimit = 100;

    /// <summary>Default and largest number of sessions one cleanup preview inspects.</summary>
    internal const int DefaultMaxSessions = 200;
    internal const int MaxSessionsLimit = 1000;

    /// <summary>Default look-back of a cleanup preview.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    private const int SearchPageSize = 100;
    private const int Parallelism = 8;

    private static readonly string[] ConflictErrors = { "The session state changed since it was read. Review the current state and try again." };
    private static readonly string[] LockedErrors = { "A receiver holds the session, so its state can't be changed now. Try again shortly." };

    // A blocking event in one of these states is still being worked on, so its block is real.
    private static readonly HashSet<StoreResolutionStatus> UnresolvedStatuses = new()
    {
        StoreResolutionStatus.Pending,
        StoreResolutionStatus.Deferred,
        StoreResolutionStatus.Failed,
        StoreResolutionStatus.TooManyRequests,
        StoreResolutionStatus.DeadLettered,
        StoreResolutionStatus.Unsupported,
    };

    private readonly ServiceBusClient _sbClient;
    private readonly IMessageTrackingStore _store;
    private readonly ILogger<SessionStateService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="sbClient">The client for the endpoints' namespace.</param>
    /// <param name="store">The message store holding the endpoints' events.</param>
    /// <param name="logger">The logger.</param>
    public SessionStateService(ServiceBusClient sbClient, IMessageTrackingStore store, ILogger<SessionStateService> logger)
    {
        _sbClient = sbClient;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SessionStateView> GetAsync(string endpointId, string sessionId, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(endpointId, sessionId, whileLocked: null, cancellationToken).ConfigureAwait(false);
        return ToView(inspection);
    }

    /// <inheritdoc />
    public async Task<SessionStateChangeOutcome> ChangeAsync(
        string endpointId,
        string sessionId,
        SessionStateChangeRequest request,
        Func<SessionStateWrite, Task> beforeWrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(beforeWrite);

        SessionStateChangeOutcome? outcome = null;
        var inspection = await InspectAsync(endpointId, sessionId, async (receiver, current) =>
        {
            if (!VersionMatches(current.Version, request.ExpectedVersion))
            {
                outcome = new SessionStateChangeOutcome(SessionStateChangeStatus.Conflict, ToView(current),
                    ConflictErrors);
                return;
            }

            var (next, errors) = Apply(request, current);
            if (next is null)
            {
                outcome = new SessionStateChangeOutcome(SessionStateChangeStatus.Invalid, ToView(current), errors);
                return;
            }

            var data = next.ToBinaryData();
            await beforeWrite(new SessionStateWrite(sessionId, ActionName(request.Action), current.Text, TextOf(data))).ConfigureAwait(false);
            await receiver.SetSessionStateAsync(data, cancellationToken).ConfigureAwait(false);
            outcome = new SessionStateChangeOutcome(SessionStateChangeStatus.Changed, ToView(current.WithStored(data)), Array.Empty<string>());
        }, cancellationToken).ConfigureAwait(false);

        return outcome ?? new SessionStateChangeOutcome(SessionStateChangeStatus.Locked, ToView(inspection),
            LockedErrors);
    }

    /// <inheritdoc />
    public async Task<SessionCleanupPreview> PreviewCleanupAsync(string endpointId, SessionCleanupPreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var since = (request.UpdatedSince ?? DateTime.UtcNow - DefaultWindow).ToUniversalTime();
        var max = Math.Clamp(request.MaxSessions ?? DefaultMaxSessions, 1, MaxSessionsLimit);
        var (sessionIds, truncated) = await CollectSessionIdsAsync(endpointId, since, max).ConfigureAwait(false);

        var inspections = new Inspection[sessionIds.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, sessionIds.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                try
                {
                    inspections[i] = await InspectAsync(endpointId, sessionIds[i], whileLocked: null, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogInspectFailed(ex, sessionIds[i], endpointId);
                    inspections[i] = Inspection.Failed(sessionIds[i], ex.Message);
                }
            }).ConfigureAwait(false);

        var withState = inspections.Where(i => i.Classification != Classification.Empty).ToList();
        return new SessionCleanupPreview
        {
            InspectedCount = inspections.Length,
            EmptyCount = inspections.Length - withState.Count,
            Truncated = truncated,
            Sessions = withState
                .OrderBy(i => i.Classification)
                .ThenBy(i => i.SessionId, StringComparer.Ordinal)
                .Select(i => new SessionCleanupCandidate
                {
                    SessionId = i.SessionId,
                    Classification = i.Classification switch
                    {
                        Classification.Idle => SessionCleanupCandidateClassification.Idle,
                        Classification.StaleBlock => SessionCleanupCandidateClassification.StaleBlock,
                        _ => SessionCleanupCandidateClassification.InUse,
                    },
                    Reason = i.Reason,
                    StateVersion = i.Version,
                    StateJson = i.Text,
                })
                .ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<SessionCleanupResult> CleanupAsync(string endpointId, SessionCleanupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var selections = (request.Sessions ?? new List<SessionCleanupSelection>())
            .Where(s => !string.IsNullOrEmpty(s.SessionId))
            .GroupBy(s => s.SessionId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var skipped = new ConcurrentBag<SessionCleanupSkip>();
        var errors = new ConcurrentBag<string>();
        var reset = 0;

        await Parallel.ForEachAsync(
            selections,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (selection, token) =>
            {
                try
                {
                    var handled = false;
                    var inspection = await InspectAsync(endpointId, selection.SessionId, async (receiver, current) =>
                    {
                        handled = true;
                        if (!VersionMatches(current.Version, selection.StateVersion))
                        {
                            skipped.Add(Skip(selection.SessionId, "The state changed since the preview."));
                            return;
                        }

                        var eligible = current.Classification == Classification.Idle
                            || (request.IncludeStaleBlocks && current.Classification == Classification.StaleBlock);
                        if (!eligible)
                        {
                            skipped.Add(Skip(selection.SessionId, current.Reason ?? "No longer idle."));
                            return;
                        }

                        await receiver.SetSessionStateAsync(new CoreSessionState().ToBinaryData(), token).ConfigureAwait(false);
                        Interlocked.Increment(ref reset);
                    }, token).ConfigureAwait(false);

                    if (!handled)
                        skipped.Add(Skip(selection.SessionId, inspection.Reason ?? "A receiver holds the session."));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogResetFailed(ex, selection.SessionId, endpointId);
                    errors.Add($"{selection.SessionId}: {ex.Message}");
                }
            }).ConfigureAwait(false);

        return new SessionCleanupResult
        {
            ResetCount = reset,
            Skipped = skipped.OrderBy(s => s.SessionId, StringComparer.Ordinal).ToList(),
            Errors = errors.OrderBy(e => e, StringComparer.Ordinal).ToList(),
        };
    }

    // ─────────── Inspection ───────────

    /// <summary>
    /// Accepts the session, reads its state and everything that says whether it is in use, and
    /// runs <paramref name="whileLocked"/> before releasing the lock. A locked session is
    /// returned without running it.
    /// </summary>
    private async Task<Inspection> InspectAsync(
        string endpointId,
        string sessionId,
        Func<ServiceBusSessionReceiver, Inspection, Task>? whileLocked,
        CancellationToken cancellationToken)
    {
        ServiceBusSessionReceiver receiver;
        try
        {
            receiver = await _sbClient.AcceptSessionAsync(endpointId, endpointId, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
        {
            var locked = Inspection.Locked(sessionId);
            return locked.WithCounts(await CountDeferredSubscriptionAsync(endpointId, sessionId, cancellationToken).ConfigureAwait(false),
                await _store.DownloadEndpointSessionStateCount(endpointId, sessionId).ConfigureAwait(false));
        }

        await using (receiver.ConfigureAwait(false))
        {
            var stored = await receiver.GetSessionStateAsync(cancellationToken).ConfigureAwait(false);
            var active = (await receiver.PeekMessagesAsync(PeekLimit, cancellationToken: cancellationToken).ConfigureAwait(false)).Count;
            var deferredSubscription = await CountDeferredSubscriptionAsync(endpointId, sessionId, cancellationToken).ConfigureAwait(false);
            var counts = await _store.DownloadEndpointSessionStateCount(endpointId, sessionId).ConfigureAwait(false);

            var inspection = Inspection.Read(sessionId, stored) with { ActiveCount = active };
            inspection = inspection.WithCounts(deferredSubscription, counts);
            if (inspection.State?.BlockedByEventId is { } blockingEventId)
            {
                var blocking = await _store.GetEvent(endpointId, blockingEventId).ConfigureAwait(false);
                inspection = inspection with { BlockingStatus = blocking?.ResolutionStatus, BlockingFound = blocking is not null };
            }

            inspection = Classify(inspection);
            if (whileLocked is not null)
                await whileLocked(receiver, inspection).ConfigureAwait(false);
            return inspection;
        }
    }

    private async Task<int> CountDeferredSubscriptionAsync(string endpointId, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var receiver = await _sbClient.AcceptSessionAsync(
                endpointId, CoreConstants.DeferredSubscriptionName, sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (receiver.ConfigureAwait(false))
            {
                return (await receiver.PeekMessagesAsync(PeekLimit, cancellationToken: cancellationToken).ConfigureAwait(false)).Count;
            }
        }
        catch (ServiceBusException ex) when (ex.Reason is ServiceBusFailureReason.SessionCannotBeLocked or ServiceBusFailureReason.MessagingEntityNotFound)
        {
            // The DeferredProcessor holds the session (it is replaying it), or the endpoint has no
            // Deferred subscription. Either way nothing can be counted; a held session is in use.
            return ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked ? -1 : 0;
        }
    }

    private async Task<(List<string> SessionIds, bool Truncated)> CollectSessionIdsAsync(string endpointId, DateTime since, int max)
    {
        var sessionIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = string.Empty;
        do
        {
            var page = await _store.GetEventsByFilter(
                new NimBus.MessageStore.EventFilter { EndPointId = endpointId, UpdatedAtFrom = since }, token ?? string.Empty, SearchPageSize).ConfigureAwait(false);
            foreach (var sessionId in page.Events.Select(e => e.SessionId))
            {
                if (string.IsNullOrEmpty(sessionId) || !seen.Add(sessionId))
                    continue;
                if (sessionIds.Count == max)
                    return (sessionIds, true);
                sessionIds.Add(sessionId);
            }

            token = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(token));

        return (sessionIds, false);
    }

    /// <summary>
    /// Classifies a read session. A session is idle when its state is non-empty but nothing
    /// depends on it: no queued or parked messages, no pending or deferred events in the store,
    /// no deferred count, no unreplayed position, no drain and no block. A block whose event is
    /// no longer unresolved is a stale block.
    /// </summary>
    internal static Inspection Classify(Inspection inspection)
    {
        if (inspection.IsLocked)
            return inspection with { Classification = Classification.InUse, Reason = "A receiver holds the session." };
        if (!inspection.Readable)
            return inspection with { Classification = Classification.InUse, Reason = "The stored state isn't NimBus session state; review it in Session state." };
        if (inspection.State is null || inspection.State.IsEmpty())
            return inspection with { Classification = Classification.Empty, Reason = null };

        var state = inspection.State;
        var reasons = new List<string>();
        if (inspection.ActiveCount > 0)
            reasons.Add(Plural(inspection.ActiveCount, "message", "queued on the session"));
        if (inspection.DeferredSubscriptionCount < 0)
            reasons.Add("the DeferredProcessor holds the session on the Deferred subscription");
        else if (inspection.DeferredSubscriptionCount > 0)
            reasons.Add(Plural(inspection.DeferredSubscriptionCount, "message", "parked on the Deferred subscription"));
        if (inspection.StorePendingCount > 0)
            reasons.Add(Plural(inspection.StorePendingCount, "pending event", "in the store"));
        if (inspection.StoreDeferredCount > 0)
            reasons.Add(Plural(inspection.StoreDeferredCount, "deferred event", "in the store"));
        if (state.HasDeferredMessages())
            reasons.Add($"DeferredCount is {state.DeferredCount}");
        // Without a recorded position every assigned sequence counts as waiting, but with
        // DeferredCount at zero and nothing parked that is old bookkeeping, not a parked message.
        if (state.ReplayedThroughSequence is not null && state.HasUnreplayedMessages())
            reasons.Add("parked messages are not replayed yet");
        if (state.DrainRequestedForSequence is not null)
            reasons.Add("a replay of parked messages was requested");

        if (reasons.Count > 0)
            return inspection with { Classification = Classification.InUse, Reason = Capitalize(string.Join("; ", reasons)) + "." };

        if (state.BlockedByEventId is { } eventId)
        {
            if (inspection.BlockingStatus is { } status && UnresolvedStatuses.Contains(status))
                return inspection with { Classification = Classification.InUse, Reason = $"Blocked by event {eventId}, which is {status}." };

            var now = inspection.BlockingFound && inspection.BlockingStatus is { } resolved
                ? $"which is {resolved}"
                : "which isn't in the store";
            return inspection with { Classification = Classification.StaleBlock, Reason = $"Blocked by event {eventId}, {now}." };
        }

        return inspection with { Classification = Classification.Idle, Reason = "Nothing queued, parked or blocked." };
    }

    private static (CoreSessionState? Next, IReadOnlyList<string> Errors) Apply(SessionStateChangeRequest request, Inspection current)
    {
        if (request.Action == SessionStateChangeRequestAction.Reset)
            return (new CoreSessionState(), Array.Empty<string>());

        if (request.Action == SessionStateChangeRequestAction.Replace)
        {
            if (string.IsNullOrWhiteSpace(request.StateJson))
                return (null, new[] { "Replace needs the new state as JSON; use Reset to clear the state." });
            return CoreSessionState.TryParseJson(request.StateJson, out var replacement, out var errors)
                ? (replacement, Array.Empty<string>())
                : (null, errors);
        }

        if (!current.Readable || current.State is null)
            return (null, new[] { "The stored state isn't NimBus session state. Reset or replace it instead." });

        var state = current.State;
        switch (request.Action)
        {
            case SessionStateChangeRequestAction.ClearBlock:
                if (state.BlockedByEventId is null)
                    return (null, new[] { "The session isn't blocked." });
                state.ClearBlock();
                return (state, Array.Empty<string>());

            case SessionStateChangeRequestAction.MarkReplayed:
                if (current.DeferredSubscriptionCount != 0 || current.StoreDeferredCount > 0)
                    return (null, new[]
                    {
                        "Messages are still parked for this session. Marking them replayed would let later events overtake them; " +
                        "use Reprocess deferred or Session purge instead.",
                    });
                state.MarkAllReplayed();
                return (state, Array.Empty<string>());

            default:
                return (null, new[] { $"Unknown action {request.Action}." });
        }
    }

    // ─────────── Mapping ───────────

    private static SessionStateView ToView(Inspection inspection)
    {
        var state = inspection.State;
        return new SessionStateView
        {
            SessionId = inspection.SessionId,
            Locked = inspection.IsLocked,
            Readable = inspection.Readable,
            IsEmpty = !inspection.IsLocked && (state?.IsEmpty() ?? inspection.Text is null),
            StateJson = inspection.Text,
            StateVersion = inspection.Version,
            BlockedByEventId = state?.BlockedByEventId,
            BlockedByMessageId = state?.BlockedByMessageId,
            DeferredCount = state?.DeferredCount ?? 0,
            NextDeferralSequence = state?.NextDeferralSequence ?? 0,
            ReplayedThroughSequence = state?.ReplayedThroughSequence,
            DrainRequestedForSequence = state?.DrainRequestedForSequence,
            DrainRequestedAtUtc = state?.DrainRequestedAtUtc,
#pragma warning disable CS0618 // Legacy deferred sequence numbers are shown, never written.
            LegacyDeferredCount = state?.DeferredSequenceNumbers.Count ?? 0,
#pragma warning restore CS0618
            Blocked = state?.BlockedByEventId is not null,
            HasUnreplayedMessages = state?.HasUnreplayedMessages() ?? false,
            ActiveMessageCount = inspection.ActiveCount,
            DeferredSubscriptionCount = Math.Max(inspection.DeferredSubscriptionCount, 0),
            StorePendingCount = inspection.StorePendingCount,
            StoreDeferredCount = inspection.StoreDeferredCount,
            BlockingEventStatus = state?.BlockedByEventId is null
                ? null
                : inspection.BlockingFound ? inspection.BlockingStatus?.ToString() : "NotFound",
            Message = inspection.IsLocked
                ? "A receiver holds the session, so its state can't be read now. Try again shortly."
                : inspection.Readable ? null : "The stored state isn't NimBus session state.",
        };
    }

    /// <summary>The version of stored session state: the SHA-256 of its bytes, in lowercase hex.</summary>
    internal static string VersionOf(BinaryData? stored) =>
        Convert.ToHexStringLower(SHA256.HashData(stored?.ToArray() ?? Array.Empty<byte>()));

    private static bool VersionMatches(string? actual, string? expected) =>
        actual is not null && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? TextOf(BinaryData? data)
    {
        if (data is null)
            return null;
        var bytes = data.ToArray();
        return bytes.Length == 0 ? null : Encoding.UTF8.GetString(bytes);
    }

    private static string ActionName(SessionStateChangeRequestAction action) => action switch
    {
        SessionStateChangeRequestAction.ClearBlock => "clearBlock",
        SessionStateChangeRequestAction.MarkReplayed => "markReplayed",
        SessionStateChangeRequestAction.Reset => "reset",
        SessionStateChangeRequestAction.Replace => "replace",
        _ => action.ToString(),
    };

    private static SessionCleanupSkip Skip(string sessionId, string reason) => new() { SessionId = sessionId, Reason = reason };

    private static string Plural(int count, string noun, string rest) =>
        $"{count}{(count >= PeekLimit ? "+" : string.Empty)} {noun}{(count == 1 ? string.Empty : "s")} {rest}";

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // ─────────── Types ───────────

    /// <summary>How a session with state relates to the endpoint's work.</summary>
    internal enum Classification
    {
        Idle,
        StaleBlock,
        InUse,
        Empty,
    }

    /// <summary>What one look at a session found.</summary>
    internal sealed record Inspection(string SessionId)
    {
        public bool IsLocked { get; init; }
        public bool Readable { get; init; } = true;
        public string? Text { get; init; }
        public string? Version { get; init; }
        public CoreSessionState? State { get; init; }
        public int ActiveCount { get; init; }

        /// <summary>Messages on the Deferred subscription; -1 when the session is held there.</summary>
        public int DeferredSubscriptionCount { get; init; }

        public int StorePendingCount { get; init; }
        public int StoreDeferredCount { get; init; }
        public StoreResolutionStatus? BlockingStatus { get; init; }
        public bool BlockingFound { get; init; }
        public Classification Classification { get; init; } = Classification.InUse;
        public string? Reason { get; init; }

        public static Inspection Locked(string sessionId) =>
            Classify(new Inspection(sessionId) { IsLocked = true, Readable = false });

        public static Inspection Failed(string sessionId, string error) =>
            new(sessionId) { Readable = false, Classification = Classification.InUse, Reason = $"Couldn't inspect the session: {error}" };

        public static Inspection Read(string sessionId, BinaryData? stored)
        {
            var inspection = new Inspection(sessionId) { Version = VersionOf(stored), Text = TextOf(stored) };
            try
            {
                return inspection with { State = CoreSessionState.FromBinaryData(stored) };
            }
            catch (System.Text.Json.JsonException)
            {
                return inspection with { Readable = false };
            }
        }

        /// <summary>This inspection after <paramref name="stored"/> was written in its place.</summary>
        public Inspection WithStored(BinaryData stored) =>
            Read(SessionId, stored) with
            {
                ActiveCount = ActiveCount,
                DeferredSubscriptionCount = DeferredSubscriptionCount,
                StorePendingCount = StorePendingCount,
                StoreDeferredCount = StoreDeferredCount,
                BlockingStatus = BlockingStatus,
                BlockingFound = BlockingFound,
            };

        public Inspection WithCounts(int deferredSubscription, NimBus.MessageStore.States.SessionStateCount counts) =>
            this with
            {
                DeferredSubscriptionCount = deferredSubscription,
                StorePendingCount = counts.PendingEvents?.Count() ?? 0,
                StoreDeferredCount = counts.DeferredEvents?.Count() ?? 0,
            };
    }

    // ─────────── Logging ───────────

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed to inspect session {SessionId} on {EndpointId} for session state cleanup")]
    private partial void LogInspectFailed(Exception ex, string sessionId, string endpointId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to reset the session state of {SessionId} on {EndpointId}")]
    private partial void LogResetFailed(Exception ex, string sessionId, string endpointId);
}
