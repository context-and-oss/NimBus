using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using Microsoft.AspNetCore.Http;
using NimBus.Core;
using NimBus.Core.Endpoints;
using Microsoft.Extensions.Configuration;
using NimBus.Management.ServiceBus;
using System.Security.Claims;
using System.Text;
using System.Reflection;
using System.IO;
using NimBus.MessageStore.States;
using EndpointSubscription = NimBus.WebApp.ManagementApi.EndpointSubscription;
using System.Collections.Concurrent;
using TechnicalContact = NimBus.MessageStore.States.TechnicalContact;
using NimBus.WebApp.Services;
using System.Threading;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace NimBus.WebApp.Controllers;

public class EndpointImplementation : IEndpointApiController
{
    private readonly IPlatform platform;
    private readonly IConfiguration configuration;
    private readonly IMessageTrackingStore messageStore;
    private readonly ISubscriptionStore _subscriptionStore;
    private readonly IEndpointMetadataStore _metadataStore;
    private readonly IServiceBusManagement serviceBusManagement;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly HttpContext _context;
    private readonly ILogger<EndpointImplementation> _logger;
    private readonly IAuditLogService _auditLogService;
    private readonly IStoreResultCache _storeResultCache;
    private readonly PayloadRedaction _payloadRedaction;
    private const int InitialEvents = 40;
    private const int PagingEvents = 40;
    private const int SqlInvalidObjectNameErrorNumber = 208;

    public EndpointImplementation(
        IHttpContextAccessor contextAccessor,
        IPlatform platform,
        IConfiguration configuration,
        IMessageTrackingStore messageStore,
        ISubscriptionStore subscriptionStore,
        IEndpointMetadataStore metadataStore,
        IServiceBusManagement serviceBusManagement,
        IEndpointAuthorizationService authorizationService,
        ILogger<EndpointImplementation> logger,
        IAuditLogService auditLogService,
        IStoreResultCache storeResultCache,
        PayloadRedaction payloadRedaction)
    {
        this.platform = platform;
        this.configuration = configuration;
        this.messageStore = messageStore;
        _subscriptionStore = subscriptionStore;
        _metadataStore = metadataStore;
        this.serviceBusManagement = serviceBusManagement;
        this._authorizationService = authorizationService;
        _context = contextAccessor.HttpContext!;
        _logger = logger;
        _auditLogService = auditLogService;
        _storeResultCache = storeResultCache;
        _payloadRedaction = payloadRedaction;
    }

    // The endpoint lists are filtered per-user: any role on the endpoint (or any
    // site role) makes it visible. Resolution is memoized per request, so the
    // sequential loop costs one snapshot lookup per endpoint, not one store hit.
    private async Task<List<string>> GetVisibleEndpointIdsAsync()
    {
        var visible = new List<string>();
        foreach (var endpoint in platform.Endpoints)
        {
            if (ShowEndpoint(endpoint.Id) && await _authorizationService.HasRoleAsync(AccessRole.Reader, endpoint.Id))
                visible.Add(endpoint.Id);
        }

        return visible;
    }

    public async Task<ActionResult<IEnumerable<string>>> EndpointIdsAllAsync()
    {
        var endpointIds = await GetVisibleEndpointIdsAsync();

        return new OkObjectResult(endpointIds);
    }

    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> EndpointstatusAllAsync()
    {
        var endpointIds = await GetVisibleEndpointIdsAsync();

        // Each count is an independent storage aggregate query (~100-500ms against
        // Cosmos); run them concurrently (bounded) rather than serially so a
        // 40-endpoint list doesn't block for many seconds. Results go into a
        // pre-sized array by index to preserve endpoint order.
        var endpointStateCounts = new EndpointStateCount[endpointIds.Count];

        try
        {
            var countOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
            await Parallel.ForEachAsync(Enumerable.Range(0, endpointIds.Count), countOptions, async (i, _) =>
            {
                endpointStateCounts[i] = await messageStore.DownloadEndpointStateCount(endpointIds[i]);
            });
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult("Endpoint container not found in database");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult("Endpoint container not found in database");
        }

        return new OkObjectResult(endpointStateCounts.Select(Mapper.EndpointStatusCountFromEndpointStateCount));
    }

    public async Task<ActionResult<IEnumerable<Event>>> EndpointstatusAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointName))
            return new ForbidResult();

        try
        {
            var endpointState = await messageStore.DownloadEndpointStatePaging(endpointName, InitialEvents, "");
            if (endpointState == null)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            var eventIds = endpointState.FailedEvents
                .Concat(endpointState.DeferredEvents)
                .Concat(endpointState.PendingEvents)
                .Distinct()
                .ToList();

            var events = await messageStore.GetEventsByIds(endpointName, eventIds);
            var res = events.ToDictionary(e => e.EventId, Mapper.EventFromMessageStoreEvent);

            if (!await _authorizationService.CanReadPiiAsync())
            {
                foreach (var e in res.Values)
                    _payloadRedaction.Redact(e);
            }

            return new OkObjectResult(res);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
    }

    public async Task<ActionResult<EndpointStatus>> GetApiEndpointstatusStatusEndpointNameAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointName))
                return new ForbidResult();

            var endpoint = await messageStore.DownloadEndpointStatePaging(endpointName, InitialEvents, "");
            var status = Mapper.EndpointStatusFromEndpointState(endpoint!);
            if (!await _authorizationService.CanReadPiiAsync())
                _payloadRedaction.Redact(status);
            return new OkObjectResult(status);
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<SessionStatus>> GetEndpointSessionStatusAsync(string endpointId,
        string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (endpointIdValid)
        {
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
                return new ForbidResult();

            try
            {
                var sessionState = await messageStore.DownloadEndpointSessionStateCount(endpointId, sessionId);
                return new OkObjectResult(Mapper.SessionStatusFromSessionState(sessionState));
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
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<EndpointStatus>> PostApiEndpointStatusEndpointNameTokenAsync(
        ContinuationToken body, string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointName))
                return new ForbidResult();

            var endpoint = await messageStore.DownloadEndpointStatePaging(endpointName, PagingEvents, body.Token);
            var status = Mapper.EndpointStatusFromEndpointState(endpoint!);
            if (!await _authorizationService.CanReadPiiAsync())
                _payloadRedaction.Redact(status);
            return new OkObjectResult(status);
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    private bool ShowEndpoint(string endpointId)
        => EndpointVisibility.IsListed(endpointId, configuration.GetValue<string>("Environment"));


    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> GetEndpointStatusCountAllAsync()
    {
        var endpointIds = await GetVisibleEndpointIdsAsync();

        // Each status download is an independent Cosmos round-trip; probe them
        // concurrently (bounded) instead of serially so the all-endpoints view
        // isn't O(endpoints) × latency. Mirrors PostApiEndpointStatusCountAsync.
        var result = new ConcurrentBag<EndpointStatusCount>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
        await Parallel.ForEachAsync(endpointIds, options, async (endpointId, _) =>
        {
            result.Add(await DownloadStatusOrStubAsync(endpointId));
        });

        var counts = result.ToList();
        await StampSubscriptionStatusAsync(counts);
        return new OkObjectResult(counts);
    }

    public async Task<ActionResult<IEnumerable<EndpointStatusCount>>> PostApiEndpointStatusCountAsync(IEnumerable<string> body)
    {
        var result = new ConcurrentBag<EndpointStatusCount>();
        var endpointIds = new List<string>();
        foreach (var id in body as string[] ?? body.ToArray())
        {
            if (EndpointVerificationService.EndpointExists(platform, id)
                && await _authorizationService.HasRoleAsync(AccessRole.Reader, id))
            {
                endpointIds.Add(id);
            }
        }

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };

        await Parallel.ForEachAsync(endpointIds, options, async (endpointId, _) =>
        {
            result.Add(await DownloadStatusOrStubAsync(endpointId));
        });

        var counts = result.ToList();
        await StampSubscriptionStatusAsync(counts);
        return new OkObjectResult(counts);
    }

    private async Task<EndpointStatusCount> DownloadStatusOrStubAsync(string endpointId)
    {
        try
        {
            // Cached below the per-user role filtering (which
            // happens at the call sites above): the cached value is the
            // endpoint-scoped raw count, identical for every authorized user.
            // Failures propagate uncached, so the stub branches below stay live.
            var endpointStateCount = await _storeResultCache.GetEndpointStateCountAsync(messageStore, endpointId);
            return Mapper.EndpointStatusCountFromEndpointStateCount(endpointStateCount);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning(ex, "Storage container missing for endpoint {EndpointId} (Cosmos 404)", endpointId);
            return StorageUnavailableStub(endpointId);
        }
        catch (EndpointNotFoundException ex)
        {
            _logger.LogWarning(ex, "Storage container missing for endpoint {EndpointId} (EndpointNotFoundException)", endpointId);
            return StorageUnavailableStub(endpointId);
        }
        catch (SqlException ex) when (ex.Number == SqlInvalidObjectNameErrorNumber)
        {
            _logger.LogWarning(ex, "Storage table missing for endpoint {EndpointId} (SQL 208)", endpointId);
            return StorageUnavailableStub(endpointId);
        }
    }

    private static EndpointStatusCount StorageUnavailableStub(string endpointId)
        => new EndpointStatusCount
        {
            EndpointId = endpointId,
            StorageStatus = "unavailable",
        };

    public async Task<ActionResult<IEnumerable<Event>>> GetEndpointStatusIdAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointName))
            return new ForbidResult();

        try
        {
            var endpointState = await messageStore.DownloadEndpointStatePaging(endpointName, InitialEvents, "");
            if (endpointState == null)
            {
                return new NotFoundObjectResult("Endpoint not found");
            }

            var eventIds = endpointState.FailedEvents
                .Concat(endpointState.DeferredEvents)
                .Concat(endpointState.PendingEvents)
                .Distinct()
                .ToList();

            var events = await messageStore.GetEventsByIds(endpointName, eventIds);
            var res = events.ToDictionary(e => e.EventId, Mapper.EventFromMessageStoreEvent);

            if (!await _authorizationService.CanReadPiiAsync())
            {
                foreach (var e in res.Values)
                    _payloadRedaction.Redact(e);
            }

            return new OkObjectResult(res);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
        catch (EndpointNotFoundException)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }
    }

    public async Task<ActionResult<IEnumerable<string>>> GetEndpointsAllAsync()
    {
        var endpointIds = await GetVisibleEndpointIdsAsync();

        return new OkObjectResult(endpointIds);
    }

    public async Task<ActionResult<EndpointStatusCount>> GetEndpointStatusCountIdAsync(string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointName))
            {
                await _auditLogService.LogAuditAsync(MessageAuditType.GetEndpointDetails, _context,
                    accessDenied: true, endpointId: endpointName);
                return new ForbidResult();
            }

            await _auditLogService.LogAuditAsync(MessageAuditType.GetEndpointDetails, _context,
                endpointId: endpointName);

            try
            {
                var state = await messageStore.DownloadEndpointStateCount(endpointName);
                var result = Mapper.EndpointStatusCountFromEndpointStateCount(state);
                await StampSubscriptionStatusAsync([result]);

                return new OkObjectResult(result);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointName}' not found in database");
            }
            catch (EndpointNotFoundException)
            {
                return new NotFoundObjectResult($"Endpoint container '{endpointName}' not found in database");
            }
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<EndpointStatus>> PostEndpointStatusIdTokenAsync(ContinuationToken body,
        string endpointName)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (endpointIdValid)
        {
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointName))
                return new ForbidResult();

            var endpoint = await messageStore.DownloadEndpointStatePaging(endpointName, PagingEvents, body.Token);
            var status = Mapper.EndpointStatusFromEndpointState(endpoint!);
            if (!await _authorizationService.CanReadPiiAsync())
                _payloadRedaction.Redact(status);
            return new OkObjectResult(status);
        }
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<SessionStatus>> GetEndpointSessionIdAsync(string endpointId, string sessionId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (endpointIdValid)
        {
            if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
                return new ForbidResult();

            try
            {
                var sessionState = await messageStore.DownloadEndpointSessionStateCount(endpointId, sessionId);
                return new OkObjectResult(Mapper.SessionStatusFromSessionState(sessionState));
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
        return new NotFoundObjectResult("Endpoint not found");
    }

    public async Task<ActionResult<IEnumerable<SessionStatus>>> PostEndpointSessionsBatchAsync(IEnumerable<string> body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        try
        {
            var sessionStates = await messageStore.DownloadEndpointSessionStateCountBatch(endpointId, body);
            var result = sessionStates.Select(Mapper.SessionStatusFromSessionState).ToList();
            return new OkObjectResult(result);
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

    public async Task<IActionResult> PostEndpointPurgeAsync(string endpointName)
    {
        // Purge requires endpoint Owner everywhere; in prod/staging only a SITE
        // Owner may purge (mirrors the old EIP_Management-only rule there).
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointName))
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointName);
            return new ForbidResult();
        }

        // Validate environment
        var env = configuration.GetValue<string>("Environment");
        var isSiteOwner = await _authorizationService.HasRoleAsync(AccessRole.Owner);
        if (!isSiteOwner && (env!.Equals("prod", StringComparison.OrdinalIgnoreCase) ||
                             env.Equals("stag", StringComparison.OrdinalIgnoreCase)))
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointName,
                data: $"Environment={env}");
            return new NotFoundObjectResult("Endpoint cannot be purged in Production and Staging environments");
        }

        // Validate endpoint
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointName);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Purge endpoint
        var isPurged = await messageStore.PurgeMessages(endpointName);

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        await endpointManagement.ClearEndpoint(endpointName);

        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointName);

        if (isPurged)
            return new OkObjectResult($"{endpointName} is purged");

        return new NotFoundObjectResult($"{endpointName} couldn't be found");
    }

    public async Task<IActionResult> PostEndpointSubscribeAsync(EndpointSubscription body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            return new ForbidResult();
        }

        // Owner identity uses the SAME null-safe resolution the delete path
        // checks against (GetCurrentUserName). An oid/groups-only principal has
        // no name claim — creating an ownerless subscription would make it
        // undeletable for that same non-admin manager, so reject instead.
        var author = GetCurrentUsersMail() ?? _authorizationService.GetCurrentUserName();
        if (string.IsNullOrEmpty(author))
        {
            return new BadRequestObjectResult(
                "The authenticated identity carries no name claim to record as the subscription owner.");
        }

        var subscriptionStatus = await _subscriptionStore.SubscribeToEndpointNotification(endpointId, body.Mail,
            body.Type, author, body.Url, body.EventTypes, body.Payload, body.Frequency);
        return new OkObjectResult(subscriptionStatus);
    }

    public async Task<IActionResult> DeleteEndpointSubscribeAsync(SubscriptionAuthor body, string endpointId)
    {
        // Validate endpoint
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            return new ForbidResult();
        }

        if (string.IsNullOrEmpty(body?.Id))
        {
            return new BadRequestObjectResult("Subscription id is required.");
        }

        // The store deletes by global subscription id, so first pin the id to
        // THIS endpoint — a manager of endpoint A must not be able to delete
        // endpoint B's subscriptions by guessing ids.
        var subscriptions = await _subscriptionStore.GetSubscriptionsOnEndpoint(endpointId);
        var subscription = subscriptions.FirstOrDefault(s => string.Equals(s.Id, body.Id, StringComparison.Ordinal));
        if (subscription == null)
        {
            return new NotFoundObjectResult("Subscription not found on this endpoint");
        }

        // Ownership comes from authenticated claims, never from the request
        // body: only the subscription's author (or a platform administrator)
        // may remove it. Admin access is evaluated first so a principal whose
        // claims carry no display name (groups/oid only) can still delete —
        // the claim lookups are null-safe. Both name resolutions are accepted
        // because creation records GetCurrentUsersMail() ?? GetCurrentUserName()
        // and the two can surface different claims for the same principal.
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner))
        {
            var candidates = new[] { _authorizationService.GetCurrentUserName(), GetCurrentUsersMail() };
            var isOwner = candidates.Any(c => !string.IsNullOrEmpty(c)
                && string.Equals(subscription.AuthorId, c, StringComparison.OrdinalIgnoreCase));
            if (!isOwner)
            {
                return new ForbidResult();
            }
        }

        var success = await _subscriptionStore.DeleteSubscription(subscription.Id);

        if (success)
            return new OkResult();

        return new BadRequestResult();
    }

    async Task<ActionResult<IEnumerable<EndpointSubscription>>> IEndpointApiController.GetEndpointSubscribeAsync(
        string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Subscriptions expose recipient mail addresses / webhook urls — gate the
        // listing on owning the endpoint like the other subscription-admin ops.
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            return new ForbidResult();
        }

        var subscriptions = await _subscriptionStore.GetSubscriptionsOnEndpoint(endpointId);
        var mapped = Mapper.SubscriptionsFromEndpointsubscriptions(subscriptions);

        // Subscription payload filters are payload fragments — visible only to
        // PiiReaders (spec 026 phase D).
        if (!await _authorizationService.CanReadPiiAsync())
            mapped.ForEach(s => PayloadRedaction.RedactSubscription(s));

        return new OkObjectResult(mapped);
    }

    private string? GetCurrentUsersMail()
    {
        var name = _context.User.Identities.FirstOrDefault()?.Name;

        // Null-safe: a groups/oid-only principal has no Name claim — that must
        // not turn into a 500.
        if (string.IsNullOrEmpty(name))
            name = _context.User.FindFirst(ClaimTypes.Name)?.Value;

        return name;
    }

    public async Task<ActionResult<string>> GetEndpointRoleAssignmentScriptAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        var builder = new StringBuilder();

        var subscriptionId = configuration.GetSection("ServiceBusManagement:SubscriptionId").Value;
        var environment = configuration.GetSection("Environment").Value;
        var resourceGroupName = configuration.GetSection("ServiceBusManagement:ResourceGroupName").Value;
        var serviceBusNamespace = configuration.GetSection("ServiceBusNamespace").Value;
        var endpoint = platform.Endpoints.FirstOrDefault(ep => ep.Id.Equals(endpointId, StringComparison.OrdinalIgnoreCase));

        if (endpoint == null)
        {
            return new NotFoundObjectResult($"Endpoint '{endpointId}' not found");
        }

        var principals = endpoint.RoleAssignments
            .Where(x => x.Environment.Equals(environment, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.PrincipalId).ToList();
        if (principals.Count > 0)
        {
            await using Stream? stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EET.EIP.WebApp.Resources.EndpointRoleAssignment.ps1");
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                builder.AppendLine($"$subscription =\"{subscriptionId}\" \n");
                builder.AppendLine($"$resourceGroupName = \"{resourceGroupName}\" \n");
                builder.Append(await reader.ReadToEndAsync());
                builder.AppendLine();
                foreach (var principal in principals)
                {
                    builder.AppendLine(
                        $"Assign-ServiceBusSubscription -assigneeId \"{principal}\" -serviceBusNamespace \"{serviceBusNamespace}\" -topic \"{endpoint.Id}\" -subscription \"{endpoint.Id}\" \n");
                }
            }
        }

        return new OkObjectResult(builder.ToString());
    }

    public async Task<IActionResult> PostEndpointSubscriptionstatusAsync(string body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Resolve the audit-type up front; both branches need it for the
        // access-denied path and the success path.
        MessageAuditType? auditType = body switch
        {
            "enable" => MessageAuditType.EnableEndpoint,
            "disable" => MessageAuditType.DisableEndpoint,
            _ => null,
        };

        if (auditType.HasValue && !await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            await _auditLogService.LogAuditAsync(auditType.Value, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        switch (body)
        {
            case "enable":
                {
                    await endpointManagement.EnableEndpoint(endpointId);
                    var metadata = await _metadataStore.GetEndpointMetadata(endpointId)
                        ?? new EndpointMetadata { EndpointId = endpointId };
                    metadata.SubscriptionStatus = true;
                    await _metadataStore.SetEndpointMetadata(metadata);
                    await _auditLogService.LogAuditAsync(MessageAuditType.EnableEndpoint, _context,
                        endpointId: endpointId);
                    if (await endpointManagement.IsEndpointActive(endpointId))
                        return new OkObjectResult($"{endpointId} is active");
                    break;
                }
            case "disable":
                {
                    await endpointManagement.DisableEndpoint(endpointId);
                    var metadata = await _metadataStore.GetEndpointMetadata(endpointId)
                        ?? new EndpointMetadata { EndpointId = endpointId };
                    metadata.SubscriptionStatus = false;
                    await _metadataStore.SetEndpointMetadata(metadata);
                    await _auditLogService.LogAuditAsync(MessageAuditType.DisableEndpoint, _context,
                        endpointId: endpointId);
                    if (!await endpointManagement.IsEndpointActive(endpointId))
                        return new OkObjectResult($"{endpointId} is disable");
                    break;
                }
        }

        return new NotFoundObjectResult($"{endpointId} status not set");
    }

    public async Task<ActionResult<string>> GetEndpointSubscriptionstatusAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        var subscriptionState = await endpointManagement.GetEndpointSubscriptionState(endpointId);
        if (subscriptionState == SubscriptionState.Active)
        {
            return new OkObjectResult($"active");
        }

        if (subscriptionState == SubscriptionState.NotFound)
        {
            return new OkObjectResult("not-found");
        }

        return new OkObjectResult("disabled");
    }

    public async Task<IActionResult> PostEndpointSendstatusAsync(string body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        MessageAuditType? auditType = body switch
        {
            "enable" => MessageAuditType.EnableEndpointSend,
            "disable" => MessageAuditType.DisableEndpointSend,
            _ => null,
        };

        if (auditType.HasValue && !await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            await _auditLogService.LogAuditAsync(auditType.Value, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        switch (body)
        {
            case "enable":
                {
                    await endpointManagement.EnableEndpointSend(endpointId);
                    await _auditLogService.LogAuditAsync(MessageAuditType.EnableEndpointSend, _context,
                        endpointId: endpointId);
                    if (await endpointManagement.GetEndpointSendState(endpointId) == TopicSendState.Enabled)
                        return new OkObjectResult($"{endpointId} send is active");
                    break;
                }
            case "disable":
                {
                    await endpointManagement.DisableEndpointSend(endpointId);
                    await _auditLogService.LogAuditAsync(MessageAuditType.DisableEndpointSend, _context,
                        endpointId: endpointId);
                    if (await endpointManagement.GetEndpointSendState(endpointId) == TopicSendState.SendDisabled)
                        return new OkObjectResult($"{endpointId} send is disabled");
                    break;
                }
        }

        return new NotFoundObjectResult($"{endpointId} send status not set");
    }

    public async Task<ActionResult<string>> GetEndpointSendstatusAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        var endpointManagement = new EndpointManagement(serviceBusManagement);
        var sendState = await endpointManagement.GetEndpointSendState(endpointId);
        if (sendState == TopicSendState.Enabled)
        {
            return new OkObjectResult($"active");
        }

        if (sendState == TopicSendState.NotFound)
        {
            return new OkObjectResult("not-found");
        }

        return new OkObjectResult("disabled");
    }

    public async Task<ActionResult<Metadata>> GetMetadataEndpointAsync(string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();

        var metadata = await _metadataStore.GetEndpointMetadata(endpointId);
        if (metadata == null)
            return new NotFoundObjectResult($"Metadata for {endpointId} not found");
        if (metadata.SubscriptionStatus == null)
        {
            await SetSubscriptionStatusMetadata(metadata);
        }

        return new OkObjectResult(Mapper.MetadataFromEndpointMetadata(metadata));
    }




    public async Task<ActionResult<IEnumerable<MetadataShort>>> PostApiMetadatashortAsync(IEnumerable<string> body)
    {
        // Per-endpoint Reader filter (like the status-count batch) so an
        // endpoint-scoped Reader still gets metadata for their endpoints.
        var endpointIds = new List<string>();
        foreach (var id in body)
        {
            if (await _authorizationService.HasRoleAsync(AccessRole.Reader, id))
                endpointIds.Add(id);
        }

        foreach (var id in endpointIds)
        {
            if (!EndpointVerificationService.EndpointExists(platform, id))
            {
                return new NotFoundObjectResult(String.Format("Endpoint with id {0} not found",id));
            }
        }

        var metadataList = await ResolveMetadataWithSubscriptionStatusAsync(endpointIds);

        return new OkObjectResult(Mapper.MetadataShortFromList(metadataList));
    }

    /// <summary>
    /// Endpoint metadata for the given ids, with every unknown SubscriptionStatus
    /// probed and persisted. Endpoints with no stored metadata get a blank record
    /// so the caller always gets one entry per requested id.
    /// </summary>
    private async Task<List<EndpointMetadata>> ResolveMetadataWithSubscriptionStatusAsync(List<string> endpointIds)
    {
        var metadataList = await _metadataStore.GetMetadatas(endpointIds)
            ?? endpointIds.Select(x => new EndpointMetadata { EndpointId = x }).ToList();

        foreach (var s in endpointIds.Where(s => !metadataList.Exists(m => m.EndpointId.Equals(s, StringComparison.OrdinalIgnoreCase))))
        {
            metadataList.Add(new EndpointMetadata { EndpointId = s });
        }

        // Each probe is an independent Service Bus admin REST call (~100-500ms);
        // run them concurrently (bounded) rather than serially so a 40-endpoint
        // list doesn't block for many seconds. Each call mutates its own metadata
        // object, so there's no shared state to guard.
        var pendingStatus = metadataList.Where(m => m.SubscriptionStatus == null);
        var statusOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
        await Parallel.ForEachAsync(pendingStatus, statusOptions, async (endpointMetadata, _) =>
        {
            await SetSubscriptionStatusMetadata(endpointMetadata);
        });

        return metadataList;
    }

    /// <summary>
    /// Stamps EndpointStatusCount.SubscriptionStatus onto freshly built counts.
    /// The receive-enabled flag lives on endpoint metadata, not on the state-count
    /// aggregate, so every count path has to join it in — without this the field
    /// is always null on the wire and the endpoints list can never show an
    /// endpoint as Disabled or its subscription as missing. Resolved outside the
    /// state-count cache so flipping receive is visible on the next refresh
    /// rather than whenever that TTL happens to lapse.
    /// </summary>
    private async Task StampSubscriptionStatusAsync(IReadOnlyCollection<EndpointStatusCount> counts)
    {
        var endpointIds = counts
            .Select(c => c.EndpointId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        if (endpointIds.Count == 0)
        {
            return;
        }

        var metadataList = await ResolveMetadataWithSubscriptionStatusAsync(endpointIds);
        foreach (var count in counts)
        {
            var metadata = metadataList.Find(m =>
                m.EndpointId != null && m.EndpointId.Equals(count.EndpointId, StringComparison.OrdinalIgnoreCase));

            // Only report a status the metadata actually knows. A null flag means
            // "unknown", not "missing": SetSubscriptionStatusMetadata leaves it null
            // both when the admin probe fails (the emulator has no reachable admin
            // REST port, and real Azure has outages) and when the subscription is
            // genuinely absent, so the two are indistinguishable here. Claiming
            // "not-found" would label a healthy endpoint as Subscription Missing and
            // dead its enable/disable switch on the strength of one failed probe.
            if (metadata?.SubscriptionStatus != null)
            {
                count.SubscriptionStatus = Mapper.SubscriptionStatusFromMetadata(metadata);
            }
        }
    }


    public async Task<IActionResult> PostMetadataEndpointAsync(Metadata body, string endpointId)
    {
        var endpointIdValid = EndpointVerificationService.EndpointExists(platform, endpointId);
        if (!endpointIdValid)
        {
            return new NotFoundObjectResult("Endpoint not found");
        }

        // Spec 026: metadata writes were previously ungated — editing ownership
        // and contact records is an endpoint-Owner operation.
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            return new ForbidResult();
        }

        var technicalContacts = body.TechnicalContacts
            .Select(technicalContact => new TechnicalContact()
            { Name = technicalContact.Name, Email = technicalContact.Email }).ToList();


        var metadataStatus = await _metadataStore.SetEndpointMetadata(
            new EndpointMetadata
            {
                EndpointOwner = body.EndpointOwner,
                EndpointId = body.Id,
                EndpointOwnerTeam = body.EndpointOwnerTeam,
                EndpointOwnerEmail = body.EndpointOwnerEmail,
                TechnicalContacts = technicalContacts,
            });
        return new OkObjectResult(metadataStatus);
    }

    private async Task SetSubscriptionStatusMetadata(EndpointMetadata metadata)
    {
        // Querying the Service Bus admin REST endpoint can fail in two known
        // scenarios: (1) running against the emulator, whose admin REST port
        // (5300) isn't reachable through the standard connection string the
        // SDK uses, and (2) transient real-Azure outages during page loads.
        // Either way, a single subscription-status probe failing should not
        // 500 the entire endpoints list — leave SubscriptionStatus null
        // ("unknown") and let the page still render.
        SubscriptionState? subscriptionState = null;
        try
        {
            var endpointManagement = new EndpointManagement(serviceBusManagement);
            subscriptionState = await endpointManagement.GetEndpointSubscriptionState(metadata.EndpointId);
        }
        catch (Exception)
        {
            // Swallow — SubscriptionStatus stays null, surfaced in UI as "unknown".
        }

        metadata.SubscriptionStatus = subscriptionState switch
        {
            SubscriptionState.Active => true,
            SubscriptionState.Disabled => false,
            _ => null,
        };

        // Only spend RU persisting a *known* status. A failed/unknown probe leaves
        // SubscriptionStatus null for this render without a redundant "unknown"
        // upsert (the previous code wrote on every probe, including failures).
        if (metadata.SubscriptionStatus != null)
        {
            await _metadataStore.SetEndpointMetadata(metadata);
        }
    }
}
