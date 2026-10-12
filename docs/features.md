# NimBus Features

This document is a concise catalog of the current NimBus feature set, grouped into **core platform features** and **extensions**.

## Core platform features

| Area | Feature | Brief description | Primary packages/services |
|---|---|---|---|
| Messaging model | Event-driven pub/sub | Endpoints publish and consume typed events through Azure Service Bus topics/subscriptions. | `NimBus.SDK`, `NimBus.ServiceBus` |
| Ordering | Session-based FIFO processing | Messages are grouped by `SessionId` to guarantee ordered handling per business key. | `NimBus.Core`, `NimBus.ServiceBus` |
| Developer API | Typed publisher/subscriber API | DI registrations for publishers, subscribers, handlers, and receiver hosting. | `NimBus.SDK` |
| Event modeling | SessionKey + metadata attributes | `[SessionKey]`, validation, and description attributes for ordering and schema/documentation. | `NimBus.Core` |
| Reliability | Retry policies with backoff | Per-event, exception-type and exception-text retry policies with fixed/linear/exponential strategies, jitter, and `Retry-After` hints (`IRetryAfterHint`). | `NimBus.Core` |
| Reliability | Endpoint circuit breaker | Opt-in failure-rate breaker pauses hosted receivers, probes at concurrency one, and restores processing without consuming delivery attempts while open. | `NimBus.Core`, `NimBus.SDK` |
| Reliability | Permanent failure classification | Classify unrecoverable exceptions and dead-letter immediately without retry budget use. | `NimBus.Core` |
| Reliability | Session blocking + deferral | When a message fails, later messages in the same session are deferred to preserve order. | `NimBus.Core`, `NimBus.ServiceBus` |
| Recovery | Deferred replay | Deferred messages are re-published in FIFO order after resubmit/retry/skip unblocks the session. | `NimBus.ServiceBus` |
| Recovery | Resubmit/skip flows | Failed events can be resubmitted or skipped while keeping resolver history consistent. | `NimBus.Manager`, `NimBus.WebApp` |
| Messaging patterns | Request/response | Typed request/response over Service Bus sessions with timeout handling. | `NimBus.SDK` |
| Messaging patterns | Message scheduling | Schedule future delivery and cancel scheduled messages (sender mode). | `NimBus.SDK`, `NimBus.ServiceBus` |
| Messaging patterns | Pending handoff | `MarkPendingHandoff` keeps the session blocked while external work runs; `IHandoffClient` settles the message later without re-running the handler. | `NimBus.SDK` |
| Messaging patterns | CloudEvents interop | Per-endpoint `UseCloudEvents` publishing and consuming in binary or structured JSON mode, with format detection on the subscriber. | `NimBus.SDK`, `NimBus.Core` |
| Messaging patterns | Commands | `Command` messages must have exactly one consumer; topology provisioning fails otherwise (ADR-014). | `NimBus.Abstractions`, `NimBus.ServiceBus` |
| Delivery guarantees | Transactional outbox | Persist outgoing messages in SQL Server outbox and dispatch asynchronously. | `NimBus.Outbox.SqlServer` |
| Delivery guarantees | Consumer inbox | Opt-in per-endpoint `MessageId` deduplication, recorded after a successful handler; a redelivered duplicate is skipped. SQL Server, Cosmos DB, or in-memory for tests. | `NimBus.Inbox.SqlServer`, `NimBus.MessageStore.CosmosDb`, `NimBus.SDK` |
| Extensibility hooks | Pipeline middleware | Middleware behaviors wrap handling for cross-cutting concerns. | `NimBus.Core` |
| Extensibility hooks | Lifecycle observers | Passive hooks for received/completed/failed/dead-lettered message events. | `NimBus.Core` |
| Built-in middleware | Logging/Metrics/Validation middleware | Built-in behaviors for processing logs, OpenTelemetry metrics, and basic message validation. | `NimBus.Core` |
| State and audit | Resolver state tracking | Central resolver stores message outcomes and endpoint resolution state in Cosmos DB or SQL Server. | `NimBus.Resolver`, `NimBus.MessageStore` |
| State and audit | Pluggable storage | The Resolver and WebApp store message state in Cosmos DB or SQL Server behind one storage contract, checked by a shared conformance suite. | `NimBus.MessageStore.Abstractions`, `NimBus.MessageStore.CosmosDb`, `NimBus.MessageStore.SqlServer` |
| Operations UI | Management WebApp | Authenticated UI/API for endpoint/event/message inspection and operational workflows, including reconciling stale `Pending` rows from their stored outcome. | `NimBus.WebApp` |
| Operations UI | Failed messages across endpoints | One page for every unresolved failure the operator can read: stacked failures-over-time chart, Messages-style filters, and list / by-endpoint / by-error views with resubmit and skip. | `NimBus.WebApp`, `NimBus.MessageStore` |
| Operations UI | Role-based access | Reader, Contributor and Owner roles site-wide and per endpoint on the Access Control page; the separate PII Reader role reveals `[Sensitive]` fields, which are otherwise redacted, partly revealed or hashed. | `NimBus.WebApp`, `NimBus.Abstractions` |
| Operations UI | Subscription incident response | Topology → Subscriptions lets a site Owner find a flooding backlog and stop or clear it per subscription, audited as `ManageSubscription`. | `NimBus.WebApp` |
| Operations UI | Simulate traffic | A site Owner can enable a Simulate page that publishes schema-valid generated events through the real bus and stands in for chosen consumers; production environments are always blocked. | `NimBus.WebApp` |
| Operations automation | CLI provisioning + operations | `nb` CLI commands for infra/topology/app deploy, purge, session cleanup, and container operations. | `NimBus.CommandLine` |
| Topology management | Declarative topology export/apply | Export platform config and provision topics/subscriptions/rules from it. | `NimBus`, `NimBus.Management.ServiceBus`, `NimBus.CommandLine` |
| Topology management | AsyncAPI and EventCatalog export | `nb asyncapi` exports, validates and diffs AsyncAPI 3.0 documents (the diff exits non-zero on breaking changes); `nb catalog export` writes a runnable EventCatalog site. | `NimBus.CommandLine`, `NimBus.ServiceBus` |
| Deployment | Private networking (preview) | `--network-mode private` deploys Service Bus Premium, the store and both apps behind private endpoints with public access off; not yet verified on a live Azure deployment. | `NimBus.CommandLine` |
| Observability | Health checks | `IHealthCheck` probes for Service Bus and the message store, surfaced on `/health`, `/alive` and `/ready`. | `NimBus.ServiceBus`, `NimBus.MessageStore.CosmosDb` |
| Observability | Platform heartbeat + Resolver liveness | Settings → Heartbeat probing probes every catalog endpoint with no adapter-side handler and settles a Resolver liveness probe, reporting status, round-trip and SDK version. | `NimBus.Core`, `NimBus.Resolver`, `NimBus.WebApp` |
| Testing | In-memory test transport | Run the full pipeline in tests without Azure Service Bus. | `NimBus.Testing` |
| Local development | Aspire orchestration | Local AppHost orchestrates provisioner, resolver, webapp, publisher, and subscriber. | `NimBus.AppHost`, `NimBus.ServiceDefaults` |
| Local development | Local stack from packages | An adapter repository's AppHost runs the Service Bus emulator, topology provisioning, Resolver and WebApp as dotnet tools, without a NimBus source checkout. | `Akaule.NimBus.AspireHosting`, `Akaule.NimBus.ServiceBusEmulator`, `Akaule.NimBus.Resolver.Host`, `Akaule.NimBus.WebApp`, `Akaule.NimBus.CommandLine` |
| Local development | Service defaults | OpenTelemetry with NimBus instrumentation, health checks and service discovery; the standard HTTP resilience handler is opt-in. | `Akaule.NimBus.ServiceDefaults` |
| AI agents | Event-type registry + REST agent API | `/api/agent/*` (catalog, event types, subscribe, receive, publish, settle) lets AI agents define event types and take part on the bus. | `NimBus.Agents`, `NimBus.WebApp` |
| AI agents | Operator MCP endpoint | Opt-in `/mcp` endpoint for monitoring endpoints, investigating failures and recovering single failed messages under WebApp roles and audit. | `NimBus.WebApp` |

## Extension framework

| Feature | Brief description | Primary packages |
|---|---|---|
| `INimBusExtension` model | Extension packages can register services, middleware, and lifecycle observers through `AddNimBus()`. | `NimBus.Core.Extensions` |
| Fluent builder composition | `INimBusBuilder` supports adding behaviors, observers, and extension instances/types. | `NimBus.Core.Extensions` |
| Backward-compatible opt-in | Without registered extensions, NimBus runs with default core behavior. | `NimBus.Core` |

## Shipped extensions

| Extension | Brief description | Package/project |
|---|---|---|
| Notifications | Sends configurable notifications on lifecycle events (failure/dead-letter by default) through Webhook, Microsoft Teams and Email channels or a custom `INotificationChannel`, with per-channel severity filters and rate limiting. | `NimBus.Extensions.Notifications` |
| Identity | Adds ASP.NET Core Identity username/password auth for WebApp, with email confirmation/reset flow and optional dual-login with Entra ID. | `NimBus.Extensions.Identity` |
| Entra bearer tokens for HTTP clients | `AddAzureBearerToken(...)` adds a cached Microsoft Entra token to an `IHttpClientFactory` client, for adapters calling Dataverse, Business Central or Finance and Operations. | `Akaule.NimBus.Extensions.Http` |
| Integration Intelligence | On-demand, idempotent, audited classification of a persisted failed or dead-lettered occurrence with bounded redacted evidence and deterministic operator guidance. | `Akaule.NimBus.Extensions.IntegrationIntelligence`, `NimBus.WebApp` |
