# Codebase review follow-ups (2026-11-27)

A read-only review of `master` at `0c51e8fe` covered security, performance, reliability and
tests. The six most severe findings each have their own fix PR:

1. The agent API (`/api/agent/*`) doesn't check the roles of signed-in people.
2. A transient failure can break FIFO order within a session (`MessageContext.Abandon` is a no-op).
3. A transient send failure dead-letters a message whose handler already succeeded. That PR also
   fixes the order of `Complete` and `CheckForRetry`.
4. A poison session can stall the outbox, and `UPDLOCK, READPAST` doesn't claim rows (the
   statement runs in autocommit). That PR also batches the outbox purge.
5. Every Resolver write triggers an endpoint recount and a SignalR broadcast, with no coalescing.
6. Terminal responses load the full event history, payloads included.

The findings below are verified but not yet scheduled. Line numbers refer to `0c51e8fe`. Each
item lists where the problem is, what goes wrong, and the suggested fix.

## Security

| ID | Severity | Finding | Where | Suggested fix |
|----|----------|---------|-------|---------------|
| S1 | Medium | Contributors can forge audit rows. `POST /api/v1/event/audit/{eventId}` stores `AuditorName`, `AuditTimestamp` and `AuditType` exactly as the client sends them, so a backdated `grantRole` row can name any user. | `src/NimBus.WebApp/Controllers/ApiContract/EventImplementation.OperatorActions.cs:17`, `Mapper.cs:237` | Keep only `Comment` from the request body. The server sets the type to `Comment` and stamps the auditor (`AuditLogService.ResolveAuditorName`) and the time (`TimeProvider`). Also check the role on the event's endpoint. |
| S2 | Medium | Resubmit-with-changes logs the whole request body, PII included, at Information level, before the role check runs. | `EventImplementation.OperatorActions.cs:195`, `:385` | Log only the event id, the message id and the content hash. |
| S3 | Medium | ACL entries are matched against claims that can change or aren't verified: `name` (the display name) and `email` (not verified for every Entra account type). | `src/NimBus.WebApp/Services/EndpointAuthorizationService.cs:227-243` | Match on `oid` first. Accept `upn` or `preferred_username` only from the configured tenant. Drop `name`. Honour `xms_edov` if `email` stays. |
| S4 | Low | Raw exception messages reach the client (`Could not compose event: {e.Message}`). | `EventImplementation.OperatorActions.cs:189` | Return a generic message and keep the details in the log. |
| S5 | Low | The CSP allows `script-src 'unsafe-inline'` and `connect-src https: wss:`. | `src/NimBus.WebApp/Middleware/SecurityHeadersMiddleware.cs:44-51` | Move scripts to a nonce-based CSP. Pin `connect-src` to `'self'` plus Application Insights. |
| S6 | Low | The SQL message store schema name is checked only for being non-empty before it goes into every query as `[{Schema}]`. | `src/NimBus.MessageStore.SqlServer/SqlServerMessageStoreBuilderExtensions.cs:63` | Apply the identifier regex that the Outbox, Inbox and Identity packages already use. |
| S7 | Low | The alert webhook SSRF guard doesn't block NAT64 (`64:ff9b::/96`), 6to4 (`2002::/16`) or IPv4-compatible IPv6 addresses. | `src/NimBus.Alerts/AlertWebhookConnectGuard.cs:91-99` | Map these to their embedded IPv4 address and apply the IPv4 rules to it. |
| S8 | Low | When `AllowRegistration=true` and `RequireEmailConfirmation=false`, someone can register an email address that has ACL grants but no account yet, and so inherit those grants. | `NimBus.Extensions.Identity` | Refuse that combination of settings, or require confirmation for any address that has existing grants. |

## Reliability

| ID | Severity | Finding | Where | Suggested fix |
|----|----------|---------|-------|---------------|
| R1 | Medium | The inbox purge removes at most 1,000 rows per hourly tick. An endpoint receiving more than about 0.28 msg/s grows without bound. | `src/NimBus.SDK/Hosting/InboxPurgeHostedService.cs:63`, `SqlServerInbox.cs:147`, `AzureTableInbox.cs:10` | Keep purging while a call returns a full batch, within a time budget, until cancelled. |
| R2 | Low | The retry delay `BaseDelay.Ticks * (long)Math.Pow(2, n)` is unchecked. A large `MaxRetries` wraps it negative, which skips the `MaxDelay` clamp. | `src/NimBus.Core/Messages/RetryPolicy.cs:61` | Use a saturating multiply, or clamp the exponent. |
| R3 | Low | When two drains run at once, the second treats `SessionCannotBeLocked` as "nothing to drain". This is already known. | `DeferredMessageDispatcher.cs:89` | Tracked in `2026-10-21-session-order-after-unblock.md` §Costs. |

## Performance

| ID | Impact | Finding | Where | Suggested fix |
|----|--------|---------|-------|---------------|
| P1 | High | The Resolver host takes `PrefetchCount=0` and `SessionIdleTimeout=30s` from code with no configuration option, and defaults to 8 concurrent sessions. With many short sessions, each slot waits up to 30 s before it moves to the next session. | `src/NimBus.Resolver.Host/Program.cs:43-53`, `NimBusReceiverOptions.cs` | Make these settings configurable, default the idle timeout to 1–5 s, and measure throughput with a load test. |
| P2 | High | Session state is read from the broker on every call, at least twice per EventRequest. | `src/NimBus.ServiceBus/MessageContext.cs:535`, `StrictMessageHandler.cs:115,128` | Cache the state for each message context and invalidate it on `UpdateSessionState`. The session lock serializes access, so the cache is safe. |
| P3 | Medium | The outbox dispatcher sends one row per round trip. | `src/NimBus.Core/Outbox/OutboxDispatcher.cs:71-89` | Send consecutive rows for the same destination with `ServiceBusMessageBatch`, and stop a session's run at its first failure. |
| P4 | Medium | The SQL endpoint count query does a key lookup per row to read `MIN(UpdatedAtUtc)`. | `SqlServerMessageTrackingStore.EndpointState.cs:12-16`, `Schema/0003_Events.sql:37` | Add a migration that adds `INCLUDE (UpdatedAtUtc)` to `IX_UnresolvedEvents_EndpointId_Status`. |
| P5 | Medium | Cosmos endpoint containers created at runtime have no indexing policy, so payloads and stack traces get indexed and every write costs more RU. | `CosmosContainerDefaults.EndpointContainer` (`:63-70`) | Apply the excluded paths from `deploy/bicep/templates/cosmosDB.bicep:67-71`. |
| P6 | Medium | The bulk-resubmit preview pages through every full failed row just to count them, which is O(N²) with SQL OFFSET paging. | `src/NimBus.WebApp/Services/AdminService.Resubmit.cs:18-45` | Replace it with a COUNT query that uses the cutoff. |
| P7 | Medium | SQL list queries use OFFSET paging. `DownloadEndpointStatePaging` also uses `SELECT *`, so it returns `EventJson`. | `SqlServerMessageTrackingStore.Search.cs:38,75,306`, `Audits.cs:100`, `EndpointState.cs:117,125` | Page with a keyset on `(UpdatedAtUtc, Id)` and reuse `EventSearchColumns`. |
| P8 | Low | A sender is created and disposed on every call. | `ServiceBusSession.cs:155`, `DeferredMessageProcessor.cs:75`, `ServiceBusHeartbeatMessageSender.cs:31`, `ManagerClient.cs:118,140`, `AdminService.Resubmit.cs:338` | Cache senders per entity, as `ReplyDispatcher` already does. |
| P9 | Low | The payload is deserialized twice when a routing-key policy applies. | `RoutingKeyGuardContextHandler.cs:90`, `EventJsonHandler.cs:32` | Pass the parsed payload along through the context. |
| P10 | Low | The endpoint-detail GET writes an audit row on every read. | `EndpointImplementation.cs:356` | Confirm the intent. If it is unintended, stop writing on reads or throttle the writes. |
| P11 | Low | The `IMessageTrackingStore` hot-path members take no `CancellationToken`. | `NimBus.MessageStore.Abstractions` | Add token overloads, keeping backward compatibility as `docs/versioning.md` requires. |

## Tests and CI

| ID | Finding | Suggested fix |
|----|---------|---------------|
| T1 | No workflow runs the frontend Vitest suite (122 files). | Add `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` to `.github/workflows/dotnet.yml` and publish its results. |
| T2 | The coverage report includes only `NimBus.Resolver` and `NimBus.SDK`, has no threshold, and is `continue-on-error`. | Report every `NimBus.*` assembly. Consider a ratchet threshold. |
| T3 | The skipped-test gate covers only the Cosmos and SQL message stores. The live Inbox, Outbox and IntegrationIntelligence suites can skip and still pass. | Extend the gate in `dotnet.yml`. |
| T4 | No test walks every endpoint to check that only a known allowlist is anonymous. | Add `AuthorizationSweepTests.Every_endpoint_requires_authentication_except_allowlist`. |
| T5 | `ServiceBusManagement` (618 lines) and `NimBus.ServiceDefaults` have almost no tests. | Add a recording admin-client test suite and a ServiceDefaults smoke test. |
| T6 | No tests cover concurrent Resolver updates to the same event or session, or interleaved resubmit and skip. | Add `ResolverConcurrencyTests` and a conformance test for a concurrent upsert of the same event. |
| T7 | No property-based round-trip tests exist for serialization. | Add FsCheck or CsCheck round trips for `MessageContent` and CloudEvents. |
| T8 | Several tests wait a fixed delay, a flake risk: `FailedInsightsServiceTests.cs:354,443,462`, `SdkBackgroundServiceTests.cs:57`, `NimBusReceiverHostedServiceTests.cs:47,156`, `SimulationEmulatorTests.cs:78`, `SqlServerRetentionTests.cs:121`. | Wait on a `TaskCompletionSource` signal, or advance `FakeTimeProvider`. |
| T9 | `tests/NimBus.CommandLine.Tests` uses xUnit, but AGENTS.md requires MSTest. | Migrate it to MSTest. |
| T10 | The WebApp implementations have few tests relative to their size: `AlertRulesImplementation`, `FailedImplementation` and `SimulationImplementation`. The MCP helpers `OperatorCursor`, `OperatorMutationLimiter` and `OperatorPayloadAccess` have no direct tests, and `StrictMessageHandler` has only 9 direct tests. | Add tests for the validation and error branches. |
