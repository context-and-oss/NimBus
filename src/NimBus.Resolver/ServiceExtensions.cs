using System;
using System.Net.Http;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using NimBus.Alerts;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Resolver.Alerts;

namespace NimBus.Resolver;

public static partial class ServiceExtensions
{
    /// <summary>
    /// Legacy IServiceCollection-based registration. Storage provider must be
    /// registered separately via NimBus builder (AddCosmosDbMessageStore /
    /// AddSqlServerMessageStore).
    /// </summary>
    public static IServiceCollection AddResolver(this IServiceCollection services)
    {
        // Serilog stays a host concern (Program.cs AddSerilog registers it
        // as an MEL provider, per ADR-006); ResolverService itself takes MEL.
        // The heartbeat stores are resolved with GetService, not GetRequiredService:
        // a provider registered without the platform-heartbeat surface still yields a
        // working Resolver, it just completes heartbeat traffic without recording it.
        services.AddSingleton<IMessageHandler>(sp => new ResolverService(
            sp.GetRequiredService<IMessageTrackingStore>(),
            sp.GetRequiredService<IMessageStateChangeNotifier>(),
            sp.GetService<ILogger<ResolverService>>(),
            sp.GetService<IEndpointMetadataStore>(),
            sp.GetService<IServiceHealthStore>(),
            sp.GetRequiredService<IEndpointAlertNotifier>()));
        services.AddFlowStateChangeNotifier();
        services.AddEndpointAlertNotifier();

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            // In Azure the `AzureWebJobsServiceBus__fullyQualifiedNamespace` app
            // setting surfaces in IConfiguration with a colon separator; the raw
            // `__` key only exists when set literally (e.g. local.settings.json).
            var fqns = config.GetValue<string>("AzureWebJobsServiceBus:fullyQualifiedNamespace")
                ?? config.GetValue<string>("AzureWebJobsServiceBus__fullyQualifiedNamespace");
            if (!string.IsNullOrEmpty(fqns) && !fqns.Contains("SharedAccessKey=", StringComparison.OrdinalIgnoreCase))
                return new ServiceBusClient(fqns, new DefaultAzureCredential());

            var connectionString = fqns
                ?? config.GetConnectionString("servicebus")
                ?? config.GetValue<string>("AzureWebJobsServiceBus")
                ?? throw new InvalidOperationException("AzureWebJobsServiceBus configuration is required");
            return new ServiceBusClient(connectionString);
        });

        services.AddSingleton<IServiceBusAdapter>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var resolverId = config.GetValue<string>("ResolverId")
                ?? throw new InvalidOperationException("ResolverId configuration is required");
            var messageHandler = sp.GetRequiredService<IMessageHandler>();
            var serviceBusClient = sp.GetRequiredService<ServiceBusClient>();
            var entityPath = $"{resolverId}/{resolverId}";
            return new ServiceBusAdapter(messageHandler, serviceBusClient, entityPath);
        });

        return services;
    }

    /// <summary>
    /// Registers the Resolver write-path state-change notifier (spec 020).
    /// When <c>NimBus:Flow:WebAppUrl</c> is configured the Resolver pushes
    /// <c>endpointupdate</c> broadcasts to the management WebApp's storage-hook
    /// webhook (driving the live Flow / Monitor pages for storage providers
    /// without a Change Feed). Absent that config it registers the no-op
    /// notifier, so existing deployments are unaffected (spec NFR-004).
    /// The pushes leave the message path and are coalesced per endpoint, at most
    /// one per <c>NimBus:Flow:NotifyInterval</c> (default 1 s), with at most
    /// <c>NimBus:Flow:NotifyMaxConcurrency</c> endpoints (default 4) in flight at once.
    /// </summary>
    internal static IServiceCollection AddFlowStateChangeNotifier(this IServiceCollection services)
    {
        services.AddSingleton<IMessageStateChangeNotifier>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var webAppUrl = config.GetValue<string>("NimBus:Flow:WebAppUrl");
            if (string.IsNullOrWhiteSpace(webAppUrl)
                || !Uri.TryCreate(EnsureTrailingSlash(webAppUrl), UriKind.Absolute, out var baseUri))
            {
                return new NoopMessageStateChangeNotifier();
            }

            var webhookKey = config.GetValue<string>("EventGrid:WebhookKey");
            var httpClient = new HttpClient
            {
                BaseAddress = baseUri,
                Timeout = TimeSpan.FromSeconds(10),
            };
            var logger = sp.GetService<ILogger<HttpEndpointStateChangeNotifier>>();
            var http = new HttpEndpointStateChangeNotifier(httpClient, webhookKey, logger);
            return new CoalescingEndpointStateChangeNotifier(
                http,
                ReadNotifyInterval(config),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<CoalescingEndpointStateChangeNotifier>>(),
                maxConcurrentSends: ReadNotifyMaxConcurrency(config));
        });

        return services;
    }

    /// <summary>
    /// Registers the endpoint alert notifier (the WebApp's Alerts tab). It mails subscribers
    /// when <c>NimBus:Alerts:Mail</c> has an SMTP host and a sender, and calls their webhooks
    /// when <c>NimBus:Alerts:Webhook</c> has a signing key. Each is switched on by its own
    /// settings. With neither, or when the storage provider offers no
    /// <see cref="ISubscriptionNotificationStore"/>, it registers the no-op notifier and logs
    /// why. Invalid settings never stop the Resolver: they are logged and that way of delivery
    /// stays off. A notifier the host registered first is kept.
    /// </summary>
    internal static IServiceCollection AddEndpointAlertNotifier(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEndpointAlertNotifier>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("NimBus.Resolver.Alerts")
                ?? NullLogger.Instance;
            var configuration = sp.GetService<IConfiguration>();
            var mail = ReadAlertMailOptions(configuration, logger);
            var webhook = ReadAlertWebhookOptions(configuration, logger);
            if (mail is null && webhook is null)
            {
                return new NoopEndpointAlertNotifier();
            }

            var subscriptions = sp.GetService<ISubscriptionStore>();
            var notifications = sp.GetService<ISubscriptionNotificationStore>();
            if (subscriptions is null || notifications is null)
            {
                LogEndpointAlertsAreOff(logger, nameof(ISubscriptionStore), nameof(ISubscriptionNotificationStore));
                return new NoopEndpointAlertNotifier();
            }

            var timeProvider = sp.GetRequiredService<TimeProvider>();
            if (mail is not null)
            {
                LogEndpointAlertMail(logger, mail.Smtp.Host, mail.Smtp.Port, mail.From);
            }

            AlertWebhookSender? webhookSender = null;
            if (webhook is not null)
            {
                // A token is only ever requested for a listed prefix, so the credential is only
                // created when there is one. A host can register its own TokenCredential.
                var credential = webhook.BearerTokens.Count == 0
                    ? null
                    : sp.GetService<TokenCredential>() ?? new DefaultAzureCredential();
                webhookSender = new AlertWebhookSender(webhook, AlertWebhookSender.CreateHttpClient(webhook), credential, timeProvider);
                LogEndpointAlertWebhooks(logger, webhook.Timeout.TotalSeconds, webhook.BearerTokens.Count, webhook.AllowPrivateNetworks);
            }

            return new SubscriptionAlertNotifier(
                subscriptions,
                notifications,
                mail is null ? null : new SmtpAlertMailSender(mail),
                mail is null ? null : new AlertMailComposer(mail),
                webhookSender,
                timeProvider,
                sp.GetService<ILogger<SubscriptionAlertNotifier>>() ?? NullLogger<SubscriptionAlertNotifier>.Instance);
        });

        return services;
    }

    /// <summary>The alert mail settings, or null (with the reason logged) while alert mail is off.</summary>
    private static AlertMailOptions? ReadAlertMailOptions(IConfiguration? configuration, ILogger logger)
    {
        var options = configuration?.GetSection(AlertMailOptions.SectionName).Get<AlertMailOptions>() ?? new AlertMailOptions();
        if (!options.IsConfigured)
        {
            LogEndpointAlertMailIsOffUntilConfigured(logger, AlertMailOptions.SectionName);
            return null;
        }

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            LogEndpointAlertMailIsOffBecause(logger, string.Join(" ", errors));
            return null;
        }

        return options;
    }

    /// <summary>The alert webhook settings, or null (with the reason logged) while alert webhooks are off.</summary>
    private static AlertWebhookOptions? ReadAlertWebhookOptions(IConfiguration? configuration, ILogger logger)
    {
        var options = configuration?.GetSection(AlertWebhookOptions.SectionName).Get<AlertWebhookOptions>() ?? new AlertWebhookOptions();
        if (!options.IsConfigured)
        {
            LogEndpointAlertWebhooksAreOffUntilConfigured(logger, AlertWebhookOptions.SectionName);
            return null;
        }

        var errors = options.Validate();
        if (errors.Count > 0)
        {
            LogEndpointAlertWebhooksAreOffBecause(logger, string.Join(" ", errors));
            return null;
        }

        return options;
    }

    /// <summary>
    /// <c>NimBus:Flow:NotifyInterval</c> as a time span (<c>00:00:01</c>); a missing, unreadable
    /// or negative value keeps the 1 s default.
    /// </summary>
    internal static TimeSpan ReadNotifyInterval(IConfiguration config)
    {
        var raw = config.GetValue<string>("NimBus:Flow:NotifyInterval");
        return TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var interval) && interval >= TimeSpan.Zero
            ? interval
            : CoalescingEndpointStateChangeNotifier.DefaultInterval;
    }

    /// <summary>
    /// <c>NimBus:Flow:NotifyMaxConcurrency</c>, how many endpoints' pushes may be in flight at
    /// once; a missing, unreadable or non-positive value keeps the default of 4.
    /// </summary>
    internal static int ReadNotifyMaxConcurrency(IConfiguration config)
    {
        var raw = config.GetValue<string>("NimBus:Flow:NotifyMaxConcurrency");
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var max) && max >= 1
            ? max
            : CoalescingEndpointStateChangeNotifier.DefaultMaxConcurrentSends;
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint alert mail is off: set {Section}:Smtp:Host and {Section}:From to mail Alerts-tab subscribers.")]
    private static partial void LogEndpointAlertMailIsOffUntilConfigured(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Error, Message = "Endpoint alert mail is off because its settings are invalid: {Errors}")]
    private static partial void LogEndpointAlertMailIsOffBecause(ILogger logger, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts are off: the storage provider registers no {SubscriptionStore} or {NotificationStore}.")]
    private static partial void LogEndpointAlertsAreOff(ILogger logger, string subscriptionStore, string notificationStore);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint alert webhooks are off: set {Section}:SigningKey to call the webhooks of Alerts-tab subscribers.")]
    private static partial void LogEndpointAlertWebhooksAreOffUntilConfigured(ILogger logger, string section);

    [LoggerMessage(Level = LogLevel.Error, Message = "Endpoint alert webhooks are off because their settings are invalid: {Errors}")]
    private static partial void LogEndpointAlertWebhooksAreOffBecause(ILogger logger, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint alert webhooks are on: timeout {TimeoutSeconds} s, {TokenPrefixes} URL prefix(es) get a bearer token, private networks allowed: {AllowPrivateNetworks}.")]
    private static partial void LogEndpointAlertWebhooks(ILogger logger, double timeoutSeconds, int tokenPrefixes, bool allowPrivateNetworks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint alert mail is on: SMTP {Host}:{Port}, from {From}.")]
    private static partial void LogEndpointAlertMail(ILogger logger, string? host, int port, string? from);
}
