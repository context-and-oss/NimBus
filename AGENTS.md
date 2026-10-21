# NimBus — Agent Instructions

NimBus is an Azure-native event-driven integration platform on Azure Service Bus with
Cosmos DB or SQL Server storage: session-ordered processing, a centralized Resolver with a
full audit trail, a management WebApp (resubmit/skip), and declarative topology provisioning.

## Workflow

- Plan before implementing architectural changes, changes involving 5+ files, or work with
  uncertain scope. Do not use Superpowers skills or workflows. Store plans in `docs/plan/`
  and design specs in `docs/spec/`.
- Branch from `master` and submit changes through pull requests; follow
  [CONTRIBUTING.md](CONTRIBUTING.md).
- Add regression tests for bug fixes and verify changed behavior before declaring work
  complete. Report the checks run and any skipped integration tests explicitly.
- For PRs changing the WebApp's design or layout, or adding a WebApp feature, capture a
  representative screenshot of the finished UI and include it in the PR description.
- For a review by the other model (Claude reviewing Codex's work or vice versa) against the
  originating spec or plan, use `/review-change` (Codex: `$review-change`). It runs only when
  invoked; don't start it yourself after implementing.

## Build & test

```bash
dotnet build src/NimBus.slnx                    # .NET 10
dotnet test src/NimBus.slnx
dotnet build src/NimBus.slnx -c Release         # what CI runs — do this before pushing
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp install   # Node.js 22
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
dotnet run --project src/NimBus.AppHost        # local Aspire stack
```

- Release promotes **compiler (CS) warnings** to errors; analyzer warnings (CA/S/SA) stay
  non-fatal. **CS8767** (nullability mismatch on an interface implementation) fails Release
  while Debug stays green.
- Every project runs the .NET analyzers and `EnforceCodeStyleInBuild`. Don't opt a project
  out; suppress a specific rule where it doesn't fit (see the test-file pragma below).
- Use `npm run test:ci` (or `npm test -- --run`) for a terminating frontend test run;
  `npm test` can enter watch mode.
- Live SQL Server conformance tests require `NIMBUS_SQL_TEST_CONNECTION`. Live Cosmos DB
  tests require `NIMBUS_COSMOS_TEST_CONNECTION`, or both `NIMBUS_COSMOS_TEST_ENDPOINT` and
  `NIMBUS_COSMOS_TEST_KEY`. They skip when configuration is absent, except that
  `NIMBUS_COSMOS_TEST_REQUIRED=1` makes missing Cosmos configuration fail. The Cosmos
  emulator uses `NIMBUS_COSMOS_TEST_GATEWAY=1`. CI supplies both providers and rejects
  skipped conformance tests; see `.github/workflows/dotnet.yml`.

## Where things live

- `src/` — `NimBus.Core` (pipeline, retry, outbox), `NimBus.ServiceBus` (transport,
  provisioning), `NimBus.SDK` (DI registration), `NimBus.Resolver`,
  `NimBus.MessageStore.*` (storage contracts and providers), `NimBus.WebApp` (+ `ClientApp/`:
  React 19, TypeScript, Vite 8, Tailwind, Vitest), `NimBus.CommandLine` (`nb` CLI),
  `NimBus.Testing` (in-memory transport and storage conformance suite), `NimBus.Extensions.*`.
- `tests/` — one `*.Tests` project per library (MSTest). `samples/` — AspirePubSub,
  CloudEventsInterop, CrmErpDemo, DynamicsBcDemo.
- `docs/` — guides, `docs/adr/` for design rationale, `docs/spec/` for specs and designs,
  `docs/plan/` for implementation plans and plan reviews. Do not create `docs/superpowers/`
  or `docs/specs/`.

## Conventions

- C# latest: file-scoped namespaces, nullable enabled, namespaces `NimBus[.Project][.Folder]`.
- MSTest (`[TestClass]`, `[TestMethod]`); test files start with
  `#pragma warning disable CA1707, CA2007`. Parameterized tests use `[TestMethod]` with
  `[DataRow]`; `[DataTestMethod]` is obsolete (`MSTEST0044`).
- Newtonsoft.Json for core message serialization. WebApp MVC uses System.Text.Json;
  Newtonsoft attributes alone do not enforce its request contracts.
- `Microsoft.Extensions.Logging` for logging (ADR-006); features register through
  `services.AddNimBus*()` extension methods.
- Dependency versions belong in individual `.csproj` files. `Directory.Packages.props`
  provides shared analyzer references but disables central package version management.
- XML doc comments on public types and members.
- Never delete public API outright: mark it `[Obsolete]` with a backward-compatible bridge
  and remove it in the next major (`docs/versioning.md`).
- Conventional Commits (`feat(webapp): …`, `fix(core): …`).

## Gotchas

- The WebApp API is generated from `src/NimBus.WebApp/api-spec.yaml` by NSwag at build time.
  Edit the spec, not `Controllers/ApiContract.g.cs` or `ClientApp/src/api-client/index.ts`.
  `SkipSpaBuild=true` also skips NSwag generation; validate API-spec changes with a build
  that leaves generation enabled.
- A new storage interface member must be implemented in every provider (Cosmos DB, SQL
  Server, in-memory), covered by the conformance suite, and forwarded by
  `InstrumentingMessageTrackingStoreDecorator` (OpenTelemetry). An unforwarded default
  interface method silently bypasses the real store.
- `EventTypeId` is the unqualified class name and is global to the Service Bus namespace:
  two event classes with the same name collide.
- Service Bus topology is created only by `ServiceBusTopologyProvisioner`
  (`nb topology apply`).
- Releases follow `docs/versioning.md#cutting-a-release` exactly, including the
  release-notes pattern.

## What not to do

- Don't add transport abstractions. Azure Service Bus is the reference transport; ADR-018 allows
  only one more, RabbitMQ for on-premises installations, matching Service Bus behaviour.
- Don't chase NServiceBus feature parity; focus on the Resolver, WebApp and sessions.
- Don't rewrite the WebApp; enhance it incrementally.
- Don't build event sourcing without a concrete use case.
