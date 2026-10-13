using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Authorization;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.Mcp.Prompts;
using NimBus.WebApp.Mcp.Tools;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp;

/// <summary>The operator MCP endpoint's resolved settings, registered as a singleton.</summary>
/// <param name="Mode">How callers are authenticated.</param>
/// <param name="Options">The bound <c>NimBus:Mcp</c> options.</param>
public sealed record McpOperatorRuntime(McpAuthenticationMode Mode, McpOperatorOptions Options);

/// <summary>Registers, guards and maps the opt-in operator MCP endpoint (Spec 035).</summary>
public static class McpOperatorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the operator MCP server when <c>NimBus:Mcp:Enabled</c> is set. Tools are
    /// registered explicitly; nothing is discovered by assembly scan. Call after the WebApp's
    /// authentication stack so the local-dev scheme, when active, already exists.
    /// </summary>
    /// <exception cref="InvalidOperationException">Enabled without any usable authentication.</exception>
    public static IServiceCollection AddNimBusOperatorMcp(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = configuration.GetSection(McpOperatorOptions.SectionName).Get<McpOperatorOptions>() ?? new McpOperatorOptions();
        var mode = McpAuthenticationModeResolver.Resolve(
            options,
            environment.IsDevelopment(),
            configuration.GetValue<bool>("EnableLocalDevAuthentication"));

        services.AddSingleton(new McpOperatorRuntime(mode, options));
        if (mode == McpAuthenticationMode.Disabled)
            return services;

        if (mode == McpAuthenticationMode.Entra)
            AddEntraAuthentication(services, options.Entra);

        services.AddAuthorization(authorization => authorization.AddPolicy(McpOperatorPolicies.Observe, policy =>
        {
            if (mode == McpAuthenticationMode.LocalDevelopment)
            {
                policy.AddAuthenticationSchemes(LocalDevAuthHandler.SchemeName).RequireAuthenticatedUser();
                return;
            }

            policy.AddAuthenticationSchemes(McpAuthenticationSchemes.Bearer)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => McpOperatorPermissions.CanObserve(context.User))
                .AddRequirements(new McpAccessRequirement());
        }));

        // Spec 037: the site Owner's access policy, read live from shared storage.
        services.AddSingleton<IMcpAccessPolicyProvider, McpAccessPolicyProvider>();
        services.AddSingleton<McpAccessRefusals>();
        services.AddScoped<IAuthorizationHandler, McpAccessRequirementHandler>();

        services.AddMcpServer(server => server.ServerInfo = new Implementation
            {
                Name = "nimbus-operator",
                Title = "NimBus operator",
                Version = ServerVersion(),
            })
            .WithHttpTransport(http => http.Stateless = true)
            .WithTools<OperatorDiscoveryTools>(ToolSerializerOptions)
            .WithTools<OperatorMessageTools>(ToolSerializerOptions)
            .WithTools<OperatorInsightTools>(ToolSerializerOptions)
            .WithTools<OperatorActionTools>(ToolSerializerOptions)
            .WithPrompts([typeof(OperatorPrompts)])
            .WithRequestFilters(filters => filters
                .AddListToolsFilter(McpToolAccess.FilterList)
                .AddCallToolFilter(McpToolAccess.FilterCall)
                .AddListPromptsFilter(McpPromptAccess.FilterList)
                .AddGetPromptFilter(McpPromptAccess.FilterGet));

        services.AddScoped<OperatorEndpointCatalog>();
        services.AddScoped<OperatorQueries>();
        services.AddScoped<OperatorPayloadAccess>();
        services.AddScoped<IOperatorClassificationSource, OperatorClassificationSource>();
        services.AddScoped<OperatorActionAccess>();
        services.AddSingleton<OperatorActionTokens>();
        services.AddSingleton<OperatorMutationLimiter>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddDataProtection();

        return services;
    }

    /// <summary>Adds the Origin and loopback checks ahead of authentication. No-op when disabled.</summary>
    public static IApplicationBuilder UseNimBusOperatorMcpGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var runtime = app.ApplicationServices.GetService<McpOperatorRuntime>();
        return runtime is null || runtime.Mode == McpAuthenticationMode.Disabled
            ? app
            : app.UseMiddleware<McpRequestGuardMiddleware>();
    }

    /// <summary>
    /// Maps <c>/mcp</c> behind the observe policy and, when rate limiting is on, the MCP rate
    /// limit. Returns null and maps nothing when the endpoint is disabled.
    /// </summary>
    public static IEndpointConventionBuilder? MapNimBusOperatorMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var runtime = endpoints.ServiceProvider.GetService<McpOperatorRuntime>();
        if (runtime is null || runtime.Mode == McpAuthenticationMode.Disabled)
            return null;

        var builder = endpoints.MapMcp(McpOperatorOptions.Path).RequireAuthorization(McpOperatorPolicies.Observe);
        if (endpoints.ServiceProvider.GetService<IOptions<RateLimitOptions>>()?.Value.Enabled == true)
            builder.RequireRateLimiting(RateLimitPolicyNames.Mcp);

        return builder;
    }

    // The release version stamped by the build, without the source-revision suffix.
    // The SDK's defaults omit null members, but the output schemas it derives from the result
    // records mark every member required, so a null member fails client-side validation.
    private static readonly JsonSerializerOptions ToolSerializerOptions = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    internal static string ServerVersion()
    {
        var informational = typeof(McpOperatorServiceCollectionExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "0.0.0" : informational.Split('+')[0];
    }

    private static void AddEntraAuthentication(IServiceCollection services, McpEntraOptions entra)
    {
        var authority = entra.Authority;
        var applicationIdUri = entra.ResolvedApplicationIdUri;

        services.AddAuthentication()
            .AddJwtBearer(McpAuthenticationSchemes.Bearer, jwt =>
            {
                jwt.Authority = authority;
                jwt.MapInboundClaims = false;
                jwt.ForwardChallenge = McpAuthenticationSchemes.Challenge;
                jwt.TokenValidationParameters.ValidAudiences = [entra.ClientId!, applicationIdUri];
                jwt.TokenValidationParameters.ValidIssuers = [authority, $"https://sts.windows.net/{entra.TenantId}/"];
                jwt.TokenValidationParameters.NameClaimType = "name";
                jwt.TokenValidationParameters.RoleClaimType = "roles";
            })
            .AddMcp(McpAuthenticationSchemes.Challenge, "NimBus operator MCP", mcp =>
            {
                // Built per request so the advertised resource matches the host the client used.
                mcp.Events.OnResourceMetadataRequest = context =>
                {
                    var request = context.HttpContext.Request;
                    context.ResourceMetadata = new ProtectedResourceMetadata
                    {
                        Resource = $"{request.Scheme}://{request.Host}{request.PathBase}{McpOperatorOptions.Path}",
                        AuthorizationServers = [authority],
                        ScopesSupported = McpOperatorPermissions.AllScopes.Select(scope => $"{applicationIdUri}/{scope}").ToList(),
                        ResourceName = "NimBus operator MCP",
                    };
                    return Task.CompletedTask;
                };
            });
    }
}
