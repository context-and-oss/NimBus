using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox;

/// <summary>
/// Optional companion to <see cref="IOutbox"/> that lets several dispatcher replicas share one
/// outbox safely and keeps a permanently failing row from stalling other sessions.
/// </summary>
/// <remarks>
/// <para>
/// When the registered <see cref="IOutbox"/> also implements this interface,
/// <see cref="OutboxDispatcher"/> claims rows with <see cref="ClaimPendingAsync"/> instead of
/// reading them with <see cref="IOutbox.GetPendingAsync"/>, reports failed sends with
/// <see cref="RecordDispatchFailureAsync"/> (retry backoff and parking), and releases rows it
/// did not attempt with <see cref="ReleaseClaimsAsync"/>.
/// </para>
/// <para>
/// Implementations evaluate every lease and retry time against their own clock: callers pass
/// durations, never timestamps, so replicas with skewed clocks agree on expiry. Provider authors
/// can execute this contract by deriving a test fixture from
/// <c>NimBus.Testing.Conformance.OutboxClaimingConformanceTests</c>.
/// </para>
/// </remarks>
public interface IOutboxClaiming
{
    /// <summary>
    /// Atomically leases up to <paramref name="batchSize"/> dispatchable rows to
    /// <paramref name="claimantId"/> and returns them in dispatch order (the order
    /// <see cref="IOutbox.GetPendingAsync"/> documents).
    /// </summary>
    /// <remarks>
    /// A row is claimable when it is not dispatched, not parked, its retry time has passed and
    /// it has no unexpired lease, and no other undispatched row with the same
    /// <see cref="OutboxMessage.SessionId"/> is parked, waiting for a retry or under an unexpired
    /// lease. Rows without a session are judged on their own. A blocked session therefore
    /// contributes no rows to the batch, and its later rows are never handed out ahead of the
    /// row that blocks it. Concurrent callers never receive the same row while its lease is valid.
    /// Returned rows carry their <see cref="OutboxMessage.AttemptCount"/>.
    /// </remarks>
    /// <param name="batchSize">Maximum number of rows to claim.</param>
    /// <param name="claimantId">Stable identifier of the dispatcher instance claiming the rows.</param>
    /// <param name="leaseDuration">How long the claim stays valid on the store's clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The claimed rows, oldest first.</returns>
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        string claimantId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed send of a row claimed by <paramref name="claimantId"/>: increments its
    /// attempt count, stores <paramref name="error"/>, releases the lease and either schedules
    /// the next attempt after <paramref name="retryDelay"/> or, when <paramref name="park"/> is
    /// <c>true</c>, parks the row so it is not claimed again until an operator requeues it.
    /// </summary>
    /// <remarks>
    /// The report is ignored when the row is already dispatched or is no longer leased to
    /// <paramref name="claimantId"/> (its lease expired and another dispatcher claimed it).
    /// </remarks>
    /// <param name="id">The outbox row identifier.</param>
    /// <param name="claimantId">The dispatcher instance that holds the row's lease.</param>
    /// <param name="error">A description of the failure; implementations may truncate it.</param>
    /// <param name="retryDelay">Delay, on the store's clock, before the row may be claimed again.</param>
    /// <param name="park">Whether to park the row instead of scheduling a retry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordDispatchFailureAsync(
        string id,
        string claimantId,
        string error,
        TimeSpan retryDelay,
        bool park,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the leases <paramref name="claimantId"/> holds on <paramref name="ids"/> without
    /// counting an attempt, so the rows can be claimed again immediately. Rows that are
    /// dispatched or leased to another claimant are left unchanged.
    /// </summary>
    /// <param name="claimantId">The dispatcher instance that holds the leases.</param>
    /// <param name="ids">The outbox row identifiers to release.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReleaseClaimsAsync(
        string claimantId,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default);
}
