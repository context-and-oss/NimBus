# NimBus WebApp REST API

The HTTP control plane for a NimBus installation. Used by the management SPA
that ships in `NimBus.WebApp`, and intended for external callers
(CI scripts, automation, partner integrations) over time.

This page is the human-readable companion to the machine-readable contract at
[`src/NimBus.WebApp/api-spec.yaml`](../src/NimBus.WebApp/api-spec.yaml). The
spec is the source of truth; this page exists to orient external integrators
and to be honest about which parts of "external use" work today versus which
are still on the roadmap.

## At a glance

- **Title** in the spec: *EIP Management Web API* — `api-spec.yaml:3`.
- **Spec version:** `0.0.1` (pre-1.0; see [Versioning + stability](#versioning--stability) below).
- **Route prefix:** every operation lives under `/api/…`.
- **Surface size:** 115 operations across 12 tags (Admin, Event, Endpoint, AccessControl, Agent, Metrics, EventType, StorageHook, Application, Message, Audit, Heartbeat).
- **Auth:** ASP.NET Identity cookie *and/or* Microsoft Entra ID (interactive browser flow + bearer-token flow). Effectively all `/api/…` endpoints require an authenticated principal. Two are reachable anonymously by design: `GET /api/app/stats` (a liveness shape that discloses nothing to an anonymous caller — see [Application](#application)) and `POST /api/storagehook/cosmos/{endpointId}` (gated by a shared webhook key rather than a principal). The agent API (`/api/agent/*`) also accepts an agent API key in `X-NimBus-Agent-Key`, which admits nothing else — see [Agent API keys](authentication.md#agent-api-keys). Beyond `/api/…`, `/health`, `/alive` and `/ready` are anonymous.
- **Schema generation:** NSwag reads `api-spec.yaml` pre-build and emits server contracts (`Controllers/ApiContract.g.cs`) plus a TypeScript client (`ClientApp/src/api-client/index.ts`) — see [Code generation](#code-generation).

## Audience

Two distinct audiences share the same surface:

1. **Operators driving the WebApp SPA.** The React app under
   `src/NimBus.WebApp/ClientApp/` calls every endpoint here, authenticated by
   the same cookie or bearer token the user signs in with.

2. **External HTTP callers.** CI scripts, partner automation, internal tools
   reaching across service boundaries. Today external use works *server-to-server*
   with an Entra ID access token; see [Calling from outside the SPA](#calling-from-outside-the-spa)
   for the supported shape and [What's not external-ready yet](#whats-not-external-ready-yet)
   for the rough edges.

## Authentication

Authentication is configured in
[`src/NimBus.WebApp/Startup.cs:60-189`](../src/NimBus.WebApp/Startup.cs). Which
branch runs depends on configuration; the four supported shapes are:

### 1. Operator — Identity cookie

When `NimBusIdentity:ConnectionString` is configured and `AzureAd:ClientId` is
not (`Startup.cs:110-120`), the WebApp runs in *Identity-only* mode: users
sign in with email + password, the server issues a cookie named
`NimBus.Identity` with an 8-hour sliding expiry, and every `/api/…` request
must carry that cookie.

This is the operator path. See [`docs/authentication.md`](authentication.md)
for password-policy, email-confirmation, and bootstrap-admin details.

### 2. Operator — Entra ID (interactive)

When `AzureAd:ClientId` is set (`Startup.cs:121-188`), the WebApp uses
Microsoft Identity Web. Browser sign-in goes through OpenID Connect;
authenticated browsers carry the standard ASP.NET cookie.

### 3. Dual — Identity *and* Entra ID

Both schemes registered concurrently (`Startup.cs:121-157`). A policy scheme
named `"Az"` inspects each request and forwards to the right handler:

- Request carries `Authorization: Bearer …` → JWT bearer.
- Request carries the `NimBus.Identity` cookie → ASP.NET Identity scheme.
- Otherwise → OpenID Connect challenge.

### 4. External — Entra ID bearer token

External callers use the JWT bearer arm of the same scheme. Acquire an Entra
ID access token via the OAuth 2.0 client-credentials flow (or on-behalf-of for
delegated automation), then attach it to every request:

```http
GET /api/endpoint/status/count HTTP/1.1
Host: nimbus.example.com
Authorization: Bearer <access-token>
```

Audience and scope come from the WebApp's `AzureAd` registration — see
[`docs/authentication.md`](authentication.md) for the app-registration setup.
The bearer arm is `JwtBearerDefaults.AuthenticationScheme`, validated by
`Microsoft.Identity.Web` (`Startup.cs:145, 176`).

### Local dev shortcut

For local development *only*, setting `EnableLocalDevAuthentication=true`
(`Startup.cs:101-109`) installs a `"LocalDev"` scheme that authenticates every
request as a fixed test user. Startup fails fast if this flag is enabled
outside the Development environment (`Startup.cs:64-71`). Never use it in
production.

## Calling from outside the SPA

The supported external shape today is server-to-server with an Entra ID bearer
token. A minimal `curl`:

```bash
TOKEN=$(curl -s -X POST \
  "https://login.microsoftonline.com/$TENANT/oauth2/v2.0/token" \
  -d "client_id=$CLIENT_ID" \
  -d "client_secret=$CLIENT_SECRET" \
  -d "scope=$NIMBUS_API_AUDIENCE/.default" \
  -d "grant_type=client_credentials" \
  | jq -r .access_token)

curl -H "Authorization: Bearer $TOKEN" \
     https://nimbus.example.com/api/endpoint/status/count
```

A bearer-token caller needs no antiforgery token. A script that instead reuses a browser's sign-in
cookie is treated as a browser session: it must `GET` an `/api/…` route first, and then send the
`NimBus.CSRF` cookie's value as the `X-NimBus-CSRF` header on writes, together with the
antiforgery cookie that came back. Otherwise writes get 400. See
[Cross-site request forgery](authentication.md#cross-site-request-forgery).

If you need a TypeScript / Python / Go client, point an OpenAPI generator at
[`src/NimBus.WebApp/api-spec.yaml`](../src/NimBus.WebApp/api-spec.yaml) — see
[Code generation](#code-generation).

## Versioning + stability

Today, **no compatibility commitment is in place.** Concretely:

- The spec lists `version: 0.0.1` (`api-spec.yaml:5`).
- Routes do not carry a `/api/v1/` prefix; every endpoint is `/api/…`
  unversioned.
- The generated TS client injects an `api-version: 2` header on every request
  ([`ClientApp/src/api-client/index.extensions.ts:45`](../src/NimBus.WebApp/ClientApp/src/api-client/index.extensions.ts))
  — this is current internal plumbing, not a versioning contract you can rely
  on.
- Individual operations may be marked `[Obsolete]` in code (e.g.
  `Controllers/ApiContract/StorageHookImplementation.cs` for the renamed
  storage hook); these signal deprecation but not removal timelines.

Practically: expect endpoints to move and rename until a `v1` cut happens.
Treat the spec as authoritative for the *shape* of each operation, the route
table as best-effort for *naming*, and pin your client against a known spec
hash rather than tracking `main`.

## Conventions

- **Format:** every request and response body is JSON. The TS client
  serialises with `JSON.stringify`; server-side serialisation uses
  `System.Text.Json` with camelCase property names.
- **Content-Type:** `application/json` on every body-bearing request.
- **Pagination:** several endpoints use a cursor-style `continuationToken` +
  `maxItemCount` pair (e.g. `/api/messages/search`,
  `/api/audits/search`). Pass the value from the previous response back in to
  fetch the next page; an empty/null token means "no more pages."
- **Errors:** *currently inconsistent.* Implementations return ad-hoc shapes
  via `ForbidResult()`, `NotFoundObjectResult()`, and similar (see
  `Controllers/ApiContract/AdminImplementation.cs:33-50`). There is no
  global `ProblemDetails` mapping yet — callers should branch on HTTP
  status code and treat the body as best-effort context. Standardising to
  `ProblemDetails` is on [the gap list](#whats-not-external-ready-yet).
- **HTTPS only:** redirected by `app.UseHttpsRedirection()`
  (`Startup.cs:404`).

## Domain reference

One subsection per OpenAPI tag, with a representative endpoint. The full
operation list lives in [`api-spec.yaml`](../src/NimBus.WebApp/api-spec.yaml);
this section is a tour, not an exhaustive enumeration.

### Endpoint

Endpoint status, sessions, and per-endpoint inspection. Example:

```bash
curl -H "Authorization: Bearer $TOKEN" \
     https://nimbus.example.com/api/endpoint/status/count
```

Returns an array of `EndpointStatusCount` rows (`api-spec.yaml:10-25`).

### Event

Inspect, resubmit, skip, and re-compose messages flowing through endpoints.
Example — resubmit a failed event by its id pair:

```bash
curl -X POST \
     -H "Authorization: Bearer $TOKEN" \
     "https://nimbus.example.com/api/event/resubmit/evt-123/msg-456"
```

(Operation id `post-resubmit-event-ids`.)

Resubmit, skip and resubmit-with-changes act on the event's latest attempt: `messageId` must be the
event's `lastMessageId`. They return **409** and send nothing when it no longer is, because the
event was resubmitted or skipped meanwhile (from the Web UI or an MCP agent) or failed again; reload
the event and retry. They return **503** when the audit row cannot be written. These commands share
one guarded path with the operator MCP tools: the caller's Contributor role is re-read from the
store, the event is claimed at the version loaded, the audit row is written, and only then is the
command published.

### EventType

Catalog of registered event types and per-endpoint event-type listings.
Example:

```bash
curl -H "Authorization: Bearer $TOKEN" \
     https://nimbus.example.com/api/event-types
```

#### Server-side schema-valid fake payloads

`GET /api/event-types/{eventtypeid}/fake` (operation id
`get-eventtypes-eventtypeid-fake`) reflects over the registered CLR type and
returns a randomized JSON payload that is guaranteed to deserialize as the
type and pass the type's own `IEvent.TryValidate` rules (the same gate the
Compose / Resubmit-with-changes submit path applies). The WebApp's
"Generate fake data" button in the Compose dialog calls this endpoint
instead of guessing values in the browser.

Request:

```bash
curl -H "Authorization: Bearer $TOKEN" \
     https://nimbus.example.com/api/event-types/CustomerRegistered/fake
```

Response shape (`200 OK`):

```json
{
  "payload": "{\n  \"CustomerId\": \"…\",\n  \"Email\": \"alex.hansen@example.com\",\n  \"FullName\": \"Alex Hansen\"\n}"
}
```

The `payload` field is a fully-formed JSON string (indented), not a
structured object — the client inserts it verbatim into the textarea. When
the type cannot be constructed (abstract, interface, or no accessible
parameterless constructor) the field is `null` and the client surfaces a
non-blocking toast. When `eventtypeid` is not registered in
`IPlatform.EventTypes` the endpoint returns `404 Not Found` with body
`"EventType not found"`. The generator strategy (seed from the authored
`static T Example`, deep-clone, randomize, validate, retry up to 5 times,
fall back to the example) lives in
`src/NimBus.WebApp/Services/FakeEventPayloadGenerator.cs`; the singleton
registration is in `Startup.cs`.

### Application

Process-wide platform stats and metadata about the running installation.

`GET /api/app/stats` is reachable without credentials so liveness probes and
status monitors keep working, but its **payload is authenticated-only** (GH#93):
environment name, exact platform version and backend topology would otherwise let
an unauthenticated scanner fingerprint the deployment.

Authenticated — the full shape:

```bash
curl -H "Authorization: Bearer $TOKEN" \
     https://nimbus.example.com/api/app/stats
```

```json
{
  "env": "Production",
  "platformVersion": "1.4.2",
  "storageProvider": "Cosmos DB",
  "ticketLinkTemplate": "https://support.example.com/browse/{ticket}"
}
```

Anonymous — 200 with an empty status object (every field null or absent):

```bash
curl https://nimbus.example.com/api/app/stats
```

```json
{ "env": null, "platformVersion": null, "storageProvider": null, "ticketLinkTemplate": null }
```

All four fields are declared optional/nullable in `api-spec.yaml`, so generated
clients accept either shape.

### Admin

Platform configuration and destructive maintenance operations (delete-by-To,
platform-config rotation, stale-Pending reconcile). The largest tag — 42
operations. Treat with appropriate care; most operations require operator
privileges.

### McpAccess

Settings → MCP access (Spec 037): `GET`/`PUT /api/admin/mcp/settings`,
`POST /api/admin/mcp/turn-off` and `GET /api/admin/mcp/activity`. Site Owner only, under the
`nimbus-admin` rate limit. The writes require the `X-NimBus-CSRF` antiforgery token that `GET`
returns, and `PUT` is fenced by the policy revision (409 when stale). See
[docs/mcp-server.md](mcp-server.md#managing-access-in-the-webapp).

### Message

Search the message history (`/api/messages/search`). Cursor-paginated.

### Failed

Unresolved failures (`Failed`, `DeadLettered`, `Unsupported`) across the endpoints the caller
holds Reader on. Three operations, each taking a `FailedSearchFilter` (endpoints, statuses,
event types, IDs, From/To, error text, UpdatedAt range):

- `POST /api/failed/search` — newest first, cursor-paginated (`maxSearchItemsCount` clamped
  to 500).
- `POST /api/failed/histogram` — counts per time bucket by status and endpoint, zero-filled.
  The bucket follows `period` (5 minutes for `1h` … one day for `30d`), or a custom
  `from`/`to` of up to 90 days with at most 60 buckets.
- `POST /api/failed/error-groups` — the Insights grouping (category, then normalized
  pattern) with each pattern's failures; up to 5,000 failures, `truncated` beyond that.

Naming an endpoint the caller cannot read returns 403. See
[Failed messages page](webapp-failed-messages.md).

### Audit

Search the per-event audit trail (`/api/audits/search`). Same pagination
shape as Message.

### Metrics

Aggregations over the configurable period window (`1h`, `12h`, `1d`, `3d`,
`7d`, `30d`). Four operations: `overview`, `latency`, `failed-insights`,
`timeseries`. Example:

```bash
curl -H "Authorization: Bearer $TOKEN" \
     "https://nimbus.example.com/api/metrics/overview?period=1d"
```

### Dev

Seed-data helpers used during local development. Not intended for production
calls. Gated by operator authentication today; expect tightening.

### StorageHook

A Cosmos DB change-feed receiver
(`POST /api/storagehook/cosmos/{endpointId}`) used to project storage events
into the resolver. Internal infrastructure surface; not for external callers.

## Code generation

The spec is authoritative; both sides of the wire are generated from it.

- **Server contracts:** NSwag emits `Controllers/ApiContract.g.cs` pre-build
  via the MSBuild target in `NimBus.WebApp.csproj:102-104`. The generated
  interfaces are implemented by hand under
  `Controllers/ApiContract/*Implementation.cs`.
- **Reference TypeScript client:** the same NSwag run emits
  `ClientApp/src/api-client/index.ts` (~338 KB; not published to npm).
  Manual overrides for auth/headers live in
  [`ClientApp/src/api-client/index.extensions.ts`](../src/NimBus.WebApp/ClientApp/src/api-client/index.extensions.ts).
- **External clients:** point any OpenAPI generator at
  `src/NimBus.WebApp/api-spec.yaml` (or the runtime copy served from the
  WebApp's `wwwroot/api-spec.yaml` in dev) to produce a client in your
  preferred language. The TS client in this repo is a usable reference.

Swagger UI is exposed only in the Development environment
(`Startup.cs:416-420`), via `app.UseOpenApi(); app.UseSwaggerUi();`. Production
hosts do not expose `/swagger` anonymously.

## CORS

The current CORS policy (`Startup.cs:399-402`) allows credentials but
restricts origins to `login.microsoftonline.com` only. Browser-based external
callers from any other origin will be blocked by the browser; that's the
intended posture for now.

External integration today should be **server-to-server**: your service makes
the HTTPS call directly to `https://<your-nimbus-host>/api/…` with the bearer
token, bypassing browser CORS entirely. If your scenario requires browser
access from a third-party origin, see
[the gap list](#whats-not-external-ready-yet).

## What's not external-ready yet

Honest list of work needed before this API can be claimed as fully
external-grade. Each item is its own focused workstream:

1. **API-key / service-principal auth — agents only.** Agents have Owner-issued,
   revocable API keys for `/api/agent/*` ([Spec 039](spec/039-agent-api-keys/spec.md)).
   The rest of the API still needs a signed-in person or an Entra ID token; a
   general integrator key path with per-caller scopes is not implemented.
2. **`ProblemDetails` error envelope (RFC 7807).** Standardise error
   responses so callers can branch on a stable schema instead of per-endpoint
   shapes. Touches every `*Implementation.cs` under
   `Controllers/ApiContract/`.
3. **Rate limiting — partial.** Four policies now protect the highest-cost
   surfaces: a concurrency limiter on `GET /api/agent/receive` and fixed
   windows on `/api/admin/*`, the two search endpoints, and
   `POST /account/login`. See [Rate Limiting](rate-limiting.md) for values,
   bounds and tuning. Every other `/api/*` route is still unthrottled — there
   is no global limiter — and enforcement is per process, so a scaled-out
   deployment multiplies effective limits by instance count.
4. **Public base URL in the spec.** `api-spec.yaml:7` lists only
   `https://localhost:5001`. A production server entry should be added so
   external generators produce clients pointing at the right host out of the
   box.
5. **SemVer cut to `v1` + route versioning.** Add `/api/v1/…` prefix (or
   document the chosen versioning strategy) and commit to a non-breaking
   change policy.
6. **External CORS allow-list.** Move from the single
   `login.microsoftonline.com` origin to a configuration-driven allow-list
   so browser-based external callers from approved origins can work.

Order in this list is roughly priority for shipping an "external" promise; the
audit that produced it lives in the planning notes for the documentation
work.

## Audit

Spec 008 (centralized audit log service) requires every privileged-action
handler in the WebApp to call `IAuditLogService.LogAuditAsync(...)` exactly
once, on both the success and access-denied branches. The service writes the
audit row to two independent sinks:

1. **Durable** — the configured `IMessageTrackingStore` (Cosmos DB or SQL
   Server). Surfaced by the existing audit-list endpoints
   (`GetMessageAudits`, `SearchAudits`) and the WebApp Audits page.
2. **Short-term operational** — Application Insights, via a structured
   `ILogger` scope. The well-known message string is
   `"Webapp AuditEvent occurred"` so KQL queries can pivot on it directly.

Both writes are best-effort: a failure to either sink is logged as a warning
and absorbed — the user's action proceeds either way. The commands that change
a message are the exception: resubmit, resubmit-with-changes, skip and report
use `IAuditLogService.LogRequiredAuditAsync`, which writes the durable row
before the command runs and refuses the command (503) if it cannot.

### Choosing which actions are recorded

A site Owner can switch individual audit types off in **Settings → Audit logging**
(`GET`/`PUT /api/admin/audit/settings`) — for example `SearchEvents`, which
fires on every list refresh. A disabled type is skipped by both sinks. Some
rows are always written regardless: access-denied attempts;
`UpdateAuditSettings`, which records every change to the selection; and the
commands that change a message (`Resubmit`, `ResubmitWithChanges`, `Skip`,
`ReportEvent` and `CommandNotSent`), which are not offered in the selection.

The selection is one record per platform (`IEndpointMetadataStore.GetAuditSettings`),
cached by each WebApp instance for 30 seconds; a save takes effect at once on
the instance that saved it. If the record cannot be read, every type is
recorded. `Comment` and `Retry` rows — event history written directly by the
event trail and the Resolver — are not governed by the setting.

### Actions audited

The `MessageAuditType` enum enumerates the audit-worthy actions and the
controller method that fires each one:

| `AuditType`           | Triggered by                                              |
|-----------------------|-----------------------------------------------------------|
| `Resubmit`            | `EventImplementation.PostResubmitEventIdsAsync`           |
| `ResubmitWithChanges` | `EventImplementation.PostResubmitWithChangesEventIdsAsync`|
| `Skip`                | `EventImplementation.PostSkipEventIdsAsync`               |
| `CompleteHandoff`     | `EventImplementation.PostHandoffCompleteAsync`            |
| `FailHandoff`         | `EventImplementation.PostHandoffFailAsync`                |
| `SearchEvents`        | `EventImplementation.PostApiEventEndpointIdGetByFilterAsync` — filter passed as `Data` |
| `GetEventDetails`     | `EventImplementation.GetEventDetailsIdAsync`              |
| `GetEndpointDetails`  | `EndpointImplementation.GetEndpointStatusCountIdAsync`    |
| `EnableEndpoint`      | `EndpointImplementation.PostEndpointSubscriptionstatusAsync` (body="enable")  |
| `DisableEndpoint`     | `EndpointImplementation.PostEndpointSubscriptionstatusAsync` (body="disable") |
| `PurgeMessages`       | `EndpointImplementation.PostEndpointPurgeAsync`, `AdminImplementation.PostAdminSessionPurgeAsync`, `AdminImplementation.PostAdminPurgeAsync` |
| `Compose`             | `EventImplementation.PostComposeNewEventAsync`            |
| `ReconcileStalePending` | `AdminImplementation.PostAdminStalePendingReconcileAsync` — one row per invocation (request + counts as `Data`) plus one per repaired event |
| `UpdateAuditSettings` | `AdminImplementation.PutAdminAuditSettingsAsync` — the disabled types as `Data`; always recorded |
| `CommandNotSent`      | `OperatorCommandCoordinator` — a resubmit or skip was audited but publishing it failed, so the event was restored; always recorded |
| `UpdateMcpSettings`   | `McpAccessImplementation.PutAdminMcpSettingsAsync` / `PostAdminMcpTurnOffAsync` — the revisions and the list of changes as `Data`; always recorded |
| `McpAccessRefused`    | The operator MCP endpoint — a call refused by the MCP access policy or for a missing scope, with `AccessDenied` set; deduplicated per caller, client, reason and tool for 5 minutes per instance |

Resubmit, skip and resubmit-with-changes run through `OperatorCommandCoordinator`, shared with the
operator MCP tools. Their `Data` records the channel (`WebApp` or `Mcp`), the reason and idempotency
key an MCP caller gave, the client application, the prior status and the message version.

`Retry` and `Comment` remain on the enum for backward compatibility with rows
written before spec 008.

### Audit row shape

Each row is a `MessageAuditEntity`:

```
AuditorName     // resolved from "name" → ClaimTypes.Name → preferred_username → "anonymous"
AuditTimestamp  // DateTime.UtcNow at the moment LogAuditAsync was called
AuditType       // enum value (stored as string for forward compatibility)
Comment         // operator-typed free text (null on the new code path)
AccessDenied    // true when the authorization layer rejected the attempt
Data            // structured action context (e.g. search filter JSON), truncated to ~4 KB
EventId         // mirrors the eventId argument; null for endpoint-scoped actions
EndpointId      // mirrors the endpointId argument
```

### App Insights query template

```kql
traces
| where message == "Webapp AuditEvent occurred"
| project timestamp,
          AuditType        = tostring(customDimensions.AuditType),
          AuditorName      = tostring(customDimensions.AuditorName),
          EventId          = tostring(customDimensions.EventId),
          EndpointId       = tostring(customDimensions.EndpointId),
          AccessDenied     = tobool(customDimensions.AccessDenied),
          Data             = tostring(customDimensions.Data)
| order by timestamp desc
```

### Guarantees and non-goals

- **Best-effort, not transactional.** A failure to write the audit row does
  not fail the user action. See spec 008 NFR-002 and User Story 5.
- **One row per event for bulk actions.** Not one row per batch — matches
  the audit-list UI's per-event granularity.
- **No PII masking inside the service.** Callers MUST pass already-masked
  content via the `Data` parameter; masking is an upstream concern.

## See also

- [`docs/architecture.md`](architecture.md) — system-level shape and where
  the WebApp sits.
- [`docs/authentication.md`](authentication.md) — credential plumbing,
  Identity / Entra setup, bootstrap admin.
- [`src/NimBus.WebApp/api-spec.yaml`](../src/NimBus.WebApp/api-spec.yaml) —
  the machine-readable contract this page describes.
- [`src/NimBus.WebApp/Startup.cs`](../src/NimBus.WebApp/Startup.cs) — auth,
  CORS, and Swagger-UI configuration.
