using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Net.Http;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Fluent builder for declaratively registering production notification channels (Webhook,
/// Teams, Email), their per-channel <see cref="NotificationChannelOptions.MinSeverity"/>, and the
/// global rate-limit / dedup settings. Each channel is registered both as an
/// <see cref="INotificationChannel"/> and as a <see cref="ChannelRegistration"/> the
/// <see cref="NotificationRouter"/> consumes.
/// </summary>
public sealed class NotificationChannelBuilder
{
    private readonly IServiceCollection _services;
    private readonly NotificationRouterOptions _routerOptions;
    private int _channelCount;

    internal NotificationChannelBuilder(IServiceCollection services, NotificationRouterOptions routerOptions)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _routerOptions = routerOptions ?? throw new ArgumentNullException(nameof(routerOptions));
    }

    /// <summary>The number of delivery channels registered through this builder.</summary>
    internal int ChannelCount => _channelCount;

    /// <summary>
    /// Registers a <see cref="WebhookChannel"/> that POSTs notifications to a configured URL.
    /// </summary>
    public NotificationChannelBuilder AddWebhook(Action<WebhookChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new WebhookChannelOptions();
        configure(options);
        options.Validate();

        AddChannel(options, (opts, http, sp) =>
            new WebhookChannel(opts, http, sp.GetService<ILogger<WebhookChannel>>()));
        return this;
    }

    /// <summary>
    /// Registers a <see cref="TeamsChannel"/> that posts an Adaptive Card to a Teams connector URL.
    /// </summary>
    public NotificationChannelBuilder AddTeams(Action<TeamsChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new TeamsChannelOptions();
        configure(options);
        options.Validate();

        AddChannel(options, (opts, http, sp) =>
            new TeamsChannel(opts, http, sp.GetService<ILogger<TeamsChannel>>()));
        return this;
    }

    /// <summary>
    /// Registers an <see cref="EmailChannel"/> (SendGrid or SMTP, selected by
    /// <see cref="EmailChannelOptions.Provider"/>).
    /// </summary>
    public NotificationChannelBuilder AddEmail(Action<EmailChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new EmailChannelOptions();
        configure(options);
        options.Validate();

        AddChannel(options, (opts, http, sp) =>
            new EmailChannel(opts, http, sp.GetService<ILogger<EmailChannel>>()));
        return this;
    }

    /// <summary>
    /// Configures a global token-bucket rate limit shared across all channels. Last call wins.
    /// </summary>
    public NotificationChannelBuilder WithRateLimit(int maxPerMinute, int burstCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPerMinute);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burstCapacity);

        _routerOptions.MaxPerMinute = maxPerMinute;
        _routerOptions.BurstCapacity = burstCapacity;
        return this;
    }

    /// <summary>
    /// Overrides the deduplication window (default 5 minutes) within which a repeated
    /// <c>(EventId, Severity)</c> is collapsed to a single delivery.
    /// </summary>
    public NotificationChannelBuilder WithDedupWindow(TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        _routerOptions.DedupWindow = window;
        return this;
    }

    private void AddChannel<TOptions>(
        TOptions options,
        Func<TOptions, HttpClient, IServiceProvider, INotificationChannel> create)
        where TOptions : NotificationChannelOptions
    {
        var clientName = string.Create(CultureInfo.InvariantCulture, $"NimBus.Notifications.{typeof(TOptions).Name}.{_channelCount++}");
        _services.AddHttpClient(clientName, client => client.Timeout = options.Timeout);

        // Build the channel exactly once and share the single instance between the
        // INotificationChannel registration (DI discovery) and the ChannelRegistration
        // (router) registration.
        INotificationChannel? instance = null;
        var gate = new object();

        INotificationChannel Build(IServiceProvider sp)
        {
            if (instance is not null) return instance;
            lock (gate)
            {
                instance ??= create(
                    options,
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName),
                    sp);
            }

            return instance;
        }

        _services.AddSingleton<INotificationChannel>(Build);
        _services.AddSingleton(sp => new ChannelRegistration(Build(sp), options));
    }
}
