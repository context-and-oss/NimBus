#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.CosmosDb.Tests;

[TestClass]
public sealed class CosmosDbSubscriptionStoreTests : SubscriptionStoreConformanceTests
{
    protected override ISubscriptionStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbSubscriptionNotificationStoreTests : SubscriptionNotificationStoreConformanceTests
{
    protected override ISubscriptionStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();

    protected override ISubscriptionNotificationStore NotificationStoreFor(ISubscriptionStore store)
        => (ISubscriptionNotificationStore)store;
}

[TestClass]
public sealed class CosmosDbEndpointMetadataStoreTests : EndpointMetadataStoreConformanceTests
{
    protected override IEndpointMetadataStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbMetricsStoreTests : MetricsStoreConformanceTests
{
    protected override INimBusMessageStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbMonitorMetricsStoreTests : MonitorMetricsStoreConformanceTests
{
    protected override INimBusMessageStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbEventSchemaStoreConformanceTests : EventSchemaStoreConformanceTests
{
    protected override IEventSchemaStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbAccessControlStoreConformanceTests : AccessControlStoreConformanceTests
{
    protected override IAccessControlStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbServiceHealthStoreConformanceTests : ServiceHealthStoreConformanceTests
{
    protected override IServiceHealthStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbEndpointAcknowledgementStoreConformanceTests : EndpointAcknowledgementStoreConformanceTests
{
    protected override IEndpointAcknowledgementStore CreateStore()
        => CosmosDbStoreTestHarness.CreateStore();
}

[TestClass]
public sealed class CosmosDbHeartbeatHistoryStoreConformanceTests : HeartbeatHistoryStoreConformanceTests
{
    protected override IHeartbeatHistoryStore CreateStore()
        => (IHeartbeatHistoryStore)CosmosDbStoreTestHarness.CreateStore();
}
