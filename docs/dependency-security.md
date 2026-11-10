# Dependency security audit

The [`Dependency security audit`](../.github/workflows/dependency-security.yml) workflow runs
daily at 04:17 UTC (and on demand via **Actions → Dependency security audit → Run workflow**).
It scans every third-party package the repository pulls in, applies the version bumps that
clear published advisories, and opens a pull request with the result. Nothing is ever pushed
to `master`.

## What it covers

| Ecosystem | Scanner | Scope |
| --- | --- | --- |
| NuGet | `dotnet list package --vulnerable --include-transitive` | Every tracked solution, plus any tracked `.csproj` no solution covers (today `src/NimBus.Manager`). Top-level and transitive packages. |
| npm | `npm audit --package-lock-only` | Every tracked `package-lock.json`: `src/NimBus.WebApp/ClientApp`, `samples/CrmErpDemo/{Crm.Web,Erp.Web,e2e}`, `samples/DynamicsBcDemo/{D365Sales.Web,BusinessCentral.Web}`, `website`. |

Targets are discovered from `git ls-files` on every run, so a new project or SPA is picked up
without editing the workflow.

`src/NimBus.WebApp/ClientApp/src/api-client` has no lockfile and is therefore not audited; it is
a generated client whose dependencies are not installed by any build.

## How it fixes things

**NuGet.** For each advisory on a vulnerable package the workflow asks the GitHub Advisory API
which version range is affected and what the first patched version is, then bumps to the lowest
version no advisory still flags — `4.5.0` becomes `4.5.1`, not "whatever is latest". The
`Version` attribute is rewritten in place in every `.csproj`/`.props`/`.targets` that pins the
package, so surrounding formatting survives.

Left alone deliberately, and reported under **Needs a human** in the PR instead:

- **Transitive packages.** The report includes the `dotnet nuget why` dependency path. Usually
  bumping the top-level parent is enough (and often already happened in the same run); pinning a
  transitive package directly across 60+ projects is a decision for a person.
- **Floating versions** (`[1.0,2.0)`, `1.*`) — rewriting one changes the resolution strategy,
  not just the version.
- **Advisories with no published fix.**

**npm.** `npm audit fix --package-lock-only` — no install, no postinstall scripts (which also
keeps the Playwright suite from downloading browsers). Without `--force` it stays inside the
semver range declared in `package.json`, so anything needing a major bump is reported rather
than applied.

The workflow upgrades to **npm 11** before auditing. Node stays on 22 because the SPA build
targets it, but the npm 10 that ships with Node 22 silently declines fixes npm 11 applies:
given an identical lockfile and identical flags, npm 10 left `fast-uri` on the vulnerable
3.1.4 while npm 11 bumped it to the patched 3.1.5. Note also that `npm audit fix --dry-run`
under-reports — it printed "up to date" for that same fix the real run then applied — so
verify a suspected miss by running it for real and reverting, not by trusting `--dry-run`.

Beware npm's "fix available via `npm audit fix`" wording in the report: it means the advisory
*has* a patched version, not that npm can reach it from your declared ranges. `react-router`
is the current example — patched only in 7.17.1, while the workspaces declare `^6.x`, so only
`--force` would take it, and that is a v6 to v7 migration rather than a dependency bump.

## The pull request

Fixes land on the bot-owned branch `deps/security-audit`, which is **rebuilt from `master` and
force-pushed on every run** — the same model Dependabot uses. Do not commit to it by hand; if
you need to change something, branch off it. An already-open PR is updated in place rather than
duplicated.

Before the PR is opened or refreshed, the workflow builds every solution in `Release` (the
configuration CI uses, where `TreatWarningsAsErrors` is on) and re-runs the NuGet scan so the
attached report describes the tree as it would merge. If that build fails the PR is still
opened, with a warning at the top of the body — a broken bump is more useful visible than
silently dropped.

The run itself fails when a vulnerability at or above **high** survives the audit, so the daily
red X means "something needs a person", not "something was found". The threshold is a
`workflow_dispatch` input (`none`/`low`/`moderate`/`high`/`critical`).

## Repository setup

Two things are worth checking if the workflow cannot open a PR:

1. **Settings → Actions → General → Workflow permissions**: *Allow GitHub Actions to create and
   approve pull requests* must be enabled for the default `GITHUB_TOKEN` to work.
2. **Optional `DEPS_PR_TOKEN` secret.** A PR opened with `GITHUB_TOKEN` does not trigger other
   workflows, so the `.NET` CI will not run on it. Setting `DEPS_PR_TOKEN` to a PAT or GitHub App
   token with `contents:write` + `pull_requests:write` gives the generated PR full CI. Without
   it, closing and reopening the PR once also starts CI.

## Running it locally

Both scripts work on Windows and Linux under PowerShell 7 and are safe to run without `-Fix`:

```powershell
pwsh .github/scripts/Invoke-NuGetAudit.ps1                    # report only
pwsh .github/scripts/Invoke-NuGetAudit.ps1 -Fix               # rewrite versions in place
pwsh .github/scripts/Invoke-NpmAudit.ps1 -Fix -MarkdownOut npm.md
```

Set `GITHUB_TOKEN` before running the NuGet script to avoid the unauthenticated GitHub Advisory
API rate limit (60 requests/hour); `gh auth token` produces a usable value.

## Dropped rather than patched: the Postman collection generator

`GHSA-qxc2-j82w-r537` (`@faker-js/faker`, high) held the daily run red for weeks. It reached the
WebApp SPA only as `openapi-to-postmanv2` (devDependency) -> `postman-collection@5.3.1` ->
`@faker-js/faker`, pinned there at the exact version `5.5.3`. The only fix npm offered was a
semver-major downgrade of the converter to `4.18.0`.

Overriding the transitive pin does not work, and the failure is worth recording so nobody
retries it: the advisory covers `<=10.4.0`, so an override has to reach `10.5.0` or later, and
`postman-collection` is written against the faker 5 API — `lib/superstring/dynamic-variables.js`
reads `faker.address.city` off `require('@faker-js/faker/locale/en')` at module load, which
throws `TypeError: Cannot read properties of undefined` on every faker release that clears the
advisory. Verified against `postman-collection@5.3.1` with `@faker-js/faker@^10.5.0`.

The converter was removed instead. It was never part of the build: `NimBus.WebApp.csproj` hooked
its `GenPostmanCollection` target to `AfterTargets="NpmRunBuild"`, a target no project defines,
so it had never run — the generated `api.postman_collection.json` was weeks stale and never
appears in a build log. The output is gitignored and nothing consumes it. So the dead target is
gone and `npm run gen-postman` now fetches the converter on demand:

```bash
cd src/NimBus.WebApp/ClientApp && npm run gen-postman
```

which runs `npx --yes --package openapi-to-postmanv2@6.3.3`. The tool is no longer installed into
an audited workspace, nothing from it ships in the SPA bundle or any NuGet package, and its only
input is our own checked-in `api-spec.yaml`. Anyone running that script should know they are
pulling a package with a known-high advisory onto their machine for the length of one conversion.
Revisit if `postman-collection` ever moves off faker 5 — track
[postman-collection](https://github.com/postmanlabs/postman-collection) and
[openapi-to-postman](https://github.com/postmanlabs/openapi-to-postman).

Removing the converter also retired two overrides that existed only for its subtree: the scoped
`ajv` pin, and a repository-wide `yaml: ^1.10.3` that was quietly forcing Vite and Tailwind onto
yaml 1.x.

## Relationship to the build-time audit

`Directory.Build.props` sets `NuGetAudit`/`NuGetAuditMode=all`/`NuGetAuditLevel=moderate`, which
raises NU1901-NU1904 at restore time. That gate only fires when someone triggers a build and
only covers packages restored by that build. This workflow runs on a clock, so an advisory
published against a dependency nobody touched is still caught, and it covers npm as well.
