using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using System.Net;

using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation
{
    public async Task<ActionResult<Message>> GetEventIdsAsync(string eventId, string messageId)
    {
        // Cross-endpoint message lookup (no endpoint scope on the route) —
        // the read floor is a site role (spec 026 phase D).
        if (!await authorizationService.HasRoleAsync(AccessRole.Reader))
            return new ForbidResult();

        try
        {
            var messageEntity = await messageStore.GetMessage(eventId, messageId);
            if (messageEntity != null)
            {
                var message = Mapper.MessageFromMessageEntity(messageEntity);
                if (!await authorizationService.CanReadPiiAsync())
                    payloadRedaction.Redact(message);
                return message;
            }
            return new NotFoundObjectResult("Event Message not found");
        }
        catch (Exception e)
        {
            logger.LogWarning("Event Message not found. EventId: {EventId}, MessageId: {MessageId}, Ex: {Exception}", eventId, messageId, e.Message);
            return new NotFoundObjectResult("Event Message not found");
        }
    }

    public async Task<ActionResult<Event>> GetUnresolvedFailedEventIdAsync(string endpointId, string eventId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        try
        {
            var unresolvedEvent = await messageStore.GetFailedEvent(endpointId, eventId, sessionId);
            if (unresolvedEvent is null)
                return new NotFoundObjectResult("Unresolved failed not found");
            var result = Mapper.EventFromMessageStoreEvent(unresolvedEvent);
            if (!await authorizationService.CanReadPiiAsync())
                payloadRedaction.Redact(result);
            return result;
        }
        catch (Exception e)
        {
            logger.LogWarning("Unresolved failed not found. EndpointId: {EndpointId}, EventId: {EventId}, SessionId: {SessionId}, Ex: {Exception}", endpointId, eventId, sessionId, e.Message);
            return new NotFoundObjectResult("Unresolved failed not found");
        }
    }

    public async Task<ActionResult<IEnumerable<MessageAudit>>> GetMessageAuditsEventIdAsync(string eventId)
    {
        if (!await authorizationService.HasRoleAsync(AccessRole.Reader))
            return new ForbidResult();

        var audits = await messageStore.GetMessageAudits(eventId);
        if (audits != null)
        {
            return audits.Reverse().Select(Mapper.MessageAuditFromMessageAuditEntity).ToList();
        }

        return new NotFoundResult();
    }

    public async Task<ActionResult<Event>> GetEventIdAsync(string id, string endpoint)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpoint);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpoint))
            return new ForbidResult();

        try
        {
            var unresolvedEvent = await messageStore.GetEvent(endpoint, id);
            if (unresolvedEvent is null)
                return new NotFoundObjectResult("Event not found");
            var result = Mapper.EventFromMessageStoreEvent(unresolvedEvent);

            // The Payload shown on the detail page must be the inbound *event
            // request* — the original EventRequest, or the latest
            // ResubmissionRequest if the event was resubmitted — never a
            // ResolutionResponse (which carries the handler's result, e.g. an
            // imported record id) or a handoff control message. The endpoint
            // container stores the *last* message's content, which for
            // completed/settled events is the resolution response, so resolve
            // the request payload from the full message history instead.
            var requestJson = await GetLatestEventRequestPayload(id);
            if (!string.IsNullOrEmpty(requestJson))
            {
                result.MessageContent ??= new ManagementApi.MessageContent();
                result.MessageContent.EventContent ??= new ManagementApi.EventContent();
                result.MessageContent.EventContent.EventJson = requestJson;
            }

            if (!await authorizationService.CanReadPiiAsync())
                payloadRedaction.Redact(result);

            return result;
        }
        catch (Exception e)
        {
            logger.LogWarning("Event not found. EndpointId: {EndpointId}, EventId: {EventId}, Ex: {Exception}", endpoint, id, e.Message);
            return new NotFoundObjectResult("Event not found");
        }
    }

    public async Task<ActionResult<EventDetails>> GetEventDetailsIdAsync(string id, string endpoint)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpoint);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpoint))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.GetEventDetails, httpContextAccessor.HttpContext!,
                accessDenied: true, eventId: id, endpointId: endpoint);
            return new ForbidResult();
        }

        await auditLogService.LogAuditAsync(MessageAuditType.GetEventDetails, httpContextAccessor.HttpContext!,
            eventId: id, endpointId: endpoint);

        var eventDetails = new EventDetails();

        try
        {
            var failedMessage = await messageStore.GetFailedMessage(id, endpoint);
            if (failedMessage != null)
            {
                logger.LogInformation("Failed message found. EventId: {EventId}, MessageId: {MessageId}, Endpoint: {Endpoint}, MessageType: {MessageType}", failedMessage.EventId, failedMessage.MessageId, endpoint, failedMessage.MessageType);
                eventDetails.FailedMessage = Mapper.MessageFromMessageEntity(failedMessage);

                var downloadedMsg = await messageStore.GetMessage(eventDetails.FailedMessage.EventId,
                    eventDetails.FailedMessage.OriginatingMessageId);
                if (downloadedMsg != null)
                {
                    eventDetails.OriginatingMessage = Mapper.MessageFromMessageEntity(downloadedMsg);
                }

                return await RedactDetailsForNonPiiReadersAsync(eventDetails);
            }

            var deadletteredMessage = await messageStore.GetDeadletteredMessage(id, endpoint);


            if (deadletteredMessage != null)
            {
                logger.LogInformation("Message found in deadletter. EventId: {EventId}, MessageId: {MessageId}, Endpoint: {Endpoint}, MessageType: {MessageType}", deadletteredMessage.EventId, deadletteredMessage.MessageId, endpoint, deadletteredMessage.MessageType);
                if (deadletteredMessage.MessageType == Core.Messages.MessageType.ResolutionResponse)
                {
                    var completedMsg = await messageStore.GetMessage(deadletteredMessage.EventId, deadletteredMessage.OriginatingMessageId!);
                    if (completedMsg != null)
                    {
                        eventDetails.FailedMessage = Mapper.MessageFromMessageEntity(completedMsg);
                    }
                    return await RedactDetailsForNonPiiReadersAsync(eventDetails);
                }

                eventDetails.FailedMessage = Mapper.MessageFromMessageEntity(deadletteredMessage);

                eventDetails.FailedMessage.ErrorContent = Mapper.MessageErrorContentFromErroryContent(deadletteredMessage);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("GetEventDetailsIdAsync: {Exception}", e.Message);
        }

        return await RedactDetailsForNonPiiReadersAsync(eventDetails);
    }

    // Every GetEventDetailsIdAsync return path funnels through here so no
    // branch can leak a raw payload to a non-PiiReader (spec 026 phase D).
    private async Task<EventDetails> RedactDetailsForNonPiiReadersAsync(EventDetails details)
    {
        if (!await authorizationService.CanReadPiiAsync())
            payloadRedaction.Redact(details);
        return details;
    }

    public async Task<ActionResult<IEnumerable<EventLogEntry>>> GetEventDetailsLogsIdAsync(string id, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        var logs = new List<EventLogEntry>();
        try
        {
            logs = (await applicationInsightsService.GetLogs(id))
                .Where(l => l.To!.Equals(endpointId, StringComparison.OrdinalIgnoreCase) || l.From!.Equals(endpointId, StringComparison.OrdinalIgnoreCase))
                .Select(Mapper.EventLogEntryFromLogEntry)
                .ToList();
        }
        catch (Exception e)
        {
            logger.LogWarning("GetEventDetailsLogsIdAsync: {Exception}", e.Message);
        }

        if (!await authorizationService.CanReadPiiAsync())
            logs.ForEach(l => payloadRedaction.Redact(l));

        return logs;
    }

    public async Task<ActionResult<IEnumerable<Message>>> GetEventDetailsHistoryIdAsync(string id, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        var histories = new List<Message>();
        try
        {
            histories = (await messageStore.GetEventHistory(id))
                .Where(x => x.EndpointId.Equals(endpointId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.EnqueuedTimeUtc)
                .Select(Mapper.MessageFromMessageEntity)
                .ToList();
        }
        catch (Exception e)
        {
            logger.LogWarning("GetEventDetailsHistoryIdAsync: {Exception}", e.Message);
        }

        if (!await authorizationService.CanReadPiiAsync())
            histories.ForEach(m => payloadRedaction.Redact(m));

        return histories;
    }

    public async Task<ActionResult<BlockedEventsPage>> GetEventBlockedIdAsync(int skip, int take, string endpointId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        // Server-side bounds: skip is non-negative; take clamps to [1, 200] with a default of 50.
        // NSwag binds the route's optional query params with their schema defaults (skip=0, take=50);
        // the clamping below is belt-and-braces against external callers passing negatives or huge values.
        var safeSkip = skip < 0 ? 0 : skip;
        var safeTake = take <= 0 ? 50 : Math.Min(take, 200);

        try
        {
            var page = await messageStore.GetBlockedEventsOnSession(endpointId, sessionId, safeSkip, safeTake);

            return new BlockedEventsPage
            {
                Items = page.Items.Select(Mapper.BlockedEventFromBlockedMessageEvent).ToList(),
                Total = page.Total,
            };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
    }

    public async Task<ActionResult<IEnumerable<Event>>> GetEventPendingIdAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        try
        {
            var events = (await messageStore.GetPendingEventsOnSession(endpointId))!
                .Select(Mapper.EventFromMessageStoreEvent)
                .ToList();

            if (!await authorizationService.CanReadPiiAsync())
                payloadRedaction.Redact(events);

            return events;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
    }


    public async Task<ActionResult<Event>> GetEventUnsupportedEndpointIdEventIdAsync(string endpointId, string eventId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        try
        {
            var result = await messageStore.GetUnsupportedEvent(endpointId, eventId, sessionId);
            if (result is null)
                return new NotFoundObjectResult("Event not found");
            var mapped = Mapper.EventFromMessageStoreEvent(result);
            if (!await authorizationService.CanReadPiiAsync())
                payloadRedaction.Redact(mapped);
            return mapped;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
    }

    public async Task<ActionResult<Event>> GetEventDeadletterEndpointIdEventIdAsync(string endpointId, string eventId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        try
        {
            var result = await messageStore.GetDeadletteredEvent(endpointId, eventId, sessionId);
            if (result is null)
                return new NotFoundObjectResult("Event not found");
            var mapped = Mapper.EventFromMessageStoreEvent(result);
            if (!await authorizationService.CanReadPiiAsync())
                payloadRedaction.Redact(mapped);
            return mapped;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult($"Endpoint container '{endpointId}' not found in database");
        }
    }

    // The detail-page Payload should reflect the event request that was
    // processed, not whatever the last message on the event happened to carry
    // (a ResolutionResponse, handoff control message, etc.). Returns the
    // EventJson of the most recent EventRequest / ResubmissionRequest in the
    // event's history, or null when none carries event content.
    private async Task<string?> GetLatestEventRequestPayload(string eventId)
    {
        try
        {
            // Server-side TOP 1 (single-partition on the messages container)
            // instead of pulling the whole message history and filtering in
            // memory on every event-detail load.
            var request = await messageStore.GetLatestEventRequestMessage(eventId);
            return request?.MessageContent?.EventContent?.EventJson;
        }
        catch (Exception e)
        {
            logger.LogWarning("GetLatestEventRequestPayload failed for EventId {EventId}: {Exception}", eventId, e.Message);
            return null;
        }
    }

    // The latest request message that still carries the event payload — the
    // "latest request message sent". Lets resubmit-with-changes resolve the
    // event type from the original EventRequest (or a later Resubmission/
    // Retry/Continuation/ProcessDeferredRequest) rather than the terminal
    // ErrorResponse, which for a failed hand-off carries neither payload nor
    // event type. Internal for unit tests (InternalsVisibleTo).
    internal static MessageEntity? LatestRequestMessageWithPayload(IEnumerable<MessageEntity> history) =>
        OperatorCommandCoordinator.LatestRequestMessageWithPayload(history);
}
