using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.Operations;

/// <summary>
/// The operator operation journal (Spec 044 Phase 2, from Spec 035 Phase 2b). It derives
/// operation ids from the caller's idempotency key, tracks a command's progress through the
/// coordinator, counts operations for agent budgets and per-message caps, and reconciles an
/// operation against the Resolver's history: the stored command message (published under the
/// operation id) is the receipt, and the response whose <c>ParentMessageId</c> names it is the
/// outcome. It never republishes a command.
/// </summary>
public sealed partial class OperatorOperationJournal
{
    /// <summary>How long operations are kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>How long a request holds its operation before the reconciler may repair it.</summary>
    public static readonly TimeSpan RequestLease = TimeSpan.FromMinutes(2);

    /// <summary>How long a possibly-sent command is looked for before its row is restored for a person.</summary>
    public static readonly TimeSpan PublishGrace = TimeSpan.FromMinutes(60);

    /// <summary>How long a dispatched command's response is looked for.</summary>
    public static readonly TimeSpan ObserveWindow = TimeSpan.FromHours(24);

    /// <summary>The auditor recorded for repairs the reconciler makes.</summary>
    public const string ReconcilerAuditor = "NimBus operation reconciler";

    private readonly IOperatorOperationStore? _operations;
    private readonly IMessageTrackingStore _messages;
    private readonly IManagerClient _manager;
    private readonly TimeProvider _time;
    private readonly ILogger<OperatorOperationJournal> _logger;

    /// <summary>Creates the journal; <paramref name="operations"/> is the optional store (ADR-018).</summary>
    public OperatorOperationJournal(
        IEnumerable<IOperatorOperationStore> operations,
        IMessageTrackingStore messages,
        IManagerClient manager,
        ILogger<OperatorOperationJournal> logger,
        TimeProvider? time = null)
    {
        // Last, like GetService: a later registration replaces an earlier one.
        _operations = operations.LastOrDefault();
        _messages = messages;
        _manager = manager;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether commands can be journaled: a journal store is registered and the Manager can
    /// publish under a chosen message id.
    /// </summary>
    public bool IsAvailable => _operations is not null && _manager is IManagerCommandClient;

    /// <summary>The current time.</summary>
    public DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    private IOperatorOperationStore Store => _operations
        ?? throw new InvalidOperationException("No operator operation store is registered.");

    /// <summary>The operation id for a caller's idempotency key: 32 hex characters of SHA-256.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="idempotencyKey">The caller's key.</param>
    public static string OperationIdFor(string? tenantId, string caller, string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{caller}\n{idempotencyKey}"));
        return Convert.ToHexStringLower(bytes)[..32];
    }

    /// <summary>The hash a retry's arguments must match.</summary>
    /// <param name="action">The command.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="eventId">The event.</param>
    /// <param name="messageVersion">The previewed version.</param>
    /// <param name="reason">The reason.</param>
    public static string ArgumentsHashFor(OperatorOperationAction action, string endpointId, string eventId, string messageVersion, string reason)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{action}\n{endpointId}\n{eventId}\n{messageVersion}\n{reason}")));

    /// <summary>The stored operation, or null.</summary>
    /// <param name="operationId">The operation id.</param>
    public Task<OperatorOperation?> GetAsync(string operationId) => Store.GetOperatorOperation(operationId);

    /// <summary>Stores <paramref name="operation"/> as <see cref="OperatorOperationStatus.Accepted"/>, or returns the existing one.</summary>
    /// <param name="operation">The new operation, without timestamps.</param>
    public Task<OperatorOperationCreateResult> CreateAsync(OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var now = UtcNow;
        operation.Status = OperatorOperationStatus.Accepted;
        operation.CreatedAtUtc = now;
        operation.UpdatedAtUtc = now;
        operation.LeaseUntilUtc = now + RequestLease;
        operation.ExpiresAtUtc = now + Retention;
        operation.TimeToLiveSeconds = (int)Retention.TotalSeconds;
        return Store.TryCreateOperatorOperation(operation);
    }

    /// <summary>Counts operations.</summary>
    /// <param name="query">The filter.</param>
    public Task<int> CountAsync(OperatorOperationQuery query) => Store.CountOperatorOperations(query);

    /// <summary>Marks an operation refused before anything changed.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="detail">Why.</param>
    public Task RefuseAsync(OperatorOperation operation, string detail)
        => ChangeAsync(operation, o =>
        {
            if (o.Status != OperatorOperationStatus.Accepted)
                return false;
            o.Status = OperatorOperationStatus.Refused;
            o.Detail = detail;
            o.LeaseUntilUtc = null;
            return true;
        });

    /// <summary>A progress tracker for the coordinator, bound to <paramref name="operation"/>.</summary>
    /// <param name="operation">The operation the command runs for.</param>
    public IOperatorCommandJournal Track(OperatorOperation operation) => new Tracker(this, operation);

    /// <summary>
    /// Brings <paramref name="operation"/> up to date with the Resolver's history. With
    /// <paramref name="repair"/>, an operation whose request lease ran out is also repaired: a
    /// command that never left has its row restored and becomes <see cref="OperatorOperationStatus.NotSent"/>,
    /// and one that may have left and is still unseen after <see cref="PublishGrace"/> has its
    /// row restored for a person and stays <see cref="OperatorOperationStatus.OutcomeUnknown"/>.
    /// Never republishes.
    /// </summary>
    /// <param name="operation">The stored operation.</param>
    /// <param name="repair">Whether to repair operations a crashed request left behind.</param>
    /// <param name="audit">Records repairs; required when <paramref name="repair"/> is true.</param>
    /// <returns>The operation as stored afterwards.</returns>
    public async Task<OperatorOperation> ReconcileAsync(OperatorOperation operation, bool repair, IAuditLogService? audit = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var now = UtcNow;
        var leaseExpired = operation.LeaseUntilUtc is null || operation.LeaseUntilUtc.Value <= now;

        switch (operation.Status)
        {
            case OperatorOperationStatus.Accepted or OperatorOperationStatus.Claimed when repair && leaseExpired:
                // Nothing was published: the coordinator records Publishing before it sends.
                await RestoreRowAsync(operation).ConfigureAwait(false);
                await AuditRepairAsync(audit, operation, "Never published; the row was restored.").ConfigureAwait(false);
                return await ChangeAsync(operation, o =>
                {
                    if (o.Status is not (OperatorOperationStatus.Accepted or OperatorOperationStatus.Claimed))
                        return false;
                    o.Status = OperatorOperationStatus.NotSent;
                    o.Detail = "The request stopped before the command was published; the message was restored.";
                    o.LeaseUntilUtc = null;
                    return true;
                }).ConfigureAwait(false);

            case OperatorOperationStatus.Publishing or OperatorOperationStatus.OutcomeUnknown:
            {
                if (operation.Status == OperatorOperationStatus.Publishing && !leaseExpired)
                    return operation;

                var command = await _messages.GetMessage(operation.EventId, operation.OperationId).ConfigureAwait(false);
                if (command is not null)
                {
                    operation = await ChangeAsync(operation, o =>
                    {
                        if (o.Status is not (OperatorOperationStatus.Publishing or OperatorOperationStatus.OutcomeUnknown))
                            return false;
                        o.Status = OperatorOperationStatus.Dispatched;
                        o.DispatchedAtUtc = command.EnqueuedTimeUtc == default ? now : command.EnqueuedTimeUtc;
                        o.Detail = null;
                        o.LeaseUntilUtc = null;
                        return true;
                    }).ConfigureAwait(false);
                    return await ObserveAsync(operation, now).ConfigureAwait(false);
                }

                if (repair && now - operation.CreatedAtUtc >= PublishGrace && operation.Status == OperatorOperationStatus.Publishing)
                {
                    await RestoreRowAsync(operation).ConfigureAwait(false);
                    await AuditRepairAsync(audit, operation, "Possibly published but not seen by the Resolver; the row was restored for a person.").ConfigureAwait(false);
                    return await ChangeAsync(operation, o =>
                    {
                        if (o.Status != OperatorOperationStatus.Publishing)
                            return false;
                        o.Status = OperatorOperationStatus.OutcomeUnknown;
                        o.Detail = "The request stopped while publishing and the Resolver has not seen the command. The message was restored; a person decides.";
                        o.LeaseUntilUtc = null;
                        return true;
                    }).ConfigureAwait(false);
                }

                return repair ? await ParkAsync(operation, now).ConfigureAwait(false) : operation;
            }

            case OperatorOperationStatus.Dispatched:
            {
                var observed = await ObserveAsync(operation, now).ConfigureAwait(false);
                return repair && observed.Status == OperatorOperationStatus.Dispatched
                    ? await ParkAsync(observed, now).ConfigureAwait(false)
                    : observed;
            }

            default:
                return operation;
        }
    }

    /// <summary>
    /// The statuses the reconciler visits: non-terminal ones. <see cref="OperatorOperationStatus.Refused"/>,
    /// <see cref="OperatorOperationStatus.NotSent"/>, Succeeded and Failed never change again.
    /// </summary>
    public static readonly IReadOnlyCollection<OperatorOperationStatus> DueStatuses =
    [
        OperatorOperationStatus.Accepted,
        OperatorOperationStatus.Claimed,
        OperatorOperationStatus.Publishing,
        OperatorOperationStatus.OutcomeUnknown,
        OperatorOperationStatus.Dispatched,
    ];

    /// <summary>Lists due operations for the reconciler.</summary>
    /// <param name="limit">The most to return.</param>
    public Task<IReadOnlyList<OperatorOperation>> ListDueAsync(int limit)
        => Store.ListDueOperatorOperations(DueStatuses, UtcNow - TimeSpan.FromSeconds(15), limit);

    /// <summary>Deletes expired operations.</summary>
    /// <param name="limit">The most to delete.</param>
    public Task<int> PurgeAsync(int limit) => Store.PurgeExpiredOperatorOperations(UtcNow, limit);

    // A Dispatched or unresolved operation that was checked and did not change is pushed back so
    // the due query (oldest UpdatedAtUtc first) reaches the others; past the observe window it is
    // checked once a day until it expires.
    private Task<OperatorOperation> ParkAsync(OperatorOperation operation, DateTime now)
        => ChangeAsync(operation, o =>
        {
            if (o.IsTerminal)
                return false;
            o.UpdatedAtUtc = now - o.CreatedAtUtc > ObserveWindow ? now + TimeSpan.FromDays(1) : now;
            return true;
        });

    private async Task<OperatorOperation> ObserveAsync(OperatorOperation operation, DateTime now)
    {
        if (operation.Status != OperatorOperationStatus.Dispatched)
            return operation;

        var history = await _messages.GetEventHistory(operation.EventId).ConfigureAwait(false);
        var response = history
            .Where(m => string.Equals(m.ParentMessageId, operation.OperationId, StringComparison.Ordinal) && IsResponse(m.MessageType))
            .OrderBy(m => m.EnqueuedTimeUtc)
            .LastOrDefault();
        if (response is null)
            return operation;

        var outcome = Outcome(response.MessageType);
        return await ChangeAsync(operation, o =>
        {
            if (o.Status != OperatorOperationStatus.Dispatched)
                return false;
            if (o.ObservedStatus == response.MessageType.ToString() && outcome is null)
                return false;
            o.ObservedStatus = response.MessageType.ToString();
            o.EvidenceMessageId = response.MessageId;
            o.ObservedAtUtc = now;
            if (outcome is { } status)
                o.Status = status;
            return true;
        }).ConfigureAwait(false);
    }

    private static bool IsResponse(MessageType type) => type is MessageType.ResolutionResponse
        or MessageType.SkipResponse
        or MessageType.ErrorResponse
        or MessageType.UnsupportedResponse
        or MessageType.PendingHandoffResponse;

    // A pending hand-off is not an outcome yet: the external work is in flight.
    private static OperatorOperationStatus? Outcome(MessageType type) => type switch
    {
        MessageType.ResolutionResponse or MessageType.SkipResponse => OperatorOperationStatus.Succeeded,
        MessageType.ErrorResponse or MessageType.UnsupportedResponse => OperatorOperationStatus.Failed,
        _ => null,
    };

    private async Task RestoreRowAsync(OperatorOperation operation)
    {
        if (!OperatorMessageVersion.TryDecode(operation.MessageVersion, out var version) || version is null)
            return;

        try
        {
            // Guarded: restores only a row still archived at the version the operation claimed.
            await _messages.TryRestoreArchivedEvent(operation.EventId, version.SessionId!, operation.EndpointId,
                version.Status, version.LastMessageId, version.UpdatedAt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRestoreFailed(_logger, ex, operation.OperationId, operation.EventId, operation.EndpointId);
        }
    }

    private async Task AuditRepairAsync(IAuditLogService? audit, OperatorOperation operation, string what)
    {
        if (audit is null)
            return;

        try
        {
            await audit.LogAuditAsync(MessageAuditType.CommandNotSent, new DefaultHttpContext(),
                data: JsonConvert.SerializeObject(new
                {
                    command = operation.Action.ToString(),
                    operationId = operation.OperationId,
                    repair = what,
                    channel = OperatorChannel.Mcp.ToString(),
                    clientId = operation.ClientId,
                }),
                eventId: operation.EventId,
                endpointId: operation.EndpointId,
                auditorNameOverride: ReconcilerAuditor).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRepairAuditFailed(_logger, ex, operation.OperationId);
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> with optimistic concurrency, reloading and retrying on a
    /// conflict. <paramref name="change"/> returns false when the reloaded operation no longer
    /// needs it; the stored operation is then returned unchanged.
    /// </summary>
    internal async Task<OperatorOperation> ChangeAsync(OperatorOperation operation, Func<OperatorOperation, bool> change)
    {
        var current = operation;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var copy = current.Clone();
            if (!change(copy))
                return current;

            copy.UpdatedAtUtc = copy.UpdatedAtUtc > UtcNow ? copy.UpdatedAtUtc : UtcNow;
            if (await Store.TryUpdateOperatorOperation(copy, current.Version).ConfigureAwait(false))
                return copy;

            current = await Store.GetOperatorOperation(operation.OperationId).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Operation '{operation.OperationId}' disappeared while it was being updated.");
        }

        throw new InvalidOperationException($"Operation '{operation.OperationId}' kept changing while it was being updated.");
    }

    private sealed class Tracker(OperatorOperationJournal journal, OperatorOperation operation) : IOperatorCommandJournal
    {
        private OperatorOperation _operation = operation;

        public string CommandMessageId => _operation.OperationId;

        public string OperationId => _operation.OperationId;

        public OperatorOperation Current => _operation;

        public Task ClaimedAsync() => Step(OperatorOperationStatus.Accepted, OperatorOperationStatus.Claimed);

        public Task PublishingAsync() => Step(OperatorOperationStatus.Claimed, OperatorOperationStatus.Publishing);

        public async Task DispatchedAsync()
        {
            var now = journal.UtcNow;
            _operation = await journal.ChangeAsync(_operation, o =>
            {
                if (o.Status != OperatorOperationStatus.Publishing)
                    return false;
                o.Status = OperatorOperationStatus.Dispatched;
                o.DispatchedAtUtc = now;
                o.LeaseUntilUtc = null;
                return true;
            }).ConfigureAwait(false);
        }

        public async Task NotRunAsync(OperatorCommandStatus status, string? detail)
        {
            _operation = await journal.ChangeAsync(_operation, o =>
            {
                if (o.Status is not (OperatorOperationStatus.Accepted or OperatorOperationStatus.Claimed))
                    return false;
                o.Status = OperatorOperationStatus.Refused;
                o.Detail = detail is null ? status.ToString() : $"{status}: {detail}";
                o.LeaseUntilUtc = null;
                return true;
            }).ConfigureAwait(false);
        }

        public async Task PublishFailedAsync(Exception exception)
        {
            _operation = await journal.ChangeAsync(_operation, o =>
            {
                if (o.Status != OperatorOperationStatus.Publishing)
                    return false;
                o.Status = OperatorOperationStatus.OutcomeUnknown;
                o.Detail = $"Sending failed ({exception.GetType().Name}); the message was restored, but the command may have reached Service Bus.";
                o.LeaseUntilUtc = null;
                return true;
            }).ConfigureAwait(false);
        }

        private async Task Step(OperatorOperationStatus from, OperatorOperationStatus to)
        {
            var updated = await journal.ChangeAsync(_operation, o =>
            {
                if (o.Status != from)
                    return false;
                o.Status = to;
                return true;
            }).ConfigureAwait(false);
            if (updated.Status != to)
                throw new InvalidOperationException($"Operation '{_operation.OperationId}' is {updated.Status}, not {from}; the command stops.");
            _operation = updated;
        }
    }

    /// <summary>The operation as the tracker last stored it.</summary>
    /// <param name="tracker">A tracker from <see cref="Track"/>.</param>
    public static OperatorOperation CurrentOf(IOperatorCommandJournal tracker)
        => tracker is Tracker t ? t.Current : throw new ArgumentException("Not a journal tracker.", nameof(tracker));

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring event {EventId} on {EndpointId} for operation {OperationId} failed")]
    private static partial void LogRestoreFailed(ILogger logger, Exception exception, string operationId, string eventId, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not audit the repair of operation {OperationId}")]
    private static partial void LogRepairAuditFailed(ILogger logger, Exception exception, string operationId);
}
