# External API readiness: the six open items in docs/webapp-rest-api.md

## Context

`docs/webapp-rest-api.md` ("What's not external-ready yet", lines 459-490) lists six gaps that keep
the WebApp REST API from being offered to external integrators. Exploration found two of its claims
are stale:

- There is no CORS policy at all. The `login.microsoftonline.com` policy was removed in `d61f123f`.
- `wwwroot/api-spec.yaml` is copied but never served.

Facts the design relies on:

- Errors come in ad-hoc shapes: about 130 `NotFoundObjectResult(string)`, 121 `ForbidResult`,
  66 `BadRequestObjectResult`, and four typed `{code, errors}` problems.
- `[ApiController]` is not used and `AddProblemDetails` is not registered.
- Auth and antiforgery return bodyless 401/403/400, and the 429 body is `text/plain`.
- `GlobalLimiter` is null.
- NSwag emits per-action `[Route("api/...")]` straight from the spec paths.
- Nothing on the server reads the SPA's `api-version: 2` header.
- Every role decision goes through `EndpointAuthorizationService.ResolveAsync`.

Decisions taken with the user:

- Integrator keys carry a role plus an endpoint list.
- Spec paths move to `/api/v1`, and the old routes stay as deprecated aliases.
- The work ships in **two PRs**. Both branch from `master`, so they are not stacked.

Plan file in repo: `docs/plan/2026-11-12-external-api-readiness.md` (a copy of this plan, first commit of PR 1).

---

## PR 1: `feat(webapp): external API readiness` (one commit per item)

### 1. ProblemDetails (RFC 7807) for every `/api/*` error

The aim is to standardise errors without rewriting the roughly 400 call sites. The conversion happens
at three choke points, and only the few structured bodies are changed by hand.

**Server**

- In `Startup.Web.cs`, call `services.AddProblemDetails(...)`. The customizer adds `traceId`
  (`Activity.Current?.Id ?? HttpContext.TraceIdentifier`) and sets `type` to the RFC 9110 section
  link for the status.
- Add a new `Controllers/ProblemDetailsResultFilter.cs`. It is a global, late `IAlwaysRunResultFilter`
  that only acts on `/api` requests. For an `ObjectResult` with status 400 or above:
  - A `string` value becomes `ProblemDetails{status, title=ReasonPhrase, detail=value}`.
  - An `IEnumerable<string>` becomes `detail` (the items joined) plus an `errors` extension.
  - `null` gets `title` only.
  - A value that is already a `ProblemDetails`, or one of the typed problems below, is left as is.

  In every case the content type is set to `application/problem+json`. Unit tests that call the
  implementations directly and cast `.Value` to `string` are unaffected, because the filter only
  runs inside MVC.
- Add a new `Middleware/ApiProblemStatusCodeMiddleware.cs`. It is registered early and covers
  `/api/*` only. When a response finishes with status 400 or above and no body, it writes
  ProblemDetails through `IProblemDetailsService`. This covers:
  - `ForbidResult`
  - the bare 401/403 from `NimBusCookieAuthenticationEvents` and the OIDC hook in
    `Startup.Security.cs:202`
  - `StatusCodeResult(503)` and bodyless `BadRequestResult`

  Use `UseStatusCodePages` with a path predicate, or a small custom middleware, whichever is
  simpler to verify.
- In `Startup.Pipeline.cs:16-24`, add an exception handler for `/api/*` that returns a 500
  ProblemDetails with no exception text outside Development. This also fixes the dead
  `/Home/Error` re-execute for API calls.
- `Csrf/CookieAntiforgeryFilter.cs:64`: `AntiforgeryRefusedResult` becomes a 400 ProblemDetails
  with `code: "InvalidAntiforgeryToken"`, the same code `IntelligenceSettingsController` already uses.
- `RateLimiting/RateLimitingServiceCollectionExtensions.cs:135`: `OnRejected` writes a 429
  ProblemDetails (with `retryAfter`) for `/api/*` and keeps `text/plain` for `/account/login`.
- `Controllers/IntelligenceSettingsController.cs`: its anonymous `{code}` objects become
  ProblemDetails with a `code` extension. The property name stays the same.
- The typed problems become ProblemDetails supersets: `AgentApiKeyProblem`, `DashboardTokenProblem`,
  `McpAccessProblem` and `SimulationProblem`. In the spec each is `allOf: ProblemDetails`, and their
  local `Problem(...)` helpers fill in `status` and `title`.
- `AdminImplementation.SessionState.cs:73`: the 409 currently returns a `SessionStateView` body. It
  becomes ProblemDetails with `code: "SessionStateChanged"` and a `session` extension.
- Unmatched `/api/*` routes currently fall through to the SPA `index.html` fallback. Add a
  `MapFallback("api/{**path}")` before `MapFallbackToFile` so they return a 404 ProblemDetails.

**Spec (`api-spec.yaml`)**

- Add `components/schemas/ProblemDetails`: `type`, `title`, `status`, `detail`, `instance`,
  `traceId`, `code`, `errors`, with `additionalProperties: true`.
- Add `components/responses` entries `BadRequest`, `Unauthorized`, `Forbidden`, `NotFound`,
  `Conflict`, `TooManyRequests`, `InternalServerError` and `ServiceUnavailable`, all as
  `application/problem+json`.
- Repoint the roughly 230 content-less 4xx/5xx entries to these, using a scripted mechanical edit
  that is reviewed afterwards.
- Fix the 404 declared as `text/string` on `/api/endpoint/{endpointName}/status/`.

**SPA (`ClientApp/src`)**

- In `api-gen.nswag`, set `wrapDtoExceptions: true` so the client still throws `SwaggerException`
  with `.status`, `.response` and `.message`, and puts the parsed problem on `.result`.
  - Verify this in the generated `index.ts`.
  - Then update the call sites that relied on thrown DTOs: `simulation-utils.ts:35`,
    `session-state.tsx:120` (`instanceof SessionStateView`) and the MCP problem handling.
- Move `bodyDetail` from `components/admin/operation-error.ts` into a shared
  `api-client/problem.ts`. Use it in `event-types-panel.tsx:77` and
  `event-type-example-payload.tsx:29`, which currently show the raw body.
- `agent-keys-model.ts:67`, `dashboard-tokens-model.ts:73` and `mcp-access-settings.tsx:26` decide
  an expired token from "a 400 that is not JSON". Change them to check
  `code === "InvalidAntiforgeryToken"`.

**Tests (red first)**

- Add `tests/NimBus.WebApp.Tests/ProblemDetailsTests.cs`, using the TestServer harness pattern from
  `RateLimitEnforcementTests.cs`. It asserts `application/problem+json` and the body for:
  - a string 404, a list 400, a bodyless `ForbidResult`, an unauthenticated 401
  - an antiforgery 400, a 429, a thrown exception (500), an unknown `/api/x`
  - a typed problem (`code` and `errors` are kept)
- Update the existing `code` assertions in `AgentKeysApiTests`, `DashboardTokensApiTests` and
  `McpAccessSettingsTests` only if their shape changes; it should not.
- Update the Vitest tests that build string-bodied `SwaggerException`s.

### 2. Global rate limiter for `/api/*`

- Add `RateLimitOptions.Global` (`WindowLimits`). The default is **600 requests per 60 s** per
  caller, and it is turned off by the existing `Enabled` kill switch.
- In the existing `AddRateLimiter` call, set
  `limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext,string>(...)`. Keep it in the
  same call, because `RateLimitWiringTests` asserts exactly one call.
  - `/api/*` gets a fixed window keyed by `UserPartitionKey`; a key caller gets its key id.
  - Everything else gets `GetNoLimiter`: the hub, `/mcp` (which has its own policy), health,
    static files and the SPA.
  - The named policies keep applying on top, as tighter ceilings.
- Extend `OnRejected` so it reports `global` as the policy name.
- Tests in `RateLimitEnforcementTests`: an unlisted `/api` route returns 429 after N requests,
  `/health` and the hub are never throttled, and `Enabled=false` disables it.
- Update `docs/rate-limiting.md`. It should also state plainly that limits stay per process (each
  instance has its own counters) and that distributed limiting is out of scope.

### 3. Production server entry in the spec

- `api-spec.yaml:6-8`: add a `servers` entry `https://{host}` with variable `host` (default
  `nimbus.example.com`, described as the WebApp host name). Change the local entry to
  `https://localhost:18443`, which matches `launchSettings.json`.
- NSwag ignores `servers` (`basePath "/"`, `serviceHost "."`). Confirm with a build that NSwag
  generation is still enabled, so not `SkipSpaBuild`.

### 4. `/api/v1` versioning

- Spec: rewrite every `paths:` key from `/api/...` to `/api/v1/...` and set
  `info.version: 1.0.0`. NSwag then emits `[Route("api/v1/...")]`, and the SPA's generated client
  follows automatically.
- Add a new `Controllers/ApiVersionAliasConvention.cs` (`IApplicationModelConvention`), inserted
  at **index 0** of `MvcOptions.Conventions`. It must run before `RateLimitPoliciesConvention` and
  `AllowAnonymousActionsConvention` add per-selector metadata, so the aliases get that metadata too.
  - For every attribute-routed action under `api/`, it makes sure both `api/v1/X` and `api/X`
    exist.
  - This also covers hand-written controllers: `IntelligenceSettingsController`, Identity
    `api/auth` and `api/integration-intelligence`.
  - The unversioned selector gets `LegacyApiRouteMetadata`.
- Add a small `Middleware/LegacyApiRouteHeadersMiddleware.cs`. On legacy endpoints it adds
  `Deprecation: true` and `Link: </api/v1/...>; rel="successor-version"`.
- Removing the legacy routes follows `docs/versioning.md`: they are deprecated in this minor and
  removed in v5.0.0. The release notes' `### Compatibility` section must say so.
- Change the SPA's raw `fetch("/api/...")` strings to `/api/v1/...`, in these files and their
  tests:
  - `agent-keys-model.ts`, `dashboard-tokens-model.ts`
  - `mcp-access-settings.tsx`, `failure-intelligence-settings.tsx`, `asyncapi-export.tsx`
  - `intelligence-card.tsx`, `sidebar-user-footer.tsx`
  - `use-current-user.ts`, `use-settings-status.ts`

  Remove the dead `api-version: 2` header in `index.extensions.ts:50`.
- `src/NimBus.Agents/Internal/RestAgentBusGateway.cs` stays on `/api/agent/*`, because the
  published client must keep working against older servers. Add a comment with the reason.
  `samples/CrmErpDemo/e2e/helpers/nimbus-api-client.ts` moves to v1.
- Path checks keep working without changes: `NimBusCookieAuthenticationEvents.ApiPathPrefix` and
  CSRF's `StartsWithSegments("/api")` both match `/api/v1`. Add a test for each to pin this.
- Tests:
  - `RateLimitEndpointMetadataTests` checks that the v1 route and its alias carry the same policy.
  - A new `ApiVersionAliasTests` checks: both routes resolve; only the legacy one has the headers;
    `[AllowAnonymous]` and agent-policy metadata are on both; no duplicate route-match ambiguity.
- Docs: rewrite the "Versioning + stability" section with the non-breaking change policy.
  - Within v1 these are allowed: additive changes (new routes, optional fields, new enum values
    only where documented).
  - A breaking change means `/api/v2` plus a deprecation window.

  Update the `/api/...` examples in `docs/` to `/api/v1/...`. The website link check must stay green.

### 5. Configuration-driven CORS allow-list

- Add `Cors:AllowedOrigins` (string array) and a new `Startup.Cors.cs`.
  - When the list is empty (the default), CORS is not registered at all, so behaviour is unchanged.
  - Startup fails on an empty or whitespace entry, `*`, or a non-`https` origin outside
    Development. This follows the config-binder empty-array lesson.
- Policy:
  - `WithOrigins(list)`, **no** `AllowCredentials` (browser callers use a bearer token or an API
    key; cookies are never sent, which keeps CSRF out of scope).
  - Methods: GET, POST, PUT, PATCH, DELETE.
  - Request headers: `Authorization`, `Content-Type`, `X-NimBus-Agent-Key`, `X-Agent-Id`, and
    `X-NimBus-Api-Key` from PR 2.
  - Exposed headers: `Retry-After`, `Deprecation`, `Link`, `Location`.
  - Preflight max-age 10 minutes.
- In `Startup.Pipeline.cs`, add `UseWhen(path starts with /api, b => b.UseCors(policy))` between
  `UseRouting` and `UseAuthentication`, so preflights are answered before the global authorize
  filter.
- Tests:
  - an allowed origin gets a preflight 204 with ACAO and no ACAC
  - an unknown origin gets no ACAO
  - a non-`/api` path gets no CORS
  - invalid config fails startup
- Docs: rewrite the CORS section of `webapp-rest-api.md`, and fix the stale line references at
  :429, :437 and :442. Add `Cors__AllowedOrigins__0` to `docs/configuration`, or wherever app
  settings are documented.

### PR 1 closing

- Update the gap list in `webapp-rest-api.md`: items 2-6 are done, item 1 points to Spec 045.
- Write a CHANGELOG / release-notes note about the `/api/v1` deprecation and the error-body change
  (Compatibility section).

---

## PR 2: `feat(webapp): integrator API keys with role and endpoint scope` (Spec 045)

**First commit: `docs/spec/045-integrator-api-keys/spec.md`.** It is modelled on Spec 039 and
written before any code. The design below is a summary of that spec.

- **Key model** (new `States/IntegratorApiKeySettings.cs`, a single CAS document like the dashboard
  tokens):
  - `KeyId`, `Name`, `SecretHash`
  - `SiteRole` (`Reader` | `Contributor`; Owner can never be issued)
  - `Endpoints` (null means the role applies site-wide; a list means the role applies only to
    those endpoints)
  - `CanReadPii`
  - `ExpiresAtUtc`, `RevokedAtUtc`/`RevokedBy`, `CreatedAtUtc`/`CreatedBy`
  - `Revision`
- **Storage**:
  - A new `IIntegratorApiKeyStore`, following the `IDashboardTokenStore` pattern.
  - Implemented in Cosmos (`CosmosDbClient`), SQL Server (new migration `00NN_IntegratorApiKeys.sql`,
    added to `SqlServerSchemaInitializer`'s required tables) and in-memory (`InMemoryMessageStore`).
  - Covered by a conformance-suite fixture and registered in both provider builder extensions.
  - Check whether `InstrumentingMessageTrackingStoreDecorator` needs forwarding. It does not for a
    separate interface, which is the same as for dashboard tokens.
- **Authentication**:
  - New `Services/IntegratorApiKeys/*` mirroring `AgentApiKeys/*`: handler, 30 s provider cache
    that fails closed, usage recorder, and service with the CAS loop. It reuses `SecretTokenFormat`.
  - Prefix `nbik_`, header `X-NimBus-Api-Key`, scheme `IntegratorApiKey`, principal
    `integrator:<keyId>`, claim `nimbus:integrator_key_id`.
  - The scheme is added to the global `AuthorizeFilter` policy's schemes on the three non-dev
    branches of `Startup.Security.cs`. It is never a default scheme.
  - The key is excluded from `/hubs/*`, `/mcp`, `/api/auth/*` and `/api/agent/*`, which keep their
    own policies.
- **Authorization**:
  - `EndpointAuthorizationService.ResolveAsync` gets a branch for a key principal. It builds
    `CurrentUserAccess` from the key's record:
    - the site role, or endpoint roles when `Endpoints` is set
    - PII from `CanReadPii`
    - no ACL lookup, no compat `groups` grant
  - Every existing `HasRoleAsync`, `CanReadPiiAsync` and `ForbidResult` check then enforces scope
    without per-route changes. `/api/admin/*` stays human-only because Owner can't be issued.
  - `HasRoleFreshAsync` re-reads the key.
- **Rate limit**: the partition key is the key id, in both the global limiter and the named policies.
- **Audit**:
  - The auditor is `integrator:<name>`, and `keyId` goes in `Data`.
  - New `MessageAuditEntity` types `IntegratorApiKeyCreated`, `IntegratorApiKeyRevoked` and
    `IntegratorApiKeyRemoved`, added to the always-recorded list in `AuditSettingsProvider`.
- **Owner API**:
  - Endpoints `/api/v1/admin/integrator-keys`: GET, POST, `/{keyId}/revoke`, DELETE `/{keyId}`.
  - Implemented in `IntegratorKeysImplementation.cs`, copying the `AgentKeysImplementation` pattern
    (Owner check, denied-write audit, CSRF token in state, `IntegratorApiKeyProblem allOf
    ProblemDetails`).
  - Add an `.Antiforgery.cs` partial and add the controller to the Admin rate-limit policy list.
- **WebApp UI**: a new Settings page, "Integrator keys", in `manage-pages.ts`, modelled on
  `agent-keys-settings.tsx`.
  - It covers: create (role, endpoint picker, PII, expiry), show the secret once, revoke and remove.
  - Capture a screenshot for the PR description using demo data on the `pr-assets` branch.
- **Docs**:
  - Add an "Integrator API keys" section to `docs/authentication.md` and the REST API doc, and add
    `X-NimBus-Api-Key` to the spec's `securitySchemes`.
  - Remove gap item 1.
- **Tests**:
  - handler: malformed, unknown, revoked, expired, success claims
  - `EndpointAuthorizationService` scope mapping: site role, endpoint-only role, PII, never Owner
  - HTTP: a Reader key gets 403 on resubmit; a Contributor key on endpoint A gets 403 on endpoint B;
    any key gets 403 on `/api/admin/*`
  - Owner API CRUD + audit rows
  - conformance for all three providers
  - Vitest for the page

---

## Verification (both PRs)

- Release builds need a temporary `global.json` pinning SDK 10.0.403, because the default SDK 11 RC2
  fails Release. Then run, in order:
  1. `dotnet build src/NimBus.slnx -c Release`: 0 CS warnings, NSwag generation enabled.
  2. `set -o pipefail; dotnet test src/NimBus.slnx -c Release --no-build`.
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`.
- `npm --prefix website ci && npm --prefix website run build` (link check).
- Live conformance for PR 2: run the SQL and Cosmos containers using the recipe in memory, or
  report them as skipped.
- Manual check against `dotnet run --project src/NimBus.AppHost`, in the browser and with curl:
  - the SPA still works on `/api/v1`
  - `curl -i /api/endpoint/status/count` returns 200 with the `Deprecation` header
  - unknown `/api/x` returns a 404 problem+json
  - CORS preflight from a configured origin
  - for PR 2: create a Reader key, call a read route with `X-NimBus-Api-Key`, and confirm a
    resubmit returns 403 problem+json
- After opening each PR, turn on auto-merge.
