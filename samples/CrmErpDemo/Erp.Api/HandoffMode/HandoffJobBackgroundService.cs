using CrmErpDemo.Contracts.Events;
using Erp.Api.Entities;
using Erp.Api.Mapping;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using NimBus.SDK;

namespace Erp.Api.HandoffMode;

// Drains expired handoff jobs once per second and drives PendingHandoff →
// Completed (or Failed) on the NimBus side via IHandoffClient.
//
// On success: applies the ERP upsert that the user handler skipped (the spec
// guarantees the user handler is NOT re-invoked on settlement, so the ERP
// write has to happen here for the demo to remain end-to-end coherent), then
// publishes ErpCustomerCreated through the same outbox the live handler uses,
// and signals IHandoffClient.CompleteAsync.
//
// On failure: skips the upsert and signals IHandoffClient.FailAsync with a
// canned DMF-style error string. The Resolver flips the audit row to Failed;
// the session stays blocked until the operator clicks Resubmit / Skip in
// NimBus.WebApp.
internal sealed class HandoffJobBackgroundService(
    HandoffJobTracker tracker,
    HandoffModeState modeState,
    IServiceProvider services,
    ILogger<HandoffJobBackgroundService> logger)
    : BackgroundService
{
    private static readonly string[] CannedDmfErrors =
    [
        "DMF rejected: invalid postal code",
        "DMF rejected: duplicate customer number",
        "DMF rejected: tax ID validation failed",
        "DMF rejected: missing mandatory address line",
        "DMF rejected: country code not configured in legal entity",
    ];

    private const string Endpoint = "ErpEndpoint";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = tracker.DrainExpired(DateTime.UtcNow);
                if (expired.Count > 0)
                {
                    logger.LogInformation("Draining {Count} expired handoff job(s).", expired.Count);
                    foreach (var job in expired)
                    {
                        await SettleAsync(job, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handoff drain tick failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SettleAsync(HandoffJob job, CancellationToken cancellationToken)
    {
        var (_, _, failureRate, _) = modeState.Snapshot();
        var roll = Random.Shared.NextDouble();
        var shouldFail = roll < failureRate;

        // Typed coords map straight off the HandoffJob row the adapter
        // persisted at MarkPendingHandoff time. No bag-of-fields ceremony,
        // no tracking-store lookup.
        var coords = new HandoffSettlement(
            EventId: job.EventId,
            SessionId: job.SessionId,
            MessageId: job.MessageId,
            EventTypeId: job.EventTypeId,
            CorrelationId: job.CorrelationId ?? job.MessageId,
            OriginatingMessageId: job.OriginatingMessageId ?? job.MessageId);

        await using var scope = services.CreateAsyncScope();
        var handoff = scope.ServiceProvider.GetRequiredService<IHandoffClient>();

        if (shouldFail)
        {
            var errorText = CannedDmfErrors[Random.Shared.Next(CannedDmfErrors.Length)];
            logger.LogWarning(
                "Handoff job {ExternalJobId} for event {EventId} failing (roll={Roll:F2} < failureRate={FailureRate:F2}): {ErrorText}",
                job.ExternalJobId, job.EventId, roll, failureRate, errorText);
            await handoff.FailAsync(coords, errorText, errorType: "DmfValidationError", cancellationToken);
            return;
        }

        try
        {
            await ApplyUpsertAsync(scope.ServiceProvider, job, cancellationToken);
        }
        catch (Exception ex)
        {
            // If the demo upsert itself blows up, treat it as a DMF failure rather
            // than leaking the exception out of the background tick.
            logger.LogError(ex, "Handoff job {ExternalJobId} upsert failed; settling as failed.", job.ExternalJobId);
            await handoff.FailAsync(coords, $"Erp upsert failed: {ex.Message}", errorType: "DmfValidationError", cancellationToken);
            return;
        }

        logger.LogInformation(
            "Handoff job {ExternalJobId} for event {EventId} completed.",
            job.ExternalJobId, job.EventId);
        await handoff.CompleteAsync(coords, new { importedRecordId = job.ExternalJobId }, cancellationToken);
    }

    private static async Task ApplyUpsertAsync(IServiceProvider scopedServices, HandoffJob job, CancellationToken cancellationToken)
    {
        if (!string.Equals(job.EventTypeId, nameof(CrmAccountCreated), StringComparison.Ordinal))
        {
            // The demo only wires PendingHandoff onto CrmAccountCreated. If we ever
            // start tracking other event types here, this is the line to revisit.
            return;
        }

        var payload = JsonConvert.DeserializeObject<CrmAccountCreated>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Handoff payload was empty for event {job.EventId}.");

        // The same rule the adapter's upsert endpoint applies; the job settles as failed with this text.
        if (Validation.CountryCodes.Problem(payload.CountryCode) is { } problem)
            throw new InvalidOperationException(problem);

        var db = scopedServices.GetRequiredService<ErpDbContext>();
        var publisher = scopedServices.GetRequiredService<IPublisherClient>();

        var existing = await db.Customers.FirstOrDefaultAsync(c => c.CrmAccountId == payload.AccountId, cancellationToken);
        var isNew = existing is null;
        var customerNumber = isNew ? await db.NextCustomerNumberAsync(cancellationToken) : null;

        await OutboxScope.RunAsync(db, async () =>
        {
            Customer entity;
            if (isNew)
            {
                entity = new Customer
                {
                    Id = Guid.NewGuid(),
                    CrmAccountId = payload.AccountId,
                    CustomerNumber = customerNumber!,
                    LegalName = payload.LegalName,
                    TaxId = payload.TaxId,
                    CountryCode = payload.CountryCode,
                    CreatedAt = DateTimeOffset.UtcNow,
                    Origin = "Crm",
                };
                db.Customers.Add(entity);
            }
            else
            {
                entity = existing!;
                entity.LegalName = payload.LegalName;
                entity.TaxId = payload.TaxId;
                entity.CountryCode = payload.CountryCode;
                entity.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
            if (isNew)
                await publisher.Publish(CustomerMapper.ToCreatedEvent(entity));
        }, cancellationToken);
    }
}
