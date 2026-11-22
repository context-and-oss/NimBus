# CRM/ERP end-to-end tests

The 84-scenario Playwright suite drives the real CRM and ERP APIs, worker and Functions
adapters, Service Bus, SQL databases, Resolver, and management WebApp. Domain tests
publish through the normal business APIs. Wire-format tests deliberately inject
native envelopes from an authenticated test route to the receiving endpoint.

Open [the interactive HTML coverage map](test-coverage.html) to explore all 84
scenarios, filter by direction or family, and step through message journeys.
The standalone page works offline and shows the recorded full-run and rerun
results from 2026-09-06; it is not a live test monitor.

## GitHub Actions

[CRM ERP end-to-end](../../../.github/workflows/crm-erp-e2e.yml) runs the entire
suite serially on Ubuntu at **02:23 UTC each day**, or manually from GitHub's
Actions tab using **Run workflow**. The workflow becomes available after it is
published to the default branch. It starts its own isolated Aspire demo with SQL
Server, the local Service Bus emulator, deterministic enrichment and a fresh masked
E2E key. No repository secrets or Azure subscription are needed.

Each run uploads a `crm-erp-e2e-<run>-<attempt>` artifact, retained for 14 days:

- `artifacts/test-coverage.html`: offline, searchable visualization of that run's
  actual outcomes, attempts, timings and commit. This is separate from the dated
  coverage map above.
- `playwright-report/`: detailed Playwright HTML report; open with
  `npx playwright show-report <extracted-artifact>/playwright-report`.
- `artifacts/catalog.json`, `artifacts/results.json`, `test-results/` and
  `artifacts/logs/`: discovered scenarios, actual results, failure evidence and
  bounded application logs with E2E keys and connection-string credentials redacted.

The job summary counts passed, failed, flaky, skipped and unexecuted scenarios.
Retries are visible but a flaky scenario still fails the complete-pass check.
Startup failures produce an incomplete result visualization when checkout and Node
setup succeeded. Cleanup and artifact upload are attempted even after failure.
Runner destruction removes remaining containers and volumes.

This initial rollout is manual/nightly; it does not change required PR checks.
Establish a clean full run on the GitHub runner before promoting it to a PR gate.

For a fresh local run with the same visualization (after starting the demo):

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
Remove-Item artifacts/results.json -ErrorAction SilentlyContinue
npx playwright test --list --reporter=json > artifacts/catalog.json
npm run test:live
npm run report:results
```

The report command exits nonzero for incomplete or nonpassing runs, while still
writing `artifacts/test-coverage.html`. Use `npm run test:report` to verify the
report generator and log redaction without starting the demo.

## Run against an isolated local demo

Use Node 22.18+ (native TypeScript support), .NET 10, Aspire CLI, Docker, and
Chromium. From the repository root in PowerShell:

```powershell
$env:E2E__Enabled = 'true'
$env:E2E__Key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ASPIRE_CLI_START_TIMEOUT = '600'
aspire start --apphost samples/CrmErpDemo/CrmErpDemo.AppHost --isolated --non-interactive
cd samples/CrmErpDemo/e2e
npm ci
npx playwright install chromium
npm run typecheck
npm run test:live
```

Keep the same key in the shell for startup and tests. Do not commit it. The runner
waits for resources and discovers URLs from Aspire; explicitly supplied URLs take
precedence over old `.env.local` ports. `npm test` still supports manually supplied
URLs, but receiver restart tests require the explicit AppHost selected by
`test:live`. Use the isolated demo: infrastructure cases temporarily disable a
send channel or restart a receiver.

```powershell
npm run test:live -- 12-message-lifecycle --max-failures=1
npm run test:live -- 13-handoff-lifecycle
npm run test:live -- 14-wire-validation
npm run test:live -- 16-outbox-recovery 17-receiver-recovery 19-broker-redelivery
npm run report
```

Stop the AppHost when finished:

```powershell
aspire stop --apphost ../CrmErpDemo.AppHost --non-interactive
```

## Coverage

| Scenario families | Specs | Assertions |
|---|---|---|
| Account/customer CRUD, round trip, routing | 01, 12 | Both directions, linked identifiers, ordered business audit revisions, soft deletion |
| Contact CRUD and relationships | 15 | Both directions, parent mapping, preserved origin, three completed events |
| Session isolation, deferral, FIFO recovery | 03, 12 | Observe failure and deferred siblings before recovery; independent session progresses; exact business revision sequence |
| Resubmit, skip, replay failure | 02, 03, 12 | Real Actions-menu resubmit, skip without applying bad revision, new blocked head during replay |
| No policy, automatic retry, exhausted budget | 12 | Exact attempt counts, two configured retries, stable event lineage, no implicit retry or implicit DLQ on exhaustion |
| Transient redelivery and broker exhaustion | 12, 19 | Same message redelivery, no error/retry response, actual MaxDeliveryCountExceeded DLQ entry |
| Permanent and discard classifications | 12 | Exact DeadLettered/Skipped status, actual DLQ, no duplicate business effects, discard releases a blocked retry |
| Middleware and validation rejection | 12, 14 | Exact DeadLettered outcome; one physical dead letter; no handler invocation for invalid payloads |
| Invalid/missing/null/deep payload, metadata mismatch, unsupported | 14 | Both receiving adapters; unknown valid types are Unsupported; malformed known types reach the broker DLQ |
| Real ERP external-job handoff | 04, 05, 06 | Pending+Handoff before explicit job release; no premature ERP write; real worker completes/fails; deferred updates replay |
| Handoff completion/failure/recovery | 13 | Both directions using a test decorator around real handlers; no handler invocation on settlement; skip, resubmit, second handoff |
| Overdue and repeated declarations | 13 | ExpectedBy is not an automatic timeout; last declaration wins; throw after declaration follows failure classification |
| Duplicate/stale settlement | 13 | An old event cannot release a newer blocked event in the same session |
| Inbox/application idempotency | 08, 12 | CRM DuplicateDetected; ERP idempotent replay; unchanged business audit |
| Transactional outbox | 16 | Rollback removes entity, audit and event; committed SQL outbox survives broker send rejection and drains after recovery |
| Receiver interruption | 17 | Restart each real adapter with a blocked session and queued/deferred work, then recover in FIFO order |
| Circuit breaker | 11 | Observed Open and Closed transitions with real downstream failures and successful recovery traffic |
| Request/reply and commands | 09, 10, 18 | Approved, not found, timeout, credit hold, concurrent reply correlation |
| Fan-out and notifications | 07, 18 | Enrichment reaches DataPlatform; webhook and Resolver history carry the failed event identity |
| Endpoint alerts (Alerts tab) | 20 | A subscription made through the WebApp API delivers the failed event to Mailpit and as a webhook whose signature verifies with the secret returned once; an alert rule opens on the failed event and resolves once it is skipped, with one signed version-2 notice and one mail each time |

## Deterministic controls and evidence

The sample-only `/api/e2e` routes are absent unless Development, `E2E__Enabled=true`,
and a key of at least 32 characters are configured. Every route checks
`X-NimBus-E2E-Key`. The handler decorator and middleware probe are installed only
under the same gate. Scripts are bounded, keyed by GUID session, event type, and
execution stage. They leave unregistered sessions on the normal code path.
The E2E profile uses a 5-second broker lock and maximum delivery count of 3 on the
two receiving subscriptions and disables automatic lock renewal on both test
receivers so transient lock-expiry retries are bounded. Normal demo provisioning
and receiver settings remain unchanged. The
scripted NimBus retry policy is separate: two retries with a 2-second delay, and
only exceptions tagged `E2E configured retry` match it.
The DataPlatform sink uses 64 concurrent sessions with a one-second idle timeout
in this profile so its deliberate ingestion delay does not build a backlog across
the suite. The enrichment agent uses the deterministic classifier.

Real ERP handoff tests use a long deadline and explicitly release the registered
job only after observing Pending/Deferred. The normal background service still
performs the business upsert, outbox publish, and settlement. Reverse handoff
contract tests use the opt-in handler decorator; CRM has no real external-job
implementation.

New lifecycle tests attach per-session Resolver rows, handler attempts, and
business audits. Wire tests attach actual broker DLQ evidence. Failure
screenshots and videos are retained; tracing is opt-in with
`--trace=retain-on-failure` (trace teardown stalled in this Windows environment).
Tests run serially, use unique entity IDs,
restore toggles, and remove their scripts. Expected dead letters and business
audit rows remain available for diagnosis; the suite never purges the namespace.

## Limits

- Passing against the local emulator does not establish Azure broker restart
  durability. The receiver tests restart adapters while the broker stays up.
- The demo external-job registry is in memory. ERP API restart durability for
  pending external jobs is not claimed; it needs a durable job registry first.
- Missing EventId wire tests use the actual DLQ as their oracle, since a valid
  event identity is unavailable for an ordinary Resolver search.

## Verification

Verified on 2026-09-06 with the isolated Development AppHost, SQL stores and local
Service Bus emulator:

- `npm run test:live`: 83 passed, one failed in 9.2 minutes. The failure exposed
  session-lock loss after handoff unblock but before deferred scheduling.
- After fixing that recovery gap, all 17 affected cases passed in 1.5 minutes:
  `npm run test:live -- 04-pending 05-pending 06-pending 13-handoff`.
  The exact stalled live session also recovered its deferred sibling and applied
  the business update when completion was redelivered. All 84 distinct scenarios
  are therefore verified across the full run and affected rerun.
- TypeScript typecheck and `git diff --check` passed.
- Targeted C# checks: 73 core lifecycle/extension tests, 23 AppHost/control tests,
  5 Service Bus session tests, 2 real AMQP regressions, and 12 WebApp resubmit
  tests passed (115 total; one existing AppHost test skipped).

The tests also drove fixes for worker dead-lettering, ERP Functions manual
settlement, failed CloudEvent handoff payload recovery, and failure notifications.
The three focused platform regressions were observed failing before their fixes;
the handoff drain regression additionally verifies that a newer blocker remains
untouched.
