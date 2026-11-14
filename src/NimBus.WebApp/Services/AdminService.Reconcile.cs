using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using StoreEventFilter = NimBus.MessageStore.EventFilter;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Services;

public partial class AdminService
{
    /// <summary>Rows the synchronous preview will classify when the caller names no limit.</summary>
    internal const int DefaultStalePendingMaxRows = 500;

    /// <summary>Hard cap on a single preview, whatever the caller asks for.</summary>
    internal const int MaxStalePendingRows = 2000;

    /// <summary>
    /// How stale a row must be before it may be repaired. The controller rejects a cut-off newer
    /// than this and the reconcile skips any row touched more recently, so a row that is simply
    /// mid-flight is never overwritten.
    /// </summary>
    internal const int StalePendingAgeMinutes = 15;

    public async Task<StalePendingPreview> PreviewStalePendingAsync(
        string endpointId,
        DateTime? enqueuedBefore,
        int maxRows)
    {
        var limit = NormalizeMaxRows(maxRows);
        var rows = new List<StalePendingRow>();
        var scan = await ScanCandidatesAsync(endpointId, enqueuedBefore, async row =>
        {
            if (rows.Count >= limit)
            {
                return false;
            }

            rows.Add(ToContractRow(await ClassifyAsync(row, endpointId)));
            return true;
        });

        return new StalePendingPreview
        {
            EndpointId = endpointId,
            Scanned = scan.Scanned,
            Candidates = rows.Count,
            Repairable = rows.Count(row => row.Verdict == StalePendingRowVerdict.Repairable),
            Truncated = scan.Truncated,
            Rows = rows,
        };
    }

    public async Task<StalePendingReconcileResult> ReconcileStalePendingAsync(
        string endpointId,
        DateTime enqueuedBefore,
        int? maxRepairs,
        string auditorName,
        string? note)
    {
        // Unlike the preview, the reconcile is not capped by candidate count: it walks every Pending
        // row before the cut-off and stops only once it holds maxRepairs repairable ones. Otherwise
        // a backlog of operator-decision rows ahead in the scan would hide the repairable rows behind
        // them, and no number of "repair what is listed and preview again" rounds would reach them.
        // The budget bounds what gets ATTEMPTED, so a caller batching through a large backlog can
        // read "Processed < maxRepairs" as "the scan reached the end".
        var budget = maxRepairs.GetValueOrDefault(int.MaxValue);
        var repairable = new List<StalePendingClassification>();
        await ScanCandidatesAsync(endpointId, enqueuedBefore, async row =>
        {
            if (repairable.Count >= budget)
            {
                return false;
            }

            var classification = await ClassifyAsync(row, endpointId);
            if (classification.IsRepairable)
            {
                repairable.Add(classification);
            }

            return true;
        });

        var result = new StalePendingReconcileResult
        {
            Errors = new List<string>(),
            RepairedEventIds = new List<string>(),
        };
        var now = DateTime.UtcNow;
        var ageCutoff = now.AddMinutes(-StalePendingAgeMinutes);

        foreach (var row in repairable.Select(classification => classification.Row))
        {
            result.Processed++;
            try
            {
                // Re-read and re-classify: the scan's verdict is a hint, the row as it stands now is
                // what gets written, conditionally on its last message id.
                // Null means the row is no longer Pending: it moved on since the preview, so the
                // reconcile skips it instead of reporting a failure.
                var storedRow = await _messageStore.GetPendingEvent(endpointId, row.EventId, row.SessionId ?? string.Empty);
                if (storedRow is null || storedRow.UpdatedAt > ageCutoff)
                {
                    result.Skipped++;
                    continue;
                }

                var history = (await _messageStore.GetEventHistory(row.EventId)).ToList();
                var classification = StalePendingReconciler.Classify(storedRow, history, endpointId);
                if (!classification.IsRepairable || classification.Response is null)
                {
                    result.Skipped++;
                    continue;
                }

                var projection = StalePendingReconciler.BuildCompletedProjection(
                    classification.Response, history, endpointId, now);
                if (!await _messageStore.TryCompletePendingMessage(
                        storedRow.EventId,
                        storedRow.SessionId!,
                        endpointId,
                        storedRow.LastMessageId,
                        projection))
                {
                    result.Skipped++;
                    continue;
                }

                var auditData = JsonConvert.SerializeObject(new
                {
                    previousStatus = storedRow.ResolutionStatus.ToString(),
                    newStatus = StoreResolutionStatus.Completed.ToString(),
                    staleMessageType = storedRow.MessageType.ToString(),
                    staleMessageId = storedRow.LastMessageId,
                    staleEnqueuedTimeUtc = storedRow.EnqueuedTimeUtc,
                    responseMessageId = classification.Response.MessageId,
                    responseEnqueuedTimeUtc = classification.Response.EnqueuedTimeUtc,
                    note,
                });
                // The row is already repaired. An audit failure from here on must not be reported
                // as a failed repair: the caller would see Failed++ for a row that did change, and
                // a second run cannot compensate because the row is no longer Pending. Count the
                // repair, and surface the missing audit row as its own error.
                try
                {
                    await _messageStore.StoreMessageAudit(
                        storedRow.EventId,
                        new MessageAuditEntity
                        {
                            AuditorName = auditorName,
                            AuditTimestamp = now,
                            AuditType = MessageAuditType.ReconcileStalePending,
                            Comment = "Reconciled a stale Pending row from its stored ResolutionResponse.",
                            Data = auditData,
                            EventId = storedRow.EventId,
                            EndpointId = endpointId,
                        },
                        endpointId,
                        storedRow.EventTypeId);
                }
                catch (Exception auditException) when (auditException is not OperationCanceledException)
                {
                    LogStalePendingAuditFailed(auditException, storedRow.EventId, endpointId);
                    result.Errors.Add(
                        $"{storedRow.EventId}: repaired, but writing its audit row failed ({auditException.Message}). " +
                        "The repair stands; the per-event audit is missing.");
                }

                result.Succeeded++;
                result.RepairedEventIds.Add(storedRow.EventId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogStalePendingRepairFailed(exception, row.EventId, endpointId);
                result.Failed++;
                result.Errors.Add($"{row.EventId}: {exception.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Walks the endpoint's Pending rows enqueued before the cut-off and hands each candidate to
    /// <paramref name="onCandidate"/>, which returns false to stop the scan; the result then reads
    /// as truncated. <c>Scanned</c> counts every row of the endpoint the query returned, candidate
    /// or not, so the caller can tell "nothing to do" from "nothing matched".
    /// </summary>
    private async Task<(int Scanned, bool Truncated)> ScanCandidatesAsync(
        string endpointId,
        DateTime? enqueuedBefore,
        Func<UnresolvedEvent, Task<bool>> onCandidate)
    {
        var scanned = 0;
        var continuationToken = string.Empty;

        do
        {
            var page = await _messageStore.GetEventsByFilter(
                new StoreEventFilter
                {
                    EndPointId = endpointId,
                    ResolutionStatus = new List<string> { StoreResolutionStatus.Pending.ToString() },
                    // Push the cut-off into the query so a backlog of rows newer than it is not
                    // paged through 20 at a time. The store's bound is inclusive and providers may
                    // ignore the field (the in-memory one does), so the strict test below stays.
                    EnqueuedAtTo = enqueuedBefore,
                },
                continuationToken,
                PageSize);

            foreach (var row in page.Events)
            {
                // EndPointId is an exact match in the first-party stores, but a provider that
                // still treats it as a prefix would make previewing "Orders" also classify
                // "OrdersArchive" rows against the wrong endpoint and report them as bogus
                // NoTerminal verdicts. Those rows are not this endpoint's, so they are not counted
                // as scanned either.
                if (!string.Equals(row.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned++;
                if (!StalePendingReconciler.IsCandidate(row)
                    || (enqueuedBefore.HasValue && row.EnqueuedTimeUtc >= enqueuedBefore.Value))
                {
                    continue;
                }

                if (!await onCandidate(row))
                {
                    return (scanned, true);
                }
            }

            continuationToken = page.ContinuationToken;
        }
        while (continuationToken is not null);

        return (scanned, false);
    }

    private async Task<StalePendingClassification> ClassifyAsync(UnresolvedEvent row, string endpointId)
    {
        var history = (await _messageStore.GetEventHistory(row.EventId)).ToList();
        return StalePendingReconciler.Classify(row, history, endpointId);
    }

    private static StalePendingRow ToContractRow(StalePendingClassification classification) => new()
    {
        EventId = classification.Row.EventId,
        SessionId = classification.Row.SessionId,
        EventTypeId = classification.Row.EventTypeId,
        RowMessageType = classification.Row.MessageType.ToString(),
        StaleMessageId = classification.Row.LastMessageId,
        RowEnqueuedTimeUtc = classification.Row.EnqueuedTimeUtc,
        RowUpdatedAt = classification.Row.UpdatedAt,
        Verdict = Enum.Parse<StalePendingRowVerdict>(classification.Verdict.ToString()),
        Detail = classification.Detail,
        ResponseMessageId = classification.Response?.MessageId,
        ResponseEnqueuedTimeUtc = classification.Response?.EnqueuedTimeUtc,
    };

    private static int NormalizeMaxRows(int maxRows) =>
        Math.Clamp(maxRows <= 0 ? DefaultStalePendingMaxRows : maxRows, 1, MaxStalePendingRows);
}
