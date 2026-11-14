using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Extensions;
using System;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// NimBus extension that adds notification support on message lifecycle events.
/// </summary>
public class NotificationsExtension : INimBusExtension
{
    private readonly Action<NotificationOptions>? _configureOptions;
    private readonly Action<IServiceCollection>? _configureChannels;

    public NotificationsExtension()
        : this(null, null)
    {
    }

    public NotificationsExtension(Action<NotificationOptions>? configureOptions, Action<IServiceCollection>? configureChannels)
    {
        _configureOptions = configureOptions;
        _configureChannels = configureChannels;
    }

    public void Configure(INimBusBuilder builder)
    {
        var options = new NotificationOptions();
        _configureOptions?.Invoke(options);
        builder.Services.AddSingleton(options);

        // Register channels - use console channel as default if none configured
        if (_configureChannels != null)
        {
            _configureChannels(builder.Services);
        }
        else
        {
            builder.Services.AddSingleton<INotificationChannel, ConsoleNotificationChannel>();
        }

        // Register the lifecycle observer
        builder.AddLifecycleObserver<NotificationLifecycleObserver>();
    }
}

/// <summary>
/// Extension methods for adding Notifications to the NimBus builder.
/// </summary>
public static class NotificationsBuilderExtensions
{
    /// <summary>
    /// Adds notification support with default options and console output.
    /// </summary>
    public static INimBusBuilder AddNotifications(this INimBusBuilder builder)
    {
        return builder.AddExtension(new NotificationsExtension());
    }

    /// <summary>
    /// Adds notification support with custom configuration.
    /// </summary>
    /// <param name="builder">The NimBus builder.</param>
    /// <param name="configureOptions">Configure notification options (which events trigger notifications).</param>
    /// <param name="configureChannels">Register custom notification channels (email, Teams, Slack, etc.).</param>
    public static INimBusBuilder AddNotifications(
        this INimBusBuilder builder,
        Action<NotificationOptions>? configureOptions = null,
        Action<IServiceCollection>? configureChannels = null)
    {
        return builder.AddExtension(new NotificationsExtension(configureOptions, configureChannels));
    }

    /// <summary>
    /// Adds notification support with production channels configured via the fluent
    /// <see cref="NotificationChannelBuilder"/> (Webhook, Teams, Email), per-channel severity
    /// routing, and rate limiting / dedup. Notifications are routed through an
    /// <see cref="INotificationRouter"/>. Session-block notifications are enabled by default on
    /// this path (override via <paramref name="configureOptions"/>).
    /// </summary>
    /// <param name="builder">The NimBus builder.</param>
    /// <param name="configureChannels">Configures the channels and rate limit.</param>
    /// <param name="configureOptions">Optionally overrides which lifecycle events trigger notifications.</param>
    public static INimBusBuilder AddNotifications(
        this INimBusBuilder builder,
        Action<NotificationChannelBuilder> configureChannels,
        Action<NotificationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureChannels);

        RegisterChannelsAndRouter(builder.Services, configureChannels, configureOptions);
        builder.AddLifecycleObserver<NotificationLifecycleObserver>();
        return builder;
    }

    /// <summary>
    /// Convenience entry point mirroring the issue's <c>services.AddNimBusNotifications(n =&gt; { … })</c>
    /// shape. Registers the fluent channels, the router, and the lifecycle observer directly on the
    /// service collection. Requires <c>services.AddNimBus(...)</c> to have registered the core
    /// <see cref="MessageLifecycleNotifier"/>. Session-block notifications are enabled by default.
    /// </summary>
    public static IServiceCollection AddNimBusNotifications(
        this IServiceCollection services,
        Action<NotificationChannelBuilder> configureChannels,
        Action<NotificationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureChannels);

        RegisterChannelsAndRouter(services, configureChannels, configureOptions);
        services.AddSingleton<IMessageLifecycleObserver, NotificationLifecycleObserver>();
        return services;
    }

    private static void RegisterChannelsAndRouter(
        IServiceCollection services,
        Action<NotificationChannelBuilder> configureChannels,
        Action<NotificationOptions>? configureOptions)
    {
        var options = new NotificationOptions { NotifyOnSessionBlock = true };
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        var routerOptions = new NotificationRouterOptions();
        var channelBuilder = new NotificationChannelBuilder(services, routerOptions);
        configureChannels(channelBuilder);

        if (channelBuilder.ChannelCount == 0)
        {
            // The fluent path was used but no channels were added (e.g. an empty lambda or only
            // WithRateLimit). Fall back to the console channel — matching the legacy default — so
            // notifications are surfaced for development instead of being silently dropped.
            var consoleChannel = new ConsoleNotificationChannel();
            var consoleOptions = new ConsoleChannelOptions { MinSeverity = NotificationSeverity.Information };
            services.AddSingleton<INotificationChannel>(consoleChannel);
            services.AddSingleton(new ChannelRegistration(consoleChannel, consoleOptions));
        }

        services.AddSingleton(routerOptions);
        services.AddSingleton<INotificationRouter, NotificationRouter>();
    }
}
