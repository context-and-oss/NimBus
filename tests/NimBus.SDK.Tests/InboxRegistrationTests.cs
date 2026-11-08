#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class InboxRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void UseInbox_requires_an_explicit_provider()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.UseInbox(_ => { }));

        StringAssert.Contains(exception.Message, nameof(InboxOptions.DeduplicationStore), StringComparison.Ordinal);
    }

    [TestMethod]
    public void UseInbox_rejects_an_unknown_provider()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        var exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            builder.UseInbox(options => options.DeduplicationStore = (InboxStore)999));

        Assert.AreEqual(nameof(InboxOptions.DeduplicationStore), exception.ParamName);
    }

    [TestMethod]
    public void UseInbox_can_only_be_configured_once()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.UseInbox(options => options.DeduplicationStore = InboxStore.InMemory);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.UseInbox(options => options.DeduplicationStore = InboxStore.SqlServer));

        StringAssert.Contains(exception.Message, "already configured", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Missing_selected_provider_fails_when_subscriber_is_started_with_guidance()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
                options.DeduplicationStore = InboxStore.SqlServer));

        using var provider = services.BuildServiceProvider();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            provider.GetRequiredService<ISubscriberClient>());

        StringAssert.Contains(exception.Message, "SqlServer", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "AddNimBusSqlServerInbox", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "keyed", StringComparison.Ordinal);
    }

    [TestMethod]
    public void UseInbox_resolves_selected_keyed_store_wraps_handler_and_registers_cleanup()
    {
        var inMemory = new NoopInboxStore();
        var sqlServer = new NoopInboxStore();
        var services = CreateServices();
        services.AddKeyedSingleton<IInboxStore>(InboxStore.InMemory, inMemory);
        services.AddKeyedSingleton<IInboxStore>(InboxStore.SqlServer, sqlServer);
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
            {
                options.DeduplicationStore = InboxStore.SqlServer;
                options.RetentionPeriod = TimeSpan.FromDays(3);
                options.CleanupInterval = TimeSpan.FromMinutes(20);
            }));

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var strictHandler = GetStrictHandler(subscriber);
        var contextHandler = GetPrivateField(strictHandler, "_eventContextHandler");

        Assert.IsInstanceOfType<InboxMiddleware>(contextHandler);
        Assert.AreSame(sqlServer, GetPrivateField(contextHandler, "_inboxStore"));
        // One-check/one-record contract: the composition hands StrictMessageHandler the
        // pre-session-guard detector and configures the decorator as record-only, so a
        // fresh delivery performs exactly one store check and one record.
        Assert.IsNotNull(GetPrivateField(strictHandler, "_inboxDuplicateDetector"));
        Assert.IsTrue((bool)GetPrivateField(contextHandler, "_checkHandledUpstream"));
        Assert.IsNotNull(provider.GetService<MessageLifecycleNotifier>());
        Assert.AreEqual(
            1,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    [TestMethod]
    public void Subscriber_without_UseInbox_has_no_decorator_notifier_or_cleanup_host()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber("Billing", _ => { });

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var strictHandler = GetStrictHandler(subscriber);
        var contextHandler = GetPrivateField(strictHandler, "_eventContextHandler");

        Assert.IsNotInstanceOfType<InboxMiddleware>(contextHandler);
        Assert.IsNull(provider.GetService<MessageLifecycleNotifier>());
        Assert.AreEqual(
            0,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    [TestMethod]
    public void UseInbox_with_preregistered_subscriber_client_fails_fast()
    {
        // TryAddSingleton discards NimBus's subscriber factory when a custom
        // ISubscriberClient is already registered, so UseInbox could never decorate the
        // effective client — yet the purge host and notifier would still install.
        // Registration must fail clearly instead of silently skipping deduplication.
        var services = CreateServices();
        services.AddSingleton<ISubscriberClient>(new FakeSubscriberClient());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddNimBusSubscriber(
                "Billing",
                builder => builder.UseInbox(options =>
                    options.DeduplicationStore = InboxStore.InMemory)));

        StringAssert.Contains(exception.Message, nameof(ISubscriberClient), StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "UseInbox", StringComparison.Ordinal);
        Assert.AreEqual(
            0,
            services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)),
            "No inbox sidecar may be registered when the subscriber factory is discarded");
    }

    [TestMethod]
    public void Preregistered_subscriber_client_without_UseInbox_installs_no_sidecars()
    {
        var services = CreateServices();
        var custom = new FakeSubscriberClient();
        services.AddSingleton<ISubscriberClient>(custom);
        services.AddNimBusSubscriber("Billing", _ => { });

        using var provider = services.BuildServiceProvider();

        Assert.AreSame(custom, provider.GetRequiredService<ISubscriberClient>());
        Assert.AreEqual(
            0,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    [TestMethod]
    public async Task Subscriber_client_registered_after_UseInbox_fails_at_startup_validation()
    {
        // The registration-time guard only sees a custom client added BEFORE
        // AddNimBusSubscriber. One added afterwards wins DI's last-registration rule, so
        // receivers would use an undecorated client while the purge host stays active —
        // startup must fail instead of silently disabling the opted-in deduplication.
        var services = CreateServices();
        services.AddKeyedSingleton<IInboxStore>(InboxStore.InMemory, new NoopInboxStore());
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
                options.DeduplicationStore = InboxStore.InMemory));
        services.AddSingleton<ISubscriberClient>(new FakeSubscriberClient());

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>()
            .OfType<InboxSubscriberStartupValidator>()
            .Single();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        StringAssert.Contains(exception.Message, nameof(ISubscriberClient), StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "UseInbox", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Startup_validation_passes_when_the_nimbus_composed_client_is_effective()
    {
        var services = CreateServices();
        services.AddKeyedSingleton<IInboxStore>(InboxStore.InMemory, new NoopInboxStore());
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
                options.DeduplicationStore = InboxStore.InMemory));

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>()
            .OfType<InboxSubscriberStartupValidator>()
            .Single();

        await validator.StartAsync(CancellationToken.None);
        await validator.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public void Subscriber_without_UseInbox_registers_no_startup_validator()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber("Billing", _ => { });

        using var provider = services.BuildServiceProvider();

        Assert.AreEqual(
            0,
            provider.GetServices<IHostedService>().OfType<InboxSubscriberStartupValidator>().Count());
    }

    [TestMethod]
    public void Later_same_endpoint_registration_cannot_partially_enable_inbox()
    {
        var services = CreateServices();
        services.AddNimBusSubscriber("Billing", _ => { });
        services.AddNimBusSubscriber(
            "Billing",
            builder => builder.UseInbox(options =>
                options.DeduplicationStore = InboxStore.SqlServer));

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var strictHandler = GetStrictHandler(subscriber);

        Assert.IsNotInstanceOfType<InboxMiddleware>(
            GetPrivateField(strictHandler, "_eventContextHandler"));
        Assert.IsNull(provider.GetService<MessageLifecycleNotifier>());
        Assert.AreEqual(
            0,
            provider.GetServices<IHostedService>().OfType<InboxPurgeHostedService>().Count());
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        return services;
    }

    private static object GetStrictHandler(ISubscriberClient subscriber)
    {
        var adapter = GetPrivateField(subscriber, "_serviceBusAdapter");
        return GetPrivateField(adapter, "_messageHandler");
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Expected private field '{fieldName}' on {target.GetType().Name}.");
        return field.GetValue(target)!;
    }

    private sealed class FakeSubscriberClient : ISubscriberClient
    {
        public void RegisterHandler<TEvent>(Func<EventHandlers.IEventHandler<TEvent>> eventHandlerFactory)
            where TEvent : Core.Events.IEvent
        {
        }

        public Task Handle(
            ServiceBusReceivedMessage message,
            Microsoft.Azure.Functions.Worker.ServiceBusSessionMessageActions sessionActions,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Handle(
            ServiceBusReceivedMessage message,
            Microsoft.Azure.Functions.Worker.ServiceBusMessageActions messageActions,
            Microsoft.Azure.Functions.Worker.ServiceBusSessionMessageActions sessionActions,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Handle(
            ServiceBusReceivedMessage message,
            ServiceBusSessionReceiver sessionReceiver,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Handle(
            ProcessSessionMessageEventArgs args,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopInboxStore : IInboxStore
    {
        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
