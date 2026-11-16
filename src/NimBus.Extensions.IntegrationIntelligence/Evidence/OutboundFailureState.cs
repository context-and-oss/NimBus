using System.Text.Json;

namespace NimBus.Extensions.IntegrationIntelligence.Evidence;

/// <summary>Projection of the evidence used to bound its size (<c>MaximumStateCharacters</c>): operational identifiers and timestamps are left out.</summary>
public static class OutboundFailureState
{
    public static object Create(FailureClassificationInput input) => new
    {
        failure = new
        {
            eventType = input.EventTypeId,
            endpoint = input.EndpointId,
            status = input.ResolutionStatus,
            retryCount = input.RetryCount,
            retryLimit = input.RetryLimit,
            exception = new { type = input.Exception.Type, message = input.Exception.Message, source = input.Exception.Source },
            deadLetterReason = input.DeadLetterReason,
        },
        recentHistory = input.RecentHistory.Select(h => new { attempt = h.Attempt, outcome = h.MessageType, errorType = h.ErrorType, errorMessage = h.ErrorMessage }),
        eventPayload = input.EventPayloadJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(input.EventPayloadJson),
    };
}
