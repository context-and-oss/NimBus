# Spec 035 Phase 3 (Complete): MCP tools for handoff settlement, deferred recovery and payload edits

## Context

Spec 035 (MCP operator access) has four phases. Phases 1, 2a and 2b are on master (2b's operation
journal landed with Spec 044 in #371). Phase 3, "Complete", is not built: an agent on `/mcp` can
resubmit or skip a Failed, DeadLettered or Unsupported message, but for a deferred message or a
pending handoff the server sends the operator to the Web UI (`OperatorInstructions.cs` rule 6,
`OperatorNextActions.cs:131`, `docs/mcp-server.md:167`).

The spec gives Phase 3 one row: "Handoff settlement, deferred recovery, payload edits", exit
"Parity with existing UI semantics". `design.html:88-91` names four tools and their access tags;
scope names, switches, result shapes and the reprocess bound were left to this plan.

Outcome: a person's agent can do the same single-message recovery the Web UI offers, through the
same services, guards and audit. Spec 035 is then complete.

## Decisions

Agreed with the repo owner (2026-11-15):

| # | Decision |
|---|---|
| U1 | Four new tools: `nimbus_settle_handoff`, `nimbus_reprocess_deferred`, `nimbus_skip_deferred_tracking`, `nimbus_resubmit_with_changes`. `nimbus_resubmit_message` and `nimbus_skip_message` keep their current states (`McpActionable` unchanged). |
| U2 | Three new delegated Entra scopes: `nimbus.settle`, `nimbus.recover` (both deferred tools), `nimbus.edit`. |
| U3 | Three new Owner capability switches (`SettleHandoffs`, `RecoverDeferred`, `EditPayloads`), default **off**, also for saved and unsaved policies. |

Already fixed by the specs (not reopened): interactive callers only, never workloads
(design:115, Spec 044 §7/§10/§12); payload edits need PiiReader and are marked destructive
(design:88, 96); preview is mandatory (design:102); `skip_deferred_tracking` returns
`auditRecorded` and is never presented as broker deletion (design:91); one PR, one commit per part.

Filled in by this plan (each is recorded in the spec's new "Phase 3 as built" section):

| # | Fills a gap | Decision |
|---|---|---|
| D1 | Prepare | `nimbus_prepare_action` gains actions `settle_handoff`, `reprocess_deferred`, `skip_deferred_tracking`, `resubmit_with_changes`. Same token (2 min, bound to caller, client, action, message version), same rate limiter. No by-ticket or batch form. |
| D2 | Journal | Only `resubmit_with_changes` is journaled (`operationId`, `nimbus_get_operation`): it has resubmit's claim/response shape. The other three return a direct result as in Phase 2a; the reconciler's assumptions (archive claim, response parented to the command) do not hold for them. |
| D3 | Payload edit | The edited JSON is passed to prepare and again to the tool; the token binds its SHA-256. Preview lists changed JSON paths (added/removed/changed), no values. Content must be a JSON object. The event type cannot be changed (design: "retain existing event-type"). The masked-placeholder check runs for **every** caller: a PiiReader without `nimbus.payload.read` reads masked values over MCP and must not write them back. |
| D4 | Settle | Requires Pending + Handoff, the previewed message version, fresh Contributor, a note/reason (1–1000). MCP order is claim (`HandoffSettlementTracker`), **required** audit, send; a failed send abandons the claim and writes `CommandNotSent`. The Web UI and agent-API order (send, then best-effort audit) is unchanged. |
| D5 | Reprocess | Preview reports the session's deferred count. Refused when the session has no Deferred row, when it still has a Failed row (reprocess clears the block, `SessionState.ClearForReprocess`, so later events would overtake the failure; the agent is told to resubmit or skip the blocker), or when the deferred count exceeds `McpOperatorOptions.MaxDeferredReprocess` (default 200). Audited before the reset with a new `MessageAuditType.ReprocessDeferred`. |
| D6 | Skip tracking | Prepare runs the broker inspection and returns `canSkip`, `skipDetail`, `brokerChecks`; the token binds the row version. The tool re-inspects, does the conditional Deferred→Skipped write, audits after it, and returns `auditRecorded` — the existing REST semantics, moved into a shared service. |

## What exists and is reused

- Pipeline for write tools: `Mcp/Tools/OperatorActionTools.cs` (`PrepareActionAsync`, `ExecuteAsync`), `Mcp/Operations/OperatorActionAccess.cs` (`RequireScopeAsync`, `RequireChangeAllowedOnAsync`, `MayAsync`, `ScopeOf`, `HoldsScope` — workloads already fall through to false), `OperatorActionTokens.cs`, `OperatorMutationLimiter.cs`, `OperatorToolErrors.cs`.
- `Services/Operations/OperatorCommandCoordinator.cs`: `RunAsync` (guard, fresh role, archive claim, required audit, publish, journal steps); `ResubmitWithChangesAsync` exists but ignores `context.Journal`.
- `IManagerCommandClient.Resubmit(..., eventJson, commandMessageId)` (`src/NimBus.Manager/ManagerClient.cs`) already carries an edited payload under a command id.
- `Services/HandoffSettlementService.cs` + `HandoffSettlementTracker`, `HandoffSettlementMapper.ToSettlement`, `IHandoffClientFactory`.
- `Services/DeferredMessageInspector.cs` (`RowVersion`, `InspectAsync`), `IMessageTrackingStore.TrySkipDeferredMessage` (all three providers, conformance-tested), `IAdminService.ReprocessDeferredAsync`.
- `IEventJsonMasker.ContainsRedactPlaceholder` / `StripMaskedMarker`.
- Tests: `tests/NimBus.WebApp.Tests/Mcp/McpTestHost.cs` (real MCP pipeline on TestServer, `StubAccess`, `RecordingManagerClient`, `RecordingAuditLog`), `HandoffOperatorImplementationTests`, `DeferredRecoveryApiTests`, `EventImplementationResubmitPiiGateTests`, `OperatorCommandCoordinatorTests`.

## Parts (one PR `feat/mcp-operator-phase3`, one commit per part, tests first in each)

**Part 0 — plan.** Commit this plan as `docs/plan/2026-11-15-mcp-operator-phase3-plan.md`.

**Part 1 — access.**
- `OperatorAction` gains `SettleHandoff`, `ReprocessDeferred`, `SkipDeferredTracking`, `ResubmitWithChanges`; `ScopeOf`, the refusal text and `EligibleActionsAsync` in `OperatorActionAccess.cs`.
- `McpOperatorPermissions` (`Mcp/McpOperatorAuthorization.cs`): three scope constants, `AllScopes`.
- `McpCapabilitySettings` (`src/NimBus.MessageStore.Abstractions/States/McpAccessSettings.cs`): three bools, default `false`. `McpAccessPolicy.AllowsAction`, `McpAccessRules.Changes`, `McpToolAccess.ChangeTools` and the prepare-tool check (any of the six change capabilities), `McpAccessApiMapper`, `api-spec.yaml` `McpCapabilities`.
- Tests: `McpAccessPolicyEnforcementTests` (off by default, hidden from the tool list, refused with `[PermissionDenied]`, on after an Owner enables), settings round trip in the access-settings conformance tests, a workload is refused all four.

**Part 2 — shared services.**
- Coordinator: `ResubmitWithChangesAsync` publishes through `IManagerCommandClient` with `journal.CommandMessageId` when the context has a journal (same as `ResubmitAsync`). New `OperatorPayloadEdit` service holding what the controller does today (server-side type resolution, placeholder gate, marker strip) plus D3's hash, JSON-object check and path diff; `EventImplementation.OperatorActions.cs` calls it (behaviour unchanged for REST).
- Handoff: `IHandoffSettlementService` gains a guarded method returning an outcome enum (expected message version, fresh role, audit-first per D4); `SettleAsync` keeps its `IActionResult` contract.
- Deferred: extract `EventImplementation.Deferred.cs`'s inspect/skip orchestration into `Services/DeferredTrackingRecovery.cs`; the controller maps its outcome to the same HTTP results. Register `DeferredMessageInspector` creation there.
- Audit: append `ReprocessDeferred` to `MessageAuditType` (end of the enum; Cosmos stores the number), `api-spec.yaml` audit enum, ClientApp audit label, `McpActivityService` types and verb (also add the three existing types it does not list).
- Tests: coordinator journal test for resubmit-with-changes; handoff guarded-method tests (stale version, audit unavailable sends nothing, send failure abandons); existing REST suites stay green unchanged.

**Part 3 — MCP tools.**
- `PrepareActionAsync`: `ParseCommand` and one branch per new action (D1, D3, D5, D6); new result fields are additive (`canSkip`, `skipDetail`, `brokerChecks`, `changedPaths`, `pendingSubStatus`).
- New `Mcp/Tools/OperatorRecoveryTools.cs` (registered with `.WithTools<>` in `McpOperatorServiceCollectionExtensions.cs`): the four tools. Annotations: `settle_handoff` and `reprocess_deferred` `Destructive=false, OpenWorld=true`; `skip_deferred_tracking` and `resubmit_with_changes` `Destructive=true`. All take `actionToken`, `idempotencyKey`, `reason`; settle adds `outcome` (`complete`|`fail`) and optional `errorType`; resubmit-with-changes adds `eventContent`.
- `OperatorOperationAction` gains `ResubmitWithChanges` (name must equal `OperatorActionAccess.NameOf`); `ExecuteAsync`'s two binary ternaries become switches.
- `OperatorNextActions`: labels, `ForMessage` (Deferred → prepare reprocess / skip tracking; Pending+Handoff → prepare settle; Web UI link kept when the caller may not), `ForPrepared`. `OperatorDiscoveryTools` docs. `McpOperatorOptions.MaxDeferredReprocess`.
- `OperatorInstructions.Text` rule 6 and `OperatorPrompts` (recover-message, investigate-message): reworded, still under 4,096 characters with the safety rules in the first 2,048.
- Tests in `tests/NimBus.WebApp.Tests/Mcp/`: new `OperatorRecoveryToolsTests.cs` (happy path, stale version, wrong state, missing scope, missing PiiReader, masked payload rejected for a PiiReader, changed payload after prepare, session with a Failed blocker, over the bound, `auditRecorded=false`); update `McpOperatorEndpointTests` tool lists and annotations, `NextActionsTests`, `OperationJournalToolsTests`.

**Part 4 — Settings UI.** `ClientApp/src/components/admin/mcp-access-model.ts` (three `CAPABILITIES` rows with tools, scope, role and risk; tool counts; replace the positional `rows[4]`/`rows[5]` with lookups by key) and `mcp-access-settings.tsx`; Vitest updates; screenshot of Settings → MCP access for the PR (mock API, demo data, on a `pr-assets/` branch).

**Part 5 — docs.** `docs/mcp-server.md` (tool list, recovery walkthroughs, the three scopes and the Entra app-registration step, capability table, remove "recovered in the Web UI", fix the stale app-role sentence at 429); Spec 035 header and a "Phase 3 as built" section with D1–D6; dated notes in Specs 040 (§ instructions), 044 (§10) and 046 (§5, out of scope) that Phase 3 delivered these tools; `docs/features.md` if it lists MCP tools.

## Not in this plan

- Widening resubmit/skip to Deferred or pending-handoff rows (U1).
- Workload access, app roles, bulk or by-ticket forms of the new tools.
- Web UI gaps found while exploring, left as they are and reported as follow-up issues: reprocess-deferred writes no audit row; a resubmit-with-changes role denial surfaces as 500; its audit row stores the raw edited body.
- The delegated Azure pilot. It needs the three scopes added to the deployment's Entra app registration and an Owner to enable the switches; it stays a manual step for the repo owner after merge.

## Verification

1. Each part: failing tests first, then green.
2. `dotnet build src/NimBus.slnx -c Release` (NSwag generation on), then `dotnet test src/NimBus.slnx -c Release --no-build`; report skipped live-provider conformance tests explicitly.
3. `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`; `npm --prefix website run build` (docs changed).
4. End to end on the local Aspire CrmErpDemo stack (emulator, local-dev MCP auth) with an MCP client: enable the three switches, then settle a parked handoff (complete and fail), reprocess a session's deferred messages, skip one stale deferred tracking row, and resubmit a failed message with an edited payload; confirm each audit row, the journal entry for the edit, and that a switched-off capability hides its tool.
5. `/code-review` on the branch before opening the PR; PR with the Settings screenshot; auto-fix on.

Work happens in a new worktree off `origin/master` (`.claude/worktrees/spec-035-phase3`); the shared checkout is not touched.
