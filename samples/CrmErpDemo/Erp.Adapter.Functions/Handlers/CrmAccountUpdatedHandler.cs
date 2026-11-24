using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

public sealed class CrmAccountUpdatedHandler(
    IErpApiClient erp,
    IServiceModeClient modeClient,
    ILogger<CrmAccountUpdatedHandler> logger)
    : IEventHandler<CrmAccountUpdated>
{
    public async Task Handle(CrmAccountUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        await ErrorModeGuard.ThrowIfEnabledAsync(modeClient, context, logger, cancellationToken, message);
        logger.LogInformation("Updating ERP customer from CRM account {AccountId}", message.AccountId);

        await erp.UpsertCustomerAsync(
            message.AccountId,
            new CustomerUpsertPayload(message.ErpCustomerId, message.LegalName, message.TaxId, message.CountryCode),
            cancellationToken);
    }
}
