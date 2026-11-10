#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.InMemory.Tests;

[TestClass]
public sealed class InMemorySubscriptionStoreTests : SubscriptionStoreConformanceTests
{
    protected override ISubscriptionStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemorySubscriptionNotificationStoreTests : SubscriptionNotificationStoreConformanceTests
{
    protected override ISubscriptionStore CreateStore() => new InMemoryMessageStore();

    protected override ISubscriptionNotificationStore NotificationStoreFor(ISubscriptionStore store)
        => (ISubscriptionNotificationStore)store;
}

[TestClass]
public sealed class InMemoryEndpointMetadataStoreTests : EndpointMetadataStoreConformanceTests
{
    protected override IEndpointMetadataStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryMetricsStoreTests : MetricsStoreConformanceTests
{
    protected override INimBusMessageStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryMonitorMetricsStoreTests : MonitorMetricsStoreConformanceTests
{
    protected override INimBusMessageStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryDashboardTokenStoreTests : DashboardTokenStoreConformanceTests
{
    protected override IDashboardTokenStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryEventSchemaStoreConformanceTests : EventSchemaStoreConformanceTests
{
    protected override IEventSchemaStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryAccessControlStoreConformanceTests : AccessControlStoreConformanceTests
{
    protected override IAccessControlStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryServiceHealthStoreConformanceTests : ServiceHealthStoreConformanceTests
{
    protected override IServiceHealthStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryEndpointAcknowledgementStoreConformanceTests : EndpointAcknowledgementStoreConformanceTests
{
    protected override IEndpointAcknowledgementStore CreateStore() => new InMemoryMessageStore();
}

[TestClass]
public sealed class InMemoryHeartbeatHistoryStoreConformanceTests : HeartbeatHistoryStoreConformanceTests
{
    protected override IHeartbeatHistoryStore CreateStore() => new InMemoryMessageStore();
}
