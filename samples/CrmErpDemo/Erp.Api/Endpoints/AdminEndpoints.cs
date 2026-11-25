using CrmErpDemo.Contracts.Demo;
using Microsoft.EntityFrameworkCore;

namespace Erp.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin");

        group.MapGet("/service-mode", (ServiceModeState state) =>
        {
            var (enabled, changedAt) = state.Snapshot();
            return Results.Ok(new ServiceModeResponse(enabled, changedAt));
        });

        group.MapPut("/service-mode", (ServiceModeRequest request, ServiceModeState state) =>
        {
            var (enabled, changedAt) = state.Set(request.Enabled);
            return Results.Ok(new ServiceModeResponse(enabled, changedAt));
        });

        group.MapGet("/error-mode", (ErrorModeState state) =>
        {
            var (enabled, reason, failureRate, changedAt) = state.Snapshot();
            return Results.Ok(new ErrorModeResponse(enabled, reason, failureRate, changedAt));
        });

        // Reason and failureRate are optional so existing callers (e2e helpers, the demo film)
        // that only send { enabled } keep working; when given, reason must be a selectable id
        // and failureRate a share in [0, 1].
        group.MapPut("/error-mode", (ErrorModeRequest request, ErrorModeState state) =>
        {
            if (request.Reason is not null && ErpFailureReasons.Find(request.Reason) is null)
            {
                return Results.BadRequest(new
                {
                    error = $"Unknown failure reason '{request.Reason}'. Known: {string.Join(", ", ErpFailureReasons.Selectable.Select(r => r.Id))}.",
                });
            }

            if (request.FailureRate is < 0.0 or > 1.0 || (request.FailureRate is { } rate && double.IsNaN(rate)))
                return Results.BadRequest(new { error = "failureRate must be in [0.0, 1.0]." });

            var (enabled, reason, failureRate, changedAt) = state.Set(request.Enabled, request.Reason, request.FailureRate);
            return Results.Ok(new ErrorModeResponse(enabled, reason, failureRate, changedAt));
        });

        // The failure catalog behind the erp-web dropdown, with Random last; shared with the
        // adapter through CrmErpDemo.Contracts so the API, the thrown exception and the UI cannot drift.
        group.MapGet("/error-mode/reasons", () => Results.Ok(ErpFailureReasons.Selectable));

        group.MapGet("/processing-delay", (ProcessingDelayState state) =>
        {
            var (enabled, delayMs, changedAt) = state.Snapshot();
            return Results.Ok(new ProcessingDelayResponse(enabled, delayMs, changedAt));
        });

        group.MapPut("/processing-delay", (ProcessingDelayRequest request, ProcessingDelayState state) =>
        {
            if (request.DelayMs < ProcessingDelayState.MinDelayMs || request.DelayMs > ProcessingDelayState.MaxDelayMs)
            {
                return Results.BadRequest(new
                {
                    error = $"delayMs must be in [{ProcessingDelayState.MinDelayMs}, {ProcessingDelayState.MaxDelayMs}].",
                });
            }

            var (enabled, delayMs, changedAt) = state.Set(request.Enabled, request.DelayMs);
            return Results.Ok(new ProcessingDelayResponse(enabled, delayMs, changedAt));
        });

        // Demo reset: wipes every ERP business row plus the NimBus outbox rows
        // living in this database — pending outbox rows would otherwise publish
        // ghost events about entities that no longer exist. Publishes NO delete
        // events; NimBus message history is untouched.
        // TRUNCATE in one transaction: all or nothing, and done in seconds even on the
        // Azure demo's Basic database (see DemoReset).
        group.MapDelete("/data", async (ErpDbContext db) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var contacts = await DemoReset.TruncateAsync(db, "[dbo].[Contacts]");
            var customers = await DemoReset.TruncateAsync(db, "[dbo].[Customers]");
            var audits = await DemoReset.TruncateAsync(db, "[dbo].[Audits]");
            var outboxRows = await DemoReset.TruncateAsync(db, "[nimbus].[OutboxMessages]");
            await transaction.CommitAsync();
            return Results.Ok(new DataResetResponse(customers, contacts, audits, outboxRows));
        });
    }
}

public record ServiceModeRequest(bool Enabled);
public record ServiceModeResponse(bool Enabled, DateTimeOffset ChangedAt);
public record ErrorModeRequest(bool Enabled, string? Reason = null, double? FailureRate = null);
public record ErrorModeResponse(bool Enabled, string Reason, double FailureRate, DateTimeOffset ChangedAt);
public record ProcessingDelayRequest(bool Enabled, int DelayMs);
public record ProcessingDelayResponse(bool Enabled, int DelayMs, DateTimeOffset ChangedAt);
public record DataResetResponse(int Customers, int Contacts, int Audits, int OutboxRows);
