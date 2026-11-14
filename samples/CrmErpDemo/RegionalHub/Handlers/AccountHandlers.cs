using CrmErpDemo.Contracts.Events;
using NimBus.SDK.EventHandlers;

namespace RegionalHub.Handlers;

// A real hub would update its regional system here. The demo only logs, because the point of
// the showcase is which hub the message reached: look at the hub's endpoint in nimbus-ops.

public sealed class AccountCreatedHandler(CrmErpDemo.Contracts.Endpoints.RegionalHub hub, ILogger<AccountCreatedHandler> logger)
    : IEventHandler<CrmAccountCreated>
{
    public Task Handle(CrmAccountCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "{Hub} received new account {AccountId} '{LegalName}' in {CountryCode}",
            hub.Name, message.AccountId, message.LegalName, message.CountryCode);
        return Task.CompletedTask;
    }
}

public sealed class AccountUpdatedHandler(CrmErpDemo.Contracts.Endpoints.RegionalHub hub, ILogger<AccountUpdatedHandler> logger)
    : IEventHandler<CrmAccountUpdated>
{
    public Task Handle(CrmAccountUpdated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "{Hub} received updated account {AccountId} '{LegalName}' in {CountryCode}",
            hub.Name, message.AccountId, message.LegalName, message.CountryCode);
        return Task.CompletedTask;
    }
}
