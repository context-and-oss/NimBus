using System;
using System.Net.Http;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
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

public static class ServiceExtensions
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
            return new HttpEndpointStateChangeNotifier(httpClient, webhookKey, logger);
        });

        return services;
    }

    /// <summary>
    /// Registers the endpoint alert notifier (the WebApp's Alerts tab). It mails subscribers
    /// when <c>NimBus:Alerts:Mail</c> has an SMTP host and a sender, and the storage provider
    /// offers <see cref="ISubscriptionNotificationStore"/>; otherwise it registers the no-op
    /// notifier and logs why. Invalid settings never stop the Resolver: they are logged and
    /// alert mail stays off. A notifier the host registered first is kept.
    /// </summary>
    internal static IServiceCollection AddEndpointAlertNotifier(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IEndpointAlertNotifier>(sp =>
        {
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger("NimBus.Resolver.Alerts")
                ?? NullLogger.Instance;
            var options = sp.GetService<IConfiguration>()?.GetSection(AlertMailOptions.SectionName).Get<AlertMailOptions>()
                ?? new AlertMailOptions();

            if (!options.IsConfigured)
            {
                logger.LogInformation(
                    "Endpoint alert mail is off: set {Section}:Smtp:Host and {Section}:From to mail Alerts-tab subscribers.",
                    AlertMailOptions.SectionName, AlertMailOptions.SectionName);
                return new NoopEndpointAlertNotifier();
            }

            var errors = options.Validate();
            if (errors.Count > 0)
            {
                logger.LogError("Endpoint alert mail is off because its settings are invalid: {Errors}",
                    string.Join(" ", errors));
                return new NoopEndpointAlertNotifier();
            }

            var subscriptions = sp.GetService<ISubscriptionStore>();
            var notifications = sp.GetService<ISubscriptionNotificationStore>();
            if (subscriptions is null || notifications is null)
            {
                logger.LogWarning(
                    "Endpoint alert mail is off: the storage provider registers no {SubscriptionStore} or {NotificationStore}.",
                    nameof(ISubscriptionStore), nameof(ISubscriptionNotificationStore));
                return new NoopEndpointAlertNotifier();
            }

            logger.LogInformation("Endpoint alert mail is on: SMTP {Host}:{Port}, from {From}.",
                options.Smtp.Host, options.Smtp.Port, options.From);
            return new SubscriptionAlertNotifier(
                subscriptions,
                notifications,
                new SmtpAlertMailSender(options),
                new AlertMailComposer(options),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<ILogger<SubscriptionAlertNotifier>>() ?? NullLogger<SubscriptionAlertNotifier>.Instance);
        });

        return services;
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}
