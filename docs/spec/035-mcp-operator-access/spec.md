# Spec 035 — MCP operator access

Status: Phase 1 (Observe) is complete. It shipped in v4.0.0 (2026-09-28), and its exit criterion,
a read-only pilot in a nonproduction Azure environment, passed on 2026-10-06; see the
[Phase 1 plan](../../plan/2026-09-25-mcp-operator-phase1-plan.md). Phase 2a (Operate) is
complete. It shipped in v4.5.0 (2026-10-06), its stale-state and UI-vs-agent race tests pass, and
its delegated pilot in a nonproduction Azure environment passed on 2026-10-06; see the
[Phase 2a plan](../../plan/2026-10-06-mcp-operator-phase2a-plan.md).
Phase 2b (the operation journal and receiver-side receipts) was delivered with
[Spec 044](../044-agent-first/spec.md) Phase 2 in context-and-oss/NimBus#371 and shipped in v4.12.0.
Phase 3 (Complete) is implemented (2026-11-15) per the
[Phase 3 plan](../../plan/2026-11-15-mcp-operator-phase3-plan.md); see
[Phase 3 as built](#phase-3-as-built). Its delegated pilot in a nonproduction Azure environment has
not run. Design: rev 2, 2026-09-25. Site Owners manage MCP access
from the WebApp (turn it off, switch tools off, restrict people, clients and endpoints) per
[Spec 037](../037-mcp-admin-access/spec.md).

The interactive design is in [design.html](design.html). It covers the architecture, the 21-tool
catalog, a recovery walkthrough, the security model, delivery phases and the code it is based on.
This file records the decisions.

## Summary

Add a remote MCP server for operators. It lets AI agents observe endpoint health, investigate
failed messages, classify failures and carry out single-message recovery through NimBus's existing
authorization, redaction, audit and Resolver command path.

## Decisions

1. **Host in Azure, inside the WebApp.** Serve an opt-in `/mcp` Streamable HTTP endpoint from the
   WebApp site that `deploy/bicep/templates/webApp.bicep` provisions, using
   `ModelContextProtocol.AspNetCore`. A separate Azure host stays possible later.
2. **Replace `NimBus.Mcp`.** The Spec 022 stdio server is retired in Phase 1. It only ever worked
   against local-dev authentication, and the hosted endpoint covers that under Aspire, so the Azure
   pilot is a Phase 1 exit criterion rather than a condition for removal. Bus participation (define event type, subscribe, receive, publish, agent settle) is not
   carried into MCP. Participant agents use the `NimBus.Agents` REST SDK. See "Replacing NimBus.Mcp"
   in the design for the tool-by-tool mapping.
3. **Entra in Azure, on its own bearer scheme.** Use a separately named JWT bearer scheme with
   its own App ID URI and `nimbus.*` scopes, bound to `/mcp` only. Do not use the WebApp's `"Az"`
   policy scheme or Identity cookies.
4. **No sign-in under Aspire.** When the existing local-dev bypass is active (Development and
   `EnableLocalDevAuthentication=true`; startup throws if the flag is set anywhere else), `/mcp`
   accepts tokenless calls as the "Local Developer" principal. Scope checks are skipped; role, PII
   and message-state checks still run. Keep it on loopback and enforce `Origin` validation against
   DNS rebinding. With the bypass off, MCP requires Entra.
5. **Operator tools only.** An explicit registry; no admin, topology, access-control, purge or
   generic API bridge.
6. **Mutations in two steps.** Phase 2a: interactive callers only, with a current-state guard
   (message still failed, same latest attempt) and audit before the side effect, with no new store.
   Phase 2b: operation journal, `nimbus_get_operation` and receiver-side command receipts, required
   before unattended workloads may mutate.
7. **Never enable topic duplicate detection.** The throttle and defer path re-sends messages with
   their original `MessageId` (`src/NimBus.ServiceBus/MessageContext.cs`). Deduplication must happen
   on the receiving side.

## Delivery

| Phase | Scope | Exit |
| --- | --- | --- |
| 1 Observe | Adapter, bearer scheme, local Aspire mode, read tools, classification reads | Read-only pilot in a nonproduction Azure environment; `NimBus.Mcp` removed |
| 2a Operate | Shared coordinator, preview, fresh ACL checks, state guard; classify, report, resubmit, skip | Delegated pilot; stale-state and UI-vs-agent race tests |
| 2b Operate | Operation journal (Cosmos, SQL, in-memory + conformance), receiver-side receipts | Crash/retry tests; bounded workload mutations |
| 3 Complete | Handoff settlement, deferred recovery, payload edits | Parity with existing UI semantics. Implemented; see [Phase 3 as built](#phase-3-as-built) |

## Phase 3 as built

Phase 3 adds the four tools the design lists as Extended. Each is prepared with
`nimbus_prepare_action` and run with its token, like resubmit and skip. The user guide is
[docs/mcp-server.md](../../mcp-server.md#handoffs-deferred-messages-and-edited-payloads).

| # | Decision |
| --- | --- |
| U1 | Four tools: `nimbus_settle_handoff`, `nimbus_reprocess_deferred`, `nimbus_skip_deferred_tracking`, `nimbus_resubmit_with_changes`. `nimbus_resubmit_message` and `nimbus_skip_message` still accept only Failed, DeadLettered and Unsupported messages. |
| U2 | Three delegated scopes: `nimbus.settle`, `nimbus.recover` (both deferred tools) and `nimbus.edit`. No app role: the tools never run for a workload (decision 6, Spec 044 §7 and §12). |
| U3 | Three Owner switches in the Spec 037 policy (`SettleHandoffs`, `RecoverDeferred`, `EditPayloads`), off by default, also in a policy saved before they existed. This departs from the existing capabilities, which default to on, so that an upgrade changes nothing. |
| D1 | `nimbus_prepare_action` gains the actions `settle_handoff`, `reprocess_deferred`, `skip_deferred_tracking` and `resubmit_with_changes`. The token, its lifetime and the change limit are unchanged. No by-ticket or batch form. |
| D2 | Only `resubmit_with_changes` is a journaled operation: it has resubmit's shape (an archive claim and a response parented to the command). The other three return their result directly; the reconciler's assumptions do not hold for them. |
| D3 | The edited payload is passed to the preview and again to the tool, and the token binds its SHA-256. The preview lists the changed JSON paths, never values. The payload must be one JSON object, the event type cannot change, and the masked-placeholder check runs for every caller: a PiiReader without `nimbus.payload.read` reads masked values over MCP. The audit row stores the paths and the hash, not the payload. |
| D4 | Settling needs Pending with sub-status Handoff at the previewed version and a fresh Contributor check. The order over MCP is claim (the in-flight tracker), required audit, send; a failed send releases the claim and is audited as `CommandNotSent`. The Web UI and the agent API keep their order (send, then best-effort audit). |
| D5 | Reprocess is refused when the session has no Deferred row, while a Failed row still blocks it (reprocess resets the block, so later events would overtake the failure), and above `NimBus:Mcp:MaxDeferredReprocess` (default 200). It is audited before the reset as the new `ReprocessDeferred` audit type. |
| D6 | The preview of a tracking skip runs the broker inspection and returns each check; it is refused unless the skip is allowed. The tool inspects again, makes the conditional Deferred-to-Skipped write, audits after it and returns `auditRecorded`. This is the REST behaviour, moved into `DeferredTrackingRecovery`, which both channels call. |

Where the implementation differs from the plan:

- The MCP commands are a new `IOperatorRecoveryCommands`, not an extension of
  `IHandoffSettlementService`, and the tools are a partial of `OperatorActionTools`, so they reuse
  its token, version and limiter checks.
- The REST resubmit-with-changes path is unchanged. Its checks depend on the caller's PiiReader
  role and the body's event type, which the MCP rules (D3) do not share, so only the masker and the
  type resolution are reused.
- A journaled resubmit-with-changes is now published under its operation id; it was not before.

Not done: the delegated Azure pilot, which needs the three scopes on the deployment's Entra app
registration and an Owner to switch the capabilities on.

## Retiring `NimBus.Mcp`

`NimBus.Mcp` has `IsPackable=false`, so it ships no NuGet package or public API, and removing it is
not a versioning break. It still needs a release-notes entry. Done in Phase 1, PR 4:

- removed `src/NimBus.Mcp`, `tests/NimBus.Mcp.Tests` and their `src/NimBus.sln` entries;
- rewrote `docs/mcp-server.md` for the hosted server, including client configuration by URL and the
  Entra app registration;
- updated `site/features.html`, Spec 034 §5.8 and the MCP question in Spec 032 (and its plan).

## Out of scope

- Authorization for the REST agent API (`/api/agent/*`), which currently checks no NimBus role and
  takes its audit actor from the `X-Agent-Id` header. It is tracked separately and outlives this
  server.
- An acknowledge/clear tool (shared Monitor acknowledgements exist server-side since #142; the
  read tools surface them), manual classification overrides, compose-new-event, bulk recovery.
