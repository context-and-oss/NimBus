using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Erp.Adapter.Functions.HandoffMode;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmAccountCreatedHandler(
    IErpApiClient erp,
    IServiceModeClient modeClient,
    IHandoffModeClient handoffMode,
    IHandoffJobRegistration jobRegistration,
    ILogger<CrmAccountCreatedHandler> logger)
    : IEventHandler<CrmAccountCreated>
{
    public async Task Handle(CrmAccountCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var mode = await handoffMode.GetAsync(cancellationToken);
        if (mode.Enabled)
        {
            var jobId = $"DMF-{Guid.NewGuid():N}".Substring(0, 16);
            await jobRegistration.RegisterAsync(new HandoffJob
            {
                EventId = context.EventId,
                SessionId = message.AccountId.ToString(),
                MessageId = context.MessageId,
                // Not MessageId: on a retry/resubmission replay that would be the
                // replay request's id and corrupt the settlement's audit lineage.
                OriginatingMessageId = context.OriginatingMessageId ?? context.MessageId,
                EventTypeId = context.EventType,
                CorrelationId = context.CorrelationId,
                ExternalJobId = jobId,
                DueAt = DateTime.UtcNow.AddSeconds(mode.DurationSeconds),
                PayloadJson = JsonConvert.SerializeObject(message),
            }, cancellationToken);

            logger.LogInformation(
                "Handing off CRM account {AccountId} to ERP DMF job {JobId} (due in {Seconds}s).",
                message.AccountId,
                jobId,
                mode.DurationSeconds);

            context.MarkPendingHandoff(
                reason: "Awaiting ERP DMF import job (demo)",
                externalJobId: jobId,
                expectedBy: TimeSpan.FromSeconds(mode.DurationSeconds));
            return;
        }

        await ErrorModeGuard.ThrowIfEnabledAsync(modeClient, context, logger, cancellationToken, message);
        logger.LogInformation("Creating ERP customer from CRM account {AccountId} ({LegalName})", message.AccountId, message.LegalName);

        await erp.UpsertCustomerAsync(
            message.AccountId,
            new CustomerUpsertPayload(null, message.LegalName, message.TaxId, message.CountryCode),
            cancellationToken);
    }
}
