using Microsoft.Data.SqlClient;
using System;
using System.Threading;

namespace NimBus.Outbox.SqlServer;

/// <summary>
/// Ambient SqlConnection/SqlTransaction context used by <see cref="SqlServerOutbox"/>.
/// When set, outbox writes are issued on the supplied connection and enlisted in the
/// supplied transaction instead of opening a new connection. This lets callers share
/// a single physical SqlConnection between EF (or other ADO.NET work) and the outbox,
/// keeping the entity write and outbox row commit truly atomic without escalating
/// to MSDTC.
/// </summary>
public static class SqlServerOutboxAmbientTransaction
{
    private static readonly AsyncLocal<AmbientState?> _current = new AsyncLocal<AmbientState?>();

    /// <summary>
    /// Active ambient connection and transaction, or null when none is set.
    /// </summary>
    public static (SqlConnection Connection, SqlTransaction Transaction)? Current
    {
        get
        {
            var state = _current.Value;
            return state is null ? null : (state.Connection, state.Transaction);
        }
    }

    /// <summary>
    /// Sets the ambient connection/transaction for the current async flow. Disposing
    /// the returned scope clears it. Nested calls are not supported and will throw.
    /// </summary>
    public static IDisposable Begin(SqlConnection connection, SqlTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (_current.Value is not null)
            throw new InvalidOperationException("A SqlServerOutbox ambient transaction is already active on this async flow.");

        var state = new AmbientState(connection, transaction);
        _current.Value = state;
        return state;
    }

    private sealed class AmbientState : IDisposable
    {
        public AmbientState(SqlConnection connection, SqlTransaction transaction)
        {
            Connection = connection;
            Transaction = transaction;
        }

        public SqlConnection Connection { get; }
        public SqlTransaction Transaction { get; }

        public void Dispose()
        {
            if (ReferenceEquals(_current.Value, this))
                _current.Value = null;
        }
    }
}
