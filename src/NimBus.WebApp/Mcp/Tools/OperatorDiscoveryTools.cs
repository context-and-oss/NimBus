using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NimBus.Core;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Mcp.Tools;

/// <summary>
/// Read-only tools that tell an agent where it is and what it may look at. Every result is
/// filtered to the endpoints the caller holds Reader on, using the same authorization as the
/// REST API.
/// </summary>
[McpServerToolType]
public sealed class OperatorDiscoveryTools
{
    private readonly IPlatform _platform;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly OperatorEndpointCatalog _catalog;
    private readonly McpOperatorRuntime _runtime;
    private readonly OperatorPayloadAccess _payloadAccess;
    private readonly OperatorActionAccess _actionAccess;
    private readonly RateLimitOptions _rateLimits;
    private readonly IMcpAccessPolicyProvider _policies;
    private readonly bool _classificationEnabled;
    private readonly IOperatorClassificationSource _classifications;

    /// <summary>Creates the tools for one request.</summary>
    public OperatorDiscoveryTools(
        IPlatform platform,
        IEndpointAuthorizationService authorization,
        OperatorEndpointCatalog catalog,
        McpOperatorRuntime runtime,
        OperatorPayloadAccess payloadAccess,
        OperatorActionAccess actionAccess,
        IOptions<RateLimitOptions> rateLimits,
        IMcpAccessPolicyProvider policies,
        IEnumerable<IntegrationIntelligenceActivation> intelligence,
        IOperatorClassificationSource classifications)
    {
        _classifications = classifications;
        _policies = policies;
        _platform = platform;
        _authorization = authorization;
        _catalog = catalog;
        _runtime = runtime;
        _payloadAccess = payloadAccess;
        _actionAccess = actionAccess;
        _rateLimits = rateLimits.Value;
        _classificationEnabled = intelligence.Any(activation => activation.Enabled);
    }

    /// <summary>Describes the deployment, the caller and what the caller may do.</summary>
    [McpServerTool(Name = "nimbus_get_capabilities", Title = "NimBus capabilities", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Call this first. Returns the NimBus environment this server belongs to, who you are, the endpoints you can read, the actions you may take, request limits and optional features. Contains no secrets or platform configuration.")]
    public async Task<CapabilitiesResult> GetCapabilitiesAsync()
    {
        var access = await _authorization.GetCurrentUserAccessAsync().ConfigureAwait(false);
        var limits = _rateLimits.Enabled
            ? new RequestLimits(_policies.Current?.RequestLimit ?? _rateLimits.Mcp.PermitLimit, _rateLimits.Mcp.WindowSeconds)
            : null;

        return new CapabilitiesResult(
            _catalog.Environment,
            DateTimeOffset.UtcNow,
            new CallerInfo(_authorization.GetCurrentUserName(), access.ObjectId, access.SiteRole.ToString(), await _payloadAccess.CanReadPayloadsAsync().ConfigureAwait(false)),
            _runtime.Mode.ToString(),
            await _catalog.GetReadableEndpointIdsAsync().ConfigureAwait(false),
            await _actionAccess.PermittedActionsAsync(_classifications.CanRecord || _classifications.CanAnalyze).ConfigureAwait(false),
            limits,
            new FeatureInfo(_classificationEnabled, _classifications.CanRecord, _classifications.CanAnalyze));
    }

    /// <summary>Lists the endpoints the caller can read, with the event types each produces and consumes.</summary>
    [McpServerTool(Name = "nimbus_list_endpoints", Title = "List NimBus endpoints", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists the NimBus endpoints you can read, with their description and the event type ids each endpoint produces and consumes. Endpoint ids are the identifiers other NimBus tools take.")]
    public async Task<EndpointListResult> ListEndpointsAsync()
    {
        var readable = (await _catalog.GetReadableEndpointIdsAsync().ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var endpoints = _platform.Endpoints
            .Where(endpoint => readable.Contains(endpoint.Id))
            .OrderBy(endpoint => endpoint.Id, StringComparer.OrdinalIgnoreCase)
            .Select(endpoint => new EndpointSummary(
                endpoint.Id,
                endpoint.Name,
                endpoint.Description,
                endpoint.EventTypesProduced.Select(eventType => eventType.Id).Order(StringComparer.Ordinal).ToList(),
                endpoint.EventTypesConsumed.Select(eventType => eventType.Id).Order(StringComparer.Ordinal).ToList()))
            .ToList();

        return new EndpointListResult(_catalog.Environment, DateTimeOffset.UtcNow, endpoints);
    }
}

/// <summary>Result of <c>nimbus_get_capabilities</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Caller">The authenticated caller.</param>
/// <param name="AuthenticationMode"><c>Entra</c> or <c>LocalDevelopment</c>.</param>
/// <param name="ReadableEndpoints">Endpoint ids the caller holds Reader on.</param>
/// <param name="PermittedActions">Actions the caller may take through MCP on at least one endpoint: resubmit, skip, report, classify (record a classification). nimbus_get_message lists what applies to one message.</param>
/// <param name="Limits">The MCP request limit, or null when rate limiting is off.</param>
/// <param name="Features">Optional features available in this deployment.</param>
public sealed record CapabilitiesResult(
    string? Environment,
    DateTimeOffset AsOfUtc,
    CallerInfo Caller,
    string AuthenticationMode,
    IReadOnlyList<string> ReadableEndpoints,
    IReadOnlyList<string> PermittedActions,
    RequestLimits? Limits,
    FeatureInfo Features);

/// <summary>The authenticated caller.</summary>
/// <param name="Name">Display name, when known.</param>
/// <param name="ObjectId">Entra object id, when the caller is an Entra principal.</param>
/// <param name="SiteRole">Site-wide role: None, Reader, Contributor or Owner.</param>
/// <param name="CanReadPayloads">Whether nimbus_get_message may return payloads to this caller: PiiReader and, for Entra, the nimbus.payload.read scope.</param>
public sealed record CallerInfo(string? Name, string? ObjectId, string SiteRole, bool CanReadPayloads);

/// <summary>A fixed-window request limit.</summary>
/// <param name="RequestsPerWindow">Requests admitted per window.</param>
/// <param name="WindowSeconds">Window length in seconds.</param>
public sealed record RequestLimits(int RequestsPerWindow, int WindowSeconds);

/// <summary>Optional features.</summary>
/// <param name="FailureClassification">Whether failure classification is enabled.</param>
/// <param name="CanRecordClassifications">Whether agents can read evidence with nimbus_get_failure_evidence and record classifications with nimbus_record_classification.</param>
/// <param name="CanAnalyzeWithProvider">Whether the deprecated built-in provider can analyze a failure with nimbus_classify_failure.</param>
public sealed record FeatureInfo(bool FailureClassification, bool CanRecordClassifications, bool CanAnalyzeWithProvider);

/// <summary>Result of <c>nimbus_list_endpoints</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Endpoints">Readable endpoints, ordered by id.</param>
public sealed record EndpointListResult(string? Environment, DateTimeOffset AsOfUtc, IReadOnlyList<EndpointSummary> Endpoints);

/// <summary>One catalog endpoint.</summary>
/// <param name="Id">Endpoint id.</param>
/// <param name="Name">Endpoint name.</param>
/// <param name="Description">Description, when the catalog provides one.</param>
/// <param name="EventTypesProduced">Event type ids the endpoint publishes.</param>
/// <param name="EventTypesConsumed">Event type ids the endpoint subscribes to.</param>
public sealed record EndpointSummary(
    string Id,
    string Name,
    string? Description,
    IReadOnlyList<string> EventTypesProduced,
    IReadOnlyList<string> EventTypesConsumed);
