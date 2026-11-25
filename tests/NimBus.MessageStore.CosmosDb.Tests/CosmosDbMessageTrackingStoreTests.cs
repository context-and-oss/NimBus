#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class CosmosDbMessageTrackingStoreTests : MessageTrackingStoreConformanceTests
{
    protected override IMessageTrackingStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();

    // Cosmos DB expires data by TTL; PurgeExpiredData returns zeros.
    protected override bool DeletesExpiredDataByAge => false;
}
