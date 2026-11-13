# Spec 044 — Agent-first NimBus

Status: proposed (2026-11-11, revision 3: organized around the failure-handling agent; all ten
decisions agreed).
Phase 1 is merged (#361) and not yet released; Phases 2 to 4 are planned in
[2026-11-12-spec-044-phases-1-4.md](../../plan/2026-11-12-spec-044-phases-1-4.md), which also lists
the amendments to this spec that need agreement. Baseline: master `b435e1bb`; the latest release was
then v4.10.1.
Builds on [Spec 033](../033-integration-intelligence-failure-classification/spec.md),
[Spec 035](../035-mcp-operator-access/spec.md), [Spec 037](../037-mcp-admin-access/spec.md),
[Spec 039](../039-agent-api-keys/spec.md), [Spec 040](../040-mcp-operator-prompts-and-plugin/spec.md)
and [Spec 043](../043-intelligent-failed-message-insights/spec.md).

This spec sets the direction for AI in NimBus and designs its first phase in detail. Phases 2 to 4
are described at the level of scope and order; each gets its own plan.

## 1. Vision

An agent handles failed messages. For every failure it reaches one of four outcomes and leaves a
record in NimBus:

| Outcome | When | What the agent does |
|---|---|---|
| **Retry** | The cause is temporary | Resubmits the message, checks the result, and stops after a fixed number of attempts. |
| **Report data quality** | The source system sent data that is wrong, incomplete or breaks a rule | Names the source system, the field and the reason, reports it to the source's owner, and links the failures to that report. |
| **Register a bug and draft a fix** | The adapter's code is wrong | Opens an issue in the adapter's repository with the evidence, drafts a pull request with a fix and a regression test, and links the failures to the issue. After the fix is deployed it resubmits them. |
| **Hand to a person** | Anything else: uncertain, credentials or configuration, the platform | Leaves its classification and rationale for the operator. |

The agent belongs to the customer and runs outside NimBus, with its own model and its own access to
the issue tracker and the repositories. NimBus gives it the evidence, records what it concluded,
and carries out retries through the Resolver. NimBus ships the workflow as an MCP prompt and a
reference runner as a sample (§9); it does not host the agent.

Issues and pull requests go to the repositories of the adapters that use the platform. The NimBus
repository is never a target: a fault in the platform itself is handed to a person.

## 2. Why this shape

NimBus has AI in two different shapes today.

**NimBus calls a model itself.** Failure classification (Spec 033,
`NimBus.Extensions.IntegrationIntelligence`) sends redacted evidence for one failed message to
TypeSafe (`jev-*`), stores a category and three likelihoods, and derives guidance with fixed rules.
TypeSafe is the only provider (`FailureClassificationOptions.Validate` rejects any other). To
support that one call, NimBus carries a provider client, a sealed API key in shared storage, a
Settings panel and paid-call idempotency with unknown-outcome handling. It can label a failure. It
cannot retry it, look at the adapter's code, or open an issue.

**An outside agent talks to NimBus.** The operator MCP endpoint (`/mcp`, Specs 035/037/040) gives an
agent 16 tools, 4 prompts and server instructions under the same roles, redaction and audit as the
Web UI. Such an agent already has what the vision needs and NimBus cannot provide: a model the
customer has approved, the customer's repositories and tracker, and the ability to write code.

So the model moves out of NimBus, and NimBus concentrates on what only it can do: evidence, a
trusted record, and safe execution on the bus.

## 3. Principles

1. **NimBus calls no model and stores no model credentials.** Reasoning happens in the customer's
   agent.
2. **Agents are callers, not a side channel.** Everything an agent reads or does goes through MCP,
   with the same endpoint roles, redaction, audit and Resolver command path as a person.
3. **What an agent concludes is stored as data, with provenance.** NimBus records who said it,
   through which client, and labels it advisory. Anything NimBus groups by or recommends is derived
   from that data by fixed rules on the server.
4. **Agent input is untrusted.** Free text from an agent is bounded, scrubbed, stored as plain text,
   and returned to other agents marked as untrusted, like error text from a failing handler.
5. **The agent proposes; people decide what ships.** It drafts pull requests in the adapters'
   repositories and reports to source owners. It never merges, it never targets the NimBus
   repository, and NimBus never holds repository or tracker credentials.

## 4. What the loop needs from NimBus

Checked against the code on the baseline.

| Step | Exists today | Gap | Phase |
|---|---|---|---|
| Find failures, grouped by pattern | `nimbus_find_messages`, `nimbus_get_metrics` (failures view) | Which failures are already classified | 1 |
| Decide the outcome | Question set: a category and three likelihoods | No question tells source data from adapter code | 1 |
| Record the conclusion | Only the built-in provider can write a classification | An agent cannot record one | 1 |
| See the data that failed | Error text. The payload only for PiiReader | A redacted payload for diagnosis | 1 |
| Know which system sent it | `nimbus_get_message` returns `From` | Not part of the evidence | 1 |
| Retry | `nimbus_prepare_action`, `nimbus_resubmit_message`; delegated callers only | Unattended resubmit, a cap per message, a readable outcome | 2 |
| Know who owns the source | `EndpointMetadata` holds owner, team, email and technical contacts | Not returned over MCP | 3 |
| Find the adapter's code | Nothing | Repository and tracker per endpoint | 3 |
| Locate the bug | `ErrorContent.ExceptionStackTrace` is stored; MCP never returns it | A scrubbed stack trace in the evidence | 3 |
| Link failures to a report | `nimbus_set_message_reported` with a ticket id, one message per call | Many messages per call; find by ticket; avoid duplicate reports | 3 |
| Resubmit after the fix | One message per call | A set of messages selected by ticket | 3 |
| See what the agent did | Audit rows and the MCP activity log | One view per agent | Later |

## 5. Decisions

Agreed with the user on 2026-11-11:

| # | Topic | Decision |
|---|---|---|
| 1 | Built-in provider | Deprecate the TypeSafe provider, its key storage and the analyze route in a 4.x minor. Remove them in v5.0.0, per the obsolete lifecycle in `docs/versioning.md`. It is not kept as an optional extension. |
| 2 | Classifications | Agents record classifications over MCP. The store, the question set, the guidance rules and the Insights groupings stay. |
| 3 | Spec 043 follow-ups | Selective analysis, representative pattern hypotheses and resumable bulk execution are withdrawn. The other §12 increments stand. |
| 4 | Bus participation over MCP | Offer it later, as a separate MCP endpoint with its own scopes, after the agent API is hardened (§9). This supersedes decision 2 of Spec 035 for participation. |
| 5 | Unattended changes to messages | Only once the operation journal of Spec 035 Phase 2b exists, and then within a budget per agent. |

Agreed on 2026-11-11, following the vision:

| # | Topic | Decision |
|---|---|---|
| 6 | Where the agent runs | Outside NimBus. NimBus ships the workflow as an MCP prompt and a reference runner under `samples/`. |
| 7 | Unattended skip | Not offered. An unattended agent may resubmit; skipping releases later messages in the session and stays with a person. |
| 8 | Fault origin | The question set gains one question (version 2) that tells source data, adapter code, target system, configuration and platform apart. |
| 9 | Data for diagnosis | A redacted payload (Phase 1) and a scrubbed stack trace (Phase 3) may be given to agents, each behind an Owner switch that is off by default. |
| 10 | Code changes | Draft pull requests only, in the repository of the adapter that failed. Never in the NimBus repository. The runner's repository token cannot merge or push to protected branches. |

## 6. Phase 1: understand

The agent can classify every failure, see enough to diagnose it, and record the conclusion. A
person's agent can already run the whole loop attended (§6.9).

### 6.1 What exists

- `FailureClassificationService.AnalyzeAsync` loads the message with a Contributor check, builds
  evidence (`FailureEvidenceBuilder.BuildAsync`), reserves an operation, calls the provider, composes
  guidance (`FailureGuidanceRules.Compose`), completes the operation and audits `FailureClassified`.
- The service, the evidence builder and the redactor are registered only when every setting is
  valid, the provider's included. Without a key the feature runs read-only (Spec 043 §9): saved
  results are readable and nothing new can be stored.
- Each failure is one JSON document holding every revision (`dbo.FailureClassifications.StateJson`,
  or a Cosmos item). Completed revisions are immutable.
- The evidence builder can already include the event payload with every value marked sensitive
  replaced (`IEventJsonRedactor`, then `IntelligenceDataRedactor`), when the Owner turned on
  `Data.IncludeEventPayload`.
- Over MCP, `nimbus_get_classification` reads the latest result. `nimbus_classify_failure` asks the
  provider, needs the delegated `nimbus.classify` scope and Contributor, and counts against the
  change limit of 5 per 60 seconds.
- The Web UI shows provider, model, time and requester on the intelligence card
  (`components/event-details/intelligence-card.tsx`). Insights groups by the saved category and
  guidance.

### 6.2 Question set, version 2

Version 2 keeps the four questions of version 1 and adds one:

| `fault_origin` | Meaning |
|---|---|
| `source_data` | The publishing system sent data that is wrong, incomplete or breaks a rule. |
| `adapter_code` | The adapter's own logic is wrong: a mapping, a null it does not handle, a case it does not support. |
| `target_system` | The receiving system or a dependency is unavailable or refuses a valid request. |
| `configuration` | Credentials, permissions, addresses or settings. |
| `platform` | NimBus, Service Bus, topology or sessions. |
| `unknown` | Not enough evidence. |

It comes with a confidence from 0 to 1. Records from version 1 have no origin and show as "not
assessed". The deprecated provider keeps asking the version 1 questions.

The prompt (§6.9) maps the answers to an outcome. NimBus does not store the outcome as a field:

| Guidance (composed by NimBus) | Fault origin | Outcome |
|---|---|---|
| `RetryMayHelp` | any | Retry |
| `ChangeLikelyRequired` | `source_data` | Report data quality |
| `ChangeLikelyRequired` | `adapter_code` | Register a bug and draft a fix |
| anything else | anything else | Hand to a person |

### 6.3 `nimbus_get_failure_evidence`

A read tool. Arguments: `endpointId`, `eventId`, `messageId` (optional; defaults to the latest
attempt, as `nimbus_classify_failure` does today).

It returns:

- the `FailureClassificationInput` that `FailureEvidenceBuilder` builds: coordinates, resolution
  status, retry count and limit, exception type, scrubbed message and source, dead-letter reason, and
  recent failures in the same endpoint and session;
- the sending endpoint (`From`, and `OriginatingFrom` when it differs);
- the redacted payload, when the Owner's payload setting is on;
- the current question set: its version and each question with its instructions and criteria;
- the `messageId` it resolved, which the record tool requires;
- a summary of the latest saved classification, if there is one.

Rules:

- It needs Endpoint Reader and readable classification storage. A failure that is not `Failed` or
  `DeadLettered` answers `[ActionNotAllowed]`, as classification does today.
- The evidence follows the saved evidence settings (history on or off, maximum items, maximum
  lengths). Error text is scrubbed exactly as it is for the provider.
- The payload here is always the redacted one, built by the existing path. `Data.IncludeEventPayload`
  stays as the Owner's switch for it and stays off by default; it now means "agents may see redacted
  payloads" and the Settings text says so. The raw payload keeps its own gate on
  `nimbus_get_message` (PiiReader, the `nimbus.payload.read` scope and the Owner's payload switch).

### 6.4 `nimbus_record_classification`

A write tool that changes no message (`ReadOnly = false`, `Idempotent = true`,
`Destructive = false`, `OpenWorld = false`).

| Argument | Rule |
|---|---|
| `endpointId`, `eventId`, `messageId` | The failed attempt, as returned by the evidence tool. `messageId` is required, so an answer is never attached to a newer attempt than the one the agent read. |
| `idempotencyKey` | A GUID. Repeating it returns the same stored result. |
| `questionSetVersion` | Must equal the current version. Otherwise `[InvalidArgument]`, telling the agent to read the evidence again. |
| `category` | One of `FailureClassificationQuestionSet.Categories`. |
| `categoryConfidence`, `retryLikelihood`, `changeRequiredLikelihood`, `externalDependencyLikelihood` | Finite numbers from 0 to 1. |
| `faultOrigin`, `faultOriginConfidence` | One of the origins of §6.2, and a number from 0 to 1. |
| `model` | Optional, at most 100 characters. What the agent says it runs on. Stored as stated and never verified. |
| `rationale` | Optional, at most 1,000 characters of plain text. For source data it names the field and the rule. |
| `supersede` | Default false. When false and a result exists, that result is returned and nothing is written. When true, a new revision is written. |

When it runs, NimBus:

1. checks the `nimbus.classify` scope, the Owner's classify switch, the client's permission to
   change, the endpoint's change scope and a fresh Contributor check, exactly as
   `nimbus_classify_failure` does now;
2. checks that the message belongs to the endpoint and is still eligible, by building the evidence
   again;
3. reserves and completes one operation in the existing store, so revisions, idempotency and the
   1.5 MB limit per failure apply unchanged. No provider call sits between the two steps, so the
   unknown-outcome state can arise only from a storage fault;
4. composes the guidance with `FailureGuidanceRules` and the saved thresholds. The agent never
   supplies the guidance;
5. audits `FailureClassified` with the caller, the client application and `provider: "Agent"`.

Stored record (`FailureClassification`):

| Field | Value for an agent result |
|---|---|
| `Provider` | `"Agent"`. This is how readers tell the two sources apart; older records keep `"TypeSafe"`. |
| `Model` | The declared model, or `"unspecified"`. |
| `RequestedBy` | The caller, as today. |
| `CategoryProbabilities` | One entry: the chosen category with its confidence. |
| `EventPayloadIncluded` | Whether the evidence NimBus last built for this attempt carried the redacted payload. |
| `InputTokens`, `OutputTokens` | Null. |
| `FaultOrigin`, `FaultOriginConfidence` (new, optional) | The answers to the new question. |
| `ClientId` (new, optional) | The client application (`azp`, else `appid`). |
| `Rationale` (new, optional) | The scrubbed rationale. |

The new fields are optional members of the JSON document, so neither store needs a migration and
older records read unchanged.

Limits: a recording counts against the MCP request limit (60 per 60 seconds per caller) and not
against the change limit, because it changes no message and costs no provider call.

### 6.5 Finding unclassified failures

`nimbus_find_messages` gains an optional `includeClassification` flag. When set, each failed row
carries whether a classification is saved and, if so, its category, guidance, fault origin and
provider. The tool reads them in one call through `IFailureClassificationBulkReader`, joined on the
row's latest message id. When classification storage is unreadable the flag reports that instead of
guessing.

`nimbus_get_metrics` keeps its error-pattern output. Returning cause and guidance groups there
still depends on the hidden-endpoint rules of Spec 043 §12.

### 6.6 Unattended recording

Recording is advisory and changes no message, so it does not wait for the operation journal:

- a new app role, `Nimbus.Classify`, on the MCP resource registration;
- a new Owner switch next to **Unattended workloads**: whether workloads may record
  classifications. Default off;
- the workload still needs Contributor on the endpoint in the NimBus access-control lists.

### 6.7 Activation

Recording needs storage and valid evidence and threshold settings. It does not need a provider.

| Mode | When | Available |
|---|---|---|
| Off | Disabled, unbindable configuration, or unreadable saved settings | Nothing |
| Read-only | Storage configured, but evidence or threshold settings are invalid | Saved results |
| Recordable | Storage configured and evidence and threshold settings valid | Saved results, evidence, recording |
| Ready | Recordable, and the provider settings are valid | Everything, including provider analysis, until v5.0.0 |

Changes:

- `FailureClassificationOptions.Validate` reports provider errors separately from evidence and
  threshold errors.
- `Provider` accepts `"None"`. It is the documented value for new deployments and becomes the
  default in v5.0.0. With `"None"`, a missing key is not a configuration warning.
- `FailureEvidenceBuilder` and `IntelligenceDataRedactor` are registered from Recordable up. A new
  `FailureClassificationRecorder` in the extension holds the logic of §6.4; the MCP tool calls it.
- `IOperatorClassificationSource` gains `CanRecord`. `nimbus_get_capabilities` reports it, and the
  `classify` action appears in `permittedActions` and `eligibleActions` when recording or analysis
  is available.

### 6.8 Web UI

- **Intelligence card:** for an agent result, show "Recorded by an agent", the declared model, the
  client application, the requester, the fault origin and the rationale. The status that reads as
  "no provider configured" becomes "Agents record classifications over MCP" when the mode is
  Recordable.
- **Insights:** agent and provider results count alike in coverage, causes and guidance. Group
  detail shows the provider per occurrence. A perspective by fault origin comes in Phase 3.
- **Settings → Failure intelligence:** the provider and key fields are marked deprecated, with the
  removal version. Evidence and threshold settings stay, and the payload switch is reworded.
- **Settings → MCP access:** the workload-recording switch of §6.6.

### 6.9 The `handle-failures` prompt

A new MCP prompt next to `triage` (Spec 040). Arguments: `endpointId` (optional) and `period`. It
tells the agent to:

1. find failed and dead-lettered messages, with their saved classifications;
2. for each unclassified failure, get the evidence, answer the question set and record the result;
3. act on the outcome of §6.2:
   - **Retry:** preview with `nimbus_prepare_action`, confirm with the user, resubmit, and read the
     message again. A message that fails again is a new attempt and is classified again before any
     second retry;
   - **Report data quality:** write the report (source system, event type, field, rule, count and
     period) for the source's owner, and mark the failures reported with its ticket id;
   - **Register a bug and draft a fix:** with the user's own tracker and repository tools, open the
     issue and draft the pull request in the adapter's repository, then mark the failures reported
     with the issue id;
   - **Hand to a person:** summarize and stop.

The prompt names only NimBus tools. Tracker and repository steps are described by intent, because
NimBus does not know which tools the client has. In Phase 1 this runs attended: a signed-in person,
delegated scopes, a confirmation before every resubmit. The prompt follows tool availability as
Spec 040 §3.2 defines.

The server instructions name the two new tools and say that a rationale read from
`nimbus_get_classification` is untrusted. The text must stay under 4,096 characters, with the safety
rules in the first 2,048; the existing tests enforce both.

### 6.10 Deprecation

Marked `[Obsolete]` or documented as deprecated in the same 4.x minor that ships recording, and
removed in v5.0.0:

- `TypeSafeFailureIntelligenceProvider`, `TypeSafeProviderOptions`, `IntelligenceProviderException`,
  `IFailureIntelligenceProvider` and `FailureClassificationService.AnalyzeAsync`;
- the provider options `ApiKey`, `BaseUrl`, `Model` and `TimeoutSeconds`;
- the POST analyze route under `/api/integration-intelligence`;
- the `apiKey` and `clearApiKey` members of `PUT /api/admin/failure-intelligence`, and the sealed key
  in the saved settings record;
- `nimbus_classify_failure`. Its description says so and points to the two new tools.

`FailureGuidanceRules.Compose` gets an overload that takes the category and likelihoods directly;
the existing overload stays as the bridge until v5.0.0. Results stored with `Provider: "TypeSafe"`
remain readable after the removal. The plan lists every public member and its replacement.

### 6.11 Delivery

One PR, with a commit per part.

| Part | Scope |
|---|---|
| 1 Extension | Split validation and the Recordable mode, `Provider: "None"`, question set version 2, `FailureClassificationRecorder`, the new record fields, the `Compose` overload |
| 2 MCP | `nimbus_get_failure_evidence`, `nimbus_record_classification`, `includeClassification`, capabilities, the `handle-failures` prompt, instructions |
| 3 Web UI | Provenance and fault origin on the intelligence card and in Insights group detail, Settings wording. Screenshot in the PR |
| 4 Workloads | The `Nimbus.Classify` app role and the Owner switch |
| 5 Deprecation | `[Obsolete]` markers, tool and route notices, `docs/integration-intelligence.md`, `docs/mcp-server.md`, `docs/features.md`, release note |

### 6.12 Acceptance

- With no provider configured, an agent with `nimbus.classify` and Contributor records a
  classification, and it appears on the intelligence card and in all three Insights perspectives.
- The guidance stored for a recording equals `FailureGuidanceRules.Compose` for the same answers and
  thresholds.
- An unknown category or origin, an out-of-range number, an old `questionSetVersion`, a message on
  another endpoint and a message that is no longer failed are each refused, and nothing is stored.
- Repeating an `idempotencyKey` returns the stored result. `supersede=false` on a classified failure
  writes nothing. `supersede=true` writes a new revision and keeps the old one in history.
- A caller without Contributor, a client denied changes, a hidden endpoint and a switched-off
  classify capability are refused and audited as today.
- A workload token records only when it carries `Nimbus.Classify` and the Owner switch is on.
- The evidence carries a payload only when the Owner's payload switch is on, and then every value
  marked sensitive in the event's metadata is replaced.
- A rationale that contains a value marked sensitive in the event's metadata is stored with that
  value replaced.
- Records with the new fields round-trip on SQL Server and Cosmos DB, and version 1 records read
  unchanged.
- Existing provider analysis still works in Ready mode, and its tests pass unchanged.
- On CrmErpDemo with error mode on, a person runs `handle-failures` from Claude Code: the transient
  failure is retried after confirmation, and the others are classified with the expected origin.

## 7. Phase 2: retry unattended

Spec 035 Phase 2b already designs the foundation; this phase adds the workload path on top.

1. **Operation journal** on all three stores with conformance tests, `nimbus_get_operation`, and
   receiver-side command receipts.
2. **Unattended resubmit.** A new app role, `Nimbus.Resubmit`, and an Owner switch that is off by
   default. The two-step preview, the state guard and the audit reason stay.
3. **Budget per agent.** A number of resubmits per agent and window, set by the Owner, at or below
   the deployment's change limit.
4. **Cap per message.** NimBus refuses an unattended resubmit of a message that agents have already
   resubmitted a set number of times (default 2) without success, and answers `[ActionNotAllowed]`.
   The count comes from the journal, so it holds across agents and restarts.

There is no app role for skip. `nimbus_resubmit_with_changes` (Spec 035 Phase 3) stays with a
person: an agent does not repair a payload.

## 8. Phase 3: report and fix

### 8.1 Ownership and code location

- `nimbus_get_endpoint` returns the owner, team and technical contacts that `EndpointMetadata`
  already holds, as the Web UI shows them.
- `EndpointMetadata` gains the address of the adapter's repository, an issue-tracker address and a
  source-system name, edited by an Owner in the endpoint's settings and returned by the same tool.
  This is a storage change on every provider, with conformance tests. An endpoint without a
  repository address has no bug route: its adapter faults are handed to a person.

### 8.2 Stack traces

`nimbus_get_failure_evidence` takes `includeStackTrace`. The trace comes from
`ErrorContent.ExceptionStackTrace`, is scrubbed like error text and is cut at 8,000 characters. A new
Owner switch governs it and is off by default. No other tool returns stack traces.

### 8.3 Linking failures to a report

- `nimbus_set_message_reported` accepts up to 50 event ids on one endpoint in one call, under one
  reason. Each message is still audited.
- `nimbus_find_messages` filters by `reported` and by `ticketId`. Before it files anything, the
  agent looks for reported failures with the same error pattern and reuses their ticket.
- For an unattended agent, marking reported needs a new app role, `Nimbus.Annotate`, and its Owner
  switch.

### 8.4 Resubmit after the fix

`nimbus_prepare_action` accepts a set of failed messages on one endpoint selected by `ticketId`. The
preview lists the count and the sessions affected. The run goes through the journal, counts against
the agent's budget, and reports a result per message.

### 8.5 Insights

A fourth perspective groups failure occurrences by fault origin. For `source_data` it breaks down
further by sending endpoint and event type, which is the data-quality view for a source's owner.

## 9. Phase 4: run unattended

- **Reference runner** under `samples/`: a scheduled, headless coding agent that connects to `/mcp`
  with a workload identity (`Nimbus.Observe`, `Nimbus.Classify`, `Nimbus.Resubmit`,
  `Nimbus.Annotate`), runs `handle-failures`, and uses tracker and repository credentials of its own.
  It checks out the adapter's repository from the address on the endpoint and works there.
- **Guide** `docs/failure-agent.md`: the Entra app roles, the Owner switches, the budget, and a
  token for the adapters' repositories that can open issues and draft pull requests and cannot merge
  or push to protected branches.
- **Exit, in two parts.** On CrmErpDemo with the failure reasons of error mode switched on in turn,
  the runner retries the transient failure, reports the missing reference data as data quality, and
  hands the expired credentials to a person. Error mode throws on purpose and CrmErpDemo lives in
  the NimBus repository, so it cannot prove the bug route. That is proven on an adapter in a
  repository of its own, built from the adapter kit, with a seeded defect: the runner opens an issue
  and a draft pull request there with a fix and a regression test.

## 10. Later, not on this path

- **The rest of Spec 035 Phase 3:** `nimbus_settle_handoff`, `nimbus_reprocess_deferred` and
  `nimbus_skip_deferred_tracking`, for a person's agent.
- **Agents view:** one page in the "AI & agents" settings group that shows, per agent, what it may
  do, its budget, and its recordings, retries and reports, linked to the audit trail. It reads the
  MCP activity log (`GET /api/admin/mcp/activity`) and the journal.
- **Bus participation over MCP** (decision 4). The agent API is demo-grade today and is hardened
  first:

  | Gap | Where |
  |---|---|
  | Subscriptions live in process memory and are lost on restart or on another instance | `AgentSubscriptionRegistry` |
  | Receive does not claim. Two agents, or one agent polling twice, can get the same event | `AgentImplementation.GetAgentReceiveAsync` |
  | One Agent Zone endpoint for every agent | `AgentZone` (`Agent:ZoneEndpointId`) |
  | A key admits all of `/api/agent/*`, for every event type | Spec 039, non-goals |
  | Signed-in people pass no role check on the agent path and name themselves with `X-Agent-Id` | Spec 039, non-goals |

  Then a second MCP endpoint, separate from `/mcp`, with its own scopes and Owner policy, exposes
  catalog, define event type, receive, publish and settle. The REST API and `NimBus.Agents` stay. It
  needs its own spec. Spec 023 (the AI Integration Mapper, open as #66) fits there: an agent proposes
  a mapping, an operator approves it, and NimBus executes it deterministically.

## 11. Risks

| Risk | Mitigation |
|---|---|
| Error text or a payload steers an agent that writes code | Draft pull requests only. The runner's token cannot merge or push to protected branches. Review and CI decide what ships. Evidence is marked untrusted. |
| Message data ends up in an issue or a pull request, which more people can read than NimBus | Evidence is redacted before it leaves NimBus, and the payload and stack-trace switches are off by default. The prompt forbids quoting payload values and asks for synthetic values in tests. Redaction cannot find business data that is not marked sensitive; the Settings text says so. |
| An agent retries in a loop | The budget per agent, the cap per message, the state guard, and a new classification before a second retry. |
| An agent mislabels failures and Insights misleads | Contributor is required. Provenance is shown wherever the result is. The Owner can switch classify off, per client or entirely. Every recording is audited, and earlier revisions are kept. |
| A wrong diagnosis blames the source system | The report goes to the source's owner as a draft with its evidence. The classification shows who recorded it and how confident it was. |
| The same bug is reported many times | The ticket lookup of §8.3. |
| A rationale carries injected text to the next agent, or leaks payload values | It is bounded, scrubbed like evidence text, stored as plain text, and marked untrusted in tool results. It is never placed in prompts or server instructions. |
| An agent fills a failure's document with revisions | The 1.5 MB limit already answers 503 without discarding history. `supersede` defaults to false. |

## 12. Out of scope

- Hosting, scheduling or configuring agents in NimBus.
- NimBus calling GitHub, Azure DevOps or any tracker, or holding credentials for them.
- An agent that merges pull requests, skips messages unattended or edits payloads.
- Issues or pull requests against the NimBus repository. Platform faults go to a person.
- A replacement provider, or a provider abstraction for customers to plug a model into NimBus.
- A REST route or Web UI form for recording a classification by hand. Spec 035 already leaves manual
  overrides out.
- Waking the agent when a failure occurs. The runner polls on a schedule; notifications can carry a
  trigger later.
- Changes to Core, the SDK, the Resolver or the transport, except the opt-in stack trace of §15 D3.
  None of them gains a dependency on the intelligence extension or on MCP.

## 13. Compatibility

- Phase 1 is additive in 4.x. A deployment with a TypeSafe key keeps working until v5.0.0.
- A deployment that enabled classification without a key moves from read-only to Recordable on
  upgrade. Nothing is recorded until a caller holds `nimbus.classify` and Contributor, so the
  upgrade grants nothing by itself.
- A deployment that had `Data.IncludeEventPayload` on keeps it on, and redacted payloads then reach
  agents that can read evidence. The release note says so.
- Customers who want an unattended agent add app roles to their MCP resource registration, one per
  phase. Nothing else changes in Entra.
- v5.0.0 removes the provider. Stored classifications, the question set, the guidance rules and
  Insights are unaffected.

## 14. Open questions

1. Should the record tool require proof that the evidence was read, for example a short-lived token
   from the evidence tool? Proposed: no. It adds state and does not stop a careless agent.
2. Should a data-quality finding carry the field as structured data (a JSON path) and not only in
   the rationale? Proposed: add it in Phase 3 if the Insights breakdown needs it.
3. Should NimBus store the outcome (retried, reported, bug filed, handed over) as a field of its
   own? Proposed: no. The resubmit audit, the reported marker with its ticket and the classification
   already say it; the Agents view assembles them.
4. A ticket id allows letters, digits, `.`, `_` and `-`, so it cannot hold an address. Should the
   Web UI build a link from the endpoint's tracker address and the ticket id? Proposed: yes, in
   Phase 3.

## 15. Amendments (2026-11-12)

Made while planning and implementing Phases 2 to 4
([plan](../../plan/2026-11-12-spec-044-phases-1-4.md)); they replace the text above where they differ.

| # | Amends | Decision |
|---|---|---|
| D1 | §7.1 | Receiver-side receipts need no Core, Resolver or transport change. The Manager publishes each journaled command with the operation id as its message id (`IManagerCommandClient`). The Resolver's stored copy is the receipt, and the response whose `ParentMessageId` names it is the outcome. A subscriber's inbox already skips a redelivered resubmission. NimBus never republishes a command. |
| D2 | §7.3 | An agent is a workload client id within its tenant. Its budget counts journaled resubmits over a rolling 24 hours (default 20, Owner range 0–500), applies even with rate limiting off, and never raises the per-minute change limit. |
| D3 | §8.2, §12 | Core stores no stack trace today. A subscriber opts in with `IncludeStackTraceInErrorResponses` (frames only, 8,000 characters); agents read it only with the Owner's `Data.IncludeStackTrace` switch. |
| D4 | §8.3 | Reports live apart from event rows and cannot be joined on Cosmos. `nimbus_find_reports` (backed by `FindEventReports`) replaces `reported`/`ticketId` filters on `nimbus_find_messages`. |
| D5 | §8.1, §14 Q4 | The endpoint's tracker address is a link template containing `{ticketId}`; it overrides the site's ticket link. The metadata editor is mounted as the endpoint's **Ownership** tab. |
| D6 | §9 | The data-quality exit case uses the `contract_schema` failure reason; `missing_reference_data` stays a hand-over or retry-later case. |
| D7 | §6.9 | `handle-failures` takes `unattended` (default false). It changes only the text; the server still enforces roles, switches, budget and cap. |
| D8 | §9 | The reference runner is a .NET console that gets the token, checks capabilities and fetches the prompt over MCP, then runs a headless coding-agent CLI (Claude Code by default). |

