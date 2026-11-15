using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <summary>How a tracking-only skip of a Deferred row ended.</summary>
public enum DeferredTrackingSkipStatus
{
    /// <summary>The row is now Skipped. Nothing was removed from the broker.</summary>
    Skipped,

    /// <summary>No such tracking row.</summary>
    NotFound,

    /// <summary>The row changed since it was inspected; nothing was written.</summary>
    Changed,

    /// <summary>The repeated inspection does not allow the skip; see the detail.</summary>
    NotSkippable,

    /// <summary>The storage provider has no conditional Deferred-to-Skipped write.</summary>
    NotSupported,
}

/// <summary>The outcome of <see cref="DeferredTrackingRecovery.SkipAsync"/>.</summary>
/// <param name="Status">How the skip ended.</param>
/// <param name="AuditRecorded">
/// Whether the audit row was written. Reported separately: the state change has already
/// happened and must not be presented as a failed skip when the audit sink fails.
/// </param>
/// <param name="Detail">Why the inspection does not allow the skip, when it does not.</param>
public sealed record DeferredTrackingSkipOutcome(DeferredTrackingSkipStatus Status, bool AuditRecorded = false, string? Detail = null);

/// <summary>
/// Inspects a Deferred tracking row against the broker and, when it is stale, marks only the
/// tracking row Skipped (see <c>docs/plan/deferred-message-recovery.md</c>). Shared by the REST
/// API and the operator MCP tools (Spec 035 Phase 3) so both apply the same checks: the
/// inspected row version, a repeated inspection on submission, and a conditional write. It
/// publishes nothing, clears no session state and deletes no history. Callers check roles.
/// </summary>
public sealed partial class DeferredTrackingRecovery
{
    private const string ChangedDetail = "The tracking record changed. Check it again.";

    private readonly IMessageTrackingStore _store;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ServiceBusAdministrationClient? _administrationClient;
    private readonly ILogger _logger;

    /// <summary>Creates the recovery over the tracking store and the broker clients it peeks with.</summary>
    public DeferredTrackingRecovery(
        IMessageTrackingStore store,
        ServiceBusClient serviceBusClient,
        ServiceBusAdministrationClient? administrationClient,
        ILogger logger)
    {
        _store = store;
        _serviceBusClient = serviceBusClient;
        _administrationClient = administrationClient;
        _logger = logger;
    }

    /// <summary>The tracking row, or null when the event or its endpoint is unknown.</summary>
    public async Task<UnresolvedEvent?> FindRowAsync(string endpointId, string eventId)
    {
        try
        {
            return await _store.GetEvent(endpointId, eventId);
        }
        catch (EndpointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Peeks the broker and reads the stored history for <paramref name="row"/>; changes nothing.</summary>
    public async Task<DeferredInspection> InspectAsync(UnresolvedEvent row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return await new DeferredMessageInspector(_serviceBusClient, _administrationClient)
            .InspectAsync(row, await _store.GetEventHistory(row.EventId), cancellationToken);
    }

    /// <summary>
    /// Marks the row Skipped when it still is the inspected <paramref name="rowVersion"/> and a
    /// repeated inspection allows it, then records the audit row.
    /// </summary>
    /// <param name="endpointId">The endpoint, in the platform's casing.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="rowVersion">The version the caller inspected (<see cref="DeferredMessageInspector.RowVersion"/>).</param>
    /// <param name="reason">The operator's reason, recorded as the audit comment.</param>
    /// <param name="auditorName">Who skipped it.</param>
    /// <param name="origin">Extra audit data naming where the command came from, or null for the Web UI.</param>
    /// <param name="cancellationToken">Cancels before the write; never between the write and the audit.</param>
    public async Task<DeferredTrackingSkipOutcome> SkipAsync(
        string endpointId,
        string eventId,
        string rowVersion,
        string reason,
        string auditorName,
        IReadOnlyDictionary<string, object?>? origin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        var row = await FindRowAsync(endpointId, eventId);
        if (row is null)
            return new(DeferredTrackingSkipStatus.NotFound);
        if (!string.Equals(rowVersion, DeferredMessageInspector.RowVersion(row), StringComparison.Ordinal))
            return new(DeferredTrackingSkipStatus.Changed, Detail: ChangedDetail);

        var expectedMessageId = row.LastMessageId;
        var expectedUpdatedAt = row.UpdatedAt;
        var inspection = await InspectAsync(row, cancellationToken);
        if (!inspection.CanSkip)
            return new(DeferredTrackingSkipStatus.NotSkippable, Detail: inspection.SkipDetail);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await _store.TrySkipDeferredMessage(eventId, row.SessionId!, endpointId, expectedMessageId, expectedUpdatedAt))
                return new(DeferredTrackingSkipStatus.Changed, Detail: ChangedDetail);
        }
        catch (NotSupportedException)
        {
            return new(DeferredTrackingSkipStatus.NotSupported);
        }

        // Report audit persistence separately: the state transition already succeeded and
        // must not be presented as a failed skip (or silently retried) if the audit sink fails.
        var auditRecorded = false;
        try
        {
            await _store.StoreMessageAudit(eventId, new MessageAuditEntity
            {
                AuditType = MessageAuditType.Skip, AuditTimestamp = DateTime.UtcNow,
                AuditorName = auditorName,
                EventId = eventId, EndpointId = endpointId, Comment = reason.Trim(),
                Data = Describe(rowVersion, inspection, origin),
            }, endpointId, row.EventTypeId);
            auditRecorded = true;
        }
        catch (Exception exception)
        {
            LogDeferredTrackingWasSkippedButAudit(_logger, exception, endpointId, eventId);
        }

        LogDeferredTrackingSkipped(_logger, endpointId, eventId, auditRecorded);
        return new(DeferredTrackingSkipStatus.Skipped, auditRecorded);
    }

    private static string Describe(string rowVersion, DeferredInspection inspection, IReadOnlyDictionary<string, object?>? origin)
    {
        var data = new
        {
            action = "skip-deferred-tracking", previousStatus = "Deferred", newStatus = "Skipped",
            inspectedVersion = rowVersion, inspection.HistoryOutcome, inspection.TerminalMessageId,
            inspection.TerminalTime, inspection.HasLaterAttempt,
            brokerChecks = inspection.BrokerChecks.Select(c => new { c.Location, status = c.Status.ToString(), c.Scanned }),
        };
        if (origin is null || origin.Count == 0)
            return JsonConvert.SerializeObject(data);

        var json = JObject.FromObject(data);
        foreach (var (name, value) in origin)
            json[name] = value is null ? JValue.CreateNull() : JToken.FromObject(value);
        return json.ToString(Formatting.None);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deferred tracking was skipped but audit failed for {EndpointId}/{EventId}")]
    private static partial void LogDeferredTrackingWasSkippedButAudit(ILogger logger, Exception exception, string endpointId, string eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deferred tracking skipped for {EndpointId}/{EventId}; audit recorded: {AuditRecorded}")]
    private static partial void LogDeferredTrackingSkipped(ILogger logger, string endpointId, string eventId, bool auditRecorded);
}
