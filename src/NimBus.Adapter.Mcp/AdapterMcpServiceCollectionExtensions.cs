using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using NimBus.Adapter.Mcp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers and maps the adapter MCP server (Spec 051 §7).</summary>
public static class AdapterMcpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the adapter MCP server over <typeparamref name="TSystem"/>, with its options
    /// bound from the <c>AdapterMcp</c> section, the token validator, the call log and
    /// <see cref="AdapterMcpHandler"/>. Serve it with <see cref="MapAdapterMcp"/>, or from a
    /// Functions HttpTrigger through <see cref="AdapterMcpHandler.HandleAsync"/>.
    /// </summary>
    /// <typeparam name="TSystem">The adapter's view of its system, registered scoped.</typeparam>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The configuration holding the <c>AdapterMcp</c> section.</param>
    /// <remarks>
    /// The SDK's MCP server is registered for this endpoint. A host that serves another MCP server
    /// of its own would share that registration; adapters are not expected to.
    /// </remarks>
    public static IServiceCollection AddAdapterMcp<TSystem>(this IServiceCollection services, IConfiguration configuration)
        where TSystem : class, IAdapterSystem
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AdapterMcpOptions>()
            .Bind(configuration.GetSection(AdapterMcpOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.TenantId) && !string.IsNullOrWhiteSpace(options.Audience),
                $"{AdapterMcpOptions.SectionName}:{nameof(AdapterMcpOptions.TenantId)} and {AdapterMcpOptions.SectionName}:{nameof(AdapterMcpOptions.Audience)} are required: the tenant and the client id of the adapter MCP resource.")
            .ValidateOnStart();
        services.AddScoped<IAdapterSystem, TSystem>();
        AdapterCallLog.Register(services);
        services.TryAddSingleton(sp => new AdapterTokenValidator(sp.GetRequiredService<IOptions<AdapterMcpOptions>>().Value));
        services.TryAddSingleton(sp => new AdapterRecordMasker(sp.GetRequiredService<IOptions<AdapterMcpOptions>>().Value.PiiHashSalt));
        services.TryAddSingleton<AdapterReadLog>();
        services.TryAddSingleton(sp => new SdkStreamableHttp(sp));
        services.TryAddSingleton(sp => new AdapterMcpHandler(sp.GetRequiredService<AdapterTokenValidator>(), sp.GetRequiredService<SdkStreamableHttp>()));
        services.AddRouting();

        services.AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = "nimbus-adapter",
                    Title = "NimBus adapter",
                    // The package's release version; the deployment's commit is in adapter_get_health.
                    Version = typeof(AdapterMcpHandler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0",
                };
                server.ServerInstructions = "Read-only access to an adapter's own system: a record by entity and key, the adapter's mapping of an event type, its last call for a key and its health. Sensitive fields are masked.";
            })
            .WithHttpTransport(http => http.Stateless = true)
            .WithTools(AdapterTools.Create());

        return services;
    }

    /// <summary>
    /// Maps the adapter MCP endpoint at <paramref name="pattern"/> for every method:
    /// <see cref="AdapterMcpHandler"/> answers POST and refuses the rest. The endpoint signs
    /// callers in itself; it needs no authentication middleware.
    /// </summary>
    /// <param name="endpoints">The host's routes.</param>
    /// <param name="pattern">The route, <c>/mcp</c> by default.</param>
    public static IEndpointConventionBuilder MapAdapterMcp(this IEndpointRouteBuilder endpoints, string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var handler = endpoints.ServiceProvider.GetService<AdapterMcpHandler>()
            ?? throw new InvalidOperationException("Call services.AddAdapterMcp<TSystem>(configuration) before MapAdapterMcp().");
        return endpoints.Map(pattern, (RequestDelegate)handler.HandleAsync);
    }
}
