using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NimBus.Core;
using NimBus.Core.Messages.PII;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.SDK;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AgentApiKeys;
// Aliased to disambiguate from the NSwag-generated NimBus.WebApp.ManagementApi.* types.
using CoreMessage = NimBus.Core.Messages.Message;
using CoreMessageType = NimBus.Core.Messages.MessageType;
using CoreMessageContent = NimBus.Core.Messages.MessageContent;
using CoreEventContent = NimBus.Core.Messages.EventContent;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// The agent API (<c>/api/v1/agent/*</c>). An agent that authenticated with an agent API key
/// (Spec 039) is identified by its key and is not role-checked. A signed-in person is held to
/// the roles that apply everywhere else: Reader on the Agent Zone endpoint to read, Contributor
/// on it to change anything, PiiReader to see an unmasked payload. A person is always recorded
/// under their own name; <c>X-Agent-Id</c> never names a person.
/// </summary>
public partial class AgentImplementation : IAgentApiController
{
    // The agent identity the local development bypass may claim (see ResolveCaller).
    private const string AgentIdHeader = AgentIdBindingFilter.HeaderName;

    private readonly IEventSchemaStore _schemas;
    private readonly IPlatform _platform;
    private readonly IAgentEventPublisher _publisher;
    private readonly IMessageTrackingStore _store;
    private readonly IHandoffClientFactory _handoffClients;
    private readonly IHandoffSettlementService _settlement;
    private readonly IAgentSubscriptionRegistry _subscriptions;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IEventJsonMasker _masker;
    private readonly IAuditLogService _audit;
    private readonly ILogger<AgentImplementation> _logger;

    /// <summary>Creates the agent API implementation.</summary>
    public AgentImplementation(
        IEventSchemaStore schemas,
        IPlatform platform,
        IAgentEventPublisher publisher,
        IMessageTrackingStore store,
        IHandoffClientFactory handoffClients,
        IHandoffSettlementService settlement,
        IAgentSubscriptionRegistry subscriptions,
        IConfiguration config,
        IHttpContextAccessor? httpContextAccessor,
        IEndpointAuthorizationService authorization,
        IEventJsonMasker masker,
        IAuditLogService audit,
        ILogger<AgentImplementation> logger)
    {
        _schemas = schemas;
        _platform = platform;
        _publisher = publisher;
        _store = store;
        _handoffClients = handoffClients;
        _settlement = settlement;
        _subscriptions = subscriptions;
        _config = config;
        _httpContextAccessor = httpContextAccessor;
        _authorization = authorization;
        _masker = masker;
        _audit = audit;
        _logger = logger;
    }

    // ── GET /api/agent/catalog ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync()
    {
        if (await AuthorizeAsync(ResolveCaller(), AccessRole.Reader) is { } denied)
            return denied;

        var schemas = await _schemas.GetSchemas();

        var catalog = new AgentCatalog
        {
            Endpoints = _platform.Endpoints.Select(e => e.Id).ToList(),
            EventTypes = schemas.Select(s => new EventTypeInfo
            {
                EventTypeId = s.EventTypeId,
                Name = s.Name,
                JsonSchema = s.JsonSchema,
                Description = s.Description,
            }).ToList(),
        };

        return new OkObjectResult(catalog);
    }

    // ── POST /api/agent/event-types ───────────────────────────────────────

    /// <inheritdoc/>
    public async Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body)
    {
        // Schemas are immutable and an EventTypeId is global, so defining one is a change.
        var caller = ResolveCaller();
        if (await AuthorizeAsync(caller, AccessRole.Contributor) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(body.EventTypeId))
            return new BadRequestObjectResult("eventTypeId is required.");

        if (string.IsNullOrWhiteSpace(body.JsonSchema))
            return new BadRequestObjectResult("jsonSchema is required.");

        // Reject an unparseable JSON Schema at define time. Schemas are immutable (409 on
        // redefine), so a bad one stored here would break every subsequent publish and
        // mapping that references it, with no way to fix it — fail fast instead.
        try
        {
            await NJsonSchema.JsonSchema.FromJsonAsync(body.JsonSchema);
        }
        // NJsonSchema throws assorted undocumented exception types on bad schema JSON
        // (JsonReaderException, InvalidOperationException, etc.) — catch-all is intentional.
        catch (Exception ex)
        {
            LogRejectedEventTypeJsonSchemaIsNot(_logger, ex, body.EventTypeId);
            return new BadRequestObjectResult($"jsonSchema is not a valid JSON Schema: {ex.Message}");
        }

        // A declared sessionKeyPath must be a plausible JSONPath (starts with '$').
        if (!string.IsNullOrWhiteSpace(body.SessionKeyPath) && !body.SessionKeyPath.TrimStart().StartsWith('$'))
            return new BadRequestObjectResult("sessionKeyPath must be a JSONPath expression starting with '$'.");

        var agentId = caller.Name;
        var schema = new EventSchema
        {
            EventTypeId = body.EventTypeId,
            Name = body.Name ?? body.EventTypeId,
            JsonSchema = body.JsonSchema,
            Description = body.Description,
            SessionKeyPath = body.SessionKeyPath,
            Version = 1,
            AgentId = agentId,
            CreatedBy = agentId,
            CreatedUtc = DateTime.UtcNow,
        };

        try
        {
            var stored = await _schemas.DefineEventType(schema);

            return new OkObjectResult(new EventTypeInfo
            {
                EventTypeId = stored.EventTypeId,
                Name = stored.Name,
                JsonSchema = stored.JsonSchema,
                Description = stored.Description,
            });
        }
        catch (SchemaConflictException)
        {
            LogSchemaConflictForEventType(_logger, body.EventTypeId);
            return new ConflictResult();
        }
    }

    // ── POST /api/agent/subscribe ─────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body)
    {
        // A subscription only narrows the caller's own receive, so it needs what receive needs.
        var caller = ResolveCaller();
        if (await AuthorizeAsync(caller, AccessRole.Reader) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(body?.EventTypeId))
            return new BadRequestObjectResult("eventTypeId is required.");

        _subscriptions.Subscribe(caller.SubscriptionKey, body.EventTypeId);
        LogAgentSubscribed(_logger, caller.Name, body.EventTypeId);
        return new OkResult();
    }

    // ── GET /api/agent/receive ────────────────────────────────────────────

    // NOTE: receive is a *non-claiming* read. A parked event stays Pending+Handoff
    // until it is settled, so two concurrent receives (two agents on the same event
    // type, or one agent polling twice before it settles) can be handed the same
    // event — delivery is at-least-once with possible duplicate processing. The
    // losing /settle then 400s on the Pending+Handoff status guard. This is
    // acceptable for the v1 demo-grade flow; a claim/lease is flagged for the spec's
    // deferred-recovery section.
    //
    // After a settle, the row still reads Pending+Handoff until the Resolver projects
    // the settlement. Receive skips a row whose settlement this instance has in flight
    // (HandoffSettlementTracker) and keeps polling until the projection moves it out of
    // the query; otherwise an agent that loops straight back to receive gets the event
    // it just settled. The record is per process, so across scaled-out instances that
    // window stays at-least-once.
    /// <inheritdoc/>
    public async Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds)
    {
        var caller = ResolveCaller();
        if (await AuthorizeAsync(caller, AccessRole.Reader) is { } denied)
            return denied;

        // A person sees the payload masked unless they hold PiiReader, as on every other
        // payload-returning endpoint. An agent key reads it unmasked (Spec 039).
        var maskPayload = !caller.IsAgent && !await _authorization.CanReadPiiAsync();

        var zoneId = AgentZone.ResolveEndpointId(_config);
        // Clamp the long-poll window so a client can't pin a request thread (and burn
        // store RUs) for hours by passing a huge waitSeconds.
        var wait = Math.Clamp(waitSeconds ?? 20, 0, 60);
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        // Stop polling as soon as the client disconnects.
        var ct = _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

        // Build the type filter once: explicit query param wins; otherwise fall back
        // to the agent's registered subscriptions; otherwise match anything (null).
        var subscribed = _subscriptions.GetSubscriptions(caller.SubscriptionKey);
        IReadOnlyCollection<string>? eventTypeIds;
        if (!string.IsNullOrWhiteSpace(eventTypeId))
            eventTypeIds = new[] { eventTypeId };
        else if (subscribed.Count > 0)
            eventTypeIds = subscribed;
        else
            eventTypeIds = null;

        do
        {
            // Bounded, server-side-filtered query: a single Pending+Handoff row for
            // the zone (optionally restricted by event type), instead of draining
            // every pending event just to long-poll. Genuine store faults
            // (throttling, connectivity) are left to propagate as 500.
            var parked = await _store.GetNextPendingHandoffEvent(zoneId, eventTypeIds);

            if (parked != null && !_settlement.IsSettlementInFlight(zoneId, parked))
            {
                var payload = parked.MessageContent?.EventContent?.EventJson;
                if (maskPayload && !string.IsNullOrEmpty(payload))
                    payload = _masker.Mask(parked.EventTypeId ?? string.Empty, payload);

                return new OkObjectResult(new AgentReceivedMessage
                {
                    EventTypeId = parked.EventTypeId,
                    Payload = payload,
                    Coordinates = new HandoffCoordinates
                    {
                        EventId = parked.EventId,
                        SessionId = parked.SessionId,
                        MessageId = parked.LastMessageId,
                        EventTypeId = parked.EventTypeId,
                        CorrelationId = parked.CorrelationId,
                        OriginatingMessageId = parked.OriginatingMessageId,
                    },
                });
            }

            if (wait == 0)
                break;

            try
            {
                await Task.Delay(500, ct);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected mid-poll — stop and report nothing parked.
                break;
            }
        }
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested);

        return new NoContentResult();
    }

    // ── POST /api/agent/publish ───────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IActionResult> PostAgentPublishAsync(AgentPublishRequest body)
    {
        // Publishing puts an EventRequest on the Agent Zone topic.
        if (await AuthorizeAsync(ResolveCaller(), AccessRole.Contributor) is { } denied)
            return denied;

        if (string.IsNullOrWhiteSpace(body.EventTypeId))
            return new BadRequestObjectResult("eventTypeId is required.");
        if (string.IsNullOrWhiteSpace(body.Payload))
            return new BadRequestObjectResult("payload is required.");

        var schema = await _schemas.GetSchema(body.EventTypeId);
        if (schema == null)
            return new NotFoundObjectResult($"Unknown eventTypeId '{body.EventTypeId}'.");

        NJsonSchema.JsonSchema jsonSchema;
        try
        {
            jsonSchema = await NJsonSchema.JsonSchema.FromJsonAsync(schema.JsonSchema);
        }
        // NJsonSchema throws assorted undocumented exception types on bad schema JSON
        // (JsonReaderException, InvalidOperationException, etc.) — catch-all is intentional.
        catch (Exception ex)
        {
            LogRegisteredSchemaForIsNotValid(_logger, ex, body.EventTypeId);
            return new BadRequestObjectResult($"Registered schema for '{body.EventTypeId}' is not valid JSON Schema: {ex.Message}");
        }

        ICollection<NJsonSchema.Validation.ValidationError> errors;
        try
        {
            errors = jsonSchema.Validate(body.Payload);
        }
        // NJsonSchema throws assorted undocumented exception types on unparseable payload
        // JSON (JsonReaderException, etc.) — catch-all is intentional.
        catch (Exception ex)
        {
            return new BadRequestObjectResult($"Payload is not valid JSON: {ex.Message}");
        }

        if (errors.Count > 0)
            return new BadRequestObjectResult(errors.Select(e => $"{e.Path}: {e.Kind}").ToList());

        var message = new CoreMessage
        {
            To = body.EventTypeId,
            EventTypeId = body.EventTypeId,
            SessionId = ResolveSessionId(body, schema),
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            RetryCount = 0,
            MessageType = CoreMessageType.EventRequest,
            MessageContent = new CoreMessageContent
            {
                EventContent = new CoreEventContent
                {
                    EventTypeId = body.EventTypeId,
                    EventJson = body.Payload,
                },
            },
        };

        await _publisher.PublishAsync(message);
        return new OkResult();
    }

    // ── POST /api/agent/settle ────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body)
    {
        var zoneId = AgentZone.ResolveEndpointId(_config);
        var httpContext = _httpContextAccessor?.HttpContext;
        var coords = body?.Coordinates;

        // A person settles under the operator path's rule: Contributor on the endpoint, with a
        // refusal audited as access denied. Authorization runs before any request validation so
        // that nothing in the body decides whether the role check (and its audit) happens.
        var caller = ResolveCaller();
        if (await AuthorizeAsync(caller, AccessRole.Contributor) is { } denied)
        {
            var deniedType = body?.Outcome == AgentSettleRequestOutcome.Fail
                ? MessageAuditType.FailHandoff
                : MessageAuditType.CompleteHandoff;
            await _audit.LogAuditAsync(deniedType, httpContext!, accessDenied: true,
                eventId: string.IsNullOrWhiteSpace(coords?.EventId) ? null : coords.EventId,
                endpointId: zoneId);
            return denied;
        }

        if (coords == null || string.IsNullOrWhiteSpace(coords.EventId))
            return new BadRequestObjectResult("coordinates.eventId is required.");
        if (string.IsNullOrWhiteSpace(coords.MessageId))
            return new BadRequestObjectResult("coordinates.messageId is required.");
        if (body!.Outcome is not AgentSettleRequestOutcome.Complete
            and not AgentSettleRequestOutcome.Fail)
        {
            return new BadRequestObjectResult("outcome must be 'complete' or 'fail'.");
        }

        // The auditor is the key's agent, or the signed-in person by name, never a
        // caller-chosen X-Agent-Id. Recorded on the CompleteHandoff/FailHandoff audit row.
        var auditor = caller.Name;

        if (body.Outcome == AgentSettleRequestOutcome.Complete)
        {
            // The load → PendingHandoff guard → settle → audit core is shared with the
            // operator path via IHandoffSettlementService, so an agent-settled handoff
            // always leaves the same audit trail (ADR-002).
            return await _settlement.SettleAsync(
                zoneId, coords.EventId, coords.MessageId,
                MessageAuditType.CompleteHandoff, body.Result, auditor, httpContext,
                (pendingEntry, _) =>
                    _handoffClients.ForEndpoint(zoneId).CompleteAsync(pendingEntry.ToSettlement(), body.Result));
        }

        // Fail outcome. Validate the reason up front (400) — mirrors the operator path's
        // PostHandoffFailAsync, which rejects a missing reason before loading the event.
        if (string.IsNullOrWhiteSpace(body.ErrorText))
            return new BadRequestObjectResult("errorText is required when outcome is 'fail'.");

        return await _settlement.SettleAsync(
            zoneId, coords.EventId, coords.MessageId,
            MessageAuditType.FailHandoff, body.ErrorText, auditor, httpContext,
            (pendingEntry, _) =>
                _handoffClients.ForEndpoint(zoneId).FailAsync(pendingEntry.ToSettlement(), body.ErrorText, body.ErrorType));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // Resolves the session id that drives ADR-001 ordered processing. Precedence:
    //   1. An explicit sessionId on the request always wins.
    //   2. Else, if the schema declares a sessionKeyPath, derive the key from the
    //      (already schema-valid) payload via JSONPath so events sharing a business
    //      key land on the same session and preserve FIFO ordering.
    //   3. Else — no path, or the path resolves to no scalar value — fall back to a
    //      fresh GUID. A missing key degrades to unordered rather than failing publish.
    private string ResolveSessionId(AgentPublishRequest body, EventSchema schema)
    {
        if (!string.IsNullOrWhiteSpace(body.SessionId))
            return body.SessionId;

        if (!string.IsNullOrWhiteSpace(schema.SessionKeyPath))
        {
            var derived = TryDeriveSessionKey(body.Payload, schema.SessionKeyPath, body.EventTypeId);
            if (!string.IsNullOrEmpty(derived))
                return derived;
        }

        return Guid.NewGuid().ToString();
    }

    // Evaluates the schema's JSONPath against the payload, returning the scalar value at
    // the path or null when there is none. A missing/null/object/array token or a
    // malformed path yields null so the caller can fall back to a random session — a bad
    // path must never surface as a 500.
    private string? TryDeriveSessionKey(string payload, string sessionKeyPath, string eventTypeId)
    {
        try
        {
            var token = JToken.Parse(payload).SelectToken(sessionKeyPath);
            if (token == null
                || token.Type == JTokenType.Null
                || token.Type == JTokenType.Object
                || token.Type == JTokenType.Array)
            {
                return null;
            }

            var value = token.ToString();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        // SelectToken throws on a malformed JSONPath expression; a bad stored path must
        // degrade to the GUID fallback, not 500. Catch-all matches this file's style.
        catch (Exception ex)
        {
            LogCouldNotDeriveSessionKey(_logger, ex, sessionKeyPath, eventTypeId);
            return null;
        }
    }

    // Who is calling, and whether they are an agent.
    //   - An agent API key (Spec 039) is the key's agent. A mismatched X-Agent-Id never
    //     reaches here (AgentIdBindingFilter returns 403). Only the key scheme's own identity
    //     counts, so a sign-in token that happens to carry the claim is not an agent.
    //   - The local development bypass (Development only, where every caller is already an
    //     Owner) keeps X-Agent-Id, so an agent run locally without a key still acts as itself.
    //   - Anyone else is a person, recorded under their own resolved name. X-Agent-Id is
    //     ignored, so a person cannot attribute their actions to an agent.
    private AgentCaller ResolveCaller()
    {
        var context = _httpContextAccessor?.HttpContext;
        var user = context?.User;

        var keyAgent = AgentApiKeyDefaults.FindAgentId(user);
        if (keyAgent is not null)
            return new AgentCaller(keyAgent, IsAgent: true);

        if (FindIdentity(user, LocalDevAuthHandler.SchemeName) is not null)
        {
            var header = context!.Request.Headers[AgentIdHeader].ToString().Trim();
            if (header.Length > 0)
                return new AgentCaller(header, IsAgent: true);
        }

        return new AgentCaller(AuditLogService.ResolveAuditorName(context), IsAgent: false);
    }

    private static System.Security.Claims.ClaimsIdentity? FindIdentity(System.Security.Claims.ClaimsPrincipal? user, string scheme)
        => user?.Identities.FirstOrDefault(i => i.IsAuthenticated && string.Equals(i.AuthenticationType, scheme, StringComparison.Ordinal));

    // Null when the caller may proceed. An agent always may (keys carry no roles; Spec 039); a
    // person needs the role on the Agent Zone endpoint, where a site-wide grant also counts.
    private async Task<ActionResult?> AuthorizeAsync(AgentCaller caller, AccessRole required)
    {
        if (caller.IsAgent)
            return null;

        var zoneId = AgentZone.ResolveEndpointId(_config);
        if (await _authorization.HasRoleAsync(required, zoneId))
            return null;

        LogAgentApiAccessDenied(_logger, required, zoneId);
        return new ObjectResult(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = $"This requires the {required} role on endpoint '{zoneId}'.",
        })
        { StatusCode = StatusCodes.Status403Forbidden };
    }

    // Name is for display and audit. SubscriptionKey keeps people and agents apart in the
    // subscription registry: an agent keeps its plain id (unchanged), a person is prefixed.
    // A key's agent id cannot contain ':' (AgentApiKeyService), so the two never collide.
    private readonly record struct AgentCaller(string Name, bool IsAgent)
    {
        public string SubscriptionKey => IsAgent ? Name : PersonSubscriptionPrefix + Name;
    }

    /// <summary>Prefixes a signed-in person's key in <see cref="IAgentSubscriptionRegistry"/>.</summary>
    internal const string PersonSubscriptionPrefix = "person:";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent API refused a signed-in caller without the {Required} role on {EndpointId}")]
    private static partial void LogAgentApiAccessDenied(ILogger logger, AccessRole required, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected event type {EventTypeId}: jsonSchema is not valid JSON Schema")]
    private static partial void LogRejectedEventTypeJsonSchemaIsNot(ILogger logger, Exception exception, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Schema conflict for event type {EventTypeId}")]
    private static partial void LogSchemaConflictForEventType(ILogger logger, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent {AgentId} subscribed to {EventTypeId}")]
    private static partial void LogAgentSubscribed(ILogger logger, string agentId, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Registered schema for {EventTypeId} is not valid JSON Schema")]
    private static partial void LogRegisteredSchemaForIsNotValid(ILogger logger, Exception exception, string eventTypeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not derive session key via '{Path}' for {EventTypeId}; using a random session")]
    private static partial void LogCouldNotDeriveSessionKey(ILogger logger, Exception exception, string path, string eventTypeId);
}
