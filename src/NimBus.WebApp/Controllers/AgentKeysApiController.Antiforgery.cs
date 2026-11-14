using Microsoft.AspNetCore.Mvc;

namespace NimBus.WebApp.ManagementApi;

/// <summary>
/// Settings → Agent keys (Spec 039). The NSwag-generated controller is partial, so the antiforgery
/// requirement is declared here: every unsafe request (issue, revoke, remove) must carry the
/// <c>X-NimBus-CSRF</c> token that <c>GET /api/admin/agent-keys</c> returns; GET is exempt.
/// </summary>
[AutoValidateAntiforgeryToken]
public partial class AgentKeysApiController;
