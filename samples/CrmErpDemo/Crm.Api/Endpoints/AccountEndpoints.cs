using Crm.Api.Entities;
using Crm.Api.Mapping;
using Microsoft.EntityFrameworkCore;
using NimBus.SDK;

namespace Crm.Api.Endpoints;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts");

        // ?page= (1-based, 100 rows unless ?pageSize= asks for fewer) answers one page and puts
        // the number of matching rows in X-Total-Count. Without it the whole list comes back.
        group.MapGet("/", async (
            int? page, int? pageSize, string? q, bool? deleted,
            HttpResponse response, CrmDbContext db, CancellationToken cancellationToken) =>
        {
            IQueryable<Account> accounts = db.Accounts;
            if (deleted is { } isDeleted)
            {
                accounts = accounts.Where(a => a.IsDeleted == isDeleted);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                accounts = accounts.Where(a =>
                    a.LegalName.Contains(term) || a.CountryCode.Contains(term) ||
                    (a.TaxId != null && a.TaxId.Contains(term)) ||
                    (a.ErpCustomerNumber != null && a.ErpCustomerNumber.Contains(term)));
            }

            return await accounts
                .OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
                .ToListResultAsync(page, pageSize, response, cancellationToken);
        });

        group.MapGet("/{id:guid}", async (Guid id, CrmDbContext db) =>
            await db.Accounts.FindAsync(id) is { } a ? Results.Ok(a) : Results.NotFound());

        group.MapPost("/", async (Account input, CrmDbContext db, IPublisherClient publisher, ILoggerFactory lf) =>
        {
            var logger = lf.CreateLogger("Crm.Api.AccountEndpoints");
            input.Id = input.Id == Guid.Empty ? Guid.NewGuid() : input.Id;
            input.CreatedAt = DateTimeOffset.UtcNow;
            input.UpdatedAt = null;
            input.Origin = "Crm";

            db.Accounts.Add(input);
            await db.SaveChangesAsync();
            logger.LogInformation("Publishing CrmAccountCreated for {AccountId} ({LegalName})", input.Id, input.LegalName);
            await publisher.Publish(AccountMapper.ToCreatedEvent(input));

            return Results.Created($"/api/accounts/{input.Id}", input);
        });

        group.MapPut("/{id:guid}", async (Guid id, Account input, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Accounts.FindAsync(id);
            if (existing is null) return Results.NotFound();

            existing.LegalName = input.LegalName;
            existing.TaxId = input.TaxId;
            existing.CountryCode = input.CountryCode;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await publisher.Publish(AccountMapper.ToUpdatedEvent(existing));
            return Results.Ok(existing);
        });

        // Called by the CRM adapter when it receives CustomerCreated ack from ERP.
        // Idempotent: second call with same ErpCustomerId is a no-op.
        group.MapPost("/{id:guid}/link-erp", async (Guid id, LinkErpRequest req, CrmDbContext db) =>
        {
            var existing = await db.Accounts.FindAsync(id);
            if (existing is null) return Results.NotFound();
            if (existing.ErpCustomerId == req.ErpCustomerId) return Results.Ok(existing);
            existing.ErpCustomerId = req.ErpCustomerId;
            existing.ErpCustomerNumber = req.CustomerNumber;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });

        // User-driven soft delete: marks IsDeleted=true and publishes CrmAccountDeleted.
        group.MapDelete("/{id:guid}", async (Guid id, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Accounts.FindAsync(id);
            if (existing is null) return Results.NotFound();
            if (existing.IsDeleted) return Results.Ok(existing);

            existing.IsDeleted = true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await publisher.Publish(AccountMapper.ToDeletedEvent(existing));
            return Results.Ok(existing);
        });

        // Adapter-side propagation of an ErpCustomerDeleted event.
        // Marks the matching CRM account (by ErpCustomerId) as deleted; does NOT
        // re-publish (no event emitted from this endpoint).
        group.MapPost("/external/{externalId:guid}/deleted", async (Guid externalId, CrmDbContext db) =>
        {
            var existing = await db.Accounts.FirstOrDefaultAsync(a => a.ErpCustomerId == externalId);
            if (existing is null) return Results.Ok();
            if (existing.IsDeleted) return Results.Ok(existing);
            existing.IsDeleted = true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });

        // Upsert used by the adapter when ERP originates an account.
        // Origin is set to "Erp" on insert and never changed on update — the
        // upsert endpoint is only reachable from ErpCustomerCreated handlers.
        group.MapPut("/external/{externalId:guid}", async (Guid externalId, AccountUpsertRequest req, CrmDbContext db, IPublisherClient publisher) =>
        {
            var existing = await db.Accounts.FirstOrDefaultAsync(a => a.ErpCustomerId == externalId);
            var requestedCrmAccountId = req.CrmAccountId.GetValueOrDefault();
            if (existing is null && requestedCrmAccountId != Guid.Empty)
                existing = await db.Accounts.FindAsync(requestedCrmAccountId);

            if (existing is null)
            {
                existing = new Account
                {
                    Id = requestedCrmAccountId != Guid.Empty
                        ? requestedCrmAccountId
                        : Guid.NewGuid(),
                    LegalName = req.LegalName,
                    TaxId = req.TaxId,
                    CountryCode = req.CountryCode,
                    ErpCustomerId = externalId,
                    ErpCustomerNumber = req.CustomerNumber,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Origin = "Erp",
                };
                db.Accounts.Add(existing);
            }
            else
            {
                existing.LegalName = req.LegalName;
                existing.TaxId = req.TaxId;
                existing.CountryCode = req.CountryCode;
                existing.ErpCustomerId = externalId;
                existing.ErpCustomerNumber = req.CustomerNumber;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync();
            return Results.Ok(existing);
        });
    }
}

public record LinkErpRequest(Guid ErpCustomerId, string CustomerNumber);
public record AccountUpsertRequest(Guid? CrmAccountId, string LegalName, string? TaxId, string CountryCode, string? CustomerNumber);
