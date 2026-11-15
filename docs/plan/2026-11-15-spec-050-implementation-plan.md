# Spec 050 (retention settings): implementation plan

Status: **draft** (2026-11-15). Nothing is implemented. Implements
[Spec 050](../spec/050-retention-settings/spec.md). Planned against master `3cd73dad`. Where this
plan and the spec disagree, the spec wins.

## Delivery

One PR with a commit per step, released as a 4.x minor. Each step adds its failing tests first
(RED, then GREEN) and follows [CONTRIBUTING.md](../../CONTRIBUTING.md) and Conventional Commits.
The PR includes a screenshot of Settings → Retention (AGENTS.md).

| Step | Content | Commit |
|---|---|---|
| 0 | Phase 0 checks (spec §14) | `docs(spec): record Spec 050 phase 0 results` |
| 1 | `RetentionSettings`, storage members in all providers, conformance | `feat(store): store retention settings` |
| 2 | `RetentionPolicy`; Cosmos stamping | `feat(store): stamp retention periods on Cosmos writes` |
| 3 | SQL cleanup on AF-199's job: policy-driven periods, the resolved-period rule, the seam, the metric | `feat(store): drive SQL Server retention from the retention settings` |
| 4 | API and page | `feat(webapp): set retention in Settings` |
| 5 | Pins in templates and CLI; carry-forward; mismatch; `nb container` | `feat(cli): pin retention at deployment` |
| 6 | Docs and release notes | `docs: retention settings` |

## Step 0: Phase 0

On a temporary resource group per provider in the non-production subscription, deleted afterwards:

- **Spec §14 item 1.** Use the Data Explorer, or a small console against a throwaway Cosmos account.
- **Items 2 and 3.**
  - Seed a million rows each into `UnresolvedEvents`, `Messages` and `MessageAudits` on S0.
  - Apply AF-199's index script, timing it and noting whether `ONLINE = ON` is accepted.
  - Run the step 3 rules by hand in AF-199's 5,000-row batches while Simulation traffic runs.
    That includes the widened resolved-period rule, and its query plan shows a seek.
  - Record the batch time, `dtu_consumption_percent` and `sys.dm_tran_locks` waits.
- **Item 4.** Read `ServiceHealth`'s shape in both providers.
- Record everything in `docs/spec/050-retention-settings/phase0-results.md`. Adjust the spec's
  batch size, pause and index options to the results.

## Step 1: storage

- **Contracts.**
  - `RetentionSettings` (in `States/`, next to `AuditSettings`), with `SingletonId`, four nullable
    `int` fields and validation helpers.
  - `RetentionPeriods`, the resolved, non-null record.
- **`IEndpointMetadataStore`** gains `GetRetentionSettings` and `SetRetentionSettings`.
  - Default implementations throw `NotSupportedException`.
  - Cosmos: `CosmosDbEndpointMetadataStore`, `settings` container, id `RetentionSettings`.
  - SQL: a new DbUp script (next free number) creating `RetentionSettings`
    (`Id`, `SettingsJson`, `UpdatedAtUtc`), and `SqlServerEndpointMetadataStore`.
  - In-memory.
  - Forwarding in `InstrumentingMessageTrackingStoreDecorator` and in each `*Client` facade.
- **Tests.** Conformance round-trip, and decorator forwarding.

## Step 2: policy and Cosmos stamping

- **`RetentionPolicy`** (singleton). Pins from `NimBus:Retention:*` and the legacy
  `NimBus:Cosmos:UnresolvedRetentionDays`, validated at startup. Saved settings through
  `IEndpointMetadataStore`, cached for five minutes. `Current` returns a `RetentionPeriods`.
- **Registration.** Register it in `AddCosmosDbMessageStore`, `AddSqlServerMessageStore` and the
  in-memory registration. `CosmosDbMessageStoreOptions.UnresolvedRetentionDays` stays as the
  legacy pin's binding.
- **Cosmos stamping.**
  - `CosmosDbMessageTrackingStore` takes `RetentionPolicy` instead of `_unresolvedTtlSeconds`.
  - The constants at `Writes.cs:103,191,315`, `Messages.cs:19` and `Audits.cs:20` read from it.
  - The 60-second soft delete (`Writes.cs:209`) stays.
- **Tests.** `RetentionPolicyTests`. Replace the TTL-constant tests in the Cosmos test project with
  stamping tests per period, and add a refresh test.

## Step 3: SQL cleanup on AF-199's job

**Prerequisite.** AF-199 (GH#291) has merged, with its DbUp script renumbered past `0028` and its
`RetentionEnabled` defaulting to `false` (spec §12 item 6). If AF-199 is closed instead, this step
builds its `SqlServerRetentionService` and `SqlServerRetentionRunner` first, from the AF-199
branch.

- **Contract.** `IMessageTrackingStore.PurgeExpiredData(RetentionPeriods, DateTime, int)` returns
  `RetentionPurgeResult`, one count per period.
  - SQL Server: AF-199's runner statements move here, as the four rules of spec §5.4. The
    resolved-period rule also covers Completed and Skipped.
  - In-memory: the same logic over its dictionaries.
  - Cosmos: zeros.
  - Forwarded by the decorator.
- **Runner.**
  - `SqlServerRetentionRunner` reads `RetentionPolicy.Current` at the start of each run, replacing
    its constants and `UnresolvedRetentionDays`.
  - It calls `PurgeExpiredData` in 5,000-row batches until a batch comes back short, as now.
  - `RetentionPolicy` reads `NimBus:SqlServer:UnresolvedRetentionDays` as a legacy pin on open
    events. It reads `NimBus:SqlServer:RetentionEnabled = true` as a legacy pin of 30, 90 and 365
    days on the resolved, history and audit periods (spec §6), with a test for each.
- **Indexes.** Widen or add an index only if step 0 showed that the resolved-period rule scans.
- **Metric.** `nimbus.retention.deleted`, tagged by period, next to the existing per-run log entry.
- **Tests.**
  - Conformance `PurgeExpiredData` cases (spec §8), run live for SQL under the gate.
  - Extend AF-199's `SqlServerRetentionTests`: periods taken from the policy, Completed and Skipped
    under the resolved period, a saved change applied at the next run.

## Step 4: API and page

- **`api-spec.yaml`.**
  - `GET` and `PUT /api/v1/admin/retention/settings`, with schemas for the periods, their sources
    and pin keys, the provider and the Resolver's periods.
  - Problem responses for `400` (validation) and `409` (pinned).
  - A build with NSwag generation enabled.
- **`RetentionImplementation` controller.**
  - Site Owner only.
  - `PUT` validates, refuses pinned periods, saves, and writes `UpdateRetentionSettings` (a new
    `MessageAuditType`) with old and new values through the required audit path.
- **ClientApp.**
  - A `retention` entry in the Settings features (`models/manage-pages.ts`).
  - The page, with: one row per period; locked pinned rows; the provider note; the "Nothing
    expires" banner on SQL; the shortening confirmation with the open-events warning; and the
    mismatch warning.
- **Tests.**
  - `tests/NimBus.WebApp.Tests` for the API rules.
  - Vitest for the page states, including the confirmation flow.
  - A screenshot from the local AppHost with demo data, on both providers.

## Step 5: pins and CLI

- **Templates.** `deploy.core.bicep` and `deploy.webapp.bicep` take a `retentionPins` object and
  add `NimBus__Retention__*` app settings for each set value, to the Resolver and the WebApp.
- **`InfrastructureDeployer` and `SetupCommand`.**
  - The four flags; values `<days>`, `unlimited` and `unpin`.
  - Carry-forward reads the Resolver's app settings once per run (the existing content-share read
    at `:712` is extended, not duplicated), including the legacy key.
  - Validation through `RetentionPolicy`'s rules before any deployment.
- **The beat.** `ServiceHealth` gains the effective periods. The SQL column comes from the step 3
  script, or a separate one if step 0 showed the shape needs it. The page compares them.
- **`nb container`.**
  - Build the Cosmos store with `RetentionPolicy`, from the saved settings plus `NimBus__Retention__*`
    environment pins.
  - `--unresolved-retention-days` becomes a per-run pin with a deprecation warning.
- **Tests.**
  - CLI: flag parsing; carry-forward of the current and legacy keys; the template parameters;
    validation failures before deployment.
  - The mismatch path in the API test.

## Step 6: docs and release notes

- The pages listed in spec §10. Rewrite the retention section of `docs/storage-providers.md`, and
  update the "Choosing a provider" table and the price notes that say SQL keeps data forever.
- **Release notes.**
  - Added: the page, the SQL cleanup, the flags.
  - Compatibility: the new interface members; the deprecated key and flag; no retention changes on
    upgrade; the index script.

## Verification gate

```bash
dotnet build src/NimBus.slnx -c Release      # pin SDK 10.0.4xx if the default SDK is newer
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
npm --prefix website ci && npm --prefix website run build
```

- Run the Cosmos and SQL conformance suites live, without skips (CI enforces it).
- Record a manual check in the PR:
  - on SQL, save a 1-day resolved period on a seeded local stack and watch the cleanup delete the
    old rows within one run;
  - on Cosmos, save a period and confirm that new rows carry the new `ttl`.

## Out of scope

- Backfill and per-endpoint periods (spec §4).
- Spec 048's default flip, which waits for step 3 (Spec 048 §5.8).
