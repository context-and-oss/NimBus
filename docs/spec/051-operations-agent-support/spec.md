# Spec 051 — Platform support for operations agents

Status: proposed (2026-11-15). Not yet approved; nothing in it is built.
Baseline: master `42eef889`; the latest release is v4.17.0.
Builds on [Spec 035](../035-mcp-operator-access/spec.md),
[Spec 037](../037-mcp-admin-access/spec.md),
[Spec 044](../044-agent-first/spec.md) and
[Spec 046](../046-mcp-guided-actions/spec.md).

An agent that handles failed messages over the operator MCP endpoint can classify, retry, report
and link failures today. This spec adds four things such an agent still lacks. It designs no agent:
agents stay outside NimBus, as Spec 044 decided, and NimBus still calls no model and holds no
repository or tracker credentials.

Each of the four is its own change with its own plan, and they can be delivered in any order.

## 1. What an agent cannot do today

Checked against the baseline.

| Need | Exists in v4.17.0 | Missing |
|---|---|---|
| Know that a failure happened without polling | The Resolver mails endpoint alert subscribers on a Failed or DeadLettered outcome. `webhook` subscriptions are stored but not delivered. Spec 044 §12 left waking an agent for later | Delivery to a webhook (§2) |
| Propose a corrected resubmit | Resubmit with changes in the Web UI and REST (`post-resubmit-with-changes-event-ids`), for a signed-in person. Spec 035 Phase 3 keeps payload edits with a person | A way for an agent to propose the change and a person to approve it (§3) |
| Look at the data in the source and target systems | Nothing. The endpoint records its owner, source system, repository address and ticket link | An address for the adapter's own MCP server (§4), and that server (§5) |

Everything else an agent uses is in place: `nimbus_get_worklist`, `nimbus_get_failure_evidence`,
`nimbus_record_classification`, the journaled resubmit with its budget and cap,
`nimbus_set_message_reported` and `nimbus_find_reports`.

## 2. A wake-up webhook

The Resolver already decides when an endpoint alert is due and mails it
([notifications](../../notifications.md#endpoint-alerts-alerts-tab)).

- The Resolver delivers a due alert to a subscription of type `webhook`: an HTTP POST with the
  endpoint id, event id, session id, event type, status and time. No payload and no error text; a
  receiver that wants more reads it through MCP under its own role.
- The request is signed with a secret per subscription (HMAC over the body and a timestamp), so the
  receiver can reject forgeries and replays. The secret is shown once, when the subscription is
  created.
- The existing frequency window and claim apply unchanged: a burst of failures sends one request.
- Delivery is best-effort and happens after the message is settled, as for mail. A slow or failing
  receiver never delays or dead-letters a message. A receiver is expected to sweep on a timer as
  well, so a lost request delays its work and does not lose it.
- The URL must be HTTPS.

## 3. Approval requests

An agent proposes a change to a message; a person approves it; NimBus carries it out.

### 3.1 Scope

The first version covers one action: resubmit with changes. Skip is not included (§8, question 1).

### 3.2 Tools

| Tool | Does | Needs |
|---|---|---|
| `nimbus_request_approval` | Files a request: endpoint id, event id, the message version the caller read, a list of changes (JSON path, new value, reason, evidence reference), a summary and an idempotency key | `Nimbus.RequestApproval` for a workload, or the delegated `nimbus.resubmit` scope for a person's agent; endpoint Contributor |
| `nimbus_get_approval` | Returns the request's state and, once run, the operation id | Endpoint Reader |

A request changes no message. It counts against the MCP request limit, not the change limit. A
message has at most one pending request; a second one is refused.

### 3.3 A patch, not a payload

NimBus applies the changes to the stored payload. It refuses:

- a path that is marked sensitive in the event's metadata;
- a change that makes the payload invalid for its event type;
- a message that is no longer failed, or whose latest attempt is not the one the caller read.

The caller never sends or receives the full payload. What it may see of the payload stays governed
by the existing evidence and payload switches.

### 3.4 Storage

One record per request on all three providers, covered by the conformance suite: who asked and
through which client, the changes, the state (Pending, Approved, Rejected, Expired, Stale), who
decided, the note, the expiry and the operation id. Free text from the caller is bounded, scrubbed
and stored as plain text, like a classification rationale.

### 3.5 Web UI

An **Approvals** inbox lists pending requests for the endpoints a person may act on. A request shows
the message, the caller, the summary, each field before and after, the evidence reference and the
expiry. Sensitive values are masked unless the viewer may read payloads. The page links to the
message and back.

### 3.6 Deciding and executing

- Approving needs what resubmit with changes needs today on that endpoint.
- On approval NimBus runs the existing resubmit-with-changes command through the operation journal.
  If the message changed since the request, nothing is sent and the request becomes Stale.
- A request that nobody decides before its expiry becomes Expired. The default expiry is set by an
  Owner.
- The request, the decision and the resubmit are audited. The resubmit names the person who
  approved and the caller that asked.

### 3.7 Notification

NimBus posts a message with a link to the inbox, and no payload values, to a Teams webhook set in
Settings. Mail to the endpoint's alert subscribers is the fallback.

### 3.8 Access

- A new app role `Nimbus.RequestApproval` on the MCP resource registration.
- A new Owner switch in **Settings → MCP access**, "Workloads may request approvals", off by default.
- `nimbus_get_capabilities` reports whether the caller may request approvals.

## 4. The adapter's MCP address on the endpoint

- `EndpointMetadata` gains `AdapterMcpUrl`, next to the source system, repository address and ticket
  link template added for Spec 044. It is a storage change on every provider, with conformance
  tests.
- It is edited on the endpoint's **Ownership** tab and returned by `nimbus_get_endpoint` to callers
  who may read the endpoint.
- NimBus stores and shows the address. It forwards no calls to it and holds no credentials for it:
  the caller connects to the adapter directly, and the adapter decides what the caller may read.

## 5. An adapter MCP kit

A small package an adapter host adds to serve a read-only MCP server next to its handlers. It
belongs with the adapter kit, not in the platform's core.

| Tool | Returns |
|---|---|
| `adapter_get_record` | The record a message is about, as it is now in the adapter's own system, by entity and key |
| `adapter_get_mapping` | How the adapter maps an event type to the system's fields, and the settings it runs with |
| `adapter_get_last_call` | What the system answered to the adapter's most recent call for a key: status and message |
| `adapter_get_health` | Whether the system is reachable from the adapter |

- Every tool only reads. The adapter author marks sensitive fields, and they are masked in the same
  way as in NimBus.
- The server accepts an Entra token and logs every read. Which resource the token is issued for is
  open (§8, question 2).
- An adapter that serves no MCP server changes nothing for anyone.

## 6. Delivery

| Part | Scope | Touches |
|---|---|---|
| Wake-up webhook | §2 | The Resolver's alert delivery, the Alerts tab, `docs/notifications.md` |
| Approval requests | §3 | A new store member on every provider, `api-spec.yaml`, the MCP tools, the Approvals page, Settings, `docs/mcp-server.md`. The Web UI change comes with a screenshot |
| Adapter address | §4 | `EndpointMetadata` on every provider, the Ownership tab, `nimbus_get_endpoint` |
| Adapter MCP kit | §5 | The adapter kit repository and a guide under `docs/` |

## 7. Risks

| Risk | Mitigation |
|---|---|
| A forged or replayed wake-up starts work | The signature and the timestamp. The request carries ids only, so a receiver still reads everything through MCP under its own role |
| An approval is given for a message that has changed | The request carries the message version; NimBus refuses and marks it Stale |
| A request smuggles a sensitive value in or out | Changes to sensitive paths are refused, values are masked in the inbox, and free text is scrubbed |
| Requests flood the inbox | One open request per message, the MCP request limit, and the Owner's switch |
| An adapter's MCP server exposes more than intended | Read-only tools, masking of sensitive fields, an Entra token and a read log |
| The address field is used to point agents at a hostile server | Only an endpoint Owner can set it, the change is audited, and it must be HTTPS |

## 8. Open questions

1. Should approval requests later cover skip as well? Proposed: not in the first version.
2. How does a caller sign in to an adapter's MCP server: a token for the NimBus MCP resource, or a
   separate resource for adapters with its own role? Proposed: a separate resource, so a token for
   NimBus is not accepted elsewhere.
3. Does the adapter register its own MCP address at start-up, or does an Owner enter it? Proposed:
   an Owner enters it, as for the repository address.

## 9. Out of scope

- An agent, its hosting, its model and its rules. Agents stay outside NimBus.
- Skipping a message or editing a payload without a person's approval.
- NimBus forwarding calls to an adapter's MCP server or to any other system.
- Service Bus actions over MCP: pause, purge and replay stay with people.
- Changes to Core, the SDK, the Resolver's message handling or the transport.
