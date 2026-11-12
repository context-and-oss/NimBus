# Spec 045: Integrator API keys

Status: proposed (2026-11-12). Plan: [External API readiness](../../plan/2026-11-12-external-api-readiness.md), PR 2.

## Problem

The REST API (`/api/v1/*`) is meant for external callers such as CI scripts, partner automation
and internal tools. Today those callers have two options, and neither fits:

- **An Entra ID token** (client credentials). This works only where the WebApp uses Entra ID, and
  needs an app registration and app-role or group plumbing for each caller. A deployment that signs
  in with NimBus Identity has no machine identity at all.
- **An agent API key** (Spec 039). It admits only `/api/v1/agent/*` and carries no role, so it
  cannot read an endpoint's failed events or resubmit one.

Item 1 of the gap list in [`docs/webapp-rest-api.md`](../../webapp-rest-api.md#whats-not-external-ready-yet)
asks for a general integrator key with per-caller scopes.

## Decisions

Taken with the user on 2026-11-12.

| Topic | Decision |
|---|---|
| Scope model | A key carries a **site role** (Reader or Contributor), an optional **endpoint list**, and a **PII flag**. Named OAuth-style scopes per route were rejected: every check in the API already speaks the role ladder, so mapping a key onto that ladder enforces scope without a per-route mapping. |
| Owner | A key can never be Owner. `/api/v1/admin/*` and everything else that requires Owner stays with signed-in people. |
| Management | A site Owner issues, revokes and removes keys in **Settings → Integrator keys**, or through the admin API. The secret is shown once, and only its hash is stored. This is the same lifecycle as agent keys. |
| Delivery | A separate PR after the external-readiness PR (ProblemDetails, `/api/v1`), so it builds on both. |

## Design

### Key format

`nbik_<keyId>_<secret>`, built with the existing `SecretTokenFormat` (Spec 039):

- `keyId` is 12 lowercase base32 characters.
- `secret` is 32 random bytes, base64url-encoded.
- The store keeps `SHA-256(secret)`, compared in constant time.

The `nbik_` prefix tells a leaked integrator key apart from an agent key (`nbak_`) or a dashboard
token (`nbdt_`).

### Storage

There is one singleton record behind a new `IIntegratorApiKeyStore` interface, following
`IDashboardTokenStore`.

```
IntegratorApiKeySettings { Id = "IntegratorApiKeys", Revision, UpdatedBy, UpdatedAtUtc,
                           Keys: List<IntegratorApiKey> }
IntegratorApiKey { KeyId, Name, SecretHash, Role (Reader | Contributor),
                   Endpoints (null = every endpoint), CanReadPii,
                   CreatedAtUtc, CreatedBy, ExpiresAtUtc?, RevokedAtUtc?, RevokedBy? }
IntegratorApiKeyUsage { KeyId, LastUsedAtUtc }
```

- **Writes** use compare-and-set on `Revision`, retried up to three times, then `RevisionConflict`.
  This is the same as for agent keys.
- **Usage** is a separate record, so recording use never conflicts with an Owner's edit. It is
  written at most once per key per instance every 5 minutes.
- **Providers:**
  - Cosmos DB: a `IntegratorApiKeys` document and `IntegratorApiKeyUsage-<keyId>` documents.
  - SQL Server: migration `00NN_IntegratorApiKeys.sql` with tables `IntegratorApiKeys` and
    `IntegratorApiKeyUsage`, listed among `SqlServerSchemaInitializer`'s required tables.
  - In-memory: in `InMemoryMessageStore`.

  The conformance suite covers all three. The store is a separate interface, so
  `InstrumentingMessageTrackingStoreDecorator` has nothing to forward.

### Authentication

- **Scheme:** `IntegratorApiKey`. The key travels in the `X-NimBus-Api-Key` header, never as
  `Authorization: Bearer`, which the Entra policy scheme forwards to JwtBearer.
- **Handler:** mirrors `AgentApiKeyAuthenticationHandler`.
  - No header returns `NoResult`.
  - These are refused with 401, and logged without the key: malformed, store unavailable, unknown
    key, hash mismatch, revoked, expired.
  - The provider caches the list for 30 s (5 s after a failed read) and fails closed: no
    snapshot older than 30 s is trusted, even while the store is unreachable, and a read that
    started before a local save never replaces it.
- **Principal:**
  - `NameIdentifier` and `Name` are `integrator:<keyId>`, plus the claim
    `nimbus:integrator_key_id`.
  - It has no `groups`, `oid`, email or role claims, so it cannot match an ACL entry, the
    platform-admin marker or the `PiiReader` app role.
- **Where it is accepted:**
  - The scheme is added to the global `AuthorizeFilter` policy on the three sign-in branches
    (Identity, Entra ID, dual). It is never a default scheme.
  - So a key authenticates on the MVC API controllers, and nowhere that has its own policy:
    `/api/v1/agent/*` (agent keys), the Monitor feed (dashboard tokens), the Identity extension's
    `/api/v1/auth/*` (a person's session), `/hubs/*`, `/mcp` and the sign-in pages.
  - In local-dev mode no global filter exists, so the scheme is not added there.
- **Key plus sign-in:** a request that carries a key and a sign-in cookie or bearer token is
  evaluated as the key alone (see Authorization). The key's identity comes first, so the audit
  actor and the rate-limit partition name the key. Writes from a browser session still need the
  CSRF token, because `CookieAntiforgeryFilter` enforces it whenever sign-in cookies are present.

### Authorization

`EndpointAuthorizationService.ResolveAsync` checks for a key identity first. When it finds one, it
builds `CurrentUserAccess` from the key's record and nothing else:

| Key | `SiteRole` | `EndpointRoles` | `IsPiiReader` |
|---|---|---|---|
| `Endpoints` null | `Role` | empty | `CanReadPii` |
| `Endpoints` listed | `None` | each listed endpoint → `Role` | `CanReadPii` |

- **What is skipped:** no ACL snapshot, no compat `groups` grant, no code-defined
  `RoleAssignments`, no `BypassEndpointAuthorization` and no `GrantPiiReaderInDevelopment`. A key
  is exactly what its Owner issued, in every environment.
- **Enforcement:** every existing `HasRoleAsync`, `HasRoleFreshAsync`, `CanReadPiiAsync` and
  `ForbidResult` check then enforces the scope. A refusal is the usual 403 problem.
- **Re-reads:** `HasRoleFreshAsync` re-reads the key list (bypassing the cache), so a fresh
  Contributor check sees a revocation from any instance at once. When that read fails, the check
  denies.
- **Site-wide listings:** endpoint-scoped keys see only their endpoints. This works through the
  existing per-endpoint filters, the same way an ACL-scoped person sees only theirs.

### Rate limiting and audit

- **Rate limiting:** the global limit and the named policies partition on `NameIdentifier`, so
  each key has its own budget (`integrator:<keyId>`).
- **Audit:** `AuditLogService` records the auditor as `integrator:<keyId>`. Every operator action
  a key takes (resubmit, skip, hand-off settlement and so on) is attributable to the key.
- **New audit types:** `IntegratorApiKeyCreated`, `IntegratorApiKeyRevoked` and
  `IntegratorApiKeyRemoved`.
  - They are always recorded, whatever the audit settings, as the agent-key types are.
  - Their `Data` is `{keyId, name, role, endpoints, canReadPii, expiresAtUtc}`, never the secret.

### Management API

All operations require site Owner. They are audited, including when access is denied, and use the
Admin rate-limit policy. Writes need the `X-NimBus-CSRF` token from the GET.

| Operation | Purpose |
|---|---|
| `GET /api/v1/admin/integrator-keys` | Lists the keys without secrets (status `active` / `revoked` / `expired`, last used), the endpoint ids the picker offers, and `csrfToken` |
| `POST /api/v1/admin/integrator-keys` | Issues a key. Body: `name` (required, up to 100 characters), `role` (`reader` or `contributor`), optional `endpoints`, `canReadPii` and `expiresAtUtc`. The response's `key` is the full key, shown once |
| `POST /api/v1/admin/integrator-keys/{keyId}/revoke` | Revokes a key |
| `DELETE /api/v1/admin/integrator-keys/{keyId}` | Removes a revoked or expired key |

**Validation errors** are problems with `code` `Invalid`, `NotFound`, `StillActive`,
`RevisionConflict` or `InvalidAntiforgeryToken`. The `Invalid` cases are:

- a role other than Reader or Contributor
- an empty or duplicate endpoint
- an endpoint the platform doesn't define
- an expiry in the past
- a missing or overlong name

### Settings UI

**Settings → Integrator keys** sits in the "Access" group next to access control. It is modelled
on Agent keys:

- A table of keys: name, role, endpoints ("All endpoints" or a list), PII, status, created,
  expires, last used.
- **Issue key**: name, role, endpoint picker (all, or chosen endpoints), PII checkbox, expiry
  (30 / 90 / 365 days, never).
- The new key is shown once with a copy button and an example `curl`.
- Revoke and Remove, each behind a confirmation.

### Docs

- `docs/authentication.md` gets an "Integrator API keys" section.
- `docs/webapp-rest-api.md` gets a fifth authentication shape, its example, and removes the last
  gap.
- The spec's `securitySchemes` gets `X-NimBus-Api-Key`.
- The CORS allow-list (`ApiCorsExtensions.AllowedHeaders`) adds `X-NimBus-Api-Key`.

## Non-goals (v1)

- Per-route or OAuth-style scopes, and Owner keys.
- Entra client-credentials mapping onto the same model. Entra callers keep using app roles and
  ACLs.
- Key rotation with overlap. Issue a second key, move the caller, then revoke the first.
- Distributed revocation faster than the 30 s cache.

## Compatibility

The change is additive: one new store interface implemented by all three providers, a new SQL
migration, new admin routes and a new header. Nothing that exists changes behaviour. A key presented
to a WebApp that predates this spec is ignored, and the request is then refused as unauthenticated.
