using Microsoft.Extensions.Logging;
using CloudEventsInterop.Contracts.Events;
using NimBus.SDK.EventHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CloudEventsInterop.Subscriber.Handlers;

/// <summary>
/// Step 3 of the demo: a NimBus <see cref="IEventHandler{TEvent}"/> consumes the
/// InvoiceCreated event NimBus routed to this endpoint. <c>message</c> is already the
/// deserialized domain event regardless of whether it arrived natively or as a CloudEvent;
/// <see cref="IEventHandlerContext.GetCloudEvent"/> exposes the CloudEvents view when present.
/// </summary>
public partial class InvoiceCreatedHandler : IEventHandler<InvoiceCreated>
{
    private readonly ILogger<InvoiceCreatedHandler> _logger;

    public InvoiceCreatedHandler(ILogger<InvoiceCreatedHandler> logger)
    {
        _logger = logger;
    }

    public Task Handle(InvoiceCreated message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        var cloudEvent = context.GetCloudEvent();
        if (cloudEvent is null)
        {
            LogNative(_logger, message.InvoiceId, message.Amount, message.CurrencyCode);
        }
        else
        {
            LogCloudEvent(_logger, message.InvoiceId, cloudEvent.Id, cloudEvent.Source, cloudEvent.Type, cloudEvent.SpecVersion);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Processed InvoiceCreated {InvoiceId} as a native NimBus message: {Amount} {Currency} (CloudEvents not enabled on the producer).")]
    private static partial void LogNative(ILogger logger, Guid invoiceId, decimal amount, string currency);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Processed InvoiceCreated {InvoiceId} via CloudEvents: id={CloudEventId}, source={Source}, type={Type}, specversion={SpecVersion}.")]
    private static partial void LogCloudEvent(ILogger logger, Guid invoiceId, string cloudEventId, string? source, string type, string? specVersion);
}
