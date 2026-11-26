# Spec 051 — Platform support for operations agents

Status: revision 6 (2026-11-25). Revised three times after reviews of an agent design that builds on
this spec, once when §2 was built and once when §5–7 were approved; the changes are listed in §13.
§2 (the wake-up webhook) and §5–6 (record bindings and the adapter's MCP address) are built. §7 is
approved. The other parts are not yet approved and not built.
Baseline: master `7122b1bd`; the latest release is v4.17.0.
Builds on [Spec 035](../035-mcp-operator-access/spec.md),
[Spec 037](../037-mcp-admin-access/spec.md),
[Spec 044](../044-agent-first/spec.md) and
[Spec 046](../046-mcp-guided-actions/spec.md).

An agent that handles failed messages over the operator MCP endpoint can classify, retry, report
and link failures today. This spec adds what such an agent still lacks. It designs no agent: agents
stay outside NimBus, as Spec 044 decided, and NimBus forwards no calls to other systems and holds no
repository or tracker credentials.

Each part is its own change with its own plan.

## 1. What an agent cannot do today

Checked against the baseline.

| Need | Exists in v4.17.0 | Missing |
|---|---|---|
| Know that a failure happened without polling | The Resolver mails endpoint alert subscribers on a Failed or DeadLettered outcome. In v4.17.0 `webhook` subscriptions were stored but not delivered; Spec 044 §12 left waking an agent for later | Delivery to a webhook (§2, built) |
| Prepare a corrected resubmit for a person | Resubmit with changes in the Web UI and REST (`post-resubmit-with-changes-event-ids`), for a signed-in person. Spec 035 Phase 3 (#400, open) adds the same for a signed-in person's agent. An unattended workload cannot change a payload, and should not | A way for a workload to flag what is wrong and hand the decision to a person (§3), limited to fields the endpoint's Owner allows (§4) |
| Look at the data in the source and target systems | Nothing. The endpoint records its owner, source system, repository address and ticket link | An address for the adapter's own MCP server (§6), that server (§7), and a statement from the Owner of which records a message refers to (§5) |
| Work through every failure under a ticket | `nimbus_find_reports` returns at most 50 rows with no cursor. `nimbus_prepare_action` by `ticketId` takes the 50 newest reports and then drops those that are no longer failed, so older failed messages under the same ticket are never reached | Paging (§8) |
| Mark a failure reported without undoing someone else's marker | `nimbus_set_message_reported` replaces whatever marker is there. Its idempotency key is recorded in the audit trail and does not make a repeat harmless | A write that leaves another ticket's marker alone (§9) |

Everything else an agent uses is in place: `nimbus_find_messages` with a cursor,
`nimbus_get_failure_evidence`, `nimbus_record_classification`, and the journaled resubmit with its
budget and cap.

## 2. A wake-up webhook

The Resolver already decides when an endpoint alert is due and mails it
([notifications](../../notifications.md#endpoint-alerts-alerts-tab)).

- The Resolver delivers a due alert to a subscription of type `webhook`: an HTTP POST with the
  endpoint id, event id, session id, event type, status and time. No payload and no error text; a
  receiver that wants more reads it through MCP under its own role.
- The request is signed with a secret per subscription (HMAC over the body and a timestamp), so the
  receiver can reject forgeries and replays. The secret is shown once, when the subscription is
  created.
- The secret is not stored. It is derived from a signing key of the deployment and the
  subscription id. The Resolver and the WebApp hold that key, and webhooks are off until it is
  set. A subscription's secret is replaced by deleting and re-creating the subscription.
- A receiver that accepts an Entra token and not a signature, such as an Azure SRE Agent HTTP
  trigger, is reached without a relay in between: an administrator lists the receiver's URL prefix
  and the token audience in the Resolver's settings, and a request to a URL under that prefix also
  carries a token of the Resolver's managed identity. A subscription cannot name an audience.
- The existing frequency window and claim apply unchanged: a burst of failures sends one request,
  and a failure inside a window that has already been used sends none. A frequency of `0` sends
  every failure. A receiver that needs every failure quickly uses `0` and drops duplicates itself.
  A receiver that cannot drop duplicates, because every request starts a run, picks a short
  window: the WebApp offers 5 and 15 minutes next to hourly, daily and weekly.
- Delivery is best-effort and happens after the message is settled, as for mail. A slow or failing
  receiver never delays or dead-letters a message. A receiver is expected to sweep on a timer as
  well, so a lost request delays its work and does not lose it.
- The URL must be HTTPS. The Resolver connects directly, to public addresses only, tries once and
  does not follow redirects. A deployment with receivers on a private network switches the address
  rule off.

The request, the settings and how a receiver checks a signature are in
[notifications](../../notifications.md#webhook-alerts).

## 3. Approval requests

An unattended agent flags what it believes is wrong in a failed message. A person supplies the
corrected value and approves. NimBus carries it out.

### 3.1 Scope

The first version covers one action: resubmit with changes. Skip is not included (§12, question 1).

The caller proposes which fields are wrong, never their new values. A value in a request is
evidence for the person to judge. NimBus never applies it on its own, and shows it as unverified.

### 3.2 Tools

| Tool | Does | Needs |
|---|---|---|
| `nimbus_request_approval` | Files a request: endpoint id, event id, the message version the caller read, a list of suspect fields, a summary and an idempotency key. Each suspect field has its JSON path, a reason and optional evidence: the value the caller read from the field's bound evidence source (§4), and when | `Nimbus.RequestApproval` for a workload, or, for a person's agent, the delegated scope for payload edits that Spec 035 Phase 3 introduces (`nimbus.edit` in #400); endpoint Contributor |
| `nimbus_get_approval` | Takes the request's id, or the idempotency key the caller filed the request with. Returns the request's state, its expiry, the reviewer's note and, once the resubmit has been started, the operation id | Endpoint Reader |

- A request changes no message. It counts against the MCP request limit, not the change limit.
- Repeating an idempotency key returns the same request, whatever its state.
- A message has at most one pending request; a second one is refused.
- A lookup by key finds only a request the caller itself filed: a key belongs to the client that
  used it. A key under which that client filed nothing is answered with "no request under this
  key". That is an answer and not a refusal, so a caller can tell a request that never arrived from
  one that cannot be read.
- A caller that lost the answer to its request looks the request up by its key. It cannot repeat
  the call: the evidence may have changed since, and a key reused with other content is refused
  (§3.3).
- The caller names no evidence source. NimBus attaches the one the Owner bound to the field, so a
  caller cannot present a value as coming from anywhere else.

### 3.3 What NimBus refuses

| Refusal | Code |
|---|---|
| A path that is not on the event type's list of correctable fields (§4), is marked sensitive, or names an object or an array and not a single value | `[ActionNotAllowed]` |
| Evidence for a field that has no evidence source bound to it | `[ActionNotAllowed]` |
| A message that already has a pending request | `[ActionNotAllowed]` |
| A message that is no longer failed, or whose latest attempt is not the one the caller read | `[StaleMessage]` |
| A caller without the role, the switch or Contributor on the endpoint | `[PermissionDenied]` |
| A malformed request, or an idempotency key reused with other content | `[InvalidArgument]` |

Every `[ActionNotAllowed]` refusal carries a reason: `path_not_correctable`, `evidence_not_bound` or
`request_pending`. The refusal for a pending request also carries that request's id and the id of
the client that filed it, so that a caller can tell its own request from another's. A caller that
may file a request on an endpoint may also read one there, so this discloses nothing new.

The caller never sends or receives the full payload. What it may see of the payload stays governed
by the existing evidence and payload switches.

### 3.4 Storage

One record per request on all three providers, covered by the conformance suite: who asked and
through which client, the suspect fields with their evidence, the state, who decided, the values
they entered, the note, the expiry and the operation id. The idempotency key is kept with the
client that used it, so that a lookup by key finds only that client's request.

Free text and evidence values from the caller are untrusted. They are bounded, scrubbed and stored
as plain text, like a classification rationale, and are shown only to people who may act on the
endpoint. A request is deleted with its message, and under the retention rules that apply to the
message.

### 3.5 Web UI

An **Approvals** inbox lists pending requests for the endpoints a person may act on. A request shows
the message, the caller and its summary and, for each suspect field:

- the value in the message;
- the evidence, labelled as unverified and supplied by the caller: the bound system and field, the
  value and when it was read;
- an empty input for the corrected value.

The person types the value, or copies the evidence into the input with a separate action for each
field. There is no action that accepts all the evidence at once, and a field whose input is left
empty is not changed. The request's expiry is shown, with links to the message and back.

### 3.6 Deciding and executing

States: Pending, Approved, Rejected, Expired, Stale, and Failed for an approved request whose
resubmit could not be made.

- Approving needs what resubmit with changes needs today on that endpoint. It is checked when the
  person decides, not when the request was filed.
- The decision is one conditional update from Pending. Of two people who decide at once, one
  succeeds and the other is told the request is already decided.
- The same update records that the resubmit is owed. NimBus then runs the existing
  resubmit-with-changes command through the operation journal, with the values the person entered.
  The operation is keyed by the request, so it is made once.
- **If NimBus stops between the decision and the resubmit**, a background task finds the approved
  request without an operation and runs it. Until then `nimbus_get_approval` returns Approved with
  no operation id. A caller treats that as "under way" and does nothing to the message.
- If the message changed since the request, nothing is sent and the request becomes Stale.
- A request that nobody decides before its expiry becomes Expired. An Owner sets the default
  expiry.
- The request, the decision and the resubmit are audited. The resubmit names the person who
  approved and supplied the values, and the caller that asked.

### 3.7 Notification

NimBus posts a message with a link to the inbox, and no payload or evidence values, to a Teams
webhook set in Settings. Mail to the endpoint's alert subscribers is the fallback.

### 3.8 Access

- A new app role `Nimbus.RequestApproval` on the MCP resource registration.
- A new Owner switch in **Settings → MCP access**, "Workloads may request approvals", off by default.
- `nimbus_get_capabilities` reports whether the caller may request approvals.

### 3.9 Acceptance

- A request for a path that is not correctable, is sensitive or names an object is refused, and
  nothing is stored. So is evidence for a field with no bound source.
- A corrected message completes after a person enters the value, and the audit trail names both the
  person and the caller. Evidence is never applied unless the person copied or typed it.
- Two approvals at once produce one resubmit. Stopping the WebApp between the decision and the
  resubmit produces one resubmit, made after it starts again.
- A request for a message that has since changed becomes Stale and sends nothing. An expired request
  sends nothing.
- A caller without the role, with the switch off, or without Contributor on the endpoint is refused
  and audited.
- A caller finds its own request by the key it filed it with, in any state. Another client's lookup
  with the same key, and a key under which nothing was filed, are answered with "no request under
  this key".
- A second request for a message that has a pending one is refused with the reason, the pending
  request's id and the client that filed it.

## 4. Correction settings

Which fields of a message may be corrected, and what may be shown as evidence for each, is the
endpoint Owner's decision. NimBus enforces it.

- Per endpoint and event type, the Owner lists the JSON paths that may be corrected. Each path names
  a single value. The list is empty by default, so no approval request can be filed until an Owner
  adds a path.
- For each path the Owner can bind one **evidence source**: one field of one record that a record
  binding (§5) identifies. A caller may attach evidence only to a path that has one. By binding a
  source, the Owner also decides which system value the endpoint's operators get to see.
- The settings are stored with the endpoint's metadata on every provider, with conformance tests,
  and edited in the endpoint's settings. A change is audited.
- The settings limit approval requests only. What a signed-in person may change with resubmit with
  changes is not affected.

## 5. Record bindings

A message refers to records in the systems on both sides. Which ones is not something a caller
should work out from the payload, and not something the sender of the message should be able to
steer freely.

- Per endpoint and event type, the Owner lists **record bindings**. A binding says: the value at
  this payload path is the key of this entity in this endpoint's adapter.
- `nimbus_get_failure_evidence` returns the event type's record bindings and correction settings,
  as paths and names. It returns no keys or values beyond what the redacted payload already holds.
- A binding tells a caller which records a message legitimately names. It does not vouch for the
  message: a sender who puts another record's key at a bound path has named that record. What a
  caller may then read is decided by the adapter (§7).
- Bindings are stored and audited like the correction settings.

## 6. The adapter's MCP address on the endpoint

- `EndpointMetadata` gains `AdapterMcpUrl`, next to the source system, repository address and ticket
  link template added for Spec 044. It is a storage change on every provider, with conformance
  tests.
- It is edited on the endpoint's **Ownership** tab by an Owner, must be HTTPS, and a change is
  audited. It is returned by `nimbus_get_endpoint` to callers who may read the endpoint.
- NimBus stores and shows the address. It forwards no calls to it and holds no credentials for it:
  the caller connects to the adapter directly, and the adapter decides what the caller may read.

## 7. An adapter MCP kit

A small package an adapter host adds to serve a read-only MCP server next to its handlers. It
belongs with the adapter kit, not in the platform's core.

| Tool | Returns |
|---|---|
| `adapter_get_record` | The record a message is about, as it is now in the adapter's own system, by entity and key |
| `adapter_get_mapping` | How the adapter maps an event type to the system's fields, and the settings it runs with |
| `adapter_get_last_call` | What the system answered to the adapter's most recent call for a key: status and message |
| `adapter_get_health` | Whether the system is reachable from the adapter, and the version the adapter's deployment runs: the commit it was built from |

- Every tool only reads. The adapter author marks sensitive fields, and they are masked in the same
  way as in NimBus.
- **Sign-in.** A deployment has one Entra resource registration for its adapter MCP servers, separate
  from the NimBus MCP resource, with one app role for reading. An adapter accepts only tokens issued
  for that resource and carrying that role. A token for NimBus is not accepted by an adapter, and a
  token for adapters is not accepted by NimBus.
- The server logs every read with the caller, the tool and the key.
- The kit cannot limit a caller to the records of one message: a caller with the role can read any
  record by key. A caller that should be limited has to limit itself, with the record bindings of
  §5, and an adapter author can leave an entity out of the server.
- An adapter that serves no MCP server changes nothing for anyone.
- The package is `src/NimBus.Adapter.Mcp` in this repository (`Akaule.NimBus.Adapter.Mcp`), and the
  adapter kit references it; no core project does. Its guide is
  [adapter-mcp](../../adapter-mcp.md).

## 8. Paging for reports

- `nimbus_find_reports` takes a cursor and returns the next one, as `nimbus_find_messages` does. The
  page size stays at most 50.
- `nimbus_prepare_action` by `ticketId` selects from the messages under the ticket that are still
  failed, not from the newest reports, and says how many failed messages remain under the ticket
  after this batch.
- A batch by ticket still has no identity of its own: each message in it is its own operation, and a
  second preparation can select other messages. A caller that needs every message handled exactly
  once pages through the reports and prepares the messages one by one.

## 9. A report marker that does not overwrite

- `nimbus_set_message_reported` gains an option: write only when the message is unmarked or already
  carries the same ticket. With the option set, a message that carries another ticket is left as it
  is and reported back as such, per message.
- Without the option the tool behaves as today.
- A repeat of a call with the option set is harmless: it finds its own ticket and changes nothing.

## 10. Delivery

| Part | Scope | Touches |
|---|---|---|
| Wake-up webhook | §2 | The Resolver's alert delivery, the Alerts tab, `docs/notifications.md` |
| Approval requests | §3 | A new store member on every provider, a background task, `api-spec.yaml`, the MCP tools, the Approvals page, Settings, `docs/mcp-server.md`. The Web UI change comes with a screenshot |
| Correction settings and record bindings | §4, §5 | `EndpointMetadata` on every provider, the endpoint's settings, `nimbus_get_failure_evidence`. Needed before approval requests are useful |
| Adapter address | §6 | `EndpointMetadata` on every provider, the Ownership tab, `nimbus_get_endpoint` |
| Adapter MCP kit | §7 | `src/NimBus.Adapter.Mcp` in this repository, referenced by the adapter kit; the guide `docs/adapter-mcp.md` with the Entra set-up, linked from `docs/mcp-server.md` |
| Paging for reports | §8 | The report store query on every provider, the two MCP tools, `docs/mcp-server.md` |
| Report marker option | §9 | The report store write on every provider, the MCP tool |

## 11. Risks

| Risk | Mitigation |
|---|---|
| A forged or replayed wake-up starts work | The signature and the timestamp. The request carries ids only, so a receiver still reads everything through MCP under its own role |
| An endpoint Owner points a webhook at a service inside the Resolver's network | The Resolver connects to public addresses only, checked on the resolved address, and follows no redirect. The answer is never shown to the subscriber |
| An endpoint Owner has the Resolver's token sent to a URL of their own | A token is attached only under a URL prefix an administrator listed, for the audience listed with it, and only over HTTPS |
| An agent's evidence value is wrong or planted | NimBus never applies it, labels it unverified, and offers no way to accept all of it at once. A person supplies every value |
| A person copies planted evidence without looking | The source is one the Owner bound, its read time is shown, and the copy is a separate action for each field. This lowers the risk and does not remove it |
| An approval is decided on old evidence | The read time is shown and the request expires |
| An approval is given for a message that has changed | The request carries the message version; NimBus refuses and marks it Stale |
| A request widens what may be changed or shown | The Owner's correction settings; sensitive paths and whole objects are refused; evidence only from the bound source |
| A request smuggles a sensitive value in or out | Changes to sensitive paths are refused, evidence is shown only to people who may act on the endpoint, and free text is scrubbed |
| An approval runs twice, or not at all | One conditional decision, an operation keyed by the request, and a background task for an interrupted one |
| Requests flood the inbox | One open request per message, the MCP request limit, and the Owner's switch |
| A sender names another record's key in a bound path | The binding cannot tell. The adapter's masking and the Owner's choice of what to bind limit what is read and shown |
| An adapter's MCP server exposes more than intended | Read-only tools, masking of sensitive fields, its own Entra resource and role, and a read log |
| A hostile adapter address replays a caller's adapter token against another adapter | All adapters of a deployment accept the same audience, so the token is valid at each. Only an endpoint Owner can set an address, the change is audited, and it must be HTTPS. A deployment that cannot accept this registers a resource per adapter |

## 12. Open questions

1. Should approval requests later cover skip as well? Proposed: not in the first version.
2. Does the adapter register its own MCP address at start-up, or does an Owner enter it? Proposed:
   an Owner enters it, as for the repository address.
3. Should the adapter MCP kit be able to tie a read to one message, so that a caller cannot read
   other records? It would need the adapter to resolve a record from an event id.
4. Should a deployment be able to choose one Entra resource per adapter, to rule out the replay of
   §11? Proposed: yes, as an option, with one shared resource as the default.

## 13. Changes

### 13.1 Revision 2

| Change | Why |
|---|---|
| The caller flags fields; a person supplies the values (§3) | A value proposed by an agent cannot be shown to have come from a system. A person decides it |
| Correctable fields, set by the endpoint's Owner (§4) | The limit on what may be corrected belongs with the endpoint, and an agent must not be able to widen it |
| `nimbus_get_approval` returns the reviewer's note and the expiry (§3.2) | A caller needs them to act on a rejection |
| One conditional decision and an operation keyed by the request (§3.6) | Two approvals at once, or a crash, must not send two resubmits |
| The evidence's read time and an expiry (§3.5, §3.6) | The system's value can change between the request and the decision |
| One separate Entra resource for adapters (§7) | A token for NimBus should not open adapters. This was an open question |
| The adapter's deployed version in its health (§7) | A caller can tell whether a fix is deployed |
| Paging for reports (§8) | The lookup and resubmit by ticket stopped at 50 |
| A webhook frequency of `0` and what a used window means (§2) | A wake-up does not guarantee a quick look at every failure |

### 13.2 Revision 3

| Change | Why |
|---|---|
| Record bindings, set by the endpoint's Owner (§5) | A key that appears in a payload does not say which record a message refers to. The Owner does |
| An evidence source bound to each correctable field; the caller names none (§3.2, §4) | A caller could otherwise choose which system value an operator is shown |
| Evidence is labelled unverified, and is copied field by field or typed (§3.5) | A value shown next to an input reads as a suggestion. It must not become the answer by default |
| A background task completes an approved request that was interrupted (§3.6) | Approved must always end in a resubmit or a stated failure |
| Refusal codes for the approval tools (§3.3) | A caller needs to tell a stale message from a missing permission |
| A report marker option that does not overwrite (§9) | A repeated or late marker write could replace another person's ticket |
| What a batch by ticket does not guarantee (§8) | A caller that needs exactly once has to work message by message |
| The shared adapter audience as a stated risk and an option (§11, §12) | One resource for all adapters means a token is valid at each |

### 13.3 Revision 4

| Change | Why |
|---|---|
| `nimbus_get_approval` finds a request by the key its caller filed it with, and answers an unknown key as "no request under this key" (§3.2) | A caller that lost the answer to its request cannot repeat the call: it does not keep the evidence it sent, the evidence may have changed, and a key reused with other content is refused. It could then neither find its request nor file another |
| A reason on every `[ActionNotAllowed]` refusal of a request, and the pending request's id and client on the refusal for a pending request (§3.3) | Three different refusals shared one code. A caller needs to know that a request is pending and whether it is its own: from an earlier attempt of the message, or from before it lost its own state |
| The idempotency key is kept with the client that used it (§3.4) | A lookup by key must not find another client's request |

### 13.4 Revision 5

| Change | Why |
|---|---|
| The signing secret is derived from a deployment key and the subscription id, not stored (§2) | The Resolver signs and the WebApp shows the secret, and they share no key ring. A stored secret would be plain text in the message store and need a new store member on every provider |
| A bearer token for administrator-listed URL prefixes (§2, §11) | The Azure SRE Agent's HTTP trigger takes an Entra token and no signature. Without this every such receiver needs a relay that swaps one for the other |
| Windows of 5 and 15 minutes (§2) | With hourly as the shortest window, a receiver that starts a run per request had to choose between one run per failure and a wake-up up to an hour late |
| The Resolver connects to public addresses only and follows no redirect (§2, §11) | The URL is chosen by an endpoint Owner, and the Resolver may sit inside a private network |
| §2 is built | Status |

### 13.5 Revision 6

| Change | Why |
|---|---|
| §5–7 approved; Q2 (an Owner enters the address) and Q4 (one Entra resource for all adapters) settled as the spec proposed | The ops agent's phase 3 reads adapters through them |
| A binding's path is a plain dotted path (`$.a.b`), with no wildcards, indexes or filters (§5) | A caller takes exactly one key from the redacted payload; a query could name many records |
| Bindings and the address are stored with the endpoint's metadata (SQL migration 0032) and edited on the Ownership tab with the other ownership fields (§5, §6) | One Owner-only, audited write, as for the code location of Spec 044 |
| §5–6 are built | Status |

## 14. Out of scope

- An agent, its hosting, its model and its rules. Agents stay outside NimBus.
- An unattended caller changing a payload, or skipping a message.
- NimBus forwarding calls to an adapter's MCP server or to any other system.
- Service Bus actions over MCP: pause, purge and replay stay with people.
- Changes to Core, the SDK, the Resolver's message handling or the transport.
