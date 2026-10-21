# ADR-018: One Additional Transport, RabbitMQ, for On-Premises Installations

## Status
Proposed (2026-10-21). When accepted, it amends two ADRs:

- [ADR-001](001-session-based-ordering.md): Service Bus sessions stay the ordering model on Azure,
  and ADR-001's "transport abstraction is explicitly out of scope" is narrowed to the rule below.
- [ADR-002](002-centralized-resolver.md): its principle that subscribers depend only on Service Bus,
  with no database connection, holds on Azure. On RabbitMQ every endpoint also needs SQL Server for
  transport state.

The design is in [Spec 041](../spec/041-on-prem-rabbitmq-transport/spec.md).

## Context

ADR-001 made Azure Service Bus sessions NimBus's ordering mechanism and ruled out a transport
abstraction, so NimBus could use Service Bus features (sessions, dead-letter queues, scheduled
delivery, forwarding subscriptions) without compromise. `CONTRIBUTING.md`, `AGENTS.md` and
`docs/contribution-areas.md` repeat the rule. It was written without a concrete case for a second
transport.

There is one now. Some customers can't or won't run on Azure and want NimBus on their own servers,
with no cloud connectivity. Storage is already covered: [ADR-010](010-pluggable-message-storage.md)
made storage pluggable, and the SQL Server provider, inbox and outbox exist. The transport is the
gap.

An investigation (Spec 041 §2; Codex reviewed it four times, and the draft spec once) found that a second transport costs less than
ADR-001 assumed:

- `NimBus.Core` does not reference the Service Bus SDK. A transport implements four Core contracts
  (`IMessageContext`, `ISender`, `IDeferredMessageProcessor`, `IReplyDispatcher`). The in-memory
  transport in `NimBus.Testing` already implements three of them; it lacks only an
  `IDeferredMessageProcessor`.
- Failure isolation lives in Core, not the broker. A failed message is completed and its session
  marked blocked; later messages for the session are parked and completed. The broker only has to
  deliver each session in order to one consumer at a time.
- The Service Bus–specific parts are the receiver hosting, session state, the `Deferred`
  subscription, scheduled delivery, subscription rules and auto-forwarding, and the WebApp's admin
  features. Each has a RabbitMQ + SQL Server counterpart.

Options considered:

1. **Stay Azure-only.** Rejected: it excludes customers who must run on-premises, for a cost the
   investigation shows is bounded.
2. **A generic transport abstraction** with pluggable brokers. Rejected: it turns NimBus into a
   messaging framework, invites NServiceBus parity work, and weakens the Service Bus integration
   ADR-001 protects.
3. **Exactly one additional transport, RabbitMQ with SQL Server, behind the existing Core
   contracts, matching Service Bus behaviour.** Chosen.
4. **Run the NimBus Service Bus emulator in production.** Rejected: it keeps messages, locks and
   session state in memory, binds to loopback and doesn't validate credentials. Making it a durable
   broker means building a broker.
5. **Fork an on-prem edition.** Rejected: Core, the Resolver, the SQL Server store, the WebApp and
   the SDK would be duplicated, and every fix would have to land twice. NimBus already pays that
   cost for its DIS fork.
6. **Another broker** (Apache ActiveMQ Artemis message groups, RabbitMQ streams). Not pursued;
   Spec 041 §13 records why.

## Decision

- **Service Bus stays the reference transport and the default.** Every feature lands on Service Bus
  first. Its behaviour defines correct behaviour for both transports.
- **RabbitMQ is the one additional transport**, for on-premises installations, always with SQL
  Server for storage and transport state. It reproduces Service Bus behaviour: per-session order,
  failure isolation, parking and replay, scheduling, dead-lettering, fan-out and request/reply.
  Where it can't match, Spec 041 §7 records the difference.
- **No generic transport layer.** The seam is the four existing Core contracts plus the minimum
  needed to host a receiver and choose a transport (Spec 041 §6.3). A third transport needs a new
  ADR.
- **One repository.** The RabbitMQ transport lives in this repository and ships as its own package,
  `Akaule.NimBus.Transport.RabbitMq`. Deployment artifacts for on-premises installations (compose
  files, installer, runbooks) may live in a separate distribution repository that consumes released
  packages, in the spirit of [ADR-015](015-customer-deployment-distribution.md).
- **One shared behaviour suite.** A transport conformance suite in `NimBus.Testing` runs against
  the in-memory transport, the Service Bus emulator and RabbitMQ. A transport change that breaks it
  doesn't merge.
- **Transport-specific features are explicit.** A feature that only one transport can offer (for
  example Service Bus subscription rules in the WebApp) is gated by a transport capability, not
  emulated badly.
- **Experimental until supportable.** The RabbitMQ registration API carries `[Experimental]` until
  Spec 041 phase 4 completes.

## Consequences

### Positive
- NimBus can run with no Azure dependency at runtime: RabbitMQ, SQL Server and .NET services.
- Core, the Resolver, the store, the WebApp and the SDK stay one codebase, so on-prem installations
  get every fix.
- The work fixes three existing Core bugs that affect Service Bus installations today (Spec 041
  §2.7), with regression tests, before any on-prem release.
- The transport conformance suite makes Service Bus behaviour explicit and tested, which helps the
  Service Bus transport too.
- The CI split that comes with it (Spec 041 §8) starts service containers only where they are
  needed and adds the WebApp frontend tests, which CI doesn't run today.

### Negative
- **NimBus owns coordination Service Bus provides.** Partition leases, session state, parking,
  scheduling and fan-out are NimBus code on RabbitMQ, with their own crash windows to test.
- **Weaker isolation inside a partition.** A slow handler or a transient retry delays the other
  sessions in its partition. Service Bus delays only the session.
- **More moving parts on-prem:** a router service (hosted in the Resolver host by default), a
  three-node RabbitMQ cluster, and every NimBus process, publish-only applications included, needs
  SQL Server access for transport state and the routing table. On Azure, ADR-002's "subscribers
  depend only on Service Bus" still holds.
- **The router is a platform service Service Bus doesn't need.** With every replica down, messages
  wait in intake queues and nothing is routed; replicas and a readiness check mitigate it.
- **More surface to maintain.** Transport-touching changes need both transports and the shared suite;
  CI gains a RabbitMQ lane.
- **The SDK package keeps its Service Bus dependency until the next major.** On-prem applications
  carry the Service Bus assembly without using it (Spec 041 §6.2).
- **Guidance changes.** `CONTRIBUTING.md`, `AGENTS.md`, `docs/contribution-areas.md` and
  `docs/architecture.md` point here instead of ruling out a second transport. While this ADR is
  proposed they say so; accepting it removes "proposed" from them and from the notes in ADR-001,
  ADR-002 and ADR-013.
