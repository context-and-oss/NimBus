# Plan: Spec 046 — Guided actions for the operator MCP server, phase 1

Spec: [docs/spec/046-mcp-guided-actions/spec.md](../spec/046-mcp-guided-actions/spec.md)
(revision 2, not yet approved). Status: P1 to P4 implemented on this branch (#380); P0, the client pilot, is not done. Baseline: master `5556ea82`; the latest release is v4.13.0. Branch
for this plan: `docs/spec-046-mcp-guided-actions` (#380).

This plan covers phase 1 of the spec (§3 to §6). Phase 2, the confirmation form (§7), gets its own
plan after a revision of the spec. Every code claim below was checked against `5556ea82`.

| Part | Goal | Depends on | Delivery |
|---|---|---|---|
| P0 Client pilot | Find out which clients can connect, and how | Nothing; a person does it | Notes in the PR; answers §10 questions 1, 3 and 4 |
| P1 `nextActions` | Options in the results of five tools | — | Commit 1 |
| P2 Worklist | `nimbus_get_worklist` and the `triage` prompt | P1 | Commit 2 |
| P3 Completions | Prompt argument completions | — | Commit 3 |
| P4 Docs and Connect card | Tool and prompt docs, connect steps, examples, errors | P0 for the per-client steps; P1 to P3 for the rest | Commit 4 |

## Delivery rules

- One PR from a branch off master, one commit per part, in the order above. No stacked PRs. The
  spec must be approved, and this plan's decisions agreed, before the branch is cut.
- Additive in 4.x: new result members, one new tool, one new protocol capability. No existing
  argument, result member, scope, role or setting changes. The next 4.x minor carries it.
- No storage interface changes, so no provider, conformance or decorator work.
- Tests first: each step below names its failing test, then the change.
- Verification before the PR:
  - `dotnet build src/NimBus.slnx -c Release` with the newest 10.0.4xx SDK pinned through a
    temporary `global.json`, then `dotnet test src/NimBus.slnx -c Release --no-build`, run only if
    the build succeeded. SPA and NSwag generation stay enabled.
  - `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build` (P4 changes the card).
  - `npm --prefix website run build` (`docs/mcp-server.md` is on the portal).
  - Live SQL and Cosmos conformance tests are not affected; the PR reports them as run by CI.
  - A screenshot of the **Connect an agent** card in the PR.

## Decisions this plan needs

Each either fills in something the spec leaves open or corrects it. When one is agreed, the spec is
amended in the implementation PR.

| # | Topic | Proposal | Why |
|---|---|---|---|
| D1 | Where a group's `ticketId` comes from (§4.2) **(fills a gap)** | One batched `IMessageTrackingStore.GetEventReports(endpoint, openEventIds)` call per endpoint that has a returned group (amended from `FindEventReports` during implementation: an exact lookup can't miss a report past a page limit). | `UnresolvedEvent`, which `GetEventsByIds` returns, has no reported flag or ticket. Reports live in their own table (Spec 044 D4). `nimbus_find_reports` already reads them this way. |
| D2 | The example's `messageVersion` (§4.1) **(fills a gap)** | `IOperatorCommands.FindCurrentAsync` on the example, not `OperatorMessageVersion.From` on the status row. | `FindCurrentAsync` also checks that commands for the message go to this endpoint. The option must rest on the same lookup `nimbus_prepare_action` uses. It costs two reads per returned group, at most 40 per call. |
| D3 | Occurrence rows without a session id (§4.1) **(fills a gap)** | Leave them out of the status check and count them in a new group member, `unchecked`. | `GetEventsByIds` needs the stored id `{EventId}_{SessionId}`. All three providers project the session id today (`MetricsStoreConformanceTests`), so this is a guard, not a case that occurs. |
| D4 | Shape of the new Insights method (§4.1) **(fills a gap)** | `IFailedInsightsQuery.GetOccurrencesForEndpointsAsync(Period, IReadOnlyCollection<string> endpointIds, CancellationToken)`. It returns the normalized rows and their read time, checks no role, and returns nothing for an empty set. Its doc comment says the caller must pass only endpoints the user may read. | The existing methods authorize site Reader in `LoadScopeAsync`. The worklist authorizes per endpoint through `OperatorEndpointCatalog`. The name keeps the difference visible at the call site. |
| D5 | Null or empty `nextActions` (§3.1) **(fills a gap)** | Always an array, empty when there is nothing to offer. | The tool serializer never omits nulls, and the derived output schemas mark every member required (`McpOperatorServiceCollectionExtensions.ToolSerializerOptions`). An array keeps clients that validate the schema working. |
| D6 | Connect card before the pilot (§6.1) **(fills a gap)** | The card and the docs gain a client only once P0 connected it end to end. If P0 is not done when P1 to P3 are ready, P4 ships without new clients and the pilot result follows in a docs PR. | The spec says the section ships only for clients that were connected. Code should not wait on a manual step. |
| D7 | Worklist tests (§8) **(fills a gap)** | Register the real `FailedInsightsService` over `InMemoryMessageStore` in the worklist tests, through `McpTestHost`'s `configureServices`. | The host registers a fake `IFailedInsightsQuery` (`McpTestHost.cs`), which would make the per-caller filter untested. |

## P0 — Client pilot (manual)

A person with Claude Desktop, Codex and an Entra tenant does this. It needs a deployed WebApp with
`/mcp` enabled; the local Aspire stack only when the client connects from the user's machine.

For each of Claude Desktop and Codex, record:

1. Can the user supply a pre-registered client id? Which redirect URI does the client use?
2. Does it connect from the user's machine or from the vendor's cloud? (Check the caller address
   in the WebApp's request log.)
3. Does it reach a local stack (`https://localhost:18443/mcp`)?
4. How does it show prompts and their arguments, and does it send `completion/complete`? How often
   while typing? (Count requests in the log against the 60-per-minute limit.)
5. Does it negotiate protocol revision 2026-07-28 and multi-round-trip requests? (Needed for
   phase 2 only.)
6. How does it render a numbered choice from `nextActions`: its own question UI, or plain text?
   This needs a build with P1.

Claude Code is the control: it is documented and known to work.

## P1 — `nextActions`

**Files:** new `Mcp/Operations/OperatorNextActions.cs`; `Mcp/Tools/OperatorMessageTools.cs`,
`OperatorActionTools.cs`; `Mcp/OperatorInstructions.cs`; `Mcp/Prompts/OperatorPrompts.cs`; new
`tests/NimBus.WebApp.Tests/Mcp/NextActionsTests.cs`.

1. **Record and builder.** `NextAction(Id, Label, Kind, Tool, Arguments, Needs, Confirm, Url)` and
   a static `OperatorNextActions` with one method per result kind. Labels are constants. The
   builder takes ids and enums only; it has no parameter through which error text could arrive.
   - Test first: every `Tool` a builder can return is a registered tool, and every key in
     `Arguments` and `Needs` is a parameter of it (reflect over the `[McpServerTool]` methods, as
     `OperatorPromptsTests.RegisteredToolNames` does).
2. **`nimbus_get_message`.** Add `NextActions` to `MessageDetailResult`, built from the
   `eligibleActions` list already computed there, the saved classification's guidance when
   classification is enabled, and the status (deferred and pending handoff get `open_web_ui` with
   the existing `WebUiUrl`).
   - Tests first: a Reader gets only `investigate`; a Contributor with each scope gets the matching
     option; a workload never gets `skip` and gets `confirm: false`; `RetryMayHelp` puts `resubmit`
     first; a message whose error text contains an instruction yields labels equal to the constants.
3. **`nimbus_prepare_action`.** Add `NextActions` to `PrepareActionResult`: one `run` naming the
   execute tool, with `actionToken` in `arguments` and `reason`, `idempotencyKey` in `needs`.
4. **`nimbus_resubmit_message`, `nimbus_skip_message`.** Add `NextActions` to
   `OperatorActionResult`: `check` when there is an `operationId`; empty for a batch and for
   `nimbus_set_message_reported`, which share the record.
5. **`nimbus_get_operation`.** Add `NextActions` to `OperationResult.From`, by status as in §3.2.
   - Test first: `OutcomeUnknown` offers `open_web_ui` and never `resubmit`.
6. **Server instructions.** Add rule 7 (§3.4). `OperatorInstructionsTests` must stay green: length
   at most 4,096, the four safety phrases before 2,048.
7. **Prompts.** Add the same sentence to `CommonRules`. In `recover-message` step 2, tell the agent
   to offer the `nextActions` of `nimbus_get_message`.

Existing tests must pass without edits, apart from assertions on exact instruction or prompt text.

## P2 — `nimbus_get_worklist`

**Files:** `Services/Insights/FailedInsightsService.cs`; new `Mcp/Operations/OperatorWorklist.cs`
and `Mcp/Tools/OperatorWorklistTools.cs`; `Mcp/McpOperatorServiceCollectionExtensions.cs`;
`Mcp/Prompts/OperatorPrompts.cs`; `Mcp/OperatorInstructions.cs`;
`tests/NimBus.WebApp.Tests/FailedInsightsServiceTests.cs`; new
`tests/NimBus.WebApp.Tests/Mcp/WorklistToolsTests.cs`.

1. **Insights method (D4).** Reuse `ResolveWindowAsync` and `ReadOccurrencesAsync`, then filter the
   cached rows to the endpoint set, case-insensitively. Nothing caller-specific enters the cache.
   - Tests first, in `FailedInsightsServiceTests`: rows of other endpoints are not returned; an
     empty set returns nothing and reads nothing; the existing overview still answers `Forbidden`
     without site Reader.
2. **`OperatorWorklist`** (scoped service). Input: readable endpoint ids, period, limit.
   1. Occurrences through the new method; group by endpoint, event type and pattern key.
   2. Order groups by occurrence count; take them one at a time until `limit` open groups are
      found or 20 groups were checked.
   3. Per group: the 50 newest distinct events with a session id; one `GetEventsByIds` call with
      stored ids; open means Failed or DeadLettered. `EndpointNotFoundException` skips the group.
   4. The example is the newest open event; `FindCurrentAsync` gives its version and row (D2);
      `OperatorActionAccess.EligibleActionsAsync` gives its options.
   5. One `ReadLatestInBulkAsync` for all examples' message ids, as `nimbus_find_messages` does,
      when classification is available.
   6. `FindEventReports` per endpoint for `ticketId` (D1); acknowledgements and totals from
      `OperatorQueries`, as `nimbus_get_overview` reads them.
3. **Tool.** `OperatorWorklistTools.GetWorklistAsync(endpointId?, period?, limit?)`, annotated like
   the other read tools, registered with `WithTools<OperatorWorklistTools>(ToolSerializerOptions)`.
   It is not in `McpToolAccess.ChangeTools`, so the policy filter lets it through like every read
   tool. Error and pattern text goes through `OperatorProjection.Truncate`.
   - Tests first, with the real Insights service (D7), seeding through `InMemoryMessageStore`:
     two causes on one endpoint give two groups, largest first; a failure that was resubmitted and
     completed is left out and counted in `resolvedGroups`; a caller who is Reader on one endpoint
     sees no group, count or pattern from another; a hidden endpoint likewise; an unreadable
     `endpointId` answers `[EndpointNotFound]`; an older open failure sets `olderFailuresLikely`;
     an endpoint where changes are not allowed over MCP has only `read` options; a group reported
     under one ticket offers `resubmit_ticket`.
4. **`triage` prompt.** Replace steps 3 to 6 with one call to `nimbus_get_worklist`. Keep the
   read-only rule. `OperatorPromptsTests` checks every tool a prompt names is registered.
5. **Server instructions.** Name the tool in the opening sentence's tool list if the length allows;
   otherwise only in the prompt.

## P3 — Completions

**Files:** new `Mcp/Prompts/OperatorPromptCompletions.cs`;
`Mcp/McpOperatorServiceCollectionExtensions.cs`; `Mcp/Access/McpPromptAccess.cs`;
`Mcp/Prompts/OperatorPrompts.cs` (make `Periods` and `Actions` internal);
`tests/NimBus.WebApp.Tests/Mcp/OperatorPromptsTests.cs`.

1. **Handler.** Registered with `WithCompleteHandler`. For a prompt reference it switches on the
   argument name: `period`, `action` and `unattended` from the constants; `endpointId` from
   `OperatorEndpointCatalog.GetReadableEndpointIdsAsync`, filtered by prefix, at most 100, with
   `total` and `hasMore` set. Anything else, including a resource reference, returns an empty list.
2. **Filter.** `McpPromptAccess.FilterComplete`, added with `AddCompleteFilter`: an empty result
   for a prompt the caller can't list. No refusal is audited; nothing was refused that the caller
   could see.
3. **Tests first:** each fixed list; `endpointId` returns only readable, unhidden endpoints and
   honours the prefix; `recover-message` completes nothing while changes are switched off; an
   unknown prompt or argument returns an empty list, not an error; the server advertises the
   completions capability; an empty optional prompt argument still counts as omitted (existing
   test, must stay green).

The endpoint catalog reads the platform model and the caller's roles, not the message store, so a
completion costs no storage query.

## P4 — Docs and Connect card

**Files:** `docs/mcp-server.md`; `ClientApp/src/components/admin/mcp-access-settings.tsx` and its
test; the spec (status, amendments from the decisions above).

1. **Tools and prompts.** Add `nimbus_get_worklist` to the Tools table, a short **Options in
   results** section on `nextActions`, the completions note under Prompts, and the new `triage`
   description.
2. **Connect a client.** The "ask your assistant" prompt (§6.2), then steps per client that passed
   P0 (D6), and a line per combination that doesn't work.
3. **Example conversations** (§6.3), taken from real runs against CrmErpDemo with error mode on.
4. **Troubleshooting.** One row per tool error code (§6.4).
5. **Connect card.** A client selector beside the existing registration selector, showing the
   command or settings for the chosen client. Only clients that passed P0 appear; with Claude Code
   alone the card is unchanged and no screenshot is needed.
   - Test first in `mcp-access-settings.test.tsx`: choosing a client changes the text shown and
     copied.

## Acceptance

Run against CrmErpDemo with error mode on, in Claude Code and in each client that passed P0, and
record the transcript in the PR:

1. "What needs me?" calls `nimbus_get_worklist` and ends in a numbered choice.
2. Choosing resubmit leads to a preview, a yes, and an operation that reaches `Succeeded`.
3. The user names no tool, endpoint id or event id.
4. As a Reader, no option that changes anything is offered.

## Risks

- **Agents ignore `nextActions`.** The options are a hint in the result and one sentence in the
  instructions. If the acceptance run shows an agent still improvising, strengthen the prompt text
  before adding anything to the protocol.
- **Result size.** Twenty groups with six options each is a few kilobytes. The default limit of 10
  keeps it small; measure it in the acceptance run.
- **Cost on a large site.** The occurrence read is the one the Insights page makes, cached for 30
  seconds for all callers. The per-call extra is at most 20 status reads, 40 reads for examples,
  one classification read and one report read per endpoint.
- **Completions and the request limit.** Measured in P0, step 4. If a client uses up the limit, the
  follow-up is a separate limit for `completion/complete`, which is not in this plan.

## Not in this plan

Phase 2 (the confirmation form), MCP Apps, resubmitting a group without a ticket (§10 question 2)
and localized labels (§10 question 5).
