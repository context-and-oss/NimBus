using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// The operator operation journal (Spec 044 Phase 2): a durable record of every operator
/// command sent over MCP, so a retry with the same idempotency key never runs twice, a crash
/// between claim and publish is repaired, the outcome can be read later, and agent budgets
/// and per-message caps hold across instances and restarts. A separate optional capability
/// (ADR-018): without a registered implementation the WebApp keeps the Phase 2a behaviour and
/// unattended resubmits are unavailable.
/// </summary>
public interface IOperatorOperationStore
{
    /// <summary>
    /// Stores <paramref name="operation"/> unless an operation with the same
    /// <see cref="OperatorOperation.OperationId"/> exists, in which case that one is returned.
    /// The store sets <see cref="OperatorOperation.Version"/>.
    /// </summary>
    /// <param name="operation">The new operation.</param>
    Task<OperatorOperationCreateResult> TryCreateOperatorOperation(OperatorOperation operation);

    /// <summary>The operation, or null when it does not exist or was purged.</summary>
    /// <param name="operationId">The operation id.</param>
    Task<OperatorOperation?> GetOperatorOperation(string operationId);

    /// <summary>
    /// Replaces the stored operation when its version still equals
    /// <paramref name="expectedVersion"/>; on success <paramref name="operation"/> carries the
    /// new version.
    /// </summary>
    /// <param name="operation">The changed operation.</param>
    /// <param name="expectedVersion">The version the caller read.</param>
    /// <returns>False when the operation changed or no longer exists; nothing is written.</returns>
    Task<bool> TryUpdateOperatorOperation(OperatorOperation operation, string expectedVersion);

    /// <summary>
    /// Operations in <paramref name="statuses"/> last updated before
    /// <paramref name="updatedBeforeUtc"/>, oldest first, at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="statuses">Statuses to list.</param>
    /// <param name="updatedBeforeUtc">Only operations updated before this time.</param>
    /// <param name="limit">The most operations to return.</param>
    Task<IReadOnlyList<OperatorOperation>> ListDueOperatorOperations(IReadOnlyCollection<OperatorOperationStatus> statuses, DateTime updatedBeforeUtc, int limit);

    /// <summary>The number of operations matching <paramref name="query"/>.</summary>
    /// <param name="query">The filter.</param>
    Task<int> CountOperatorOperations(OperatorOperationQuery query);

    /// <summary>Deletes up to <paramref name="limit"/> operations that expired before <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="limit">The most operations to delete.</param>
    /// <returns>The number deleted.</returns>
    Task<int> PurgeExpiredOperatorOperations(DateTime nowUtc, int limit);
}
