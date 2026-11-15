---
name: make-pr-easy-to-review
description: "Prepare a NimBus pull request for review without changing what it does: regroup noisy commits into Conventional Commits in dependency order, rewrite the PR description in the repo's Why / What changes / Verification / Not done shape, and point reviewers at the files that matter and away from generated ones. Use for \"make this PR easy to review\", \"tidy this PR\", \"clean up the commits\" or \"improve the PR description\". Invoke with an optional PR number or branch."
argument-hint: "[pr-number-or-branch]"
---

# Make PR easy to review

Prepare a pull request so a reviewer can see quickly why it exists, what it changes, where to start reading and what could go wrong. **The code stays as it is.** This skill changes commits, the PR title and the PR description, never behaviour.

## How NimBus PRs land

These facts decide what is worth tidying:

- PRs target `master` on the public repo `context-and-oss/NimBus`. The required check is `ci`.
- The merge queue **squashes**, and the squash message is the PR title followed by **every commit message**. So the PR title must be a Conventional Commit (`feat(webapp): …`), and each commit message ends up in `master`'s history. A trail like `wip`, `fix test`, `address review` is worth cleaning up for that reason alone.
- Pushing to the PR **dismisses existing approvals**. Rewriting history after someone approved costs them a re-review: say so before you push.
- **No stacked PRs.** A squash queue breaks stacks. A multi-part change is one PR with a commit per part; a PR that is too big splits into independent PRs from `master`.
- The repo is public. Screenshots use demo data, never real tenant, customer or endpoint names. Never mention an unpublished security advisory (a draft GHSA) or its details.

## Arguments

A PR number or branch name, if given. Otherwise use the PR for the current branch (`gh pr view`). If there is none, work on the current branch against `origin/master` and offer to open the PR at the end.

## 1. Read the PR

```bash
gh pr view <PR> --json number,title,body,state,isDraft,baseRefName,headRefName,commits,files,reviews
git fetch origin master <headRefName>
MB=$(git merge-base origin/master origin/<headRefName>)
git log --oneline $MB..origin/<headRefName>
git diff --stat $MB origin/<headRefName>
```

Stop if the state is not `OPEN` or the PR is in the merge queue: rewriting it then does harm. `gh pr view` has no merge-queue field; ask GraphQL:

```bash
gh api graphql -f query='{repository(owner:"context-and-oss",name:"NimBus"){pullRequest(number:<PR>){isInMergeQueue}}}'
``` Read the linked issue, and the spec (`docs/spec/NNN-*/spec.md`) or plan (`docs/plan/*.md`) the commits or body name. They are what the description should explain.

## 2. Sort the changed files

Put every changed path in one bucket. The buckets are the reading order you will give the reviewer:

| Bucket | NimBus paths |
| --- | --- |
| Intent | `docs/spec/`, `docs/plan/` |
| Contracts | `src/NimBus.WebApp/api-spec.yaml`, `src/NimBus.MessageStore.Abstractions/`, public types in `src/NimBus.*`, SQL schema scripts in `src/NimBus.MessageStore.SqlServer/Schema/` |
| Logic | `NimBus.Core`, `NimBus.ServiceBus`, `NimBus.Resolver`, `NimBus.MessageStore.*` providers, `NimBus.Extensions.*` |
| Wiring | `AddNimBus*()` registrations, `NimBus.SDK`, `NimBus.AppHost`, `NimBus.CommandLine` |
| Surface | `src/NimBus.WebApp/` controllers and services, `ClientApp/` |
| Tests | `tests/`, `*.test.tsx`, `samples/CrmErpDemo/e2e/` |
| Docs | `docs/` (outside spec/plan), `website/`, `samples/` |
| Generated | `src/NimBus.WebApp/Controllers/ApiContract.g.cs` and `ClientApp/src/api-client/index.ts` (both from `api-spec.yaml`), `package-lock.json` |
| Mechanical | renames, moves, formatting, line-ending churn |

Find mechanical churn with `git diff -w --ignore-cr-at-eol --stat $MB origin/<headRefName>` and compare it with the plain stat. A file that drops out, or shrinks a lot, is mostly whitespace or line endings. Prettier is not enforced in CI and many files don't pass it, so a whole-file reformat in a feature PR is usually accidental.

## 3. Find what a reviewer will trip over

Check for these and report them. **Don't fix them silently**: each one is a behaviour or scope question for the author.

- **Mixed concerns:** mechanical churn or an unrelated fix inside a logic commit.
- **Generated files out of step:** `api-spec.yaml` changed but the generated contract and client didn't, or the reverse. Reviewers should read the spec, not the generated code.
- **Storage interface member:** a new member needs every provider (Cosmos DB, SQL Server, in-memory), a conformance-suite case in `NimBus.Testing`, and forwarding in `InstrumentingMessageTrackingStoreDecorator`. An unforwarded default interface method bypasses the real store without any error.
- **Public API removed** instead of marked `[Obsolete]` with a bridge (`docs/versioning.md`). A breaking change needs a major version; a patch release is fixes only.
- **New event class** whose name another event already uses: `EventTypeId` is the unqualified class name and is global to the namespace.
- **Service Bus topology** created anywhere other than `ServiceBusTopologyProvisioner` (`nb topology apply`).
- **New page under `docs/`** missing from `website/sidebars.js` or `unpublishedFiles` in `website/docs-scope.js`.
- **Bug fix without a regression test**, or a test written after the code that was never seen failing.
- **WebApp design, layout or feature change without a screenshot** in the description.
- **Description out of date:** it describes an earlier version of the branch, or claims checks that the commits since then haven't had.

## 4. Propose, then apply

Show the plan before changing anything: the new commit list with messages, and an outline of the new description. **Rewrite history only when the user asks for it or agrees to the plan.** If you only edit the description, no agreement on history is needed, but show the new body before you save it.

If the PR is too big to make reviewable with notes, say so and propose a split into independent PRs from `master` instead of polishing around the problem.

## 5. Regroup the commits

Record what must not change:

```bash
ORIGINAL_HEAD=$(git rev-parse origin/<headRefName>)
ORIGINAL_TREE=$(git rev-parse "$ORIGINAL_HEAD^{tree}")
```

Work on the PR branch, in its own worktree if the main checkout is shared. Interactive rebase isn't available to agents, so rebuild from the merge base: `git reset --soft $MB`, then `git restore --staged .`, then stage by path and commit, group by group. Never `git add -A` (it sweeps in untracked local files). A file whose hunks belong to two groups goes in the later commit; say so in the Commits section rather than splitting hunks.

Order the commits so each builds on the one before:

1. Intent: spec or plan, if the PR carries one.
2. Contracts, each complete in one commit: `api-spec.yaml` **with** its regenerated contract and client; a storage interface **with** every provider, the conformance case and the decorator; schema scripts.
3. Logic.
4. Wiring.
5. Surface: WebApp and ClientApp.
6. Docs and samples.
7. Mechanical churn, alone and labelled (`style(webapp): format topology components`), if it can't be dropped.

Keep tests in the commit with the code they cover, so each commit can be reviewed on its own. For a multi-part plan, one commit per part of the plan can replace this order; follow the plan.

Fold "address review" fixes into the commit they fix, **unless a reviewer has already reviewed the PR**. Then keep them as separate commits, so the reviewer can see what changed since their review.

Commit messages are Conventional Commits with a scope (`feat(webapp)`, `fix(core)`, `test(resolver)`, `docs(spec)`, `chore(deps)`) and an imperative summary. The body says why, for anything that isn't obvious. Because every message lands in `master`, write them for someone reading history in a year. No agent session links.

Before pushing, prove that only the history changed:

```bash
echo "Original tree: $ORIGINAL_TREE"
echo "Current tree:  $(git rev-parse HEAD^{tree})"
git diff "$ORIGINAL_HEAD" HEAD --stat   # must print nothing
```

**Don't push if the trees differ.** Find what moved, and fix it or abandon the rewrite. Then check again that the PR is still `OPEN`, and push with a lease, so a commit someone else pushed meanwhile is not lost:

```bash
git push --force-with-lease=<headRefName>:$ORIGINAL_HEAD origin HEAD:<headRefName>
```

## 6. Rewrite the description

Use the repo's PR shape. Leave out a section with nothing to say, except Verification:

```markdown
## Why
<The problem, in a paragraph. Link the issue (`Closes #123`) and the spec or plan path.>

## What changes
<TL;DR that matches the actual diff. A table when there are several parallel items.
State the upgrade impact plainly: "Nothing changes on upgrade", or what an operator must do
(schema scripts, `nb topology apply`, new configuration keys, a new switch and its default).>

Guards worth reviewing:
- <Each place where the change refuses, limits or orders something, and why.>

Changes to existing behaviour:
- <Everything an existing user would notice. A behaviour change hidden here is a defect in the description.>

## Screenshot
<Required for a WebApp design, layout or feature change. Demo data only.>

## Commits
<How the commits are grouped and where to start reading. Name the files to read closely and the
ones to skim: generated (`ApiContract.g.cs`, `api-client/index.ts`, lock files) and mechanical.>

## Verification
<What was run, with numbers: Release build, `dotnet test`, `npm run test:ci`, website build, live
checks. Which tests were seen failing first. Name the skipped tests explicitly: the live SQL Server
and Cosmos DB conformance suites need a connection and run only in CI.>

## Not done
<What wasn't verified or is deferred, and why.>
```

Only claim checks that ran on the current head. If the commits changed since the last run, either run the checks again or say which head they ran on.

A screenshot needs a URL. Push an asset-only commit to a `pr-assets/<topic>` branch and link it as `https://raw.githubusercontent.com/context-and-oss/NimBus/<sha>/<file>.png`, so it never merges. Keep `pr-assets/*` branches after the merge: deleting them breaks the image.

Save the body with `gh pr edit <PR> --body-file <file>`, written to a temp file. Fix the title with `--title` if it isn't a Conventional Commit that describes the whole PR.

## 7. Report

Tell the user:

- The commit list before and after, and the tree check (`Original tree` = `Current tree`), or that history was left alone.
- What changed in the title and description.
- The findings from step 3 that still need the author, and whether a split is recommended.
- Whether approvals were dismissed by the push.

## Guardrails

- Never hide a behaviour change in a "cleanup", "style" or "refactor" commit.
- If you find a bug while tidying, report it. Don't fix it as part of this skill.
- Don't bypass hooks or signing (`--no-verify`, `--no-gpg-sign`) unless the user asks.
- Don't rewrite a PR that is merged, closed or in the merge queue.
- Don't remove or reword someone else's review replies or commits without asking.

---

Adapted from Lauren Tan's `make-pr-easy-to-review` skill (github.com/ericlitman/open-pstack, MIT). Changes: NimBus's squash merge queue, file buckets and review hazards; the repo's PR description shape; commit-by-path regrouping for agents without interactive rebase; a lease-guarded push.
