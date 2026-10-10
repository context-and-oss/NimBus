using Crm.Api;
using CrmErpDemo.Contracts.E2E;
using Crm.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NimBus.SDK;
using NimBus.SDK.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

// Only wire the real ServiceBus client if a connection string was provided.
// Without this guard, Aspire.Azure.Messaging.ServiceBus throws at startup when
// ConnectionStrings:servicebus is missing, Kestrel never binds, and the SPA
// sees ECONNREFUSED. With the guard: the API still comes up, saves to SQL
// succeed, and the only thing that can't happen is publishing to a real topic.
var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
}

var crmConnectionString = builder.Configuration.GetConnectionString("crm")
    ?? throw new InvalidOperationException("ConnectionStrings:crm is required.");

builder.Services.AddDbContext<CrmDbContext>(opt => opt.UseSqlServer(crmConnectionString));

if (hasServiceBus)
{
    builder.Services.AddNimBusPublisher("CrmEndpoint");
}
else
{
    // Keep the CRUD API usable in DB-only smoke tests. Publishing is skipped
    // when Service Bus is not configured.
    builder.Services.AddSingleton<IPublisherClient, NoopPublisherClient>();
}

// Circuit-breaker showcase: the outage toggle the operator flips and the last
// circuit state pushed by the CRM adapter (see Endpoints/AdminEndpoints.cs).
builder.Services.AddSingleton<ErrorModeState>();
builder.Services.AddSingleton<CircuitStateStore>();

// CORS for the Vite SPA.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

if (E2eSettings.IsEnabled(builder.Configuration, builder.Environment))
{
    builder.Services.AddSingleton<E2eRegistry>();
    builder.Services.AddNimBusHandoffClient("CrmEndpoint");
}
var app = builder.Build();
app.MapE2eControls()?.MapE2eWireControls("CrmEndpoint");
app.UseCors();
// The Azure deployment copies the built web UI into wwwroot (aspire publish/deploy), so the
// UI's relative /api calls stay same-origin. Locally the Vite dev server serves the UI and
// there is no wwwroot, so these are no-ops.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapDefaultEndpoints();

// Ensure DB tables exist for the demo.
// Retry so the API survives SQL-container startup timing; if init ultimately fails,
// log the error and keep running so Kestrel binds — endpoints will surface the error
// back to the web client via 500 instead of leaving the port unreachable (ECONNREFUSED).
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var initSucceeded = false;
for (var attempt = 1; attempt <= 10 && !initSucceeded; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CrmDbContext>();
        // EnsureCreatedAsync only creates the schema when it also creates the database.
        // Aspire's SQL Server integration pre-creates the 'crm' database empty, so
        // EnsureCreatedAsync becomes a no-op and Accounts/Contacts never get created.
        // Check for the specific Accounts table instead of relying on a broad
        // database-exists check.
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync())
            await creator.CreateAsync();
        var accountsExists = await db.Database.SqlQueryRaw<int>(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.Accounts', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Value")
            .FirstAsync();
        if (accountsExists == 0)
            await creator.CreateTablesAsync();

        // Idempotent column migrations: keep older dev DBs working as the model
        // evolves. Avoids forcing a manual DB drop on every demo iteration.
        await db.Database.ExecuteSqlRawAsync(@"
            IF COL_LENGTH('dbo.Accounts', 'Origin') IS NULL
                ALTER TABLE dbo.Accounts ADD Origin NVARCHAR(8) NOT NULL CONSTRAINT DF_Accounts_Origin DEFAULT '';
            IF COL_LENGTH('dbo.Contacts', 'Origin') IS NULL
                ALTER TABLE dbo.Contacts ADD Origin NVARCHAR(8) NOT NULL CONSTRAINT DF_Contacts_Origin DEFAULT '';
            IF COL_LENGTH('dbo.Accounts', 'IsDeleted') IS NULL
                ALTER TABLE dbo.Accounts ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Accounts_IsDeleted DEFAULT 0;
            IF COL_LENGTH('dbo.Contacts', 'IsDeleted') IS NULL
                ALTER TABLE dbo.Contacts ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Contacts_IsDeleted DEFAULT 0;
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Audits' AND schema_id = SCHEMA_ID('dbo'))
            BEGIN
                CREATE TABLE dbo.Audits (
                    Id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Audits PRIMARY KEY,
                    EntityType NVARCHAR(50) NOT NULL,
                    EntityId UNIQUEIDENTIFIER NOT NULL,
                    Action NVARCHAR(20) NOT NULL,
                    FieldName NVARCHAR(100) NULL,
                    OldValue NVARCHAR(MAX) NULL,
                    NewValue NVARCHAR(MAX) NULL,
                    Timestamp DATETIMEOFFSET NOT NULL,
                    Origin NVARCHAR(10) NULL
                );
                CREATE INDEX IX_Audits_Entity ON dbo.Audits (EntityType, EntityId, Timestamp DESC);
            END");

        initSucceeded = true;
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Startup init attempt {Attempt} failed: {Message}", attempt, ex.Message);
        if (attempt < 10) await Task.Delay(TimeSpan.FromSeconds(3));
    }
}
if (!initSucceeded)
    startupLogger.LogError("Startup init did not complete after 10 attempts; continuing so Kestrel binds.");

app.MapAccountEndpoints();
app.MapContactEndpoints();
app.MapAuditEndpoints();
app.MapErpIntegrationEndpoints();
app.MapAdminEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
