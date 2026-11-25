using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Endpoints;

/// <summary>
/// Demo admin surface for the circuit-breaker showcase: the outage toggle the
/// operator flips (mirroring Erp.Api's error mode), the live circuit state the
/// SPA polls, and the webhook the CRM adapter pushes transitions into.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/error-mode", (ErrorModeState state) =>
        {
            var (enabled, changedAt) = state.Snapshot();
            return Results.Ok(new ErrorModeResponse(enabled, changedAt));
        });

        group.MapPut("/error-mode", (ErrorModeRequest request, ErrorModeState state) =>
        {
            var (enabled, changedAt) = state.Set(request.Enabled);
            return Results.Ok(new ErrorModeResponse(enabled, changedAt));
        });

        group.MapGet("/circuit-state", (CircuitStateStore store) =>
            Results.Ok(store.Snapshot()));

        // CloudEvents partner showcase: crm-web flips the recurring PartnerPortal leads on and
        // off, and partner-portal reads the flag before each lead.
        group.MapGet("/partner-leads", (PartnerLeadsState state) =>
        {
            var (enabled, changedAt) = state.Snapshot();
            return Results.Ok(new ErrorModeResponse(enabled, changedAt));
        });

        group.MapPut("/partner-leads", (ErrorModeRequest request, PartnerLeadsState state) =>
        {
            var (enabled, changedAt) = state.Set(request.Enabled);
            return Results.Ok(new ErrorModeResponse(enabled, changedAt));
        });

        // Demo reset: wipes every CRM business row plus the NimBus inbox-dedup
        // rows living in this database. Publishes NO delete events — this is a
        // reset, not business activity; NimBus message history is untouched.
        // TRUNCATE in one transaction: all or nothing, and done in seconds even on the
        // Azure demo's Basic database (see DemoReset).
        group.MapDelete("/data", async (CrmDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var contacts = await DemoReset.TruncateAsync(db, "[dbo].[Contacts]");
            var accounts = await DemoReset.TruncateAsync(db, "[dbo].[Accounts]");
            var audits = await DemoReset.TruncateAsync(db, "[dbo].[Audits]");
            var inboxRows = await DemoReset.TruncateAsync(db, "[nimbus].[InboxMessages]");
            await transaction.CommitAsync();
            return Results.Ok(new DataResetResponse(accounts, contacts, audits, inboxRows));
        });

        // Pushed by Crm.Adapter's CircuitStateReporter on every transition.
        app.MapPost("/api/webhooks/circuit-state", (CircuitStateWebhook payload, CircuitStateStore store) =>
        {
            store.Set(
                payload.Endpoint ?? "CrmEndpoint",
                payload.To ?? "Closed",
                payload.Reason ?? string.Empty,
                payload.Timestamp ?? DateTimeOffset.UtcNow);
            return Results.Accepted();
        });
    }
}

public record ErrorModeRequest(bool Enabled);
public record ErrorModeResponse(bool Enabled, DateTimeOffset ChangedAt);
public record CircuitStateWebhook(string? Endpoint, string? From, string? To, string? Reason, DateTimeOffset? Timestamp);
public record DataResetResponse(int Accounts, int Contacts, int Audits, int NimbusRows);
