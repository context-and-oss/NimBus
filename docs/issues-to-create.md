# Issues to Create

Paste-ready GitHub Issues for every Not-Started backlog item in the P0–P3 priority bands, drafted to match `.github/ISSUE_TEMPLATE/backlog-item.yml`.

Workflow:

1. Open the repo's Issues tab and pick **Backlog item** as the template.
2. Copy the **title** below, paste into the issue title field.
3. Copy each section's content into the matching form field.
4. Apply the suggested labels.

Once an issue is opened, delete the corresponding section from this file (or replace with `→ #123`) so this document stays an active checklist rather than drifting documentation.

---

## P2 — Medium

### 1. Circuit Breaker Middleware

→ [#114](https://github.com/akakaule/NimBus/issues/114)

---

### 2. Inbox Pattern (Idempotent Consumers)

**Title:** `[Backlog] Inbox Pattern — Idempotent Consumers`
**Labels:** `backlog`, `P2`, `phase-3`, `feature`, `reliability`

**Use case:**
The transactional outbox guarantees at-least-once *delivery* from the publisher's side. But duplicates can still occur — Service Bus redelivery on a transient handler failure, message replay through Resubmit in the WebApp, network retries. Without an inbox, handlers must individually be idempotent — which is hard to enforce and easy to get wrong. The inbox pattern stores `MessageId` of every successfully processed message in a deduplication store; subsequent deliveries of the same ID are skipped. Paired with the outbox, this gives you exactly-once *processing semantics* across the pipeline.

**Proposed API:**
```csharp
// Registration — opt-in per subscriber
services.AddNimBusSubscriber("billing", b => {
    b.AddHandler<OrderPlaced, OrderPlacedHandler>();
    b.UseInbox(opts => {
        opts.DeduplicationStore = InboxStore.Cosmos;  // or Sql
        opts.RetentionPeriod = TimeSpan.FromDays(7);
    });
});

// Abstraction
public interface IInboxStore
{
    Task<bool> TryRecordAsync(string messageId, CancellationToken ct);  // false if already exists
    Task PurgeExpiredAsync(DateTimeOffset olderThan, CancellationToken ct);
}
```

**Integration points:**
- `src/NimBus.Core/Inbox/IInboxStore.cs` (new abstraction)
- `src/NimBus.Core/Inbox/InboxMiddleware.cs` (new pipeline behavior)
- Cosmos implementation in `src/NimBus.MessageStore/CosmosInboxStore.cs`
- SQL implementation in `src/NimBus.Inbox.SqlServer/` (new package, parallels outbox)
- Background cleanup via existing `OutboxCleanup` host pattern
- Integrates as middleware so it runs early in the pipeline (before handler invocation)

**Acceptance criteria:**
- [ ] `IInboxStore` abstraction with `TryRecordAsync` and `PurgeExpiredAsync`
- [ ] Cosmos DB implementation in `NimBus.MessageStore`
- [ ] SQL Server implementation as `NimBus.Inbox.SqlServer` package (mirrors outbox structure)
- [ ] `InboxMiddleware` registered as `IMessagePipelineBehavior` running before handler dispatch
- [ ] Configurable TTL with background purge
- [ ] Skipped duplicates emit a `DuplicateDetected` lifecycle event for the Resolver
- [ ] WebApp shows a "skipped (duplicate)" status on the affected events
- [ ] Unit tests covering: first delivery succeeds, duplicate skipped, expired record allows reprocessing
- [ ] Integration test demonstrating end-to-end exactly-once with outbox + inbox
- [ ] Documentation in `docs/inbox-pattern.md`

**Reference implementations:**
- CAP: built-in inbox via SQL or MongoDB
- NServiceBus: outbox includes deduplication on consumer side
- General pattern: https://microservices.io/patterns/data/transactional-outbox.html

**Open questions:**
- Should the inbox be required or opt-in? (Probably opt-in — there's a perf cost and not every handler needs it.)
- Is `MessageId` always trustworthy, or do we need to derive a deterministic content hash? (Service Bus `MessageId` is set by the publisher, so trust depends on the publisher. Resubmit re-uses the same ID, which is what we want.)
- How does this interact with sessions? (Inbox check happens per message regardless of session.)
- Should the deduplication record include a hash of the payload to detect "same ID, different content" as a publisher bug?

**Estimated effort:** Medium (1–2 weeks)

---

### 3. Claim-Check Pattern

**Title:** `[Backlog] Claim-Check Pattern — Large Payload Offload to Blob Storage`
**Labels:** `backlog`, `P2`, `phase-4`, `feature`, `extension`, `enterprise`

**Use case:**
Azure Service Bus has a 256KB message size limit on Standard tier and 1MB on Premium. Real-world enterprise messages — orders with line-item arrays, documents with embedded metadata, events with diagnostic snapshots — frequently exceed this. Today, NimBus consumers either crash on send (`MessageSizeExceededException`) or developers manually serialize payloads to Blob Storage and pass references, which is error-prone and inconsistent across services. The claim-check pattern automates this transparently: payloads above a threshold are written to Blob Storage by the publisher and rehydrated by the consumer, with a small reference flowing through Service Bus.

**Proposed API:**
```csharp
// Publisher registration
services.AddNimBusPublisher("storefront", opts => {
    opts.UseClaimCheck(cc => {
        cc.SizeThresholdBytes = 200 * 1024;  // offload anything > 200KB
        cc.BlobContainer = "nimbus-claim-check";
        cc.BlobConnectionString = configuration["BlobStorage:ConnectionString"];
        cc.RetentionPeriod = TimeSpan.FromDays(7);  // matches DLQ retention
    });
});

// Subscriber registration — automatic rehydration
services.AddNimBusSubscriber("billing", b => {
    b.AddHandler<OrderPlaced, OrderPlacedHandler>();
    b.UseClaimCheck();  // reads same blob container
});

// New package
namespace NimBus.Extensions.ClaimCheck;
```

**Integration points:**
- New package: `src/NimBus.Extensions.ClaimCheck/`
- `ClaimCheckMiddleware` implementing `IMessagePipelineBehavior` for the consumer side
- `IClaimCheckStore` abstraction with default `BlobClaimCheckStore`
- Publisher side: hooks into `PublisherClient` via decorator (parallels `OutboxSender` pattern)
- Application property `nimbus.claim_check.uri` on the Service Bus message identifies offloaded payloads
- WebApp Event Details page shows "Payload offloaded to claim-check" with a link if the user has access

**Acceptance criteria:**
- [ ] `NimBus.Extensions.ClaimCheck` package with publisher + consumer integration
- [ ] Configurable size threshold
- [ ] Transparent rehydration: handler receives the original message type, no API change for consumers
- [ ] Audit trail: Resolver records claim-check URI in the message store for traceability
- [ ] Cleanup: orphaned blobs purged after retention period
- [ ] Resubmit support: blob reference survives resubmit-as-is; resubmit-with-modifications uploads a new blob
- [ ] Unit + integration tests covering: small payload (no offload), large payload (offload), missing blob (graceful failure with dead-letter), resubmit
- [ ] Documentation in `docs/claim-check.md`
- [ ] Sample in `samples/AspirePubSub/` with a `LargePayloadEndpoint`

**Reference implementations:**
- NServiceBus DataBus + `[DataBusProperty]` — https://docs.particular.net/nservicebus/messaging/databus/
- Azure Architecture Center: Claim-Check pattern — https://learn.microsoft.com/azure/architecture/patterns/claim-check
- MassTransit: middleware-based file storage

**Open questions:**
- Field-level vs message-level offload? NServiceBus does field-level; simpler is message-level whole-payload offload. Recommend message-level for v1.
- Should the consumer fail or skip if the blob is missing (e.g. retention expired)? Recommend dead-letter with a clear reason.
- How do we handle authentication? Managed Identity vs connection string. Probably both, with Managed Identity preferred.
- Should claim-check work with the outbox? Yes — outbox stores the claim-check reference, not the original payload.

**Estimated effort:** Medium (2 weeks)

---

## P3 — Lower (workable now)

### 4. Failed Message Hook

**Title:** `[Backlog] Failed Message Hook — Last-Chance Handler Before Dead-Letter`
**Labels:** `backlog`, `P3`, `phase-4`, `feature`, `reliability`, `good first issue`

**Use case:**
After retry exhaustion, NimBus dead-letters a message. That's a fail-stop outcome — operations must intervene via the WebApp. But many failures are recoverable in software: a malformed postal code can be normalized, a stale token can be refreshed, a missing field can be inferred. The Failed Message Hook gives the application a final intercept *before* dead-lettering: enrich diagnostics, re-route to a quarantine endpoint, modify and replay, or publish a compensating event. This shifts recoverable failures from human ops work to automated handling, while keeping the WebApp resubmit flow as the safety net for everything else.

**Proposed API:**
```csharp
// Registration
services.AddNimBusSubscriber("billing", b => {
    b.AddHandler<OrderPlaced, OrderPlacedHandler>();
    b.AddFailedHandler<OrderPlaced, OrderPlacedFailedHandler>();
});

// Implementation
public class OrderPlacedFailedHandler : IFailedMessageHandler<OrderPlaced>
{
    public async Task<FailedMessageOutcome> HandleAsync(
        OrderPlaced message,
        Exception lastError,
        IFailedMessageContext context,
        CancellationToken ct)
    {
        if (lastError is AddressNormalizationException)
        {
            var fixedMessage = message with { PostalCode = NormalizePostalCode(message.PostalCode) };
            return FailedMessageOutcome.Modify(fixedMessage).Retry();
        }

        return FailedMessageOutcome.DeadLetter
            .WithDiagnostic("customer_id", message.CustomerId.ToString())
            .WithDiagnostic("retry_count", context.DeliveryCount.ToString());
    }
}

public abstract record FailedMessageOutcome
{
    public static FailedMessageOutcome DeadLetter { get; }
    public static FailedMessageOutcome Drop { get; }
    public static ModifyOutcome Modify(IMessage replacement);
    public static FailedMessageOutcome Reroute(string endpoint);
}
```

**Integration points:**
- `src/NimBus.Abstractions/Failed/IFailedMessageHandler.cs` (new)
- `src/NimBus.Core/Pipeline/FailedMessageDispatch.cs` (new dispatch point)
- Hooks into `StrictMessageHandler` at the point retry budget is exhausted but before dead-letter
- Resolver gets new lifecycle event types: `FailedHandlerInvoked`, `FailedHandlerRerouted`, `FailedHandlerModifiedAndReplayed`, `FailedHandlerDropped`
- WebApp Event Details shows "handled by failed-message hook → outcome" instead of just "failed"

**Acceptance criteria:**
- [ ] `IFailedMessageHandler<T>` abstraction in `NimBus.Abstractions`
- [ ] `FailedMessageOutcome` discriminated union: DeadLetter, Drop, Modify+Retry, Reroute
- [ ] Registration via `NimBusSubscriberBuilder.AddFailedHandler<T,THandler>()`
- [ ] Dispatch point in `StrictMessageHandler` after retry exhaustion
- [ ] Resolver lifecycle events recorded for each outcome
- [ ] WebApp UI shows the outcome on Event Details page
- [ ] Unit tests for each outcome type
- [ ] Integration test in `tests/NimBus.EndToEnd.Tests/` showing modify-and-retry succeeding on second attempt
- [ ] Documentation in `docs/failed-message-hook.md`

**Reference implementations:**
- Rebus `IFailed<T>`: https://github.com/rebus-org/Rebus/wiki/Second-level-retries
- MassTransit `Fault<T>`: https://masstransit.io/documentation/concepts/exceptions
- NServiceBus second-level retries

**Open questions:**
- Should "Modify+Retry" reset the delivery count or preserve it? Recommend reset, since the modified message is functionally a new message.
- Is "Reroute" a generic publish to another endpoint, or does it need session-aware delivery? Start with simple publish.
- Can the failed handler itself fail? If yes, fall back to dead-letter with a `FailedHandlerErrored` lifecycle event.

**Estimated effort:** Small (3 days)

---

### 5. Source Generators

**Title:** `[Backlog] Source Generators for Event Type Discovery`
**Labels:** `backlog`, `P3`, `phase-4`, `feature`, `dx`, `performance`

**Use case:**
NimBus discovers event types via reflection at startup — scanning assemblies, evaluating attributes, building the type catalog. This is invisible to developers when it works but causes three concrete pains: (1) startup latency proportional to assembly count, (2) AOT-incompatible (blocks NativeAOT publishing), and (3) misconfiguration errors surface at runtime as null reference exceptions instead of compile errors. Source generators move all this to compile time: faster startup, AOT-friendly, and "you forgot `[SessionKey]`" becomes a CS-error during build instead of a 3am page.

**Proposed API:**
```csharp
// No API change for consumers. The generator runs automatically based on existing attributes:
[Description("Customer placed an order.")]
[SessionKey(nameof(OrderId))]
public class OrderPlaced : Event
{
    [Required] public Guid OrderId { get; set; }
}

// Generator emits a partial class registering this with the catalog at compile time.
// Reflection-based discovery is removed (or kept as a fallback for assemblies that
// haven't migrated).
```

**Integration points:**
- New project: `src/NimBus.SourceGenerators/` (Roslyn-based)
- Replaces reflection in `NimBus.Abstractions/Events/EventCatalog.cs`
- Generator emits one partial-class file per assembly registering all events
- Diagnostics: `NB001` (Event missing `[Description]`), `NB002` (`[SessionKey]` references non-existent property), `NB003` (Event class is not partial when needed)

**Acceptance criteria:**
- [ ] `NimBus.SourceGenerators` project with Roslyn incremental generator
- [ ] Discovers all `Event`-derived types and emits registration code
- [ ] Emits compiler diagnostics for misconfiguration
- [ ] Performance: startup time reduced by 50%+ for projects with 50+ event types (measured)
- [ ] AOT-compatible: project can be published with `<PublishAot>true`
- [ ] Backward-compatible fallback: if generator output is missing, reflection still works
- [ ] Unit tests using `Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing`
- [ ] Documentation in `docs/source-generators.md`

**Reference implementations:**
- MassTransit consumer source generator
- MediatR source generator: https://github.com/jbogard/MediatR/tree/master/src/MediatR.SourceGeneration
- Roslyn cookbook: https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md

**Open questions:**
- Should the generator also handle handler discovery, or only event types? Recommend events only for v1, handlers in a follow-up.
- AOT-compatibility for the WebApp is a separate concern (System.Text.Json, EF Core) — out of scope.
- Versioning: how do we keep the generator and runtime in sync across NuGet package versions?

**Estimated effort:** Medium (2 weeks)

---

### 6. Message Versioning

**Title:** `[Backlog] Message Versioning — Schema Evolution`
**Labels:** `backlog`, `P3`, `phase-4`, `feature`, `dx`, `contracts`

**Use case:**
Evolving message contracts is the #1 source of production incidents in long-lived messaging systems. A field added to `OrderPlaced` breaks consumers running an older version. A field removed strands in-flight messages. Today, NimBus relies on Newtonsoft.Json's permissive defaults — additive nullable fields work, removals do not, and there's no compile-time guarantee of compatibility. Message versioning gives the framework explicit support: version attributes, polymorphic dispatch from V2 to V1 handlers, and (with source generators) compile-time compatibility checks.

**Proposed API:**
```csharp
[MessageVersion(1)]
[Description("Customer placed an order.")]
public class OrderPlaced : Event
{
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
}

[MessageVersion(2)]
public class OrderPlacedV2 : OrderPlaced
{
    public string PromoCode { get; set; }   // new in v2
}

// Handler written for V1 also receives V2 (polymorphic dispatch)
public class BillingHandler : IEventHandler<OrderPlaced> { ... }

// Handler can opt into V2 if it cares
public class FulfillmentHandler : IEventHandler<OrderPlacedV2> { ... }
```

**Integration points:**
- `src/NimBus.Abstractions/Events/MessageVersionAttribute.cs` (new)
- `src/NimBus.Core/Routing/PolymorphicDispatcher.cs` (new) — dispatches each message to all matching handlers up the inheritance chain
- Schema registry in the WebApp showing versions and consumer compatibility
- Source generator (depends on #5) for compile-time schema diff checks
- Documentation patterns: additive-nullable, never-rename, never-remove

**Acceptance criteria:**
- [ ] `[MessageVersion(int)]` attribute
- [ ] Polymorphic dispatch: handler for `T` receives all messages assignable to `T`
- [ ] Version metadata recorded in Service Bus application properties
- [ ] WebApp shows the version on Event Details
- [x] Documentation: contract evolution playbook in `docs/message-versioning.md`
- [ ] Unit tests for inheritance dispatch
- [ ] Integration test: V1 handler receives V2 messages with new fields ignored
- [ ] (Stretch) Source-generator-based compile-time schema diff with `NB100..NB199` diagnostics

**Reference implementations:**
- NServiceBus polymorphic message handling: https://docs.particular.net/nservicebus/messaging/polymorphic-messages
- Confluent Schema Registry (Kafka pattern, conceptually similar)
- Wolverine message versioning

**Status (2026-11-24):** The contract evolution playbook is written:
[`message-versioning.md`](message-versioning.md). It settles the first open question below for
today's NimBus: a breaking change is a **separate event type** (`OrderPlacedV2` does not inherit
from `OrderPlaced`), because polymorphic dispatch does not exist. The `[MessageVersion]`
attribute, polymorphic dispatch and a version on the wire remain unbuilt, and the proposed API
above is not how NimBus behaves.

**Open questions:**
- Inheritance vs separate types? Inheritance gives polymorphic dispatch for free; separate types are cleaner but require explicit migration handlers.
- Should we ship a schema-registry feature or just the attribute + polymorphism? Recommend the latter for v1.
- How does versioning interact with the outbox? Stored payload is versioned; deserializer must handle older versions.

**Estimated effort:** Medium (1–2 weeks)

---

### 7. Rate Limiting Middleware

**Title:** `[Backlog] Rate Limiting Middleware`
**Labels:** `backlog`, `P3`, `phase-4`, `feature`, `middleware`, `resilience`, `good first issue`

**Use case:**
NimBus consumers can pull messages from Service Bus at line rate — but downstream services (third-party APIs with quotas, internal services with capacity limits, paid SaaS endpoints) often cannot. Without rate limiting, a backlog burst saturates the downstream and triggers 429s, exhausting the retry budget on every message. A rate-limiting middleware throttles consumption to a configurable rate; over-the-limit messages abandon back to the queue for later redelivery.

**Proposed API:**
```csharp
services.AddNimBusSubscriber("integration", b => {
    b.AddHandler<DocumentSync, DocumentSyncHandler>();
    b.AddPipelineBehavior<RateLimitingMiddleware>(opts => {
        opts.Strategy = RateLimitStrategy.TokenBucket;
        opts.PermitLimit = 100;
        opts.Window = TimeSpan.FromMinutes(1);
        opts.QueueLimit = 0;       // don't queue inside the middleware; abandon to Service Bus
    });
});

// Per-event-type
b.ConfigureRateLimit<DocumentSync>(opts => { opts.PermitLimit = 50; });
```

**Integration points:**
- `src/NimBus.Core/Pipeline/RateLimitingMiddleware.cs` (new)
- Built on `System.Threading.RateLimiting` (BCL) — token bucket, sliding window, fixed window, concurrency
- When limit exceeded: middleware throws `RateLimitExceededException` → caught by `StrictMessageHandler` → message is abandoned (returned to queue) rather than dead-lettered
- Metrics: `nimbus.rate_limit.permits_consumed_total`, `nimbus.rate_limit.permits_rejected_total`

**Acceptance criteria:**
- [ ] `RateLimitingMiddleware` implementing `IMessagePipelineBehavior`
- [ ] All four `System.Threading.RateLimiting` strategies supported
- [ ] Per-endpoint and per-event-type configuration
- [ ] Over-limit messages abandoned (NOT dead-lettered)
- [ ] Metrics for permits consumed and rejected
- [ ] Unit tests for each strategy
- [ ] Integration test demonstrating throttle and recovery
- [ ] Documentation in `docs/pipeline-middleware.md`

**Reference implementations:**
- BCL `System.Threading.RateLimiting` — https://learn.microsoft.com/dotnet/api/system.threading.ratelimiting
- ASP.NET Core rate limiting middleware (same primitive, different host)

**Open questions:**
- Single rate limiter per endpoint vs per-instance vs per-(endpoint, partition key)?
- Distributed rate limiting (Redis-backed) — out of scope for v1, add later if needed.
- How do we surface "currently rate limited" in the WebApp?

**Estimated effort:** Small (3–5 days)

---

### 8. Notification Channels

**Title:** `[Backlog] Notification Channels — Webhook, Teams, Email`
**Labels:** `backlog`, `P3`, `phase-4`, `feature`, `extension`, `ops`

**Use case:**
The existing `NimBus.Extensions.Notifications` framework can detect failures, but ops teams have no production-ready channel to send the alerts to. They want to wake up to a Teams message or PagerDuty page when a session blocks, not discover it the next morning in the WebApp. Notification Channels supplies three production channels (webhook, Teams, email) plus severity-based routing and rate limiting to prevent notification storms when something cascades.

**Proposed API:**
```csharp
services.AddNimBusNotifications(n => {
    n.AddWebhook(opts => {
        opts.Url = "https://incident-bot.example.com/nimbus";
        opts.MinSeverity = NotificationSeverity.Warning;
    });
    n.AddTeams(opts => {
        opts.ConnectorUrl = configuration["Teams:WebhookUrl"];
        opts.MinSeverity = NotificationSeverity.Critical;
    });
    n.AddEmail(opts => {
        opts.Provider = EmailProvider.SendGrid;
        opts.ApiKey = configuration["SendGrid:ApiKey"];
        opts.From = "alerts@example.com";
        opts.To = ["oncall@example.com"];
        opts.MinSeverity = NotificationSeverity.Critical;
    });
    n.WithRateLimit(maxPerMinute: 10, burstCapacity: 20);
});
```

**Integration points:**
- Extends existing `NimBus.Extensions.Notifications` package
- Three new channel implementations: `WebhookChannel`, `TeamsChannel`, `EmailChannel`
- New `INotificationRouter` for severity-based filtering
- Built-in batching: aggregate notifications within a window before sending (prevents storms)
- Templates: configurable payload templates per channel

**Acceptance criteria:**
- [ ] Webhook channel with HTTP POST + configurable JSON template
- [ ] Microsoft Teams channel using Adaptive Cards via incoming webhook
- [ ] Email channel: SendGrid + SMTP options
- [ ] Severity-based routing per channel
- [ ] Rate limiting / batching to prevent notification storms
- [ ] Trigger sources: failed messages, dead-letters, session blocks
- [ ] Unit tests with mocked channels
- [ ] Integration test with a local webhook receiver
- [ ] Documentation in `docs/notifications.md`

**Reference implementations:**
- NServiceBus error notifications + ServiceControl
- Hangfire notifications dashboard

**Open questions:**
- Should we support PagerDuty / Opsgenie out of the box, or rely on the webhook channel?
- Email: SendGrid as primary, SMTP as fallback? Or both equal?
- Acknowledgement / deduplication — if Service Bus redelivers a failure event, do we re-notify? Recommend: dedupe on `(EventId, Status)` within a 5-minute window.

**Estimated effort:** Medium (1–2 weeks)

---

### 9. Orchestration Pattern Guide

**Title:** `[Backlog] Orchestration Pattern Guide & Sample`
**Labels:** `backlog`, `P3`, `phase-3`, `documentation`, `sample`

**Use case:**
[ADR-009](adr/009-orchestration-via-application-services.md) established that NimBus deliberately does not implement framework-level sagas — instead, multi-step workflows are implemented as application-level orchestration services using NimBus's messaging primitives. That decision is documented; the pattern is not. New teams hitting their first orchestration use case (refund → cancel shipment → notify customer) have no canonical example. They either reinvent it or ask "doesn't NimBus have sagas?" The guide closes that gap with documentation, a concrete sample, and patterns for the two common cases (timeouts via scheduling, compensation via published events).

**Proposed API:**
No API change. This is documentation + sample.

**Integration points:**
- New documentation page: `docs/orchestration.md`
- New sample: `samples/AspirePubSub/OrchestratorService` (alongside existing Publisher / Subscriber / Provisioner)
- Sample demonstrates a 3-step workflow: `OrderPlaced` → `PaymentRequested` → `ShipmentCreated` with compensation on failure
- Uses `ScheduleMessage` for timeouts (already shipped in Phase 3)
- Uses published events for compensation paths

**Acceptance criteria:**
- [ ] `docs/orchestration.md` covering: when to use orchestration vs choreography, state management options (inline DB vs in-message), timeout patterns, compensation patterns, testing
- [ ] Working sample in `samples/AspirePubSub/OrchestratorService/`
- [ ] Sample includes timeout (using `ScheduleMessage` / `CancelScheduledMessage`)
- [ ] Sample includes compensation (publishing `OrderCancelled` on failure)
- [ ] README in the sample explaining the flow with a sequence diagram
- [ ] Cross-link from `docs/getting-started.md` and ADR-009

**Reference implementations:**
- ADR-009 in this repo
- MassTransit Sagas (the *opposite* approach — show the contrast)
- NServiceBus Sagas (same — show the contrast)
- Yves Goeleven's "Orchestration vs Choreography" blog series

**Open questions:**
- Should the sample use Cosmos for orchestrator state or in-memory? Cosmos for realism, but with a `--in-memory` flag for quick local runs.
- Do we want a "starter template" repo separate from the Aspire sample? Out of scope for v1.

**Estimated effort:** Small–Medium (1 week)

---

## Coverage check

This file is the canonical list of paste-ready Not-Started backlog issues. Each section is self-contained — pick one, open the **Backlog item** issue template, paste the contents into the matching form fields, and apply the suggested labels.

| # | Item | Priority |
|---|---|---|
| §1 | Circuit Breaker Middleware | P2 |
| §2 | Inbox Pattern | P2 |
| §3 | Claim-Check Pattern | P2 |
| §4 | Failed Message Hook | P3 |
| §5 | Source Generators | P3 |
| §6 | Message Versioning | P3 |
| §7 | Rate Limiting Middleware | P3 |
| §8 | Notification Channels | P3 |
| §9 | Orchestration Pattern Guide | P3 |

Items intentionally not listed: "WebApp Enhancements: Alerting" is folded into §8 Notification Channels, and "Documentation & Onboarding: Migration guide" is small enough to be a single PR rather than warranting its own backlog issue. Speculative P4 items (Transport Abstraction, Multi-Tenant, Event Sourcing) are deliberately omitted — open them only when there is a concrete use case.
