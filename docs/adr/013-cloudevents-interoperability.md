# ADR-013: CloudEvents 1.0 Interoperability Layer

## Status
Accepted. The transport rule quoted under Context still holds for CloudEvents. Separately,
[ADR-018](018-on-prem-rabbitmq-transport.md) (proposed, 2026-10-21) would allow one additional
transport, RabbitMQ, for on-premises installations.

## Context

NimBus's wire format is a NimBus-specific envelope routed with `user.*` Service
Bus application properties (`To`, `From`, `EventTypeId`, `MessageId`,
`SessionId`, `CorrelationId` — see `docs/asyncapi-mapping.md`). That format is
the right shape for NimBus-to-NimBus traffic, but it is opaque to any
consumer/producer that does not speak it: partner systems, generic AMQP
tooling, and integrations built against the [CloudEvents 1.0
specification](https://github.com/cloudevents/spec/blob/v1.0.2/cloudevents/spec.md)
(the CNCF's vendor-neutral event envelope) cannot read or write NimBus
messages without a translation layer.

Two forces shaped the design:

1. **NimBus's differentiators — sessions, the centralized Resolver, retry,
   dead-lettering, deferred processing — depend on the native `user.*`
   routing properties.** Any interoperability layer that replaced or hid those
   properties would break session ordering and audit tracking, the two things
   ADR-001 and ADR-002 exist to protect.
2. **The roadmap explicitly rules out a transport abstraction** ("don't
   abstract transports prematurely — Azure Service Bus is NimBus's
   strength"). CloudEvents is a *message envelope* standard, not a transport;
   adding CloudEvents support must not turn into adding a second transport or
   a pluggable-broker layer.

The chosen shape follows directly from those two constraints: CloudEvents
becomes an **opt-in encoding of the message body/properties**, layered on top
of the existing Azure Service Bus topic-per-endpoint topology — never a
replacement for it.

## Decision

### Opt-in per publisher / subscriber, off by default

CloudEvents is enabled per endpoint via `NimBusPublisherOptions.UseCloudEvents`
and `NimBusSubscriberOptions.UseCloudEvents`
(`src/NimBus.SDK/Extensions/NimBusPublisherOptions.cs`,
`NimBusSubscriberOptions.cs`). Neither method is called by default, so:

- **Native wire format is byte-identical when CloudEvents is not used.**
  `Message.CloudEvent` (`src/NimBus.Core/Messages/Models/Message.cs`) is a new
  nullable property that defaults to `null`; the transport binding only
  engages when it is set.
- Enabling CloudEvents on a publisher affects only that endpoint's outbound
  messages. Enabling it on a subscriber affects only that endpoint's inbound
  detection/parsing. A platform can mix native-only, CloudEvents-only, and
  `AutoDetect` endpoints freely — this is a per-endpoint decision, not a
  platform-wide switch.

```csharp
services.AddNimBusPublisher("BillingEndpoint", options =>
{
    options.Endpoint = "BillingEndpoint";
    options.UseCloudEvents(ce =>
    {
        ce.Source = new Uri("urn:customer:billing");
        ce.TypeNameStrategy = CloudEventTypeNameStrategy.UnqualifiedName;
        ce.ContentMode = CloudEventContentMode.Binary;
    });
});
```

### Two content modes, one Service Bus (AMQP) binding

`CloudEventContentMode` (`src/NimBus.Core/CloudEvents/CloudEventContentMode.cs`)
offers the two modes the CloudEvents AMQP protocol binding defines:

- **Binary** — the message body stays the raw domain-event JSON; CloudEvents
  context attributes ride as AMQP application properties under the
  `cloudEvents:` prefix (the standard AMQP binding prefix), and the AMQP
  content-type carries `datacontenttype`.
- **Structured** — the entire CloudEvent (context + `data`) is serialized as
  one `application/cloudevents+json` JSON envelope in the message body.

Both are implemented in one place, `CloudEventsServiceBusBinding`
(`src/NimBus.ServiceBus/CloudEventsServiceBusBinding.cs`) — `WriteBinary`,
`WriteStructured`, and a single `TryParse` that detects either shape on
consume (a structured content-type, or any core attribute present under an
accepted prefix). No new transport is introduced; this is a
serialization/property-mapping concern inside the existing
`NimBus.ServiceBus` AMQP path.

### Attribute set and the NimBus ↔ CloudEvents mapping

`CloudEvent` (`src/NimBus.Core/CloudEvents/CloudEvent.cs`) models the
CloudEvents 1.0 core attributes plus an open extension bag. The fixed part of
the mapping (not configurable — these have one obvious NimBus counterpart):

| CloudEvents attribute | NimBus source (publish) / sink (consume) |
|---|---|
| `id` | `MessageId` |
| `source` | `CloudEventPublisherOptions.Source` (endpoint-level, required for interop) |
| `type` | Event contract name — `CloudEventTypeNameStrategy.UnqualifiedName` (default, matches native `EventTypeId`) or `FullName`, or a `TypeOverride` factory |
| `specversion` | Always `"1.0"` — the only version NimBus emits or accepts |
| `data` | `EventContent.EventJson` (the domain event), **not** the NimBus `MessageContent` envelope |
| `datacontenttype` | `CloudEventPublisherOptions.DataContentType` (default `application/json`) |
| `dataschema` | Optional `CloudEventPublisherOptions.DataSchema` URI |
| `subject`, `time` | Optional, publisher-supplied factories (`Subject`, `Time`) |

Two NimBus concepts have no natural CloudEvents core attribute —
`CorrelationId` and `SessionId`. `CloudEventMapping`
(`src/NimBus.Core/CloudEvents/CloudEventMapping.cs`) controls where they land,
symmetrically on publish and consume:

| NimBus field | Default CloudEvents location | Overridable to |
|---|---|---|
| `CorrelationId` | extension attribute `correlationid` | `subject` (via the `CloudEventMapping.SubjectAttribute` sentinel), or any other extension name |
| `SessionId` | extension attribute `sessionid` | `subject`, or any other extension name |

Custom extension attributes beyond correlation/session are supported via
`CloudEventPublisherOptions.Extensions`, an
`Action<IEvent, IDictionary<string, string>>` hook invoked before the mapped
correlation/session values are written (so a caller cannot accidentally shadow
them).

### Opt-in per-endpoint model, not a platform-wide switch

`CloudEventPublisherOptions` / `CloudEventSubscriberOptions`
(`src/NimBus.SDK/Extensions/`) hang off `NimBusPublisherOptions.CloudEvents`
/ `NimBusSubscriberOptions.CloudEvents`, both `null` unless `UseCloudEvents`
is called. `PublisherClient` only builds a `CloudEventPublishContext`
(`src/NimBus.SDK/PublisherClient.cs`, `BuildCloudEvent`) when the publisher's
options carry a non-null `CloudEvents` block — native routing metadata
(`To`, `From`, `EventTypeId`, `MessageId`, `SessionId`, `CorrelationId`, trace
context) is built exactly as before and is *never* replaced by the CloudEvent
projection; the CloudEvent is an additional view over the same message, not a
different message.

On the consume side, `CompatibilityMode`
(`src/NimBus.Core/CloudEvents/CompatibilityMode.cs`) gives four choices:
`NimBusNative` (default — no CloudEvents parsing), `CloudEventsBinary`,
`CloudEventsStructuredJson`, or `AutoDetect` (mixed native/CloudEvents traffic
on the same subscription, detected per message — the recommended mode for a
subscription that receives from both NimBus and external producers).

### Consume-side detection and the `ce-` alternate prefix

`CloudEventReadOptions.DefaultAcceptedPrefixes` is `["cloudEvents:", "ce-"]`.
NimBus always **writes** the canonical `cloudEvents:` prefix (the standard AMQP
binding prefix) but **accepts** the widely-used alternate `ce-` prefix on
consume, because several non-Microsoft CloudEvents SDKs emit AMQP application
properties under `ce-` rather than `cloudEvents:`. This is a read-side-only
compatibility affordance — NimBus's own publisher never emits `ce-`. The
accepted prefix list is configurable per subscriber
(`CloudEventSubscriberOptions.AcceptedPrefixes`) for producers that use a
different convention entirely.

### Dead-letter behavior for invalid/unknown CloudEvents

`CloudEventValidatingContextHandler`
(`src/NimBus.SDK/EventHandlers/CloudEventValidatingContextHandler.cs`) decorates
the handler pipeline only when a subscriber has `UseCloudEvents` enabled. For
every inbound message detected as a CloudEvent (native messages pass through
unchanged), it validates the four required attributes (`id`, `source`,
`type`, `specversion == "1.0"`) and that `type` resolves to a registered event
contract. Any violation raises `InvalidCloudEventException`
(`src/NimBus.Core/CloudEvents/InvalidCloudEventException.cs`) wrapped in
`PermanentFailureException`, which the existing permanent-failure path
dead-letters with the exception message as the inspectable reason — the same
path used for other unrecoverable NimBus errors. This was a deliberate choice
over two alternatives:

- **Silently dropping invalid CloudEvents** — rejected outright; NimBus's
  audit-first design (ADR-002) never silently drops a message.
- **Treating an unknown `type` like a native unknown `EventTypeId`** (send an
  `UnsupportedResponse` instead of dead-lettering) — rejected because a native
  unknown type is a *routing* configuration gap NimBus itself created
  (topology provisioning didn't wire a consumer for it), whereas an unknown
  CloudEvents `type` usually means an external producer sent something this
  endpoint was never meant to receive. Dead-lettering with a clear
  `InvalidCloudEventException` reason gives operators a directly actionable
  signal instead of a silent `Unsupported` audit row.

### AsyncAPI export

`nb catalog asyncapi` (`src/NimBus.CommandLine/AsyncApiExporter.cs`,
documented in `docs/asyncapi-mapping.md`) reflects CloudEvents opt-in through an
explicit, exporter-visible source of truth. Because the exporter is
platform-model-driven (ADR-007) while `UseCloudEvents` is a DI-registration
concern the exporter never sees, an endpoint declares its CloudEvents
participation by implementing the optional `ICloudEventsAware` interface
(`NimBus.Core.Endpoints`) on its endpoint definition. When an endpoint
implements it, the exporter emits an `x-cloudevents` extension on that channel
(recording `specversion`, content mode, `source`, and the CloudEvents attribute
set) and a shared `CloudEventsMessageHeaders` schema under `components.schemas`.
Endpoints that do not implement `ICloudEventsAware` produce byte-identical
output to before this change, so existing catalogs are unaffected. Keeping the
opt-in signal in the platform model (rather than inferring it from the runtime
`UseCloudEvents` call) is deliberate: it keeps the exporter deterministic and
lets a catalog author document CloudEvents intent independently of the hosting
process's DI wiring.

## Consequences

### Positive

- Native NimBus traffic is provably unaffected — `Message.CloudEvent` is
  `null` unless a publisher explicitly opts in, so existing deployments see
  zero wire-format or behavior change.
- Partner systems and generic CloudEvents tooling can produce into, and
  consume from, a NimBus-managed Service Bus topology without linking
  `NimBus.SDK` — they only need to speak the CloudEvents 1.0 AMQP binding.
  `samples/CloudEventsInterop` demonstrates this end-to-end, including a
  consumer built against `Azure.Messaging.ServiceBus` alone.
- Sessions, retry, dead-lettering, deferred processing, and message tracking
  all continue to work unmodified, because CloudEvents is layered over the
  native routing metadata rather than replacing it (see "Opt-in per-endpoint
  model" above).
- Invalid or unroutable CloudEvents fail loud and inspectable via the existing
  dead-letter/audit path — no silent drops, no new failure taxonomy to learn.
- The mapping for the two fields with no natural CloudEvents home
  (`CorrelationId`, `SessionId`) is explicit and overridable
  (`CloudEventMapping`), rather than a hardcoded convention adapter authors
  have to work around.

### Negative

- `AsyncApiExporter` only marks an endpoint as CloudEvents-enabled when its
  definition implements `ICloudEventsAware`; a producer that calls
  `UseCloudEvents` at DI time but whose endpoint model does not implement the
  interface will not be annotated. This is the intended trade-off (the exporter
  is platform-model-driven, not DI-driven — see "AsyncAPI export" above), but it
  means catalog authors must implement the interface to surface CloudEvents
  intent in the export.
- The CloudEvents `type` attribute and the native `EventTypeId` used for
  routing/tracking can diverge: `EventTypeId` is always the unqualified CLR
  class name (`PublisherClient.GetMessageStatic`), while
  `CloudEventTypeNameStrategy.FullName` or a `TypeOverride` produces a
  different `type` string on the wire. Routing, tracking, and audit always use
  the native `EventTypeId`; only `context.GetCloudEvent().Type` reflects the
  configured CloudEvents strategy. This is documented behavior
  (`docs/cloudevents.md`), not a bug, but it is a easy detail to miss.
- The audit record (`MessageAuditEntity`) gains optional `CloudEventId`,
  `CloudEventSource`, `CloudEventType`, and `CloudEventSubject` fields, so the
  CloudEvents `id`/`source`/`type`/`subject` are preserved in tracking where the
  store supports it (verified against the in-memory store; they default to
  `null` for native rows). `dataschema` and custom extension attributes remain
  inspectable only at handler time via `context.GetCloudEvent()`, and the
  WebApp does not yet render a dedicated CloudEvents surface — under the default
  mapping the existing native fields already carry the `id`/`type`/
  `correlationid`/`sessionid` values an operator needs.
- One more decorator (`CloudEventValidatingContextHandler`) and one more
  branch in the consume path for CloudEvents-enabled subscribers to reason
  about; native-only subscribers are unaffected (the decorator is not
  constructed unless `UseCloudEvents` was called).

### Operational

- Adapters that need interoperability enable it endpoint-by-endpoint; the
  rest of the platform (Resolver, WebApp, CLI, storage providers) requires no
  changes to support a CloudEvents-enabled endpoint alongside native ones.
- Operators debugging a dead-lettered CloudEvent get the specific missing/
  invalid attribute or the unresolved `type` in the dead-letter reason, not a
  generic parse failure.

## See Also

- `docs/cloudevents.md` — usage guide: publishing, consuming, the mapping
  table, configuring `CloudEventMapping`, and behavior with sessions/retry/
  dead-lettering/deferred messages/tracking.
- `docs/asyncapi-mapping.md` / ADR-007 — the export pipeline, which reflects
  CloudEvents-enabled endpoints via `ICloudEventsAware` (see "AsyncAPI export"
  above).
- ADR-001: Session-based ordering — the native routing metadata CloudEvents
  layers on top of, never replaces.
- ADR-002: Centralized Resolver — the audit-trail contract CloudEvents
  publish/consume still feeds via native `EventTypeId`/`MessageId`/
  `CorrelationId`/`SessionId`.
- `samples/CloudEventsInterop` — worked example: NimBus publishes a
  CloudEvent, a NimBus subscriber consumes it via `context.GetCloudEvent()`,
  and a plain `Azure.Messaging.ServiceBus` consumer (no NimBus dependency)
  reads the same wire message.
