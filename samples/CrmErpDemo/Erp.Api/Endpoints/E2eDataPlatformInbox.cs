using Azure.Messaging.ServiceBus;
using CrmErpDemo.Contracts.Events;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace Erp.Api.Endpoints;

/// <summary>Delivers a repeatable synthetic customer to the DataPlatform subscriber.</summary>
internal static class E2eDataPlatformInbox
{
    public static async Task<IResult> SendAsync(Guid session, ServiceBusClient bus, CancellationToken cancellationToken)
    {
        if (session == Guid.Empty) return Results.BadRequest();
        var id = session.ToString();
        var customer = new ErpCustomerCreated
        {
            AccountId = session, ErpCustomerId = session, CustomerNumber = $"E2E-{id}",
            LegalName = "Inbox demo", CountryCode = "DK", Origin = CustomerOrigin.Erp,
        };
        var message = new Message
        {
            From = "ErpEndpoint", OriginatingFrom = "ErpEndpoint", To = "DataPlatformEndpoint",
            EventId = id, MessageId = id, SessionId = id, CorrelationId = id, OriginatingMessageId = id,
            MessageType = MessageType.EventRequest, EventTypeId = nameof(ErpCustomerCreated),
            MessageContent = new MessageContent { EventContent = new EventContent
            {
                EventTypeId = nameof(ErpCustomerCreated), EventJson = JsonConvert.SerializeObject(customer),
            } },
        };
        await using var sender = bus.CreateSender(message.To);
        await sender.SendMessageAsync(MessageHelper.ToServiceBusMessage(message), cancellationToken);
        return Results.NoContent();
    }
}
