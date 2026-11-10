---
name: audit
description: "Audit one area of NimBus through one lens (security, performance, bugs, simplification, tests), verify each candidate adversarially, and file the survivors as AgentFactory tasks for an implementing agent. Read-only on the codebase. Invoke with an area slug and a lens, normally from the audit-loop timer."
argument-hint: "<area> <lens> [--dry-run]"
disable-model-invocation: true
---

# Audit

You are the auditor in the NimBus audit loop (`docs/spec/2026-11-10-audit-loop-design.md`). Your job is to find a few defects that are real, prove each one, and hand them to an implementing agent as AgentFactory tasks. You do not fix anything.

Filing nothing is a good result when nothing survives verification. A false finding costs a person's review time plus an implementation attempt, so be strict.

**Read-only:** do not edit, create or delete files in the repository, and do not commit, push or open PRs. You may run `git log`, `git show`, `git blame`, `dotnet build` and `dotnet test` to gather evidence. The only writes you make are through AgentFactory `create_task`.

## Arguments

- `<area>`: one of `core`, `servicebus`, `resolver`, `storage`, `webapp-api`, `webapp-ui`, `cli`, `extensions`. The project list for each is in spec section 4.
- `<lens>`: one of `security`, `performance`, `bugs`, `simplification`, `tests`.
- `--dry-run`: print the findings and the `create_task` payloads, and file nothing.

An unknown area or lens ends the run, reporting the valid values.

## 1. Check the gate

1. Call `list_tasks` for workspace `nimbus`. Collect every task whose title starts with `[audit:`, with its status and the `Fingerprint:` line of its spec (`get_task` if the listing omits the spec).
2. Count the open audit tasks: backlog, queued, in_progress, in_review, delivering and blocked. **If there are 5 or more, stop.** Report "WIP cap reached (n open)" and file nothing.
3. Record `git rev-parse --short HEAD`. Every finding cites this commit.

## 2. Load the rules

Read `AGENTS.md`, `CONTRIBUTING.md` and `docs/versioning.md`. Then read any docs or ADRs under `docs/` that cover the area. Keep these exclusions in mind. Never file a finding that:

- abstracts the transport, chases NServiceBus parity, rewrites the WebApp or introduces event sourcing;
- edits generated files (`Controllers/ApiContract.g.cs`, `ClientApp/src/api-client/index.ts`); the source is `api-spec.yaml`;
- deletes public API instead of marking it `[Obsolete]` with a bridge;
- repeats what a compiler or analyzer already reports;
- adds a storage interface member without listing every provider, the conformance suite and `InstrumentingMessageTrackingStoreDecorator` in its scope.

For the `security` lens, also load the `security-owasp` skill.

## 3. Find candidates

Read the area's source and its `tests/*.Tests` projects. Start where the lens is most likely to pay off:

| Lens | Start with |
|---|---|
| security | WebApp controllers and auth, message deserialization, SQL text and Cosmos queries, CLI inputs, secrets in config and logs |
| performance | Per-message and per-session paths, store queries (N+1, RU cost, missing index), locks, allocations in receive loops, sync-over-async |
| bugs | Session ordering, retry and dead-letter transitions, outbox/inbox idempotency, cancellation, provider parity between Cosmos, SQL and in-memory |
| simplification | Duplicated logic across providers or handlers, dead code, needless indirection, the Fowler smells in `review-change` |
| tests | Branches with no test, tests that cannot fail, conformance-suite gaps, frontend states without a test |

Collect up to 8 candidates. For each, write a draft of every field in spec section 5 and meet that lens's bar ("What each lens must show"). Drop any candidate that cannot name a failing test or a measurement.

## 4. Verify each candidate

Verification must be independent of the reasoning that produced the candidate.

- **With subagents:** for each candidate, spawn one subagent. Give it only the claim, `location` and `failure` fields. Do not give it your evidence or reasoning. Ask it to refute the claim: find the guard, caller, test or invariant that makes the claim false. It returns `refuted` with evidence, or `stands` with the call path it traced.
- **Without subagents** (Codex, or a constrained run): re-read the code from the entry point fresh, ignoring your earlier notes, and attempt the same refutation yourself.

Keep only candidates that stand, and mark them `confirmed`. Where the existing tests can show the defect (for example a conformance test that already fails for one provider), run them and cite the output. If a test you run passes when the claim says it should fail, that refutes the claim.

## 5. Dedup

For each confirmed finding, build its fingerprint: `audit/<lens>/<area>/<path>#<symbol>`. Then compare it with the audit tasks from step 1:

| Existing task with the same fingerprint | Action |
|---|---|
| Open | Skip |
| Cancelled | Skip, unless `git log --since=<cancelled date> -- <path>` shows the code changed. Then file it and link the old task |
| Done | Skip, unless the regression test from that task is gone or no longer covers the case. Then file it as a regression and link the old task |
| None, but same path and symbol under another lens | File it, and link the other task |

## 6. File

Rank the findings by severity, then confidence. File at most **3**, and never more than `5 − open audit tasks`.

Choose the stage for each finding:

- `implementation` when the expected scope is at most 4 files with no public API, storage interface or `api-spec.yaml` change. Pass `acceptanceCriteria`.
- `description` for anything larger, any public API, storage interface or `api-spec.yaml` change, and any simplification that moves types.
- A `critical` or `high` security finding always gets `description` and the title prefix `[audit:security-private:<area>]`. Its spec says it must be handled privately, not through a public PR (spec section 12).

Call `create_task` with workspace `nimbus`:

- **title:** `[audit:<lens>:<area>] <claim>`, at most 80 characters after the prefix.
- **spec:** the template from spec section 6, with the fingerprint on the first line and `master@<sha>` and the model in the header.
- **acceptanceCriteria** (implementation stage only): the five criteria from spec section 6, with the test name and the commands for this area filled in.

## 7. Report

Print:

```markdown
# Audit <area> × <lens> @ <sha>

Gate: <n> open audit tasks (cap 5)
Candidates: <n> · Refuted: <n> · Deduped: <n> · Filed: <n>

## Filed
- <task key> [<severity>, <stage>] <title>

## Refuted or dropped
- <title>: <one-line reason>   (keep these; they show how strict the lens bar is)
```
