using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using AccessRole = NimBus.WebApp.Services.AccessRole;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Site- and endpoint-scoped role management (spec 026). Mutations are
/// read-modify-write against <see cref="IAccessControlStore"/>, audited as
/// GrantRole/RevokeRole, and bump the snapshot cache so grants take effect on
/// the next request of this instance.
/// </summary>
public partial class AccessControlImplementation : IAccessControlApiController
{
    private readonly IAccessControlStore _store;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly IAccessControlSnapshotProvider _snapshotProvider;
    private readonly IAuditLogService _auditLogService;
    private readonly IPlatform _platform;
    private readonly HttpContext _context;

    public AccessControlImplementation(
        IAccessControlStore store,
        IEndpointAuthorizationService authorizationService,
        IAccessControlSnapshotProvider snapshotProvider,
        IAuditLogService auditLogService,
        IPlatform platform,
        IHttpContextAccessor contextAccessor)
    {
        _store = store;
        _authorizationService = authorizationService;
        _snapshotProvider = snapshotProvider;
        _auditLogService = auditLogService;
        _platform = platform;
        _context = contextAccessor.HttpContext!;
    }

    public async Task<ActionResult<AccessControlSet>> GetAccessControlAsync()
    {
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner))
            return new ForbidResult();

        var acl = await _store.GetSiteAccessControl() ?? new AccessControlList();
        return new OkObjectResult(ToSet(acl));
    }

    public Task<ActionResult<AccessControlSet>> PostAccessControlRoleAsync(RoleEntry body)
        => MutateSiteAsync(body, grant: true);

    public Task<ActionResult<AccessControlSet>> DeleteAccessControlRoleAsync(RoleEntry body)
        => MutateSiteAsync(body, grant: false);

    public async Task<ActionResult<CurrentUserAccessInfo>> GetAccessControlMeAsync()
    {
        var access = await _authorizationService.GetCurrentUserAccessAsync();
        return new OkObjectResult(new CurrentUserAccessInfo
        {
            Name = _authorizationService.GetCurrentUserName(),
            Email = access.Email,
            SiteRole = (CurrentUserAccessInfoSiteRole)(int)access.SiteRole,
            IsPiiReader = access.IsPiiReader,
            CanManageAccessControl = access.SiteRole == AccessRole.Owner,
            EndpointRoles = access.EndpointRoles
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new EndpointRoleInfo
                {
                    EndpointId = kvp.Key,
                    Role = (EndpointRoleInfoRole)(int)kvp.Value,
                })
                .ToList(),
        });
    }

    public async Task<ActionResult<AccessControlSet>> GetEndpointAccessControlAsync(string endpointId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
            return new ForbidResult();

        var acl = await _store.GetEndpointAccessControl(endpointId) ?? new AccessControlList();
        return new OkObjectResult(ToSet(acl));
    }

    public Task<ActionResult<AccessControlSet>> PostEndpointAccessControlRoleAsync(RoleEntry body, string endpointId)
        => MutateEndpointAsync(body, endpointId, grant: true);

    public Task<ActionResult<AccessControlSet>> DeleteEndpointAccessControlRoleAsync(RoleEntry body, string endpointId)
        => MutateEndpointAsync(body, endpointId, grant: false);

    /// <summary>
    /// Spec 052 §6.7: an <c>agent:&lt;agentId&gt;</c> entry names an MCP agent key's agent. It must
    /// name a valid agent id, and may be a Reader or Contributor but never an Owner or PiiReader.
    /// Other entries are not newly validated. Null when <paramref name="entry"/> is acceptable.
    /// </summary>
    private static string? AgentEntryProblem(RoleEntryRole role, string entry)
    {
        if (!entry.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!AgentEntryPattern().IsMatch(entry))
            return "An agent entry is agent:<agentId>, where the id is 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit.";
        if (role is RoleEntryRole.Owner or RoleEntryRole.PiiReader)
            return "An agent may be a reader or contributor, never an owner or PII reader.";
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^agent:[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex AgentEntryPattern();

    private async Task<ActionResult<AccessControlSet>> MutateSiteAsync(RoleEntry body, bool grant)
    {
        var auditType = grant ? MessageAuditType.GrantRole : MessageAuditType.RevokeRole;
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner))
        {
            await _auditLogService.LogAuditAsync(auditType, _context,
                accessDenied: true, data: AuditData("site", body));
            return new ForbidResult();
        }

        var entry = body?.Entry?.Trim();
        if (string.IsNullOrEmpty(entry))
            return new BadRequestObjectResult("An email address or object id entry is required.");
        if (grant && AgentEntryProblem(body!.Role, entry) is { } agentProblem)
            return new BadRequestObjectResult(agentProblem);
        if (grant && body!.Label?.Trim().Length > MaxLabelLength)
            return new BadRequestObjectResult($"A name is at most {MaxLabelLength} characters.");

        var acl = await _store.GetSiteAccessControl() ?? new AccessControlList();
        var list = SelectList(acl, body!.Role);
        Apply(list, entry, grant);
        ApplyLabel(acl, entry, grant ? body.Label : null);
        acl.UpdatedAtUtc = DateTime.UtcNow;
        await _store.SetSiteAccessControl(acl);

        _snapshotProvider.Invalidate();
        await _auditLogService.LogAuditAsync(auditType, _context, data: AuditData("site", body));
        return new OkObjectResult(ToSet(acl));
    }

    private async Task<ActionResult<AccessControlSet>> MutateEndpointAsync(RoleEntry body, string endpointId, bool grant)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var auditType = grant ? MessageAuditType.GrantRole : MessageAuditType.RevokeRole;
        if (!await _authorizationService.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            await _auditLogService.LogAuditAsync(auditType, _context,
                accessDenied: true, endpointId: endpointId, data: AuditData(endpointId, body));
            return new ForbidResult();
        }

        // PiiReader is site-scoped only (DIS parity): payload reveal is a
        // platform capability, never something an endpoint Owner can self-grant.
        if (body?.Role == RoleEntryRole.PiiReader)
            return new BadRequestObjectResult("The piiReader role is site-scoped; grant it on the site access-control lists.");

        var entry = body?.Entry?.Trim();
        if (string.IsNullOrEmpty(entry))
            return new BadRequestObjectResult("An email address or object id entry is required.");
        if (grant && AgentEntryProblem(body!.Role, entry) is { } agentProblem)
            return new BadRequestObjectResult(agentProblem);
        if (grant && body!.Label?.Trim().Length > MaxLabelLength)
            return new BadRequestObjectResult($"A name is at most {MaxLabelLength} characters.");

        var acl = await _store.GetEndpointAccessControl(endpointId) ?? new AccessControlList();
        var list = SelectList(acl, body!.Role);
        Apply(list, entry, grant);
        ApplyLabel(acl, entry, grant ? body.Label : null);
        acl.UpdatedAtUtc = DateTime.UtcNow;
        await _store.SetEndpointAccessControl(endpointId, acl);

        _snapshotProvider.Invalidate();
        await _auditLogService.LogAuditAsync(auditType, _context,
            endpointId: endpointId, data: AuditData(endpointId, body));
        return new OkObjectResult(ToSet(acl));
    }

    private static void Apply(List<string> list, string entry, bool grant)
    {
        // Re-normalize on grant: drop every case/whitespace-equivalent entry
        // (padded entries are legal stored shapes and live grants) so the list
        // ends with exactly one canonical trimmed grant.
        list.RemoveAll(e => string.Equals(e.Trim(), entry, StringComparison.OrdinalIgnoreCase));
        if (grant)
            list.Add(entry);
    }

    private const int MaxLabelLength = 100;

    /// <summary>
    /// Names belong to the entry, not one role: a null <paramref name="label"/> keeps the current
    /// name, a blank one removes it. A name whose entry no longer holds any role is dropped.
    /// </summary>
    private static void ApplyLabel(AccessControlList acl, string entry, string? label)
    {
        acl.Labels ??= new Dictionary<string, string>();
        var key = LabelKey(entry);
        if (label != null)
        {
            if (string.IsNullOrWhiteSpace(label))
                acl.Labels.Remove(key);
            else
                acl.Labels[key] = label.Trim();
        }

        var granted = new[] { acl.Readers, acl.Contributors, acl.Owners, acl.PiiReaders }
            .Any(l => l?.Any(e => LabelKey(e) == key) == true);
        if (!granted)
            acl.Labels.Remove(key);
    }

    private static string LabelKey(string entry) => entry.Trim().ToLowerInvariant();

    private static List<string> SelectList(AccessControlList acl, RoleEntryRole role) => role switch
    {
        RoleEntryRole.Reader => acl.Readers,
        RoleEntryRole.Contributor => acl.Contributors,
        RoleEntryRole.Owner => acl.Owners,
        RoleEntryRole.PiiReader => acl.PiiReaders,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown access role."),
    };

    private static string AuditData(string scope, RoleEntry body)
        => JsonConvert.SerializeObject(new { scope, role = body?.Role.ToString(), entry = body?.Entry, label = body?.Label });

    private static AccessControlSet ToSet(AccessControlList acl) => new AccessControlSet
    {
        Readers = acl.Readers ?? new List<string>(),
        Contributors = acl.Contributors ?? new List<string>(),
        Owners = acl.Owners ?? new List<string>(),
        PiiReaders = acl.PiiReaders ?? new List<string>(),
        Labels = acl.Labels ?? new Dictionary<string, string>(),
    };
}
