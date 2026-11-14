using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK;

/// <summary>
/// Audit-row coordinates needed to settle a pending handoff. Adapters already
/// persist these alongside their <c>ExternalJobId</c> when they register the
/// external work (see the CrmErpDemo's <c>HandoffJob</c> table for the
/// canonical shape), so the typed record lets the adapter hand them back to
/// NimBus without re-deriving anything.
///
/// <para>Replaces the <c>MessageEntity</c> bag-of-fields the original
/// <c>IManagerClient.CompleteHandoff(MessageEntity, …)</c> API required (removed in 3.0) —
/// same six values, named for what they are rather than carried inside a
/// row-shaped DTO. <c>EventId</c>, <c>SessionId</c> and <c>MessageId</c> are
/// required (they address the pending row and route the control message into
/// its session); the lineage fields (<c>CorrelationId</c>,
/// <c>OriginatingMessageId</c>, <c>EventTypeId</c>) may be null on legacy
/// rows — the wire falls back <c>OriginatingMessageId ?? MessageId</c>.</para>
/// </summary>
public sealed record HandoffSettlement(
    string EventId,
    string SessionId,
    string MessageId,
    string EventTypeId,
    string? CorrelationId,
    string? OriginatingMessageId);

/// <summary>
/// Adapter-facing API for settling a pending handoff initiated by
/// <c>IEventHandlerContext.MarkPendingHandoff</c>. Adapters call
/// <see cref="CompleteAsync"/> when the external work succeeds, or
/// <see cref="FailAsync"/> when it doesn't.
///
/// <para>The user-supplied handler is NOT re-invoked on settlement — settlement
/// is a Pending → Completed/Failed audit transition only. If business state
/// needs to change as part of a successful handoff (the canonical example is
/// the ERP customer write in the CrmErpDemo sample), the settlement code has
/// to do that itself before calling <see cref="CompleteAsync"/>.</para>
///
/// <para>Registered automatically by <c>AddNimBusSubscriber</c> (handler code
/// can inject it directly), and standalone by
/// <c>AddNimBusHandoffClient(endpoint)</c> for settlement-only processes that
/// don't host any subscribers themselves.</para>
/// </summary>
public interface IHandoffClient
{
    /// <summary>
    /// Drive Pending → Completed for the pending-handoff row identified by
    /// <paramref name="coords"/>.
    /// </summary>
    /// <param name="coords">Audit-row coordinates the adapter persisted alongside its external job id.</param>
    /// <param name="result">Optional completion payload. Strings pass through verbatim; other types are serialised to JSON via Newtonsoft so they land in <c>EventContent.EventJson</c>.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    Task CompleteAsync(
        HandoffSettlement coords,
        object? result = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drive Pending → Failed for the pending-handoff row identified by
    /// <paramref name="coords"/>.
    /// </summary>
    /// <param name="coords">Audit-row coordinates the adapter persisted alongside its external job id.</param>
    /// <param name="errorText">Human-readable error description. Surfaces verbatim on the resulting Failed audit row.</param>
    /// <param name="errorType">Optional logical error classifier (e.g. <c>"DmfValidationError"</c>). Useful for grouping / alerting downstream.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    Task FailAsync(
        HandoffSettlement coords,
        string errorText,
        string? errorType = null,
        CancellationToken cancellationToken = default);
}
