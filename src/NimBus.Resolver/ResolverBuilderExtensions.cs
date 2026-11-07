using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Resolver.Services;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.Resolver;

/// <summary>
/// Extension methods to register Resolver services via the NimBus builder.
/// Storage provider registration is the consumer's responsibility — call
/// AddCosmosDbMessageStore() or AddSqlServerMessageStore() in the host
/// composition root before AddResolver().
/// </summary>
public static class ResolverBuilderExtensions
{
    /// <summary>
    /// Adds the Resolver services (Service Bus listener + message handling) to the
    /// NimBus builder. The active storage provider must already be registered.
    /// </summary>
    public static INimBusBuilder AddResolver(this INimBusBuilder builder)
    {
        var services = builder.Services;

        services.AddSingleton<IMessageHandler, ResolverService>();
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

        return builder;
    }
}
