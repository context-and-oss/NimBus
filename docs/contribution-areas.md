# Where contributions are most welcome

This is a curated list of areas where external contributions are actively wanted, with a rough size and whether the work touches core (and therefore needs an ADR/design review first — see [CONTRIBUTING.md](../CONTRIBUTING.md#design-review--adrs)). It's a starting point for the conversation, not a fixed backlog — open a [backlog issue](../.github/ISSUE_TEMPLATE/backlog-item.yml) to claim or propose an item.

Sizes are rough, pre-discovery: **S** ≈ a few days, **M** ≈ ~1–2 weeks, **L** ≈ multi-week.

## Best first areas

These are self-contained, have clear boundaries, and don't require deep changes to core contracts.

| Area | Size | Core change / ADR? | Notes |
|------|------|--------------------|-------|
| **Additional pipeline behaviors** — circuit breaker, rate limiting | S–M | No | Implement `IMessagePipelineBehavior`, register via `AddPipelineBehavior<T>`. Both are on the roadmap (P2/P3). Good entry point to the pipeline model — see `docs/pipeline-middleware.md`. |
| **Inbox pattern (idempotent consumers)** | M | Light (storage-adjacent) | Roadmap P2. Dedup on message id via the message store; align the storage touchpoints in an issue first. |
| **Notification channels** — webhook / Teams / email | S–M | No | Extends `NimBus.Extensions.Notifications`. Purely additive; a clean contribution surface. |
| **WebApp enhancements** | S–L | No (enhance, don't rewrite) | Incremental UI/UX improvements to the Management UI. See the two larger visualization items below. |
| **Docs & samples** | S | No | Getting-started polish, new sample adapters, ADR write-ups, flow diagrams. |

## Larger, higher-value items

Worth co-designing. These map directly to what evaluating teams have asked for.

| Area | Size | Core change / ADR? | Notes |
|------|------|--------------------|-------|
| **CloudEvents envelope support** | M | Yes (envelope/wire format) | Envelope + content-type adapter at publish/consume, over the existing NimBus envelope. Decide structured vs binary content mode and which attributes; keep the AsyncAPI export in sync. Needs an ADR because it touches the wire format. |
| **Interface/integration visualization in the Management UI** | L | Partly (new derived model) | Model logical *interfaces* (one-way, single event-type flow) and physical *integrations* (same-direction flows between the same two systems) and render the relationship graph. The underlying data already exists (event types, producers/consumers, forwarding topology); this is a modeling + visualization layer. Confirm the definitions map cleanly first. |
| **Native message-journey view** | L | Partly | A unified cross-adapter message journey / trace waterfall inside the Management UI, with drill-down from a dashboard to the failing/blocking step. Tracing data already flows (W3C context + OpenTelemetry, six `ActivitySource`s); this surfaces it natively from the tracking store. |
| **Additional storage providers** | M–L | Yes (implements storage abstractions) | Implement `IMessageTrackingStore` / `ISubscriptionStore` / `IEndpointMetadataStore` / `IMetricsStore` and pass the conformance suite in `NimBus.Testing`. ADR needed; follow the pattern of the Cosmos and SQL Server providers (ADR-010). |

## Roadmap context

The broader roadmap lives in the [backlog issues](../.github/ISSUE_TEMPLATE/backlog-item.yml) and the [ADRs](adr). Current bands:

- **P2 (near-term):** Circuit Breaker, Inbox, Claim-Check.
- **P3 (medium-term):** Failed-Message hook, Source Generators, Message Versioning, Rate Limiting, Notification Channels, Orchestration guide.

## Out of scope

Please don't start these without a prior conversation — see [What we will and won't take](../CONTRIBUTING.md#what-we-will-and-wont-take). In short: no generic transport abstraction (ADR-018, proposed, would allow only the on-prem RabbitMQ transport), no NServiceBus parity chase, no WebApp rewrite, no event sourcing without a concrete use case.
