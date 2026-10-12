# NimBus → AsyncAPI mapping

`nb asyncapi export` (and its backward-compatible alias `nb catalog asyncapi`) generates an
[AsyncAPI 3.0](https://www.asyncapi.com/) document from a NimBus `IPlatform`. The
`nb asyncapi validate` and `nb asyncapi diff` subcommands turn that document into a CI/CD
governance gate (see [`docs/cli.md`](cli.md#nb-asyncapi)). This page explains how NimBus concepts
map to AsyncAPI, and — importantly — how the document represents NimBus's **real Azure Service Bus
topology** rather than a simplified "one topic per event" model.

## The topology being described

NimBus does **not** use a topic per event category. Its runtime shape (created by
`ServiceBusTopologyProvisioner`) is:

- **Topic per endpoint.** Every endpoint gets one Service Bus topic named after its id.
- **Routing by SQL rules on application properties** — `user.To`, `user.From`, `user.EventTypeId`
  (see `NimBus.Core/Messages/UserPropertyName.cs`), not by topic name.
- **Auto-forwarding between topics.** A producer publishes only to its *own* topic
  (`user.To` = event class name, `user.From` = null). For each consumer, a forward subscription on
  the producer topic — rule `user.EventTypeId = 'X' AND user.From IS NULL`, action
  `SET user.From=<producer>; SET user.EventId=newid(); SET user.To=<consumer>` — auto-forwards a
  rewritten copy into the consumer's topic.
- **Consumers read their own topic.** Delivery is a **session-required** subscription named after
  the endpoint, filtered `user.To = '<endpoint>'`. The consumer never subscribes to the producer.

## Why specification extensions (no `servicebus` binding)

AsyncAPI has no official Azure Service Bus [binding](https://github.com/asyncapi/bindings) — the
`amqp1` binding is a v0.1.0 empty placeholder, and Service Bus speaks AMQP 1.0. So the document is
a **hybrid**: portable logical channels/operations (what developer portals expect) enriched with
Service Bus specifics carried in `x-servicebus*` / `x-nimbus*` **specification extensions**. The
server declares `protocol: amqp` and an empty `amqp1` binding for tooling that keys off it.

## Exporting the document

Two ways to produce the same full-platform document:

- **CLI** — `nb catalog asyncapi` writes it to a file (`.json` ⇒ JSON, else YAML).
- **Management WebApp** — the **Topology** page's header offers
  **Download YAML** and **Download JSON** buttons. The endpoint is
  `GET /api/admin/asyncapi?format=yaml|json` (missing/empty `format` defaults to YAML; any other
  value is a `400`). It is site **Owner** only — the same gate as the rest of `/api/admin`,
  including `GET /api/admin/platform-config` — and returns the document as a `nimbus-asyncapi.yaml` /
  `nimbus-asyncapi.json` attachment. Both paths reuse the one exporter
  (`NimBus.ServiceBus.AsyncApi.AsyncApiExporter`), so the WebApp download and the CLI output are
  identical for the same platform.

## Concept mapping

| NimBus concept | AsyncAPI construct |
|---|---|
| Service Bus namespace | `servers.production` (`protocol: amqp`, host `{namespace}.servicebus.windows.net`) |
| Topology (topic-per-endpoint, SQL routing, auto-forward, Resolver/Deferred subs) | `servers.production.x-nimbus-topology` |
| Endpoint topic | `channels.<endpointId>` (`address` = topic; `x-servicebus.resourceType: topic`) |
| Event type appearing on a topic (produced here, or forwarded in) | entry in `channels.<endpointId>.messages` |
| Producer publishes an event | `operations.<producer>_send_<event>` (`action: send`) — **one per producer** |
| Consumer consumes an event | `operations.<consumer>_receive_<event>` (`action: receive`) |
| Consumer's session delivery subscription | `operations.<...>_receive_<...>.x-servicebus-delivery.deliverySubscription` (`requiresSession: true`, filter `user.To = '<endpoint>'`) |
| Auto-forward subscription(s) on producer topic(s) | `...x-servicebus-delivery.forwardSubscriptions[]` (topic, subscription, `forwardTo`, filter, action) |
| Event contract (`IEventType` / CLR class) | `components.messages.<event>` + `components.schemas.<event>` |
| `user.*` application properties | `components.schemas.NimBusMessageHeaders`, referenced by every message's `headers` |
| `[SessionKey]`, MessageId/CorrelationId conventions, session/dead-letter | `components.messages.<event>.x-servicebus` |
| `[Description]` / `[AsyncApiMessage]` on an event | message `title` / `summary` / `description` / `tags` |
| `[AsyncApiMessage(Name=…)]` / fluent `o.AsyncApi.Name` | message `name` **and** payload schema `title` (component key stays the event id) |
| `Owner` / `Team` / `BusinessCapability` / `Version` | message `x-nimbus-governance.*` (no standard AsyncAPI slot) |
| `ExternalDocsUrl` / `ExternalDocsDescription` | message `externalDocs.url` / `externalDocs.description` |
| `Deprecated` | payload schema `deprecated: true` (the AsyncAPI/JSON-Schema marker), mirrored as `x-nimbus-governance.deprecated` |
| `Event`'s static example instance (`GetEventExample()`) + fluent `o.AsyncApi.Examples` | message `examples[]` (derived `sample` first, then fluent examples) |
| `DynamicForward` (spec 022) | message flagged `x-nimbus-dynamic` + its send/receive operations |

## Enrichment: attributes and fluent per-publish config

Enrichment can be supplied two ways, and they are merged into one document:

- **Attribute** — `[AsyncApiMessage(Title=…, Summary=…, Description=…, Tags=…, Name=…, Owner=…, Team=…, BusinessCapability=…, Version=…, Deprecated=…, ExternalDocsUrl=…, ExternalDocsDescription=…)]` on the event class.
- **Fluent** — at publisher registration:
  ```csharp
  services.AddNimBusPublisher("StorefrontEndpoint", b => b
      .Publish<OrderPlaced>(o =>
      {
          o.AsyncApi.Title = "Order placed";
          o.AsyncApi.Owner = "commerce";
          o.AsyncApi.Tags.Add("CRM");
          o.AsyncApi.Examples.Add(new AsyncApiMessageExample { Name = "typical", Payload = new { orderId = "…" } });
      }));
  ```
  The fluent call records metadata only — it never changes the send path.

**Merge rule (deterministic).** When both are present for the same event: scalars use **fluent → attribute → derived default**; `Tags` are **unioned** (first-seen, de-duped); `Examples` are the derived `sample` followed by fluent examples; `Deprecated` is **OR-ed**.

### The fluent → document bridge

Fluent enrichment is imperative container state (recorded in an `AsyncApiEnrichmentRegistry` by the
`Publish<T>` calls), so it is exported by the **host that registered the publishers**: call
`AddNimBusAsyncApiDocument(platform, (p, f, r) => AsyncApiExporter.Serialize(p, f, r))`, then resolve
`IAsyncApiDocumentProvider` and call `GetDocument(format)`. The provider reads the same registry, so
fluent values appear in that host's exported document. `AsyncApiFormat`, `AsyncApiEnrichmentRegistry`,
and `IAsyncApiDocumentProvider` live in `NimBus.Abstractions` (ns `NimBus.Core.Events`) so the SDK,
CLI, and consumers share them without any project depending on `NimBus.CommandLine`.

Three surfaces expose fluent enrichment:

1. **`nb asyncapi export --assembly <host.dll> [--provider <Type>]`** — the CLI loads the host
   assembly (via `Assembly.LoadFrom`, the same convention the WebApp uses to load an `IPlatform`) and
   resolves the provider it exposes, then writes `GetDocument(format)`. Because
   `AddNimBusAsyncApiDocument` registers a **private, DI-backed** `IAsyncApiDocumentProvider` (it has
   constructor dependencies the standalone CLI cannot instantiate), the host bridges to it with a
   public parameterless **`IAsyncApiDocumentProviderFactory`** whose `Create()` builds the container
   and resolves the provider from it; the CLI also accepts a directly-exposed public parameterless
   `IAsyncApiDocumentProvider`. This is the CLI path to a **fluent-enriched** document. Without
   `--assembly` the CLI exports the static built-in `PlatformConfiguration`, which surfaces
   **attribute** enrichment only.
2. **In-process** — any host that called `AddNimBusAsyncApiDocument(...)` resolves
   `IAsyncApiDocumentProvider` from its own container.

A management-UI download of the enriched document (issue capability #6) is a follow-up — see
[Notes & limits](#notes--limits).

## Validation & diff (governance)

- **`nb asyncapi validate`** is **section-aware**: each `$ref` must resolve to the correct AsyncAPI
  section for its context (operation → channel; operation message → channel-scoped message →
  component message, or directly to component message; channel message → component message; message
  `payload`/`headers` → a component **schema**).
  A payload `$ref` into `#/components/messages` is rejected even though the node exists.
- **`nb asyncapi diff`** classifies added/removed/changed channels (including a channel message whose
  `$ref` is retargeted to a different component message — breaking), operations, messages, and schemas
  (down to schema properties, including a property's effective shape, enum values on properties or
  array `items`, `[Range]`-derived `minimum`/`maximum` bounds, and `description` metadata) and flags
  breaking changes for build gating.
  A tightened bound is breaking; a relaxed/removed bound and metadata edits are non-breaking but still
  reported (so a metadata-only delta never shows as "No differences"). See
  [`docs/cli.md`](cli.md#nb-asyncapi-diff-old-file-new-file) for the exact breaking-change list.

## Schema generation

Schemas are reflected from the CLR event type (`IEventType.GetEventClassType()`):

- **Formats** — `Guid` → `string`/`uuid`; `DateTime`/`DateTimeOffset` → `string`/`date-time`;
  integers → `integer`; `decimal`/`double`/`float` → `number`; `bool` → `boolean`.
- **Enums** → `string` with an `enum` list. **Collections** → `array` with `items`. **Nested
  objects** → `$ref` to a registered schema (recursively).
- **Required** — a property is required if it has `[Required]` **or** is non-nullable (value types,
  and NRT-annotated reference types). `[Range]` maps to `minimum`/`maximum`.

## Notes & limits

- `EventTypeId` is the unqualified CLR class name and is global to the namespace; two different
  classes with the same name collide (documented hazard) — the exporter emits both producers'
  `send` operations but the shared message/schema key resolves to one type.
- Dynamically-typed events have no compiled contract, so they get no payload schema.
- Not yet included (follow-ups): contract-first validation (verifying implemented
  publishers/subscribers match a supplied contract) and XML-doc-comment-sourced schema
  descriptions (today only `[Description]` attributes are read).

See [ADR-007](adr/007-code-first-catalog-export.md) for the decision record and
[`docs/cli.md`](cli.md#nb-catalog-asyncapi) for command usage.
