#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// The only test that can prove <see cref="CosmosDatabaseAdapter"/> actually forwards
/// <see cref="ContainerProperties"/> to the SDK: the fake-adapter tests stop at the
/// adapter boundary. Skipped unless a live Cosmos endpoint is configured (see
/// <see cref="CosmosDbStoreTestHarness"/>); required in CI via NIMBUS_COSMOS_TEST_REQUIRED.
/// </summary>
[TestClass]
public sealed class CosmosDbEndpointContainerTtlTests
{
    [TestMethod]
    public async Task Endpoint_container_is_created_with_ttl_enabled_and_the_row_carries_the_configured_ttl()
    {
        var store = CosmosDbStoreTestHarness.CreateStore(
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 1 });

        // A fresh id per run: CreateContainerIfNotExistsAsync never reconciles the
        // properties of a container that already exists, so reusing one would assert
        // nothing about what this change creates.
        var endpointId = $"ttl-test-{Guid.NewGuid():N}";
        var container = CosmosDbStoreTestHarness.RawClient.GetContainer("MessageDatabase", endpointId);

        try
        {
            await store.UploadPendingMessage("event-1", "session-1", endpointId, new UnresolvedEvent
            {
                EventId = "event-1",
                EventTypeId = "event-type-1",
            });

            var properties = (await container.ReadContainerAsync()).Resource;
            Assert.AreEqual(-1, properties.DefaultTimeToLive,
                "Cosmos honours a document ttl only when the container's DefaultTimeToLive is set.");

            // JObject, not System.Text.Json.JsonElement: the Cosmos SDK's default serializer is
            // Newtonsoft-based, and it cannot materialise a JsonElement — it would hand back an
            // Undefined element whose GetProperty("ttl") throws before any assertion ran.
            var document = await container.ReadItemAsync<JObject>(
                "event-1_session-1", new PartitionKey("event-1_session-1"));
            var ttl = document.Resource["ttl"];
            Assert.IsNotNull(ttl, "The stored document carries no ttl property.");
            Assert.AreEqual(86_400, ttl.Value<int>(),
                "One day of retention is 86 400 seconds.");
        }
        finally
        {
            try
            {
                await container.DeleteContainerAsync();
            }
            catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.InternalServerError)
            {
                // Cleanup only: the vNext emulator in CI sometimes fails a container delete with
                // PostgresError 58P01 after the test has passed. The container has a unique name.
            }
        }
    }
}
