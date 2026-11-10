#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerSubscriptionStoreTests : SubscriptionStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerSubscriptionStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerSubscriptionStoreTests));

    protected override ISubscriptionStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerSubscriptionStoreTests));
}

[TestClass]
public sealed class SqlServerSubscriptionNotificationStoreTests : SubscriptionNotificationStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerSubscriptionNotificationStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerSubscriptionNotificationStoreTests));

    protected override ISubscriptionStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerSubscriptionNotificationStoreTests));

    protected override ISubscriptionNotificationStore NotificationStoreFor(ISubscriptionStore store)
        => (ISubscriptionNotificationStore)store;
}

[TestClass]
public sealed class SqlServerEndpointMetadataStoreTests : EndpointMetadataStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerEndpointMetadataStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerEndpointMetadataStoreTests));

    protected override IEndpointMetadataStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerEndpointMetadataStoreTests));
}

[TestClass]
public sealed class SqlServerMetricsStoreTests : MetricsStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerMetricsStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerMetricsStoreTests));

    protected override INimBusMessageStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerMetricsStoreTests));
}

[TestClass]
public sealed class SqlServerMonitorMetricsStoreTests : MonitorMetricsStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerMonitorMetricsStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerMonitorMetricsStoreTests));

    protected override INimBusMessageStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerMonitorMetricsStoreTests));
}

[TestClass]
public sealed class SqlServerDashboardTokenStoreTests : DashboardTokenStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerDashboardTokenStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerDashboardTokenStoreTests));

    protected override IDashboardTokenStore CreateStore()
        => (IDashboardTokenStore)SqlServerStoreTestHarness.CreateStore(typeof(SqlServerDashboardTokenStoreTests));
}

[TestClass]
public sealed class SqlServerEventSchemaStoreConformanceTests : EventSchemaStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerEventSchemaStoreConformanceTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerEventSchemaStoreConformanceTests));

    protected override IEventSchemaStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerEventSchemaStoreConformanceTests));
}

[TestClass]
public sealed class SqlServerAccessControlStoreConformanceTests : AccessControlStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerAccessControlStoreConformanceTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerAccessControlStoreConformanceTests));

    protected override IAccessControlStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerAccessControlStoreConformanceTests));
}

[TestClass]
public sealed class SqlServerServiceHealthStoreConformanceTests : ServiceHealthStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerServiceHealthStoreConformanceTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerServiceHealthStoreConformanceTests));

    protected override IServiceHealthStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerServiceHealthStoreConformanceTests));
}

[TestClass]
public sealed class SqlServerEndpointAcknowledgementStoreConformanceTests : EndpointAcknowledgementStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerEndpointAcknowledgementStoreConformanceTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerEndpointAcknowledgementStoreConformanceTests));

    protected override IEndpointAcknowledgementStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerEndpointAcknowledgementStoreConformanceTests));
}

[TestClass]
public sealed class SqlServerHeartbeatHistoryStoreConformanceTests : HeartbeatHistoryStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerHeartbeatHistoryStoreConformanceTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerHeartbeatHistoryStoreConformanceTests));

    protected override IHeartbeatHistoryStore CreateStore()
        => (IHeartbeatHistoryStore)SqlServerStoreTestHarness.CreateStore(typeof(SqlServerHeartbeatHistoryStoreConformanceTests));
}
