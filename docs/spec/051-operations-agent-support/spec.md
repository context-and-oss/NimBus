# Spec 051 — Platform support for operations agents

Status: proposed, revision 2 (2026-11-15). Revised after a review of an agent design that builds on
this spec; the changes are listed in §11. Not yet approved; nothing in it is built.
Baseline: master `7122b1bd`; the latest release is v4.17.0.
Builds on [Spec 035](../035-mcp-operator-access/spec.md),
[Spec 037](../037-mcp-admin-access/spec.md),
[Spec 044](../044-agent-first/spec.md) and
[Spec 046](../046-mcp-guided-actions/spec.md).

An agent that handles failed messages over the operator MCP endpoint can classify, retry, report
and link failures today. This spec adds what such an agent still lacks. It designs no agent: agents
stay outside NimBus, as Spec 044 decided, and NimBus forwards no calls to other systems and holds no
repository or tracker credentials.

Each part is its own change with its own plan, and they can be delivered in any order.

## 1. What an agent cannot do today

Checked against the baseline.

| Need | Exists in v4.17.0 | Missing |
|---|---|---|
| Know that a failure happened without polling | The Resolver mails endpoint alert subscribers on a Failed or DeadLettered outcome. `webhook` subscriptions are stored but not delivered. Spec 044 §12 left waking an agent for later | Delivery to a webhook (§2) |
| Prepare a corrected resubmit for a person | Resubmit with changes in the Web UI and REST (`post-resubmit-with-changes-event-ids`), for a signed-in person. Spec 035 Phase 3 (#400, open) adds the same for a signed-in person's agent. An unattended workload cannot change a payload, and should not | A way for a workload to flag what is wrong and hand the decision to a person (§3), limited to fields the endpoint's Owner allows (§4) |
| Look at the data in the source and target systems | Nothing. The endpoint records its owner, source system, repository address and ticket link | An address for the adapter's own MCP server (§5), and that server (§6) |
| Work through every failure under a ticket | `nimbus_find_reports` returns at most 50 rows with no cursor. `nimbus_prepare_action` by `ticketId` takes the 50 newest reports and then drops those that are no longer failed, so older failed messages under the same ticket are never reached | Paging (§7) |

Everything else an agent uses is in place: `nimbus_find_messages` with a cursor,
`nimbus_get_failure_evidence`, `nimbus_record_classification`, the journaled resubmit with its
budget and cap, and `nimbus_set_message_reported`.

## 2. A wake-up webhook

The Resolver already decides when an endpoint alert is due and mails it
([notifications](../../notifications.md#endpoint-alerts-alerts-tab)).

- The Resolver delivers a due alert to a subscription of type `webhook`: an HTTP POST with the
  endpoint id, event id, session id, event type, status and time. No payload and no error text; a
  receiver that wants more reads it through MCP under its own role.
- The request is signed with a secret per subscription (HMAC over the body and a timestamp), so the
  receiver can reject forgeries and replays. The secret is shown once, when the subscription is
  created.
- The existing frequency window and claim apply unchanged: a burst of failures sends one request,
  and a failure inside a window that has already been used sends none. A frequency of `0` sends
  every failure. A receiver that needs every failure quickly uses `0` and drops duplicates itself.
- Delivery is best-effort and happens after the message is settled, as for mail. A slow or failing
  receiver never delays or dead-letters a message. A receiver is expected to sweep on a timer as
  well, so a lost request delays its work and does not lose it.
- The URL must be HTTPS.

## 3. Approval requests

An unattended agent flags what it believes is wrong in a failed message. A person supplies the
corrected value and approves. NimBus carries it out.

### 3.1 Scope

The first version covers one action: resubmit with changes. Skip is not included (§10, question 1).

The caller proposes which fields are wrong, never their new values. A value in a request is
evidence for the person to judge, and NimBus never applies it on its own.

### 3.2 Tools

| Tool | Does | Needs |
|---|---|---|
| `nimbus_request_approval` | Files a request: endpoint id, event id, the message version the caller read, a list of suspect fields, a summary and an idempotency key. Each suspect field has its JSON path, a reason and optional evidence: the system and field a value was read from, that value and the time it was read | `Nimbus.RequestApproval` for a workload, or, for a person's agent, the delegated scope for payload edits that Spec 035 Phase 3 introduces (`nimbus.edit` in #400); endpoint Contributor |
| `nimbus_get_approval` | Returns the request's state, its expiry, the reviewer's note and, once run, the operation id | Endpoint Reader |

- A request changes no message. It counts against the MCP request limit, not the change limit.
- Repeating an idempotency key returns the same request.
- A message has at most one pending request; a second one is refused.

### 3.3 What NimBus refuses

- a path that is not on the event type's list of correctable fields (§4);
- a path that is marked sensitive in the event's metadata;
- a path that names an object or an array and not a single value;
- a message that is no longer failed, or whose latest attempt is not the one the caller read.

The caller never sends or receives the full payload. What it may see of the payload stays governed
by the existing evidence and payload switches.

### 3.4 Storage

One record per request on all three providers, covered by the conformance suite: who asked and
through which client, the suspect fields with their evidence, the state (Pending, Approved,
Rejected, Expired, Stale), who decided, the values they entered, the note, the expiry and the
operation id.

Free text and evidence values from the caller are untrusted. They are bounded, scrubbed and stored
as plain text, like a classification rationale, and are shown only to people who may act on the
endpoint. A request is deleted with its message, and under the retention rules that apply to the
message.

### 3.5 Web UI

An **Approvals** inbox lists pending requests for the endpoints a person may act on. A request shows
the message, the caller and its summary and, for each suspect field:

- the value in the message;
- the evidence: which system and field, the value read and when it was read;
- an input for the corrected value, which the person fills in or confirms field by field.

It also shows the request's expiry and links to the message and back. Nothing is prefilled as
decided: a field the person does not confirm is not changed.

### 3.6 Deciding and executing

- Approving needs what resubmit with changes needs today on that endpoint. It is checked when the
  person decides, not when the request was filed.
- The decision is one conditional update from Pending. Of two people who decide at once, one
  succeeds and the other is told the request is already decided.
- On approval NimBus runs the existing resubmit-with-changes command through the operation journal,
  with the values the person entered. The operation is keyed by the request, so a repeat after a
  crash returns the same operation and sends nothing twice.
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
  nothing is stored.
- A corrected message completes after a person enters the value, and the audit trail names both the
  person and the caller.
- Two approvals at once produce one resubmit. Killing the WebApp between the decision and the
  resubmit, and repeating the approval, produces one resubmit.
- A request for a message that has since changed becomes Stale and sends nothing. An expired request
  sends nothing.
- A caller without the role, with the switch off, or without Contributor on the endpoint is refused
  and audited.

## 4. Correctable fields

Which fields of a message may be corrected at all is the endpoint Owner's decision, and NimBus
enforces it.

- Per endpoint and event type, the Owner lists the JSON paths that may be corrected. Each path names
  a single value. The list is empty by default, so no approval request can be filed until an Owner
  adds a path.
- The list is stored with the endpoint's metadata on every provider, with conformance tests, and
  edited in the endpoint's settings. A change is audited.
- `nimbus_get_failure_evidence` returns the list for the message's event type, as paths, so a caller
  does not file requests that will be refused.
- The list limits approval requests only. What a signed-in person may change with resubmit with
  changes is not affected.

## 5. The adapter's MCP address on the endpoint

- `EndpointMetadata` gains `AdapterMcpUrl`, next to the source system, repository address and ticket
  link template added for Spec 044. It is a storage change on every provider, with conformance
  tests.
- It is edited on the endpoint's **Ownership** tab by an Owner, must be HTTPS, and a change is
  audited. It is returned by `nimbus_get_endpoint` to callers who may read the endpoint.
- NimBus stores and shows the address. It forwards no calls to it and holds no credentials for it:
  the caller connects to the adapter directly, and the adapter decides what the caller may read.

## 6. An adapter MCP kit

A small package an adapter host adds to serve a read-only MCP server next to its handlers. It
belongs with the adapter kit, not in the platform's core.

| Tool | Returns |
|---|---|
| `adapter_get_record` | The record a message is about, as it is now in the adapter's own system, by entity and key |
| `adapter_get_mapping` | How the adapter maps an event type to the system's fields, and the settings it runs with |
| `adapter_get_last_call` | What the system answered to the adapter's most recent call for a key: status and message |
| `adapter_get_health` | Whether the system is reachable from the adapter, and the adapter's deployed version: the commit it was built from |

- Every tool only reads. The adapter author marks sensitive fields, and they are masked in the same
  way as in NimBus.
- **Sign-in.** A deployment has one Entra resource registration for its adapter MCP servers, separate
  from the NimBus MCP resource, with one app role for reading. An adapter accepts only tokens issued
  for that resource and carrying that role. A token for NimBus is not accepted by an adapter, and a
  token for adapters is not accepted by NimBus.
- The server logs every read with the caller, the tool and the key.
- The kit cannot limit a caller to the records of one message: a caller with the role can read any
  record by key. A caller that should be limited has to limit itself, and an adapter author can
  leave an entity out of the server.
- An adapter that serves no MCP server changes nothing for anyone.

## 7. Paging for reports

- `nimbus_find_reports` takes a cursor and returns the next one, as `nimbus_find_messages` does. The
  page size stays at most 50.
- `nimbus_prepare_action` by `ticketId` selects from the messages under the ticket that are still
  failed, not from the newest reports. Its result says how many failed messages remain under the
  ticket after this batch, so a caller can prepare the next batch until none remain. A batch stays
  at most 50, and each batch is its own journaled operation set and counts against the budget.

## 8. Delivery

| Part | Scope | Touches |
|---|---|---|
| Wake-up webhook | §2 | The Resolver's alert delivery, the Alerts tab, `docs/notifications.md` |
| Approval requests | §3 | A new store member on every provider, `api-spec.yaml`, the MCP tools, the Approvals page, Settings, `docs/mcp-server.md`. The Web UI change comes with a screenshot |
| Correctable fields | §4 | `EndpointMetadata` on every provider, the endpoint's settings, `nimbus_get_failure_evidence`. Needed before approval requests are useful |
| Adapter address | §5 | `EndpointMetadata` on every provider, the Ownership tab, `nimbus_get_endpoint` |
| Adapter MCP kit | §6 | The adapter kit repository, a guide under `docs/` and the Entra set-up in `docs/mcp-server.md` |
| Paging for reports | §7 | The report store query on every provider, the two MCP tools, `docs/mcp-server.md` |

## 9. Risks

| Risk | Mitigation |
|---|---|
| A forged or replayed wake-up starts work | The signature and the timestamp. The request carries ids only, so a receiver still reads everything through MCP under its own role |
| An agent's evidence value is wrong or planted | NimBus never applies it. A person enters or confirms every value, with the time the evidence was read in front of them |
| An approval is decided on old evidence | The read time is shown and the request expires |
| An approval is given for a message that has changed | The request carries the message version; NimBus refuses and marks it Stale |
| A request widens what may be changed | The Owner's list of correctable fields, sensitive paths and whole objects are refused |
| A request smuggles a sensitive value in or out | Changes to sensitive paths are refused, evidence is shown only to people who may act on the endpoint, and free text is scrubbed |
| An approval runs twice | One conditional decision, and an operation keyed by the request |
| Requests flood the inbox | One open request per message, the MCP request limit, and the Owner's switch |
| An adapter's MCP server exposes more than intended | Read-only tools, masking of sensitive fields, its own Entra resource and role, and a read log |
| The address field is used to point agents at a hostile server | Only an endpoint Owner can set it, the change is audited, and it must be HTTPS |

## 10. Open questions

1. Should approval requests later cover skip as well? Proposed: not in the first version.
2. Does the adapter register its own MCP address at start-up, or does an Owner enter it? Proposed:
   an Owner enters it, as for the repository address.
3. Should the adapter MCP kit be able to tie a read to one message, so that a caller cannot read
   other records? It would need the adapter to resolve a record from an event id.

## 11. Changes in revision 2

| Change | Why |
|---|---|
| The caller flags fields; a person supplies the values (§3) | A value proposed by an agent cannot be shown to have come from a system. A person decides it |
| Correctable fields, set by the endpoint's Owner (§4) | The limit on what may be corrected belongs with the endpoint, and an agent must not be able to widen it |
| `nimbus_get_approval` returns the reviewer's note and the expiry (§3.2) | A caller needs them to act on a rejection |
| One conditional decision and an operation keyed by the request (§3.6) | Two approvals at once, or a crash, must not send two resubmits |
| The evidence's read time and an expiry (§3.5, §3.6) | The system's value can change between the request and the decision |
| One separate Entra resource for adapters (§6) | A token for NimBus should not open adapters. This was an open question |
| The adapter's deployed version in its health (§6) | A caller can tell whether a fix is deployed |
| Paging for reports (§7) | The lookup and resubmit by ticket stopped at 50 |
| A webhook frequency of `0` and what a used window means (§2) | A wake-up does not guarantee a quick look at every failure |

## 12. Out of scope

- An agent, its hosting, its model and its rules. Agents stay outside NimBus.
- An unattended caller changing a payload, or skipping a message.
- NimBus forwarding calls to an adapter's MCP server or to any other system.
- Service Bus actions over MCP: pause, purge and replay stay with people.
- Changes to Core, the SDK, the Resolver's message handling or the transport.
