# Spec 050 — Retention settings in the WebApp

Status: **proposed** (2026-11-15). Nothing is implemented. Target: a 4.x minor.
Implementation plan: [2026-11-15-spec-050-implementation-plan.md](../../plan/2026-11-15-spec-050-implementation-plan.md).
Baseline: master `3cd73dad`.
Related: [Spec 048](../048-sql-server-default-provider/spec.md), whose default flip waits for this
spec's SQL cleanup (Spec 048 §5.8).
Scope: the storage contracts and all three providers, the Cosmos store's TTL stamping, a SQL
cleanup service in the WebApp, a Settings → Retention page and its API, the `nb` CLI
(`infra apply`, `setup`, `container`), the app templates and docs.
Unchanged: the message flow, the Resolver's processing, and every other retention in the platform
(heartbeat history, operator operations, inbox, Integration Intelligence).

## 1. Summary

Today a Site Owner can't see or change how long NimBus keeps its data. On Cosmos, one value is a
host setting and the rest are constants. On SQL Server, nothing is ever deleted. This spec adds
**Settings → Retention**, where a Site Owner sets four retention periods for the whole deployment:

| Period | Data |
|---|---|
| **Open events** | Tracking rows in Pending, Failed, Deferred, DeadLettered or Unsupported |
| **Resolved events** | Completed and Skipped rows, archived failures, and (SQL) removed or purged rows |
| **Message history** | Every stored message: the Flow tab and message details |
| **Audit trail** | Audit entries |

The repo owner decided three rules on 2026-11-15:

1. **A value pinned in host configuration wins.** The page shows it locked, with its source.
2. **No backfill.** On Cosmos, a new period applies to data written from then on.
3. **No per-endpoint retention.** One set of periods covers the whole deployment.

On SQL Server, a cleanup job deletes data older than its period. It builds on AF-199 (GH#291),
which is in flight and adds a SQL retention job with fixed periods (§2). This spec makes those
periods configurable. One question is open (§13): whether SQL starts deleting with Cosmos's
periods on upgrade, as AF-199 does today, or only once a period is set.

## 2. Current state

| Aspect | Today (`3cd73dad`) |
|---|---|
| Cosmos, open events | `CosmosDbMessageStoreOptions.UnresolvedRetentionDays`, bound from `NimBus:Cosmos:UnresolvedRetentionDays`: `-1` (unlimited, the default) or 1–365 (`CosmosDbMessageStoreOptions.cs:15-35`). Stamped as the row's `ttl` on every write of a non-terminal row |
| Cosmos, resolved events | Constants: 30 days on Completed and Skipped rows (`CosmosDbMessageTrackingStore.Writes.cs:191`), on archived failures (`:315`) and on archive through the operator tools (`:103`). Removed rows get 60 seconds (`:209`) |
| Cosmos, message history | Constant 90 days, item `ttl` (`CosmosDbMessageTrackingStore.Messages.cs:19`). The `messages` container also has `defaultTtl: 7776000` in Bicep |
| Cosmos, audit trail | Constant 365 days, item `ttl` (`CosmosDbMessageTrackingStore.Audits.cs:20`) |
| SQL Server (master) | Nothing expires. `RemoveMessage`, `ArchiveFailedEvent` and both `PurgeMessages` only set `Deleted = 1` (`SqlServerMessageTrackingStore.Writes.cs:276-310`). No code on master deletes `UnresolvedEvents`, `Messages` or `MessageAudits` rows by age, so the database grows without bound. Completed and Skipped rows have `Deleted = 0` |
| SQL indexes | `0014_SearchSortIndexes.sql` already has `IX_UnresolvedEvents_UpdatedAtUtc`, `IX_MessageAudits_CreatedAtUtc` and `IX_Messages_EnqueuedTimeUtc`. `Messages` has no index on `CreatedAtUtc` |
| In flight: AF-199 (GH#291) | Branch `feature/AF-199-gh-291-add-retention-to-the-sql-server-m`, last commit 2026-11-12, at the board's delivering stage, no PR yet:<br>- adds a WebApp-only `SqlServerRetentionService` (hourly, `RetentionInterval`) with `SqlServerRetentionRunner`, which deletes in `DELETE TOP (5000)` batches;<br>- R1 deletes `Messages` older than 90 days by `CreatedAtUtc`; R2 deletes `MessageAudits` older than 365 days; R3 deletes `UnresolvedEvents` with `Deleted = 1` older than 30 days; R4 deletes `Deleted = 0` rows, **any status**, older than `NimBus:SqlServer:UnresolvedRetentionDays` (never by default);<br>- `RetentionEnabled` defaults to `true`, so R1-R3 delete on upgrade;<br>- its index script is numbered `0026`, which master has since taken (`0026_OperatorOperations.sql`, up to `0028`), so it needs renumbering |
| Host setting on deployed apps | No template sets `NimBus__Cosmos__UnresolvedRetentionDays`. The Function App templates replace the app settings on every `nb infra apply` (`functionApp.bicep:84`, `flexConsumptionFunctionApp.bicep:81`), and the WebApp keeps only `AzureAd__*` and `ServiceBusManagement__*` (`InfrastructureDeployer.cs:643-645`). So a value set by hand is lost on the next run |
| CLI | `nb container` commands rewrite Cosmos rows and take `--unresolved-retention-days`, which must match the hosts' value by hand (`CliApplicationFactory.cs:37-41`) |
| Pattern to follow | Settings → Audit stores a singleton through `IEndpointMetadataStore.GetAuditSettings`/`SetAuditSettings`, in the Cosmos `settings` container and the SQL `AuditSettings` table (`0022_AuditSettings.sql`), behind `GET/PUT /api/v1/admin/audit/settings` (`api-spec.yaml:1719-1746`) |
| Cleanup pattern | The WebApp already prunes on a schedule: heartbeat history (`HeartbeatService.cs:369`) and operator operations (`OperatorOperationJournal.cs:243`) |

## 3. Goals

1. A Site Owner sets the four periods in the WebApp, with no redeploy and no app-setting edits.
2. Both providers honour them. On SQL Server that means data is actually deleted.
3. A period pinned in host configuration can't be changed from the page, and the page says why.
4. The Resolver, the WebApp and the `nb` CLI use the same periods.
5. Upgrading changes no Cosmos deployment's retention. For SQL, see §13.

## 4. Non-goals

- Backfill: rewriting the expiry of Cosmos documents written before a change (decided).
- Per-endpoint periods (decided).
- Archiving or exporting data before it expires. Expiry deletes; GH#94's archive option stays
  separate.
- The retention of heartbeat history, operator operations, the inbox, Integration Intelligence
  data and Service Bus messages.
- Removing the soft-delete window. A removed Cosmos row still expires after 60 seconds; that is a
  mechanism, not a retention period.

## 5. Design

### 5.1 The settings

A provider-neutral `RetentionSettings` singleton in `NimBus.MessageStore.Abstractions`, beside
`AuditSettings`:

| Field | Unit | Allowed | Cosmos default | SQL default |
|---|---|---|---|---|
| `OpenEventsDays` | days | `-1` (unlimited) or 1–3650 | unlimited | unlimited |
| `ResolvedEventsDays` | days | `-1` or 1–3650 | 30 | unlimited, or 30 (§13) |
| `MessageHistoryDays` | days | `-1` or 1–3650 | 90 | unlimited, or 90 (§13) |
| `AuditTrailDays` | days | `-1` or 30–3650 | 365 | unlimited, or 365 (§13) |

- **Cosmos defaults** are what Cosmos does today, so a Cosmos deployment that never sets a period
  behaves as before.
- **SQL defaults** depend on the open question in §13. Unlimited keeps today's master behaviour
  until the major, when Spec 048 §5.8 moves them to Cosmos's. The Cosmos values match AF-199's
  fixed periods.
- **A saved document holds only the periods an Owner has set.** A field that is absent uses the
  default. "Reset to default" removes the field.
- **One rule across periods.** A bounded audit trail must be at least as long as every other
  bounded period, so the audit trail outlives the data it describes. It generalizes today's
  one-year cap on open events, which existed only to stay within the audit trail's fixed year; the
  cap goes. Unlimited counts as longer than any number. With the defaults (open events unlimited,
  audit trail 365 days) the rule holds, because it compares bounded values only.
- **Storage.** `IEndpointMetadataStore` gains `GetRetentionSettings()` and
  `SetRetentionSettings(RetentionSettings)`, implemented in all three providers as
  `AuditSettings` is: the Cosmos `settings` container (document id `RetentionSettings`), a new SQL
  `RetentionSettings` table, and the in-memory store. The conformance suite covers them, and
  `InstrumentingMessageTrackingStoreDecorator` forwards them (AGENTS.md).

### 5.2 Pins in host configuration

- **Keys.** `NimBus:Retention:OpenEventsDays`, `ResolvedEventsDays`, `MessageHistoryDays` and
  `AuditTrailDays` (environment form `NimBus__Retention__…`). A key that is set pins its period.
- **The legacy key.** `NimBus:Cosmos:UnresolvedRetentionDays` still pins open events when the new
  key is absent. Setting both to different values fails startup. The docs mark the legacy key as
  deprecated, and the next major removes it.
- **Validation.** Pins are checked at startup, against the ranges and the cross-period rule, so a
  bad pin fails the host with the key and the value, as `UnresolvedRetentionDays` does today.
- **Effective period** = the pin, else the saved value, else the default. One `RetentionPolicy`
  type computes it, and every host uses that type (§5.3).
- **Pins reach both apps through the templates.** Otherwise the next `nb infra apply` would wipe
  them, as it wipes the hand-set key today (§2).
  - `nb infra apply` and `nb setup` gain `--retention-open-days`, `--retention-resolved-days`,
    `--retention-message-days` and `--retention-audit-days`. Each takes a number, `unlimited` or
    `unpin`.
  - The values go to both the Resolver and the WebApp as `NimBus__Retention__*` app settings,
    through new template parameters.
  - **Carried forward.** Without a flag, `nb` reads the Resolver's current
    `NimBus__Retention__*` settings, and the legacy key, with
    `az functionapp config appsettings list` (as `InfrastructureDeployer.cs:712` does for the
    content share), and passes them again. A pin survives reruns. A legacy value is carried over
    into the new key.
- **Mismatch.** Pins set by hand on one app can still differ between the two. The Resolver adds
  its effective periods to the liveness beat it already writes (`ServiceHealth`). The Retention
  page compares them with the WebApp's and shows a warning naming the period that differs.

### 5.3 Who applies the periods

`RetentionPolicy` is registered with the storage provider (`AddCosmosDbMessageStore`,
`AddSqlServerMessageStore`, the in-memory store) as a singleton. It reads the saved settings
through the store and caches them for five minutes, and it applies the pins from configuration. A
saved change therefore reaches every host within five minutes, without a restart.

**Cosmos** stamps the effective period as the document's `ttl` on every write, replacing the
constants in §2:

- open-event rows, as `UnresolvedRetentionDays` does today;
- Completed and Skipped rows, archived failures and archive through the operator tools, with the
  resolved-events period;
- messages, with the message-history period;
- audits, with the audit-trail period.

`-1` is written as `ttl: -1`, never to expire. The container-level defaults in Bicep
(`messages` 90 days, `audits` 1 year) stay; an item's `ttl` overrides them (§14 item 1).

**No backfill.** A document keeps the `ttl` it was written with until it is written again. Cosmos
counts `ttl` from the last write, so a row that keeps changing is re-stamped each time.

**SQL Server** stamps nothing. A cleanup service deletes rows whose age exceeds the current period
(§5.4). So on SQL a change applies to existing data at the next run. That is the difference
between the providers, and the page states it (§5.5).

**The `nb` CLI.** `nb container` commands build the Cosmos store with the same `RetentionPolicy`,
reading the saved settings and the `NimBus__Retention__*` environment variables as pins. So a
rewritten row gets the same `ttl` the hosts would give it. `--unresolved-retention-days` still
works for one release as a per-run pin, with a deprecation warning, and is removed in the next
major.

### 5.4 SQL cleanup, built on AF-199

AF-199 (§2) already provides the job: `SqlServerRetentionService` in the WebApp only,
`SqlServerRetentionRunner`, `DELETE TOP (5000)` batches repeated until a batch comes back short,
a `RetentionInterval` (default one hour), a `RetentionEnabled` switch, startup validation and
indexes. This spec keeps all of that and changes three things.

1. **The periods come from `RetentionPolicy`** (§5.3) instead of AF-199's constants
   (`MessageRetentionDays`, `AuditRetentionDays`, `ArchivedTrackingRetentionDays`) and its
   `UnresolvedRetentionDays` option. The runner reads `RetentionPolicy.Current` at the start of
   each run, so a saved change applies at the next run. AF-199's
   `NimBus:SqlServer:UnresolvedRetentionDays` becomes a legacy pin on open events, the same way as
   the Cosmos key (§5.2).
2. **Completed and Skipped rows move to the resolved period**, as on Cosmos. AF-199's R4 deletes
   `Deleted = 0` rows of any status by the unresolved period, which by default is never. So
   completed rows would be kept forever while Cosmos expires them after 30 days. The rules become:

   | Period | Rows deleted |
   |---|---|
   | Open events (R4) | `UnresolvedEvents` with `Deleted = 0`, a status in Pending, Failed, Deferred, DeadLettered or Unsupported, and `UpdatedAtUtc` older than the period |
   | Resolved events (R3) | `UnresolvedEvents` with `Deleted = 1`, or a status of Completed or Skipped, and `UpdatedAtUtc` older than the period |
   | Message history (R1) | `Messages` with `CreatedAtUtc` older than the period |
   | Audit trail (R2) | `MessageAudits` with `CreatedAtUtc` older than the period |

   Unlimited periods are skipped. `UpdatedAtUtc` is the row's last write, which matches Cosmos
   counting `ttl` from the last write. R3's wider predicate may need its index widened; AF-199's
   filtered index covers only `Deleted = 1` (§14 item 3).
3. **A provider-neutral seam.** The runner's statements move behind
   `IMessageTrackingStore.PurgeExpiredData(RetentionPeriods, DateTime nowUtc, int batchSize)`, so
   the conformance suite can pin each rule on SQL Server and the in-memory store:
   - Cosmos returns zeros, because TTL does the work;
   - the decorator forwards it;
   - AF-199's service calls it in place of running SQL directly.

The metric `nimbus.retention.deleted`, tagged by period, is added next to AF-199's per-run log
entry.

**Sequencing.** AF-199 merges first, after renumbering its DbUp script past `0028`. This spec's
SQL step then changes the runner as above. If AF-199 is closed instead, this spec's SQL step
builds the same job itself.

**Consequences of deleting a row** are the same as Cosmos expiry:
  - An expired open-event row takes its resubmit ability with it, as `docs/storage-providers.md`
    already warns for Cosmos.
  - An expired Failed row no longer blocks the Monitor's counts. Its Service Bus session can stay
    blocked; the page warns about that before an Owner bounds open events (§5.5).

### 5.5 The page: Settings → Retention

A new Settings feature, `retention`, Site Owner only, like Settings → Audit:

- **One row per period:**
  - the effective value, as a number of days or "Keep forever";
  - its source: *Pinned by host configuration* (locked, naming the key), *Saved* or *Default*;
  - an editor and a reset-to-default action, for unpinned periods;
  - the data it covers, in plain words.
- **Provider note**, depending on the provider:
  - Cosmos: "A new period applies to data written from now on. Data already stored keeps its
    current expiry."
  - SQL Server: "Data older than the new period is deleted at the next cleanup, within an hour."
    The page gives the configured `RetentionInterval`.
  - SQL Server, while every period is unlimited, also shows a banner: "Nothing expires on this
    deployment."
- **Confirmation before saving a shorter period.** The dialog states:
  - what will be deleted and when, using the provider's semantics;
  - that expiry deletes the whole record: payload, resubmit ability and history;
  - for open events: that a deleted Failed row can no longer be resubmitted or skipped from the
    WebApp, while its session stays blocked in Service Bus.
- **Validation in the page and in the API**: ranges and the audit-trail rule, with the reason
  inline.
- **Resolver mismatch warning** (§5.2).
- **API**: `GET` and `PUT /api/v1/admin/retention/settings` in `api-spec.yaml`.
  - The `GET` returns each period's effective value, source and pinning key, the provider, and the
    Resolver's effective periods with their beat time.
  - The `PUT` takes the saved values. A period that is pinned is refused with `409` and the key.
  - Every save writes an `UpdateRetentionSettings` audit entry with the old and new values,
    through the required audit path, like the other Settings changes.
- **Screenshot** of the page in the implementing PR (AGENTS.md).

## 6. Existing deployments

| Deployment | After upgrading |
|---|---|
| Cosmos, nothing configured | Unchanged: open events unlimited, resolved 30 days, history 90 days, audits 1 year |
| Cosmos with `NimBus__Cosmos__UnresolvedRetentionDays` on its apps | Unchanged. It is a pin on open events. The next `nb infra apply` carries it into `NimBus__Retention__OpenEventsDays` |
| SQL Server, before AF-199 | Depends on §13. With option A, nothing expires until an Owner saves a period or a pin is set, and the page shows the "Nothing expires" banner. With option B, the Cosmos periods apply from the first cleanup |
| SQL Server with AF-199 already deployed | Its periods carry over: AF-199's fixed values equal this spec's Cosmos defaults, and `NimBus:SqlServer:UnresolvedRetentionDays` becomes a pin on open events. One change: Completed and Skipped rows move from the open-events period, which is never by default, to the resolved period of 30 days (§5.4). So old completed rows are deleted at the first run after upgrading, which the release notes say |

## 7. Evaluation

- **Gains.** Retention becomes visible and adjustable, and it survives `nb infra apply`. SQL
  deployments can finally bound their storage, which matters on S0's 250 GB.
- **Risks.**
  - A shortened period on SQL deletes existing data quickly. The confirmation states it, the audit
    entry records who did it, and an audit trail of at least 30 days is enforced.
  - Cosmos and SQL apply a change differently (§5.3). The page states which applies.
  - The cleanup's deletes compete with the Resolver's writes on small SQL tiers. Batching and the
    time limit bound that; Phase 0 measures it on S0 (§14 item 2).
  - Pins set by hand on one app only. The mismatch warning surfaces them.
- **Compatibility.**
  - Adding members to `IEndpointMetadataStore` and `IMessageTrackingStore` affects third-party
    providers, as earlier storage additions did. They get default implementations that throw
    `NotSupportedException`, so the page reports the provider as unsupported instead of silently
    doing nothing. The three shipped providers implement them.
  - `--unresolved-retention-days` and the legacy key are deprecated. The release notes list both
    under Compatibility. The next major removes them.

**Alternatives.**

| Alternative | Verdict |
|---|---|
| Keep retention in host configuration only, and document it | Rejected. The request is the WebApp, and today's host setting doesn't survive `infra apply` |
| SQL stamps an expiry column on write, like Cosmos's `ttl` | Rejected. Existing rows would carry no expiry and stay forever, which leaves the SQL gap open. Age-based deletion needs no schema change on large tables beyond indexes |
| Backfill Cosmos documents on change | Rejected by the repo owner (§1) |
| Run the SQL cleanup in the Resolver | Rejected. On Flex Consumption it doesn't run continuously, and it would compete with message processing in the same process |
| A SQL Agent job or elastic job | Rejected. Azure SQL Database has no Agent, and it would need setup outside `nb` |

## 8. Tests

**Abstractions and policy (`tests/NimBus.MessageStore.Abstractions.Tests` or the closest existing
project):**

- `RetentionPolicy`:
  - pin over saved over default, for each period;
  - the legacy key pinning open events, and failing when it conflicts with the new key;
  - the five-minute cache and refresh;
  - range and cross-period validation, including unlimited values.

**Conformance (`MessageTrackingStoreConformanceTests` and the metadata conformance):**

- `Get`/`SetRetentionSettings` round-trip; absent fields stay absent.
- `PurgeExpiredData`, on the providers that delete by age:
  - each period deletes exactly its rows and nothing newer;
  - unlimited deletes nothing;
  - a batch limit is honoured;
  - removed and purged rows fall under resolved events;
  - prefix-sibling endpoints are unaffected, though no endpoint filter is involved, as a guard.

**Cosmos (`tests/NimBus.MessageStore.CosmosDb.Tests`):** each write stamps the effective `ttl` for
its period, replacing the constants' tests. A change to the saved settings reaches stamping after
the cache refresh. `-1` is written as `ttl: -1`.

**SQL (`tests/NimBus.MessageStore.SqlServer.Tests`, live under the conformance gate):** the cleanup
SQL against seeded rows. The new indexes exist after DbUp.

**WebApp (`tests/NimBus.WebApp.Tests`, ClientApp Vitest):**

- The API:
  - `GET` shapes, including sources and pin keys;
  - `PUT` validation;
  - `409` for a pinned period;
  - Owner only;
  - the audit entry with old and new values.
- AF-199's `SqlServerRetentionService` with the policy: it reads `RetentionPolicy` per run; does
  nothing on Cosmos or when every period is unlimited; Completed and Skipped rows fall under the
  resolved period; and the metric is recorded.
- The page: pinned rows locked; each provider's note; the confirmation on shortening, with the
  open-events warning; the mismatch warning.

**CLI:** the four flags, `unlimited` and `unpin`; the carry-forward of current and legacy settings;
the values reaching both apps' settings; `nb container` using the policy; the deprecation warning
on `--unresolved-retention-days`.

## 9. Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | The §14 checks | Results in this folder |
| 1 | Settings and policy in all providers; Cosmos stamping; the page and API | Release build green; conformance without skips; screenshot |
| 2 | SQL cleanup: AF-199's job reads `RetentionPolicy`; the resolved-period rule; the `PurgeExpiredData` seam; the metric (§5.4). Starts after AF-199 merges | A seeded SQL deployment is cleaned within the limits on S0 |
| 3 | Pins: template parameters, CLI flags, carry-forward; the mismatch warning; `nb container` | A pin survives two `infra apply` runs; a hand-set mismatch shows on the page |

One PR with a commit per phase, released as a 4.x minor.

## 10. Documentation

- `docs/storage-providers.md`:
  - "Retention of unresolved rows" becomes "Retention", covering the four periods, the page, pins,
    each provider's semantics and the SQL cleanup;
  - the "Choosing a provider" guidance drops "SQL keeps data forever" once Phase 2 ships.
- `docs/cli.md`: the four flags; the deprecation of `--unresolved-retention-days`.
- `docs/deployment.md`: pinning retention at deployment time.
- The WebApp's Settings help text.
- Release notes: Added (the page, the SQL cleanup, the flags); Compatibility (the interface members,
  the deprecations, and that no deployment's retention changes on upgrade).

## 11. Residual risks

1. **An Owner shortens a period by mistake.** On SQL the data goes at the next cleanup. The
   confirmation and the audit entry mitigate this; nothing can undo it.
2. **Cosmos changes take effect slowly** for rows that are never rewritten, because there is no
   backfill. That is the decision; the page says so.
3. **A blocked session after an open event expires** (§5.4), on both providers, as today on Cosmos.
4. **The cleanup on a very large first run** takes several runs (§5.4).

## 12. Decisions

1. A pinned value in host configuration wins and locks the period (repo owner, 2026-11-15).
2. No backfill (repo owner).
3. No per-endpoint retention (repo owner).
4. Four periods, with today's Cosmos behaviour as Cosmos's default (§5.1).
5. SQL deletes by age in a WebApp background service, AF-199's job (§5.4).

## 13. Open questions for the repo owner

1. **SQL on upgrade.** AF-199 enables its job by default (`RetentionEnabled = true`), so an
   upgraded SQL deployment starts deleting messages older than 90 days, audits older than a year
   and archived rows older than 30 days. Master deletes nothing today. Which way?
   - **A (proposed): off until set, in 4.x.** AF-199 ships with `RetentionEnabled` defaulting to
     `false`. Once this spec lands, SQL's periods default to unlimited, and the job deletes only
     what an Owner or a pin has bounded. SQL takes the Cosmos defaults in the next major (Spec 048
     §5.8). This follows
     `docs/versioning.md`, which keeps deleting data on upgrade for a major.
   - **B: as AF-199 does.** The Cosmos periods apply from the first cleanup after upgrading, in a
     4.x minor, listed under Compatibility. It closes the storage-growth gap sooner, but existing SQL
     deployments lose data older than those periods without anyone deciding to.

   The answer changes AF-199 before it merges, so it is needed first.

## 14. Facts to verify (Phase 0)

1. **Cosmos item `ttl` against the container default.** An item `ttl` longer than the container's
   `defaultTtl` (an audit at 3,650 days in a 1-year container) is honoured, and `ttl: -1` overrides
   a positive default.
2. **Cleanup cost on S0.** Seed a million rows per table; measure the batch duration, DTU
   percentage and lock waits while Simulation traffic runs. Tune the batch size and the pause.
3. **Indexes.** Whether AF-199's filtered `IX_UnresolvedEvents_Archived_UpdatedAtUtc` (`Deleted =
   1`) together with `IX_UnresolvedEvents_UpdatedAtUtc` (`0014`) keeps the widened resolved-period
   rule a seek. Whether `ONLINE = ON` is available on S0 to S4, and how long an index build takes on
   a table of several million rows.
4. **The beat.** `ServiceHealth` can carry the Resolver's effective periods on both providers; the
   SQL table needs a column.

## 15. References

- [Storage providers: retention](../../storage-providers.md#retention-of-unresolved-rows).
- [Cosmos DB time to live](https://learn.microsoft.com/azure/cosmos-db/nosql/time-to-live).
- Spec 048 (SQL Server as the default), GH#94 (archive-style retention), GH#291 and AF-199 (SQL
  retention job, in flight).

## 16. Review trail

- 2026-11-15: drafted, then renumbered from 049 to 050, because PR #393 had already taken 049.
  Revised to build on AF-199's SQL retention job, found on the AgentFactory board while filing
  GH#391, instead of designing a second job. The SQL-on-upgrade question became §13.
