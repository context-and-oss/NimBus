# Versioning & Release Policy

NimBus packages (`Akaule.NimBus.*`) follow [SemVer 2.0](https://semver.org/):

- **Major** — breaking changes: removed/changed public API, or behavior changes a
  consumer could observe without a compile error (called out explicitly in the
  release notes under ⚠️ Breaking).
- **Minor** — new features and non-breaking additions. Deprecations land here:
  the old surface gets `[Obsolete]` with a message naming the replacement.
- **Patch** — fixes only.

**Obsolete lifecycle:** deprecate in a minor, delete in the *next* major. Bridge
code (adapter constructors, forwarding overloads) lives exactly one major cycle.

**Release mechanics:** releases are tag-driven. Pushing a `v*` tag runs
`.github/workflows/nuget-publish.yml`, which builds Release (warnings-as-errors
for compiler warnings), runs the full test suite, packs with `/p:Version` from
the tag, pushes every packable project to nuget.org, and publishes the Resolver
and WebApp as deployable zips. The version lives only in the tag —
`Directory.Build.props` holds a `0.0.0` placeholder, so no file is edited to
release. A GitHub Release accompanies every tag; its notes follow the pattern
below, which is the one the release history actually uses (3.6.0 onward).

A build from source that cannot pass `/p:Version` sets the `NIMBUS_BUILD_VERSION`
environment variable instead; `/p:Version` wins when both are given. The
CrmErpDemo AppHost sets it from `git describe --tags` when `aspire deploy` builds
its images, as `nb deploy apps` stamps its own builds from the latest tag.

## Cutting a release

1. Read the two most recent releases first — `gh release view v<prev>` — and
   match them. The notes are what package consumers and operators read; a
   consistent house form keeps successive releases scannable.
2. Tag the release commit on `master` with an **annotated** tag whose body is
   the one-line title, nothing more. Notes live on the release, not in the tag,
   and a tag is never force-pushed: every `v*` tag push re-runs the publish
   workflow.

   ```shell
   git tag -a v3.7.0 -m "NimBus v3.7.0"
   git push origin v3.7.0
   ```

3. Create the release from a notes file written to
   [`.github/RELEASE_NOTES_TEMPLATE.md`](../.github/RELEASE_NOTES_TEMPLATE.md):

   ```shell
   gh release create v3.7.0 --verify-tag --title "NimBus v3.7.0" --notes-file notes.md
   ```

4. Confirm the workflow run is green and that nuget.org lists the new version
   (`https://api.nuget.org/v3-flatcontainer/akaule.nimbus.core/index.json`;
   indexing lags the push by a few minutes per package).

**The notes pattern**, in GitHub markdown, in this order and nothing else:

- **Title:** `NimBus vX.Y.Z` — no subtitle.
- **Opening sentence:** `NimBus X.Y.Z adds / improves / fixes …` — one
  sentence, what the release is for.
- **`### Highlights`** — plain bullets, one sentence each, verb first (`Added`,
  `Fixed`, `Improved`); bold only a UI feature name. Roughly one bullet per
  user-visible change, not per commit.
- **`### Compatibility`** — every change a consumer must act on: new or changed
  public surface, removed pages or endpoints, config defaults that moved,
  metrics renamed. State plainly when there is nothing (`No schema migration,
  topology change or wire change.`). A **major** additionally opens this
  section with the breaking changes.
- **Validation paragraph:** `This release was validated with …` — the build,
  the test counts, the suites that ran live, and any manual check.
- **Footer**, always in this order:
  - `PR:` / `PRs:` — the merged pull requests in the release, comma-separated.
    Omit the line only when every change landed directly on `master`.
  - `Commits:` — always present: the compare link from the previous
    *release* (`compare/v<prev>...vX.Y.Z`).

Every release uses every section, patches included — a patch is a sentence,
one or two Highlights bullets, Compatibility, validation and the footer. Aim
for the length of v3.6.0 — about twenty lines for a ten-item minor.

All published releases, back to 0.1.0-alpha, follow this pattern (rewritten
2026-09-23). Do not revive the pre-3.6.0 form (`## What's New`, emoji
sections, `**Full Changelog**`).

**Note for maintainers:** several DI registrations assume their service type has
a single public constructor (e.g. `AddSingleton<IManagerClient, ManagerClient>()`).
Adding a second constructor requires switching those registrations to explicit
factory lambdas to keep constructor selection deterministic.
