using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NimBus.Agents.Internal;

namespace NimBus.Agents;

/// <summary>Dependency-injection registration for hosted NimBus agents.</summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a hosted agent: a typed REST gateway over <c>/api/agent/*</c> plus a background
    /// loop that drives <typeparamref name="THandler"/> (subscribe → receive → handle → publish →
    /// settle). The host must call <c>AddServiceDefaults()</c> for service discovery to resolve a
    /// <c>https+http://</c> <see cref="AgentOptions.BaseAddress"/>, or set an absolute URL.
    /// <para>One agent per process — calling this twice in the same container throws.
    /// <see cref="AgentOptions"/> and <see cref="IAgentBusGateway"/> are non-keyed singletons,
    /// so a second agent would silently share the first agent's options and cross-wire its
    /// <c>X-Agent-Id</c> header and base address. Host each additional agent in a separate
    /// process (matching the per-worker Aspire topology in samples/CrmErpDemo).</para>
    /// </summary>
    /// <typeparam name="THandler">The handler implementation.</typeparam>
    /// <typeparam name="TInput">The deserialized source-event payload type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures <see cref="AgentOptions"/>.</param>
    /// <returns>The service collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A NimBus agent was already registered in this container.</exception>
    public static IServiceCollection AddNimBusAgent<THandler, TInput>(this IServiceCollection services, Action<AgentOptions> configure)
        where THandler : class, IAgentHandler<TInput>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AgentOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.AgentId))
            throw new ArgumentException("AgentOptions.AgentId is required.", nameof(configure));
        if (string.IsNullOrWhiteSpace(options.SourceEventTypeId))
            throw new ArgumentException("AgentOptions.Subscribe(eventTypeId) must be called with a source event type.", nameof(configure));
        if (options.ReceiveWaitSeconds is < 0 or > 60)
            throw new ArgumentException("AgentOptions.ReceiveWaitSeconds must be between 0 and 60.", nameof(configure));

        // One-agent-per-process guard. AgentOptions and the IAgentBusGateway HttpClient are
        // non-keyed singletons: a second call would leave the last-registered options winning for
        // every worker, append a duplicate X-Agent-Id header, and (because the second same-TInput
        // AgentLoopWorker is silently deduped) never actually run the second agent.
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(AgentRegistrationMarker)
                && descriptor.ImplementationInstance is AgentRegistrationMarker existing)
            {
                throw new InvalidOperationException(
                    $"AddNimBusAgent was already called for agent '{existing.AgentId}'. " +
                    $"Registering a second agent ('{options.AgentId}') in the same container is not " +
                    "supported — AgentOptions and IAgentBusGateway are non-keyed singletons, so the " +
                    "agents would cross-wire their options, X-Agent-Id header and base address. " +
                    "Host each agent in a separate process (matches the per-worker Aspire topology " +
                    "in samples/CrmErpDemo).");
            }
        }
        services.AddSingleton(new AgentRegistrationMarker(options.AgentId));

        services.AddSingleton(options);
        services.TryAddSingleton<THandler>();
        services.AddSingleton<IAgentHandler<TInput>>(sp => sp.GetRequiredService<THandler>());

        services.AddHttpClient<IAgentBusGateway, RestAgentBusGateway>(client =>
        {
            client.BaseAddress = new Uri(options.BaseAddress);
            client.DefaultRequestHeaders.Add("X-Agent-Id", options.AgentId);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Add("X-NimBus-Agent-Key", options.ApiKey.Trim());
        });

        services.AddHostedService<AgentLoopWorker<TInput>>();
        return services;
    }

    /// <summary>
    /// Sentinel registered by <see cref="AddNimBusAgent{THandler, TInput}"/> to enforce the
    /// one-agent-per-process rule. Its presence signals that an agent is already registered.
    /// </summary>
    private sealed class AgentRegistrationMarker(string agentId)
    {
        public string AgentId { get; } = agentId;
    }
}
