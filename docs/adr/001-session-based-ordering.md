# ADR-001: Use Service Bus Sessions for Ordered Delivery

## Status
Accepted. [ADR-018](018-on-prem-rabbitmq-transport.md) (proposed, 2026-10-21) would amend it once
accepted: sessions stay the ordering model on Azure, and the transport-abstraction exclusion below
is narrowed to one additional transport, RabbitMQ, for on-premises installations.

## Context
NimBus needs guaranteed FIFO ordering per logical entity (e.g., all messages for a single order must be processed sequentially). Without ordering, a payment confirmation could be processed before the order creation, or a retry could execute while the original is still being handled.

Options considered:
1. **Service Bus sessions** — Exclusive lock per session ID, ordered delivery within session
2. **Partitioned queues** — Distribute messages across partitions by key
3. **Application-level locking** — Distributed lock (Redis/Cosmos) before processing
4. **Single consumer** — One consumer per entity type (no concurrency)

## Decision
Use Azure Service Bus sessions with the entity ID (e.g., OrderId) as the session ID. Session IDs are determined in three ways:

1. **Attribute-declared (recommended)** — The event class uses `[SessionKey(nameof(OrderId))]` to declaratively specify which property determines the session. No method override needed — the base `Event.GetSessionId()` reads the attribute via reflection.
2. **Event-defined (override)** — The event class overrides `GetSessionId()` to provide a computed or custom session key. Takes precedence over the attribute.
3. **Publisher-controlled (explicit)** — The publisher can override the session ID at publish time via `publisher.Publish(event, sessionId, correlationId)` or `publisher.Publish(event, sessionId, correlationId, messageId)`. This enables scenarios where:
   - The ordering key differs from the event's natural key
   - Multiple event types should share a session for cross-event ordering
   - The publisher needs deterministic message IDs for deduplication

When a handler fails, the session is blocked — subsequent messages for that session are deferred to a separate subscription and replayed in FIFO order after recovery.

## Consequences

### Positive
- Guaranteed FIFO ordering per session without application-level locking
- Built-in session state for tracking blocked/deferred status
- Concurrent processing across different sessions (high throughput)
- Native Service Bus feature — no external dependencies

### Design Choice
- NimBus is intentionally built for Azure Service Bus — sessions are a core platform capability, not an abstraction to be swapped. Transport abstraction is explicitly out of scope (see roadmap "What NOT to Do"). This focus enables deep integration with Service Bus features (sessions, dead-letter queues, scheduled delivery, forwarding subscriptions) without compromise.
- *Amendment proposed in ADR-018:* Service Bus stays the reference transport and the default. RabbitMQ with SQL Server would be allowed as one additional transport for on-premises installations, behind the existing Core contracts and matching Service Bus behaviour. A generic transport layer, or a third transport, is still out of scope.

### Trade-offs
- Session lock management adds complexity (lock renewal, idle timeout)
- Maximum concurrent sessions is configurable but bounded
- Testing requires session-aware mocking (addressed by `NimBus.Testing` in-memory transport)
