using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// In-memory <see cref="IOperatorOperationStore"/>. Every read and write copies the
/// operation, so no caller holds a stored instance by reference.
/// </summary>
public class InMemoryOperatorOperationStore : IOperatorOperationStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, OperatorOperation> _operations = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public virtual Task<OperatorOperationCreateResult> TryCreateOperatorOperation(OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.OperationId);
        lock (_lock)
        {
            if (_operations.TryGetValue(operation.OperationId, out var existing))
                return Task.FromResult(new OperatorOperationCreateResult(false, existing.Clone()));

            var stored = operation.Clone();
            stored.Version = NewVersion();
            _operations[stored.OperationId] = stored;
            operation.Version = stored.Version;
            return Task.FromResult(new OperatorOperationCreateResult(true, stored.Clone()));
        }
    }

    /// <inheritdoc />
    public virtual Task<OperatorOperation?> GetOperatorOperation(string operationId)
    {
        lock (_lock)
        {
            return Task.FromResult(_operations.TryGetValue(operationId, out var stored) ? stored.Clone() : null);
        }
    }

    /// <inheritdoc />
    public virtual Task<bool> TryUpdateOperatorOperation(OperatorOperation operation, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_lock)
        {
            if (!_operations.TryGetValue(operation.OperationId, out var stored) || stored.Version != expectedVersion)
                return Task.FromResult(false);

            var replacement = operation.Clone();
            replacement.Version = NewVersion();
            _operations[replacement.OperationId] = replacement;
            operation.Version = replacement.Version;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<OperatorOperation>> ListDueOperatorOperations(
        IReadOnlyCollection<OperatorOperationStatus> statuses, DateTime updatedBeforeUtc, int limit)
    {
        lock (_lock)
        {
            IReadOnlyList<OperatorOperation> due = _operations.Values
                .Where(o => statuses.Contains(o.Status) && o.UpdatedAtUtc < updatedBeforeUtc)
                .OrderBy(o => o.UpdatedAtUtc)
                .Take(Math.Max(0, limit))
                .Select(o => o.Clone())
                .ToList();
            return Task.FromResult(due);
        }
    }

    /// <inheritdoc />
    public virtual Task<int> CountOperatorOperations(OperatorOperationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_lock)
        {
            return Task.FromResult(_operations.Values.Count(query.Matches));
        }
    }

    /// <inheritdoc />
    public virtual Task<int> PurgeExpiredOperatorOperations(DateTime nowUtc, int limit)
    {
        lock (_lock)
        {
            var expired = _operations.Values
                .Where(o => o.ExpiresAtUtc < nowUtc)
                .OrderBy(o => o.ExpiresAtUtc)
                .Take(Math.Max(0, limit))
                .Select(o => o.OperationId)
                .ToList();
            foreach (var id in expired)
                _operations.Remove(id);
            return Task.FromResult(expired.Count);
        }
    }

    private static string NewVersion() => Guid.NewGuid().ToString("N");
}
