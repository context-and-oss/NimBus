#pragma warning disable CA1707, CA1515, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver.Alerts;
using NimBus.Resolver.Services;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.ServiceBus;
using NimBus.Testing.Conformance;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The Resolver host's composition root: which store and notifier the message handler gets,
/// how the Service Bus client is built from the Functions app settings, and which storage
/// provider <c>Program.cs</c> selects. A configuration-key bug here once kept the dev
/// Resolver down for weeks (6d19da6); these pin the settings contract so it fails in CI.
/// Both <c>AddResolver</c> overloads carry the same wiring, so every case runs against each.
/// </summary>
[TestClass]
public class ResolverCompositionTests
{
    private const string Namespace = "nimbus-test.servicebus.windows.net";
    private const string ConnectionString =
        "Endpoint=sb://nimbus-test.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=c2VjcmV0";

    public enum Registration
    {
        ServiceCollection,
        NimBusBuilder,
    }

    // ── Message handler ────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task MessageHandler_IsTheResolverService(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?>());

        Assert.IsInstanceOfType<ResolverService>(provider.GetRequiredService<IMessageHandler>());
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task MessageHandler_GetsTheHeartbeatStoresFromTheRegisteredProvider(Registration registration)
    {
        // The optional heartbeat stores are resolved from the container: a Resolver wired
        // without them would silently complete heartbeat answers without recording them.
        var store = new InMemoryMessageStore();
        await using var provider = Build(registration, new Dictionary<string, string?>(), store);
        var handler = provider.GetRequiredService<IMessageHandler>();
        var message = ResolverHeartbeatTests.CreateHeartbeatContext(
            MessageType.ResolutionResponse, to: Constants.ResolverId, from: "BillingEndpoint",
            payload: new CoreHeartbeat { Endpoint = "BillingEndpoint" });

        await handler.Handle(message);

        var overview = await store.GetHeartbeatOverview();
        Assert.IsTrue(overview.Any(h => h.EndpointId == "BillingEndpoint"),
            "The heartbeat answer must be recorded through the registered store.");
        Assert.AreEqual(1, message.CompletedCalls);
    }

    // ── Endpoint alert mail ────────────────────────────────────────────────────────────

    private static Dictionary<string, string?> AlertMailSettings(string from = "nimbus@example.com") => new()
    {
        ["NimBus:Alerts:Mail:Smtp:Host"] = "smtp.example.com",
        ["NimBus:Alerts:Mail:From"] = from,
    };

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task AlertNotifier_WithoutMailSettings_IsTheNoop(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?>(), registerSubscriptionStores: true);

        Assert.IsInstanceOfType<NoopEndpointAlertNotifier>(provider.GetRequiredService<IEndpointAlertNotifier>());
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task AlertNotifier_WithMailSettings_MailsSubscribers(Registration registration)
    {
        await using var provider = Build(registration, AlertMailSettings(), registerSubscriptionStores: true);

        Assert.IsInstanceOfType<SubscriptionAlertNotifier>(provider.GetRequiredService<IEndpointAlertNotifier>());
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task AlertNotifier_WithInvalidMailSettings_IsTheNoop(Registration registration)
    {
        // A bad alert setting must not stop the Resolver from settling messages.
        await using var provider = Build(registration, AlertMailSettings(from: "not an address"), registerSubscriptionStores: true);

        Assert.IsInstanceOfType<NoopEndpointAlertNotifier>(provider.GetRequiredService<IEndpointAlertNotifier>());
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task AlertNotifier_WithoutTheSubscriptionStores_IsTheNoop(Registration registration)
    {
        await using var provider = Build(registration, AlertMailSettings());

        Assert.IsInstanceOfType<NoopEndpointAlertNotifier>(provider.GetRequiredService<IEndpointAlertNotifier>());
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task MessageHandler_HandsFailuresToTheRegisteredAlertNotifier(Registration registration)
    {
        // A host may register its own notifier before AddResolver; the handler must use it.
        var alerts = new ResolverAlertTests.RecordingAlertNotifier();
        await using var provider = Build(
            registration,
            new Dictionary<string, string?>(),
            configure: services => services.AddSingleton<IEndpointAlertNotifier>(alerts));
        var handler = provider.GetRequiredService<IMessageHandler>();
        var message = ResolverTestMessages.CreateMessageContext(
            MessageType.ErrorResponse, to: Constants.ResolverId, from: "BillingEndpoint",
            messageId: "rsp-1", parentMessageId: "req-1");

        await handler.Handle(message);

        Assert.AreEqual("BillingEndpoint", alerts.Contexts.Single().EndpointId);
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task MessageHandler_WithoutAStore_FailsToResolve(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?>(), registerStore: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredService<IMessageHandler>());
    }

    // ── State-change notifier ──────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("not a url")]
    public async Task Notifier_WithoutAValidWebAppUrl_IsTheNoop(string? webAppUrl)
    {
        await using var provider = Build(Registration.ServiceCollection, new Dictionary<string, string?>
        {
            ["NimBus:Flow:WebAppUrl"] = webAppUrl,
        });

        Assert.IsInstanceOfType<NoopMessageStateChangeNotifier>(provider.GetRequiredService<IMessageStateChangeNotifier>());
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task Notifier_WithAWebAppUrl_PostsToTheWebApp(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?>
        {
            ["NimBus:Flow:WebAppUrl"] = "https://webapp.example.com",
            ["EventGrid:WebhookKey"] = "key",
        });

        Assert.IsInstanceOfType<HttpEndpointStateChangeNotifier>(provider.GetRequiredService<IMessageStateChangeNotifier>());
    }

    // ── Service Bus client ─────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(Registration.ServiceCollection, "AzureWebJobsServiceBus:fullyQualifiedNamespace")]
    [DataRow(Registration.ServiceCollection, "AzureWebJobsServiceBus__fullyQualifiedNamespace")]
    [DataRow(Registration.NimBusBuilder, "AzureWebJobsServiceBus:fullyQualifiedNamespace")]
    [DataRow(Registration.NimBusBuilder, "AzureWebJobsServiceBus__fullyQualifiedNamespace")]
    public async Task ServiceBusClient_FromTheIdentityBasedSetting_UsesTheNamespace(Registration registration, string key)
    {
        // Azure surfaces the AzureWebJobsServiceBus__fullyQualifiedNamespace app setting with a
        // colon; local.settings.json can carry the raw double-underscore key. Both must work.
        await using var provider = Build(registration, new Dictionary<string, string?> { [key] = Namespace });

        Assert.AreEqual(Namespace, provider.GetRequiredService<ServiceBusClient>().FullyQualifiedNamespace);
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection, "AzureWebJobsServiceBus")]
    [DataRow(Registration.ServiceCollection, "ConnectionStrings:servicebus")]
    [DataRow(Registration.ServiceCollection, "AzureWebJobsServiceBus:fullyQualifiedNamespace")]
    [DataRow(Registration.NimBusBuilder, "AzureWebJobsServiceBus")]
    [DataRow(Registration.NimBusBuilder, "ConnectionStrings:servicebus")]
    [DataRow(Registration.NimBusBuilder, "AzureWebJobsServiceBus:fullyQualifiedNamespace")]
    public async Task ServiceBusClient_FromAConnectionString_UsesIt(Registration registration, string key)
    {
        // The namespace setting holding a full connection string is treated as one.
        await using var provider = Build(registration, new Dictionary<string, string?> { [key] = ConnectionString });

        Assert.AreEqual(Namespace, provider.GetRequiredService<ServiceBusClient>().FullyQualifiedNamespace);
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task ServiceBusClient_FromALowercaseConnectionStringInTheNamespaceSetting_UsesIt(Registration registration)
    {
        // Connection-string keys are case-insensitive, so a lowercase one must not be mistaken
        // for a bare namespace.
        await using var provider = Build(registration, new Dictionary<string, string?>
        {
            ["AzureWebJobsServiceBus:fullyQualifiedNamespace"] = ConnectionString.ToLowerInvariant(),
        });

        Assert.AreEqual(Namespace, provider.GetRequiredService<ServiceBusClient>().FullyQualifiedNamespace);
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task ServiceBusClient_WithoutConfiguration_FailsWithTheSettingName(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?>());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredService<ServiceBusClient>());
        StringAssert.Contains(exception.Message, "AzureWebJobsServiceBus");
    }

    // ── Service Bus adapter ────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task Adapter_WithoutResolverId_FailsWithTheSettingName(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?> { ["AzureWebJobsServiceBus"] = ConnectionString });

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => provider.GetRequiredService<IServiceBusAdapter>());
        StringAssert.Contains(exception.Message, "ResolverId");
    }

    [TestMethod]
    [DataRow(Registration.ServiceCollection)]
    [DataRow(Registration.NimBusBuilder)]
    public async Task Adapter_WithFullConfiguration_Resolves(Registration registration)
    {
        await using var provider = Build(registration, new Dictionary<string, string?>
        {
            ["AzureWebJobsServiceBus"] = ConnectionString,
            ["ResolverId"] = Constants.ResolverId,
        });

        Assert.IsInstanceOfType<ServiceBusAdapter>(provider.GetRequiredService<IServiceBusAdapter>());
    }

    // ── Storage provider selection (Program.cs) ────────────────────────────────────────

    [TestMethod]
    [DataRow("NimBus:StorageProvider", "sqlserver", "sqlserver")]
    [DataRow("StorageProvider", "SqlServer", "SqlServer")]
    [DataRow("NimBus:StorageProvider", "cosmos", "cosmos")]
    public void StorageProvider_ExplicitSettingWins(string key, string value, string expected)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [key] = value,
            // Present to prove the explicit setting beats auto-detection.
            ["CosmosConnection"] = "AccountEndpoint=https://x/;AccountKey=a",
            ["SqlConnection"] = "Server=.;Database=nimbus",
        });

        Assert.AreEqual(expected, ResolverStorageProvider.Select(configuration));
    }

    [TestMethod]
    [DataRow("SqlConnection")]
    [DataRow("ConnectionStrings:sqlserver")]
    [DataRow("SqlServerConnection")]
    public void StorageProvider_OnlySqlConfigured_SelectsSqlServer(string key)
    {
        var configuration = Configuration(new Dictionary<string, string?> { [key] = "Server=.;Database=nimbus" });

        Assert.AreEqual(ResolverStorageProvider.SqlServer, ResolverStorageProvider.Select(configuration));
        Assert.IsTrue(ResolverStorageProvider.IsSqlServer(ResolverStorageProvider.Select(configuration)));
    }

    [TestMethod]
    [DataRow("CosmosAccountEndpoint")]
    [DataRow("ConnectionStrings:cosmos")]
    [DataRow("CosmosConnection")]
    public void StorageProvider_BothConfigured_KeepsCosmosForBackwardsCompatibility(string cosmosKey)
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            [cosmosKey] = "https://cosmos.example.com",
            ["SqlConnection"] = "Server=.;Database=nimbus",
        });

        Assert.AreEqual(ResolverStorageProvider.Cosmos, ResolverStorageProvider.Select(configuration));
    }

    [TestMethod]
    public void StorageProvider_NothingConfigured_DefaultsToCosmos()
    {
        var selected = ResolverStorageProvider.Select(Configuration(new Dictionary<string, string?>()));

        Assert.AreEqual(ResolverStorageProvider.Cosmos, selected);
        Assert.IsFalse(ResolverStorageProvider.IsSqlServer(selected));
    }

    private static IConfiguration Configuration(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static ServiceProvider Build(
        Registration registration,
        IDictionary<string, string?> settings,
        InMemoryMessageStore? store = null,
        bool registerStore = true,
        bool registerSubscriptionStores = false,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configuration(settings));
        services.AddLogging();
        if (registerStore)
        {
            var inMemory = store ?? new InMemoryMessageStore();
            services.AddSingleton<IMessageTrackingStore>(inMemory);
            services.AddSingleton<IEndpointMetadataStore>(inMemory);
            services.AddSingleton<IServiceHealthStore>(inMemory);
            if (registerSubscriptionStores)
            {
                services.AddSingleton<ISubscriptionStore>(inMemory);
                services.AddSingleton<ISubscriptionNotificationStore>(inMemory);
            }
        }

        configure?.Invoke(services);

        if (registration == Registration.ServiceCollection)
            ServiceExtensions.AddResolver(services);
        else
            services.AddNimBus(nimbus => nimbus.AddResolver());

        return services.BuildServiceProvider();
    }
}
