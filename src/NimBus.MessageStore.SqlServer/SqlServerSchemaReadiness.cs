using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Signals that <see cref="SqlServerSchemaInitializer"/> has created or verified the schema,
/// so background work such as <see cref="SqlServerRetentionService"/> never queries tables
/// before they exist — even when hosted services start concurrently.
/// </summary>
internal sealed class SqlServerSchemaReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Marks the schema as ready. Idempotent.</summary>
    public void MarkReady() => _ready.TrySetResult();

    /// <summary>Completes once the schema is ready, or is cancelled with <paramref name="cancellationToken"/>.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);
}
