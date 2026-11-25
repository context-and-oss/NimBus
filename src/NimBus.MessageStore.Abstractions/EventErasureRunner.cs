using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions;

/// <summary>One tracking row of an event copy, as an erasure reads it.</summary>
/// <param name="Event">The row.</param>
/// <param name="Hidden">
/// True when the row is archived, soft-deleted or otherwise not visible to the endpoint's views.
/// A hidden row never blocks its session.
/// </param>
public sealed record ErasureTrackingRow(UnresolvedEvent Event, bool Hidden);

/// <summary>What an erasure reads before it writes anything.</summary>
/// <param name="TrackingRows">Every tracking row of the pair, in any status, session or visibility.</param>
/// <param name="HistoryCount">The message-history entries of the event on the endpoint.</param>
/// <param name="Payloads">
/// Every event payload (<c>EventContent.EventJson</c>) held by those rows and history entries;
/// the Compose audits are matched against them.
/// </param>
public sealed record ErasureSources(
    IReadOnlyList<ErasureTrackingRow> TrackingRows,
    int HistoryCount,
    IReadOnlyCollection<string> Payloads);

/// <summary>
/// The provider-specific reads and writes of one event copy's erasure, driven by
/// <see cref="EventErasureRunner"/>. Each write returns how many items it removed or redacted and
/// throws on a provider error; the runner maps the error to an outstanding place.
/// </summary>
public interface IEventErasureSteps
{
    /// <summary>Reads the tracking rows, history count and payloads of the pair.</summary>
    Task<ErasureSources> ReadSourcesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Redacts the audit entries with the pair's event id on its endpoint (or with no endpoint) for
    /// which <see cref="ErasureRules.AuditNeedsRedaction"/> holds: data replaced by
    /// <see cref="ErasureRules.RedactedAuditData"/>, comment cleared. Returns the entries changed.
    /// </summary>
    Task<int> RedactEventAuditsAsync(string marker, CancellationToken cancellationToken);

    /// <summary>
    /// Redacts the Compose audit entries stored under <paramref name="producerEndpointId"/> and
    /// <paramref name="eventTypeId"/> whose data <paramref name="matches"/> (given the data and the
    /// event type stored on the entry): data replaced with <paramref name="marker"/>, comment cleared.
    /// Returns the entries changed.
    /// </summary>
    Task<int> RedactComposeAuditsAsync(
        string producerEndpointId,
        string eventTypeId,
        Func<string?, string?, bool> matches,
        string marker,
        CancellationToken cancellationToken);

    /// <summary>Deletes every message-history entry of the event on the endpoint. Returns the entries deleted.</summary>
    Task<int> DeleteHistoryAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the pair's event report marker. Returns 1 when one was deleted, else 0.</summary>
    Task<int> DeleteEventReportAsync(CancellationToken cancellationToken);

    /// <summary>Deletes every tracking row of the pair, whatever its status, session or visibility. Returns the rows deleted.</summary>
    Task<int> DeleteTrackingRowsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs <see cref="IMessageTrackingStore.EraseEventCopyAsync"/> over a provider's
/// <see cref="IEventErasureSteps"/>, so every provider applies the same rules (Spec 049
/// Decision 4): the blocking-row guard, the order (audits, history, report marker, tracking rows
/// last), source retention, and the mapping of provider errors to outstanding places.
/// </summary>
public static class EventErasureRunner
{
    /// <summary>Erases the copy named by <paramref name="request"/> through <paramref name="steps"/>.</summary>
    /// <exception cref="ArgumentException">The request has an empty endpoint id, event id or erasure id.</exception>
    public static async Task<EventErasureResult> RunAsync(
        EventErasureRequest request,
        IEventErasureSteps steps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EndpointId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EventId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ErasureId, nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        ErasureSources sources;
        try
        {
            sources = await steps.ReadSourcesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was written: without the rows the blocking guard can't be applied.
            return EventErasureResult.AllOutstanding(request.EndpointId, request.EventId, ErasureOutstandingReasons.WriteFailed);
        }

        if (sources.TrackingRows.Any(row => !row.Hidden && BlockedEventRules.BlocksItsSession(row.Event)))
            return EventErasureResult.AllOutstanding(request.EndpointId, request.EventId, ErasureOutstandingReasons.BlocksSession);

        var marker = ErasureRules.RedactionMarker(request.ErasureId);
        var results = new Dictionary<ErasurePlace, ErasurePlaceResult>
        {
            [ErasurePlace.EventAudits] = await WriteAsync(
                ErasurePlace.EventAudits, ErasurePlaceState.Redacted,
                () => steps.RedactEventAuditsAsync(marker, cancellationToken)).ConfigureAwait(false),
        };

        var compose = await ComposeAsync(request, sources, steps, marker, cancellationToken).ConfigureAwait(false);
        results[ErasurePlace.ComposeAudits] = compose;
        var sourcesFree = compose.State != ErasurePlaceState.Outstanding;

        // Source retention: the rows and history are the only copies of the payload the Compose
        // audits are matched against, so they stay until that place is done.
        results[ErasurePlace.MessageHistory] = sourcesFree
            ? await WriteAsync(ErasurePlace.MessageHistory, ErasurePlaceState.Removed,
                () => steps.DeleteHistoryAsync(cancellationToken)).ConfigureAwait(false)
            : Retained(ErasurePlace.MessageHistory, sources.HistoryCount);

        results[ErasurePlace.EventReport] = await WriteAsync(
            ErasurePlace.EventReport, ErasurePlaceState.Removed,
            () => steps.DeleteEventReportAsync(cancellationToken)).ConfigureAwait(false);

        // Last, so a failure part-way leaves the row and the event stays visible and known.
        results[ErasurePlace.TrackingRow] = sourcesFree
            ? await WriteAsync(ErasurePlace.TrackingRow, ErasurePlaceState.Removed,
                () => steps.DeleteTrackingRowsAsync(cancellationToken)).ConfigureAwait(false)
            : Retained(ErasurePlace.TrackingRow, sources.TrackingRows.Count);

        return new EventErasureResult(
            request.EndpointId,
            request.EventId,
            Enum.GetValues<ErasurePlace>().Select(place => results[place]).ToList());
    }

    private static async Task<ErasurePlaceResult> ComposeAsync(
        EventErasureRequest request,
        ErasureSources sources,
        IEventErasureSteps steps,
        string marker,
        CancellationToken cancellationToken)
    {
        var payloads = sources.Payloads.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();

        // With no payload left, or no producer or type to search under, the Compose audits can't
        // be searched. Absence is reported only when an earlier step already confirmed it.
        if (payloads.Count == 0
            || string.IsNullOrWhiteSpace(request.ProducerEndpointId)
            || string.IsNullOrWhiteSpace(request.EventTypeId))
        {
            return request.ComposeConfirmed
                ? ErasurePlaceResult.Done(ErasurePlace.ComposeAudits, ErasurePlaceState.Redacted, 0)
                : ErasurePlaceResult.Outstanding(ErasurePlace.ComposeAudits, ErasureOutstandingReasons.RetryContextMissing);
        }

        return await WriteAsync(
            ErasurePlace.ComposeAudits, ErasurePlaceState.Redacted,
            () => steps.RedactComposeAuditsAsync(
                request.ProducerEndpointId,
                request.EventTypeId,
                (data, storedEventTypeId) => ErasureRules.ComposeDataMatches(data, storedEventTypeId, payloads),
                marker,
                cancellationToken)).ConfigureAwait(false);
    }

    private static ErasurePlaceResult Retained(ErasurePlace place, int found) =>
        found > 0
            ? ErasurePlaceResult.Outstanding(place, ErasureOutstandingReasons.RetainedForRetry)
            : ErasurePlaceResult.Done(place, ErasurePlaceState.Removed, 0);

    private static async Task<ErasurePlaceResult> WriteAsync(
        ErasurePlace place,
        ErasurePlaceState cleaned,
        Func<Task<int>> write)
    {
        try
        {
            return ErasurePlaceResult.Done(place, cleaned, await write().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            return ErasurePlaceResult.Outstanding(place, ErasureOutstandingReasons.WriteFailed);
        }
    }
}
