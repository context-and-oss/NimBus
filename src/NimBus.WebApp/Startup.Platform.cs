using NimBus.WebApp.Services.Simulation;
using NimBus.Core;
using NimBus.Core.Messages.PII;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus.Provisioning;
using NimBus.WebApp.Services;
using NimBus.Core.Extensions;
using NimBus.Management.ServiceBus;
using NimBus.MessageStore.SqlServer;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace NimBus.WebApp;

public partial class Startup
{
    private void AddPlatformCatalog(IServiceCollection services)
    {
        // IPlatform catalog selection.
        // By default the WebApp shows the bundled PlatformConfiguration (Storefront/Billing/Warehouse).
        // Samples that define their own platform (e.g. CrmErpDemo) inject the catalog via config:
        //   NimBus:PlatformType     = "CrmErpDemo.Contracts.CrmErpPlatformConfiguration"
        //   NimBus:PlatformAssembly = absolute path to CrmErpDemo.Contracts.dll (optional; required
        //                             when the assembly isn't already loaded in the WebApp process).
        services.AddSingleton<IPlatform>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var typeName = cfg["NimBus:PlatformType"];
            if (string.IsNullOrWhiteSpace(typeName))
                return new PlatformConfiguration();

            var assemblyPath = ResolvePlatformAssemblyPath(cfg["NimBus:PlatformAssembly"], sp);
            System.Reflection.Assembly? assembly = null;
            if (!string.IsNullOrWhiteSpace(assemblyPath) && System.IO.File.Exists(assemblyPath))
                assembly = System.Reflection.Assembly.LoadFrom(assemblyPath);

            var type = assembly?.GetType(typeName, throwOnError: false)
                       ?? Type.GetType(typeName, throwOnError: false);
            if (type == null)
                throw new InvalidOperationException(
                    $"NimBus:PlatformType '{typeName}' could not be resolved. " +
                    (assemblyPath is null
                        ? "Set NimBus:PlatformAssembly to the DLL path, or reference the assembly from NimBus.WebApp."
                        : $"Checked assembly at '{assemblyPath}'."));

            if (!typeof(IPlatform).IsAssignableFrom(type))
                throw new InvalidOperationException($"Type '{typeName}' does not implement IPlatform.");

            return (IPlatform)Activator.CreateInstance(type)!;
        });

        // Field-level PII masking for non-PiiReader responses. Built from the same
        // IPlatform catalog so the event-type -> CLR type map the masker resolves
        // [Sensitive] annotations against is exactly the one the WebApp serves.
        // The optional salt only affects MaskMode.Hash; an empty salt still yields
        // deterministic output, it is simply not environment-specific.
        services.AddSingleton<IEventJsonMasker>(sp => new EventJsonMasker(
            sp.GetRequiredService<IPlatform>(),
            sp.GetRequiredService<IConfiguration>()["NimBus:PiiHashSalt"] ?? string.Empty));

        services.AddSingleton<IEventJsonRedactor>(sp =>
            sp.GetRequiredService<IEventJsonMasker>() as IEventJsonRedactor
            ?? new NullEventJsonRedactor());

        services.AddSingleton<PayloadRedaction>();
    }

    /// <summary>
    /// Resolves <c>NimBus:PlatformAssembly</c>. An absolute path is used as given, which
    /// is how the Aspire samples point at a project's build output. A bare file name is
    /// resolved against the app's own directory: `nb deploy apps --platform-package`
    /// ships the customer's catalog assembly inside the deployment zip, and the deployed
    /// location differs between Windows and Linux App Service — so the setting stays a
    /// file name and the lookup happens here.
    /// </summary>
    private static string? ResolvePlatformAssemblyPath(string? configured, IServiceProvider sp)
    {
        if (string.IsNullOrWhiteSpace(configured) || Path.IsPathRooted(configured))
            return configured;

        foreach (var root in new[]
                 {
                     sp.GetService<IWebHostEnvironment>()?.ContentRootPath,
                     AppContext.BaseDirectory,
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var candidate = Path.Combine(root, configured);
            if (File.Exists(candidate)) return candidate;
        }

        return configured;
    }

    private void AddServiceBusClients(IServiceCollection services)
    {
        // Env-var providers replace `__` with `:`, so the canonical config key
        // for `AzureWebJobsServiceBus__fullyQualifiedNamespace` is read with a colon.
        // We also accept the literal-double-underscore form (for any appsettings.json
        // that uses it as a flat key) and a bare `ServiceBusNamespace` (sb-nimbus-dev)
        // which we expand to its FQDN.
        string? serviceBusFqns = Configuration["AzureWebJobsServiceBus:fullyQualifiedNamespace"]
            ?? Configuration["AzureWebJobsServiceBus__fullyQualifiedNamespace"];
        if (string.IsNullOrWhiteSpace(serviceBusFqns))
        {
            var ns = Configuration["ServiceBusNamespace"];
            if (!string.IsNullOrWhiteSpace(ns))
            {
                serviceBusFqns = ns.Contains('.', StringComparison.Ordinal)
                    ? ns
                    : $"{ns}.servicebus.windows.net";
            }
        }
        string? serviceBusConnection = Configuration.GetConnectionString("servicebus")
            ?? Configuration.GetValue<string>("AzureWebJobsServiceBus");
        if (!string.IsNullOrEmpty(serviceBusFqns) && !serviceBusFqns.Contains("SharedAccessKey="))
        {
            var credential = new DefaultAzureCredential();
            services.AddSingleton(new ServiceBusAdministrationClient(serviceBusFqns, credential));
            services.AddSingleton(new ServiceBusClient(serviceBusFqns, credential));
            services.AddSingleton<IResolverDeadLetterClient>(sp => new ResolverDeadLetterClient(
                new ServiceBusClient(serviceBusFqns, credential, new ServiceBusClientOptions
                {
                    EnableCrossEntityTransactions = true,
                }),
                sp.GetRequiredService<ILogger<ResolverDeadLetterClient>>()));
        }
        else
        {
            services.AddSingleton(new ServiceBusAdministrationClient(serviceBusConnection));
            services.AddSingleton(new ServiceBusClient(serviceBusConnection));
            services.AddSingleton<IResolverDeadLetterClient>(sp => new ResolverDeadLetterClient(
                new ServiceBusClient(serviceBusConnection, new ServiceBusClientOptions
                {
                    EnableCrossEntityTransactions = true,
                }),
                sp.GetRequiredService<ILogger<ResolverDeadLetterClient>>()));
        }
    }

    // Selects the storage provider and registers the NimBus message store.
    // The returned provider name also drives the health-check registrations.
    internal static string AddStorage(IServiceCollection services, IConfiguration configuration)
    {
        // Provider selection is configuration-driven (NimBus__StorageProvider /
        // StorageProvider env-var or appsetting, default 'cosmos'). SQL Server
        // is selected when explicitly configured OR when no Cosmos config is
        // present but a SQL connection string is.
        var storageProvider = configuration.GetValue<string>("NimBus:StorageProvider")
            ?? configuration.GetValue<string>("StorageProvider");
        if (string.IsNullOrWhiteSpace(storageProvider))
        {
            var hasSqlConfig = !string.IsNullOrWhiteSpace(configuration.GetValue<string>("SqlConnection"))
                || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("sqlserver"))
                || !string.IsNullOrWhiteSpace(configuration.GetValue<string>("SqlServerConnection"));
            var hasCosmosConfig = !string.IsNullOrWhiteSpace(configuration.GetValue<string>("CosmosAccountEndpoint"))
                || !string.IsNullOrWhiteSpace(configuration.GetConnectionString("cosmos"))
                || !string.IsNullOrWhiteSpace(configuration.GetValue<string>("CosmosConnection"));
            storageProvider = (hasSqlConfig && !hasCosmosConfig) ? "sqlserver" : "cosmos";
        }

        services.AddNimBus(nimbus =>
        {
            if (string.Equals(storageProvider, "sqlserver", StringComparison.OrdinalIgnoreCase))
            {
                nimbus.AddSqlServerMessageStore();
            }
            else
            {
                nimbus.AddCosmosDbMessageStore();
            }
        });
        return storageProvider;
    }

    private void AddManagementServices(IServiceCollection services)
    {
        services.AddSingleton<IManagerClient, ManagerClient>();

        // Handoff settlement clients for endpoints resolved at runtime (route
        // params / agent zones) — see EventImplementation / AgentImplementation.
        services.AddNimBusHandoffClientFactory();

        services.AddSingleton<ICodeRepoService>(sp => new CodeRepoService(Configuration["RepositoryUrl"]));

        // FakeEventPayloadGenerator is registered as a singleton — it
        // holds no shared mutable state (each call uses a per-call Random
        // instance), so the JIT can keep the heuristic lookup hot.
        services.AddSingleton<FakeEventPayloadGenerator>();

        services.AddSingleton<IServiceBusManagement>(sp => new ServiceBusManagement(
            sp.GetRequiredService<ServiceBusAdministrationClient>(),
            sp.GetService<ILogger<ServiceBusManagement>>()));

        // Rebuilding a subscription after an operator deletes it to discard a backlog
        // is provisioning, so it goes through the provisioner rather than a second
        // implementation that could drift from it.
        services.AddSingleton<ITopologyRebuilder>(sp => new ServiceBusTopologyRebuilder(
            sp.GetRequiredService<ServiceBusAdministrationClient>()));
    }

    // Traffic simulator (Settings → Simulation, /Simulate). Singleton, in-memory state per
    // instance; options are validated at startup, including the production-name exclusion.
    private void AddSimulation(IServiceCollection services)
    {
        services.AddNimBusSimulation(Configuration);
    }
}
