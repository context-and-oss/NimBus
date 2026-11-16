# Spec 052 — Agent API keys on `/mcp`

Status: approved (2026-11-16) with every recommendation in §15: the deployment ceiling is
required and off by default, both uses keep `nbak_`, no keys-only `/mcp`, no IP allowlist in v1,
the journal does not name the key, the staleness bound for `/api/agent/*` is a follow-up, and MCP
keys expire within 365 days. Plan: [docs/plan/2026-11-16-spec-052-mcp-agent-api-keys.md](../../plan/2026-11-16-spec-052-mcp-agent-api-keys.md).
Baseline: master `7068c38c`; the latest release is v4.18.0. Issue: context-and-oss/NimBus#409.
Builds on [Spec 035](../035-mcp-operator-access/spec.md) (operator MCP),
[Spec 037](../037-mcp-admin-access/spec.md) (MCP access policy),
[Spec 039](../039-agent-api-keys/spec.md) (agent API keys) and
[Spec 044](../044-agent-first/spec.md) (workload classify, resubmit and annotate).
Scope: `NimBus.WebApp` (the `/mcp` authentication, `Mcp/Access/`, `Mcp/Operations/`,
`Services/AgentApiKeys/`, `EndpointAuthorizationService`, `RateLimiting/`, `api-spec.yaml`, the
Settings and Access Control pages), `NimBus.MessageStore.Abstractions` (fields on two existing
singleton records), the conformance suite, and docs. Unchanged: the Entra bearer scheme, the MCP
tool contracts apart from three additive `nimbus_get_capabilities` fields, the operation journal's
schema, and `/api/agent/*`.

Let `/mcp` accept a NimBus agent API key and treat its caller as an unattended **workload**,
governed exactly like an app-only Entra token. A key never skips a message and never reads a
payload.

## 1. Problem

`/mcp` accepts only Entra access tokens issued for the MCP resource, or, in local development, the
bypass. Some hosted agents can send only a fixed secret. Azure SRE Agent's MCP connector, for
example, offers "Bearer token: Personal access token (PAT) or API key" and "custom headers". An
Entra access token pasted there expires after about an hour.

The Entra options that do work need setup that not every customer can do:

- OAuth with a pre-registered client acts as the person who signed in, for every run of the agent.
- A managed identity needs an app role assignment on the MCP resource. That became possible only
  with #407, and it needs a tenant administrator.

## 2. Current state

Facts at the baseline.

- **MCP authentication.** `AddNimBusOperatorMcp` registers the `NimBusMcp` JwtBearer scheme and the
  `NimBusMcpChallenge` scheme only in `McpAuthenticationMode.Entra`
  (`src/NimBus.WebApp/Mcp/McpOperatorServiceCollectionExtensions.cs`). The `NimBusMcpObserve`
  policy names only `NimBusMcp`, requires `McpOperatorPermissions.CanObserve` (the
  `nimbus.observe` scope or the `Nimbus.Workload.Observe` app role) and adds
  `McpAccessRequirement`. In local development it names only the `LocalDev` scheme. The mode is
  chosen once at startup by `McpAuthenticationModeResolver`, and Entra mode needs
  `NimBus:Mcp:Entra` (`Mcp/McpAuthenticationMode.cs`).
- **Policy schemes.** In Entra-only and dual sign-in, the `Az` policy scheme forwards every request
  whose `Authorization` header starts with `Bearer` to the WebApp's own JwtBearer
  (`Startup.Security.cs`). That is why Spec 039 chose a custom header. `UseAuthentication` runs that
  default scheme on every request, `/mcp` included; the `/mcp` endpoint policy then authenticates
  again with the schemes it names (`Startup.Pipeline.cs`).
- **Agent keys.** `AgentApiKeyAuthenticationHandler` reads `X-NimBus-Agent-Key`, parses
  `nbak_<keyId>_<secret>` (`AgentApiKeyFormat` over `SecretTokenFormat`), and checks the key
  against `IAgentApiKeyProvider`. The principal has `NameIdentifier = name = agent:<agentId>`,
  `nimbus:agent_id` and `nimbus:agent_key_id`, and no `oid`, `groups`, `scp` or `roles`. Only the
  `AgentApi` policy on `/api/agent/*` names the scheme
  (`Services/AgentApiKeys/AgentApiKeyServiceCollectionExtensions.cs`). `/api/agent/*` returns event
  payloads on receive (`AgentImplementation.cs`, `EventJson`) and publishes events.
- **Key storage.** `AgentApiKeySettings` is one record on `IEndpointMetadataStore`
  (`States/AgentApiKeySettings.cs`): on SQL Server a JSON column in table `AgentApiKeys`
  (`0024_AgentApiKeys.sql`), on Cosmos a fixed-id document in the settings container, in memory in
  `InMemoryMessageStore`. Last use is a separate per-key record. `AgentApiKeyProvider` re-reads the
  record at most every 30 s (5 s after a failure), keeps the last list after a failed read, and is
  replaced at once on the instance that saved a change (`Accept`).
- **Workloads.** `McpCaller.IsWorkload` is true for a principal without `scp`
  (`Mcp/Access/McpCaller.cs`). `OperatorActionAccess.HoldsScope` admits a workload to
  `Classify` (record only), `Report` and `Resubmit` through the app roles `Nimbus.Workload.Classify`,
  `Nimbus.Workload.Annotate` and `Nimbus.Workload.Resubmit`, each with its Owner switch in
  `McpAccessSettings` (`AllowWorkloadClassification`, `AllowWorkloadAnnotation`,
  `AllowWorkloadResubmit`), all effective only while `AllowWorkloads` is on. Skip, the Phase 3
  tools and `nimbus_classify_failure`'s paid analysis need a delegated scope
  (`RequireDelegatedScopeAsync`). Payloads need PiiReader and the delegated `nimbus.payload.read`
  (`OperatorPayloadAccess`).
- **Workload identity.** A workload's NimBus roles come from Access Control grants matched against
  its `oid` (`EndpointAuthorizationService.ResolveIdentifiers`: emails and the object id).
  `OperatorActionTokens.Identify` names the caller by `oid`, `sub`, `NameIdentifier` or name, the
  tenant by `tid` and the client by `azp`, else `appid`. The unattended resubmit budget counts
  journal operations per tenant and client (`OperatorActionTools.RequireWithinWorkloadLimitsAsync`),
  and `OperatorOperationQuery.Matches` treats a null `ClientId` as "any client".
- **Rate limits.** `/mcp` and `OperatorMutationLimiter` partition by
  `McpPartitionKey` = `mcp:{tid}:{azp|appid}:{oid|sub|user}` plus the effective limit
  (`RateLimiting/RateLimitingServiceCollectionExtensions.cs`).
- **Audit.** MCP command rows carry `channel`, `reason`, `idempotencyKey` and `clientId` in `Data`
  (`OperatorCommandCoordinator`). `McpAccessRefusals` writes `McpAccessRefused` with `clientId`,
  deduplicated per tenant, caller id (`oid`/`sub`), client, reason, tool and endpoint.
- **UI.** Settings → Agent keys (`ClientApp/src/components/admin/agent-keys-settings.tsx`) issues
  keys with expiry Never, 30, 90 or 365 days. Settings → MCP access
  (`components/admin/mcp-access-settings.tsx`) has "Unattended workloads (app-only tokens)" and the
  three workload switches with the budget. Access Control (`pages/access-control.tsx`,
  `components/endpoint-list/endpoint-access-modal.tsx`) says entries are email addresses or Entra
  object ids; the API does not validate an entry's form (`AccessControlImplementation.cs`).

So today a key presented on `/mcp` gets a 401: the only scheme the policy names is JwtBearer. And
even if it authenticated, the key principal holds no Observe role, matches no Access Control entry,
shares one resubmit budget with every other key (no `tid`, no client id), shares a rate-limit
partition with the other keys of its agent, and its refusals are deduplicated together with every
other key's.

## 3. Goals

1. A hosted agent that can send only a fixed secret can use `/mcp` as an unattended workload.
2. A key is governed exactly like an app-only token: the read tools, and classify (record),
   annotate or resubmit only through the Owner's workload switches, budget and per-message cap.
3. A key's endpoint access comes from Access Control, granted to the key's agent identity.
4. A deployment opts in, and a site Owner opts in, before any key works on `/mcp`.
5. Every action and refusal of a key caller is audited with the key id.
6. A revoked key, or a switch turned off, stops working on every instance within 30 seconds.
7. Existing deployments and existing keys behave exactly as today.

## 4. Non-goals

- A key that skips messages, reads payloads, uses the Spec 035 Phase 3 tools or runs a paid
  classification analysis. These are hard limits, not defaults (§6.1).
- A key that acts for a person. A person's agent keeps using delegated Entra tokens.
- `/mcp` without an Entra registration (a keys-only MCP mode). See §15, question 3.
- Changing `/api/agent/*`, or letting one key open both `/api/agent/*` and `/mcp`.
- A failed-key rate limiter (as in Spec 039, keys carry 256 bits).
- Per-key source IP allowlists (§15, question 4).
- Entra client credentials for `/api/agent/*` (Spec 039's later `NimBusAgentBearer`).

## 5. Decisions

Each is argued in §6. Alternatives are listed where the decision is made.

| Question from #409 | Recommendation |
|---|---|
| How the key is sent | On `/mcp` only, as `Authorization: Bearer nbak_…` **or** `X-NimBus-Agent-Key: nbak_…`. A policy scheme used only by the MCP policy routes by the `nbak_` prefix, never by JWT shape. Exactly one credential per request (§6.2) |
| Which keys work on MCP | A key is issued for one use, `AgentApi` or `Mcp`, fixed at issue. Same record, format, cache and Settings panel. An MCP key carries its own workload permissions: observe (always), classify, annotate, resubmit (§6.3) |
| Policy | A deployment ceiling `NimBus:Mcp:AgentKeys:Enabled` (default false) and an Owner switch **Agent API keys** next to "Unattended workloads" (default false, effective only while workloads are allowed) (§6.5) |
| Access Control | Grants to the entry `agent:<agentId>`, matched only for a principal authenticated by the MCP key scheme. Reader and Contributor only; never Owner or PiiReader (§6.7) |
| Audit | Every row written for a key caller names `agentKeyId`; the auditor is `agent:<agentId>` (§6.9) |
| Expiry | Required for MCP keys, at most 365 days, 90 by default (§6.10) |
| Rotation | A **Rotate** action issues a successor with the same agent, use and permissions; both work until the old one is revoked or expires (§6.10) |
| Revocation | Within the existing 30 s key cache; plus a 5-minute staleness bound after which MCP refuses every key (§6.10) |
| Rate limits | Per key: the route and change limits partition by key id, and a key can carry a lower request limit. The daily resubmit budget is per agent, so rotating does not refill it (§6.8) |

## 6. Design

### 6.1 Hard limits

A key caller is a workload. These hold whatever the policy, the grant or Access Control say, and
each has a test (§11):

- **No skip.** No key permission maps to `Skip`. `HoldsScope(Skip)` stays false for every workload.
- **No payloads.** `IsPiiReader` is false for a key principal, even with a PiiReader grant (which
  §6.7 refuses), an Entra role or `Authorization:GrantPiiReaderInDevelopment`. Payload access also
  needs the delegated `nimbus.payload.read`, which a key never carries.
- **No delegated-only tools.** `nimbus_settle_handoff`, `nimbus_reprocess_deferred`,
  `nimbus_skip_deferred_tracking`, `nimbus_resubmit_with_changes` and the analysis path of
  `nimbus_classify_failure` keep requiring a delegated scope.
- **No other surface.** An MCP key authenticates on `/mcp` and nowhere else. An `AgentApi` key never
  authenticates on `/mcp`.

### 6.2 How the key is sent

**Recommendation:** accept both forms on `/mcp`:

```
Authorization: Bearer nbak_<keyId>_<secret>
X-NimBus-Agent-Key: nbak_<keyId>_<secret>
```

- The MCP policy names a new policy scheme, `NimBusMcpAuth`, instead of `NimBusMcp`. Its
  `ForwardDefaultSelector`:
  1. `X-NimBus-Agent-Key` present → `NimBusMcpAgentKey`;
  2. `Authorization` is `Bearer` followed by a value starting with `nbak_` → `NimBusMcpAgentKey`;
  3. anything else → `NimBusMcp`, the JwtBearer scheme, unchanged.
- `ForwardChallenge` stays `NimBusMcpChallenge`, so a refused key gets the same 401 with the
  protected-resource metadata as a refused token.
- **One credential per request.** The key handler fails a request that carries the custom header
  and any `Authorization` header, or two different keys. Because the selector forwards to exactly
  one scheme, a key principal and an Entra principal are never merged. Naming both schemes on the
  policy instead would let the policy evaluator merge them.
- The key is never read from the query string or a cookie.
- The `Az` policy schemes are not changed. Outside `/mcp` a `Bearer nbak_…` header still reaches the
  WebApp's JwtBearer and fails, which is correct: an MCP key opens nothing else. On `/mcp`,
  `UseAuthentication` already runs the default scheme on MCP tokens and fails (their audience is the
  MCP resource); a key fails there the same way before the endpoint policy authenticates it (§16,
  fact 1).
- **Why a prefix.** `nbak_` identifies a NimBus key positively, and no JWT can start with it (a
  JWS or JWE starts with the base64url of `{"`, `eyJ`). The key alphabet (base32, base64url and
  `_`) has no `.`, so a three-segment test would also work today, but it identifies keys only by
  what they are not: a JWE has five segments, and an opaque token from another issuer would be
  routed to the key handler.

Rejected:

- **The custom header only.** It works with SRE Agent ("No authentication" plus a custom header), but
  many connectors offer only a bearer or API-key field, and logging tools and proxies redact
  `Authorization` by default but not custom headers. It stays accepted, for parity with
  `/api/agent/*`.
- **JWT shape detection** (three dot-separated segments). See above.
- **Bearer only.** Clients already configured for `/api/agent/*` send the header.
- **Changing the `Az` selectors** to recognise keys everywhere. Not needed, and it would widen where a
  key is even parsed.

### 6.3 Which keys work on MCP

**Recommendation:** a key has a **use**, chosen when it is issued and never changed:

| Use | Authenticates on | Scheme |
|---|---|---|
| `AgentApi` (default; every existing key) | `/api/agent/*` | `AgentApiKey` (Spec 039) |
| `Mcp` | `/mcp` | `NimBusMcpAgentKey` (new) |

- Both uses share the record, the `nbak_` format, `SecretTokenFormat`, `AgentApiKeyProvider`, the
  usage recorder, the management API and the Settings panel. Each scheme refuses a key of the other
  use with the same log line as an unknown key, and the client sees 401.
- An MCP key carries a **grant**: observe (implied), and booleans `Classify`, `Annotate` and
  `Resubmit`, which map to the workload app roles (§6.6), plus an optional `RequestsPerWindow`
  (§6.8). The grant is fixed at issue; a different grant means a rotated key (§6.10). Fixed grants
  keep an issued secret's power what the Owner saw when the key was shown once, and need no new
  audit type.
- The grant is the most a key can do. The Owner's switches in MCP access can only narrow it.

Rejected:

- **An MCP capability on existing keys.** One secret would then open `/api/agent/*` too, whose
  receive returns payloads and whose publish sends events. That breaks the payload limit, and it
  would widen secrets already deployed in agents without the Owner issuing anything new.
- **A separate store, format and panel.** It would copy `SecretTokenFormat`, the provider, the usage
  recorder and the management API for no security gain: the use is enforced on the server. Whether
  MCP keys deserve their own prefix is §15, question 2.

### 6.4 Authentication on `/mcp`

`NimBusMcpAgentKey` is registered only in Entra mode, and only when the deployment ceiling is on
(§6.5). It reuses Spec 039's checks: malformed, unknown, revoked or expired keys fail, failures are
logged with key id and client IP and never the value, and success records last use.

It adds three checks: the key's use is `Mcp`; the request carries one credential (§6.2); and the
key list is fresh enough (§6.10).

The principal:

| Claim | Value |
|---|---|
| `NameIdentifier`, `name` | `agent:<agentId>`, as for Spec 039 keys |
| `nimbus:agent_id`, `nimbus:agent_key_id` | the agent and key ids |
| `nimbus:mcp_permission` | `observe`, and `classify`, `annotate` or `resubmit` per the grant |
| Authentication type | `NimBusMcpAgentKey` |

It has no `scp`, `roles`, `oid`, `tid`, `azp`, `groups` or email claim. So `McpCaller.IsWorkload` is
true without a change, the platform-admin marker can never match, and `EntraAdminClaimsTransformation`
and `NimBusClaimsTransformation` add nothing (the latter already checks the authentication type).

- `McpCaller` gains `AgentId` and `AgentKeyId`. They are read only when the identity's
  authentication type is `NimBusMcpAgentKey`, so an Entra token that happens to carry a
  `nimbus:agent_id` claim is not a key caller.
- `McpOperatorPermissions.CanObserve` and `IsWorkloadWithRole` accept, on a key identity only, the
  `nimbus:mcp_permission` that matches the role: `ObserveRole` ↔ `observe`, `ClassifyRole` ↔
  `classify`, `AnnotateRole` ↔ `annotate`, `ResubmitRole` ↔ `resubmit`. Every other check then works
  unchanged.

Rejected: putting Entra-style `roles` claims on the key principal. It is less code, but a claim
NimBus minted would be indistinguishable from an app role Entra issued to everything that reads
`roles`, now and later.

### 6.5 Admission: ceiling and switch

- **Deployment ceiling:** `NimBus:Mcp:AgentKeys:Enabled` (`NimBus__Mcp__AgentKeys__Enabled`),
  default false. While it is false, the selector never routes to the key scheme and keys on `/mcp`
  get 401. Spec 037 goal 6 says nothing in the WebApp can make NimBus trust more than Entra and the
  deployment allow. A key is a credential NimBus mints itself, so accepting one is a deployment
  decision, like enabling `/mcp`.
- **Owner switch:** `McpAccessSettings.AllowAgentKeys`, labelled **Agent API keys**, below
  "Unattended workloads". Default false, also in a policy saved before the switch existed, like the
  Phase 3 capabilities. Effective only while `AllowWorkloads` is on. Turning it on widens access
  (Spec 037 §6.4) and needs confirmation.
- **Refusal:** `McpAccessPolicy.CheckCaller` returns a new `McpRefusalReason.Key` for a key caller
  while keys are not allowed (the switch is off or `AllowWorkloads` is off). The requirement answers
  403 and writes `McpAccessRefused` with reason `key`.
- **People list:** not applied. A workload has no person, as `CheckCaller` already decides.
- **Client list:** not applied. A key has no Entra client id; the Owner approved the key by issuing
  it, and its grant says what it may change. `ClientMayChange` is true for a key caller. The MCP
  access page says so next to the client list.
- **Turn off now** and `Enabled = false` stop keys too: the guard answers 503 before authentication.
- **Local development:** keys are not accepted. The bypass admits every loopback caller as the
  local developer.

Rejected:

- **The Owner switch alone.** It would let a hijacked Owner session open a credential path that does
  not depend on Entra, which Spec 037 keeps out of the WebApp. Spec 039 lets an Owner mint keys for
  `/api/agent/*`, but those never reach the operator tools.
- **Refusing keys while the client list is Approved.** A pilot that limits Entra clients could then
  never use a key.
- **Listing `agent-key:<id>` as an approved client.** The list holds Entra client GUIDs, and it would
  duplicate the key's own grant.

### 6.6 What a key may do

| Tool | An Entra workload needs | An MCP key needs |
|---|---|---|
| The read tools | `Nimbus.Workload.Observe`, `AllowWorkloads`, Reader on the endpoint (Site Reader for search and metrics) | observe (implied), `AllowWorkloads`, `AllowAgentKeys`, the same grants to `agent:<agentId>` |
| `nimbus_record_classification` | `Nimbus.Workload.Classify`, `AllowWorkloadClassification`, Classify on, Contributor | grant `Classify`, plus the same |
| `nimbus_set_message_reported` | `Nimbus.Workload.Annotate`, `AllowWorkloadAnnotation`, Mark reported on, Contributor | grant `Annotate`, plus the same |
| `nimbus_prepare_action` and `nimbus_resubmit_message` | `Nimbus.Workload.Resubmit`, `AllowWorkloadResubmit`, Resubmit on, the journal, Contributor, the budget and cap | grant `Resubmit`, plus the same |
| Skip, Phase 3 tools, classification analysis, payloads | never | never |

Endpoint visibility and the change-endpoint list (Spec 037 §5.5) apply unchanged. The prompts that
are offered only while the caller may change messages follow the same checks.

### 6.7 Access Control keyed by agent identity

- For a key identity, `EndpointAuthorizationService` matches exactly one identifier,
  `agent:<agentId>`. Emails and object ids are not read from it. The identifier comes from
  `nimbus:agent_id` on a `NimBusMcpAgentKey` identity, never from `name`: an Entra display name can
  be `agent:x`.
- **Granting:** an Owner adds `agent:<agentId>` to Readers or Contributors, site-wide or on an
  endpoint, with the existing Access Control API and pages. The API now validates an entry that
  starts with `agent:` against the agent-id pattern. It refuses such an entry, with 400, on the Owner
  lists and on the PiiReader list. Other entries are not newly validated.
- **Matching** is case-insensitive, as for every Access Control entry, so `agent:Failure-Bot` and
  `agent:failure-bot` are one grantee. The issue form warns when a new agent id differs from an
  existing one only in case.
- **What else stays out:** the compatibility admin role from the `groups` claim, code-defined
  `RoleAssignments` (matched on `oid`) and PiiReader. In Development, `BypassEndpointAuthorization`
  still makes every principal Owner, keys included; it is refused outside Development already, and
  the hard limits in §6.1 still hold.
- **`AgentApi` keys** keep Spec 039's guarantee: they match no entry.
- A grant names the agent, not the key, so it survives rotation. All MCP keys of one agent share its
  grants.

Rejected:

- **Grants keyed by key id.** Rotation would drop them, and the issue asks for the agent identity.
- **Endpoint grants stored on the key.** That would be a second access-control list that endpoint
  Owners cannot see or edit.

### 6.8 Budget, limits and rate partitions

- **Budget per agent.** For a key caller the journal records `ClientId = agent:<agentId>`; the
  tenant stays empty and the caller is `agent:<agentId>`, as `OperatorActionTokens.Identify` already
  returns. `RequireWithinWorkloadLimitsAsync` and the capabilities `unattended` block then count per
  agent. Without this, every key would share one budget, because a null client id means "any client"
  in `OperatorOperationQuery`. Per agent, not per key, so rotating a key does not refill the budget.
  The per-message cap is unchanged: it counts across all agents.
- **Rate partitions per key.** `McpPartitionKey` returns `mcp:agent-key:<keyId>` for a key caller,
  for both the route limit and `OperatorMutationLimiter`. Today it would fall back to the
  `NameIdentifier` and put every key of an agent in one partition. Per key, so a leaked key that is
  being abused does not exhaust its replacement's window during rotation.
- **Per-key request limit.** A grant may set `RequestsPerWindow`, 1 up to the deployment's MCP limit.
  The effective limit is the lowest of the deployment's, the Owner's and the key's, and is part of
  the partition key as today.
- The change limit has no per-key value; the Owner's applies.

### 6.9 Audit

- Rows written for a key caller carry `agentKeyId` in `Data`, and `clientId` is `agent:<agentId>`.
  The auditor is the `name` claim, `agent:<agentId>`. This covers the command rows from
  `OperatorCommandCoordinator`, `McpAccessRefused`, and the rows the classification recorder and the
  report tool write (§16, fact 4).
- `McpAccessRefusals` adds the key id to its deduplication key. Today a key caller has no `oid` or
  `sub`, so the refusals of all keys would be deduplicated as one.
- `GET /api/admin/mcp/activity` returns `agentKeyId` on its items and lists refused keys next to
  refused clients.
- Key lifecycle rows stay `AgentApiKeyCreated`, `AgentApiKeyRevoked` and `AgentApiKeyRemoved`. Their
  `Data` gains `use`, the grant and, for a rotation, `replaces`. No audit type is added.
- The journal operation itself does not name the key (no schema change); its command's audit row
  does. See §15, question 5.

### 6.10 Expiry, rotation and revocation

- **Expiry** is required for MCP keys: at most 365 days after issue, 90 by default in the UI, no
  "Never". `AgentApi` keys are unchanged.
- **Expiring soon:** a key within 14 days of expiry makes the Agent keys feature "Needs attention".
  `nimbus_get_capabilities` returns `caller.credentialExpiresAtUtc`, so an agent can warn its owner.
- **Rotate** (`POST /api/v1/admin/agent-keys/{keyId}/rotate`) issues a successor with the same agent
  id, use and grant, a new expiry (default: the same length as the old key's) and `Replaces =
  <old keyId>`. The old key keeps working until it is revoked or expires. The dialog that shows the
  new key offers to revoke the old one. Both keys share the agent's grants and budget and have their
  own rate partitions. To change a grant, rotate and pick the new permissions.
- **Revocation** applies at once on the instance that saved it (`AgentApiKeyProvider.Accept`) and
  within 30 s on the others (`AgentApiKeyProvider.SuccessTtl`). Turning **Agent API keys** off
  applies within the policy's 30 s (`McpAccessPolicyProvider`). Removal works as today.
- **Staleness bound.** After a failed read the provider keeps its last list with no age limit. A
  revocation saved on one instance then never reaches an instance that cannot read the store. The
  provider exposes when it last read successfully, and the MCP key scheme refuses every key once
  that is more than 5 minutes ago. This fails closed, as the MCP policy provider does before its
  first load. Whether `/api/agent/*` should do the same is §15, question 6.
- A command whose audit row was written before a revocation completes (Spec 037 §11).

### 6.11 Capabilities

`CallerInfo` in `nimbus_get_capabilities` gains `agentId`, `agentKeyId` and
`credentialExpiresAtUtc`, all null for Entra callers. `isWorkload` is true for a key. These are MCP
tool results, not `api-spec.yaml`. The output serializer emits null members
(`ToolSerializerOptions`), so the additions are visible and additive.

## 7. Storage

| Record | Change | SQL Server | Cosmos DB | In-memory |
|---|---|---|---|---|
| `AgentApiKey` (in `AgentApiKeySettings`) | `Use` (`AgentApiKeyUse`: `AgentApi`, `Mcp`; string enum; missing means `AgentApi`), `Mcp` (`AgentApiKeyMcpGrant?`: `Classify`, `Annotate`, `Resubmit`, `RequestsPerWindow?`), `Replaces?` | Inside `SettingsJson` of `AgentApiKeys`; no migration | Inside the existing document | `Clone()` round-trips it |
| `McpAccessSettings` | `AllowAgentKeys` (default false) | Inside `SettingsJson` of `McpAccessSettings` (`0023`); no migration | Inside the existing document | As above |

- No member is added to `IEndpointMetadataStore`, `IMessageTrackingStore` or
  `IOperatorOperationStore`, and `OperatorOperation` is unchanged. So no provider needs new code
  beyond serialization, and `InstrumentingMessageTrackingStoreDecorator`
  (`src/NimBus.OpenTelemetry/Instrumentation/`) needs no change: it implements only
  `IMessageTrackingStore`. If implementation finds a store member is needed after all (for example
  the key id on the journal, §15 question 5), AGENTS.md applies in full: every provider, the
  conformance suite, and forwarding in the decorator when the member is on `IMessageTrackingStore`.
- **Conformance** (`EndpointMetadataStoreConformanceTests`): the agent-key and MCP-access round-trip
  samples gain the new fields, so all three providers prove they keep them. The SQL Server and Cosmos
  runs need `NIMBUS_SQL_TEST_CONNECTION` and the Cosmos variables; CI rejects skipped conformance
  tests.
- **Compatibility tests** (unit, on the abstractions): a stored key without `Use` reads back as
  `AgentApi` with no grant; a stored policy without `AllowAgentKeys` reads back with it false.

## 8. API spec and NSwag

All changes are in `src/NimBus.WebApp/api-spec.yaml`; the controllers and the TypeScript client are
generated (do not edit `Controllers/ApiContract.g.cs` or `ClientApp/src/api-client/index.ts`).

| Schema or operation | Change |
|---|---|
| `AgentApiKeyCreate` | `use` (`agentApi` \| `mcp`, default `agentApi`), `mcp` {`classify`, `annotate`, `resubmit`, `requestsPerWindow?`}. For `mcp`: `expiresAtUtc` required and at most 365 days ahead; `mcp` refused for `agentApi` |
| `AgentApiKeyInfo` | `use`, `mcp`, `replaces` |
| `post-admin-agent-keys-rotate` (new) | `POST /api/v1/admin/agent-keys/{keyId}/rotate`, body `AgentApiKeyRotate` {`name?`, `expiresAtUtc?`}, returns `AgentApiKeyIssued`; 404, 409, and 400 for a revoked or expired key. Owner only, Admin rate limit, antiforgery: the existing `AgentKeysApiController.Antiforgery.cs` partial covers it |
| `post-admin-agent-keys` | The description says where each use authenticates |
| `AgentApiKeyProblem` | Validation messages for the new rules |
| `McpAccessSettings` | `allowAgentKeys`. A client that omits it saves false, so an older page cannot turn keys on |
| `McpAccessState.deployment` | `agentKeysEnabled` (the ceiling) |
| MCP activity items and refused callers | `agentKeyId` |
| `RoleEntry` descriptions | Entries may be `agent:<agentId>`; 400 for such an entry on Owner or PiiReader |

No audit type is added, so the audit-type enums and `AuditTypeContractTests` are unchanged.
`SkipSpaBuild=true` also skips NSwag, so these changes are validated with a build that generates.

## 9. UI

Each change follows the page's existing patterns. The PR includes a screenshot of each changed panel
(AGENTS.md).

**Settings → Agent keys** (`agent-keys-settings.tsx`, `agent-keys-model.ts`):

- The create form asks for the use: **Agent API** (`/api/agent/*`) or **MCP** (`/mcp`, as a workload).
- For MCP: checkboxes for Record classifications, Mark messages reported and Resubmit failed
  messages, each with a line on the Owner switch it also needs. An optional request limit. Expiry
  30, 90 (default) or 365 days, without Never.
- The once-only dialog for an MCP key shows the endpoint URL and both header forms, and says that
  the agent also needs a grant to `agent:<agentId>` in Access Control, with a link.
- The table gains a Use column and the permissions, marks keys expiring within 14 days, and has a
  **Rotate** action. When the ceiling or the Owner switch is off, an MCP key row says it will not
  work yet and why.
- The feature status counts active keys per use, and is "Needs attention" when an MCP key expires
  within 14 days.

**Settings → MCP access** (`mcp-access-settings.tsx`):

- Below "Unattended workloads", a row **Agent API keys**: "Hosted agents may connect with an MCP key
  from Settings → Agent keys. A key acts as a workload: the switches below and the budget apply, and
  a key never skips messages or reads payloads. Off by default." The toggle is disabled with the
  app setting to set when the ceiling is off, while workloads are off, and in local development.
- The client list notes that it does not apply to keys.
- Effective access gains an "Agent key" caller, shown like Workload.
- Connect an agent gains a key option: the URL and the `Authorization: Bearer` form.
- Recent agent activity shows the key id.

**Access Control** (`pages/access-control.tsx`, `endpoint-access-modal.tsx`): the hint says entries
may also be `agent:<agentId>` for an MCP key's agent. The entry field suggests agent ids that have
MCP keys, and the Owner and PiiReader lists refuse `agent:` entries with the server's message.

## 10. Security considerations

| Risk | Mitigation |
|---|---|
| A long-lived secret in a third-party connector leaks | Required expiry of at most a year; rotation without downtime; revocation within 30 s; the key is a workload with Owner-limited permissions, per-agent budget, per-message cap and per-key rate limit; never skip or payloads; every use audited with the key id |
| A key reaches more than `/mcp` | One use per key, fixed at issue; each scheme refuses the other use; only the MCP policy names the MCP key scheme; the `Az` selectors are unchanged |
| A key and a token merge into one principal | The selector forwards to one scheme; a request with two credentials fails |
| An Entra token poses as a key, or a key as a token | Routing by the `nbak_` prefix, which no JWT starts with; key claims and the `agent:` identifier are read only from an identity whose authentication type is the key scheme |
| A key gains a person's or an admin's rights | No `oid`, email, `groups`, `scp` or `roles`; `agent:` entries are refused on Owner and PiiReader lists; `IsPiiReader` is forced false; the people rules do not apply because there is no person |
| A hijacked Owner session opens a non-Entra path to the tools | The deployment ceiling, off by default; the Owner switch is a confirmed widening with an always-recorded audit row; issuing a key is audited (`AgentApiKeyCreated`) |
| A revoked key keeps working | At once on the saving instance, 30 s elsewhere, and refused everywhere once an instance has not read the key list for 5 minutes |
| A reused agent id inherits grants | Grants name the agent by design (rotation). The issue form shows the existing grants for that agent id before it issues a key |
| Brute force or key enumeration | 256-bit secrets; constant-time comparison; one 401 for every failure; failures logged with key id and IP, never the value. No failure limiter (Spec 039) |
| The key leaks through logs | Never accepted in a query string; the handler never logs the presented value; `Authorization` is redacted by common logging defaults; `nbak_` is recognisable to secret scanners |
| A browser page uses a key | Keys are not cookies, and the guard's `Origin` check applies as today |
| An agent loops on refusals | Refusals are audited once per key, reason and tool per 5 minutes per instance; the request limit applies to admitted calls |

What this spec does not mitigate: a leaked, unrevoked key can do everything its grant and the
Owner's switches allow until it expires. That is the same exposure as a leaked client secret of an
Entra workload, which can mint tokens until the secret expires, except that NimBus can revoke a key
itself.

## 11. Test plan

Add each failing test first.

- **Storage:** the conformance round-trips (§7) on all three providers; the two compatibility tests.
- **Selector and handler** (`AgentApiKeyAuthenticationTests` style):
  - `Bearer nbak_…` and `X-NimBus-Agent-Key` authenticate an MCP key; a JWT never reaches the key
    handler, and a key never reaches JwtBearer;
  - a request with the header and an `Authorization` header, or two keys, fails;
  - an `AgentApi` key on `/mcp` and an MCP key on `/api/agent/*` both get 401;
  - malformed, unknown, revoked, expired and stale-list keys fail; the log line has the key id and no
    secret;
  - with the ceiling off, a key gets 401 and the JWT path is unchanged;
  - in local development a key is ignored.
- **Startup wiring:** the MCP key scheme exists only in Entra mode with the ceiling on, and only the
  MCP policy names it (in the style of `AgentApiKeyStartupWiringTests`).
- **Enforcement through the real MCP pipeline** (`McpAccessPolicyEnforcementTests`,
  `McpTestHost`):
  - with the switch off, or workloads off: 403 and `McpAccessRefused` with reason `key`;
  - an observe-only key lists only read tools; classify, annotate and resubmit each need both the
    grant and the Owner switch;
  - skip, the Phase 3 tools, classification analysis and `includePayload` are refused for a key with
    every permission, every switch on, a PiiReader grant attempt and
    `GrantPiiReaderInDevelopment`;
  - the client list does not block a key; the people list does not apply;
  - hidden endpoints and the change-endpoint list apply.
- **Access Control:** `agent:<agentId>` grants work only for a key identity; an Entra token with
  `name = agent:x` or a `nimbus:agent_id` claim matches nothing; `agent:` entries are refused on
  Owner and PiiReader; `AgentApi` keys match nothing.
- **Budget and limits:** two keys of one agent share the daily budget, and two agents do not;
  rotation does not refill it; route and change limits partition per key; a key's lower request
  limit applies.
- **Audit:** command rows, refusals and classification and report rows carry `agentKeyId`; refusal
  deduplication is per key.
- **Expiry, rotation, revocation** (with `FakeTimeProvider`): expiry is required and capped; a
  revoked key works on another instance until the TTL and not after; the 5-minute staleness bound;
  rotate copies agent, use and grant and sets `Replaces`.
- **Admin API:** create validation, rotate (404, 409, revoked), Owner only, antiforgery, the
  `allowAgentKeys` default when omitted, the widening confirmation.
- **Compatibility:** with no saved policy, the ceiling off and only existing keys, the tool list,
  permitted actions, limits and `/api/agent/*` behave as today. Pin it.
- **Frontend (Vitest):** the use choice and grant checkboxes, no "Never" for MCP, the once-only
  dialog, Rotate, expiring-soon, the MCP access row's disabled states, the Access Control refusals.
- **Gates:** `dotnet build src/NimBus.slnx -c Release`; `dotnet test src/NimBus.slnx -c Release
  --no-build`; `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`; a build with
  NSwag generation on.
- **Pilot** (not CI): an Azure SRE Agent connected to a nonproduction deployment with an MCP key, as
  `Authorization: Bearer`, reads, records a classification and resubmits within the budget; revoking
  the key stops it within 30 s.

## 12. Delivery

One implementation PR, after this spec is approved, with one commit per part in dependency order. No
stacked PRs.

| # | Commit | Contents |
|---|---|---|
| 0 | none | Verify §16 and record the findings in the plan (`docs/plan/`) |
| 1 | `feat(store): key use, MCP grant and the agent-keys policy switch` | §7, conformance and compatibility tests |
| 2 | `feat(webapp): authenticate MCP agent keys on /mcp` | The ceiling option, `NimBusMcpAuth`, `NimBusMcpAgentKey`, the principal, the use and staleness checks (§6.2, §6.4, §6.10) |
| 3 | `feat(webapp): govern MCP agent keys as workloads` | `McpCaller`, permissions, `CheckCaller`, Access Control identity, budget, partitions, audit, capabilities (§6.5–§6.9, §6.11) |
| 4 | `feat(webapp): issue, rotate and grant MCP agent keys` | `api-spec.yaml`, the key service, rotate, Access Control validation (§8) |
| 5 | `feat(webapp): Settings and Access Control for MCP agent keys` | §9, with screenshots in the PR |
| 6 | `docs: agent API keys on /mcp` | §13 |

Exit: the pilot in §11.

## 13. Documentation

- `docs/mcp-server.md`: a section "Agent API keys", with the two settings, issuing a key, the
  Access Control grant, connecting Azure SRE Agent (Bearer token field), and what a key can never
  do. The workload sections name keys next to app roles.
- `docs/authentication.md`: the key's two uses.
- `docs/webapp-rest-api.md`: the agent-keys changes and rotate.
- `docs/rate-limiting.md`: per-key partitions and the per-key limit.
- Spec 039's and Spec 037's status lines link here.
- Release notes, following `docs/versioning.md#cutting-a-release`.

## 14. Compatibility

- Additive. Every stored key is an `AgentApi` key; no existing key works on `/mcp`. With the ceiling
  off, nothing changes. With the ceiling on and the switch off, a key gets 403.
- The Access Control API refuses an `agent:` entry on Owner and PiiReader lists, which it accepted
  before. Such an entry matched nobody, so no grant is lost; existing entries are not rewritten.
- `nimbus_get_capabilities` gains three fields. Nothing is removed or marked obsolete.

## 15. Open questions

1. **Deployment ceiling.** Recommended: required, default off (§6.5). The alternative, the Owner
   switch alone, saves one app setting.
2. **Prefix.** Keep `nbak_` for both uses (recommended), or issue MCP keys as `nbmk_` so a leaked key
   says what it opens?
3. **Keys without Entra.** Should `/mcp` be available with keys only, for deployments that sign in
   with NimBus Identity and have no MCP app registration? It needs a new `McpAuthenticationMode`.
   Proposed: not in this spec.
4. **IP allowlist per key.** Proposed: not in v1; connector egress addresses are often unpublished.
5. **Key id on the journal operation.** Proposed: not in v1; the command's audit row names it. It
   would be a store change on every provider.
6. **Staleness bound for `/api/agent/*`.** Should the 5-minute bound apply to `AgentApi` keys too?
   Proposed: yes, as a follow-up, since it changes Spec 039 behaviour.
7. **Maximum expiry.** 365 days (recommended) or 90?

## 16. Facts to verify before implementing

1. In Entra-only and dual sign-in, `UseAuthentication`'s default scheme on a `/mcp` request with
   `Bearer nbak_…` fails without side effects other than a log line, as it does today for MCP JWTs.
2. With a policy scheme as the MCP policy's only scheme, the policy evaluator sets
   `HttpContext.User` to the key principal before `UseRateLimiter`, so `McpPartitionKey` sees it.
3. The JwtBearer identity's authentication type can never equal `NimBusMcpAgentKey`.
4. The full list of audit writers an MCP workload reaches (coordinator, refusals, classification
   recorder, report tool) and where `agentKeyId` is added to each.
5. An action token prepared under one key is accepted under a sibling key of the same agent (both
   are `agent:<agentId>` to `OperatorActionTokens`). Intended; pin it.
6. Azure SRE Agent's connector sends the bearer value verbatim and no `Origin` header.
7. The generated `McpAccessSettings` request type maps an omitted `allowAgentKeys` to false.

## 17. References

- Issues: context-and-oss/NimBus#409, #407.
- Specs: [035](../035-mcp-operator-access/spec.md), [037](../037-mcp-admin-access/spec.md),
  [039](../039-agent-api-keys/spec.md), [044](../044-agent-first/spec.md).
- Docs: [docs/mcp-server.md](../../mcp-server.md), [docs/authentication.md](../../authentication.md).
- Code: `src/NimBus.WebApp/Mcp/McpOperatorServiceCollectionExtensions.cs`,
  `Mcp/McpOperatorAuthorization.cs`, `Mcp/McpAuthenticationMode.cs`, `Mcp/McpRequestGuardMiddleware.cs`,
  `Mcp/Access/McpCaller.cs`, `Mcp/Access/McpAccessPolicy.cs`, `Mcp/Access/McpAccessRequirement.cs`,
  `Mcp/Access/McpAccessRefusals.cs`, `Mcp/Operations/OperatorActionAccess.cs`,
  `Mcp/Operations/OperatorActionTokens.cs`, `Mcp/Tools/OperatorActionTools.cs`,
  `Mcp/Tools/OperatorDiscoveryTools.cs`, `Services/AgentApiKeys/`, `Services/SecretTokenFormat.cs`,
  `Services/EndpointAuthorizationService.cs`, `Controllers/ApiContract/AccessControlImplementation.cs`,
  `RateLimiting/RateLimitingServiceCollectionExtensions.cs`, `RateLimiting/RateLimitCallers.cs`,
  `Startup.Security.cs`, `Startup.Pipeline.cs`;
  `src/NimBus.MessageStore.Abstractions/States/AgentApiKeySettings.cs`, `States/McpAccessSettings.cs`,
  `States/OperatorOperation.cs`; `src/NimBus.MessageStore.SqlServer/Schema/0023_McpAccessSettings.sql`,
  `0024_AgentApiKeys.sql`; `src/NimBus.Testing/Conformance/EndpointMetadataStoreConformanceTests.cs`.
