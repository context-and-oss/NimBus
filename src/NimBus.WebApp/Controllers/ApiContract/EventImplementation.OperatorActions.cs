using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.SDK;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using System.Net;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation
{
    public async Task<IActionResult> PostMessageAuditAsync(MessageAudit body, string eventId)
    {
        // Writing an operator comment/audit row is an action, not a read.
        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor))
            return new ForbidResult();

        try
        {
            var audit = Mapper.MessageAuditEntityFromMessageAudit(body);
            await messageStore.StoreMessageAudit(eventId, audit);
            return new OkResult();
        }
        catch (Exception e)
        {
            logger.LogWarning("Failed to PostMessageAudit: {ExceptionMessage}", e.Message);
            return new BadRequestResult();
        }
    }

    public async Task<IActionResult> PostResubmitEventIdsAsync(string eventId, string messageId)
    {
        logger.LogInformation("Resubmit message. EventId:{EventId}, MessageId:{MessageId}", eventId, messageId);

        var lookup = await operatorCommands.FindByMessageAsync(eventId, messageId);
        if (lookup.Target == null)
            return LookupFailure(lookup.Status, eventId, messageId);

        var result = await operatorCommands.ResubmitAsync(lookup.Target, WebAppCommand);
        return CommandResult(result);
    }

    public async Task<IActionResult> PostSkipEventIdsAsync(string eventId, string messageId)
    {
        logger.LogInformation("Skip message. EventId:{EventId}, MessageId:{MessageId}", eventId, messageId);

        var lookup = await operatorCommands.FindByMessageAsync(eventId, messageId);
        if (lookup.Target == null)
            return LookupFailure(lookup.Status, eventId, messageId);

        var result = await operatorCommands.SkipAsync(lookup.Target, WebAppCommand);
        return CommandResult(result);
    }

    public async Task<IActionResult> PostReportEventAsync(ReportEventRequest body, string endpointId, string eventId)
    {
        // `Reported` is modelled nullable so an omitted value binds as null
        // (MVC binds with System.Text.Json, which ignores the generated
        // Newtonsoft Required attribute) — an empty `{}` body must be a 400,
        // not a silent "clear the marker".
        if (body?.Reported is not bool reported)
            return new BadRequestObjectResult("The 'reported' field is required.");

        var result = await operatorCommands.SetReportedAsync(endpointId, eventId, reported, body.TicketId, WebAppCommand);
        return CommandResult(result);
    }

    public async Task<ActionResult<DeferredReprocessResult>> PostReprocessDeferredAsync(string endpointId, string sessionId)
    {
        if (!EndpointVerificationService.EndpointExists(platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
            return new ForbidResult();

        var result = await adminService.ReprocessDeferredAsync(endpointId, sessionId);
        return new OkObjectResult(result);
    }

    public async Task<IActionResult> PostComposeNewEventAsync(ResubmitWithChanges body)
    {
        logger.LogInformation("Compose new event. EventTypeId:{EventTypeId}", ForLog(body?.EventTypeId));

        var eventType = platform.EventTypes.FirstOrDefault(x => x.Id.Equals(body!.EventTypeId, StringComparison.OrdinalIgnoreCase));
        if (eventType == null)
        {
            logger.LogError("Could not find event type: {EventTypeId}", ForLog(body!.EventTypeId));
            return new BadRequestObjectResult("Could not find event type: " + body.EventTypeId);
        }

        var producingEndpoint = platform.GetProducers(eventType).FirstOrDefault();
        if (producingEndpoint == null)
        {
            logger.LogError("Could not find any producers for the given event: {EventTypeId}", eventType.Id);
            return new BadRequestObjectResult("Could not find any producers for the given event");
        }

        if (string.IsNullOrWhiteSpace(body!.EventContent))
        {
            logger.LogError("Event content is empty");
            return new BadRequestObjectResult("Event content is empty");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, producingEndpoint.Id))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.Compose, httpContextAccessor.HttpContext!,
                accessDenied: true, data: JsonConvert.SerializeObject(body),
                endpointId: producingEndpoint.Id, eventTypeId: body.EventTypeId);
            throw new UnauthorizedAccessException($"User is unauthorized to compose events for endpoint '{producingEndpoint.Id}'.");
        }

        var type = eventType.GetEventClassType();
        try
        {
            var @event = (Core.Events.IEvent)JsonConvert.DeserializeObject(body.EventContent, type)!;
            var validationResult = @event.TryValidate();
            if (!validationResult.IsValid)
            {
                string errorMessage = string.Join(", ", validationResult.ValidationResults.Select(x => x.ErrorMessage));
                return new BadRequestObjectResult($"Validation failed. Event does not fulfill the scheme '{type.Name}' Error: '{errorMessage}'");
            }

            // Publish directly to the producing endpoint's topic via the SDK PublisherClient,
            // so the message envelope (sessionId, correlationId, EventRequest type, EventContent)
            // matches what every other producer in the platform sends.
            var publisher = await PublisherClient.CreateAsync(serviceBusClient, producingEndpoint.Id);
            await publisher.Publish(@event);
            await auditLogService.LogAuditAsync(MessageAuditType.Compose, httpContextAccessor.HttpContext!,
                data: JsonConvert.SerializeObject(body),
                endpointId: producingEndpoint.Id, eventTypeId: body.EventTypeId);
            return new OkResult();
        }
        catch (JsonReaderException e)
        {
            logger.LogError("Could not parse the event content. Exception: {ExceptionMessage}", e.Message);
            return new BadRequestObjectResult($"Could not parse the value '{e.Path}'");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not compose event.");
            return new BadRequestObjectResult($"Could not compose event: {e.Message}");
        }
    }

    public async Task<IActionResult> PostResubmitWithChangesEventIdsAsync(ResubmitWithChanges body, string eventId, string messageId)
    {
        logger.LogInformation("Resubmit message with changes. EventId:{EventId}, MessageId:{MessageId}, Body:{Body}", eventId, messageId, JsonConvert.SerializeObject(body));

        var lookup = await operatorCommands.FindByMessageAsync(eventId, messageId);
        if (lookup.Target == null)
            return LookupFailure(lookup.Status, eventId, messageId);

        var target = lookup.Target;
        var endpoint = target.EndpointId;
        var auditData = JsonConvert.SerializeObject(body);

        string eventTypeId = body.EventTypeId;
        if (string.IsNullOrEmpty(body.EventTypeId))
        {
            eventTypeId = await operatorCommands.ResolveEventTypeIdAsync(target);
        }

        // Checked here as well as in the coordinator so a caller without the role is refused
        // before the payload checks below reveal anything about the event type.
        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpoint))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext!,
                accessDenied: true, data: auditData,
                eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
            throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");
        }

        // A non-PiiReader was served a payload whose [Sensitive] fields were masked.
        // Resubmitting that body verbatim would overwrite the real values with the
        // mask token, so reject it and make them re-enter the sensitive fields.
        // The type id is always resolved server-side here: trusting body.EventTypeId
        // would let a caller name a type with no [Sensitive] members to skip the check.
        if (!await authorizationService.CanReadPiiAsync())
        {
            var serverEventTypeId = string.IsNullOrEmpty(body.EventTypeId)
                ? eventTypeId
                : await operatorCommands.ResolveEventTypeIdAsync(target);

            // Fail closed: with no resolvable type we cannot prove the body is clean.
            if (string.IsNullOrEmpty(serverEventTypeId))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext!,
                    accessDenied: true, data: auditData,
                    eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                return new BadRequestObjectResult(
                    "Resubmit rejected: the event type could not be resolved server-side, so the payload cannot be checked for masked PII. Ask a site Owner for the PiiReader role on the Access Control page.");
            }

            if (masker.ContainsRedactPlaceholder(serverEventTypeId, body.EventContent))
            {
                await auditLogService.LogAuditAsync(MessageAuditType.ResubmitWithChanges, httpContextAccessor.HttpContext!,
                    accessDenied: true, data: auditData,
                    eventId: eventId, endpointId: endpoint, eventTypeId: eventTypeId);
                return new BadRequestObjectResult(
                    "Resubmit rejected: sensitive fields still contain the mask placeholder. Re-enter every masked value, or ask a site Owner for the PiiReader role to resubmit the payload unmodified.");
            }
        }

        // The body may have round-tripped the $piiMasked sidecar marker; strip it so
        // the marker never leaks into the actual event payload.
        var forwardedContent = masker.StripMaskedMarker(body.EventContent);

        var result = await operatorCommands.ResubmitWithChangesAsync(target, eventTypeId, forwardedContent, auditData, WebAppCommand);
        if (result.Status == OperatorCommandStatus.Forbidden)
            throw new UnauthorizedAccessException($"User is unauthorized to manage endpoint '{endpoint}'.");
        return CommandResult(result);
    }

    private static readonly OperatorCommandContext WebAppCommand = new(OperatorChannel.WebApp);

    // The composed event type id comes from the caller; strip line breaks so it cannot forge
    // log entries.
    private static string ForLog(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');

    private const string StaleMessageDetail =
        "This message changed since it was loaded: it was resubmitted, skipped or failed again. Refresh and try again.";

    private IActionResult LookupFailure(OperatorCommandStatus status, string eventId, string messageId)
    {
        if (status == OperatorCommandStatus.Stale)
        {
            logger.LogInformation("Operator command refused: message {MessageId} of event {EventId} is no longer current", messageId, eventId);
            return new ConflictObjectResult(StaleMessageDetail);
        }

        logger.LogWarning("Operator command refused: message not found. EventId: {EventId}, MessageId: {MessageId}", eventId, messageId);
        return new NotFoundObjectResult("Message not found");
    }

    private static IActionResult CommandResult(OperatorCommandResult result) => result.Status switch
    {
        OperatorCommandStatus.Accepted => new OkResult(),
        OperatorCommandStatus.NotFound => new NotFoundObjectResult(result.Detail ?? "Message not found"),
        OperatorCommandStatus.Stale => new ConflictObjectResult(StaleMessageDetail),
        OperatorCommandStatus.NotAllowed => new ConflictObjectResult(result.Detail),
        OperatorCommandStatus.Forbidden => new ForbidResult(),
        OperatorCommandStatus.Invalid => new BadRequestObjectResult(result.Detail),
        OperatorCommandStatus.AuditUnavailable => new ObjectResult(result.Detail) { StatusCode = StatusCodes.Status503ServiceUnavailable },
        _ => throw new InvalidOperationException($"Unhandled operator command status {result.Status}."),
    };

    public async Task<IActionResult> DeleteEventInvalidIdAsync(string endpointId, string eventId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
            return new ForbidResult();

        try
        {
            // Deleting the event its session is blocked on would strand the session (#288).
            var blocking = await messageStore.GetFailedEvent(endpointId, eventId, sessionId)
                ?? await messageStore.GetPendingEvent(endpointId, eventId, sessionId);
            if (blocking != null && BlockedEventRules.BlocksItsSession(blocking))
                return new ConflictObjectResult(EventBlocksSessionException.Describe(eventId));

            var result = await messageStore.RemoveMessage(eventId, sessionId, endpointId);
            return result
                ? new OkResult()
                : new NotFoundObjectResult($"Event '{eventId}' not found or could not be deleted");
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
}
