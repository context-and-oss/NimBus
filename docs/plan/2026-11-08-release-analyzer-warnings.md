# Clear the Release build's analyzer warnings

Status: complete (2026-11-09).

## Goal

`dotnet build src/NimBus.slnx -c Release --no-incremental` reported 1,518 unique warnings
across ~360 files (2026-11-08, `master` at `f030350a`): 1,506 analyzer (CA) warnings and 12
CS1574 warnings. None failed the build, because Release keeps analyzer warnings non-fatal and
`Directory.Build.props` exempts CS1574, CA1848, CA1873 and CA1305. The goal is a Release
build with zero warnings and no behavior change.

An incremental build only re-reports projects it recompiles, so every measurement here uses
`--no-incremental`.

## Decisions

- **CA1848 / CA1873 (logging, 482 warnings).** In `src/`, logging calls become
  `[LoggerMessage]` source-generated `private static partial void LogXxx(ILogger logger, ...)`
  methods declared on the calling type, which becomes `partial`. In `samples/`, both rules
  are off in `samples/.editorconfig`: the samples are teaching code, and plain `ILogger` calls
  read better there. Chosen by the maintainer on 2026-11-08.
- **Optional loggers.** Types that stored an optional `ILogger? _logger` and logged with
  `_logger?.LogX(...)` now default the field to `NullLogger` in the constructor. A
  `[LoggerMessage]` method needs a non-null logger, and one null-check in the constructor
  replaces one per call. With no logger configured, the arguments of a log call are now
  evaluated before the generated method discards the message. The Cosmos DB unit-test fake
  returned `null` responses and relied on that short-circuit; it now returns a response, as
  the Cosmos SDK does.
- **CA1873 inside `IsEnabled` guards.** The .NET 10.0.403 analyzer recognizes an
  `IsEnabled` guard around `logger.LogX(...)` but not around a `[LoggerMessage]` call. The
  eight guarded sites evaluate their expensive arguments into locals inside the guard, which
  satisfies the rule without a suppression and keeps the work behind the guard.
- **Public API.** No public member is removed, renamed or re-typed (`docs/versioning.md`).
  CA1711 on `IEventHandler<T>` and `MessagePipelineDelegate`, and CA1822 on
  `EmailChannel.BuildSubject`, get a `[SuppressMessage]` with a justification.
  `IServiceBusSession.DeadLetterAsync` keeps its `reason`/`v` parameter names, and the
  `ServiceBusSession` implementation keeps `deadLetterReason`/`deadLetterErrorDescription`:
  renaming either side breaks callers that pass those arguments by name (CS1739), so the
  implementation suppresses CA1725 with a justification. The
  `T_Event` type parameter becomes `TEvent` (CA1707), which no caller can observe.
- **Test code.** Test files missing the house pragma `#pragma warning disable CA1707,
  CA2007` get it. `tests/.editorconfig` turns off CA1861 and CA1859 (performance rules that
  hurt test readability), CA1001 (per-test disposables live in `[TestCleanup]`) and CA1711
  (test doubles named for what they stand in for). The storage conformance suite in
  `src/NimBus.Testing/Conformance` is test code that ships, so it gets the same CA1861 setting.
- **Owned token sources.** `SimulatedEndpointHost`, `SimulatedPublisher` and
  `AzureAccessTokenCache` keep their CA1001 warnings suppressed with a justification:
  the simulation abandons loops that still observe the token after a stop times out, and the
  token cache's `SemaphoreSlim` never allocates a wait handle.
- **Behavior-preserving fixes only.** CA1307 adds the `StringComparison.Ordinal` that
  already matches the overload's default (MSTest's `StringAssert` compares ordinally).
  CA1304, CA1310, CA1311 and CA1305 use the invariant culture: entity names, SQL scalars,
  generated scripts and log formatting must not depend on the machine's locale.
- **Build policy unchanged.** `WarningsNotAsErrors` still lists CS1574, CA1848, CA1873 and
  CA1305; removing them would make new warnings of those kinds fail CI and is a separate
  decision.

## Steps

1. Apply analyzer code fixes with `dotnet format analyzers --severity info` for the rules
   whose fixers are mechanical (CA1510, CA1512, CA1507, CA1860, CA1854, CA1845, CA1850,
   CA2249, CA1875, CA2263) and review the diff.
2. Add the missing test-file pragma (CA1707).
3. Fix the remaining rules by hand or with location-driven scripts: CA1307, CS1574 crefs,
   CA1304/CA1311/CA1310/CA1305/CA1862, CA1861, CA1859, CA1852, CA1805, CA1822, CA1001,
   CA1816, CA2201, CA1806, CA1826, CA1869, CA2254, CA1000, CA1711, CA1068, CA1725.
4. Turn off CA1848/CA1873 under `samples/`.
5. Default optional loggers to `NullLogger`, then convert `src/` logging to `[LoggerMessage]`
   with a Roslyn codemod (309 calls in 68 files) that takes parameter types from the semantic
   model. Two calls whose templates the codemod could not express (a repeated placeholder and
   a helper that took the template as a parameter) are converted by hand.

## Verification

- `dotnet build src/NimBus.slnx -c Release --no-incremental`: 0 warnings, 0 errors.
- `dotnet build src/NimBus.slnx` (Debug): 0 warnings, 0 errors.
- `dotnet test src/NimBus.slnx -c Release --no-build`: 3,768 passed, 0 failed, 459 skipped,
  the same totals as before the change. The skipped tests are the live SQL Server and Cosmos
  DB conformance tests, which need `NIMBUS_SQL_TEST_CONNECTION` and `NIMBUS_COSMOS_TEST_*`;
  CI supplies both.
- Frontend untouched; no frontend checks needed.
