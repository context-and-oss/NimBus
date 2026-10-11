# Spec 039: Agent API keys, implementation plan

Design: [docs/spec/039-agent-api-keys/spec.md](../spec/039-agent-api-keys/spec.md). Issue: context-and-oss/NimBus#241.

## Branches and PRs

There are two PRs, both against `master`:

| PR | Branch | Contents |
|---|---|---|
| 1 | `claude/agent-api-auth` | Storage, authentication scheme, management API, client option, docs. It works end to end through the API. |
| 2 | `claude/agent-api-keys-ui`, from PR 1 once merged | The Settings panel, the CrmErpDemo deployment of the agents, and a screenshot. |

## PR 1: backend

Each step writes its failing test first.

### 1. Storage contract
- `AgentApiKeySettings`, `AgentApiKey` and `AgentApiKeyUsage` in `NimBus.MessageStore.Abstractions/States`.
- Four new `IEndpointMetadataStore` members: Get/TrySet settings, Get usage, Record use.
- Conformance tests in `EndpointMetadataStoreConformanceTests` (the MCP access tests are the template):
  - defaults when never written;
  - compare-and-set conflict;
  - round trip of every field;
  - usage upsert keeps the latest time.

### 2. Providers
- **In-memory:** `InMemoryMessageStore`.
- **SQL:** `0024_AgentApiKeys.sql` and `SqlServerEndpointMetadataStore`, plus the `SqlServerMessageStore` forwarders.
- **Cosmos:** `CosmosDbEndpointMetadataStore` (in the `settings` container), plus the `CosmosDbClient` forwarders.
- Run the conformance suite live against SQL and the Cosmos vnext emulator, using the container recipe from memory.

### 3. Key material
- `AgentApiKeyFormat`: generate, parse and hash.
- Unit tests:
  - round trip;
  - rejects malformed input (wrong prefix, keyId length or alphabet, a bad secret);
  - the hash is the hash of the secret only.

### 4. Provider and handler
- `AgentApiKeyProvider`: a cached, fail-closed read, plus `Accept()`.
- `AgentApiKeyAuthenticationHandler`.
- `AgentApiKeyUsageRecorder`: throttled and best-effort; a failed write is logged and never fails the request.
- Handler tests:
  - no header gives `NoResult`;
  - a malformed, unknown, revoked, expired or wrong-secret key gives `Fail`;
  - a valid key gives a principal with the expected claims and no `groups`/`oid`;
  - a store failure fails closed.

### 5. Wiring per sign-in mode
- `Startup.Security.cs` registers the scheme in all four branches and records each branch's sign-in scheme name.
- The `AgentApi` policy plus an application-model convention on `AgentApiController`.
- Host tests, following `AnonymousEndpointsTests` and `McpTestHost`, for Identity-only, Entra-only, dual and LocalDev:
  - a valid key reaches `/api/agent/catalog`;
  - the same key gets 401 on a non-agent API such as `/api/me` or `/api/endpoint/...`;
  - a signed-in person still reaches `/api/agent/catalog`;
  - no key and no sign-in gives 401.

### 6. Agent id binding
- `AgentImplementation.CurrentAgentId` uses the key's agent id; a mismatched `X-Agent-Id` gets 403.
- Tests:
  - the settle audit row names the key's agent;
  - define-schema stamps `AgentId`;
  - a mismatched header gives 403;
  - human and LocalDev callers keep the header behaviour.

### 7. Management API
- Add the operations to `api-spec.yaml` (NSwag generates the rest) and implement `AgentApiKeysImplementation`:
  - Owner check;
  - antiforgery;
  - Admin rate-limit policy (`RateLimitPoliciesConvention` and `RateLimitEndpointMetadataTests`);
  - always-recorded audit types appended last to `MessageAuditType` and the api-spec enums;
  - 409 on a revision conflict.
- Tests:
  - non-Owner gets 403, and the denial is audited;
  - create returns the secret once and later reads never do;
  - revoke makes the key fail within the provider's cache window (with `Accept()` it fails immediately on the same instance);
  - delete is refused for an active key;
  - `agentId` validation.

### 8. Client
- `AgentOptions.ApiKey` and the `X-NimBus-Agent-Key` header in `AddNimBusAgent`.
- `AgentServiceCollectionExtensionsTests`: the header is sent when the key is set and absent when it is not.
- `EnrichmentAgent` reads `NimBus:Agent:ApiKey`.

### 9. Docs
- `docs/webapp-rest-api.md`: the agent API section and agent keys.
- `docs/authentication.md`: the new scheme and what a key admits.
- `docs/rate-limiting.md`: the note that failed keys are not throttled.
- `src/NimBus.Agents` README or XML docs.
- `CHANGELOG`/release notes are not part of this; they are cut at release time.

### Gate
- `dotnet build src/NimBus.slnx -c Release`: 0 errors. CS8767 in particular, since new interface members are involved.
- `dotnet test src/NimBus.slnx -c Release --no-build`, with SQL and Cosmos conformance running live.
- An NSwag-enabled build, so not `SkipSpaBuild`: the api-spec change must regenerate the controller and TS client.
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci`, because the regenerated TS client must still compile.
- Manual check: run the CrmErpDemo AppHost with `NIMBUS_IDENTITY=true`, create a key through the API, give it to `enrichment-agent`, and see a `CrmContactCreated` get enriched and settled, with the audit naming the agent.

## PR 2: UI and the demo

### 10. Settings panel
- `components/settings/agent-keys-*.tsx` (the model plus the panel), following `audit-settings.tsx` / `mcp-access-settings.tsx`.
- Register it in `models/manage-pages.ts` ("AI & agents") and `pages/settings.tsx`; add a status entry in `use-settings-status.ts`.
- Vitest tests:
  - list rendering;
  - create shows the key once and hides it after close;
  - revoke and remove confirmations;
  - a 409 reload message;
  - a non-Owner read-only state.

### 11. CrmErpDemo
- Publish mode deploys `agent-zone` and `enrichment-agent`, with an optional `enrichment-agent-key` secret parameter mapped to `NimBus__Agent__ApiKey`.
- Remove the README's "not deployed" row and add the create-key-then-redeploy step.
- Remove the AppHost comment about agents being local-only.

### Gate
- The PR 1 gate, plus the ClientApp build.
- A screenshot of the panel (AGENTS.md), with demo data via the pr-assets recipe.
- A real `aspire deploy` is optional and needs approval: it is billable.

## Risks

- **Policy scheme union.** If the policy names only `AgentApiKey`, MVC authenticates the action with only that scheme and signed-in people lose the agent API. Step 5's per-mode tests guard this.
- **Cache window.** A revoked key keeps working for up to 30 seconds on other instances. This is documented, and it matches MCP access settings.
- **Last-used writes.** They are throttled and best-effort, so a lost write only makes "last used" stale.

## PR 1 outcome (2026-10-11)

- Steps 1-9 are done.
- **Gate:**
  - Release build: 0 errors.
  - Full Release test suite: 3,745 passed, 0 failed, with SQL Server and the Cosmos emulator live.
  - ClientApp Vitest: 712 passed.
- **Design correction (step 5):** the `AgentApi` policy names the deployment's *effective* default authenticate scheme, resolved from `AuthenticationOptions`, not a name per branch. Entra-only resolves to `OpenIdConnect` and dual mode to `Identity.Application`. The spec's table records this.
- **Manual check** (CrmErpDemo with `NIMBUS_IDENTITY=true`):
  - The Owner issued a key through the API.
  - The key reaches `/api/agent/catalog`, gets 401 on `/api/me` and `/api/admin/*`, and gets 403 when `X-Agent-Id` names another agent.
  - The EnrichmentAgent with the key subscribed, received, published and settled. Its audit rows name `enrichment-agent`, and the key's last use was recorded.
- **Found and fixed on the way:** `NimBusClaimsTransformation` threw "Collection was modified" for every Identity user with a role. It was latent until the bootstrap admin got `EIP_Management` in #242, so any signed-in bootstrap admin got 500s.
- **Found, out of scope:** the agent can receive and settle the same pending hand-off twice, producing two `completeHandoff` audit rows. It looks like a pre-existing race between settle and the Resolver; it is tracked separately.
