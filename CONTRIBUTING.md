# Contributing to NimBus

Thanks for your interest in contributing. NimBus welcomes external contributions — **all changes come in through pull requests against `master`**. This guide covers how to propose work, the bar a PR has to clear, and where help is most valuable.

NimBus is MIT-licensed. By submitting a contribution you agree to license it under the [MIT License](LICENSE).

## TL;DR

1. **Talk first for anything non-trivial.** Open a [backlog issue](.github/ISSUE_TEMPLATE/backlog-item.yml) (or comment on an existing one) before writing code. Anything touching core gets a design review — see [Design review & ADRs](#design-review--adrs).
2. Fork, branch with a `feat/` · `fix/` · `chore/` · `docs/` prefix.
3. Build and test in **Release** locally — compiler warnings become errors there.
4. Open a PR to `master`, fill in the checklist, link the issue.
5. A maintainer reviews. Keep PRs focused and small where you can.

## Before you start

- **Small fixes** (typos, docs, an obvious bug with a clear repro): just open a PR.
- **Anything else** (a feature, a new pipeline behavior, a storage provider, a UI addition): **open an issue first** using the backlog-item template so we can agree on approach and priority before you invest time. This avoids PRs that have to be reworked or declined.
- Not sure whether your idea fits? See [What we will and won't take](#what-we-will-and-wont-take) and [docs/contribution-areas.md](docs/contribution-areas.md) for where help is actively wanted.

## Development setup

Requirements: **.NET 10 SDK**, **Azure Functions Core Tools** and **Node.js 22** (for the WebApp client).

```bash
# From the repository root: build the whole solution
dotnet build src/NimBus.slnx

# Run all tests
dotnet test src/NimBus.slnx

# WebApp client
npm --prefix src/NimBus.WebApp/ClientApp install
npm --prefix src/NimBus.WebApp/ClientApp run build

# Local end-to-end via Aspire
dotnet run --project src/NimBus.AppHost -- --UseEmulator true
```

CI (`.github/workflows/dotnet.yml`) runs `restore` → `build --configuration Release` → `test` on every PR to `master`, including the SQL Server message-store conformance suite in a service container. **Reproduce failures locally by building Release**, since that's where compiler warnings become errors:

```bash
dotnet build src/NimBus.slnx --configuration Release
```

The local AppHost uses SQL Server in a container and can use the NimBus Service Bus emulator. A running Docker-compatible container runtime is required. See [Getting Started](docs/getting-started.md) for Azure and Cosmos alternatives. The WebApp builds its frontend during the .NET build; NSwag runs from its NuGet package without a local tool manifest.

## Package publishing (maintainers)

Packages use the `Akaule.NimBus.*` prefix; assemblies and namespaces remain `NimBus.*`. Push a version tag to trigger the [NuGet publish workflow](.github/workflows/nuget-publish.yml):

```shell
git tag v1.0.0
git push origin v1.0.0
```

Use the intended new release version; prerelease tags such as `v1.0.0-preview.1` are supported.

Tag with `git tag -a vX.Y.Z -m "NimBus vX.Y.Z"` and pair every tag with a GitHub Release whose notes follow the house pattern — see [Cutting a release](docs/versioning.md#cutting-a-release) and the [notes template](.github/RELEASE_NOTES_TEMPLATE.md).

## Branching & commits

- Branch from `master`. Name it with a type prefix and a short kebab summary, e.g. `feat/cloudevents-envelope`, `fix/resolver-session-lag`, `docs/contributing-guide`.
- Use [Conventional Commits](https://www.conventionalcommits.org/) for messages: `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`. Scope is encouraged, e.g. `feat(webapp-ui): …`.
- Keep each PR to one logical change. Split unrelated work into separate PRs.

## Code conventions

The analyzer packages (AsyncFixer, Meziantou, SecurityCodeScan, SonarAnalyzer, StyleCop) are referenced through `Directory.Packages.props`. The .NET analyzers and `EnforceCodeStyleInBuild` are enabled in every project; analyzer warnings remain non-fatal in Release.

- **C#**: latest language features — file-scoped namespaces, nullable reference types, implicit usings.
- **Namespaces**: `NimBus[.Project][.Subfolder]`.
- **Tests**: MSTest (`[TestClass]` / `[TestMethod]`). Put `#pragma warning disable CA1707, CA2007` at the top of test files.
- **Serialization**: Newtonsoft.Json (`JsonConvert`).
- **Logging**: `Microsoft.Extensions.Logging` — no custom logging abstraction (see ADR-006).
- **DI**: register features via `services.AddNimBus*()` extension methods.
- **Public API**: XML doc comments on public types and members.
- **Backward compatibility**: don't delete public API. Mark it `[Obsolete]` and keep a working bridge.

Every contribution should come with tests. New behaviors, storage providers, and transport changes should extend the relevant conformance suite in `NimBus.Testing` where one exists.

## Design review & ADRs

NimBus records significant decisions as [Architecture Decision Records](docs/adr). **Open an issue and expect an ADR discussion before you send a PR** if your change touches:

- the transport (`NimBus.ServiceBus`) or transport abstractions,
- the Resolver or its message-state model,
- the storage abstractions (`NimBus.MessageStore.Abstractions`) or the wire/envelope format,
- anything that changes a public contract in `NimBus.Abstractions`.

Smaller, self-contained work — a new pipeline behavior, an extension, a WebApp enhancement, docs — doesn't need an ADR, just an issue to align on scope.

## What we will and won't take

NimBus has a deliberately narrow focus. Contributions that fit the direction are very welcome; these ones generally aren't, and are worth a conversation before any code:

- **Don't abstract the transport.** Azure Service Bus is NimBus's strength — a generic transport layer is explicitly out of scope.
- **Don't chase NServiceBus feature parity.** The focus is the Resolver, the WebApp, and session-based ordering.
- **Don't rewrite the WebApp.** Enhance it incrementally.
- **No event sourcing** without a concrete, agreed use case.

## Pull request checklist

Before you open a PR, confirm:

- [ ] There's a linked issue (for anything beyond a trivial fix), and the approach was agreed.
- [ ] `dotnet build src/NimBus.slnx --configuration Release` is clean (no warnings).
- [ ] `dotnet test src/NimBus.slnx` passes, and new/changed behavior has tests.
- [ ] Public API changes have XML docs; removed API is bridged with `[Obsolete]`, not deleted.
- [ ] Docs updated if behavior or configuration changed (and an ADR added if the change is architectural).
- [ ] The PR is focused on one logical change with a Conventional-Commit title.

## Review & merge

A maintainer will review for design fit, correctness, tests, and the conventions above. Expect a round or two of feedback — it's normal. Once approved and green, a maintainer adds the PR to the `master` merge queue. The queue re-runs CI on top of the latest `master`, so you don't need to keep your branch up to date yourself. Releases are published as `Akaule.NimBus.*` packages separately.

## Questions

Open an issue with the question template, or start a discussion on an existing backlog item. For where to begin, see [docs/contribution-areas.md](docs/contribution-areas.md).
