using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Extensions.IntegrationIntelligence.Storage;
using NimBus.MessageStore;
using NimBus.ServiceBus.AsyncApi;
using NimBus.ServiceBus.Provisioning;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Heartbeat;
using NimBus.WebApp.Services.IntegrationIntelligence;
using AsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;

namespace NimBus.WebApp.Controllers.ApiContract;

public class AdminImplementation : IAdminApiController
{
    private readonly IAdminService _adminService;
    private readonly ISubscriptionAdminService _subscriptionAdminService;
    private readonly IPlatform _platform;
    private readonly IConfiguration _configuration;
    private readonly HttpContext _context;
    private readonly IAuditLogService _auditLogService;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly IHeartbeatService _heartbeatService;
    private readonly ILogger<AdminImplementation> _logger;
    private readonly ICosmosContainerAdmin? _containerAdmin;
    private readonly IAuditSettingsProvider? _auditSettings;

    public AdminImplementation(
        IHttpContextAccessor contextAccessor,
        IAdminService adminService,
        ISubscriptionAdminService subscriptionAdminService,
        IPlatform platform,
        IConfiguration configuration,
        IAuditLogService auditLogService,
        IEndpointAuthorizationService authorizationService,
        IHeartbeatService heartbeatService,
        ILogger<AdminImplementation>? logger = null,
        ICosmosContainerAdmin? containerAdmin = null,
        IAuditSettingsProvider? auditSettings = null)
    {
        _adminService = adminService;
        _subscriptionAdminService = subscriptionAdminService;
        _platform = platform;
        _configuration = configuration;
        _context = contextAccessor.HttpContext!;
        _auditLogService = auditLogService;
        _authorizationService = authorizationService;
        _heartbeatService = heartbeatService;
        _logger = logger ?? NullLogger<AdminImplementation>.Instance;
        _containerAdmin = containerAdmin;
        _auditSettings = auditSettings;
    }

    public async Task<ActionResult<PlatformConfig>> GetAdminPlatformConfigAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var result = await _adminService.GetPlatformConfigAsync(_platform);
        return new OkObjectResult(result);
    }

    // Full-platform AsyncAPI 3.0 export. Admin-only (EIP_Management), matching platform-config access.
    // Authorization is checked before any serialization work. Missing/empty format defaults to YAML;
    // only 'yaml' and 'json' are accepted — anything else is a 400 (never a silent default).
    public async Task<IActionResult> GetAdminAsyncapiAsync(string format)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        AsyncApiFormat exportFormat;
        string fileName;
        string contentType;

        if (string.IsNullOrWhiteSpace(format) ||
            string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase))
        {
            exportFormat = AsyncApiFormat.Yaml;
            fileName = "nimbus-asyncapi.yaml";
            contentType = "application/x-yaml";
        }
        else if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            exportFormat = AsyncApiFormat.Json;
            fileName = "nimbus-asyncapi.json";
            contentType = "application/json";
        }
        else
        {
            return new BadRequestObjectResult($"Unsupported format '{format}'. Use 'yaml' or 'json'.");
        }

        var content = AsyncApiExporter.Serialize(_platform, exportFormat);
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FileContentResult(bytes, contentType) { FileDownloadName = fileName };
    }

    public async Task<ActionResult<TopologyAuditResult>> GetAdminTopologyAsync(string endpointName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.AuditTopologyAsync(endpointName);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<TopologyCleanupResult>> PostAdminTopologyRemoveDeprecatedAsync(string endpointName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointName))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.RemoveDeprecatedTopologyAsync(endpointName);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkResubmitPreview>> GetAdminFailedPreviewAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewFailedMessagesAsync(endpointId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminBulkResubmitAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.BulkResubmitFailedAsync(endpointId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<Response2>> GetAdminDeadletteredPreviewAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var count = await _adminService.GetDeadLetteredCountAsync(endpointId);
        var queue = await _adminService.GetDeadLetterQueueCountsAsync(endpointId);
        return new OkObjectResult(new Response2
        {
            Count = count,
            QueueCount = queue?.Total,
            MatchingQueueCount = queue?.MatchingStoredEvents,
        });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteDeadletteredAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteDeadLetteredAsync(endpointId);
        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(new
            {
                operation = "delete-deadlettered",
                result.Processed,
                result.Succeeded,
                result.Failed,
            }));
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<SessionPurgePreview>> GetAdminSessionPreviewAsync(string endpointId, string sessionId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewSessionPurgeAsync(endpointId, sessionId);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<SessionPurgeResult>> PostAdminSessionPurgeAsync(string endpointId, string sessionId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointId,
                data: JsonConvert.SerializeObject(new { sessionId }));
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PurgeSessionAsync(endpointId, sessionId);
        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(new { sessionId }));
        return new OkObjectResult(result);
    }

    public async Task<IActionResult> DeleteAdminEventAsync(string endpointId, string eventId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var deleted = await _adminService.DeleteEventAsync(endpointId, eventId);
        if (deleted)
            return new OkResult();

        return new NotFoundResult();
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteAllAsync(string endpointId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.DeleteAllEventsAsync(endpointId);
        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(new
            {
                operation = "delete-all",
                result.Processed,
                result.Succeeded,
                result.Failed,
            }));
        return new OkObjectResult(result);
    }

    // ───────────── Service Bus subscriptions ─────────────
    //
    // Listing is allowed for any topic in the namespace, including ones outside the
    // platform topology — the overview lists them deliberately, so a stray topic sitting
    // on a backlog is visible. Every mutation is gated on the topic being one NimBus owns:
    // acting on someone else's entity from here would be a surprise with no way back.

    public async Task<ActionResult<IEnumerable<ServiceBusTopicOverview>>> GetAdminServicebusTopicsAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var result = await _subscriptionAdminService.GetTopicOverviewAsync();
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<IEnumerable<CosmosContainerInfo>>> GetAdminCosmosContainersAsync()
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (_containerAdmin is null)
            return new NotFoundObjectResult("Cosmos DB storage is not configured.");

        try
        {
            var containers = await _containerAdmin.ListContainerIdsAsync(_context.RequestAborted);
            var result = containers
                .Order(StringComparer.Ordinal)
                .Select(name => new CosmosContainerInfo { Name = name, IsInPlatform = IsProtectedContainer(name) })
                .ToArray();
            return new OkObjectResult(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not list Cosmos DB containers");
            return new ObjectResult("Cosmos DB containers could not be loaded.")
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }
    }

    public async Task<ActionResult<CosmosContainerDeleteResult>> PostAdminCosmosContainerDeleteAsync(
        CosmosContainerDeleteRequest body,
        string containerName)
    {
        var auditData = JsonConvert.SerializeObject(new { containerName });
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.DeleteStorageContainer, _context,
                accessDenied: true, data: auditData);
            return new ForbidResult();
        }

        if (body is null || !string.Equals(body.Confirmation, containerName, StringComparison.Ordinal))
            return new BadRequestObjectResult("Confirmation must exactly match the container name.");
        if (IsProtectedContainer(containerName))
            return new BadRequestObjectResult("Platform and internal NimBus containers cannot be deleted.");
        if (_containerAdmin is null)
            return new NotFoundObjectResult("Cosmos DB storage is not configured.");

        try
        {
            var containers = await _containerAdmin.ListContainerIdsAsync(_context.RequestAborted);
            if (!containers.Contains(containerName, StringComparer.Ordinal))
                return new NotFoundObjectResult($"Container '{containerName}' was not found.");

            var deleted = await _containerAdmin.DeleteContainerAsync(containerName, _context.RequestAborted);
            if (!deleted) return new NotFoundObjectResult($"Container '{containerName}' was not found.");

            await _auditLogService.LogAuditAsync(MessageAuditType.DeleteStorageContainer, _context,
                data: auditData);
            return new OkObjectResult(new CosmosContainerDeleteResult { Name = containerName, Deleted = true });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not delete Cosmos DB container {ContainerName}", containerName);
            await _auditLogService.LogAuditAsync(MessageAuditType.DeleteStorageContainer, _context,
                data: JsonConvert.SerializeObject(new { containerName, success = false }));
            return new ObjectResult("The Cosmos DB container could not be deleted.")
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }
    }

    // Integration Intelligence keeps its settings and classification history in this database
    // too. Neither is a message-store or endpoint container, but deleting either destroys the
    // extension's data, so both are protected whether or not the feature is enabled.
    private bool IsProtectedContainer(string containerName) =>
        CosmosContainerDefaults.ReservedContainerIds.Contains(containerName)
        || containerName is CosmosIntelligenceSettingsStore.ContainerName
            or CosmosClassificationStore.DefaultContainerName
        || _platform.Endpoints.Any(endpoint =>
            string.Equals(endpoint.Id, containerName, StringComparison.Ordinal));

    public async Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusTopicAsync(string topicName)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (IsPlatformTopic(topicName))
            return new BadRequestObjectResult("Platform topics cannot be deleted from this page.");

        var result = await _subscriptionAdminService.DeleteTopicAsync(topicName);
        await _auditLogService.LogAuditAsync(MessageAuditType.ManageSubscription, _context,
            data: JsonConvert.SerializeObject(new { topicName, action = "delete-topic" }));
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<IEnumerable<ServiceBusSubscriptionInfo>>> GetAdminServicebusSubscriptionsAsync(string topicName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var result = await _subscriptionAdminService.GetSubscriptionsAsync(topicName);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<DeadLetterOverview>> GetAdminServicebusResolverDeadlettersAsync(string subscriptionName)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        try
        {
            var result = await _subscriptionAdminService.GetResolverDeadLettersAsync(
                subscriptionName,
                _context.RequestAborted);
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
        catch (ResolverDeadLetterTargetNotSupportedException exception)
        {
            return new BadRequestObjectResult(exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "Could not inspect Resolver dead letters for subscription {SubscriptionName}",
                subscriptionName);
            return new ObjectResult("Resolver dead letters could not be inspected.")
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminServicebusResolverDeadlettersResubmitAsync(
        DeadLetterResubmitRequest body,
        string subscriptionName)
    {
        const string action = "resubmit-resolver-deadletters";
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(
                MessageAuditType.ManageSubscription,
                _context,
                accessDenied: true,
                data: JsonConvert.SerializeObject(new
                {
                    action,
                    topicName = NimBus.Core.Messages.Constants.ResolverId,
                    subscriptionName,
                }));
            return new ForbidResult();
        }

        if (body is null
            || (body.Scope != DeadLetterResubmitRequestScope.All && body.Scope != DeadLetterResubmitRequestScope.Reason)
            || (body.Scope == DeadLetterResubmitRequestScope.All && body.Reason is not null))
        {
            return new BadRequestObjectResult("The dead-letter replay selection is invalid.");
        }

        try
        {
            var all = body.Scope == DeadLetterResubmitRequestScope.All;
            var result = await _subscriptionAdminService.ResubmitResolverDeadLettersAsync(
                subscriptionName,
                all,
                body.Reason,
                _context.RequestAborted);
            await LogResolverReplayResultAsync(action, subscriptionName, all, body.Reason, result);
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
        catch (ResolverDeadLetterTargetNotSupportedException exception)
        {
            return new BadRequestObjectResult(exception.Message);
        }
        catch (ResolverReplayInProgressException exception)
        {
            return new ConflictObjectResult(exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "Could not replay Resolver dead letters for subscription {SubscriptionName}",
                subscriptionName);
            await LogResolverReplayResultAsync(
                action,
                subscriptionName,
                body.Scope == DeadLetterResubmitRequestScope.All,
                body.Reason,
                new BulkOperationResult { Processed = 0, Succeeded = 0, Failed = 0 },
                overallSuccess: false);
            return new ObjectResult("Resolver dead letters could not be replayed.")
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
        }
    }

    private Task LogResolverReplayResultAsync(
        string action,
        string subscriptionName,
        bool all,
        string? reason,
        BulkOperationResult result,
        bool? overallSuccess = null) =>
        _auditLogService.LogAuditAsync(
            MessageAuditType.ManageSubscription,
            _context,
            data: JsonConvert.SerializeObject(new
            {
                action,
                topicName = NimBus.Core.Messages.Constants.ResolverId,
                subscriptionName,
                scope = all ? "all" : "reason",
                reason = all ? null : reason,
                result.Processed,
                result.Succeeded,
                result.Failed,
                success = overallSuccess ?? result.Failed == 0,
            }));

    public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionStatusAsync(
        SubscriptionStatusRequest body, string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName,
            body?.Action == SubscriptionStatusRequestAction.Enable ? "resume" : "pause",
            () => _subscriptionAdminService.SetSubscriptionStatusAsync(
                topicName, subscriptionName, body?.Action == SubscriptionStatusRequestAction.Enable));

    public async Task<ActionResult<BulkOperationResult>> PostAdminServicebusSubscriptionPurgeAsync(
        string topicName, string subscriptionName)
    {
        var denied = await GuardSubscriptionMutationAsync(topicName, subscriptionName, "purge");
        if (denied is not null) return denied;

        try
        {
            var result = await _subscriptionAdminService.PurgeSubscriptionAsync(topicName, subscriptionName);
            await LogSubscriptionAuditAsync(topicName, subscriptionName, "purge");
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
        catch (SubscriptionPurgeNotSupportedException exception)
        {
            return new BadRequestObjectResult(exception.Message);
        }
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminServicebusSubscriptionDeadlettersPurgeAsync(
        string topicName, string subscriptionName)
    {
        var denied = await GuardSubscriptionMutationAsync(topicName, subscriptionName, "purge-dead-letters");
        if (denied is not null) return denied;

        try
        {
            var result = await _subscriptionAdminService.PurgeDeadLetterQueueAsync(topicName, subscriptionName);
            await LogSubscriptionAuditAsync(topicName, subscriptionName, "purge-dead-letters");
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
    }

    public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionRecreateAsync(
        string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, "recreate",
            () => _subscriptionAdminService.RecreateSubscriptionAsync(topicName, subscriptionName));

    public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusSubscriptionAsync(
        string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, "delete",
            () => _subscriptionAdminService.DeleteSubscriptionAsync(topicName, subscriptionName));

    public Task<ActionResult<SubscriptionActionResult>> DeleteAdminServicebusSubscriptionRuleAsync(
        string topicName, string subscriptionName, string ruleName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, $"detach-rule:{ruleName}",
            () => _subscriptionAdminService.DeleteRuleAsync(topicName, subscriptionName, ruleName));

    public Task<ActionResult<SubscriptionActionResult>> PostAdminServicebusSubscriptionRestoreRulesAsync(
        string topicName, string subscriptionName) =>
        MutateSubscriptionAsync(topicName, subscriptionName, "restore-rules",
            () => _subscriptionAdminService.RestoreRulesAsync(topicName, subscriptionName));

    /// <summary>
    /// Authorization, ownership and audit around one mutating subscription action, with the
    /// service's typed failures mapped to status codes rather than surfacing as a 500.
    /// </summary>
    private async Task<ActionResult<SubscriptionActionResult>> MutateSubscriptionAsync(
        string topicName,
        string subscriptionName,
        string action,
        Func<Task<SubscriptionActionResult>> mutate)
    {
        var denied = await GuardSubscriptionMutationAsync(topicName, subscriptionName, action);
        if (denied is not null) return denied;

        try
        {
            var result = await mutate();
            await LogSubscriptionAuditAsync(topicName, subscriptionName, action);
            return new OkObjectResult(result);
        }
        catch (SubscriptionNotFoundException exception)
        {
            return new NotFoundObjectResult(exception.Message);
        }
        catch (SubscriptionNotDescribableException exception)
        {
            return new BadRequestObjectResult(exception.Message);
        }
    }

    private async Task<ActionResult?> GuardSubscriptionMutationAsync(string topicName, string subscriptionName, string action)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.ManageSubscription, _context,
                accessDenied: true,
                data: JsonConvert.SerializeObject(new { topicName, subscriptionName, action }));
            return new ForbidResult();
        }

        if (!IsPlatformTopic(topicName))
            return new NotFoundObjectResult($"Topic '{topicName}' is not part of the platform topology.");

        return null;
    }

    private Task LogSubscriptionAuditAsync(string topicName, string subscriptionName, string action) =>
        _auditLogService.LogAuditAsync(MessageAuditType.ManageSubscription, _context,
            data: JsonConvert.SerializeObject(new { topicName, subscriptionName, action }));

    private bool IsPlatformTopic(string topicName) =>
        TopologyDescriptor.IsSystemTopic(topicName)
        || EndpointVerificationService.EndpointExists(_platform, topicName);

    // ───────────── Advanced Operations ─────────────

    public async Task<ActionResult<PurgePreview>> PostAdminPurgePreviewAsync(string endpointId, PurgeRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionPreviewAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<StalePendingPreview>> PostAdminStalePendingPreviewAsync(
        string endpointId,
        StalePendingReconcileRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.PreviewStalePendingAsync(
            endpointId,
            body?.EnqueuedBefore,
            body?.MaxRows ?? AdminService.DefaultStalePendingMaxRows);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<StalePendingReconcileResult>> PostAdminStalePendingReconcileAsync(
        string endpointId,
        StalePendingReconcileRequest body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(
                MessageAuditType.ReconcileStalePending,
                _context,
                accessDenied: true,
                endpointId: endpointId,
                data: JsonConvert.SerializeObject(body));
            return new ForbidResult();
        }

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var cutoff = body?.EnqueuedBefore;
        if (!cutoff.HasValue || cutoff.Value > DateTime.UtcNow.AddMinutes(-AdminService.StalePendingAgeMinutes))
            return new BadRequestObjectResult(
                $"enqueuedBefore is required and must be at least {AdminService.StalePendingAgeMinutes} minutes in the past.");

        var auditorName = AuditLogService.ResolveAuditorName(_context);
        var result = await _adminService.ReconcileStalePendingAsync(
            endpointId,
            cutoff.Value,
            body?.MaxRepairs,
            auditorName,
            body?.Note);
        await _auditLogService.LogAuditAsync(
            MessageAuditType.ReconcileStalePending,
            _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(new
            {
                request = body,
                result.Processed,
                result.Succeeded,
                result.Failed,
                result.Skipped,
            }));
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminPurgeAsync(string endpointId, PurgeRequest body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
                accessDenied: true, endpointId: endpointId,
                data: JsonConvert.SerializeObject(body));
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var subscription = string.IsNullOrEmpty(body.Subscription) ? endpointId : body.Subscription;
        var result = await _adminService.PurgeSubscriptionAsync(endpointId, subscription, body.States?.ToList() ?? new(), body.Before);
        await _auditLogService.LogAuditAsync(MessageAuditType.PurgeMessages, _context,
            endpointId: endpointId,
            data: JsonConvert.SerializeObject(body));
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminDeleteByToPreviewAsync(DeleteByToRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        var count = await _adminService.DeleteMessagesByToPreviewAsync(body.ToField);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteByToAsync(DeleteByToRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        var result = await _adminService.DeleteMessagesByToAsync(body.ToField);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminDeleteByStatusPreviewAsync(string endpointId, DeleteByStatusRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        if (!AdminStatusValidation.TryNormalizeDeleteStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var count = await _adminService.DeleteByStatusPreviewAsync(endpointId, statuses);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminDeleteByStatusAsync(string endpointId, DeleteByStatusRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        if (!AdminStatusValidation.TryNormalizeDeleteStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var result = await _adminService.DeleteByStatusAsync(endpointId, statuses);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminSkipPreviewAsync(string endpointId, SkipRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        var before = body?.Before;
        if (!AdminStatusValidation.TryNormalizeSkipStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var count = await _adminService.SkipMessagesPreviewAsync(endpointId, statuses, before);
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<BulkOperationResult>> PostAdminSkipAsync(string endpointId, SkipRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");
        var before = body?.Before;
        if (!AdminStatusValidation.TryNormalizeSkipStatuses(
                body?.Statuses?.Select(status => status.ToString()),
                out var statuses,
                out var error))
            return new BadRequestObjectResult(error);

        var result = await _adminService.SkipMessagesAsync(endpointId, statuses, before);
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CopyResult>> PostAdminCopyAsync(string endpointId, CopyRequest body)
    {
        if (!await IsSiteOwnerAsync()) return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId)) return new NotFoundObjectResult("Endpoint not found");

        var result = await _adminService.CopyEndpointDataAsync(
            endpointId, body.TargetConnectionString,
            body.From, body.To,
            body.Statuses?.ToList() ?? new(), body.BatchSize);
        return new OkObjectResult(result);
    }

    // ───────────── Audit settings ─────────────
    //
    // Which audit types are recorded. Owner-gated; the write is audited on both
    // branches as UpdateAuditSettings, which the selection can never switch off.

    public async Task<ActionResult<AuditSettings>> GetAdminAuditSettingsAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();
        if (_auditSettings is null)
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);

        return new OkObjectResult(ToAuditSettings(await _auditSettings.GetDisabledAsync()));
    }

    public async Task<ActionResult<AuditSettings>> PutAdminAuditSettingsAsync(AuditSettings body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.UpdateAuditSettings, _context,
                accessDenied: true, data: JsonConvert.SerializeObject(new { body?.DisabledAuditTypes }));
            return new ForbidResult();
        }

        if (body is null)
            return new BadRequestObjectResult("An audit settings body is required.");
        if (_auditSettings is null)
            return new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);

        var disabled = new List<MessageAuditType>();
        var rejected = new List<string>();
        foreach (var name in body.DisabledAuditTypes ?? Enumerable.Empty<string>())
        {
            if (AuditSettingsProvider.TryParseName(name, out var type) && AuditSettingsProvider.Configurable.Contains(type))
                disabled.Add(type);
            else
                rejected.Add(name ?? "null");
        }

        if (rejected.Count != 0)
            return new BadRequestObjectResult($"These audit types cannot be disabled: {string.Join(", ", rejected)}");

        var result = ToAuditSettings(await _auditSettings.SaveAsync(disabled));
        await _auditLogService.LogAuditAsync(MessageAuditType.UpdateAuditSettings, _context,
            data: JsonConvert.SerializeObject(new { result.DisabledAuditTypes }));
        return new OkObjectResult(result);
    }

    // The contract spells audit types camelCase, as AuditEntry.auditType does.
    private static AuditSettings ToAuditSettings(IReadOnlySet<MessageAuditType> disabled) => new()
    {
        DisabledAuditTypes = AuditSettingsProvider.Configurable.Where(disabled.Contains).Select(ToContractName).ToList(),
        ConfigurableAuditTypes = AuditSettingsProvider.Configurable.Select(ToContractName).ToList(),
    };

    private static string ToContractName(MessageAuditType type)
    {
        var name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    // ───────────── Platform heartbeat ─────────────
    //
    // Reads are Owner-gated like everything else here; the three mutations are
    // audited on both the success and the denial branch.

    public async Task<ActionResult<HeartbeatSettings>> GetAdminHeartbeatSettingsAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var settings = await _heartbeatService.GetSettingsAsync();
        return new OkObjectResult(Mapper.HeartbeatSettingsFromState(settings));
    }

    public async Task<ActionResult<HeartbeatSettings>> PutAdminHeartbeatSettingsAsync(HeartbeatSettings body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.UpdateHeartbeatSettings, _context,
                accessDenied: true, data: JsonConvert.SerializeObject(body));
            return new ForbidResult();
        }

        if (body is null)
            return new BadRequestObjectResult("A heartbeat settings body is required.");

        // The service clamps interval and timeout, so the audit records what was
        // actually stored rather than what was asked for.
        var stored = await _heartbeatService.SetSettingsAsync(Mapper.HeartbeatSettingsToState(body));
        var result = Mapper.HeartbeatSettingsFromState(stored);
        await _auditLogService.LogAuditAsync(MessageAuditType.UpdateHeartbeatSettings, _context,
            data: JsonConvert.SerializeObject(result));
        return new OkObjectResult(result);
    }

    public async Task<ActionResult<CountResponse>> PostAdminHeartbeatSendAsync()
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.SendHeartbeatNow, _context, accessDenied: true);
            return new ForbidResult();
        }

        // "Send now" ignores the Enabled switch — an operator asking for a probe
        // right now has said what they want.
        var count = await _heartbeatService.SendHeartbeatsAsync(force: true);
        await _auditLogService.LogAuditAsync(MessageAuditType.SendHeartbeatNow, _context,
            data: JsonConvert.SerializeObject(new { count }));
        return new OkObjectResult(new CountResponse { Count = count });
    }

    public async Task<ActionResult<IEnumerable<HeartbeatOverviewRow>>> GetAdminHeartbeatOverviewAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var rows = await _heartbeatService.GetOverviewAsync();
        return new OkObjectResult(rows.Select(Mapper.HeartbeatOverviewRowFromItem).ToList());
    }

    public async Task<IActionResult> PutAdminHeartbeatEndpointEnabledAsync(HeartbeatEndpointEnabledRequest body, string endpointId)
    {
        // Which action this is depends on the body, and the denial branch has to
        // record the attempt, so resolve the audit type before the gate.
        var auditType = body?.Enabled == false
            ? MessageAuditType.DisableEndpointHeartbeat
            : MessageAuditType.EnableEndpointHeartbeat;

        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(auditType, _context,
                accessDenied: true, endpointId: endpointId);
            return new ForbidResult();
        }

        if (body is null)
            return new BadRequestObjectResult("An enabled flag is required.");

        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        await _heartbeatService.SetEndpointEnabledAsync(endpointId, body.Enabled);
        await _auditLogService.LogAuditAsync(auditType, _context, endpointId: endpointId);
        return new OkResult();
    }

    public async Task<ActionResult<IEnumerable<ServiceHealthRow>>> GetAdminHealthServicesAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var rows = await _heartbeatService.GetServiceHealthAsync();
        return new OkObjectResult(rows.Select(Mapper.ServiceHealthRowFromState).ToList());
    }

    // Every /api/admin/* operation is cross-endpoint and destructive-capable, so
    // the gate is the site Owner role (spec 026; the EIP_Management marker claim
    // still maps to site Owner via the authorization service's compat union).
    private Task<bool> IsSiteOwnerAsync() => _authorizationService.HasRoleAsync(AccessRole.Owner);
}
