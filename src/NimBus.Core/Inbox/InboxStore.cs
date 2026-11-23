namespace NimBus.Core.Inbox;

/// <summary>
/// Identifies a keyed inbox-store provider registration.
/// </summary>
public enum InboxStore
{
    /// <summary>A Cosmos DB inbox provider.</summary>
    Cosmos = 0,

    /// <summary>A SQL Server inbox provider.</summary>
    SqlServer = 1,

    /// <summary>An in-memory inbox provider intended for tests and local development.</summary>
    InMemory = 2,

    /// <summary>An Azure Table Storage inbox provider.</summary>
    AzureTable = 3,
}
