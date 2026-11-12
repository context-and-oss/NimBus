#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using System.Linq;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class NotificationRegistrationTests
{
    // ── AC 8: the spec's literal snippet compiles and resolves channels + router ──

    [TestMethod]
    public void AddNimBusNotifications_SpecSnippet_ResolvesAllChannelsAndRouter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNimBus();

        services.AddNimBusNotifications(n =>
        {
            n.AddWebhook(opts =>
            {
                opts.Url = "https://incident-bot.example.com/nimbus";
                opts.MinSeverity = NotificationSeverity.Warning;
            });
            n.AddTeams(opts =>
            {
                opts.ConnectorUrl = "https://outlook.office.com/webhook/abc";
                opts.MinSeverity = NotificationSeverity.Critical;
            });
            n.AddEmail(opts =>
            {
                opts.Provider = EmailProvider.SendGrid;
                opts.ApiKey = "SG.example";
                opts.From = "alerts@example.com";
                opts.To = ["oncall@example.com"];
                opts.MinSeverity = NotificationSeverity.Critical;
            });
            n.WithRateLimit(maxPerMinute: 10, burstCapacity: 20);
        });

        var sp = services.BuildServiceProvider();
        var channels = sp.GetServices<INotificationChannel>().ToList();

        Assert.AreEqual(3, channels.Count);
        Assert.IsTrue(channels.Any(c => c is WebhookChannel));
        Assert.IsTrue(channels.Any(c => c is TeamsChannel));
        Assert.IsTrue(channels.Any(c => c is EmailChannel));
        Assert.IsNotNull(sp.GetService<INotificationRouter>());

        var registrations = sp.GetServices<ChannelRegistration>().ToList();
        Assert.AreEqual(3, registrations.Count);

        var notifier = sp.GetRequiredService<MessageLifecycleNotifier>();
        Assert.IsTrue(notifier.HasObservers, "The notification observer must be wired into the lifecycle notifier.");
    }

    [TestMethod]
    public void AddNimBusNotifications_RegistersSameChannelInstanceForRouterAndDiscovery()
    {
        var services = new ServiceCollection();
        services.AddNimBus();
        services.AddNimBusNotifications(n =>
            n.AddWebhook(opts => opts.Url = "https://example.com/hook"));

        var sp = services.BuildServiceProvider();

        var asChannel = sp.GetServices<INotificationChannel>().Single();
        var asRegistration = sp.GetServices<ChannelRegistration>().Single();
        Assert.AreSame(asChannel, asRegistration.Channel,
            "The channel resolved as INotificationChannel and inside ChannelRegistration must be the same instance.");
    }

    // ── AC 8: INimBusBuilder fluent overload ─────────────────────────────

    [TestMethod]
    public void AddNotifications_BuilderFluentOverload_ResolvesChannelsAndRouter()
    {
        var services = new ServiceCollection();
        services.AddNimBus(builder =>
            builder.AddNotifications(n =>
            {
                n.AddWebhook(opts => opts.Url = "https://example.com/hook");
                n.WithRateLimit(maxPerMinute: 5, burstCapacity: 5);
            }));

        var sp = services.BuildServiceProvider();

        Assert.AreEqual(1, sp.GetServices<INotificationChannel>().Count());
        Assert.IsNotNull(sp.GetService<INotificationRouter>());
    }

    [TestMethod]
    public void AddNotifications_FluentPath_DefaultsSessionBlockNotificationsOn()
    {
        var services = new ServiceCollection();
        services.AddNimBus(builder =>
            builder.AddNotifications(n => n.AddWebhook(opts => opts.Url = "https://example.com/hook")));

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<NotificationOptions>();

        Assert.IsTrue(options.NotifyOnSessionBlock,
            "The fluent registration path must enable session-block notifications by default (AC 7).");
    }

    // ── AC 9: legacy behaviour preserved ─────────────────────────────────

    [TestMethod]
    public void AddNotifications_Default_StillRegistersConsoleChannelAndNoRouter()
    {
        var services = new ServiceCollection();
        services.AddNimBus(builder => builder.AddNotifications());

        var sp = services.BuildServiceProvider();

        var channel = sp.GetServices<INotificationChannel>().Single();
        Assert.IsInstanceOfType<ConsoleNotificationChannel>(channel);
        Assert.IsNull(sp.GetService<INotificationRouter>(), "Legacy path does not register a router.");
    }

    // ── Fluent path with no channels falls back to console instead of dropping ──

    [TestMethod]
    public void AddNimBusNotifications_FluentPathWithNoChannels_FallsBackToConsole()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNimBus();
        services.AddNimBusNotifications(n => n.WithRateLimit(maxPerMinute: 10, burstCapacity: 20));

        var sp = services.BuildServiceProvider();

        var registration = sp.GetServices<ChannelRegistration>().Single();
        Assert.IsInstanceOfType<ConsoleNotificationChannel>(registration.Channel, "A fluent registration with no channels must fall back to the console channel, not drop notifications.");
        Assert.IsInstanceOfType<ConsoleNotificationChannel>(sp.GetServices<INotificationChannel>().Single());
        Assert.IsNotNull(sp.GetService<INotificationRouter>());
    }

    [TestMethod]
    public void AddNotifications_FluentEmptyLambda_FallsBackToConsole()
    {
        var services = new ServiceCollection();
        services.AddNimBus(builder => builder.AddNotifications((NotificationChannelBuilder _) => { }));

        var sp = services.BuildServiceProvider();

        Assert.IsInstanceOfType<ConsoleNotificationChannel>(sp.GetServices<ChannelRegistration>().Single().Channel);
    }

    [TestMethod]
    public void AddNotifications_LegacyChannelLambda_StillWorks()
    {
        var services = new ServiceCollection();
        services.AddNimBus(builder => builder.AddNotifications(
            configureOptions: opts => opts.NotifyOnReceived = true,
            configureChannels: s => s.AddSingleton<INotificationChannel, FakeNotificationChannel>()));

        var sp = services.BuildServiceProvider();

        Assert.IsInstanceOfType<FakeNotificationChannel>(sp.GetServices<INotificationChannel>().Single());
        Assert.IsTrue(sp.GetRequiredService<NotificationOptions>().NotifyOnReceived);
        Assert.IsTrue(sp.GetRequiredService<NotificationOptions>().NotifyOnCircuitOpen);
    }
}
