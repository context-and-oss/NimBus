using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NimBus.Core;

namespace NimBus.WebApp.Services.Simulation;

/// <summary>DI registration for the WebApp traffic simulator.</summary>
public static class SimulationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the simulator: validated options, the singleton <see cref="ISimulationService"/>
    /// and its lifetime service. Requires <see cref="IPlatform"/>, a <c>ServiceBusClient</c> and
    /// <see cref="FakeEventPayloadGenerator"/> to be registered.
    /// </summary>
    public static IServiceCollection AddNimBusSimulation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SimulationOptions>()
            .Bind(configuration.GetSection(SimulationOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SimulationOptions>, SimulationOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISimulatedEndpointHostFactory>(sp => new SimulatedEndpointHostFactory(
            sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<ISimulationPublisherFactory>(sp => new ServiceBusSimulationPublisherFactory(
            sp.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>()));
        services.TryAddSingleton<ISimulationEventFactory>(sp => new FakePayloadSimulationEventFactory(
            sp.GetRequiredService<FakeEventPayloadGenerator>(),
            sp.GetService<IPlatform>()));
        services.TryAddSingleton<ISimulationService>(sp => new SimulationService(
            sp.GetRequiredService<IPlatform>(),
            sp.GetRequiredService<IOptions<SimulationOptions>>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ISimulatedEndpointHostFactory>(),
            sp.GetRequiredService<ISimulationPublisherFactory>(),
            sp.GetRequiredService<ISimulationEventFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IServiceScopeFactory>()));

        // Not AddHostedService: that deduplicates by implementation type via TryAddEnumerable.
        services.AddSingleton<IHostedService>(sp => new SimulationLifetimeService(
            sp.GetRequiredService<ISimulationService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<SimulationLifetimeService>>()));

        return services;
    }
}
