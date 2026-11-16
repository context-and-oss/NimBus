using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using Api = NimBus.WebApp.ManagementApi;
using Store = NimBus.MessageStore.States;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Settings → MCP access API (Spec 037). Site Owner only, like every other <c>/api/admin/*</c>
/// operation. Writes are always audited; their <c>X-NimBus-CSRF</c> antiforgery token is checked
/// by <c>[AutoValidateAntiforgeryToken]</c> on the generated controller before they get here.
/// </summary>
public class McpAccessImplementation : Api.IMcpAccessApiController
{
    /// <summary>The longest period the activity summary covers.</summary>
    public const int MaxActivityHours = 168;

    private readonly HttpContext _context;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IAntiforgery _antiforgery;
    private readonly McpAccessSettingsService _settings;
    private readonly McpActivityService _activity;
    private readonly McpOperatorRuntime _runtime;
    private readonly RateLimitOptions _rateLimits;
    private readonly IMcpAccessPolicyProvider? _policies;
    private readonly bool _failureIntelligenceEnabled;

    /// <summary>Creates the controller implementation.</summary>
    public McpAccessImplementation(
        IHttpContextAccessor contextAccessor,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IAntiforgery antiforgery,
        McpAccessSettingsService settings,
        McpActivityService activity,
        McpOperatorRuntime runtime,
        IOptions<RateLimitOptions> rateLimits,
        IEnumerable<IMcpAccessPolicyProvider> policies,
        IEnumerable<IntegrationIntelligenceActivation> intelligence)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        ArgumentNullException.ThrowIfNull(rateLimits);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(intelligence);
        _context = contextAccessor.HttpContext!;
        _authorization = authorization;
        _audit = audit;
        _antiforgery = antiforgery;
        _settings = settings;
        _activity = activity;
        _runtime = runtime;
        _rateLimits = rateLimits.Value;
        _policies = policies.FirstOrDefault();
        _failureIntelligenceEnabled = intelligence.Any(a => a.Enabled);
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.McpAccessState>> GetAdminMcpSettingsAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        return new OkObjectResult(await StateAsync(await _settings.GetAsync()));
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.McpAccessState>> PutAdminMcpSettingsAsync(Api.McpAccessUpdate body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.UpdateMcpSettings, _context, accessDenied: true);
            return new ForbidResult();
        }

        if (body?.Settings is null)
            return Problem(400, "Invalid", ["A settings body is required."]);

        var result = await _settings.SaveAsync(McpAccessApiMapper.FromApi(body.Settings), body.Revision, body.ConfirmWidening, _authorization.GetCurrentUserName());
        switch (result.Status)
        {
            case McpAccessSaveStatus.Invalid:
                return Problem(400, "Invalid", result.Errors);
            case McpAccessSaveStatus.ConfirmationRequired:
                return Problem(400, "ConfirmationRequired", ["This change lets agents do more. Confirm it to save."], result.Changes);
            case McpAccessSaveStatus.Conflict:
                return Problem(409, "RevisionConflict", ["Another administrator changed these settings. Reload them before reviewing again."]);
        }

        await AuditSaveAsync(result, turnOff: false);
        return new OkObjectResult(await StateAsync(result.Saved));
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.McpAccessState>> PostAdminMcpTurnOffAsync()
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.UpdateMcpSettings, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { turnOff = true }));
            return new ForbidResult();
        }


        var result = await _settings.TurnOffAsync(_authorization.GetCurrentUserName());
        if (result.Status != McpAccessSaveStatus.Saved)
            return Problem(409, "RevisionConflict", ["The settings kept changing while turning MCP access off. Try again."]);

        await AuditSaveAsync(result, turnOff: true);
        return new OkObjectResult(await StateAsync(result.Saved));
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.McpActivity>> GetAdminMcpActivityAsync(int hours)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        var activity = await _activity.GetAsync(Math.Clamp(hours, 1, MaxActivityHours));
        return new OkObjectResult(McpAccessApiMapper.ToApi(activity));
    }

    private async Task<Api.McpAccessState> StateAsync(Store.McpAccessSettings saved)
    {
        var effective = _policies is null ? null : await _policies.GetAsync();
        return new Api.McpAccessState
        {
            Saved = McpAccessApiMapper.ToApi(saved),
            Effective = effective is null ? null : McpAccessApiMapper.ToApi(effective.Settings),
            PolicyLoaded = _policies is null || effective is not null,
            Deployment = Deployment(),
            FailureIntelligenceEnabled = _failureIntelligenceEnabled,
            CsrfToken = _antiforgery.GetAndStoreTokens(_context).RequestToken ?? string.Empty,
        };
    }

    private Api.McpDeployment Deployment()
    {
        var request = _context.Request;
        var origin = $"{request.Scheme}://{request.Host}{request.PathBase}";
        var entra = _runtime.Options.Entra;
        var isEntra = _runtime.Mode == McpAuthenticationMode.Entra;
        return new Api.McpDeployment
        {
            Mode = _runtime.Mode switch
            {
                McpAuthenticationMode.LocalDevelopment => Api.McpDeploymentMode.LocalDevelopment,
                McpAuthenticationMode.Entra => Api.McpDeploymentMode.Entra,
                _ => Api.McpDeploymentMode.Disabled,
            },
            EndpointUrl = origin + McpOperatorOptions.Path,
            ResourceMetadataUrl = isEntra ? $"{origin}/.well-known/oauth-protected-resource{McpOperatorOptions.Path}" : null,
            ServerVersion = McpOperatorServiceCollectionExtensions.ServerVersion(),
            TenantId = entra.TenantId,
            ClientId = entra.ClientId,
            ApplicationIdUri = entra.IsConfigured ? entra.ResolvedApplicationIdUri : null,
            Authority = entra.IsConfigured ? entra.Authority : null,
            AllowedOrigins = _runtime.Options.AllowedOrigins.ToList(),
            RateLimitsEnabled = _rateLimits.Enabled,
            // Keys are accepted only in Entra mode with the deployment's ceiling on (Spec 052 §6.5).
            AgentKeysEnabled = isEntra && _runtime.Options.AgentKeys.Enabled,
            RequestLimit = new Api.McpWindowLimit { Permit = _rateLimits.Mcp.PermitLimit, WindowSeconds = _rateLimits.Mcp.WindowSeconds },
            MutationLimit = new Api.McpWindowLimit { Permit = _rateLimits.McpMutations.PermitLimit, WindowSeconds = _rateLimits.McpMutations.WindowSeconds },
            Endpoints = _settings.CatalogEndpoints.ToList(),
        };
    }

    private Task AuditSaveAsync(McpAccessSaveResult result, bool turnOff)
        => _audit.LogAuditAsync(MessageAuditType.UpdateMcpSettings, _context, data: JsonConvert.SerializeObject(new
        {
            revision = result.Saved.Revision,
            previousRevision = result.PreviousRevision,
            turnOff,
            changes = result.Changes.Select(c => new { text = c.Text, widens = c.Widens }),
        }));

    private static ObjectResult Problem(int status, string code, IEnumerable<string> errors, IEnumerable<McpAccessChange>? changes = null)
        => new(new Api.McpAccessProblem
        {
            Code = code,
            Errors = errors.ToList(),
            Changes = (changes ?? []).Select(c => new Api.McpAccessChange { Text = c.Text, Widens = c.Widens }).ToList(),
        })
        { StatusCode = status };

    private Task<bool> IsSiteOwnerAsync() => _authorization.HasRoleAsync(AccessRole.Owner);
}
