using Microsoft.AspNetCore.DataProtection;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.Csrf;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp;

public partial class Startup
{
    public Startup(IConfiguration configuration, IWebHostEnvironment webEnv)
    {
        Configuration = configuration;
        Env = webEnv;
    }

    public IWebHostEnvironment Env { get; }
    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    // Registration ORDER is load-bearing in places (Identity opt-in must precede the
    // auth-branch ladder's reflection probe; IClaimsTransformation is last-wins;
    // ServiceBus clients must precede the management services that resolve them;
    // the storage provider selected in AddStorage also drives the health checks) —
    // the steps below run in exactly the order the registrations previously appeared.
    public void ConfigureServices(IServiceCollection services)
    {
        EnforceProductionSafeConfiguration();
        AddAuthenticationStack(services);
        AddWebPipeline(services);
        AddPlatformCatalog(services);
        AddServiceBusClients(services);
        var storageProvider = AddStorage(services, Configuration);
        services.AddScoped<IIntegrationIntelligenceHost, WebAppIntegrationIntelligenceHostAdapter>();
        services.AddSingleton<IIntegrationIntelligenceStorageSettings>(sp =>
            new WebAppIntegrationIntelligenceStorageSettings(storageProvider, sp));
        services.AddSingleton<IIntelligenceSettingsStore>(sp => IntelligenceSettingsStore.Create(
            sp.GetRequiredService<IIntegrationIntelligenceStorageSettings>()));
        services.AddSingleton(IntelligenceSettingsSnapshot.Create(Configuration));
        // The same application name is set in IntelligenceSettingsBootstrap so the pre-host
        // settings read can unseal an Admin-saved provider key.
        services.AddDataProtection().SetApplicationName(IntelligenceSecretProtector.ApplicationName);
        services.AddSingleton<IIntelligenceSecretProtector, DataProtectionSecretProtector>();
        services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
        // #296: the generated API controllers require that token on writes from a browser session.
        services.AddNimBusCookieAntiforgery();
        AddManagementServices(services);
        AddSimulation(services);
        AddObservability(services, storageProvider);
        AddAuthorizationAndAuditServices(services);
        // The intelligence adapter depends on the scoped authorization and
        // audit services. Register those before the optional extension so
        // enabled routes can never be discovered with an unconstructible
        // adapter.
        services.AddNimBusIntegrationIntelligence(Configuration);
        AddApiControllers(services);
        // Spec 035: opt-in operator MCP endpoint. After the authentication stack so the
        // local-dev scheme it reuses under Aspire is already registered.
        services.AddNimBusOperatorMcp(Configuration, Env);
    }
}
