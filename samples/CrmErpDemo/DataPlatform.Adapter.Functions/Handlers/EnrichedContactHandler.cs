using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;

namespace DataPlatform.Adapter.Functions.Handlers;

/// <summary>
/// Enriched-contact payload published by the AI agent as a dynamically-typed event
/// "crm.contact.enriched.v1". No compiled IEvent class exists for this event — it is
/// routed by EventTypeId string only (spec 022).
/// </summary>
internal sealed record EnrichedContact(
    string ContactId,
    string Industry,
    int LeadScore,
    string? Rationale);

/// <summary>
/// Consumes the AI-agent event "crm.contact.enriched.v1" on DataPlatformEndpoint.
/// A real downstream lake / data-warehouse write would happen here; for the demo, a
/// structured log line provides the observable "DataPlatform received enriched contact"
/// signal without coupling the sample to extra infrastructure.
/// </summary>
public sealed class EnrichedContactHandler(ILogger<EnrichedContactHandler> logger) : IEventJsonHandler
{
    public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        var json = context.MessageContent.EventContent!.EventJson!;

        var enriched = JsonConvert.DeserializeObject<EnrichedContact>(json)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize {context.MessageContent.EventContent.EventTypeId} payload.");

        logger.LogInformation(
            "DataPlatform received enriched contact {ContactId}: industry={Industry} leadScore={LeadScore} rationale={Rationale}",
            enriched.ContactId,
            enriched.Industry,
            enriched.LeadScore,
            enriched.Rationale);

        return Task.CompletedTask;
    }
}
