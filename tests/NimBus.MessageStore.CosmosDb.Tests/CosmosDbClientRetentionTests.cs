#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// The document <c>ttl</c> the store writes, and the container-level TTL mode it creates
/// containers with. Both are asserted on the wire representation (the serialized document
/// and the captured <see cref="ContainerProperties"/>) because <c>EventDbo</c> is private
/// to <see cref="CosmosDbClient"/>.
/// </summary>
[TestClass]
public sealed class CosmosDbClientRetentionTests
{
    private const int ThirtyDaysInSeconds = 2_592_000;
    private const int OneEightyDaysInSeconds = 15_552_000;

    // ── AC 4: unset options keep today's "TTL disabled" behaviour ──

    [TestMethod]
    [DynamicData(nameof(UnresolvedWrites))]
    public async Task Unresolved_write_defaults_to_ttl_disabled(string name, Func<CosmosDbClient, Task> write)
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await write(client);

        Assert.AreEqual(-1, (int)adapter.Container("endpoint-1").SingleUpsertedDocument()["ttl"]!,
            $"{name} must write ttl = -1 when no retention is configured.");
    }

    // ── AC 5: a configured retention is written as whole days in seconds ──

    [TestMethod]
    [DynamicData(nameof(UnresolvedWrites))]
    public async Task Unresolved_write_stamps_configured_retention(string name, Func<CosmosDbClient, Task> write)
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 365 });

        await write(client);

        Assert.AreEqual(31_536_000, (int)adapter.Container("endpoint-1").SingleUpsertedDocument()["ttl"]!,
            $"{name} must write the configured retention in seconds.");
    }

    [TestMethod]
    public async Task Retention_of_one_day_is_eighty_six_thousand_four_hundred_seconds()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 1 });

        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());

        Assert.AreEqual(86_400, (int)adapter.Container("endpoint-1").SingleUpsertedDocument()["ttl"]!);
    }

    // ── AC 6: the window slides forward on every rewrite of the row ──

    [TestMethod]
    public async Task Retention_slides_forward_on_every_rewrite()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 180 });

        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());
        await client.UploadFailedMessage("event-1", "session-1", "endpoint-1", NewEvent());
        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());

        var container = adapter.Container("endpoint-1");
        Assert.AreEqual(3, container.UpsertedItems.Count, "Each write is a full-document upsert.");
        for (var i = 0; i < 3; i++)
        {
            var doc = container.UpsertedDocument(i);
            Assert.AreEqual("event-1_session-1", (string)doc["id"]!);
            Assert.AreEqual(OneEightyDaysInSeconds, (int)doc["ttl"]!,
                $"Write {i} must re-stamp the retention so the window restarts.");
        }
    }

    // ── AC 7: terminal, soft-delete and archive TTLs are untouched ──

    [TestMethod]
    public async Task Terminal_and_archive_ttls_are_unchanged_under_a_configured_retention()
    {
        // 180 days deliberately, not 30: 30 x 86400 == 2_592_000 is the terminal/archive
        // constant, so a wrongly-coupled path would pass unnoticed at 30.
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 180 });

        await client.UploadCompletedMessage("event-1", "session-1", "endpoint-1", NewEvent());
        await client.UploadSkippedMessage("event-2", "session-1", "endpoint-1", NewEvent());
        await client.UploadPendingMessage("event-3", "session-1", "endpoint-1", NewEvent());
        await client.RemoveMessage("event-1", "session-1", "endpoint-1");
        await client.ArchiveFailedEvent("event-2", "session-1", "endpoint-1");

        var container = adapter.Container("endpoint-1");
        Assert.AreEqual(ThirtyDaysInSeconds, (int)container.UpsertedDocument(0)["ttl"]!, "Completed rows keep 30 days.");
        Assert.AreEqual(ThirtyDaysInSeconds, (int)container.UpsertedDocument(1)["ttl"]!, "Skipped rows keep 30 days.");
        Assert.AreEqual(OneEightyDaysInSeconds, (int)container.UpsertedDocument(2)["ttl"]!,
            "The unresolved row in the same run must use the configured retention — proving the paths differ.");

        AssertPatchSetsTtl(container.CapturedPatches[0], 60, "RemoveMessage keeps its 60-second soft delete.");
        AssertPatchSetsTtl(container.CapturedPatches[1], ThirtyDaysInSeconds, "ArchiveFailedEvent keeps 30 days.");
    }

    // ── AC 8: configuring the option costs no extra I/O ──

    [TestMethod]
    public async Task Configuring_retention_issues_no_extra_reads_or_writes()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 180 });

        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());

        var container = adapter.Container("endpoint-1");
        Assert.AreEqual(1, container.UpsertedItems.Count, "The setting must not add a write.");
        Assert.AreEqual(0, container.CapturedPatches.Count, "The setting must not patch existing documents.");
        Assert.AreEqual(0, container.QueryCount, "The setting must not query existing documents.");
    }

    // ── AC 9: endpoint containers are created with container-level TTL enabled ──

    [TestMethod]
    public async Task Endpoint_containers_are_created_with_container_level_ttl()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());

        var properties = adapter.CreationPropertiesFor("endpoint-1");
        Assert.IsNotNull(properties, "Endpoint containers must be created through the ContainerProperties overload.");
        Assert.AreEqual("endpoint-1", properties.Id);
        Assert.AreEqual("/id", properties.PartitionKeyPath);
        Assert.AreEqual(-1, properties.DefaultTimeToLive);
    }

    [TestMethod]
    public async Task Heartbeat_history_uses_endpoint_partitions_and_item_level_retention()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());
        var now = DateTime.UtcNow;

        await client.UpsertHeartbeatUptimeDays([new HeartbeatUptimeDay
        {
            EndpointId = "endpoint-1",
            DayUtc = now.Date,
            LastBeatUtc = now,
        }]);
        await client.UpsertHeartbeatGaps([
            new HeartbeatGap { EndpointId = "endpoint-1", FromUtc = now.AddMinutes(-10) },
            new HeartbeatGap { EndpointId = "endpoint-1", FromUtc = now.AddMinutes(-20), ToUtc = now },
        ]);

        foreach (var containerId in new[] { "heartbeatuptimedays", "heartbeatgaps" })
        {
            var properties = adapter.CreationPropertiesFor(containerId);
            Assert.IsNotNull(properties);
            Assert.AreEqual("/EndpointId", properties.PartitionKeyPath);
            Assert.AreEqual(-1, properties.DefaultTimeToLive);
        }

        Assert.AreEqual(7_776_000,
            (int)adapter.Container("heartbeatuptimedays").SingleUpsertedDocument()["ttl"]!);
        var gaps = adapter.Container("heartbeatgaps");
        Assert.AreEqual(-1, (int)gaps.UpsertedDocument(0)["ttl"]!, "An open outage must not expire.");
        Assert.AreEqual(7_776_000, (int)gaps.UpsertedDocument(1)["ttl"]!);
        Assert.IsTrue(client.PrunesHeartbeatHistoryAutomatically,
            "The scheduled fold must rely on these TTLs instead of scanning both containers every interval.");
    }

    // ── AC 10: documents that never carried a ttl still do not get one ──

    [TestMethod]
    public async Task Shared_container_documents_keep_their_own_ttl_or_none()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 180 });

        await client.StoreMessage(new MessageEntity { MessageId = "message-1", EventId = "event-1", EndpointId = "endpoint-1" });
        await client.StoreMessageAudit("event-1", new MessageAuditEntity(), "endpoint-1", "event-type-1");
        await client.SetEndpointMetadata(new EndpointMetadata { EndpointId = "endpoint-1" });
        await client.SetEventReport("endpoint-1", "event-1", isReported: true, reportedBy: "someone", ticketId: "T-1");

        Assert.AreEqual(60 * 60 * 24 * 90, (int)adapter.Container("messages").SingleUpsertedDocument()["ttl"]!,
            "Per-message documents keep their 90-day TTL.");
        Assert.AreEqual(60 * 60 * 24 * 365, (int)adapter.Container("audits").SingleUpsertedDocument()["ttl"]!,
            "Audit documents keep their 1-year TTL.");
        Assert.IsFalse(adapter.Container("Metadata").SingleUpsertedDocument().ContainsKey("ttl"),
            "Endpoint metadata never carried a ttl and must not gain one.");
        Assert.IsFalse(adapter.Container("eventreports").SingleUpsertedDocument().ContainsKey("ttl"),
            "Event reports never carried a ttl and must not gain one.");
    }

    // ── AC 11: shared containers keep today's creation path (TTL disabled) ──

    [TestMethod]
    public async Task Shared_containers_are_created_without_container_properties()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await client.GetSubscriptionsOnEndpoint("endpoint-1");
        await client.SetEndpointMetadata(new EndpointMetadata { EndpointId = "endpoint-1" });
        await client.StoreMessage(new MessageEntity { MessageId = "message-1", EventId = "event-1", EndpointId = "endpoint-1" });
        await client.StoreMessageAudit("event-1", new MessageAuditEntity(), "endpoint-1", "event-type-1");
        await client.SetEventReport("endpoint-1", "event-1", isReported: true, reportedBy: "someone", ticketId: "T-1");
        await client.GetSchemas();
        await client.GetEndpointAccessControls();

        foreach (var shared in new[] { "subscriptions", "Metadata", "messages", "audits", "eventreports", "eventschemas", "accesscontrol" })
        {
            Assert.IsTrue(adapter.WasCreated(shared), $"Expected the test to exercise the '{shared}' container.");
            Assert.IsNull(adapter.CreationPropertiesFor(shared),
                $"'{shared}' is not a per-endpoint container and must keep container-level TTL disabled.");
        }
    }

    [TestMethod]
    public async Task Container_ttl_mode_does_not_depend_on_call_order()
    {
        await AssertOrderIndependent(async client =>
        {
            await client.GetSubscriptionsOnEndpoint("endpoint-1");
            await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());
        });

        await AssertOrderIndependent(async client =>
        {
            await client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent());
            await client.GetSubscriptionsOnEndpoint("endpoint-1");
        });
    }

    // ── Reserved endpoint ids ──

    [TestMethod]
    [DataRow("subscriptions")]
    [DataRow("messages")]
    [DataRow("audits")]
    [DataRow("eventschemas")]
    [DataRow("eventreports")]
    [DataRow("accesscontrol")]
    [DataRow("Metadata")]
    [DataRow("inbox")]
    public async Task Endpoint_ids_that_collide_with_a_store_container_are_rejected(string reserved)
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        var ex = await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => client.DownloadEndpointStateCount(reserved));

        StringAssert.Contains(ex.Message, reserved, StringComparison.Ordinal);
        Assert.AreEqual(0, adapter.ContainerCreations.Count,
            "A rejected endpoint id must not create anything.");
    }

    [TestMethod]
    [DataRow("Messages")]
    [DataRow("Subscriptions")]
    public async Task Differently_cased_ids_are_their_own_endpoint_containers(string endpointId)
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await client.UploadPendingMessage("event-1", "session-1", endpointId, NewEvent());

        Assert.AreEqual(-1, adapter.CreationPropertiesFor(endpointId)!.DefaultTimeToLive);
    }

    [TestMethod]
    public async Task The_stores_own_metadata_and_subscription_writes_still_work()
    {
        // Regression for the guard: "Metadata" and "subscriptions" are reserved ids, so the
        // store's own calls must go through the dedicated accessors, not GetEndpointContainer.
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await client.SetEndpointMetadata(new EndpointMetadata { EndpointId = "endpoint-1" });
        await client.GetSubscriptionsOnEndpoint("endpoint-1");

        Assert.IsTrue(adapter.WasCreated("Metadata"));
        Assert.IsTrue(adapter.WasCreated("subscriptions"));
    }

    // ── Legacy adapters fail closed rather than silently dropping the TTL ──

    [TestMethod]
    public async Task Adapter_without_the_container_properties_overload_fails_closed()
    {
        var adapter = new LegacyCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        var ex = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => client.UploadPendingMessage("event-1", "session-1", "endpoint-1", NewEvent()));

        StringAssert.Contains(ex.Message, nameof(ContainerProperties), StringComparison.Ordinal);
        Assert.AreEqual(0, adapter.ContainerCreations.Count,
            "Silently falling back to a TTL-disabled container is exactly the defect this guards.");
    }

    [TestMethod]
    public async Task Adapter_without_the_container_properties_overload_still_serves_shared_containers()
    {
        var adapter = new LegacyCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await client.SetEndpointMetadata(new EndpointMetadata { EndpointId = "endpoint-1" });

        CollectionAssert.Contains(adapter.ContainerCreations, "Metadata");
    }

    // ── The write `nb container resubmit` performs ──

    [TestMethod]
    public async Task Resubmit_write_honours_a_configured_retention()
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = 180 });

        await client.UploadFailedMessage("event-1", "session-1", "endpoint-1", NewEvent());

        Assert.AreEqual(OneEightyDaysInSeconds, (int)adapter.Container("endpoint-1").SingleUpsertedDocument()["ttl"]!);
    }

    // ── Constructor validation for non-host consumers ──

    [TestMethod]
    [DataRow(0)]
    [DataRow(366)]
    public void Constructor_rejects_an_invalid_retention(int days)
    {
        var ex = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CosmosDbClient(
            new RecordingCosmosClientAdapter(),
            null!,
            new CosmosDbMessageStoreOptions { UnresolvedRetentionDays = days }));

        StringAssert.Contains(ex.Message, days.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [TestMethod]
    public void Constructor_rejects_null_options()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new CosmosDbClient(
            new RecordingCosmosClientAdapter(), null!, null!));
    }

    public static IEnumerable<object[]> UnresolvedWrites => new[]
    {
        new object[] { nameof(CosmosDbClient.UploadPendingMessage), Write((c, e) => c.UploadPendingMessage("event-1", "session-1", "endpoint-1", e)) },
        new object[] { nameof(CosmosDbClient.UploadFailedMessage), Write((c, e) => c.UploadFailedMessage("event-1", "session-1", "endpoint-1", e)) },
        new object[] { nameof(CosmosDbClient.UploadDeferredMessage), Write((c, e) => c.UploadDeferredMessage("event-1", "session-1", "endpoint-1", e)) },
        new object[] { nameof(CosmosDbClient.UploadDeadletteredMessage), Write((c, e) => c.UploadDeadletteredMessage("event-1", "session-1", "endpoint-1", e)) },
        new object[] { nameof(CosmosDbClient.UploadUnsupportedMessage), Write((c, e) => c.UploadUnsupportedMessage("event-1", "session-1", "endpoint-1", e)) },
    };

    private static Func<CosmosDbClient, Task> Write(Func<CosmosDbClient, UnresolvedEvent, Task<bool>> write) =>
        client => write(client, NewEvent());

    private static async Task AssertOrderIndependent(Func<CosmosDbClient, Task> exercise)
    {
        var adapter = new RecordingCosmosClientAdapter();
        var client = new CosmosDbClient(adapter, null!, new CosmosDbMessageStoreOptions());

        await exercise(client);

        Assert.IsNull(adapter.CreationPropertiesFor("subscriptions"),
            "The shared subscriptions container must never be created with container-level TTL.");
        Assert.AreEqual(-1, adapter.CreationPropertiesFor("endpoint-1")!.DefaultTimeToLive,
            "The endpoint container must always be created with container-level TTL enabled.");
    }

    private static void AssertPatchSetsTtl(IReadOnlyList<PatchOperation> operations, int expectedSeconds, string because)
    {
        var ttlOperation = operations.Single(op => string.Equals(op.Path, "/ttl", StringComparison.Ordinal));
        Assert.IsInstanceOfType<PatchOperation<int>>(ttlOperation, because);
        Assert.AreEqual(expectedSeconds, ((PatchOperation<int>)ttlOperation).Value, because);
    }

    private static UnresolvedEvent NewEvent() => new()
    {
        EventId = "event-1",
        EventTypeId = "event-type-1",
    };
}
