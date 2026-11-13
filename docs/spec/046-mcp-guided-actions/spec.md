# Spec 046 — Guided actions for the operator MCP server

Status: draft, revision 2 (2026-11-13). Revised after a review of revision 1 against the code; the
review log at the end maps each finding to the section that addresses it. Not yet approved.
Plan for phase 1: [2026-11-13-spec-046-mcp-guided-actions.md](../../plan/2026-11-13-spec-046-mcp-guided-actions.md).
Baseline: master `5556ea82`. Builds on [Spec 035](../035-mcp-operator-access/spec.md),
[Spec 037](../037-mcp-admin-access/spec.md),
[Spec 040](../040-mcp-operator-prompts-and-plugin/spec.md),
[Spec 043](../043-intelligent-failed-message-insights/spec.md) and
[Spec 044](../044-agent-first/spec.md).
Scope: `NimBus.WebApp` (`Mcp/`, `Services/Insights/`, the **Connect** card in `ClientApp`),
`tests/NimBus.WebApp.Tests` and `docs/mcp-server.md`. Unchanged: scopes, roles, the Spec 037 access
policy and its storage, the operation journal, every storage interface, `api-spec.yaml`, every
`NimBus__Mcp__*` setting, and the contract of every existing tool argument.

## 1. Why

An operator in Claude Desktop or Codex who wants to resubmit a failed message has to know what to
ask for. The server has 20 tools and 5 prompts, and the path to one resubmit is six calls:
`nimbus_get_capabilities`, `nimbus_get_overview`, `nimbus_find_messages`, `nimbus_get_message`,
`nimbus_prepare_action`, `nimbus_resubmit_message`. Four things make that harder than it should be:

- **The options are prose.** `nimbus_get_message` returns `eligibleActions` as bare names
  (`resubmit`, `skip`, `report`, `classify`). What each one means, which tool runs it and with which
  arguments is in the server instructions and the prompts. Each agent turns that into a different
  question, and some turn it into no question at all.
- **There is no starting point.** No single call answers "what needs me?". `nimbus_get_metrics`
  groups failures by cause, but needs a site-wide Reader role and returns no ids to act on.
  `nimbus_get_overview` returns counts only. `nimbus_find_messages` returns ids but no error text,
  so grouping by cause takes one `nimbus_get_message` per message.
- **Prompt arguments are free text.** `recover-message` takes `endpointId`, `eventId` and `action`
  as typed strings. A client that shows prompts as commands or menu entries can't offer a list.
- **Only Claude Code is documented.** `docs/mcp-server.md` and the WebApp's **Connect an agent**
  card give a `claude mcp add` command and nothing for Claude Desktop or Codex.

The confirmation before a resubmit or skip is also a promise by the model, not a fact of the
protocol: the server instructions tell the agent to ask, and the server can't tell whether it did.
The client's own permission prompt before a tool call is the only gate a person is sure to see, and
a user can switch it off.

[HandleMCP](https://handlemcp.dk/), a small local MCP server for grocery shopping, was read for
ideas. Most of what it does well NimBus already has (a preview before a write, idempotent writes,
scrubbed results, a boundary enforced by a test: `A_workload_can_never_skip`). Three of its ideas
are adopted here: a copy-paste prompt that lets the assistant set the server up while the person
signs in (§6.2), example conversations in the docs (§6.3), and an error table that says what to do
(§6.4). Its usage telemetry is not adopted (§9).

## 2. Decisions

Proposed. Each needs confirming before implementation.

| Topic | Decision |
|---|---|
| How options reach the user | **Structured `nextActions` in tool results** (§3). It works in every client and needs nothing from the protocol beyond structured content. The client's own question UI renders the choice. |
| Starting point | **One new read tool, `nimbus_get_worklist`** (§4), for endpoint Readers. It returns open failures grouped by cause, each with ids and `nextActions`. |
| Worklist data | **The Spec 043 failure occurrences**, filtered per caller to readable endpoints. No new store query and no per-message reads. |
| Prompt arguments | **Completions** for `endpointId`, `period`, `action` and `unattended` (§5). `eventId` is not completed. |
| Who builds the options | **The server, from fixed text and ids.** A label never contains error text, log text or payload values. |
| What an option may offer | **Only what the caller could run now.** Options come from the same checks as `eligibleActions`. An option is a hint: every tool it names still authorizes, audits and rate-limits the call. |
| Confirmation | **Unchanged in phase 1.** The preview token, the audit reason and the agent's question stay as they are. Phase 2 adds a confirmation form through MCP elicitation where the client supports it (§7). |
| Inline UI (MCP Apps) | **Not in this spec** (§9). |
| Compatibility | **Additive only.** New result members and one new tool. `eligibleActions` and `next` stay. |

## 3. `nextActions`

### 3.1 Shape

A result that has a sensible next step carries `nextActions`, a list of at most 6:

```json
{
  "id": "resubmit",
  "label": "Resubmit this message",
  "kind": "change",
  "tool": "nimbus_prepare_action",
  "arguments": { "action": "resubmit", "endpointId": "erp", "eventId": "…", "messageVersion": "…" },
  "needs": [],
  "confirm": true
}
```

| Member | Meaning |
|---|---|
| `id` | Stable within the result: `investigate`, `resubmit`, `skip`, `report`, `classify`, `run`, `check`, `open_web_ui`, `resubmit_ticket`. |
| `label` | Fixed English text chosen by the server. Never contains data. |
| `kind` | `read`, `change` (changes NimBus state or sends a command) or `destructive` (skip). |
| `tool` | The tool to call. Null for `open_web_ui`, which carries `url` instead. |
| `arguments` | Arguments the server already knows. Only ids, versions and tokens the same result already holds. |
| `needs` | Arguments the agent must add: `reason`, `idempotencyKey`, `ticketId`. |
| `confirm` | True when a person present must say yes before the tool that changes something runs. |

`arguments` can hold an event id, which the producer chose. It is data, like every id in a result,
and it is already there today; the fixed `label` is what the agent shows.

The first option is the one the server recommends when it has grounds to, otherwise the least
risky. Grounds are a saved classification's guidance (`RetryMayHelp` puts `resubmit` first) and the
message's state. Without a saved classification, `investigate` comes first.

### 3.2 Where

| Result | Options |
|---|---|
| `nimbus_get_message` | One per entry in `eligibleActions`, plus `investigate` (`nimbus_get_message_history`) and, for deferred messages and pending handoffs, `open_web_ui`. |
| `nimbus_get_worklist` (§4) | Per group: `investigate` the example, `resubmit` the example, `resubmit_ticket` when the group is reported under one ticket, `report`, `classify`. |
| `nimbus_prepare_action` | `run` (`nimbus_resubmit_message` or `nimbus_skip_message` with the `actionToken`, `needs` `reason` and `idempotencyKey`). |
| `nimbus_resubmit_message`, `nimbus_skip_message` | `check` (`nimbus_get_operation` with the `operationId`). A batch result has none at the top; each item already carries its `operationId`. |
| `nimbus_get_operation` | By status: `check` again while not terminal; `classify` after `Failed`; `resubmit` after `NotSent`; none after `Succeeded`; `open_web_ui` after `OutcomeUnknown`. Never `resubmit` after `OutcomeUnknown`. |

`eligibleActions` and the `next` text stay, so existing agents and the prompts keep working.

### 3.3 Rules

- Options are computed by `OperatorActionAccess.EligibleActionsAsync` and the checks
  `nimbus_prepare_action` already makes. The code that decides eligibility is not duplicated.
- A workload (`isWorkload`) is never offered `skip`, and its options have `confirm: false`.
- An option is omitted, not disabled, when the caller can't run it. This keeps the rule that a
  resource the caller can't read looks like one that doesn't exist.
- No audit entry is written for offering an option. The call that follows is audited as today.

### 3.4 Server instructions

A rule 7 is added after the six existing rules: when a result has `nextActions`, offer them to the
user as a short numbered choice, in the order given, with the client's question UI when it has one.
It adds about 250 characters to the 3,541 there today, under the 4,096 limit. The four safety
phrases that must stay within the first 2,048 characters are in rules 1 to 5 and don't move. The
confirmation rule itself is rule 2 and is not repeated. The five prompts get the same sentence in
`CommonRules`.

## 4. `nimbus_get_worklist`

"What needs me?" in one call, for a caller who is Reader on one or more endpoints.

**Arguments:** `endpointId` (optional; default every readable endpoint), `period` (as
`nimbus_get_metrics`; default `1d`), `limit` (groups, 1–20; default 10).

### 4.1 Data

Revision 1 scanned each endpoint with the message search. That can't work: a search row carries no
error text, so a cause needs one details read per message, and each details read writes an audit
row.

The Spec 043 Insights service already has what is needed. It reads every failed attempt in a window
with one store query (`GetFailedMessageInsights(from, to)`), caches the result for all callers
under single-flight, and normalizes each row's error into a pattern. Each row has the endpoint,
event type, event id, session id, failed attempt's message id, time and publishing endpoint.

- `IFailedInsightsQuery` gets one new method that returns the occurrences of a window restricted to
  a set of endpoints the caller passes in. It checks no role itself. The existing methods keep
  their site-Reader check.
- The tool passes the caller's readable endpoints from `OperatorEndpointCatalog`, which already
  leaves out hidden ones. Rows are filtered per caller after the shared cache, as Insights does
  today, so the cache never holds anything caller-specific.
- Rows are grouped by endpoint, event type and error pattern.

An occurrence is a failed attempt, not an open message: a message that failed and was resubmitted
successfully is still an occurrence. So for each of the largest groups, up to `limit`, the tool
reads the current status of the group's 50 newest distinct events with one `GetEventsByIds` call
(stored ids, `{EventId}_{SessionId}`, as the Insights drill-down does). A group with no open
message among those is left out and counted in `resolvedGroups`. That is at most 20 status reads
per call, one bulk classification read, and one `FindCurrentAsync` per returned group for its
example's `messageVersion`.

### 4.2 Result

`groups`, most open messages first; `resolvedGroups`; `totals` (the `nimbus_get_overview` totals
for the same endpoints); and `olderFailuresLikely`, true when `totals` shows more failed and
dead-lettered messages than the groups account for, which means the period hides older ones. Each
group:

| Member | Source |
|---|---|
| `endpointId`, `eventTypeId` | The occurrences. |
| `errorPattern` | The normalized pattern. Untrusted. |
| `occurrences`, `firstSeen`, `lastSeen` | Failed attempts in the period. |
| `checked`, `open` | How many of the newest events were checked, and how many are still Failed or DeadLettered. |
| `blockedSessions` | Distinct sessions among the open Failed ones, which hold later messages back. |
| `exampleEventId`, `exampleMessageVersion` | The newest open message, to act on without another read. |
| `classification` | The example's saved classification summary, when classification is enabled. Advisory. |
| `ticketId` | Set when every reported open message checked shares one ticket. |
| `acknowledged` | The endpoint's Monitor acknowledgement, when someone is already looking. |
| `nextActions` | As §3.2. |

### 4.3 Limits

- **Unsupported and deferred messages are not grouped.** They have no failed attempt with an error.
  They appear in `totals` only.
- **A failure older than the period is not grouped.** `olderFailuresLikely` says so, and the agent
  can ask again with a longer period.
- **Freshness.** The occurrence read is cached for the metrics TTL (30 seconds). Status and
  `messageVersion` are read live, so an option never rests on a stale state.
- **Whole-group resubmit is not offered.** The only batch path stays `nimbus_prepare_action` with
  `ticketId` (Spec 044 Phase 3), so a group must be reported under a ticket first. See §10,
  question 2.

**Access:** endpoint Reader, per endpoint. Annotations as the other read tools (`ReadOnly`,
`Idempotent`, not `OpenWorld`). Like them it has no switch in the Spec 037 policy; the policy's
hidden endpoints and people, client and workload rules apply. `endpointId` for an endpoint the
caller can't read answers `[EndpointNotFound]`.

The `triage` prompt is rewritten to call `nimbus_get_worklist` in place of its steps 3 to 6.

## 5. Prompt argument completions

MCP clients ask `completion/complete` while the user fills in a prompt argument.

| Argument | Values |
|---|---|
| `period` | `1h`, `12h`, `1d`, `3d`, `7d`, `30d` |
| `action` | `resubmit`, `skip` |
| `unattended` | `true`, `false` |
| `endpointId` | The caller's readable endpoints from `OperatorEndpointCatalog`, filtered by the typed prefix, at most 100 |

One handler, registered with `WithCompleteHandler`, serves all four from the constants
`OperatorPrompts` already validates against. The SDK can also derive completions from
`[AllowedValues]` on a parameter, but that is not used: whether the attribute also changes how an
empty argument is treated is unknown, and an empty optional argument must keep counting as omitted.

- `action` is not filtered per caller. `recover-message` is already hidden from a caller who may
  change nothing.
- A completion for a prompt the caller can't list returns nothing, through a complete filter
  (`AddCompleteFilter`) beside the existing `McpPromptAccess` filters.
- `endpointId` completions reveal exactly what `nimbus_list_endpoints` does.
- Completions are not audited.
- `eventId` is not completed. A list of bare GUIDs helps nobody, and completion values have no
  label. `nimbus_get_worklist` is the way to find a message.

**Rate limit.** `/mcp` allows 60 requests per 60 seconds per caller, counted on the route, so a
completion counts like a tool call. A client that asks on every keystroke could use up the budget
the user needs for the tools. Phase 1 keeps the single limit and measures it in the pilot (§10,
question 4). If it bites, completions get their own limit, which means the limiter has to read the
JSON-RPC method.

## 6. Connecting and docs

### 6.1 Clients

`docs/mcp-server.md` gets a **Connect a client** section with exact steps for Claude Code, Claude
Desktop and Codex, for local development and for Entra. The WebApp's **Connect an agent** card
(`mcp-access-settings.tsx`) shows the same per client, filled with the deployment's URL and an
approved client id.

Two things may rule a client out, and both need a pilot before any step is written (§10,
question 1):

- **Entra has no dynamic client registration.** The client must let the user supply a
  pre-registered client id, and its redirect URI must be registered.
- **Where the connection comes from.** A client that connects to remote servers from its vendor's
  cloud, not from the user's machine, can't reach a local Aspire stack, and reaches a deployment in
  [private networking mode](../034-private-networking/spec.md) only through the Application
  Gateway.

The section ships only for clients that were connected end to end, and says plainly which
combinations don't work.

### 6.2 "Ask your assistant"

The section opens with a prompt to paste into the assistant:

> Add the NimBus operator MCP server at `https://<webapp>/mcp` with client id `<client-id>`. Don't
> sign in for me: tell me when to do it in the browser. Then call `nimbus_get_capabilities` and
> tell me which endpoints I can read and what I may do.

The assistant adds the server; the person signs in. Nobody types a credential into a chat.

### 6.3 Example conversations

Three short transcripts: "what needs me?" (worklist, a numbered choice), "resubmit it" (preview,
yes, operation outcome) and a Reader who is offered no change. Each shows the user's sentence, the
tools called in order and the closing question.

### 6.4 Errors

The Troubleshooting table gains one row per tool error code (`[StaleMessage]`, `[OutcomeUnknown]`,
`[ActionNotAllowed]`, `[RateLimited]`, `[AuditUnavailable]`, `[FeatureUnavailable]`,
`[SourceUnavailable]`, `[InvalidCursor]`) with what the operator should do. The error texts
themselves already say what to do next and are not changed.

## 7. Phase 2: a confirmation form

Phase 1 leaves the yes with the agent. Phase 2 lets the server ask for it.

The endpoint is stateless, and stays so. The SDK (`ModelContextProtocol.AspNetCore` 2.3.1) supports
elicitation on a stateless server through multi-round-trip requests: a tool returns an
input-required result with a form and an opaque `requestState`, and the client retries the call
with the answers. It is available only when the client negotiated it (`McpServer.IsMrtrSupported`).

- `nimbus_resubmit_message` and `nimbus_skip_message`, called by a delegated caller on a client
  that supports it, answer with a form: the preview's impact text, a required reason, and confirm
  or cancel. For skip the form states that the message will never be processed.
- `requestState` is protected with Data Protection and bound to the caller, tenant, client,
  `actionToken` and `idempotencyKey`, like `OperatorActionTokens`. A retry with a state that
  doesn't match is `[StaleMessage]`.
- A decline or cancel sends nothing and is audited as a refusal.
- Clients without support, and workloads, behave as in phase 1.
- The audit entry records whether the confirmation came through the form.

A form answered by the client is stronger evidence than the model's word, but it is not proof that
a person answered: the client is not trusted more than its token. Nothing in authorization depends
on it.

Phase 2 is designed in a revision of this spec once phase 1 has shipped and question 3 in §10 is
answered.

## 8. Tests and delivery

Tests (`tests/NimBus.WebApp.Tests/Mcp/`), written before the code they cover:

- `nextActions` on `nimbus_get_message` match `eligibleActions` for a Reader (none that change), a
  Contributor with and without each scope, a workload (never `skip`, `confirm` false), and each
  status.
- No label contains text from an error, a log entry or a payload, for a message whose error text
  holds an instruction.
- `nimbus_get_operation` never offers `resubmit` for `OutcomeUnknown`.
- Every `tool` named in a `nextActions` entry is registered, and every argument name exists on it.
- `nimbus_get_worklist`: grouping and order; a resolved group left out and counted; hidden and
  unreadable endpoints' rows never returned, including to a caller who is Reader on one endpoint
  only; `[EndpointNotFound]` for an unreadable `endpointId`; `olderFailuresLikely`; and an endpoint
  where changes are not allowed over MCP offering no option that changes anything.
- The new Insights method returns nothing for an empty endpoint set, and the existing methods still
  refuse a caller without site Reader.
- Completions: fixed lists, `endpointId` limited to readable endpoints, nothing for a hidden
  prompt. An empty optional prompt argument still counts as omitted.
- `OperatorInstructionsTests` still pass: under 4,096 characters, safety rules in the first 2,048,
  every named tool and prompt registered.
- Existing MCP tests pass unchanged, which shows the change is additive.

Delivery: an implementation plan in `docs/plan/` first, as the change spans more than five files.
Then one pull request, one commit per part (§3, §4, §5, §6), with a screenshot of the **Connect an
agent** card. Additive, so a minor version.

Acceptance: in each client that passed the pilot, connected to a deployment with seeded failures,
"what needs me?" leads to a numbered choice, and choosing resubmit leads to a preview, a yes and a
`Succeeded` operation, without the user naming a tool, an endpoint id or an event id.

## 9. Out of scope

- **MCP Apps** (inline UI with buttons). The richest experience and the narrowest client support.
  Reconsider after phase 2.
- **MCP resources.** Nothing here needs them.
- **New change actions.** No bulk skip, no resubmit by group, no deferred or handoff recovery.
- **Usage telemetry.** The activity view in Settings → MCP access (`/api/v1/admin/mcp/activity`)
  already reports agent calls.
- **A client-side package.** Spec 040 §4.4 stands.

## 10. Open questions

1. For Claude Desktop and for Codex: can the user supply a pre-registered Entra client id, which
   redirect URI does the client use, and does it connect from the user's machine or from the
   vendor's cloud? Needs a pilot per client.
2. Should a group be resubmittable without a ticket? It would be a new batch path next to
   resubmit-by-ticket, with its own preview and budget rules.
3. Which clients negotiate multi-round-trip requests today, and how do they render a form?
4. How often does each client send `completion/complete`, and does it use up the request limit?
5. Should `label` be localizable, or is fixed English enough given that the agent rephrases it?

## Review log (revision 1 → 2)

| # | Finding | Addressed in |
|---|---|---|
| 1 | The worklist scanned endpoints with the message search, whose rows carry no error text; grouping by cause would have cost a details read, and an audit row, per message. | §4.1: built on the Spec 043 occurrence read. |
| 2 | Occurrences are failed attempts, not open messages, so a naive group would list failures already recovered. | §4.1: live status check per group; §4.2: `checked`, `open`, `resolvedGroups`. |
| 3 | The worklist could miss failures older than the period, and unsupported and deferred messages. | §4.2 `totals` and `olderFailuresLikely`; §4.3. |
| 4 | Revision 1 said the worklist obeys the Spec 037 tool switches. Read tools have none. | §4.3 Access. |
| 5 | Adding the `nextActions` rule to rule 2 of the instructions could push a safety phrase past 2,048 characters. | §3.4: a new rule 7 after them. |
| 6 | `[AllowedValues]` completions might change how an empty prompt argument is handled, and filtering `action` per caller didn't fit a static attribute. | §5: one handler, no attribute, `action` unfiltered. |
| 7 | Completions count against the 60-per-minute request limit. | §5 Rate limit; §10 question 4. |
| 8 | The acceptance test assumed Claude Desktop and Codex can reach a local demo stack and sign in with Entra. | §6.1; §8 Acceptance; §10 question 1. |
| 9 | A batch resubmit result had no defined options. | §3.2. |
| 10 | `arguments` can carry a producer-chosen event id. | §3.1. |
| 11 | No implementation plan was called for, though the change spans more than five files. | §8 Delivery. |
