using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.E2E;
using Erp.Api;
using Erp.Api.Endpoints;
using Erp.Api.HandoffMode;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NimBus.Core.CloudEvents;
using NimBus.Outbox.SqlServer;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults(options => options.UseStandardResilienceHandler = true);

// Only wire ServiceBus when a real connection string is present. Without this guard,
// Aspire.Azure.Messaging.ServiceBus throws at startup on missing config, Kestrel never
// binds, and the SPA sees ECONNREFUSED. See Crm.Api/Program.cs for the same treatment.
var hasServiceBus = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("servicebus"));
if (hasServiceBus)
{
    builder.AddAzureServiceBusClient("servicebus");
}

var erpConnectionString = builder.Configuration.GetConnectionString("erp")
    ?? throw new InvalidOperationException("ConnectionStrings:erp is required.");

builder.Services.AddDbContext<ErpDbContext>(opt => opt.UseSqlServer(erpConnectionString));

builder.Services.AddSingleton<ServiceModeState>();
builder.Services.AddSingleton<ErrorModeState>();
builder.Services.AddSingleton<ProcessingDelayState>();
builder.Services.AddSingleton<HandoffModeState>();
builder.Services.AddSingleton<HandoffJobTracker>();
builder.Services.AddSingleton<AlertsState>();

// ERP hosts the outbox dispatcher (the Functions adapter doesn't host long-running polling).
builder.Services.AddNimBusSqlServerOutbox(erpConnectionString);
if (hasServiceBus)
{
    // ERP publishes its events as CloudEvents 1.0 (binary mode) so NimBus-free
    // consumers like the PartnerPortal can read them. The envelope survives the
    // SQL outbox: Message.CloudEvent is serialized into the Payload column and the
    // dispatcher re-emits the same CloudEvent shape.
    builder.Services.AddNimBusPublisher(options =>
    {
        options.Endpoint = "ErpEndpoint";
        options.UseCloudEvents(ce =>
        {
            ce.Source = new Uri("urn:crmerpdemo:erp");
            ce.ContentMode = CloudEventContentMode.Binary;
        });
    });
    builder.Services.AddSingleton<OutboxDispatcherSender>(sp =>
    {
        var client = sp.GetRequiredService<ServiceBusClient>();
        return new OutboxDispatcherSender(client.CreateSender("ErpEndpoint"));
    });
    builder.Services.AddNimBusOutboxDispatcher(TimeSpan.FromSeconds(1));

    // IHandoffClient drives Pending → Completed/Failed transitions for handoff
    // jobs once the ERP "DMF import" completes. Only registered when Service Bus
    // is wired — without it the handoff demo can't issue control messages.
    // AddNimBusHandoffClient also registers the matching IMessageTrackingStore
    // dependency lookup; the SQL Server store is wired separately above.
    builder.Services.AddNimBusHandoffClient("ErpEndpoint");
    builder.Services.AddHostedService<HandoffJobBackgroundService>();
}
else
{
    // Fallback: events go to the outbox only; no dispatcher forwards them anywhere.
    builder.Services.AddSingleton<NimBus.SDK.IPublisherClient>(sp =>
    {
        var outbox = sp.GetRequiredService<NimBus.Core.Outbox.IOutbox>();
        var sender = new NimBus.Core.Outbox.OutboxSender(outbox);
        return new NimBus.SDK.PublisherClient(sender);
    });
}

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

if (E2eSettings.IsEnabled(builder.Configuration, builder.Environment))
    builder.Services.AddSingleton<E2eRegistry>();
var app = builder.Build();
var e2e = app.MapE2eControls();
e2e?.MapE2eWireControls("ErpEndpoint");
e2e?.MapE2eOutboxControls();
e2e?.MapPost("/handoff-jobs/{eventId}/release", (string eventId, HandoffJobTracker tracker) =>
    tracker.Release(eventId) ? Results.NoContent() : Results.NotFound());
app.UseCors();
// The Azure deployment copies the built web UI into wwwroot (aspire publish/deploy), so the
// UI's relative /api calls stay same-origin. Locally the Vite dev server serves the UI and
// there is no wwwroot, so these are no-ops.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapDefaultEndpoints();

// See Crm.Api/Program.cs — same retry + non-fatal startup pattern.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var initSucceeded = false;
for (var attempt = 1; attempt <= 10 && !initSucceeded; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpDbContext>();
        // See Crm.Api/Program.cs for the rationale — EnsureCreatedAsync is a no-op when
        // Aspire has pre-created the database; check for the specific Customers table
        // because HasTablesAsync will return true for the outbox table alone.
        var creator = (IRelationalDatabaseCreator)db.GetService<IDatabaseCreator>();
        if (!await creator.ExistsAsync())
            await creator.CreateAsync();
        var customersExists = await db.Database.SqlQueryRaw<int>(
            "SELECT CASE WHEN OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL THEN 1 ELSE 0 END AS Value")
            .FirstAsync();
        if (customersExists == 0)
            await creator.CreateTablesAsync();

        // Idempotent column migrations: keep older dev DBs working as the model
        // evolves. Avoids forcing a manual DB drop on every demo iteration.
        await db.Database.ExecuteSqlRawAsync(@"
            IF COL_LENGTH('dbo.Customers', 'Origin') IS NULL
                ALTER TABLE dbo.Customers ADD Origin NVARCHAR(8) NOT NULL CONSTRAINT DF_Customers_Origin DEFAULT '';
            IF COL_LENGTH('dbo.Contacts', 'Origin') IS NULL
                ALTER TABLE dbo.Contacts ADD Origin NVARCHAR(8) NOT NULL CONSTRAINT DF_Contacts_Origin DEFAULT '';
            IF COL_LENGTH('dbo.Customers', 'IsDeleted') IS NULL
                ALTER TABLE dbo.Customers ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Customers_IsDeleted DEFAULT 0;
            IF COL_LENGTH('dbo.Contacts', 'IsDeleted') IS NULL
                ALTER TABLE dbo.Contacts ADD IsDeleted BIT NOT NULL CONSTRAINT DF_Contacts_IsDeleted DEFAULT 0;
            IF COL_LENGTH('dbo.Customers', 'CreditHold') IS NULL
                ALTER TABLE dbo.Customers ADD CreditHold BIT NOT NULL CONSTRAINT DF_Customers_CreditHold DEFAULT 0;
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

        var outbox = (SqlServerOutbox)scope.ServiceProvider.GetRequiredService<NimBus.Core.Outbox.IOutbox>();
        await outbox.EnsureTableExistsAsync();
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

app.MapCustomerEndpoints();
app.MapErpContactEndpoints();
app.MapAdminEndpoints();
app.MapAlertsEndpoints();
app.MapHandoffEndpoints();
app.MapAuditEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
