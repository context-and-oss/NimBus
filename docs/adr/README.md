# Architecture Decision Records

This directory contains Architecture Decision Records (ADRs) for the NimBus platform. Each ADR captures the context, decision, and consequences of a significant technical choice.

## Index

| ADR | Title | Status | Date |
|-----|-------|--------|------|
| [001](001-session-based-ordering.md) | Use Service Bus sessions for ordered delivery | Accepted; amendment proposed in 018 | 2026-01 |
| [002](002-centralized-resolver.md) | Centralized Resolver for message state tracking | Accepted; amendment proposed in 018 | 2026-01 |
| [003](003-separated-deferred-processor.md) | Separated DeferredProcessor in subscriber apps | Accepted | 2026-03 |
| [004](004-pipeline-behavior-pattern.md) | Delegate-based pipeline behaviors over ASP.NET middleware | Accepted | 2026-03 |
| [005](005-transactional-outbox-sql-server.md) | Transactional outbox with SQL Server | Accepted | 2026-02 |
| [006](006-standard-logging.md) | Microsoft.Extensions.Logging over custom abstraction | Accepted | 2026-04 |
| [007](007-code-first-catalog-export.md) | Code-first EventCatalog and AsyncAPI export | Accepted | 2026-04 |
| [008](008-per-endpoint-cosmos-containers.md) | Per-endpoint Cosmos DB containers | Accepted | 2026-01 |
| [009](009-orchestration-via-application-services.md) | Orchestration via application services, not a saga framework | Accepted | 2026-04 |
| [010](010-pluggable-message-storage.md) | Pluggable message storage providers (SQL Server + Cosmos DB) | Accepted | 2026-05 |
| [012](012-pending-handoff.md) | PendingHandoff outcome for async message completion | Accepted | 2026-05 |
| [013](013-cloudevents-interoperability.md) | CloudEvents 1.0 interoperability layer | Accepted | 2026-07 |
| [014](014-commands-single-consumer.md) | Commands are events with an enforced single consumer | Accepted | 2026-07 |
| [015](015-customer-deployment-distribution.md) | Customer deployments: starter repo now, packaged deployables next | Accepted | 2026-08 |
| [016](016-private-networking-mode.md) | Private networking is an opt-in, bring-your-own-network deployment mode | Accepted | 2026-09 |
| [017](017-single-cosmos-tracking-container.md) | One Cosmos DB tracking container, partitioned by endpoint | Proposed | 2026-09 |
| [018](018-on-prem-rabbitmq-transport.md) | One additional transport, RabbitMQ, for on-premises installations | Proposed | 2026-10 |
