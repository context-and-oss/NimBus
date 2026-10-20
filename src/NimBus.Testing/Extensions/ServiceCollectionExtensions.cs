using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using System;

namespace NimBus.Testing.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory test transport. <paramref name="endpoint"/> names the logical
    /// subscriber endpoint the composition models; when <c>UseInbox</c> is configured it becomes
    /// the deduplication scope and the cleanup host's purge scope, mirroring the production
    /// subscriber where the configured endpoint — never the message's <c>To</c> — keys the inbox.
    /// </summary>
    public static IServiceCollection AddNimBusTestTransport(
        this IServiceCollection services,
        Action<NimBusSubscriberBuilder> configureBuilder,
        string endpoint = "in-memory")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        services.AddNimBusInstrumentation();

        var bus = new InMemoryMessageBus();
        services.AddSingleton(bus);
        // Wrap with the instrumenting decorator so the in-memory transport emits
        // the same publisher span as the production transports — required for the
        // cross-transport instrumentation conformance category.
        services.AddSingleton<ISender>(NimBusOpenTelemetryDecorators.InstrumentSender(bus, MessagingSystem.InMemory));

        services.TryAddSingleton<IPublisherClient>(sp => new PublisherClient(sp.GetRequiredService<ISender>()));

        var builder = new NimBusSubscriberBuilder(services);
        configureBuilder(builder);
        InboxRegistration.AddServices(services, endpoint, builder.InboxConfiguration);

        // WithCircuitBreaker must take effect on the in-memory path too — a test
        // suite silently exercising a different middleware composition than
        // production is worse than failing. The SDK's ISubscriberClient startup
        // validator is intentionally omitted: this path composes IMessageHandler
        // directly.
        NimBus.SDK.Extensions.ServiceCollectionExtensions.RegisterCircuitBreakerCore(
            services, endpoint, builder.CircuitBreakerConfiguration);

        services.TryAddSingleton<IMessageHandler>(sp =>
        {
            var eventHandlerProvider = new EventHandlerProvider(
                sp.GetRequiredService<IServiceScopeFactory>());
            var responseBus = new InMemoryMessageBus();
            var responseService = new ResponseService(responseBus);

            foreach (var registration in builder.HandlerRegistrations)
            {
                registration.Register(sp, eventHandlerProvider);
            }

            IEventContextHandler contextHandler = InboxRegistration.Decorate(
                sp,
                eventHandlerProvider,
                builder.InboxConfiguration,
                endpoint);

            IRetryPolicyProvider? retryPolicyProvider = null;
            if (builder.RetryPolicyConfigurator != null)
            {
                var provider = new DefaultRetryPolicyProvider();
                builder.RetryPolicyConfigurator(provider);
                retryPolicyProvider = provider;
            }
            else
            {
                retryPolicyProvider = sp.GetService<IRetryPolicyProvider>();
            }

            var logger = sp.GetService<ILogger<StrictMessageHandler>>()
                ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;

            return new StrictMessageHandler(
                contextHandler,
                responseService,
                logger,
                retryPolicyProvider,
                sp.GetService<MessagePipeline>(),
                sp.GetService<MessageLifecycleNotifier>(),
                sp.GetService<IFailureDispositionClassifier>(),
                InboxRegistration.CreateDuplicateDetector(sp, builder.InboxConfiguration, endpoint));
        });

        return services;
    }
}
