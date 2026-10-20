using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.WebApp.Controllers.ApiContract;

public class EventTypeImplementation : IEventTypeApiController
{
    private readonly IPlatform platform;
    private readonly ICodeRepoService codeRepoService;
    private readonly FakeEventPayloadGenerator fakePayloadGenerator;
    private readonly IEndpointAuthorizationService authorizationService;

    public EventTypeImplementation(
        IPlatform platform,
        ICodeRepoService codeRepoService,
        FakeEventPayloadGenerator fakePayloadGenerator,
        IEndpointAuthorizationService authorizationService)
    {
        this.platform = platform;
        this.codeRepoService = codeRepoService;
        this.fakePayloadGenerator = fakePayloadGenerator;
        this.authorizationService = authorizationService;
    }

    // Event-type catalog data is platform-wide, so the read floor is a site
    // role (spec 026 phase D).
    private Task<bool> IsSiteReaderAsync() => authorizationService.HasRoleAsync(AccessRole.Reader);

    public async Task<ActionResult<IEnumerable<ManagementApi.EventType>>> GetEventTypesAsync()
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();

        var eventTypes = platform.EventTypes.Select(e =>
        {
            var producers = platform.GetProducers(e).Select(p => p.Name).ToList();
            var consumers = platform.GetConsumers(e).Select(c => c.Name).ToList();
            return Mapper.EventTypeFromIEventType(e, producers.Count, consumers.Count, producers, consumers);
        });
        return new OkObjectResult(eventTypes);
    }

    public async Task<ActionResult<Response>> GetEventtypesByEndpointIdAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        IEndpoint? endpoint = platform.Endpoints.FirstOrDefault(e => e.Name.Equals(endpointId, StringComparison.OrdinalIgnoreCase));
        if (endpoint == null)
        {
            return new NotFoundObjectResult($"Endpoint '{endpointId}' not found");
        }

        // Map each event type exactly once (mapping walks the type's
        // properties and probes producers/consumers per type); the
        // groupings below reuse the already-mapped EventTypes instead of
        // re-mapping the whole list a second time.
        EventTypeDetails CreateDetails(IEventType eventType) => new EventTypeDetails
        {
            EventType = Mapper.EventTypeFromIEventType(eventType),
            CodeRepoLink = codeRepoService.GetSearchUrl(eventType.Name, eventType.Namespace),
            Producers = platform.GetProducers(eventType).Select(x => x.Name).ToList(),
            Consumers = platform.GetConsumers(eventType).Select(x => x.Name).ToList(),
        };

        // Ordering invariants: details list consumed types first, then
        // produced; a type in both directions appears once per direction.
        var consumedDetails = endpoint.EventTypesConsumed.Select(CreateDetails).ToList();
        var producedDetails = endpoint.EventTypesProduced.Select(CreateDetails).ToList();

        static List<EventTypeGrouping> GroupByNamespace(IEnumerable<EventTypeDetails> details) =>
            details
                .Select(d => d.EventType)
                .GroupBy(e => e.Namespace)
                .Select(g => new EventTypeGrouping() { Namespace = g.Key, Events = g.ToList() })
                .ToList();

        return new Response
        {
            Consumes = GroupByNamespace(consumedDetails),
            Produces = GroupByNamespace(producedDetails),
            EventTypeDetails = consumedDetails.Concat(producedDetails).ToList()
        };
    }

    public async Task<ActionResult<EventTypeDetails>> GetEventtypesEventtypeidAsync(string eventtypeid)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();

        var eventType = platform.EventTypes.FirstOrDefault(et => et.Id.Equals(eventtypeid, StringComparison.OrdinalIgnoreCase));
        if (eventType == null)
        {
            return new NotFoundObjectResult($"EventType '{eventtypeid}' not found");
        }

        var eventTypeDetails = new EventTypeDetails
        {
            // The details page is the one consumer of the example payload.
            EventType = Mapper.EventTypeFromIEventType(eventType, includeExamplePayload: true),
            CodeRepoLink = codeRepoService.GetSearchUrl(eventType.Name, eventType.Namespace),
            Producers = platform.GetProducers(eventType).Select(x => x.Name).ToList(),
            Consumers = platform.GetConsumers(eventType).Select(x => x.Name).ToList(),
        };
        return new OkObjectResult(eventTypeDetails);
    }

    public async Task<ActionResult<FakeEventPayload>> GetEventtypesEventtypeidFakeAsync(string eventtypeid)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();

        var eventType = platform.EventTypes.FirstOrDefault(et => et.Id.Equals(eventtypeid, StringComparison.OrdinalIgnoreCase));
        if (eventType == null)
        {
            // Match the spec's expected body verbatim; the existing
            // GetEventtypesEventtypeidAsync also returns a 404 NotFoundObjectResult.
            return new NotFoundObjectResult("EventType not found");
        }

        var payload = fakePayloadGenerator.Generate(eventType);
        return new OkObjectResult(new FakeEventPayload { Payload = payload });
    }
}
