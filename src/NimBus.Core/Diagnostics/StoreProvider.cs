namespace NimBus.Core.Diagnostics;

/// <summary>
/// Values for the <c>nimbus.store.provider</c> attribute emitted by the
/// instrumenting message-store decorator. Storage-provider extensions pass the
/// matching constant to
/// <c>NimBus.OpenTelemetry.NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore</c>
/// so the tag value is consistent across the codebase.
/// </summary>
public static class StoreProvider
{
    /// <summary>Cosmos DB-backed message store (NimBus.MessageStore.CosmosDb).</summary>
    public const string Cosmos = "cosmos";

    /// <summary>SQL Server-backed message store (NimBus.MessageStore.SqlServer).</summary>
    public const string SqlServer = "sqlserver";

    /// <summary>In-memory message store (NimBus.Testing — tests only).</summary>
    public const string InMemory = "inmemory";
}
