# Audit loop design: Fable finds, Opus fixes

**Date:** 2026-11-10
**Status:** Draft for review
**Skill:** [`.agents/skills/audit/SKILL.md`](../../.agents/skills/audit/SKILL.md)
**Depends on:** AgentFactory (board, dispatcher, reviewer, watcher), already running for other workspaces.

## 1. Goal

Run a standing loop that keeps NimBus improving without a person writing every ticket:

1. **Find.** Claude Fable 5.2 audits one slice of NimBus through one lens: security, performance, bugs, simplification or tests.
2. **Verify.** A second, independent pass tries to disprove each candidate. Only findings that survive are kept.
3. **Hand off.** Each surviving finding becomes an AgentFactory task through MCP `create_task`.
4. **Fix.** The AgentFactory dispatcher runs Claude Opus 5.5 (medium effort) on the task: regression test first, then the fix, then Release build and tests.
5. **Review and deliver.** The AgentFactory reviewer (Fable + GPT) reviews against the original finding, a person approves, and the watcher opens the PR.
6. **Learn.** Merged and cancelled audit tasks feed the next run's dedup, so declined findings are not raised again.

## 2. Non-goals

- No new orchestrator in NimBus. AgentFactory already owns claiming, retries, worktrees, cost metrics, review and PR delivery. Rebuilding them here with `/loop`, Workflow scripts or the Agent tool would repeat work AgentFactory has already debugged.
- No automatic queueing. `create_task` files into **Backlog**, and a person queues each task. That is the main noise filter and it stays.
- No whole-repo sweeps. Every run covers one area and one lens.
- No change to AgentFactory in phase 1. Phase 2 (section 10) is a separate AgentFactory spec.

## 3. Roles and models

| Role | Runs in | Model | Effort | Permissions |
|---|---|---|---|---|
| Auditor (find + verify) | `claude -p` from a timer, NimBus checkout on `master` | `claude-fable-5-2` | high | Read-only, plus `dotnet build`/`dotnet test`, `git log`/`git show`, and AgentFactory `list_tasks` and `create_task` |
| Spec writer (description stage, large findings only) | AgentFactory dispatcher | `claude-opus-5-5` | high | Existing worker profile |
| Implementer | AgentFactory dispatcher | `claude-opus-5-5` | medium | Existing worker profile, `acceptEdits`, own worktree |
| Reviewers | AgentFactory reviewer | `claude-fable-5-2` + `gpt-6-astra` | high / medium | Existing reviewer profile |
| Gate | Person | | | Queues tasks, approves implementation, merges PR |

The implementer, spec-writer and reviewer models are set as `nimbus` workspace overrides in the board's **Agents › Models** tab. The supervisor config files carry the same values as fallbacks.

Having the same model family find and review a fix risks anchoring. The GPT reviewer is the independent second opinion, and the implementation gate stays human.

## 4. Rotation

Each run audits one cell of an **area × lens** grid. The runner picks the cell from the date, so nothing has to be stored between runs: `cell = (day-of-year × runs-per-day + run-index) mod 40`.

| Area slug | Projects |
|---|---|
| `core` | NimBus.Core, NimBus.Abstractions, NimBus.SDK, NimBus |
| `servicebus` | NimBus.ServiceBus, NimBus.Management.ServiceBus, NimBus.ServiceBusEmulator |
| `resolver` | NimBus.Resolver, NimBus.Resolver.Host |
| `storage` | NimBus.MessageStore.*, NimBus.Outbox.SqlServer, NimBus.Inbox.SqlServer |
| `webapp-api` | NimBus.WebApp (C#, `api-spec.yaml`) |
| `webapp-ui` | NimBus.WebApp/ClientApp |
| `cli` | NimBus.CommandLine, NimBus.Manager |
| `extensions` | NimBus.Extensions.*, NimBus.Agents, NimBus.OpenTelemetry |

Lenses: `security`, `performance`, `bugs`, `simplification`, `tests`.

Eight areas and five lenses give 40 cells: one full cycle in 40 days at one run per day, or 20 days at two. Each run covers the matching `tests/*.Tests` projects too, because the tests lens needs them and the other lenses use them as evidence.

## 5. The finding contract

A candidate becomes a finding only if it has every field below. The skill drops candidates that are missing any of them.

| Field | Content |
|---|---|
| `lens`, `area` | From the rotation cell |
| `title` | One claim, at most 80 characters, stated as the defect ("Resolver drops the audit entry when …") |
| `location` | `path:line` and the symbol |
| `evidence` | The code path quoted or cited, with the call chain that reaches it |
| `failure` | Concrete input or state, then the wrong result. For performance, the cost and the hot path. For tests, the mutation that survives today. |
| `proof` | The regression test that fails on `master` today, named and sketched. For performance, the measurement that shows the cost. |
| `fix` | The intended direction, not the code |
| `scope` | Files expected to change, and whether it touches public API, a storage interface or `api-spec.yaml` |
| `severity` | `critical`, `high`, `medium` or `low` |
| `confidence` | `confirmed` (the verify pass reproduced or traced it) or `plausible`. Only `confirmed` findings are filed. |

### What each lens must show

- **Security.** A path from input an attacker controls (an HTTP request to the WebApp, a message body or application property, CLI arguments, configuration from an untrusted source) to a sink, inside NimBus's threat model. No defence-in-depth nits below `high`.
- **Performance.** The code is on a hot path (per message, per session, per WebApp request, per store query). The finding names the cost (allocations, round trips, N+1 queries, Cosmos RUs, lock hold time) and how to measure it. Micro-optimisations off the hot path are dropped.
- **Bugs.** A concrete input or state, the wrong output, and a test that fails today.
- **Simplification.** Behaviour stays the same and the change deletes more than it adds. Existing tests cover the behaviour, or the task adds a characterization test first. Public API is never deleted: it is marked `[Obsolete]` with a bridge (`docs/versioning.md`).
- **Tests.** Names a behaviour or branch with no test, or a test that cannot fail, plus a realistic mutation that survives today. Raising coverage numbers alone does not count.

### Excluded from every lens

These follow from `AGENTS.md › What not to do`:

- Transport abstraction.
- NServiceBus feature parity.
- A WebApp rewrite.
- Event sourcing.
- Editing generated files (`ApiContract.g.cs`, `ClientApp/src/api-client/index.ts`).
- Anything analyzers already report.

## 6. Hand-off to AgentFactory

### Title and fingerprint

The title is `[audit:<lens>:<area>] <claim>`. The spec's first line carries a fingerprint:

```
Fingerprint: audit/<lens>/<area>/<path>#<symbol>
```

### Spec body

```markdown
Fingerprint: audit/bugs/resolver/src/NimBus.Resolver/…/ResolverService.cs#HandleAsync

**Lens:** bugs · **Severity:** high · **Found:** 2026-11-10 on master@<sha> by claude-fable-5-2

## Defect
<title restated in one or two sentences>

## Evidence
<path:line, call chain, quoted lines>

## Failure scenario
<input/state → wrong result>

## Proof
<regression test name and sketch; for performance, the measurement>

## Fix direction
<intended direction; files in scope>

## Constraints
Follow AGENTS.md. Never delete public API. A new storage interface member needs all providers, the conformance suite and the instrumenting decorator.
```

### Acceptance criteria

Required when the task enters at `implementation`:

1. A regression test named in **Proof** is added first and fails on the base commit. For performance, the measurement is recorded before and after.
2. **If the test cannot be made to fail on the base commit, stop.** Submit "not reproduced" with what was tried. Do not change product code.
3. The fix makes the test pass without weakening any existing test.
4. `dotnet build src/NimBus.slnx -c Release` and `dotnet test src/NimBus.slnx -c Release --no-build` pass, plus `npm run test:ci` and `npm run build` for `webapp-ui`. Skipped integration tests are reported per test project, with the count and the reason they were skipped.
5. The change stays inside the files listed in **Fix direction**, or explains why it could not.

### Stage selection

| Finding | `create_task` stage | Why |
|---|---|---|
| Expected scope up to 4 files, no public API, storage interface or `api-spec.yaml` change | `implementation` | The finding is already the spec |
| 5+ files, public API change, storage interface member, `api-spec.yaml`, or any `simplification` that moves types | `description` | AGENTS.md requires a plan first; the spec stage writes one and it is AI-reviewed before code |

## 7. Dedup and the ledger

Before filing, the auditor calls `list_tasks` for the `nimbus` workspace and reads each `[audit:` task's fingerprint.

- **Open task with the same fingerprint** (backlog, queued, in progress, in review, delivering, blocked): skip.
- **Cancelled task with the same fingerprint:** a person declined it, so skip. Cancelling is the rejection ledger and no separate file is needed. Exception: the code at that location changed after the cancellation date (`git log --since`). Then the finding may be filed again, with a note that links the old task.
- **Done task with the same fingerprint:** the fix was merged. File again only if the regression test from that task no longer exists or no longer covers the case. That would be a regression.
- **Same file and symbol under a different lens:** file it, and link the other task in the spec.

## 8. Limits

- **WIP cap.** If at least 5 `[audit:` tasks are open in the workspace, the run exits after the rotation check and files nothing. A person reviewing PRs is the bottleneck, and the cap keeps the backlog reviewable.
- **Per-run cap.** At most 3 tasks per run. When more survive, file the highest severity first.
- **Zero is a valid result.** A run that files nothing because nothing survived verification has succeeded.
- **Budget.** The runner passes `--max-turns`, and a systemd `RuntimeMaxSec=45min` caps wall-clock time.

## 9. Phase 1: running it

Phase 1 is manual. Run the audit by hand until its findings are consistently worth merging, then schedule it.

### In this repository

- `.agents/skills/audit/SKILL.md` is the auditor procedure. Claude invokes it as `/audit <area> <lens>`, Codex as `$audit`.
- `.claude/skills` is a symlink to `../.agents/skills`, so Claude Code discovers the shared skills. That also makes `/review-change` resolve in Claude Code.
- `tools/audit.sh <area> <lens> [--dry-run]` runs one audit headless:
  - Fable 5.2 at high effort, with read-only tools and the three AgentFactory tools it needs.
  - It writes the JSON result to the log directory and prints the report and its cost.
  - Run it on a clean checkout of `master`.

### On the machine (outside the repository)

The board runs on the developer's Linux machine. The AgentFactory checkout is at `~/Work/github/AgentFactory`, and the database is `agentfactory.db` in that checkout. Machine configuration lives in `~/.config/agentfactory/`:

| File | Purpose |
|---|---|
| `env` | `AGENTFACTORY_DB`, plus a `PATH` that includes the mise .NET SDK, Node, `claude`, `codex` and `gh` |
| `start.sh` | Starts the board (`AUTH_MODE=token`, port 8787) and the dispatcher, reviewer and watcher. `start.sh board` starts the board only. Ctrl-C stops everything. |
| `dispatcher.config.json` | Pinned to `nimbus`, `maxConcurrent: 1`. `claudeArgs` pre-allows the Bash commands a headless worker needs: `dotnet build/test/restore/format`, `npm`, and read-only `git`. |
| `reviewer.config.json` | Pinned to `nimbus`. Reviewers are `claude-fable-5-2` (high) and `gpt-6-astra` (medium), with consensus on. |
| `watcher.config.json` | `createPullRequests: true`, `mergePullRequests: false`. A person merges every NimBus PR. |
| `mcp-nimbus.json` | AgentFactory MCP server pinned to `nimbus`, used by `tools/audit.sh` |
| `logs/` | Supervisor session logs and audit results |

On the board, the `nimbus` workspace is set up as follows:

- `repoPath`: `~/Work/github/NimBus`, with base branch `master`.
- `verifyCommand`: the Release build and tests, plus the ClientApp `test:ci` and `build`.
- Model overrides in `agent_models`:
  - Spec stage: `claude-opus-5-5`, effort high.
  - Implementation: `claude-opus-5-5`, effort medium.
  - Reviewers: as listed in the reviewer config above.

The board uses token auth because its server listens on every interface. The supervisors use the database directly and need no token. The watcher gets `GITHUB_TOKEN` from `gh auth token` at start-up.

Not set up yet:

- **A timer.** Add one once manual runs are consistently worth merging. It should run `tools/audit.sh` from a dedicated clean checkout and pick the cell as in section 4.
- **Live conformance tests.** `NIMBUS_SQL_TEST_CONNECTION` and the Cosmos DB variables are not in `env`, so workers report those tests as skipped (acceptance criterion 4). CI still runs them on the PR.

## 10. Phase 2: an AgentFactory scout supervisor (deferred)

Once the finding format has held steady for a few weeks, move the runner into AgentFactory as a `scout` supervisor, shaped like `intake`:

- A heartbeat on the Live view's health strip, instead of a timer nobody watches.
- Model and effort from the Models tab, as a `scout` role.
- Per-workspace rotation config and WIP cap on the board.
- An `externalRef` (fingerprint) column on tasks with a unique index per workspace. That replaces the title-and-spec convention in section 7.
- Audit metrics per lens: filed, queued, merged, cancelled, not reproduced, and cost per merged fix.

This needs its own AgentFactory spec. It is listed here only so phase 1 makes no choice that blocks it: the fingerprint format and the title prefix both carry over.

## 11. How we know it works

Review after four weeks, using the AgentFactory task metrics:

| Measure | Target | Action if missed |
|---|---|---|
| Merged ÷ filed, per lens | ≥ 60% | Below 40%: tighten that lens's bar, or drop the lens |
| "Not reproduced" ÷ queued | ≤ 15% | Raise the verify bar; require the verify pass to trace the full path |
| Cancelled as duplicate | ≈ 0 | Fix the fingerprint rule |
| Cost per merged fix | Tracked, no target yet | Compare lenses; drop the most expensive per merge |

## 12. Risks

- **Plausible but wrong findings.** LLM audits produce confident false positives. The defences are the verify pass, the "must name a failing test" rule, the not-reproduced exit, and the human queue gate.
- **Churn from the simplification lens.** It touches the most files and has the highest rejection risk. The rules that guard it: stage `description` for anything that moves types, and never delete public API.
- **Anchoring.** Fable both finds the defect and reviews the fix. The GPT reviewer and the human gate stay in the loop.
- **Security findings in public.** The watcher opens PRs on a public repository. A `critical` or `high` security finding must not go through the open PR flow. The auditor files it with stage `description` and the title prefix `[audit:security-private:…]`. A person handles it through a GitHub security advisory and a private fork, not the dispatcher.

## 13. Open questions

1. Resolved 2026-11-10: the board runs on the Linux development machine (section 9).
2. Does `list_tasks` return archived tasks? If not, archiving a cancelled audit task would remove it from the ledger.
3. Should the intake supervisor assess audit tasks before they are queued? It might add a useful second filter, or just add cost.
