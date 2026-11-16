# Spec 037 — Admin-managed MCP access

Status: **implemented** (2026-10-07). [Spec 052](../052-mcp-agent-api-keys/spec.md) later added the
**Agent API keys** switch to this policy. Built on branch `claude/mcp-admin-config-mockup-63fed4`, all three
slices together; §16 records what the implementation resolved and where it differs from this
text. Not yet piloted in Azure. [Spec 035](../035-mcp-operator-access/spec.md#phase-3-as-built)
Phase 3 later added three capabilities to this policy (settle pending handoffs, recover deferred
messages, resubmit with an edited payload); unlike the capabilities in §5.2 they default to off,
also in a policy saved before they existed. The mockup is
[admin-mockup.html](admin-mockup.html). Its bottom bar switches between the four states in §6.1,
and its Design notes summarize §5.
Baseline: master `023891a9`. Builds on [Spec 035](../035-mcp-operator-access/spec.md) Phase 1
(Observe) and Phase 2a (Operate).
Scope: `NimBus.WebApp` (`Mcp/`, `RateLimiting/`, the Admin API in `api-spec.yaml`, a new Admin
tab in `ClientApp/`), `NimBus.MessageStore.Abstractions` (a singleton record on
`IEndpointMetadataStore` and two audit types), the Cosmos DB, SQL Server and in-memory providers
with the conformance suite, and docs. Unchanged: the MCP tool contracts, the Entra app
registration, Phase 2a's action tokens and state guard, and the meaning of every `NimBus__Mcp__*`
and `RateLimiting__*` setting.
Why: today the only ways to turn `/mcp` off or keep agents away from a tool are an app-setting
change plus a restart, or an Entra change that needs tenant admin rights. A site Owner who sees an
agent misbehave has no control over it in NimBus.

## 1. Summary

Add **Admin → MCP access**, a site Owner tab with three jobs:

- turn the operator MCP endpoint on and off;
- restrict which tools agents may use, for whom, through which client applications, on which
  endpoints and how often;
- show how to connect an agent and what agents have done.

The deployment remains the trust anchor and sets the ceiling. The tab stores a policy that can
only narrow it. A change reaches every instance within 30 seconds, without a restart. **Turn off
now** stops all agent access in one step. With no saved policy, behaviour is exactly as today.

## 2. Current state

Facts at the baseline:

- `/mcp` exists only when `NimBus:Mcp:Enabled` is true with an Entra registration, or when
  `EnableForLocalDevelopment` is true and the local-dev bypass is on. `McpAuthenticationModeResolver`
  decides this once at startup (`src/NimBus.WebApp/Mcp/McpAuthenticationMode.cs`), and
  `MapNimBusOperatorMcp` maps nothing otherwise.
- Every call passes the `NimBusMcpObserve` policy: the delegated scope `nimbus.observe` or the app
  role `Nimbus.Observe` (`McpOperatorAuthorization.cs`). Changing a message needs the action's
  delegated scope and Contributor on the endpoint (`OperatorActionAccess`). Payloads need PiiReader
  and `nimbus.payload.read` (`OperatorPayloadAccess`). A workload token carries no `scp`, so it can
  never change a message. In local development, scopes are skipped.
- `OperatorEndpointCatalog` resolves the endpoints the caller may read.
  - Tools that take an endpoint id call `RequireReadableAsync`.
  - `nimbus_list_endpoints`, `nimbus_get_overview`, `nimbus_get_capabilities` and
    `nimbus_get_session` use `GetReadableEndpointIdsAsync`.
  - `nimbus_search_messages` and `nimbus_get_metrics` require Site Reader, read across all
    endpoints and don't consult the catalog.
- `McpRequestGuardMiddleware` runs before authentication. `Startup.Pipeline.cs` orders it as the
  guard, then `UseAuthentication`, `UseAuthorization`, then `UseRateLimiter`. The guard enforces
  `AllowedOrigins` and, in local development, loopback-only callers.
- Rate limits: `RateLimiting:Mcp` (60 per 60 s) applies to the route. `RateLimiting:McpMutations`
  (5 per 60 s) applies in `OperatorMutationLimiter`, which the resubmit, skip, report and classify
  tools call. Both are read once at startup. Partition key: `mcp:{tid}:{azp|appid}:{oid|sub}`
  (`RateLimitingServiceCollectionExtensions.McpPartitionKey`). The rate limiter runs after
  authorization, so calls that authorization refuses are not counted.
- Audit: MCP commands write rows whose `Data` JSON holds `channel` (`"Mcp"`), `reason`,
  `idempotencyKey` and `clientId` (`OperatorCommandCoordinator`). Role denials in the coordinator
  are audited with `accessDenied`. The `[PermissionDenied]` errors from
  `OperatorActionAccess.RequireScope` (missing scope) are not audited. `AuditFilter` has no channel
  filter. The auditor is the `name` claim, which is not unique.
- Settings precedents:
  - `AuditSettings` is a singleton on `IEndpointMetadataStore`: a fixed-id Cosmos document in the
    settings container, and on SQL Server the table from `0022_AuditSettings.sql`.
    `AuditSettingsProvider` reads it through a 30-second cache. Changes apply live, and a failed
    read fails open.
  - The Failure intelligence settings (`IntelligenceSettingsStore`) are revision-fenced: a
    conditional `UPDATE` on SQL Server, `IfMatchEtag` on Cosmos. Writes are protected by the
    `X-NimBus-CSRF` antiforgery header. They need a restart and fail closed.
- `ModelContextProtocol.AspNetCore` 2.2.0 provides
  `WithRequestFilters(b => b.AddListToolsFilter(...).AddCallToolFilter(...))`.

## 3. Goals

1. A site Owner can turn agent access off and on from the WebApp. The change reaches every
   instance within 30 seconds, with no restart.
2. A site Owner can switch off individual capabilities: raw payloads, marking messages reported,
   classify, resubmit, skip, and workload (app-only) tokens.
3. A site Owner can restrict, for example for a pilot:
   - which people and which client applications may connect;
   - which endpoints agents can see;
   - on which endpoints agents may change messages.
4. A site Owner can lower the MCP rate limits below the deployment's values.
5. The tab shows, read-only:
   - the deployment's MCP configuration;
   - what a given kind of caller can do;
   - how to connect an agent;
   - recent agent activity.
6. Nothing on the tab can make NimBus trust another tenant, or allow more than the Entra scopes,
   NimBus roles and deployment settings allow.
7. Existing deployments behave exactly as today until a policy is saved.

## 4. Non-goals

- Editing any of these: the Entra tenant, client id, App ID URI or instance; allowed browser
  origins; `NimBus__Mcp__Enabled` or `EnableForLocalDevelopment`. Raising a rate limit.
- Granting Entra scopes, consent or app assignments, or NimBus roles (Access Control does that).
- Tool policies per person or group beyond the people allowlist. Per-client policies beyond "may
  change messages".
- Letting workloads change messages or read payloads (Spec 035 Phase 2b).
- A daily change budget per caller. The mockup's first draft had one; it is deferred (§12).
- Tracking or counting read-only tool calls.
- New MCP tools, or changes to Phase 2a semantics (action tokens, state guard, idempotency).

## 5. Design

### 5.1 Two layers

| Layer | Owns | Changed by |
| --- | --- | --- |
| Deployment: ceiling and trust anchor | `NimBus__Mcp__Enabled`, `NimBus__Mcp__EnableForLocalDevelopment`, `NimBus__Mcp__Entra__*`, `NimBus__Mcp__AllowedOrigins__*`, `RateLimiting__Enabled`, `RateLimiting__Mcp*` | App settings and a restart (needs Azure access) |
| Admin policy | Everything in §5.2 | A site Owner in Admin → MCP access, live |

A call proceeds only when all of these allow it:

> deployment ceiling ∧ admin policy ∧ Entra scope or app role ∧ NimBus role on the endpoint ∧
> message state

The admin policy only removes permissions. A hijacked Owner session can turn things off or narrow
them, and can widen them again up to the ceiling (§7). It cannot change who can obtain a token.

When the deployment has not enabled MCP, the tab shows setup steps (§6.3). A policy can still be
saved, and it applies once the endpoint exists.

### 5.2 Policy model

A new class, `McpAccessSettings`, in `NimBus.MessageStore.Abstractions/States/`. It uses Newtonsoft
attributes, like `AuditSettings`.

| Field | Type | Default when nothing is saved | Meaning |
| --- | --- | --- | --- |
| `Id` | string | `"McpAccessSettings"` | Singleton id |
| `Revision` | string? | null | A new GUID on every save. Null means never saved |
| `UpdatedBy`, `UpdatedAtUtc` | string?, DateTime? | null | Display only |
| `Enabled` | bool | true | Serve `/mcp`, within the ceiling |
| `Capabilities.Payloads` | bool | true | `nimbus_get_message` with `includePayload=true` |
| `Capabilities.Report` | bool | true | `nimbus_set_message_reported` |
| `Capabilities.Classify` | bool | true | `nimbus_classify_failure` |
| `Capabilities.Resubmit` | bool | true | `nimbus_resubmit_message`, and `nimbus_prepare_action` for resubmit |
| `Capabilities.Skip` | bool | true | `nimbus_skip_message`, and `nimbus_prepare_action` for skip |
| `AllowWorkloads` | bool | true | Accept app-only tokens (`Nimbus.Observe`) for the read tools |
| `People.Mode` | `All` \| `Listed` | `All` | Who may connect |
| `People.Principals` | list of {`Principal`, `Label?`} | [] | An email, user object id or group object id: the same forms as Access Control grants |
| `Clients.Mode` | `Any` \| `Approved` | `Any` | Which client applications may connect |
| `Clients.Approved` | list of {`ClientId`, `Name`, `MayChange`} | [] | `ClientId` is matched against the `azp` claim, then `appid` |
| `Endpoints.Visibility` | `All` \| `AllExcept` | `All` | Which endpoints agents can see |
| `Endpoints.Hidden` | list of endpoint ids | [] | Used when `AllExcept` |
| `Endpoints.Changes` | `AllVisible` \| `Listed` | `AllVisible` | Where agents may change messages |
| `Endpoints.ChangeOn` | list of endpoint ids | [] | Used when `Listed` |
| `Limits.RequestsPerWindow` | int? | null | Null means the deployment value |
| `Limits.MutationsPerWindow` | int? | null | Null means the deployment value |

The defaults reproduce today's behaviour. The read tools have no switch: the endpoint has no
purpose without them.

Validation returns 400 with one code per rule, and the UI checks the same rules:

- `Listed` people needs 1–100 principals. Each is trimmed and is either a valid email address or a
  GUID, at most 256 characters. Entries are unique, case-insensitively. A label is at most
  100 characters.
- `Approved` clients needs 1–50 entries. Each `ClientId` is a unique GUID. `Name` is 1–100
  characters.
- `AllExcept` needs at least one hidden endpoint. `Listed` changes needs at least one endpoint,
  and none of them may be hidden. Every id must exist in the platform catalog. Ids match
  case-insensitively and are stored in the catalog's spelling. Each list holds at most 200 ids.
- A limit is null or between 1 and the deployment's value. When rate limiting is off in the
  deployment, both limits must be null. Window lengths are not configurable and stay the
  deployment's.

### 5.3 Storage

Two methods on `IEndpointMetadataStore`, next to `GetAuditSettings` and `SetAuditSettings`:

```csharp
/// <summary>The saved policy, or the defaults with a null Revision when none was saved.</summary>
Task<McpAccessSettings> GetMcpAccessSettings();

/// <summary>
/// Saves <paramref name="settings"/> when the stored revision equals
/// <paramref name="expectedRevision"/>. Null means: only when nothing is stored yet.
/// <paramref name="settings"/>.Revision must already hold the new revision.
/// </summary>
/// <returns>False on a revision conflict.</returns>
Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision);
```

- **Cosmos DB:** a fixed-id document in the settings container that already holds
  `AuditSettings`. The provider reads the item, checks its revision, then replaces it with
  `IfMatchEtag`. It creates the item when `expectedRevision` is null. `PreconditionFailed` and
  `Conflict` return false, as in `IntelligenceSettingsStore`.
- **SQL Server:** migration `0023_McpAccessSettings.sql` adds table `McpAccessSettings`
  (`Id varchar(64)` primary key, `Revision varchar(36)`, `SettingsJson nvarchar(max)`,
  `UpdatedAtUtc datetime2`) with at most one row. A save is one batch: a conditional
  `UPDATE … WHERE Revision = @expected`, or an `INSERT` guarded against an existing row. Add the
  table to `RequiredTables` in `SqlServerSchemaInitializer`.
- **In-memory** (`NimBus.Testing/Conformance/InMemoryMessageStore.cs`): a field guarded by a lock.
- **Forwarding:** `CosmosDbClient` and `SqlServerMessageStore` forward both methods. No
  `InstrumentingMessageTrackingStoreDecorator` change is needed: it wraps `IMessageTrackingStore`,
  which does not extend `IEndpointMetadataStore`.
- **Conformance** (`EndpointMetadataStoreConformanceTests`):
  - the defaults come back with a null revision;
  - creating works only when nothing is stored;
  - every field round-trips;
  - a stale revision returns false and leaves the record unchanged;
  - of two writers using the same expected revision, exactly one succeeds.

Every provider implements both members. Neither is a default interface method (AGENTS.md).

### 5.4 Policy provider

`IMcpAccessPolicyProvider` is a singleton in `src/NimBus.WebApp/Mcp/Access/`:

- `Task<McpAccessPolicy> GetAsync()` re-reads the record at most every 30 s after a successful
  read and every 5 s after a failed one, like `AuditSettingsProvider`.
- `McpAccessPolicy? Current` returns the last loaded snapshot without I/O. The synchronous
  rate-limit partition callbacks use it (§5.6).
- `McpAccessPolicy` is immutable: the saved record narrowed by the deployment ceiling. Each limit is
  the lower of the saved and deployment values.
- A save or turn-off through the API replaces this instance's snapshot at once. Other instances
  catch up within the 30-second TTL, which is why the UI says "within 30 seconds".
- **Fail closed:** after one successful load, a failed read keeps the last snapshot. Until the
  first successful load, `/mcp` answers
  `503 [Unavailable] The MCP access policy could not be loaded.` and the provider retries every
  5 s. It never falls back to the defaults, because they may be wider than the saved policy. Audit
  settings fail open; this provider deliberately does the opposite.
- In `Disabled` mode the provider is not registered, and nothing reads the record.

### 5.5 Enforcement

| Rule | Where | Effect |
| --- | --- | --- |
| `Enabled` is false | `McpRequestGuardMiddleware`, before authentication | `503` with `text/plain` `[Disabled] MCP access is turned off by an administrator.`, shaped like the guard's existing 403 answers. Not audited. `/.well-known/oauth-protected-resource/mcp` keeps working. |
| People, clients, workloads | A new `McpAccessRequirement` and handler on the `NimBusMcpObserve` policy, Entra mode only | `403` and an `McpAccessRefused` audit row (§5.7) |
| Capabilities | `AddListToolsFilter` hides the tool. `AddCallToolFilter` refuses a call with `[PermissionDenied] <tool> is turned off by an administrator.` | Also ANDed into `OperatorActionAccess.HasScope` and `OperatorPayloadAccess.CanReadPayloadsAsync`, so `eligibleActions`, `permittedActions` and payload checks agree with the tool list |
| A client's `MayChange` is false | The same filters, plus `OperatorActionAccess`, per request | The change tools are hidden from that client and refused |
| Hidden endpoints | `OperatorEndpointCatalog`, plus `nimbus_search_messages` and `nimbus_get_metrics` | Left out of `GetReadableEndpointIdsAsync`. `RequireReadableAsync` throws `[EndpointNotFound]`, the same answer as for an endpoint the caller cannot read. The two Site Reader tools, which don't use the catalog today, drop hidden endpoints from their results and from their endpoint filters |
| Change endpoints | `OperatorActionAccess.MayAsync` and `EligibleActionsAsync` | `[PermissionDenied] Changes are not allowed on this endpoint over MCP.` |
| Limits | The route policy in `RateLimitingServiceCollectionExtensions`, and `OperatorMutationLimiter` | The effective limit, which is also part of the partition key (§5.6) |

Which tools are hidden from `tools/list`:

| Tool | Hidden when |
| --- | --- |
| The 11 read tools | Never. When the endpoint is off, no call reaches the tool list. |
| `nimbus_prepare_action` | Both Resubmit and Skip are off, or the client may not change messages. A call for an action that is off is refused. |
| `nimbus_resubmit_message` | Resubmit is off, or the client may not change messages |
| `nimbus_skip_message` | Skip is off, or the client may not change messages |
| `nimbus_set_message_reported` | Report is off, or the client may not change messages |
| `nimbus_classify_failure` | Classify is off, or the client may not change messages |

- **Payloads** have no tool of their own. When Payloads is off, `nimbus_get_message` with
  `includePayload=true` takes the existing path for a caller without payload access.
- **Workloads:** a token without an `scp` claim is app-only. With `AllowWorkloads` false, the
  requirement refuses it.
- **Local development:** the caller is always the Local Developer and has no client id, so the
  people, client and workload rules are not evaluated. The endpoint switch, capabilities, endpoint
  rules and limits apply.
- **`nimbus_get_capabilities`** reports the effective limits.

### 5.6 Limits

- **Route limit:** the policy callback reads `IMcpAccessPolicyProvider.Current` from
  `HttpContext.RequestServices` and uses the effective limit as the permit count.
- **The limit is part of the partition key**
  (`mcp:{tid}:{client}:{caller}:{limit}`), so a changed limit gets a new fixed-window limiter
  instead of the cached one. The rate limiter discards the idle ones.
- **Mutation limit:** `OperatorMutationLimiter` does the same.
- **Rate limiting off:** with rate limiting off in the deployment, nothing changes.

### 5.7 Audit

Two values appended to `MessageAuditType`, after `CommandNotSent`:

- **`UpdateMcpSettings`:** a saved policy or a turn-off. `Data` holds
  `{ revision, previousRevision, turnOff, changes: [{ text, widens }] }`.
- **`McpAccessRefused`:** a call refused by the admin policy, or by the existing missing-scope
  check in `OperatorActionAccess.RequireScope`. It is written with `accessDenied: true`. `Data`
  holds `{ channel: "Mcp", reason: "endpoint|person|client|workload|tool|scope", tool?, clientId?,
  endpointId? }`. Refused calls are not rate-limited (§2), so each instance writes at most one row
  per tenant, caller, client, reason and tool every 5 minutes.

Both types are added to `AuditSettingsProvider.AlwaysRecorded`, which also keeps them off the
Admin → Audit list. `formatAuditType` in `ClientApp/src/functions/audit.functions.ts` gets labels
for them.

### 5.8 API

The routes are defined in `api-spec.yaml` and generated by NSwag. All of them:

- are for site Owners only;
- use the admin rate-limit policy;
- validate the `X-NimBus-CSRF` antiforgery header on writes, through `[AutoValidateAntiforgeryToken]`
  on the generated controller (see §16);
- cap request bodies at 64 KB.

**`GET /api/admin/mcp/settings`** returns `McpAccessState`:

- `saved`: the record, whose `revision` is null when nothing was saved.
- `effective`: what this instance enforces, and `policyLoaded`.
- `deployment`: `mode` (`Disabled`, `LocalDevelopment` or `Entra`), `endpointUrl`,
  `resourceMetadataUrl`, `serverVersion`, `tenantId`, `clientId`, `applicationIdUri`, `authority`,
  `allowedOrigins`, `rateLimitingEnabled`, `requestLimit {permit, windowSeconds}` and
  `mutationLimit {permit, windowSeconds}`. The URLs are built from the request host, as the
  protected-resource metadata is. Nothing here is secret.
- `failureIntelligenceEnabled` (for the Classify row) and `csrfToken`.

**`PUT /api/admin/mcp/settings`** takes `{ revision, settings, confirmWidening }`. It answers:

- 200 with the new state;
- 400 with the validation codes;
- 400 `ConfirmationRequired` when the server classes a change as widening (§6.4) and
  `confirmWidening` is false;
- 409 when the revision is stale;
- 403 for anyone who is not a site Owner. The refusal is audited as `UpdateMcpSettings` with
  `accessDenied`.

**`POST /api/admin/mcp/turn-off`** takes no body; the antiforgery header is still required. It
answers 200 with the new state. It reads the current record, sets `Enabled` to false and saves it
with that record's revision, retrying up to three times on a conflict. It also works when no record
exists. It never fails because the editor's page is out of date.

**`GET /api/admin/mcp/activity?hours=24`** takes `hours` from 1 to 168 and returns:

- `summary`: `actions`, `actionsByType`, `refused` and `refusedByReason`;
- `items`: the newest 50 rows;
- `refusedClients`: `[{ clientId, calls, lastAuditor, lastAtUtc }]`.

It runs `SearchAudits` for each relevant audit type (`Resubmit`, `Skip`, `ReportEvent`,
`FailureClassified`, `McpAccessRefused`, `UpdateMcpSettings`) with `CreatedAtFrom` set. It then
keeps only rows whose `Data.channel` is `"Mcp"`; settings rows are always kept. Each query is
capped at 500 rows, and the summary says when a cap was hit.

The endpoint can be turned on only through `PUT`, with confirmation.

## 6. UI: Admin → MCP access

The tab follows the mockup.

- **Component:** `ClientApp/src/components/admin/mcp-access-settings.tsx`, with a test. It is added
  to `pages/admin.tsx` as the last tab.
- **Building blocks:** the existing primitives (`Card`, `Toggle`, `Select`, `Input`, `Button`).
- **Conventions:** the review and save flow, and the CSRF handling, of
  `failure-intelligence-settings.tsx`.

### 6.1 Layout and states

**Header:**
- the purpose of the tab;
- "Revision N · saved by X, time · applies to every instance within 30 s, no restart", or a note
  that nothing is saved yet;
- the **Turn off now** button, shown while the effective policy serves the endpoint. It opens a
  dialog, and confirming sends `POST /turn-off`.

**Banners:**

| State | Banner |
| --- | --- |
| Serving (Entra) | None |
| Turned off | Who turned it off and when; the other settings were kept |
| Not set up (`mode == Disabled`) | Setup steps (§6.3) |
| Local development | No sign-in, loopback only, scopes not checked, people and client rules not used |
| Policy not loaded (`effective.policyLoaded` false) | Agents get `503 [Unavailable]` until shared storage is reachable |

**Tiles ("Active now"):**
- Endpoint: Serving, Off or Not set up, with the server version;
- Sign-in: the Entra tenant, or Local developer;
- Agent actions · 24 h and Refused · 24 h, from the activity endpoint.

**01 Activation:**
- the switch;
- the endpoint and resource-metadata URLs, each with Copy;
- deployment checks;
- a collapsed, read-only "Sign-in (managed by deployment)" section that explains why it can't be
  edited here.

**02 What agents may do:**
- the access formula from §5.1;
- Observe, always on;
- one row per capability, showing its tools, Entra scope, NimBus role and risk;
- warnings when Payloads or Skip is on;
- on Classify, whether Failure intelligence is enabled;
- the workloads switch, disabled in local development.

**03 Who and where:**
- People: All or Listed, with a chip editor;
- Clients: Any or Approved. Approved clients are listed in a table with a "may change messages"
  switch. There is an Approve dialog, and suggestions to approve come from `refusedClients`;
- Endpoints: what agents can see and where they may change messages, chosen from the catalog.

People and Clients are disabled in local development.

**04 Limits:** two number inputs, each capped at the deployment value. They are read-only when rate
limiting is off.

**Side column:**
- **Effective access:** a preview for four kinds of caller (Reader, Contributor, Contributor +
  PiiReader, Workload). The browser computes it from the draft and labels it a preview; the server
  is authoritative.
- **Connect an agent:**
  - Entra: the `claude mcp add` command with the chosen approved client id, plus the URL, client
    id, scopes and redirect for other clients;
  - local development: `.mcp.json`.
- **Recent agent activity**, with a link to the Audit Log.

**Footer:** the number of unsaved changes, **Discard changes** and **Review changes**.

The mockup's endpoint-tile "last call" time, the "people today" count and the "last valid token"
check are not specified. They would need read calls to be tracked, which §4 excludes.

### 6.2 Save flow

1. **Review:** lists every change and marks it "widens" or "narrows" (§6.4).
2. **Confirm:** a widening change needs a checkbox. Turning Skip on uses the stronger wording from
   the mockup.
3. **Save:** sends `PUT` with `confirmWidening`.

- **Another editor saved first (409):** "Another administrator changed these settings. Reload
  before reviewing again."
- **Success:** a toast shows the new revision.

### 6.3 Not set up

The banner lists three steps:

1. Register the MCP resource, with a link to `docs/mcp-server.md`.
2. Set the three app settings with a copyable `az webapp config appsettings set` command.
3. Restart.

The form stays editable; only the endpoint switch is disabled.

### 6.4 Widening rules

A change widens access when it:

- turns the endpoint on;
- turns on a capability or `AllowWorkloads`;
- changes People from Listed to All, or adds a principal to a Listed list;
- changes Clients from Approved to Any, adds a client, or lets a client change messages;
- shows a hidden endpoint again, or switches visibility from AllExcept to All;
- changes Changes from Listed to AllVisible, or adds an endpoint to `ChangeOn`;
- raises a limit, or resets it to the deployment value.

Every other change narrows access. The server uses the same rules for `ConfirmationRequired` and
for the audit `changes` list.

### 6.5 Phone width

The tab uses the same full-width layout as Failure intelligence, with no horizontal scrolling at
390 px.

## 7. Security evaluation

- **Trust anchor:** the tenant, audience and issuer stay deployment-only.
- **Narrow-only:** every rule is ANDed onto the existing checks. No code path lets the policy grant
  anything.
- **Compromised Owner session:** it can widen access up to the ceiling. Mitigations: the
  confirmation step, the always-recorded audit row with its list of changes, and the deployment
  ceiling. The exposure matches Access Control, where an Owner can grant roles.
- **Store failure:** handled fail-closed (§5.4).
- **CSRF:** the antiforgery header on writes, and the admin rate limit.
- **Information disclosure:**
  - `GET` returns non-secret deployment identifiers, to Owners only.
  - The `503 [Disabled]` answer reveals only that `/mcp` exists, which the metadata document
    already does.
- **Log flooding:** refused calls are not rate-limited, so their audit rows are deduplicated
  (§5.7).
- **Group overage:** a user in too many groups gets no `groups` claim. A group entry then doesn't
  match them, so they are refused. This is safe, and is documented.

## 8. Tests

Add each failing test first.

- **Storage (conformance, all three providers):** the cases in §5.3. The SQL Server and Cosmos runs
  need `NIMBUS_SQL_TEST_CONNECTION` and the Cosmos variables. CI rejects skipped conformance tests.
- **Policy provider:**
  - TTL;
  - last-known-good after a failed read;
  - `503 [Unavailable]` before the first load;
  - a local save refreshes the cache at once;
  - limits are narrowed to the deployment values.
- **Guard middleware:**
  - `[Disabled]` and `[Unavailable]` answers;
  - an enabled endpoint passes;
  - the metadata endpoint is unaffected;
  - origin and loopback checks still come first.
- **Access requirement:**
  - people matched by email, object id and group;
  - Approved and Any clients;
  - workloads;
  - the requirement is not evaluated in local development;
  - one refusal audit row per deduplication window.
- **Tool filters and access:**
  - `tools/list` hides tools that are off, and hides change tools from a client that may not change
    messages;
  - a direct call to a hidden tool gets `[PermissionDenied]` and an audit row;
  - `nimbus_prepare_action` follows its rules;
  - `eligibleActions` and `permittedActions` match the policy;
  - the Payloads switch is ANDed into payload access;
  - change-endpoint rules apply.
- **Endpoint visibility:**
  - a hidden endpoint answers `[EndpointNotFound]` in every tool that takes an endpoint id;
  - hidden endpoints are left out of list, overview, search and metrics results.
- **Limits:**
  - a lowered limit applies after the TTL;
  - saving above the deployment value is rejected;
  - the limit is part of the partition key;
  - nothing changes when rate limiting is off.
- **API:**
  - only site Owners (403, audited);
  - antiforgery checked;
  - validation codes;
  - 409 on a stale revision;
  - `ConfirmationRequired`;
  - turn-off ignores the editor's revision and works when nothing is saved;
  - `UpdateMcpSettings` rows list the changes;
  - activity keeps only MCP rows and reports when a cap is hit.
- **Compatibility:** with no saved record and `NimBus:Mcp:Enabled=true`, the tool list, permitted
  actions and limits equal today's. Pin this with a regression test.
- **Frontend (Vitest):**
  - each banner state;
  - the unsaved-change count;
  - the widening and narrowing labels in the review;
  - saving is blocked until the checkbox is ticked;
  - the turn-off dialog;
  - the 409 message;
  - People and Clients disabled in local development;
  - the request body's shape.
- **Gates:**
  - `dotnet build src/NimBus.sln -c Release` and `dotnet test src/NimBus.sln -c Release --no-build`;
  - `npm --prefix src/NimBus.WebApp/ClientApp run test:ci`, then `run build`;
  - a build with NSwag generation enabled, not `SkipSpaBuild`.
- **Azure pilot:** turn MCP off from the tab. An agent gets `503 [Disabled]` within 30 seconds on
  every instance. Turn it back on and the agent works again.

## 9. Phases

Each slice ships on its own. The PR that adds the tab includes a screenshot (AGENTS.md).

| Slice | Contents | PRs |
| --- | --- | --- |
| 0 | Verify §14 | none |
| A: enable and configure | §5.2–§5.4; the endpoint switch, capabilities and workloads (§5.5); `UpdateMcpSettings`; `GET`/`PUT` settings and `POST` turn-off; the tab with cards 01 and 02, Effective access and Connect an agent | 1: storage and conformance. 2: provider, enforcement and API. 3: UI |
| B: who, and activity | People and clients (`McpAccessRequirement`); `McpAccessRefused` with deduplication; the activity endpoint, tiles and panel; refused-client suggestions; card 03's first two sections | 4: backend. 5: UI |
| C: where, and how often | Endpoint visibility and change scope; limits that can only be lowered; card 03's endpoint section and card 04 | 6: backend and UI |

Slice A alone covers turning MCP on and configuring what it may do from the Admin area.

## 10. Documentation

- `docs/mcp-server.md`: a new section, "Managing access in the WebApp". It covers the two layers,
  precedence, how quickly changes apply, Turn off now and fail-closed behaviour. Add `[Disabled]`
  and `[Unavailable]` to the list of errors.
- `docs/rate-limiting.md`: limits that the Admin tab can only lower.
- `docs/webapp-rest-api.md`: the four routes, next to the other `/api/admin/*` routes.
- Spec 035's status line: a link to this spec.
- Release notes, following `docs/versioning.md#cutting-a-release`.

## 11. Residual risks

- **Propagation window:** another instance can serve calls under the old policy for up to
  30 seconds. A command that has already written its audit row completes after a turn-off.
- **Preview drift:** the browser's Effective access preview can disagree with the server. It is
  labelled as a preview, and the tests cover the server rules.
- **Activity cost:** the activity endpoint parses `Data` in memory and caps its queries, so on a
  busy site a 7-day view can be incomplete. The summary says so.
- **Widening by an Owner:** an Owner can widen access within the ceiling (§7).

## 12. Alternatives considered

- **Editable Entra settings in Admin:** rejected. They are the trust anchor; changing them should
  need Azure access.
- **Restart-required, like Failure intelligence:** rejected. A kill switch must take effect at
  once, and nothing here changes service registration.
- **Removing the route at runtime:** rejected. Endpoint routing is built at startup, and a gate in
  the existing guard middleware is simpler.
- **A separate settings store, like `IntelligenceSettingsStore`:** rejected in favour of the
  `IEndpointMetadataStore` singleton pattern. Its revision fence is reused.
- **Restricting people and clients only in Entra** (assignment required, pre-authorization):
  possible today, but it needs Entra admin rights. Both can be used together; the tab gives NimBus
  Owners a narrower control.
- **Fail open when the store is unreadable:** rejected. It could quietly undo a restriction.
- **Daily change budget per caller:** deferred. Audit rows record the `name` claim, which is not
  unique, so the budget can't be counted from audit. An in-memory counter is per instance. The
  right store is Phase 2b's operation journal, which records each command per caller; revisit
  then.

## 13. Open decisions for the repo owner

1. **Entra settings stay deployment-only** (§5.1). Recommended.
2. **What an unsaved policy means:** today's behaviour, with every tool on, including Skip (§5.2).
   Recommended for compatibility. The alternative is Skip off by default.
3. **Turned-off answer:** `503 [Disabled]` (recommended) or `404`.
4. **Refusal audit window:** 5 minutes per instance (§5.7).

## 14. Facts to verify before implementing (slice 0)

1. How `nimbus_search_messages` and `nimbus_get_metrics` query across endpoints, and so where
   hidden endpoints are filtered out. A filter applied after a page is read can return short or
   empty pages, so excluding hidden endpoints in the query is preferred.
2. The principal matching that Access Control uses for emails, object ids and groups (the WebApp's
   `EndpointAuthorizationService`), so the people allowlist reuses it rather than copying it.
3. Whether `nimbus_classify_failure` audits `FailureClassified` with `channel: "Mcp"` in `Data`.
   The activity endpoint depends on it.
4. That, in SDK 2.2.0's stateless HTTP mode, the list-tools and call-tool filters can reach the
   request's `HttpContext` and user.
5. What MCP clients show for a `403` from a failed authorization requirement. A `[PermissionDenied]`
   body may be needed.
6. How each provider stores `MessageAuditType`, so appending two values is safe for existing rows.
7. That the Cosmos settings container is partitioned on `/id`, as the `AuditSettings` reads imply.

## 15. References

- Mockup: [admin-mockup.html](admin-mockup.html)
- [Spec 035](../035-mcp-operator-access/spec.md), [docs/mcp-server.md](../../mcp-server.md),
  [docs/rate-limiting.md](../../rate-limiting.md)
- Precedents: [configurable audit types](../../plan/2026-09-29-configurable-audit-types.md),
  [Failure intelligence Admin settings](../../plan/2026-09-21-intelligence-admin-settings.md)
- Code: `src/NimBus.WebApp/Mcp/` (`McpOperatorServiceCollectionExtensions.cs`,
  `McpRequestGuardMiddleware.cs`, `McpOperatorAuthorization.cs`, `Operations/OperatorActionAccess.cs`,
  `Operations/OperatorPayloadAccess.cs`, `Operations/OperatorEndpointCatalog.cs`,
  `Operations/OperatorMutationLimiter.cs`),
  `src/NimBus.WebApp/RateLimiting/RateLimitingServiceCollectionExtensions.cs`,
  `src/NimBus.WebApp/Services/AuditSettingsProvider.cs`,
  `src/NimBus.WebApp/Services/IntegrationIntelligence/IntelligenceSettingsStore.cs`,
  `src/NimBus.WebApp/Services/Operations/OperatorCommandCoordinator.cs`,
  `src/NimBus.MessageStore.Abstractions/IEndpointMetadataStore.cs`,
  `src/NimBus.WebApp/ClientApp/src/components/admin/failure-intelligence-settings.tsx`

## 16. Implementation record (2026-10-07)

What slice 0 found, and where the code differs from §5–§6:

1. **Search and metrics** (§14.1): `nimbus_search_messages` and `nimbus_get_metrics` read through
   the REST implementations, which offer no endpoint exclusion. Hidden endpoints are filtered out
   after the page is read, so a search page can come back short; a filter that names a hidden
   endpoint answers `[EndpointNotFound]`. In the failures view, a group that touches a hidden
   endpoint keeps its count but loses the hidden name, and the total is then the sum of the kept
   groups.
2. **Principal matching** (§14.2): Access Control matches emails and the object id only, with no
   groups. `EndpointAuthorizationService.ResolveIdentifiers` now exposes that matching, and the
   people allowlist adds the `groups` claim values to it. Workload (app-only) tokens are not
   subject to the people list; the workload switch governs them.
3. **Classification activity** (§14.3): the classification service audits `FailureClassified`
   without a channel, so classify calls over MCP are not counted in the activity summary, which
   reads `Resubmit`, `Skip`, `ReportEvent`, `McpAccessRefused` and `UpdateMcpSettings`. Coordinator
   role denials with `channel: "Mcp"` count as refusals with reason `role`.
4. **Filters** (§14.4): the SDK's `AddListToolsFilter`/`AddCallToolFilter` reach the request
   through `IHttpContextAccessor`. A refused direct call returns an `isError` tool result rather
   than an exception.
5. **403 body** (§14.5): a refusal by the people, client or workload rules is a plain `403` with
   no body. It is audited.
6. **Audit types** (§14.6): appended after `CommandNotSent`, added to the three audit-type enums in
   `api-spec.yaml` (pinned by `AuditTypeContractTests`) and to `AlwaysRecorded`, which keeps them
   off the Admin → Audit list.
7. **Cosmos container** (§14.7): confirmed `/id`; live conformance passed on the vNext emulator.
8. **API naming:** the deployment field is `rateLimitsEnabled`, not `rateLimitingEnabled`, because
   `RateLimitWiringTests` forbids the text `RateLimiting` in the generated controllers.
9. **Registration:** the policy provider is registered only when the deployment serves `/mcp`; the
   Admin API is registered always, so a policy can be saved before the endpoint exists. In that
   case `GET` returns `effective: null`.
10. **Payloads switched off** answer `[PermissionDenied] Payloads are turned off by an
    administrator.` rather than the PiiReader message.
11. **Antiforgery:** the token is checked by `[AutoValidateAntiforgeryToken]`, declared on the
    NSwag-generated `McpAccessApiController` through a hand-written partial
    (`Controllers/McpAccessApiController.Antiforgery.cs`), not inside the implementation. It covers
    PUT and POST and skips GET, and CodeQL (`cs/web/missing-token-validation`) recognizes it. A
    request without a valid token gets a bodiless 400 before the Owner check, so it is not audited.
12. **Mockup:** the "last call", "people today" and "last valid token" figures and the daily
    budget are not implemented (§4, §6.1).

Verification: Release build of `src/NimBus.sln` with 0 errors; `dotnet test -c Release`, all
green. That run includes 41 new WebApp tests (16 enforcement tests through the real MCP pipeline,
25 rules, service, provider and API tests) and 4 new conformance tests. The SQL Server
(197/197) and Cosmos (352/352) store suites ran live against local containers with nothing
skipped. Vitest: 78 files, 568 tests. ESLint and `tsc` are clean. Removing the requirement and the
tool filters turns 7 enforcement tests red. The tab was checked in a browser with stubbed API
responses.

