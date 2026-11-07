#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;
using CoreEndpoint = NimBus.Core.Endpoints.Endpoint;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Hosts the production MCP registration (<c>AddNimBusOperatorMcp</c>, the request guard and
/// <c>MapNimBusOperatorMcp</c>) on a TestServer, in the same pipeline order as
/// <c>Startup.Pipeline</c>, with a stub catalog and stub endpoint authorization.
/// </summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    public const string TenantId = "22222222-2222-2222-2222-222222222222";
    public const string ClientId = "11111111-1111-1111-1111-111111111111";
    public const string RemoteAddressHeader = "X-Test-Remote-Address";

    /// <summary>The client application test tokens are issued to (<c>azp</c>).</summary>
    public const string AgentClientId = "44444444-4444-4444-4444-444444444444";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("nimbus-mcp-test-signing-key-0123456789abcdef"));

    private readonly IHost _host;

    private McpTestHost(IHost host)
    {
        _host = host;
        Server = host.GetTestServer();
    }

    public TestServer Server { get; }

    /// <summary>A service from the host, for seeding state and asserting side effects.</summary>
    public T Get<T>() where T : notnull => _host.Services.GetRequiredService<T>();

    public static string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    /// <summary>Endpoints the stub authorization grants Reader on.</summary>
    public static readonly string[] ReadableEndpoints = ["CrmEndpoint", "ErpEndpoint"];

    public static Task<McpTestHost> StartDisabledAsync()
        => StartAsync("Production", new Dictionary<string, string?> { ["NimBus:Mcp:Enabled"] = "false" });

    public static Task<McpTestHost> StartLocalDevelopmentAsync(Action<IServiceCollection>? configureServices = null)
        => StartAsync("Development", new Dictionary<string, string?>
        {
            ["NimBus:Mcp:Enabled"] = "true",
            ["EnableLocalDevAuthentication"] = "true",
        }, configureServices);

    public static Task<McpTestHost> StartEntraAsync(Action<IServiceCollection>? configureServices = null)
        => StartAsync("Production", new Dictionary<string, string?>
        {
            ["NimBus:Mcp:Enabled"] = "true",
            ["NimBus:Mcp:Entra:TenantId"] = TenantId,
            ["NimBus:Mcp:Entra:ClientId"] = ClientId,
            ["NimBus:Mcp:AllowedOrigins:0"] = "https://agents.example",
        }, configureServices);

    /// <summary>The setting the Aspire AppHost passes, with or without the local-dev bypass.</summary>
    public static Task<McpTestHost> StartAspireAsync(bool localDevBypass)
        => StartAsync("Development", new Dictionary<string, string?>
        {
            ["NimBus:Mcp:EnableForLocalDevelopment"] = "true",
            ["EnableLocalDevAuthentication"] = localDevBypass ? "true" : "false",
        });

    private static async Task<McpTestHost> StartAsync(string environment, Dictionary<string, string?> settings, Action<IServiceCollection>? configureServices = null)
    {
        settings["Environment"] = "test";

        var builder = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatform>(new TestCatalog());
                    services.AddSingleton(new StubAccess());
                    services.AddScoped<IEndpointAuthorizationService, StubAuthorization>();

                    // The shared operator-command path the write tools (and get_message's
                    // version) run through, over an in-memory store with recording sinks.
                    var store = new InMemoryMessageStore();
                    services.AddSingleton(store);
                    services.AddSingleton<IMessageTrackingStore>(store);
                    services.AddSingleton<IEndpointMetadataStore>(store);
                    services.AddSingleton<RecordingManagerClient>();
                    services.AddSingleton<IManagerClient>(sp => sp.GetRequiredService<RecordingManagerClient>());
                    services.AddSingleton<RecordingAuditLog>();
                    services.AddSingleton<IAuditLogService>(sp => sp.GetRequiredService<RecordingAuditLog>());
                    services.AddScoped<IOperatorCommands, OperatorCommandCoordinator>();

                    // The REST implementations the read tools delegate to. Unconfigured
                    // fakes by default; tests replace them through configureServices.
                    services.AddSingleton(InterfaceFake<NimBus.WebApp.ManagementApi.IEndpointApiController>.Create().Instance);
                    services.AddSingleton(InterfaceFake<NimBus.WebApp.ManagementApi.IEventApiController>.Create().Instance);
                    services.AddSingleton(InterfaceFake<NimBus.WebApp.ManagementApi.IMonitorApiController>.Create().Instance);
                    services.AddSingleton(InterfaceFake<NimBus.WebApp.ManagementApi.IMessageApiController>.Create().Instance);
                    services.AddSingleton(InterfaceFake<NimBus.WebApp.ManagementApi.IMetricsApiController>.Create().Instance);
                    services.AddSingleton(InterfaceFake<NimBus.WebApp.Services.Insights.IFailedInsightsQuery>.Create().Instance);

                    // Mirrors the local-dev branch of Startup.AddAuthenticationStack.
                    var authentication = services.AddAuthentication();
                    if (context.HostingEnvironment.IsDevelopment())
                    {
                        authentication.AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(
                            LocalDevAuthHandler.SchemeName, null);
                    }

                    services.AddAuthorization();
                    services.AddNimBusRateLimiting(context.Configuration);
                    services.AddNimBusOperatorMcp(context.Configuration, context.HostingEnvironment);

                    // Validate test tokens offline: no metadata download.
                    services.PostConfigure<JwtBearerOptions>(McpAuthenticationSchemes.Bearer, options =>
                    {
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                            new OpenIdConnectConfiguration { Issuer = Issuer });
                        options.TokenValidationParameters.IssuerSigningKey = SigningKey;
                    });

                    configureServices?.Invoke(services);
                });
                web.Configure(app =>
                {
                    // Test seam: lets a test present a non-loopback caller.
                    app.Use((ctx, next) =>
                    {
                        if (ctx.Request.Headers.TryGetValue(RemoteAddressHeader, out var address))
                            ctx.Connection.RemoteIpAddress = IPAddress.Parse(address!);
                        return next(ctx);
                    });
                    app.UseRouting();
                    app.UseNimBusOperatorMcpGuard();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapNimBusOperatorMcp());
                });
            });

        return new McpTestHost(await builder.StartAsync());
    }

    /// <summary>Creates an MCP client over the TestServer, optionally with a bearer token and Origin.</summary>
    public async Task<McpClient> CreateClientAsync(string? bearerToken = null, string? origin = null)
    {
        var http = Server.CreateClient();
        if (bearerToken is not null)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        if (origin is not null)
            http.DefaultRequestHeaders.Add("Origin", origin);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(Server.BaseAddress, "/mcp") },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>Sends a raw MCP tools/list POST, for asserting status codes the client would hide.</summary>
    public async Task<HttpResponseMessage> PostToolsListAsync(Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure?.Invoke(request);
        return await Server.CreateClient().SendAsync(request);
    }

    public static string CreateToken(
        string audience = ClientId,
        string? scopes = "nimbus.observe",
        string? roles = null,
        string issuer = "",
        string oid = "33333333-3333-3333-3333-333333333333",
        string azp = AgentClientId)
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = oid,
            ["tid"] = TenantId,
            ["azp"] = azp,
            ["name"] = "Agent Operator",
        };
        if (scopes is not null) claims["scp"] = scopes;
        if (roles is not null) claims["roles"] = new[] { roles };

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer.Length == 0 ? Issuer : issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private sealed class TestCatalog : Platform
    {
        public TestCatalog()
        {
            AddEndpoint(new CrmEndpoint());
            AddEndpoint(new ErpEndpoint());
            AddEndpoint(new BillingEndpoint());
        }
    }

    private sealed class CustomerChanged : Event { }

    private sealed class CrmEndpoint : CoreEndpoint
    {
        public CrmEndpoint() => Produces<CustomerChanged>();
    }

    private sealed class ErpEndpoint : CoreEndpoint
    {
        public ErpEndpoint() => Consumes<CustomerChanged>();
    }

    private sealed class BillingEndpoint : CoreEndpoint { }

    private sealed class StubAuthorization(StubAccess access) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => Task.FromResult(Grants(required, endpointId, access.Contributor));

        public Task<bool> HasRoleFreshAsync(AccessRole required, string endpointId)
            => Task.FromResult(Grants(required, endpointId, access.ContributorFresh ?? access.Contributor));

        private bool Grants(AccessRole required, string? endpointId, bool contributor)
        {
            if (endpointId is null)
                return required <= AccessRole.Reader && access.SiteReader;

            var readable = ReadableEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase);
            return readable && (required <= AccessRole.Reader || (contributor && required <= AccessRole.Contributor));
        }

        public Task<bool> CanReadPiiAsync() => Task.FromResult(access.PiiReader);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync()
            => Task.FromResult(new CurrentUserAccess
            {
                ObjectId = "33333333-3333-3333-3333-333333333333",
                SiteRole = access.SiteReader ? AccessRole.Reader : AccessRole.None,
                IsPiiReader = access.PiiReader,
                EndpointRoles = ReadableEndpoints.ToDictionary(
                    id => id, _ => access.Contributor ? AccessRole.Contributor : AccessRole.Reader, StringComparer.OrdinalIgnoreCase),
            });

        public string? GetCurrentUserName() => "Agent Operator";
    }

    /// <summary>What the stub authorization grants beyond Reader on <see cref="ReadableEndpoints"/>.</summary>
    public sealed class StubAccess
    {
        /// <summary>Site-wide Reader, the floor for cross-endpoint search and metrics.</summary>
        public bool SiteReader { get; init; }

        /// <summary>PiiReader: may see raw payloads.</summary>
        public bool PiiReader { get; init; }

        /// <summary>Contributor on <see cref="ReadableEndpoints"/>: may change messages there.</summary>
        public bool Contributor { get; init; }

        /// <summary>The fresh access-control answer, when it differs from the cached grant.</summary>
        public bool? ContributorFresh { get; init; }
    }

    /// <summary>Records the commands the coordinator publishes.</summary>
    public sealed class RecordingManagerClient : IManagerClient
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _sent = new();

        public IReadOnlyList<string> Sent => _sent.ToList();

        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson)
        {
            _sent.Enqueue($"resubmit:{endpoint}:{errorResponse.EventId}");
            return Task.CompletedTask;
        }

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId)
        {
            _sent.Enqueue($"skip:{endpoint}:{errorResponse.EventId}");
            return Task.CompletedTask;
        }
    }

    /// <summary>One recorded audit row.</summary>
    public sealed record AuditRow(MessageAuditType Type, bool Denied, string? Data, bool Required);

    /// <summary>Records audit rows, marking the required ones.</summary>
    public sealed class RecordingAuditLog : IAuditLogService
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<AuditRow> _rows = new();

        public IReadOnlyList<AuditRow> Rows => _rows.ToList();

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            _rows.Enqueue(new AuditRow(type, accessDenied, data, false));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, System.Threading.CancellationToken cancellationToken = default)
        {
            _rows.Enqueue(new AuditRow(type, false, data, true));
            return Task.CompletedTask;
        }
    }
}
