# NimBus operator MCP server

The management WebApp can serve a **Model Context Protocol (MCP)** endpoint at `/mcp`. It lets an
AI agent (Claude Code, Claude Desktop or any MCP client) look at endpoint health, find and inspect
messages, search across endpoints, read metrics and failure classifications, read the evidence of a
failure and record its own classification of it, and, for a signed-in operator, resubmit, skip and
mark reported failed messages. It applies the same authorization, redaction and audit as the Web UI.

- **Transport:** Streamable HTTP, stateless, protocol revisions up to 2026-07-28
  (`ModelContextProtocol.AspNetCore`).
- **Access:** read tools for every caller with the observe permission. Tools that change a message
  are for interactive, delegated callers only (see [Changing messages](#changing-messages)). There
  are no administrative tools.
- **Design:** [Spec 035](spec/035-mcp-operator-access/spec.md).

The endpoint is off unless it is enabled. It replaces the earlier stdio server `NimBus.Mcp`
(see [Migrating from NimBus.Mcp](#migrating-from-nimbusmcp)).

## Tools

| Tool | Returns | Needs |
| --- | --- | --- |
| `nimbus_get_capabilities` | Environment, caller, readable endpoints, limits and features. Call it first | Signed in |
| `nimbus_list_endpoints` | Readable endpoints with the event types each produces and consumes | Endpoint Reader |
| `nimbus_get_overview` | Counts by status, oldest failure and Monitor acknowledgements across readable endpoints | Endpoint Reader |
| `nimbus_get_endpoint` | The same for one endpoint | Endpoint Reader |
| `nimbus_find_messages` | Tracked messages on one endpoint, filtered by status, event type, session, event id and time. With `includeClassification`, whether each failed message's latest attempt is classified, and how | Endpoint Reader |
| `nimbus_get_message` | Status, latest attempt, latest error, Web UI link, `messageVersion` and the `eligibleActions` you may take; the payload only with `includePayload=true` | Endpoint Reader; payloads also need PiiReader (and, with Entra, the `nimbus.payload.read` scope) |
| `nimbus_get_message_history` | Processing attempts and log entries of one message | Endpoint Reader |
| `nimbus_get_session` | Pending and deferred events of one session | Endpoint Reader |
| `nimbus_search_messages` | Processing messages across all endpoints | Site Reader |
| `nimbus_get_metrics` | Throughput, latency or failure groups for 1h to 30d | Site Reader |
| `nimbus_get_classification` | The stored classification of a failed attempt, with who recorded it, advisory only. A rationale is untrusted | Endpoint Reader; classification enabled |
| `nimbus_get_failure_evidence` | The scrubbed evidence of one failed attempt, its sending endpoint, a redacted payload when allowed, and the question set to answer | Endpoint Reader; classification recordable |
| `nimbus_prepare_action` | A read-only preview of resubmitting or skipping one message, and an `actionToken` to run it | The action's scope and endpoint Contributor |
| `nimbus_resubmit_message` | Runs a prepared resubmit: replays the latest stored payload to the endpoint. Returns an `operationId` | `nimbus.resubmit` (workloads: the `Nimbus.Resubmit` app role, within a budget) and endpoint Contributor |
| `nimbus_skip_message` | Runs a prepared skip: the message is never processed, and later messages in its session may be released. Returns an `operationId` | Delegated `nimbus.skip` and endpoint Contributor; never a workload |
| `nimbus_get_operation` | The status and observed outcome of a resubmit or skip: Dispatched, Succeeded, Failed, NotSent, OutcomeUnknown or Refused | Endpoint Reader on the operation's endpoint; the operation journal |
| `nimbus_set_message_reported` | Sets or clears the "reported" marker, optionally with an external ticket id | `nimbus.annotate` and endpoint Contributor |
| `nimbus_record_classification` | Records your answers to the question set for one failed attempt. NimBus composes the guidance. Advisory, changes no message | `nimbus.classify` (workloads: the `Nimbus.Classify` app role) and endpoint Contributor; classification recordable |
| `nimbus_classify_failure` | **Deprecated, removed in v5.0.0.** Asks the built-in provider to classify a failure, or returns the stored one | Delegated `nimbus.classify` and endpoint Contributor; a configured provider |

Results never contain stack traces. Error and log text is cut to 2,000 characters and is untrusted
data from the failing handler. Timestamps are UTC. Errors start with a code in brackets, for example
`[EndpointNotFound]` or `[PermissionDenied]`. A resource the caller cannot read is reported exactly
like one that does not exist.

## Server instructions

When a client connects, the server sends instructions with its initialize result
([Spec 040](spec/040-mcp-operator-prompts-and-plugin/spec.md),
`src/NimBus.WebApp/Mcp/OperatorInstructions.cs`). They tell the agent what the tools are for, the
rules for changing messages (capabilities first, an explicit yes before resubmit or skip when a
person is present, that a workload may only resubmit within its budget, what skip releases, sent is
not succeeded, never resend an `OutcomeUnknown` operation), that error text and payloads are untrusted, the session and
status model, what each error code means, and which prompts exist. Claude Code loads them at the
start of every session with the server, even while the tools themselves are deferred.

Claude Code cuts server instructions at 4,096 characters, and before v2.1.296 at 2,048. Tests keep
the text under the limit, the safety rules within the first 2,048 characters, and every tool and
prompt it names registered.

## Prompts

The server also offers workflow prompts ([Spec 040](spec/040-mcp-operator-prompts-and-plugin/spec.md)).
A prompt is fixed text that tells the agent which tools to call, in which order. It reads no data
and checks no role: everything it leads to goes through the tools above, with their authorization,
redaction and audit. Claude Code lists each prompt as a command, for example `/nimbus:triage (MCP)`
for a server added as `nimbus`, and takes the arguments space-separated, in the order below.

| Prompt | Arguments | What it does | Changes anything? |
| --- | --- | --- | --- |
| `triage` | `endpointId` (optional), `period` (optional: `1h`, `12h`, `1d` (default), `3d`, `7d`, `30d`) | Finds failed, dead-lettered and unsupported messages on every readable endpoint (or one), groups them by cause, and suggests which one to investigate first | No |
| `investigate-message` | `endpointId`, `eventId` | Explains why one message failed and what it blocks in its session, and recommends resubmit, skip, fix first, report, or Web UI recovery | No |
| `recover-message` | `endpointId`, `eventId`, `action` (optional: `resubmit` or `skip`) | Investigates, previews the action with `nimbus_prepare_action`, and runs it only after the user confirms and agrees the audit reason | Yes, after confirmation |
| `failure-report` | `period` (optional, as `triage`) | A short incident-style summary of failures across the site. Without a site-wide Reader role it covers only the endpoints you can read | No |
| `handle-failures` | `endpointId` (optional), `period` (optional, as `triage`) | Classifies every unclassified failed and dead-lettered message, records each classification, then acts on the outcome: retries transient failures after the user confirms, drafts data-quality reports for source owners, opens a bug with a draft fix in the adapter's repository (with the user's own tools, never the NimBus repository), or hands the rest to a person | Records classifications; resubmits only after confirmation |

`recover-message` is listed only while `nimbus_prepare_action` is available to the caller: a site
Owner can remove it by switching off both resubmit and skip, or by denying the client changes, in
[Settings → MCP access](#managing-access-in-the-webapp). Requesting it anyway answers
`[PermissionDenied] recover-message is turned off by an administrator.` and is audited like a
refused tool. `handle-failures` follows `nimbus_record_classification` the same way: switching off
**Classify failures**, or denying the client changes, removes it. An invalid argument (an id longer than 200 characters or with control characters, an
unknown period or action) answers `[InvalidArgument]`. An endpoint you cannot read surfaces as
`[EndpointNotFound]` from the first tool the agent calls.

## Changing messages

Resubmit and skip take two steps, so the user can see what will happen before it does:

1. `nimbus_get_message` returns the message's `messageVersion`, a fingerprint of its status, session,
   latest attempt and update time, and the `eligibleActions` you may take on it.
2. `nimbus_prepare_action` with the action and that version checks your permission and the
   message's state without changing anything. It returns the impact, including how many later
   messages are waiting in the session, and an `actionToken`.
3. `nimbus_resubmit_message` or `nimbus_skip_message` with the token, a reason and a new GUID as
   `idempotencyKey` runs the action.

The token is valid for two minutes, only for the caller, client application and environment it
was issued to, and only for that action on that version of the message. It is a state check, not
proof that a human approved the action: clients should show the preview and ask the user before a
skip. When the tool runs, NimBus:

- re-reads the caller's role from the stored access-control lists, so a grant revoked moments ago
  is honored;
- claims the message at the version in the token, so a second command on the same version, whether
  from the Web UI, another agent or a replayed token, sends nothing and gets `[StaleMessage]`;
- writes the audit row (channel, reason, idempotency key, client application) and refuses the action
  with `[AuditUnavailable]` if it cannot;
- then publishes the command.

The result says the command was sent, not that it succeeded. Read the message again to see the
outcome. A message that failed again has a new version and needs a new token. For the few seconds
between the command and the Resolver recording it, `nimbus_get_message` reports the message as
`[MessageNotFound]`, because the command claims the row by archiving it; read it again a little
later.

Over MCP, resubmit and skip accept Failed, DeadLettered and Unsupported messages. Deferred messages
and pending handoffs are recovered in the Web UI. `nimbus_set_message_reported`,
`nimbus_record_classification` and `nimbus_classify_failure` need no token; they take a reason or an
idempotency key directly.

Tools that change a message require a delegated scope from a signed-in user. A workload token,
which carries app roles and no `scp` claim, is refused with `[PermissionDenied]`. The exceptions are
recording a classification, which changes no message (see
[Recording classifications](#recording-classifications)), and an unattended resubmit within a budget
(see [Unattended resubmits](#unattended-resubmits)). A workload can never skip. Errors specific
to these tools are `[StaleMessage]`, `[ActionNotAllowed]` (the message's state does not allow the
action, or a workload's budget or the message's cap is used up), `[RateLimited]`,
`[AuditUnavailable]` and `[OutcomeUnknown]` (publishing failed after the message was claimed; it was
restored, but the command may have reached Service Bus).

## Operation journal

Every resubmit and skip over MCP is an operation ([Spec 044](spec/044-agent-first/spec.md) Phase 2,
from Spec 035 Phase 2b), stored in the message store: SQL Server table `OperatorOperations`
(migration `0026_OperatorOperations.sql`), the Cosmos container `operatoroperations` (partition key
`/id`, item TTL on), or the in-memory store. Operations are kept for 30 days.

- **The same idempotency key never runs twice.** The operation id is derived from the caller and
  the key. Retrying with the same key returns the existing operation (`replayed: true`) and sends
  nothing, even after the action token expired or the message changed. A key used for a resubmit
  cannot be reused for a skip.
- **The outcome comes from the Resolver.** The command is published with the operation id as its
  Service Bus message id. When the Resolver has stored that message the operation is `Dispatched`,
  and the endpoint's response, whose `ParentMessageId` names it, makes it `Succeeded`
  (`ResolutionResponse`, `SkipResponse`) or `Failed` (`ErrorResponse`, `UnsupportedResponse`).
  `nimbus_get_operation` reads it, and catches up with the history on every read.
- **A crashed request is repaired, never republished.** The journal records each step before it is
  taken: claimed, then publishing, then dispatched. A background reconciler on every WebApp
  instance visits operations whose request stopped. One that stopped before publishing is
  `NotSent` and its message is restored. One that stopped while publishing is confirmed from the
  Resolver's history, or after 60 minutes without a trace its message is restored for a person and
  it stays `OutcomeUnknown`. NimBus never sends a command again on its own, and an agent must not
  resubmit an `OutcomeUnknown` operation. Repairs are audited as `CommandNotSent` by
  "NimBus operation reconciler".

Topic duplicate detection stays off: a subscriber that enables the [inbox](inbox-pattern.md) also
skips a redelivered command, because the inbox records a resubmission's message id. Without the
journal store (a custom storage provider that does not implement `IOperatorOperationStore`) resubmit
and skip work as before, `nimbus_get_operation` answers `[FeatureUnavailable]`, and unattended
resubmits are unavailable. The Web UI's own resubmit and skip are not journaled.

## Unattended resubmits

A workload resubmits failed messages without a person when:

- its token carries the `Nimbus.Resubmit` app role;
- the Owner turned on **Workloads may resubmit** in **Settings → MCP access** (off by default,
  effective only while workload tokens are accepted and **Resubmit messages** is on);
- its object id has Contributor on the endpoint in Access Control; and
- the deployment has the operation journal.

NimBus then enforces two limits, both counted from the journal, so they hold across instances and
restarts and apply even when rate limiting is off:

| Limit | Default | Range | Refusal |
| --- | --- | --- | --- |
| Resubmits per workload client in a rolling 24 hours | 20 | 0–500 | `[ActionNotAllowed]` "… used its budget …" |
| Unsuccessful workload resubmits of one message, across agents | 2 | 1–5 | `[ActionNotAllowed]` "… Hand it to a person." |

A resubmit counts against the cap unless its operation `Succeeded` (or was never sent or was
refused). The per-minute change limit still applies on top. `nimbus_get_capabilities` reports
`caller.isWorkload` and, for a workload admitted to resubmit, an `unattended` block with the budget,
how much of it is used and the cap. The `handle-failures` prompt takes `unattended: true` for a
runner with no person present: it then retries without asking, polls `nimbus_get_operation`, and
hands a message to a person when a limit refuses it. There is no app role for skip.

## Recording classifications

An agent classifies a failure with its own model ([Spec 044](spec/044-agent-first/spec.md)):

1. `nimbus_find_messages` with `includeClassification: true` finds the failures whose latest
   attempt has no saved classification. When classification storage cannot answer,
   `classificationStatus` is `unavailable` and nothing is inferred.
2. `nimbus_get_failure_evidence` returns the evidence and the current question set: a failure
   category, three likelihoods and the fault origin (`source_data`, `adapter_code`, `target_system`,
   `configuration`, `platform` or `unknown`). Each question names the argument that carries its
   answer. A message that is no longer Failed or DeadLettered answers `[ActionNotAllowed]`.
3. `nimbus_record_classification` stores the answers, with the `messageId` and `questionSetVersion`
   the evidence returned, a new GUID as `idempotencyKey`, and optionally the model and a rationale of
   up to 1,000 characters. NimBus composes the guidance from the answers and the saved thresholds.
   When a classification already exists it is returned unchanged (`existing: true`) unless
   `supersede` is true.

A recording is advisory and changes no message. It counts against the request limit, not the
change limit. The rationale is scrubbed like error text and is returned to other agents as untrusted
data. Every recording is audited as `FailureClassified` with the provider `Agent` and the client
application.

The evidence carries a redacted payload only when an Owner turns on **Settings → Failure
intelligence → Agents may see redacted payloads** (off by default). Values not marked sensitive in
the event's metadata remain, so the prompt tells agents never to copy payload values into issues,
reports or tests.

**Unattended recording.** A workload (an app-only token) may record when it holds the
`Nimbus.Classify` app role, the Owner turned on **Workloads may record classifications** in
**Settings → MCP access** (off by default, and effective only while workload tokens are accepted),
and the workload's object id has Contributor on the endpoint in Access Control. The role admits
`nimbus_record_classification` only: never `nimbus_classify_failure`, resubmit, skip or report.

## Managing access in the WebApp

A site Owner manages MCP access in **Settings → MCP access** ([Spec 037](spec/037-mcp-admin-access/spec.md)).
There are two layers:

- **The deployment** decides whether `/mcp` can exist and whom NimBus trusts for tokens:
  `NimBus__Mcp__Enabled`, `NimBus__Mcp__EnableForLocalDevelopment`, `NimBus__Mcp__Entra__*`,
  `NimBus__Mcp__AllowedOrigins__*` and the `RateLimiting__Mcp*` limits. The panel's
  **Overview** and **Limits** tabs show them but cannot change them.
- **The Owner's policy** can only narrow that. A call proceeds only when the policy, the Entra scope
  or app role, the NimBus role on the endpoint and the message state all allow it.

The policy covers:

| Setting | Effect |
| --- | --- |
| Serve the MCP endpoint | When off, every call gets `503 [Disabled]`. The protected-resource metadata keeps working. |
| Raw payloads, mark reported, classify, resubmit, skip | A switched-off tool is removed from `tools/list` and from `permittedActions`; a direct call gets `[PermissionDenied] … turned off by an administrator`. The read tools have no switch. |
| Unattended workloads | Whether app-only (`Nimbus.Observe`) tokens are accepted for the read tools. |
| Workloads may record classifications | Whether workloads with the `Nimbus.Classify` app role may call `nimbus_record_classification`. Off by default; effective only while workload tokens are accepted and **Classify failures** is on. |
| Workloads may resubmit | Whether workloads with the `Nimbus.Resubmit` app role may resubmit unattended, with the resubmits per agent per day and the unsuccessful resubmits per message (see [Unattended resubmits](#unattended-resubmits)). Off by default. |
| People | Everyone with a NimBus role, or only listed users and groups (email, user object id, or a group object id from the `groups` claim). Workloads are governed by the workload switch instead. |
| Client applications | Any client Entra lets sign in, or only approved ones, matched on `azp` (then `appid`). Each approved client can be allowed or denied the tools that change messages. |
| Endpoints | Hide endpoints from agents (they answer `[EndpointNotFound]`), and limit where agents may change messages. |
| Limits | Lower the request and change limits below the deployment's values. |

Callers refused by the people, client or workload rules get `403`. Refusals are audited as
`McpAccessRefused` (at most one row per caller, client, reason and tool every 5 minutes per
instance); every save is audited as `UpdateMcpSettings` with the list of changes.

Changes apply without a restart: each instance re-reads the policy at most every 30 seconds, and
the instance that saved it applies it at once. **Turn off now** turns the endpoint off on the
latest saved policy, whatever revision the page last read. The policy fails closed: if it cannot
be read, an instance keeps the last policy it loaded, and until it has loaded one at all it
answers `503 [Unavailable]`. With no saved policy, everything the deployment allows is on, as
before the policy existed.

The policy is one record in shared storage (`IEndpointMetadataStore.GetMcpAccessSettings`;
SQL Server migration `0023_McpAccessSettings.sql`, a fixed-id document in the Cosmos settings
container). The API is `GET`/`PUT /api/admin/mcp/settings`, `POST /api/admin/mcp/turn-off` and
`GET /api/admin/mcp/activity`.

## Local development (Aspire)

With the NimBus Aspire AppHost (`src/NimBus.AppHost`) no setup is needed: it sets
`NimBus__Mcp__EnableForLocalDevelopment=true`, and the
WebApp serves `/mcp` whenever its local-dev bypass is on (Development and
`EnableLocalDevAuthentication=true`, which the AppHost sets unless `NIMBUS_IDENTITY` is on). The
CrmErpDemo and DynamicsBcDemo AppHosts set it too; CrmErpDemo also turns on failure classification
with `Provider: None`, so an agent can record classifications there. No sign-in
is involved; every call runs as the "Local Developer" user, with the same role and PII checks as the
Web UI. Scopes do not apply in this mode, so the tools that change messages need only the
Contributor role. With the bypass off, `/mcp` is not served.

Point the client at the WebApp's HTTPS URL from the Aspire dashboard (by default
`https://localhost:18443`) plus `/mcp`. For example, an `.mcp.json` entry:

```json
{
  "mcpServers": {
    "nimbus": { "type": "http", "url": "https://localhost:18443/mcp" }
  }
}
```

In this mode the endpoint accepts loopback connections only, and refuses browser requests whose
`Origin` is not a localhost address. This protects against DNS rebinding.

## Azure (Entra ID)

In Azure the endpoint requires an Entra access token issued for its own app registration. It does
not use the WebApp's sign-in registration, cookies or the `"Az"` bearer scheme.

### 1. Register the MCP resource

In the tenant that signs in your operators:

1. Create an app registration, for example `NimBus MCP (dev)`, single tenant. Note its
   **Application (client) ID**.
2. **Expose an API:** keep the default Application ID URI (`api://<client-id>`) or set your own.
   Add these delegated scopes:
   - `nimbus.observe`: use the read tools.
   - `nimbus.payload.read`: see raw event payloads. It only adds to the PiiReader role, and never
     replaces it.
   - `nimbus.resubmit`, `nimbus.skip`, `nimbus.annotate` and `nimbus.classify`: use
     `nimbus_resubmit_message`, `nimbus_skip_message`, `nimbus_set_message_reported` and
     `nimbus_record_classification` (and the deprecated `nimbus_classify_failure`). Each only adds to
     the Contributor role on the endpoint. Consider making them admin-consent scopes so only clients
     you approve can request them.
3. **Add the endpoint URL as a second Application ID URI:** `https://<webapp>/mcp`, exactly as
   clients connect to it. MCP clients send that URL as the OAuth `resource` parameter, and Entra
   refuses a sign-in whose `resource` does not belong to the same app as the requested scopes
   (`AADSTS9010010: The resource parameter provided in the request doesn't match with the
   requested scopes`). Keep `api://<client-id>` as well: the advertised scopes use it.
   ```bash
   az ad app update --id <client-id> --identifier-uris "api://<client-id>" "https://<webapp>/mcp"
   ```
4. **Manifest:** set `api.requestedAccessTokenVersion` to `2`, so access tokens carry the client
   id as their audience whichever identifier the client asked for.
5. **App roles** (optional, for unattended workloads): add `Nimbus.Observe` for applications, and
   `Nimbus.Classify` for workloads that record failure classifications. No app role grants payload
   access or changes messages.
6. **Token configuration** (optional): add the `groups` claim if your NimBus role grants use Entra
   groups. Grants keyed by object id or email work without it.
7. **Register an MCP client.** Entra does not support dynamic client registration, so every MCP
   client needs a client id of its own. Do not use the MCP resource's client id from step 1.1 (the
   WebApp shows it as **MCP resource (API) app ID**): it has no redirect URI, so Entra refuses the
   sign-in with `AADSTS500113: No reply address is registered for the application`. For a desktop
   client such as Claude Code, create a second single-tenant app registration, for example
   `NimBus MCP Client (dev)`:
   - **Authentication:** add a **Mobile and desktop** redirect URI on the client's loopback
     callback. For Claude Code that is `http://localhost:<port>/callback`, where `<port>` is the
     port you pass as `--callback-port`; pick any free port, but it must match exactly.
   - Turn on **Allow public client flows**. The client needs no secret.
   - On the MCP resource (**Expose an API → Authorized client applications**), pre-authorize the
     client id for `nimbus.observe`, and for the other scopes only where needed, or grant consent
     for them.

The endpoint accepts v2.0 tokens (issuer `https://login.microsoftonline.com/<tenant>/v2.0`,
audience the client id) and v1.0 tokens (issuer `https://sts.windows.net/<tenant>/`, audience the
Application ID URI).

### 2. Configure the WebApp

Set these app settings on the management WebApp:

| Setting | Value |
| --- | --- |
| `NimBus__Mcp__Enabled` | `true` |
| `NimBus__Mcp__Entra__TenantId` | Tenant id |
| `NimBus__Mcp__Entra__ClientId` | The MCP resource's client id from step 1.1, not a client from step 1.7 |
| `NimBus__Mcp__Entra__ApplicationIdUri` | Only if you changed it from `api://<client-id>`. The scopes' identifier, not the endpoint URL from step 1.3 |
| `NimBus__Mcp__Entra__Instance` | Only for a sovereign cloud; default `https://login.microsoftonline.com/` |
| `NimBus__Mcp__AllowedOrigins__0` | Only for browser-based MCP clients: their origin |

```bash
az webapp config appsettings set --resource-group <resource-group> --name <webapp-name> --settings NimBus__Mcp__Enabled=true NimBus__Mcp__Entra__TenantId=<tenant-id> NimBus__Mcp__Entra__ClientId=<client-id>
```

`nb infra apply` keeps every `NimBus__Mcp__*` setting across redeploys. If `NimBus__Mcp__Enabled`
is `true` but the tenant or client id is missing, the WebApp fails to start and names the missing
keys, rather than serving an unauthenticated endpoint.

### 3. Verify

- `GET https://<webapp>/.well-known/oauth-protected-resource/mcp` returns the resource metadata:
  the authorization server and the six scopes.
- `POST https://<webapp>/mcp` without a token returns `401` with a `WWW-Authenticate` header that
  points at that metadata.
- An MCP client that signs in with the `nimbus.observe` scope can call `nimbus_get_capabilities`.
  For Claude Code, add the server with the client registration from step 1.7 and the port of its
  redirect URI, then run `/mcp` in a `claude` session and authenticate `nimbus` in the browser:

  ```bash
  claude mcp add --transport http --client-id <mcp-client-app-id> --callback-port <port> nimbus https://<webapp>/mcp
  ```

  The WebApp's **Settings → MCP access → Connect** tab shows this command, filled with an approved
  client when the policy approves specific clients.

- With a write scope and Contributor, `nimbus_get_capabilities` lists the action in
  `permittedActions`. After you add a scope to the registration, sign in again so the token
  carries it.

In [private networking mode](spec/034-private-networking/spec.md), only clients inside the network
can reach `/mcp`. Cloud-hosted agents need a path through the Application Gateway.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `AADSTS9010010`: the resource doesn't match the requested scopes | The endpoint URL isn't an Application ID URI of the MCP resource. Add `https://<webapp>/mcp` ([step 1.3](#1-register-the-mcp-resource)) |
| `AADSTS500113`: no reply address is registered | The client id is the MCP resource's own app id. Use the separate client registration ([step 1.7](#1-register-the-mcp-resource)) |
| `AADSTS50011`: the redirect URI doesn't match | Register `http://localhost:<port>/callback` under **Mobile and desktop** on the client registration, with the same port as `--callback-port` |
| `AADSTS65001`: consent required | Pre-authorize the client on the MCP resource for the scopes it asks for, or grant admin consent |
| `/.well-known/oauth-protected-resource/mcp` returns the WebApp's HTML page or a sign-in redirect instead of JSON | `/mcp` isn't enabled on this WebApp (`NimBus__Mcp__Enabled`), or its NimBus version predates the endpoint |
| `403` when connecting | The site Owner's [access policy](#managing-access-in-the-webapp) refuses this person, client application or workload |
| `503 [Disabled]` | A site Owner turned the endpoint off in Settings → MCP access |
| `nimbus_get_capabilities` lists no readable endpoints | The user holds no NimBus role on any endpoint |
| An action you expected is missing from `permittedActions` | The token lacks the action's scope (sign in again after adding it), the user isn't Contributor on the endpoint, or a site Owner switched the action off |
| New tools or prompts don't appear after an upgrade | Claude Code reads the tool list when it connects. Reconnect the server in `/mcp` or start a new session |

## Limits

`/mcp` has its own rate-limit policy, `nimbus-mcp`: 60 requests per 60 seconds, per tenant,
client application and user. Tools that change a message are also limited to 5 per 60 seconds
for the same caller (`RateLimiting:McpMutations`), and answer `[RateLimited]` beyond that. A site
Owner can lower both limits in Settings → MCP access, but not raise them. See
[rate limiting](rate-limiting.md).

## Migrating from NimBus.Mcp

`NimBus.Mcp` was a local stdio process that called `/api/agent/*` and identified itself with an
`X-Agent-Id` header. It has been removed.

- **Client configuration:** replace the `mcpServers` entry that ran `dotnet run --project
  src/NimBus.Mcp/...` with the `/mcp` URL above, and drop `NIMBUS_API_BASEURL` and
  `NIMBUS_AGENT_ID`.
- **Replaced tools:** `discover_topology` is now `nimbus_list_endpoints`. `search_failures` is now
  `nimbus_find_messages`, `nimbus_search_messages` and `nimbus_get_message_history`.
- **Bus participation:** `define_event_type`, `subscribe`, `receive_messages`, `publish_event` and
  `settle_message` are not operator tools. Agents that take part on the bus use the `NimBus.Agents`
  REST SDK against `/api/agent/*`.
