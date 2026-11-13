# Spec 045: Route events on a routing key, not only on event type

Status: phase 1 implemented (2026-11-13), revision 2. Revised after an adversarial review of
revision 1; the review log at the end maps each finding to the section that addresses it. The
implementation plan is [docs/plan/2026-11-13-spec-045-routing-keys.md](../../plan/2026-11-13-spec-045-routing-keys.md);
the user guide is [docs/routing-keys.md](../../routing-keys.md).

## Problem

NimBus routes an event to a consumer by event type alone. A consumer's forward rule on the
producer's topic is

```sql
user.EventTypeId = 'CustomerCreated' AND user.MessageType = 'EventRequest' AND user.From IS NULL
```

(`TopologyDescriptor.ForwardFilter`). Every endpoint that consumes `CustomerCreated` gets every
`CustomerCreated`. The publisher can't add a property to route on either: `MessageHelper` stamps
NimBus's own fixed set of application properties and nothing else.

That breaks down when one event type has several targets of the same kind and the target is
decided by the data. The case that prompted this spec:

- CRM publishes `CustomerCreated`.
- The customer runs 10+ Business Central instances, one per country. The contract is the same in
  every country; a field in the payload says which instance the customer belongs to.
- One ERP adapter codebase serves every instance.

The options today are both poor:

| Option | Problem |
|---|---|
| One `Erp` endpoint that picks the BC instance in the handler | No isolation between instances: the endpoint circuit breaker, scaling, rate limits and WebApp view are shared, so one BC outage pauses every country. |
| One endpoint per instance | Every endpoint receives every event and must discard the ones for other countries: 10× the deliveries, and the Resolver records 9 no-op completions per event. |

A router endpoint that republishes `CustomerCreatedDk`, `CustomerCreatedSe` and so on is worse:
`EventTypeId` is the global unqualified class name, so every instance adds a contract and a hop.

Commands and requests have the same problem, and harder: they travel the same forward rules as
events (`Command.cs`, `PublisherClient.Request`), and `PlatformValidation.ValidateCommandConsumers`
requires exactly one consumer, so a command consumed by ten instance endpoints fails
`nb topology apply` today.

Service Bus already routes on message properties with SQL filters. NimBus needs to let a publisher
stamp one routing property and let a consumer filter on it.

## Decisions

Proposed in this revision. Each needs confirming before implementation.

| Topic | Decision |
|---|---|
| Wrong delivery | **The receiving endpoint checks the key itself** (section 5). A filter on the bus is not trusted on its own: anything that rebuilds a rule with older code removes it silently. |
| Unmatched events | **One unmatched consumer per keyed event type is required**, unless the event opts out with `DropUnmatched`. Missing it is a validation error, not a warning. |
| Unmatched rule shape | **One rule with `OR`**, under the existing rule name. No second rule, so `nb topology apply` never has to delete a rule. |
| Rule changes | **Widen before narrow**, in two passes per producer topic (section 4). |
| Commands and requests | **In scope.** A command needs exactly one consumer per key. |
| Case | **Keys are case-sensitive and compared ordinally** in filters, the receive check and the emulator. Declared keys that differ only by case are rejected. |
| Key type | **String properties only.** A property marked `[Sensitive]` can't be a routing key. |
| Recovery of a stray | **Phase 1: manual** (correct at the source, skip the stray). **Phase 2: a Re-route action.** |
| Stored key | **Not stored** on tracking rows in this spec. The receive check derives the key from the payload instead. |

## Goals

- An event, command or request can declare a **routing key**, taken from its payload, that NimBus
  stamps on the message as the application property `RoutingKey`.
- A consumer can declare that it takes an event type **only for given routing keys**. Its forward
  rule filters on them, so other instances don't receive the message.
- A message that reaches an endpoint with a key the endpoint did not declare is **dead-lettered
  with a clear reason, never handled**.
- An **unmatched consumer** receives the events whose key no consumer claims, and the events with
  no key. A mistyped key or a country that isn't provisioned yet is visible, not lost.
- Changing which endpoint takes which key never opens a window in which a message matches nobody.
- Consumers that declare no keys behave exactly as today, with byte-identical rules, so
  `nb topology apply` changes nothing on an existing platform.
- The same adapter code can run as several endpoints, differing only in configuration and a small
  endpoint class.

## Non-goals

- **Several keys on one message.** A message carries at most one routing key. An event for several
  instances is published once per instance (see Open questions).
- **Arbitrary payload filters.** The key is one declared property, not an expression over the
  payload.
- **Config-driven endpoint instances.** `Endpoint.Id` is the class name (`GetType().Name`), so an
  endpoint per instance is a class per instance (see Open questions).
- **Storing the key on tracking rows.** No storage-interface change, and no provider or conformance
  work. The WebApp therefore can't filter stored messages by key; each instance is its own endpoint,
  which already separates them.
- **Keyed dynamic forwards.** A `DynamicForward` (Spec 022) keeps today's filter: its target
  receives every key and claims none.
- **Removing stale forward rules in general.** `nb topology apply` still doesn't delete a rule for
  an event type an endpoint stopped consuming; `nb endpoint topics removeDeprecated` remains the
  tool for that. This spec is designed so it never needs a rule deleted.
- **Fewer rules per consumer.** One rule per consumer and event type stays (see Capacity).

## Design

### 1. Declaring the key on a message class

Mirror `[SessionKey]`:

```csharp
[RoutingKey(nameof(ErpInstance))]
[SessionKey(nameof(CustomerNo))]
public sealed class CustomerCreated : Event
{
    public required string CustomerNo { get; init; }
    public required string ErpInstance { get; init; }
    // …
}
```

- `RoutingKeyAttribute` (class-level, inherited, one per class) in `NimBus.Core.Events`, with one
  option: `DropUnmatched` (default `false`, see section 8).
- The attributed property **must be of type `string`**. An enum or number would make the key depend
  on `ToString()` while the JSON carries something else. For a key computed from other types,
  override `GetRoutingKey()`.
- The attributed property **must not be `[Sensitive]`**, directly or through a class-level marking:
  the key travels as a plain application property into the broker, traces and dead-letter reasons.
- `Event` gets `public virtual string? GetRoutingKey()`. By default it reads the attributed
  property and returns `null` when there is no attribute, or when the value is null or empty. An
  unknown, non-string or sensitive property throws `InvalidOperationException`, as an unknown
  `[SessionKey]` property does.
- `IEvent` is **not** changed. The publisher and the receive check ask a new optional interface,
  `IRoutingKeyProvider { string? GetRoutingKey(); }`, which `Event` implements. `Command` derives
  from `Event`, so commands get it too.
- `IEventType` gets `string? RoutingKeyProperty => null` (default member); `EventType` implements
  it from the attribute, as the concrete `EventType` exposes `SessionKeyProperty` (which is not on
  the interface). It's used by exporters, the simulator and validation.

### 2. Carrying the key on the message

`MessageHelper.ToServiceBusMessage` takes `IMessage`, and received messages are `IReceivedMessage :
IMessage`, so the key belongs on the interface:

- `IMessage` gets `string? RoutingKey => null` (default member, so external implementers compile).
  A missing override means "no key", which routes to the unmatched consumer: visible, never a wrong
  delivery. In-repo implementers are all updated: `Message` (settable), `MessageContext`
  (reads the application property; null when absent) and `InMemoryMessageContext`.
- `UserPropertyName.RoutingKey` is added. `MessageHelper` writes the application property only when
  the key is non-empty, so a message without a key looks exactly as it does today.

Every path that builds a message from an event goes through `PublisherClient.GetMessage` or
`GetMessageStatic`; both set `RoutingKey` from `IRoutingKeyProvider`. That covers `Publish` (all
overloads), `PublishFromContext`, `PublishBatch`, `PublishBatches`, `Schedule` and `Request`.

- **`Publish(IMessage)`** sends a caller-built message and never sees the event. The caller sets
  `Message.RoutingKey`; the XML doc says so.
- **CloudEvents mode:** `MessageHelper` writes the native properties and then shapes the body. A
  test covers both content modes, so the property is proven present.
- **Outbox:** `OutboxSender` serialises the whole message into `Payload` and `OutboxDispatcher`
  deserialises it, so the key survives with no schema change.
- **Deferred replay, retry and resubmit** are addressed to the endpoint with `To`, not through a
  forwarder. A resubmit (`ManagerClient.Resubmit`) builds a new message from the stored row and so
  carries **no** `RoutingKey` property. That is why the receive check works from the payload
  (section 5).
- **Publish-side values are not validated** beyond null or empty. A property value never becomes
  SQL text; only keys declared by consumers are interpolated into filters. An unexpected value
  routes to the unmatched consumer.

### 3. Declaring keys on a consumer

```csharp
public abstract class ErpInstanceEndpoint : Endpoint
{
    protected ErpInstanceEndpoint(string instance)
    {
        Consumes<CustomerCreated>(routingKeys: instance);
        Consumes<CustomerUpdated>(routingKeys: instance);
    }
}

public sealed class ErpInstance01 : ErpInstanceEndpoint { public ErpInstance01() : base("01") { } }
public sealed class ErpInstance02 : ErpInstanceEndpoint { public ErpInstance02() : base("02") { } }

public sealed class ErpUnrouted : Endpoint
{
    public ErpUnrouted()
    {
        ConsumesUnmatched<CustomerCreated>();
        ConsumesUnmatched<CustomerUpdated>();
    }
}
```

- `Endpoint` gets two overloads:
  - `protected void Consumes<TEvent>(params string[] routingKeys)`. A call without arguments still
    binds to today's `Consumes<TEvent>()`. An explicitly passed empty array, or a null or empty key,
    throws: a key that comes from configuration and turns out empty must never degrade to "all
    keys".
  - `protected void ConsumesUnmatched<TEvent>()`.
- `IEndpoint` gets two members with default implementations, so existing implementers compile:

  ```csharp
  IReadOnlyCollection<string> GetRoutingKeys(IEventType eventType) => Array.Empty<string>();
  bool IsUnmatchedConsumer(IEventType eventType) => false;
  ```

  `Endpoint` (the only in-repo implementer) overrides both.
- `IPlatform` gets one member, used wherever the full set of claimed keys is needed:

  ```csharp
  IReadOnlyCollection<string> GetClaimedRoutingKeys(IEventType eventType);   // default: union over GetConsumers
  ```

  `SingleEndpointPlatformView` narrows `GetConsumers` to one endpoint, so it **must forward this
  member to the inner platform**; otherwise a per-service export computes the wrong exclusion list.
  A test covers it.
- `EventTypesConsumed` still lists the event type, so `GetConsumers` and everything built on it
  keep working.

### 4. Topology

The provisioner, the WebApp's subscription rebuild and `nb endpoint topics removeDeprecated` all
read `TopologyDescriptor`, so rules are defined there once.

**Rules.** Every consumer keeps exactly one rule per event type, under today's name (the event
type id). Only the filter text changes; `ForwardAction` is unchanged.

| Consumer declares | Filter |
|---|---|
| no keys | `ForwardFilter(eventTypeId)`, unchanged byte for byte |
| one key | `ForwardFilter(eventTypeId) AND user.RoutingKey = '01'` |
| several keys | `ForwardFilter(eventTypeId) AND user.RoutingKey IN ('01', '02')` |
| unmatched, and some consumer claims keys | `ForwardFilter(eventTypeId) AND (user.RoutingKey IS NULL OR user.RoutingKey NOT IN ('01', '02'))` |
| unmatched, and no consumer claims keys | `ForwardFilter(eventTypeId)`, unchanged |

- The exclusion list is `platform.GetClaimedRoutingKeys(eventType)`.
- The last row is what keeps keyed events flowing between "the publisher starts stamping keys" and
  "the first instance endpoint exists".
- `IS NULL OR NOT IN` is correct whatever Service Bus returns for `NOT IN` on a missing property:
  the `IS NULL` branch already decides that case.
- Keys are written in ordinal order, so the text is stable and `RuleMatches` doesn't churn.
- Because the rule name never changes, a real namespace updates the rule in place
  (`UpdateRuleAsync`), and no rule ever has to be deleted.

**Changing rules without losing messages.** `ApplyCoreAsync` ensures a topic's forwarders in
consumer-id order, so today the outcome of moving a key depends on endpoint names. The rule for
this spec: *no rule is narrowed before the rule taking over its messages has been widened.* For
each producer topic, `nb topology apply` runs two passes over the forward rules whose key set
changes:

1. **Widen.** Create new subscriptions and rules. Set each changed consumer rule to the union of
   its old and new keys, and each changed unmatched rule to exclude only the keys claimed both
   before and after.
2. **Narrow.** Set every rule to its final filter.

The old key set is read back from the existing filter, which NimBus wrote in a canonical form
(`TopologyDescriptor.TryReadRoutingKeys`). A filter that isn't in that form is treated as the
widest case: no key restriction for a consumer rule, no exclusions for the unmatched rule. When
nothing changes, both passes are no-ops and no calls are made.

The consequence is a short overlap, never a gap: while a key moves, its events can reach both the
old and the new owner, or both an instance and the unmatched consumer. The receive check
(section 5) and the built-in unmatched handler (section 6) make the extra copy harmless: it's
dead-lettered with a reason, not handled.

- **Deploy before apply.** The receive check reads the keys from the endpoint class in the
  adapter's own build, so the adapter with the new keys is deployed before the topology is applied.
- **Emulators** replace a rule by delete-then-create (existing behaviour), which has its own brief
  gap. That is accepted for development.
- **Paused and purging forwarders.** `EnsureSubscriptionAsync` leaves the subscription properties
  of a paused or purge-marked forwarder alone but still ensures its rules. That stays: both passes
  apply to them, so a paused forwarder collects exactly what it would forward.

**Limits.** A SQL filter is at most 1,024 characters and a rule name at most 50. Validation
(section 8) rejects a key set that would exceed the filter limit, naming the event type and
endpoint. Rule names don't change, so the name limit is no tighter than today.

**Capacity.** Every consumer and event type is one SQL filter on the producer's topic. An endpoint
topic carries 8 fixed filters (own subscription 3, reply 1, Resolver 2, Deferred 1,
DeferredProcessor 1), so a producer topic holds

```
8 + (instances + 1 unmatched + other consumers) × routed event types
```

| Case | Filters on the CRM topic | Against the limit |
|---|---|---|
| 10 instances, 5 routed types | 63 | under both limits |
| 10 instances, 6 routed types | 74 | over the official emulator's 70; real namespace allows 2,000 |
| 10 instances, 20 routed types | 228 | real namespace only |

- The official emulator therefore can't host the customer topology beyond five routed types; the
  in-repo emulator (the CrmErpDemo default) has no such cap.
- On the Standard tier, SQL filters add to throttling cost whether or not they match. A load test
  at the customer's expected publish rate, on the tier they will run, is part of acceptance.
- Collapsing a consumer's rules into one per consumer would cut the count but rename existing
  rules. It is left as a later option.

### 5. Checking the key on receive

A filter on the bus is one layer. It disappears, with no error, whenever a rule is rebuilt by code
that predates this spec: an older `nb`, or an older WebApp rebuilding a subscription. Every
instance would then receive every customer. The key can also disagree with the payload: after a
resubmit with changes, with an external producer, or with a hand-built message.

So the subscriber checks, before the handler runs:

1. If the endpoint declares routing keys for the event type and the deserialised event is an
   `IRoutingKeyProvider`, the key **derived from the payload** must be one the endpoint declared.
2. If the message also carries a `RoutingKey` property, it must equal the payload-derived key.

A failure throws `PermanentFailureException`, so the message is dead-lettered (it doesn't block
its session) with a reason such as `Routing key '02' is not accepted by endpoint ErpInstance01
(accepts: 01)`. Comparison is ordinal.

- The check runs from the payload because resubmitted messages carry no property (section 2), and
  because it then also covers a payload edited during resubmit.
- The unmatched consumer accepts every key; rule 2 still applies to it.
- A handler registered by string key has no CLR type to derive a payload key from, so neither
  rule applies as written. For it the check is that the `RoutingKey` property, when present, is one
  of the keys the endpoint declares for that event type id. This is documented.

Where the declared keys come from:

- **Automatically**, when an `IPlatform` is registered in the container: the subscriber finds its
  endpoint by `NimBusSubscriberOptions.Endpoint`.
- **Explicitly** otherwise: `subscriber.EnforceRoutingKeys<ErpInstance01>()` or
  `subscriber.EnforceRoutingKeys("01")`.

An adapter that does neither has no check. The multi-instance guide in `docs/routing-keys.md` sets
it up in every example and states that an instance endpoint without the check relies on the bus
filter alone.

### 6. Unmatched events

The unmatched consumer is an ordinary endpoint, so its messages show in the WebApp, the Resolver
audit and the MCP tools like any other.

**Built-in handler.** NimBus ships `UnmatchedRoutingKeyHandler<TEvent>`, registered with
`subscriber.AddUnmatchedHandler<TEvent>()`. It throws `PermanentFailureException` with the reason
`No endpoint consumes CustomerCreated with routing key '99'` (or `with no routing key`). No adapter
code is needed, and a second copy that arrives during a key move (section 4) ends the same way.

**Recovery, phase 1 (manual).** A dead-lettered stray is one of two things:

| Cause | What the operator does |
|---|---|
| Wrong key in the source data | Correct it in the source system, which publishes a new event; skip the stray. |
| The key's instance isn't provisioned yet | Provision it, then have the source republish; skip the stray. |

Resubmit doesn't help: it returns the message to the unmatched endpoint by `To`. The guide says so,
and says to apply the topology for a new instance **before** the source starts sending its key.

**Recovery, phase 2 (Re-route).** A WebApp action (and MCP tool) on a dead-lettered message of an
unmatched endpoint: the operator picks a claimed key, optionally edits the payload, and NimBus
sends the event directly to the endpoint that claims that key, then marks the stray skipped with
the target recorded. The target's receive check validates the result. The mechanism builds on
`ManagerClient.Resubmit`, which already takes a target endpoint; how an endpoint and the Resolver
treat a resubmission for an event they have no row for must be established before this phase is
planned.

### 7. Commands and requests

Both use the forward rules of section 4 unchanged. What changes is validation.

`ValidateCommandConsumers` accepts a command type in one of two shapes:

- **Unkeyed:** exactly one consumer, declaring no keys (today's rule).
- **Keyed:** the command class provides a routing key (attribute, or `GetRoutingKey` overridden);
  every consumer is keyed or unmatched; each key is claimed by exactly one consumer; and there is
  exactly one unmatched consumer. `DropUnmatched` is not allowed on a command.

A request has no marker type, so it can't be validated this way. A keyed request that no consumer
matches reaches the unmatched consumer, which dead-letters it; the caller sees its timeout. The
guide says so. Replies are addressed with `To` and are unaffected.

### 8. Validation

`PlatformValidation` gains `ValidateRoutingKeys(IPlatform)`, returning errors like
`ValidateCommandConsumers`, and `EnsureValid(IPlatform)`, which runs both. The one existing call
site, `ServiceBusTopologyProvisioner.ApplyAsync`, calls `EnsureValid`. There are no warnings and no
start-up validation in the SDK.

Errors:

- A declared key that doesn't match `^[A-Za-z0-9_.-]{1,64}$`. This is what makes interpolating
  declared keys into SQL safe; `TopologyDescriptor` checks it again when it builds the filter.
- Two declared keys for one event type that differ only by case.
- An endpoint that declares keys and unmatched for the same type, or declares a type twice.
- A keyed event type with no unmatched consumer, unless its class sets
  `[RoutingKey(…, DropUnmatched = true)]`. With the opt-out, events with an unclaimed key are
  dropped by Service Bus, as events with no consumer are today.
- A consumer declaring keys for an event type whose class provides no routing key (the consumer
  could never receive anything). Skipped for event types without a CLR type.
- A routing-key property that is not a `string`, or is `[Sensitive]`.
- A filter that would exceed 1,024 characters.
- The command rules of section 7.

Allowed: two consumers of an *event* claiming the same key, so a reporting endpoint can take DK
too. Each gets its own copy, as with fan-out today.

### 9. Emulators

- **In-repo emulator (Spec 027).** `SqlRuleEngine` supports `AND`, `=` and `IS [NOT] NULL`, and
  throws on anything else. It gains `IN (…)` and `NOT IN (…)` over string literals, and one
  parenthesised `OR` group as a predicate. `NOT IN` on a missing property is false. Comparison
  stays ordinal.
- **Official emulator** evaluates SQL filters natively and is covered by the same provisioning
  test, within its cap of 70 SQL filters per topic.

### 10. Tooling

- **AsyncAPI** (`AsyncApiExporter`): emit the filters of section 4, `x-nimbus-routing-key` on the
  message (the property name) and `x-nimbus-routing` on each receive operation:
  `{ routingKeys: [...] }` or `{ unmatched: true, excludedRoutingKeys: [...] }`. It sits on the
  operation, not the channel, because a channel carries every event type the endpoint consumes.
- **EventCatalog** (`nb catalog export`): the routing-key property on the event; keys on each
  consumer.
- **`nb topology export`** (`PlatformConfigExporter`): `RoutingKeyProperty` on event types;
  `RoutingKeys` and `Unmatched` on each consumed entry.
- **WebApp API** (`api-spec.yaml`): event-type responses list consumers as names today. Add a
  parallel `consumerRouting` array (`endpoint`, `routingKeys`, `unmatched`); the existing field is
  unchanged. The event-type pages show the keys beside each consumer, so ten consumers no longer
  read as ten deliveries. This is a UI change and needs the PR screenshot.
- **MCP discovery tool** (`OperatorDiscoveryTools`): `nimbus_list_endpoints` adds
  `routingKeysConsumed` (event type id → keys) and `unmatchedEventTypes`, both omitted or null
  when empty.
- **Simulator:** `FakePayloadSimulationEventFactory` fills the routing-key property with one of the
  claimed keys, picked at random, so simulated traffic reaches instance endpoints and not only the
  unmatched one.
- **`nb endpoint topics removeDeprecated`** reads the descriptor and compares rule names, which
  don't change; no work.
- **Docs:** new `docs/routing-keys.md`, added to `website/sidebars.js`, linked from
  `docs/message-flows.md` and `docs/building-adapters.md`. It covers the multi-instance pattern,
  the receive check, the unmatched consumer, the rollout order and the capacity formula.

### 11. Sessions and ordering

Each instance endpoint has its own subscription and its own sessions, so the session key does
**not** need to contain the routing key. `[SessionKey(nameof(CustomerNo))]` is enough.

Ordering holds within one endpoint only. If a customer's key changes between two events, or events
of one session are published on both sides of a cut-over (below), they land on different endpoints
and their relative order is not guaranteed.

### 12. External producers

A producer outside NimBus must already stamp `MessageType = EventRequest` to be forwarded. To be
routed it also stamps the `RoutingKey` application property. If the two disagree, the receive
check rejects the message. CloudEvents ingress (`CloudEventsServiceBusBinding`) maps an extension
attribute to it (see Open questions).

## Rollout order

The order matters because older code neither stamps keys nor writes keyed filters.

1. **Upgrade everything first:** `nb`, the WebApp, the Resolver, the Manager and every adapter.
   Declare no keys until this is done. An older `nb` or WebApp that rebuilds a rule afterwards
   reverts it to the unfiltered form; the receive check contains the damage, and the next
   `nb topology apply` repairs it.
2. **Add `[RoutingKey]` to the message classes** and deploy the publishers. Routing doesn't change:
   no consumer claims a key yet.
3. **Drain.** Wait until no keyless message of those types is in flight: outbox rows, scheduled
   messages, forwarder backlogs. Anything left reaches the unmatched consumer after step 4.
4. **Deploy the instance adapters and the unmatched endpoint, then apply the topology.**
5. **Coming from a single unkeyed endpoint** (one `Erp` endpoint): stop it consuming the types in
   the same change. `nb topology apply` leaves its forward rule in place, so stop the old adapter
   first, apply, run `nb endpoint topics removeDeprecated <producer>` straight away, and purge what
   collected on the old endpoint's subscription in between. Rehearse this on a test namespace.

Per-session ordering across the cut-over is kept only if step 3 completes.

## Compatibility

- Additive: new attribute, new optional interface, new overloads, defaulted interface members, a
  new application property. No public API is removed or changes behaviour.
- With no keys declared, every filter is byte-identical and `nb topology apply` makes no calls it
  doesn't make today. Nothing is deleted.
- Older publishers send no `RoutingKey`: their events reach unkeyed consumers and the unmatched
  consumer, never a keyed one.
- Minor version (`docs/versioning.md`).

## Phasing

| Phase | Content |
|---|---|
| 1 | Sections 1 to 5, 7 to 12, the built-in unmatched handler and the manual recovery guide. |
| 2 | The Re-route action (section 6), after its open point is settled. |

## Testing

Unit and regression (MSTest):

- `TopologyDescriptor`: each row of the filter table, asserted as exact strings; ordinal key
  order; unchanged output for unkeyed consumers; `TryReadRoutingKeys` round-trip and the
  non-canonical fallback.
- **Unmatched with no keyed consumers** receives a keyed event (the gap in revision 1).
- Provisioner: the widen and narrow passes for adding a key, removing a key and moving a key
  between endpoints, with the unmatched endpoint named both before and after the instances in
  ordinal order; no calls on a second apply; rules still ensured on a paused and on a
  purge-marked forwarder.
- `PlatformValidation`: every error in section 8, and both command shapes.
- `Event.GetRoutingKey()`: attribute, override, null or empty, unknown, non-string and sensitive
  property.
- `PublisherClient`: every publish path stamps the key; `Publish(IMessage)` passes a caller-set
  key through; both CloudEvents content modes; outbox round-trip.
- Receive check: accepted key, foreign key, keyless resubmit with a matching payload, payload
  edited to another key, property and payload disagreeing, string-keyed handler.
- `SingleEndpointPlatformView.GetClaimedRoutingKeys` forwards to the inner platform.
- `SqlRuleEngine`: `IN`, `NOT IN`, the `OR` group, missing property.
- Simulator: generated events carry a claimed key.

Integration:

- In-repo emulator, end to end: one producer, two keyed consumers, one unmatched consumer. Keys
  `01`, `02`, `99` and none each land on the right endpoint exactly once; a keyed command reaches
  one instance.
- Real namespace, run manually before release: the same test; **case sensitivity** (`'dk'` against
  `'DK'`); a key moved between endpoints while a publisher runs, with no message unaccounted for;
  the load test of the Capacity section.

## Open questions

1. **Several targets per message.** If a customer can belong to several instances, choose between
   publishing once per target (works with this spec) or a key list matched with `LIKE`.
2. **Config-driven instances.** Should `Endpoint` get a protected constructor taking an explicit
   id, so a platform can add instance endpoints from configuration instead of a class each? That
   needs an audit of every place that assumes `Id == GetType().Name`.
3. **CloudEvents attribute name.** `routingkey` or a NimBus-prefixed extension, consistent with
   `docs/cloudevents.md`.
4. **Re-route mechanics** (phase 2): how an endpoint and the Resolver treat a resubmission for an
   event they have no row for.
5. **Case sensitivity on a real namespace.** The decision is "case-sensitive, ordinal". If the
   manual test shows Service Bus matching case-insensitively, a stray such as `'dk'` would be
   forwarded to the `'DK'` endpoint and dead-lettered there by the receive check rather than at the
   unmatched endpoint. Acceptable, but confirm.

## Review log

Findings from the adversarial review of revision 1, and where each is addressed.

| # | Finding | Addressed in |
|---|---|---|
| 1 | Keyed events dropped when only an unmatched consumer exists | §4 filter table, last row |
| 2 | A rebuild by older code silently removes filtering | §5 receive check; Rollout order 1 |
| 3 | Adding an instance loses or duplicates messages depending on endpoint names | §4 widen before narrow |
| 4 | No way to recover an unmatched event | §6 built-in handler, manual recovery, Re-route phase |
| 5 | Commands and requests are not `To`-addressed | Problem; §7 |
| 6 | `PlatformValidation` doesn't run at start-up and has no warnings | §8 |
| 7 | `RoutingKey` must be on `IMessage` and `IEventType` | §1, §2 |
| 8 | Not every publish path builds the message from the event | §2 |
| 9 | Key and payload can disagree | §5 rules 1 and 2; §12 |
| 10 | Pruning stale rules is a behaviour change | Removed: one rule per consumer, nothing to prune (§4, Non-goals) |
| 11 | Filter count and cost not assessed | §4 Capacity |
| 12 | Non-string keys undefined | §1; §8 |
| 13 | A sensitive property could become a routing key | §1; §8 |
| 14 | The simulator wouldn't reach keyed endpoints | §10 |
| 15 | Consumer listings mislead; single-endpoint view computes the wrong list | §3 `GetClaimedRoutingKeys`; §10 |
| 16 | Dynamic forwards not mentioned | Non-goals |
| 17 | Session ordering breaks at cut-over | §11; Rollout order |
