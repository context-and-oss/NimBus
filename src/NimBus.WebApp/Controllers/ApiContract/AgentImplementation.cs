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
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.SDK;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
// Aliased to disambiguate from the NSwag-generated NimBus.WebApp.ManagementApi.* types.
using CoreMessage = NimBus.Core.Messages.Message;
using CoreMessageType = NimBus.Core.Messages.MessageType;
using CoreMessageContent = NimBus.Core.Messages.MessageContent;
using CoreEventContent = NimBus.Core.Messages.EventContent;

namespace NimBus.WebApp.Controllers.ApiContract;

public class AgentImplementation : IAgentApiController
{
    // Header carrying a demo-grade agent identity. Full API-key auth is deferred;
    // an X-Agent-Id header is the stand-in for v1 (spec 022).
    private const string AgentIdHeader = "X-Agent-Id";
    private const string DefaultAgentId = "demo-agent";

    // The pending sub-status discriminator the Agent Zone subscriber parks events under.

    private readonly IEventSchemaStore _schemas;
    private readonly IPlatform _platform;
    private readonly IAgentEventPublisher _publisher;
    private readonly IMessageTrackingStore _store;
    private readonly IHandoffClientFactory _handoffClients;
    private readonly IHandoffSettlementService _settlement;
    private readonly IAgentSubscriptionRegistry _subscriptions;
    private readonly IConfiguration _config;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AgentImplementation> _logger;

    public AgentImplementation(
        IEventSchemaStore schemas,
        IPlatform platform,
        IAgentEventPublisher publisher,
        IMessageTrackingStore store,
        IHandoffClientFactory handoffClients,
        IHandoffSettlementService settlement,
        IAgentSubscriptionRegistry subscriptions,
        IConfiguration config,
        IHttpContextAccessor httpContextAccessor,
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
        _logger = logger;
    }

    // ── GET /api/agent/catalog ────────────────────────────────────────────

    public async Task<ActionResult<AgentCatalog>> GetAgentCatalogAsync()
    {
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

    public async Task<ActionResult<EventTypeInfo>> PostAgentEventTypesAsync(DefineEventTypeRequest body)
    {
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
            _logger.LogWarning(ex, "Rejected event type {EventTypeId}: jsonSchema is not valid JSON Schema", body.EventTypeId);
            return new BadRequestObjectResult($"jsonSchema is not a valid JSON Schema: {ex.Message}");
        }

        // A declared sessionKeyPath must be a plausible JSONPath (starts with '$').
        if (!string.IsNullOrWhiteSpace(body.SessionKeyPath) && !body.SessionKeyPath.TrimStart().StartsWith('$'))
            return new BadRequestObjectResult("sessionKeyPath must be a JSONPath expression starting with '$'.");

        var agentId = CurrentAgentId();
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
            _logger.LogWarning("Schema conflict for event type {EventTypeId}", body.EventTypeId);
            return new ConflictResult();
        }
    }

    // ── POST /api/agent/subscribe ─────────────────────────────────────────

    public Task<IActionResult> PostAgentSubscribeAsync(AgentSubscribeRequest body)
    {
        if (string.IsNullOrWhiteSpace(body?.EventTypeId))
            return Task.FromResult<IActionResult>(new BadRequestObjectResult("eventTypeId is required."));

        var agentId = CurrentAgentId();
        _subscriptions.Subscribe(agentId, body.EventTypeId);
        _logger.LogInformation("Agent {AgentId} subscribed to {EventTypeId}", agentId, body.EventTypeId);
        return Task.FromResult<IActionResult>(new OkResult());
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
    public async Task<ActionResult<AgentReceivedMessage>> GetAgentReceiveAsync(string eventTypeId, int? waitSeconds)
    {
        var zoneId = AgentZone.ResolveEndpointId(_config);
        // Clamp the long-poll window so a client can't pin a request thread (and burn
        // store RUs) for hours by passing a huge waitSeconds.
        var wait = Math.Clamp(waitSeconds ?? 20, 0, 60);
        var deadline = DateTime.UtcNow.AddSeconds(wait);
        // Stop polling as soon as the client disconnects.
        var ct = _httpContextAccessor?.HttpContext?.RequestAborted ?? CancellationToken.None;

        // Build the type filter once: explicit query param wins; otherwise fall back
        // to the agent's registered subscriptions; otherwise match anything (null).
        var subscribed = _subscriptions.GetSubscriptions(CurrentAgentId());
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
                return new OkObjectResult(new AgentReceivedMessage
                {
                    EventTypeId = parked.EventTypeId,
                    Payload = parked.MessageContent?.EventContent?.EventJson,
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

    public async Task<IActionResult> PostAgentPublishAsync(AgentPublishRequest body)
    {
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
            _logger.LogError(ex, "Registered schema for {EventTypeId} is not valid JSON Schema", body.EventTypeId);
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

    public async Task<IActionResult> PostAgentSettleAsync(AgentSettleRequest body)
    {
        var coords = body?.Coordinates;
        if (coords == null || string.IsNullOrWhiteSpace(coords.EventId))
            return new BadRequestObjectResult("coordinates.eventId is required.");
        if (string.IsNullOrWhiteSpace(coords.MessageId))
            return new BadRequestObjectResult("coordinates.messageId is required.");
        if (body.Outcome is not AgentSettleRequestOutcome.Complete
            and not AgentSettleRequestOutcome.Fail)
        {
            return new BadRequestObjectResult("outcome must be 'complete' or 'fail'.");
        }

        var zoneId = AgentZone.ResolveEndpointId(_config);
        // Auditor for the settlement is the agent, not a request principal — the X-Agent-Id
        // header stand-in (spec 022). Recorded on the CompleteHandoff/FailHandoff audit row.
        var auditor = CurrentAgentId();
        var httpContext = _httpContextAccessor?.HttpContext;

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
            _logger.LogWarning(ex, "Could not derive session key via '{Path}' for {EventTypeId}; using a random session", sessionKeyPath, eventTypeId);
            return null;
        }
    }

    // Demo-grade agent identity: read the X-Agent-Id header, defaulting to
    // "demo-agent" when absent. Full API-key auth is deferred (spec 022).
    private string CurrentAgentId()
    {
        var header = _httpContextAccessor?.HttpContext?.Request?.Headers[AgentIdHeader].ToString();
        return string.IsNullOrWhiteSpace(header) ? DefaultAgentId : header.Trim();
    }
}
