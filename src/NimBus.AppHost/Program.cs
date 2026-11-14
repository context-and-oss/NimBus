using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Storage provider selection for local Aspire runs only — deployed environments
// pick their provider through `nb setup` and the bicep templates, not from here.
// Default 'sqlserver' spins up an Aspire-managed SQL Server container, so the
// stack runs entirely locally with nothing to provision first. Cosmos is still
// one switch away: NIMBUS_STORAGE_PROVIDER=cosmos (or --NIMBUS_STORAGE_PROVIDER
// cosmos), which expects a Cosmos connection string in the 'cosmos' parameter.
var storageProvider = (Environment.GetEnvironmentVariable("NIMBUS_STORAGE_PROVIDER")
    ?? builder.Configuration["NIMBUS_STORAGE_PROVIDER"]
    ?? "sqlserver").ToLowerInvariant();

// Optional: enable NimBus.Extensions.Identity (username/password sign-in) for the
// management WebApp. Off by default — set NIMBUS_IDENTITY=true (or pass
// --NIMBUS_IDENTITY true) to opt in. Identity needs SQL, so flipping the switch
// also provisions the SQL container even when storage is Cosmos.
var identityEnabled = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_IDENTITY") ?? builder.Configuration["NIMBUS_IDENTITY"],
    "true",
    StringComparison.OrdinalIgnoreCase);

// Optional: the WebApp's traffic simulator replaces the sample processes. Off by
// default — set NIMBUS_SIMULATION=true (or pass --NIMBUS_SIMULATION true). The
// simulator then owns BillingEndpoint and WarehouseEndpoint and hosts their
// handlers inside the WebApp, so the sample publisher/subscribers are left out
// instead of competing with it for the same subscriptions.
var simulationEnabled = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_SIMULATION") ?? builder.Configuration["NIMBUS_SIMULATION"],
    "true",
    StringComparison.OrdinalIgnoreCase);

var useServiceBusEmulator = string.Equals(
    Environment.GetEnvironmentVariable("NIMBUS_SB_EMULATOR") ?? builder.Configuration["UseEmulator"],
    "true",
    StringComparison.OrdinalIgnoreCase);

IResourceBuilder<IResourceWithConnectionString> servicebus;
IResourceBuilder<ProjectResource>? serviceBusEmulatorProject = null;
if (useServiceBusEmulator)
{
    var emulator = builder.AddNimBusServiceBusEmulator<Projects.NimBus_ServiceBusEmulator>("servicebus");
    servicebus = emulator.ConnectionString;
    serviceBusEmulatorProject = emulator.Project;
}
else
{
    servicebus = builder.AddConnectionString("servicebus");
}

// Aspire-managed SQL Server container — provisioned when storage is sqlserver
// OR when Identity is enabled (Identity always needs SQL, even if messages are
// stored in Cosmos). Persistent container + data volume keep the database across
// AppHost restarts so users don't have to re-bootstrap admins every run.
IResourceBuilder<SqlServerDatabaseResource>? nimbusDb = null;
if (storageProvider == "sqlserver" || identityEnabled)
{
    var sql = builder.AddSqlServer("sqlserver")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume();
    nimbusDb = sql.AddDatabase("nimbusdb");

    // DbGate — web SQL browser for the Aspire-managed SQL Server. Wired manually
    // because the CommunityToolkit DbGate package doesn't ship a WithDbGate()
    // extension for SqlServerServerResource (only Postgres/Mongo/MySQL/Redis).
    // Reads the resource's password parameter so the auto-generated sa password
    // is wired into the container without leaking into config files.
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

// Topology provisioner — runs once then exits
var provisioner = builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus);
if (serviceBusEmulatorProject is not null)
{
    provisioner.WaitFor(serviceBusEmulatorProject);
}

// Resolver Function App. AddAzureFunctionsProject (not AddProject) so Aspire
// allocates the Functions host port and passes it through: as a plain project
// the host falls back to its hardcoded 7071 and refuses to start whenever
// anything else on the machine already holds it — a second AppHost, or a
// previous run's resolver that has not exited yet. Matches CrmErpDemo.AppHost.
var resolver = builder.AddAzureFunctionsProject<Projects.NimBus_Resolver>("resolver")
    .WithReference(servicebus)
    .WithEnvironment("ResolverId", "Resolver")
    .WithEnvironment("AzureWebJobsServiceBus", servicebus.Resource.ConnectionStringExpression)
    .WaitForCompletion(provisioner);

// WebApp (Management UI)
var webapp = builder.AddProject<Projects.NimBus_WebApp>("webapp")
    .WithReference(servicebus)
    .WithExternalHttpEndpoints()
    // Operator MCP endpoint (Spec 035) at <webapp>/mcp. The WebApp maps it only when its
    // local-dev bypass is on (set below unless Identity sign-in is enabled), so no
    // sign-in is needed; otherwise it stays off.
    .WithEnvironment("NimBus__Mcp__EnableForLocalDevelopment", "true")
    .WaitForCompletion(provisioner);

// Live Flow / Monitor realtime push (spec 020). Point the Resolver's
// write-path notifier at the WebApp's storage-hook webhook so endpointupdate
// broadcasts fire for storage providers without a Change Feed (e.g. SQL).
resolver.WithEnvironment("NimBus__Flow__WebAppUrl", webapp.GetEndpoint("http"));

// Endpoint alert mail (the Alerts tab on an endpoint). The Resolver sends it to Mailpit, which
// catches every message and shows it in its web UI, so subscribing any address works without
// a real mail server. Matches CrmErpDemo.AppHost.
var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.33.0")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");
var smtp = mailpit.GetEndpoint("smtp");
resolver
    .WithEnvironment("NimBus__Alerts__Mail__Smtp__Host", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("NimBus__Alerts__Mail__Smtp__Port", smtp.Property(EndpointProperty.Port))
    .WithEnvironment("NimBus__Alerts__Mail__Smtp__UseSsl", "false")
    .WithEnvironment("NimBus__Alerts__Mail__From", "nimbus-alerts@nimbus.example")
    .WithEnvironment("NimBus__Alerts__Mail__WebAppUrl", webapp.GetEndpoint("https"));

// Bind the active storage provider to both runtime services. Each provider package
// resolves its own connection string at runtime.
if (storageProvider == "sqlserver")
{
    // The SQL Server provider in NimBus.MessageStore.SqlServer reads
    // ConnectionStrings:sqlserver / SqlConnection / SqlServerConnection.
    // Bridge nimbusDb's ConnectionStringExpression onto those keys so the
    // runtime picks up the Aspire-managed container without further config.
    // NimBus__StorageProvider must be set explicitly, not just the connection string:
    // Startup.AddStorage only auto-detects SQL when NO Cosmos config is present, and a
    // developer's user secrets usually still carry a CosmosConnection. Without this the
    // stack would run on the SQL container's connection string yet still select Cosmos.
    resolver.WithReference(nimbusDb!)
            .WithEnvironment("NimBus__StorageProvider", "sqlserver")
            .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
            .WaitFor(nimbusDb);
    webapp.WithReference(nimbusDb!)
          .WithEnvironment("NimBus__StorageProvider", "sqlserver")
          .WithEnvironment("ConnectionStrings__sqlserver", nimbusDb!.Resource.ConnectionStringExpression)
          .WaitFor(nimbusDb);
}
else
{
    var cosmos = builder.AddConnectionString("cosmos");
    resolver.WithReference(cosmos).WithEnvironment("NimBus__StorageProvider", "cosmos");
    webapp.WithReference(cosmos).WithEnvironment("NimBus__StorageProvider", "cosmos");
}

if (identityEnabled)
{
    var adminEmail = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_EMAIL")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_EMAIL"]
        ?? "admin@local";
    var adminPassword = Environment.GetEnvironmentVariable("NIMBUS_IDENTITY_ADMIN_PASSWORD")
        ?? builder.Configuration["NIMBUS_IDENTITY_ADMIN_PASSWORD"]
        ?? "Local!Admin123";

    webapp
        .WithReference(nimbusDb!)
        .WithEnvironment("NimBusIdentity__ConnectionString", nimbusDb!.Resource.ConnectionStringExpression)
        .WithEnvironment("NimBusIdentity__RequireEmailConfirmation", "false")
        .WithEnvironment("NimBusIdentity__Bootstrap__Email", adminEmail)
        .WithEnvironment("NimBusIdentity__Bootstrap__Password", adminPassword)
        .WaitFor(nimbusDb);

    Console.WriteLine(
        $"Local Identity: enabled. Sign in at /account/login as {adminEmail} " +
        "(override with NIMBUS_IDENTITY_ADMIN_EMAIL / NIMBUS_IDENTITY_ADMIN_PASSWORD).");
}
else
{
    // Local-dev sign-in bypass. A fresh clone has no WebApp sign-in configured (its
    // appsettings.Development.json is gitignored), so without this the WebApp refuses
    // to start. Aspire runs the WebApp in Development, the only environment in which
    // the WebApp honors this flag. Matches CrmErpDemo.AppHost.
    webapp.WithEnvironment("EnableLocalDevAuthentication", "true");
}

if (simulationEnabled)
{
    // The owned list is never empty, so the configuration binder cannot drop it.
    webapp
        .WithEnvironment("NimBus__Simulation__EnabledByDefault", "true")
        .WithEnvironment("NimBus__Simulation__OwnedEndpoints__0", "BillingEndpoint")
        .WithEnvironment("NimBus__Simulation__OwnedEndpoints__1", "WarehouseEndpoint");

    Console.WriteLine(
        "Traffic simulation: enabled. The WebApp simulates BillingEndpoint and WarehouseEndpoint; " +
        "the sample publisher and subscribers are not started. Open Settings → Simulation, then Simulate.");
}
else
{
    // Sample Publisher (HTTP API for publishing events)
    builder.AddProject<Projects.AspirePubSub_Publisher>("publisher")
        .WithReference(servicebus)
        .WithExternalHttpEndpoints()
        .WaitForCompletion(provisioner);

    // Sample Subscriber (handles events + separated DeferredProcessor)
    builder.AddProject<Projects.AspirePubSub_Subscriber>("subscriber")
        .WithReference(servicebus)
        .WaitForCompletion(provisioner);

    // Warehouse adapter — its own process because a container hosts one subscriber
    // endpoint. Deliberately fails ~30% of what it handles, so the sample always has
    // failures to retry, resubmit and group next to a healthy Billing endpoint. It
    // also keeps WarehouseEndpoint answering heartbeat probes, which only a running
    // subscriber can do.
    builder.AddProject<Projects.AspirePubSub_WarehouseSubscriber>("warehouse-subscriber")
        .WithReference(servicebus)
        .WaitForCompletion(provisioner);
}

builder.Build().Run();
