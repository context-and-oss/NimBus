# Spec 039: Agent API keys

Status: implemented. Merged as context-and-oss/NimBus#247 and #258 and shipped in v4.8.0.
[Spec 052](../052-mcp-agent-api-keys/spec.md) adds a second use: keys issued for `/mcp`.
Proposed 2026-10-11. Issue: context-and-oss/NimBus#241.

## Problem

Agents built on `NimBus.Agents` call the WebApp's `/api/agent/*` endpoints and send only an `X-Agent-Id` header.

- Once the WebApp requires sign-in (NimBus Identity, Entra ID, or both), every agent call returns 401. The agent loop logs the failure and backs off forever.
- Only the Development-only local auth bypass works, so the CrmErpDemo Azure deployment leaves `enrichment-agent` and `agent-zone` out.
- `X-Agent-Id` is free text chosen by the client. It is written to the audit trail as the auditor when an agent settles a hand-off, so any caller can claim to be any agent.

## Decisions

Taken with the user on 2026-10-11.

| Topic | Decision |
|---|---|
| Mechanism | Agent API keys, in a dedicated authentication scheme. Entra client credentials can follow later as a second scheme, in the same way `/mcp` has its own `NimBusMcp` bearer scheme. |
| Key management | A site Owner issues and revokes keys in **Settings**. A key's secret is shown once, and only its hash is stored. |
| What a key admits | Only `/api/agent/*`. The agent id comes from the key, so `X-Agent-Id` can no longer be spoofed by a key holder. Payload access is unchanged. |

## Design

### Key format

The key is `nbak_<keyId>_<secret>`.

- `keyId` is 12 lowercase base32 characters. It is used for lookup and display.
- `secret` is 32 random bytes from `RandomNumberGenerator`, base64url-encoded.
- The store keeps `SHA-256(secret)`. With 256 bits of entropy, a slow KDF adds nothing.
- Comparison uses `CryptographicOperations.FixedTimeEquals`.
- The `nbak_` prefix makes leaked keys recognisable to secret scanners. The display prefix is `nbak_<keyId>`.

### Storage

There is one singleton record, `AgentApiKeySettings`, on `IEndpointMetadataStore`, modelled on `McpAccessSettings` (Spec 037):

```
AgentApiKeySettings { Id = "AgentApiKeys", Revision, UpdatedBy, UpdatedAtUtc,
                      Keys: [ AgentApiKey { KeyId, AgentId, Name, SecretHash,
                                            CreatedAtUtc, CreatedBy, ExpiresAtUtc?,
                                            RevokedAtUtc?, RevokedBy? } ] }
```

- `GetAgentApiKeySettings()` / `TrySetAgentApiKeySettings(settings, expectedRevision)` use the existing compare-and-set pattern: Cosmos ETag, and SQL `UPDATE … WHERE Revision`.
- Last-used times are kept in a separate per-key record with last-write-wins semantics: `GetAgentApiKeyUsage()` / `RecordAgentApiKeyUse(keyId, usedAtUtc)`. That way authentication never touches the revisioned record that an Owner may be editing.
- Writes are throttled to once per key per 5 minutes per process.
- Providers:
  - Cosmos: documents in the existing `settings` container (no new container).
  - SQL: script `0024_AgentApiKeys.sql` with two tables.
  - In-memory: in `InMemoryMessageStore`.
- Covered by `EndpointMetadataStoreConformanceTests`. `InstrumentingMessageTrackingStoreDecorator` implements only `IMessageTrackingStore`, so it needs no change.
- Revoked keys stay in the record so the Settings page can show them. Removing revoked keys is a separate action.

### Authentication

- **Scheme:** `AgentApiKey`, an `AuthenticationHandler` that reads the `X-NimBus-Agent-Key` header.
  - It uses a dedicated header, not `Authorization: Bearer`, because the Entra policy schemes forward every `Bearer` header to JwtBearer.
  - The keys come from `AgentApiKeyProvider`, which copies `McpAccessPolicyProvider`: a 30-second cache, `Accept()` after a save, and fail closed when the store cannot be read.
  - A missing header returns `NoResult`. A malformed, unknown, revoked or expired key returns `Fail`.
- **Principal:**
  - one identity with authentication type `AgentApiKey`;
  - claims `NameIdentifier = name = agent:<agentId>`, `nimbus:agent_id`, `nimbus:agent_key_id`;
  - **no `groups` claim and no `oid`**, so it can never match the `EIP_Management` marker or an ACL entry.
  - Rate-limit partitions and audit rows therefore name the agent.
- **Scope:** the scheme is registered in every sign-in mode but is never a default or forward target. The only place that names it is an `AgentApi` authorization policy:
  - the policy is `AddAuthenticationSchemes(<default authenticate scheme>, AgentApiKey)` plus an authenticated user;
  - an application-model convention (like `AllowAnonymousActionsConvention`) adds it to the generated `AgentApiController`;
  - MVC authenticates an action only with the schemes its combined policy names, so naming the default scheme keeps signed-in people working on the agent API as today;
  - every other endpoint authenticates only with the default scheme, so a key admits nothing else.
- **Which default scheme:** it is read from the final `AuthenticationOptions` (`DefaultAuthenticateScheme ?? DefaultScheme`) when the policy is built, not hard-coded per sign-in branch. The branches' own names are misleading:

  | Mode | Default authenticate scheme |
  |---|---|
  | LocalDev | `LocalDev` |
  | Identity-only | `Identity.Application` |
  | Dual | `Identity.Application`; `AddIdentity` sets it explicitly |
  | Entra-only | `OpenIdConnect`; Microsoft.Identity.Web's web-app setup sets it, not the `Az` policy scheme |

  `AgentApiKeyStartupWiringTests` pins this table.
- **Agent id:**
  - When the caller authenticated with a key, `AgentImplementation` takes the agent id from `nimbus:agent_id`. A different `X-Agent-Id` header gets 403, so a mismatch is visible rather than silently ignored.
  - Signed-in people keep today's header behaviour. Tightening the human path is out of scope (see Non-goals).
- **Brute force:** keys carry 256 bits of entropy, and failures are logged with `keyId` and the client IP. There is no failure limiter in v1, because `UseRateLimiter` runs after authentication.

### Management API

All of these require Owner, `[AutoValidateAntiforgeryToken]` and the Admin rate-limit policy, and are defined in `api-spec.yaml`:

| Operation | Effect |
|---|---|
| `GET /api/admin/agent-keys` | Lists the keys (never the secrets) with last-used times, plus a CSRF token. |
| `POST /api/admin/agent-keys` `{agentId, name, expiresAtUtc?}` | Creates a key and returns its full value **once**. |
| `POST /api/admin/agent-keys/{keyId}/revoke` | Revokes a key. |
| `DELETE /api/admin/agent-keys/{keyId}` | Removes a key, but only once it is revoked or expired. |

- A revision conflict returns 409.
- New always-recorded audit types `AgentApiKeyCreated`, `AgentApiKeyRevoked` and `AgentApiKeyRemoved` are **appended** to `MessageAuditType` and to the api-spec enums.
- `agentId` must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`.

### Settings UI

A new **Agent keys** feature in the "AI & agents" group of the Spec 038 Settings page, built on `components/settings/panel-frame.tsx`.

- It lists keys with their agent, name, prefix, created, last used, expiry and status.
- A create form returns the key once, with a copy button and a "you won't see this again" note.
- Each key has revoke and remove actions, with confirmation.
- The feature status shows the number of active keys.

### Client

- `AgentOptions.ApiKey` (optional): when set, `AddNimBusAgent` sends `X-NimBus-Agent-Key`, and `X-Agent-Id` stays as is. Like the other options, it is set in the `configure` delegate. `AddNimBusAgent` does not bind configuration.
- The `EnrichmentAgent` sample sets it from configuration (`NimBus:Agent:ApiKey`, so environment variable `NimBus__Agent__ApiKey`).

### CrmErpDemo

- Publish mode deploys `agent-zone` and `enrichment-agent` again. The agent gets an optional secret parameter, `enrichment-agent-key`.
- README: deploy, create the key in Settings, then redeploy with `Parameters__enrichment_agent_key`. Until then the agent logs 401s and the Agent Zone holds contacts as Pending, which the README says.
- Run mode is unchanged: the local auth bypass still admits the agent without a key.

### Signed-in people on the agent API

The agent API also accepts the sign-in scheme, so a person is held to the same rules as everywhere else in the WebApp. Roles are checked on the Agent Zone endpoint (`Agent:ZoneEndpointId`), and a site-wide role also counts.

| Operation | A person needs |
|---|---|
| `GET catalog`, `POST subscribe`, `GET receive` | Reader |
| `POST event-types`, `POST publish`, `POST settle` | Contributor |

- A refused call returns 403. A refused settle also writes an access-denied `CompleteHandoff` or `FailHandoff` audit row, as the operator settle path does.
- `receive` masks the payload for a person who is not a PiiReader, using the masker every other payload-returning endpoint uses. Agent events have no CLR class, so the masker fails closed and the person gets its marker instead of the payload.
- A person is recorded under their own resolved name (the name used for every other audit row) on schema definitions, subscriptions and settle audit rows. `X-Agent-Id` is ignored, so a person cannot attribute an action to an agent.
- Agent API key callers are unchanged: they are not role-checked, they read payloads unmasked, and they are recorded as the key's agent. Only the key scheme's own identity counts, so a sign-in token carrying the agent id claim is still a person.
- The local development bypass (`EnableLocalDevAuthentication`, Development only, where every caller is already an Owner) keeps `X-Agent-Id`. An agent run locally without a key, as in CrmErpDemo Run mode, still acts and is audited as itself.

## Non-goals (v1)

- Entra client-credential tokens for agents (a later `NimBusAgentBearer` scheme).
- Per-key scopes, such as limiting a key to certain event types or endpoints, and PII masking on `receive` for key callers.
- A failed-key rate limiter.

## Compatibility

- The WebApp API gains new operations. Agent API key callers and the local development bypass are unaffected: an agent run locally without a key, as in CrmErpDemo Run mode, still works and is audited under its `X-Agent-Id`.
- A client that calls the agent API on a person's sign-in session, without a key, is now held to that person's roles. It gets 403 without Reader (to read) or Contributor (to change anything) on the Agent Zone endpoint, `receive` returns the payload masked unless the person holds PiiReader, and `X-Agent-Id` is ignored, so its actions and subscriptions are recorded under the person's own name rather than the agent it named. Such a client should move to an agent API key.
- Subscriptions are kept per caller kind: a person and an agent with the same name do not share them.
- Nothing is removed or marked obsolete.
