using Aspire.Hosting.Azure;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// `aspire publish` / `aspire deploy` target Azure Container Apps (see the README's
// "Deploy to Azure" section). Run mode (`aspire run`) is unchanged: emulator, SQL
// container, Vite dev servers and the local-dev auth bypass.
var isPublish = builder.ExecutionContext.IsPublishMode;

// NimBus storage provider toggle. Accepts the same flag names as the slim
// src/NimBus.AppHost so muscle memory carries between the two sample apphosts:
//   --NIMBUS_STORAGE_PROVIDER sqlserver  (env or arg)
//   --StorageProvider sqlserver          (legacy synonym, kept for backwards compat)
//   NIMBUS_STORAGE_PROVIDER env var
// Default is sqlserver, which runs entirely locally on the Aspire-managed SQL
// Server container. The Azure deployment always uses SQL Server (Azure SQL).
var storageProvider = (
        Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER")
        ?? builder.Configuration["NIMBUS_STORAGE_PROVIDER"]
        ?? builder.Configuration["StorageProvider"]
        ?? "sqlserver")
    .ToLowerInvariant();

if (storageProvider is not ("sqlserver" or "cosmos"))
{
    throw new InvalidOperationException(
        $"Unknown StorageProvider '{storageProvider}'. Use 'sqlserver' (default) or 'cosmos'.");
}

if (isPublish && storageProvider != "sqlserver")
{
    throw new InvalidOperationException("The Azure deployment provisions Azure SQL; publish with StorageProvider 'sqlserver'.");
}

// Optional: enable NimBus.Extensions.Identity (username/password sign-in) on the
// nimbus-ops WebApp. Off by default locally — the demo's e2e Playwright suite relies on
// the LocalDev auth bypass below. Same flag shape as the slim NimBus.AppHost.
// Always on in the Azure deployment, where the LocalDev bypass refuses to start.
var identityEnabled = isPublish || string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_IDENTITY") ?? builder.Configuration["NIMBUS_IDENTITY"],
    "true",
    StringComparison.OrdinalIgnoreCase);

if (isPublish)
{
    // Provisions the Container Apps environment, a container registry, the managed
    // identities and the Aspire dashboard.
    builder.AddAzureContainerAppEnvironment("aca");
}

// Service Bus source. The in-process NimBus Service Bus emulator (spec 027) is
// the default, so local/CI demos need neither Azure nor another container. Set
// `UseEmulator=false` (CLI flag) or `NIMBUS_SB_EMULATOR=false` (env var) to use
// a real Azure namespace via the connection string in the AppHost's user-secrets.
// The emulator multiplexes AMQP and the ATOM admin API on one port, so the
// regular runtime provisioner works against it without pre-declared UserConfig.
// The Azure deployment provisions its own namespace with local (SAS) auth disabled,
// so every service connects with its managed identity.
var useEmulator = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_SB_EMULATOR")
        ?? builder.Configuration["UseEmulator"]
        ?? "true",
    "true",
    StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> servicebus;
IResourceBuilder<ProjectResource>? serviceBusEmulatorProject = null;
// Fully qualified namespace (host name) of the Azure deployment's Service Bus. Null in run mode.
ReferenceExpression? serviceBusNamespace = null;
if (isPublish)
{
    var azureServiceBus = builder.AddAzureServiceBus("servicebus");
    servicebus = azureServiceBus;
    serviceBusNamespace = azureServiceBus.Resource.HostName;
}
else if (useEmulator)
{
    var emulator = builder.AddNimBusServiceBusEmulator<Projects.NimBus_ServiceBusEmulator>("servicebus");
    servicebus = emulator.ConnectionString;
    serviceBusEmulatorProject = emulator.Project;
}
else
{
    servicebus = builder.AddConnectionString("servicebus");
}

// SQL Server — locally Aspire spins up a container; in Azure it is an Azure SQL server
// with Entra-only auth (managed identities). CRM and ERP always get their own databases.
// NimBus's message store and Identity live in the nimbus database.
IResourceBuilder<IResourceWithConnectionString> crmDb;
IResourceBuilder<IResourceWithConnectionString> erpDb;
IResourceBuilder<IResourceWithConnectionString>? nimbusDb = null;
if (isPublish)
{
    var azureSql = builder.AddAzureSqlServer("sql");
    crmDb = azureSql.AddDatabase("crm");
    erpDb = azureSql.AddDatabase("erp");
    nimbusDb = azureSql.AddDatabase("nimbus");
}
else
{
    var sql = builder.AddSqlServer("sql")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
    crmDb = sql.AddDatabase("crm");
    erpDb = sql.AddDatabase("erp");
    // nimbusDb is provisioned when the message store needs it OR when Identity
    // needs it. Identity always requires SQL even if the message store is Cosmos.
    if (storageProvider == "sqlserver" || identityEnabled)
    {
        nimbusDb = sql.AddDatabase("nimbus");
    }

    // DbGate — web SQL UI for browsing the Aspire-managed SQL Server, including
    // the ERP [nimbus].[OutboxMessages] table. Wired manually because the
    // CommunityToolkit DbGate package does not ship a WithDbGate() extension for
    // SqlServerServerResource (only Postgres/Mongo/MySQL/Redis).
    builder.AddDbGate("dbgate")
        .WaitFor(sql)
        .WithEnvironment(ctx =>
        {
            ctx.EnvironmentVariables["CONNECTIONS"] = "sql";
            ctx.EnvironmentVariables["LABEL_sql"] = "Aspire SQL Server";
            ctx.EnvironmentVariables["ENGINE_sql"] = "mssql@dbgate-plugin-mssql";
            ctx.EnvironmentVariables["USER_sql"] = "sa";
            ctx.EnvironmentVariables["SERVER_sql"] = sql.Resource.PrimaryEndpoint.Property(EndpointProperty.Host);
            ctx.EnvironmentVariables["PORT_sql"] = sql.Resource.PrimaryEndpoint.Property(EndpointProperty.Port);
            ctx.EnvironmentVariables["PASSWORD_sql"] = sql.Resource.PasswordParameter;
        });
}

// Provision topics/subscriptions for CrmEndpoint + ErpEndpoint at runtime.
// Runs in both modes — the NimBus emulator serves the ATOM admin API, so
// ServiceBusTopologyProvisioner works against it exactly like real Azure.
// In Azure, WaitForCompletion has no effect, so the provisioner becomes a manually
// triggered Container Apps job that is started once after each deploy.
var provisioner = builder.AddProject<Projects.CrmErpDemo_Provisioner>("provisioner")
    .WithReference(servicebus);
if (serviceBusEmulatorProject is not null)
{
    provisioner.WaitFor(serviceBusEmulatorProject);
}
if (isPublish)
{
    provisioner.PublishAsAzureContainerAppJob();
}

// Reused operator surface — the same Resolver + WebApp used in the main NimBus.AppHost.
//
// Endpoint ports for crm-api / erp-api / nimbus-ops are pinned locally to the values
// in each project's launchSettings.json so the e2e Playwright suite (under
// samples/CrmErpDemo/e2e/) can target deterministic URLs without scraping the
// Aspire dashboard for DCP-assigned ports.
var resolver = builder.AddAzureFunctionsProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver");
WithFunctionsServiceBus(resolver);

// Point nimbus-ops at the CRM/ERP platform catalog instead of the default
// Storefront/Billing/Warehouse one, so Endpoints/EventTypes show Crm & Erp.
// Locally the WebApp loads the catalog from the AppHost's build output. The Azure image
// carries a copy of the assembly next to the WebApp (NimBusPlatformAssemblyPath below),
// so the setting is just its file name — the same mechanism `nb deploy apps
// --platform-package` uses.
var crmErpContractsPath = typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).Assembly.Location;
var nimbusOps = builder.AddProject<Projects.NimBus_WebApp>("nimbus-ops")
    .WithReference(servicebus)
    .WithEnvironment("NimBus__PlatformType", typeof(CrmErpDemo.Contracts.CrmErpPlatformConfiguration).FullName!)
    .WithEnvironment("NimBus__PlatformAssembly", isPublish ? Path.GetFileName(crmErpContractsPath) : crmErpContractsPath)
    .WithExternalHttpEndpoints();

if (isPublish)
{
    // Read by NimBus.WebApp.csproj when the container image is built: copies the catalog
    // assembly into the publish output. Container builds run as child processes of this
    // AppHost and MSBuild picks environment variables up as properties.
    Environment.SetEnvironmentVariable("NimBusPlatformAssemblyPath", crmErpContractsPath);

    nimbusOps
        // The WebApp keeps SignalR connections, simulation state and the agent registry
        // in memory, so it must not scale out.
        .PublishAsAzureContainerApp((_, app) => PinToOneReplica(app))
        // Container Apps ingress forwards the client IP; the login rate limiter needs it.
        .WithEnvironment("RateLimiting__TrustForwardedForHeader", "true");
    WithFunctionsServiceBus(nimbusOps);
}
else
{
    nimbusOps
        .WithEndpoint("http", e => e.Port = 18080)
        .WithEndpoint("https", e => e.Port = 18443);
}

if (identityEnabled)
{
    nimbusOps
        .WithReference(nimbusDb!)
        .WithEnvironment("NimBusIdentity__ConnectionString", nimbusDb!.Resource.ConnectionStringExpression)
        .WithEnvironment("NimBusIdentity__RequireEmailConfirmation", "false")
        .WaitFor(nimbusDb);

    if (isPublish)
    {
        // Deploy-time inputs (Parameters__ops_admin_email / Parameters__ops_admin_password).
        // The bootstrap admin is created on the first start against an empty user store.
        nimbusOps
            .WithEnvironment("NimBusIdentity__Bootstrap__Email", builder.AddParameter("ops-admin-email"))
            .WithEnvironment("NimBusIdentity__Bootstrap__Password", builder.AddParameter("ops-admin-password", secret: true));
    }
    else
    {
        var adminEmail = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_EMAIL")
            ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_EMAIL"]
            ?? "admin@local";
        var adminPassword = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_PASSWORD")
            ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_PASSWORD"]
            ?? "Local!Admin123";

        nimbusOps
            .WithEnvironment("NimBusIdentity__Bootstrap__Email", adminEmail)
            .WithEnvironment("NimBusIdentity__Bootstrap__Password", adminPassword);

        Console.WriteLine(
            $"Local Identity: enabled. Sign in at /account/login as {adminEmail} " +
            "(override with NIMBUS_IDENTITY_ADMIN_EMAIL / NIMBUS_IDENTITY_ADMIN_PASSWORD).");
    }
}
else
{
    // Local-dev auth bypass — required so the e2e Playwright suite (and a
    // human operator browsing the dashboard) can hit the API without an
    // Azure AD ClientId. Aspire defaults ASPNETCORE_ENVIRONMENT to Development
    // for child projects, which is the gate Startup.cs checks before honoring
    // this flag.
    nimbusOps.WithEnvironment("EnableLocalDevAuthentication", "true");
}

// Live Flow / Monitor realtime push (spec 020). SQL storage has no Cosmos
// Change Feed, so point the Resolver's write-path notifier at the WebApp's
// storage-hook webhook; the WebApp downloads the counts and broadcasts the
// endpointupdate that the Flow page animates. Best-effort — failures are
// swallowed and the pages reconcile via polling.
resolver.WithEnvironment("NimBus__Flow__WebAppUrl", nimbusOps.GetEndpoint("http"));
if (isPublish)
{
    // Outside Development the WebApp only accepts storage-hook calls that carry the
    // shared webhook key. Generated on the first deploy and kept in the deployment state.
    var flowWebhookKey = builder.AddParameter(
        "flow-webhook-key",
        new GenerateParameterDefault { MinLength = 32, Special = false },
        secret: true,
        persist: true);
    resolver.WithEnvironment("EventGrid__WebhookKey", flowWebhookKey);
    nimbusOps.WithEnvironment("EventGrid__WebhookKey", flowWebhookKey);
}

// Provider-specific wiring. The WebApp/Resolver pick their backend off NimBus__StorageProvider;
// we set it explicitly so the AppHost CLI flag wins over the runtime auto-detect fallback.
if (storageProvider == "sqlserver")
{
    // Aspire's WithReference(<database>) exposes the connection under ConnectionStrings:<dbname>.
    // The SQL Server provider looks up SqlConnection / ConnectionStrings:sqlserver / SqlServerConnection,
    // so bridge nimbusDb's connection string into ConnectionStrings__sqlserver.
    resolver.WithReference(nimbusDb!)
            .WithEnvironment("NimBus__StorageProvider", "sqlserver")
            .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
            .WaitFor(nimbusDb);
    nimbusOps.WithReference(nimbusDb!)
             .WithEnvironment("NimBus__StorageProvider", "sqlserver")
             .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
             .WaitFor(nimbusDb);
}
else // cosmos — connection string is supplied via the AppHost user-secret ConnectionStrings:cosmos.
{
    var cosmos = builder.AddConnectionString("cosmos");
    resolver.WithReference(cosmos)
            .WithEnvironment("NimBus__StorageProvider", "cosmos");
    nimbusOps.WithReference(cosmos)
             .WithEnvironment("NimBus__StorageProvider", "cosmos");
}

// CRM side.
var crmApi = builder.AddProject<Projects.Crm_Api>("crm-api")
    .WithReference(servicebus)
    .WithReference(crmDb)
    .WithExternalHttpEndpoints()
    .WaitFor(crmDb)
    .WaitForCompletion(provisioner);

var crmAdapter = builder.AddProject<Projects.Crm_Adapter>("crm-adapter")
    .WithReference(servicebus)
    .WithReference(crmApi)
    // The crm business DB doubles as the inbox deduplication store
    // ([nimbus].[InboxMessages]) — see the inbox showcase in the README.
    .WithReference(crmDb)
    .WaitFor(crmDb)
    .WaitFor(crmApi)
    .WaitForCompletion(provisioner);

var crmWeb = builder.AddViteApp("crm-web", "../Crm.Web")
    .WithReference(crmApi)
    .WaitFor(crmApi);

// ERP side.
var erpApi = builder.AddProject<Projects.Erp_Api>("erp-api")
    .WithReference(servicebus)
    .WithReference(erpDb)
    .WithExternalHttpEndpoints()
    .WaitFor(erpDb)
    .WaitForCompletion(provisioner);

var erpAdapter = builder.AddAzureFunctionsProject<Projects.Erp_Adapter_Functions>("erp-adapter")
    .WithReference(servicebus)
    .WithReference(erpApi)
    .WithEnvironment("TopicName", "ErpEndpoint")
    .WithEnvironment("SubscriptionName", "ErpEndpoint")
    .WaitFor(erpApi)
    .WaitForCompletion(provisioner);
WithFunctionsServiceBus(erpAdapter);

var erpWeb = builder.AddViteApp("erp-web", "../Erp.Web")
    .WithReference(erpApi)
    .WaitFor(erpApi);

if (isPublish)
{
    // The Vite dev-server proxy does not exist in Azure: each API serves its built UI
    // from wwwroot, so the UI's relative /api calls stay same-origin. The APIs keep
    // demo toggles and handoff jobs in memory, so they (and the long-running
    // subscribers) run exactly one replica.
    crmApi.PublishWithContainerFiles(crmWeb, "./wwwroot")
          .PublishAsAzureContainerApp((_, app) => PinToOneReplica(app));
    erpApi.PublishWithContainerFiles(erpWeb, "./wwwroot")
          .PublishAsAzureContainerApp((_, app) => PinToOneReplica(app));
    crmAdapter.PublishAsAzureContainerApp((_, app) => PinToOneReplica(app));
}
else
{
    crmApi.WithEndpoint("http", e => e.Port = 5080);
    erpApi.WithEndpoint("http", e => e.Port = 5090);
    crmWeb.WithExternalHttpEndpoints();
    erpWeb.WithExternalHttpEndpoints();
}

// DataPlatform sink. Pure terminal subscriber for ErpCustomerCreated —
// mirrors the Erp adapter's Functions hosting model. No Api / Web companion
// projects (the handler logs the inbound event; a real sink would write to
// a lake / warehouse here).
var dataPlatformAdapter = builder
    .AddAzureFunctionsProject<Projects.DataPlatform_Adapter_Functions>("dataplatform-adapter")
    .WithReference(servicebus)
    .WithEnvironment("TopicName", "DataPlatformEndpoint")
    .WithEnvironment("SubscriptionName", "DataPlatformEndpoint")
    .WaitForCompletion(provisioner);
WithFunctionsServiceBus(dataPlatformAdapter);

// PartnerPortal — simulated EXTERNAL partner for the CloudEvents interop showcase.
// Deliberately references only Azure.Messaging.ServiceBus (zero NimBus): it publishes
// raw CloudEvents leads to the PartnerInbound topic (drained by crm-adapter's second
// receiver in AutoDetect mode) and reads ERP CloudEvents from the plain
// ErpEndpoint/PartnerPortalCapture subscription. Waits for crm-adapter so leads
// don't pile up before the subscriber is listening.
var partnerPortal = builder.AddProject<Projects.PartnerPortal>("partner-portal")
    .WithReference(servicebus)
    .WaitFor(crmAdapter)
    .WaitForCompletion(provisioner);
if (isPublish)
{
    partnerPortal.PublishAsAzureContainerApp((_, app) => PinToOneReplica(app));
}

// Agent Zone + EnrichmentAgent (spec 022) run locally only. The agent authenticates to
// nimbus-ops with nothing but an X-Agent-Id header, which the Azure deployment's
// sign-in rejects, and without the agent the Agent Zone would park every
// CrmContactCreated forever.
IResourceBuilder<ProjectResource>? enrichmentAgent = null;
if (!isPublish)
{
    // Agent Zone. The park host subscribes to AgentZoneEndpoint and parks
    // every inbound CrmContactCreated as Pending+Handoff. Wired exactly like the
    // Crm.Adapter worker subscriber (WithReference(servicebus) + gate on the
    // provisioner): the AgentZoneEndpoint topology is created by the provisioner —
    // the same topology gate the other subscribers rely on.
    builder.AddProject<Projects.CrmErpDemo_AgentZone>("agent-zone")
        .WithReference(servicebus)
        .WaitForCompletion(provisioner);

    // EnrichmentAgent. Runs the receive->classify->define->publish->settle
    // loop against the agent REST API on nimbus-ops. nimbus-ops is registered
    // unconditionally above, so the agent binds to it directly — service discovery
    // rewrites "https+http://nimbus-ops" to the resolved endpoint. ANTHROPIC_API_KEY
    // is forwarded when present; absent, the agent uses its deterministic classifier.
    enrichmentAgent = builder.AddProject<Projects.EnrichmentAgent>("enrichment-agent")
        .WithReference(nimbusOps)
        .WaitFor(nimbusOps)
        .WithEnvironment("ANTHROPIC_API_KEY", builder.Configuration["ANTHROPIC_API_KEY"] ?? "");
}

// Explicit test profile. Normal demo runs never expose E2E controls.
if (builder.Configuration.GetValue<bool>("E2E:Enabled"))
{
    if (isPublish)
        throw new InvalidOperationException("E2E:Enabled is a local test profile and cannot be published.");

    var e2eKey = builder.Configuration["E2E:Key"];
    if (e2eKey is not { Length: >= 32 })
        throw new InvalidOperationException("E2E:Key must contain at least 32 characters for the test profile.");
    crmApi.WithEnvironment("E2E__Enabled", "true").WithEnvironment("E2E__Key", e2eKey);
    erpApi.WithEnvironment("E2E__Enabled", "true").WithEnvironment("E2E__Key", e2eKey);
    crmAdapter.WithEnvironment("E2E__Enabled", "true").WithEnvironment("E2E__Key", e2eKey);
    crmAdapter.WithEnvironment("DOTNET_ENVIRONMENT", "Development");
    erpAdapter.WithEnvironment("E2E__Enabled", "true").WithEnvironment("E2E__Key", e2eKey);
    erpAdapter.WithEnvironment("AzureFunctionsJobHost__extensions__serviceBus__maxAutoLockRenewalDuration", "00:00:00");
    provisioner.WithEnvironment("E2E__Enabled", "true").WithEnvironment("E2E__Key", e2eKey);
    dataPlatformAdapter.WithEnvironment("AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions", "64")
        .WithEnvironment("AzureFunctionsJobHost__extensions__serviceBus__sessionIdleTimeout", "00:00:01");
    enrichmentAgent!.WithEnvironment("ANTHROPIC_API_KEY", "");
}
builder.Build().Run();

// Functions triggers bind Connection = "AzureWebJobsServiceBus", and the Resolver and
// WebApp switch to managed identity when AzureWebJobsServiceBus__fullyQualifiedNamespace
// is set. Locally the setting carries the connection string instead. Bound from the
// resource's runtime expression so both AddConnectionString (string in user-secrets)
// and the emulator (string materialised at start) flow through the same path.
void WithFunctionsServiceBus<T>(IResourceBuilder<T> resource) where T : IResourceWithEnvironment
{
    if (serviceBusNamespace is not null)
        resource.WithEnvironment("AzureWebJobsServiceBus__fullyQualifiedNamespace", serviceBusNamespace);
    else
        resource.WithEnvironment("AzureWebJobsServiceBus", servicebus.Resource.ConnectionStringExpression);
}

static void PinToOneReplica(Azure.Provisioning.AppContainers.ContainerApp app)
{
    app.Template.Scale.MinReplicas = 1;
    app.Template.Scale.MaxReplicas = 1;
}
