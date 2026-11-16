# Plan: Spec 052, agent API keys on `/mcp`

Spec: [docs/spec/052-mcp-agent-api-keys/spec.md](../spec/052-mcp-agent-api-keys/spec.md). Issue #409.
Baseline: master `0b41b1e2` (after v4.20.0). Approved 2026-11-16 with every recommendation in §15.

## Decisions taken from §15

1. Deployment ceiling `NimBus:Mcp:AgentKeys:Enabled`, default false. Required.
2. `nbak_` for both uses. The use is enforced on the server.
3. No keys-only `/mcp` mode. Keys need Entra mode.
4. No per-key IP allowlist.
5. The journal operation does not name the key; the command's audit row does.
6. The 5-minute staleness bound applies to MCP keys only. `/api/agent/*` keeps Spec 039 behaviour (follow-up).
7. MCP keys expire within 365 days, 90 by default in the UI.

## §16 facts, verified

1. **The default scheme on `/mcp` with `Bearer nbak_…`.** In Entra-only and dual sign-in the `Az`
   policy scheme (`Startup.Security.cs`) forwards any `Authorization: Bearer` to the WebApp's
   JwtBearer. `UseAuthentication` (`Startup.Pipeline.cs`) runs it on every request; a key is not a
   JWT, so the handler fails and `HttpContext.User` stays anonymous, with only JwtBearer's log
   line. MCP JWTs already take this path today (wrong audience). Holds.
2. **`HttpContext.User` before the rate limiter.** `UseAuthorization` runs the endpoint policy, whose
   `PolicyEvaluator.AuthenticateAsync` authenticates with the schemes the policy names and assigns
   `HttpContext.User`. `UseNimBusRateLimitCallers` and `UseRateLimiter` come after it, so
   `McpPartitionKey` sees the key principal when the policy names the `NimBusMcpAuth` policy
   scheme. `RateLimitCallers` then skips the request because the user is authenticated, so the
   Spec 039 scheme never sees an MCP key there (it would log a refusal). Holds.
3. **Authentication types.** JwtBearer builds its identity with
   `TokenValidationParameters.AuthenticationType` (default `AuthenticationTypes.Federation`); the
   MCP bearer does not change it. It can never be `NimBusMcpAgentKey`. Holds.
4. **Audit writers an MCP workload reaches**, and where `agentKeyId` goes:
   - `OperatorCommandCoordinator`: `Describe` (resubmit, skip) and the report row. Through
     `OperatorCommandContext.AgentKeyId`.
   - `McpAccessRefusals`: the refusal row and the deduplication key.
   - `FailureClassificationRecorder`: the `FailureClassified` row. Through
     `FailureClassificationRecording.AgentKeyId`.
   - `OperatorActionTools.AuditRecordingDeniedAsync`: the denied recording row.
   - `OperatorRecoveryCommands` and the edit path are delegated-only; a key never reaches them.
     They get the field anyway through the context, at no cost.
5. **Action tokens across sibling keys.** `OperatorActionTokens.Identify` binds a token to caller,
   tenant and client. For a key it returns `agent:<agentId>`, no tenant, and client
   `agent:<agentId>`, so a token prepared under one key is accepted under a sibling key of the same
   agent. Intended; pinned by a test.
6. **Azure SRE Agent sends the bearer value verbatim and no `Origin`.** Not verifiable in CI; part of
   the §11 pilot.
7. **An omitted `allowAgentKeys` reads as false.** NSwag generates a non-nullable `bool` for an
   optional boolean without a default, so an older page that omits it saves false. Pinned by an
   Admin API test.

## Implementation notes

- `McpAgentKeyDefaults` (`Services/AgentApiKeys/`) holds the scheme names, claim types and the one
  helper that reads an agent key identity: `TryRead(ClaimsPrincipal)` returns the agent id, key id,
  permissions, request limit and expiry only from an identity whose authentication type is
  `NimBusMcpAgentKey`. Every consumer (`McpCaller`, `McpOperatorPermissions`,
  `EndpointAuthorizationService`, `OperatorActionTokens`, the rate-limit partition, capabilities)
  goes through it.
- `IAgentApiKeyProvider.LastReadAtUtc` exposes the last successful read for the staleness bound.
- The `AgentApiKey` scheme refuses `Mcp` keys and the MCP key scheme refuses `AgentApi` keys, both
  logged as an unknown key.
- `EndpointAuthorizationService`: for a key identity the identifiers are only
  `agent:<agentId>`; Owner entries are ignored (they are refused on save anyway), the role is capped
  at Contributor, `RoleAssignments` and the `groups` compat grant are skipped, and `IsPiiReader` is
  false. `BypassEndpointAuthorization` (Development only) still applies.
- Budget per agent comes for free from `Identify` returning `agent:<agentId>` as the client.

## Commits

As in spec §12, one PR, no stacked PRs:

1. `feat(store): key use, MCP grant and the agent-keys policy switch`
2. `feat(webapp): authenticate MCP agent keys on /mcp`
3. `feat(webapp): govern MCP agent keys as workloads`
4. `feat(webapp): issue, rotate and grant MCP agent keys`
5. `feat(webapp): Settings and Access Control for MCP agent keys`
6. `docs: agent API keys on /mcp`
