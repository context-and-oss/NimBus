# Plan: Spec 045 phase 1 — routing keys

Spec: `docs/spec/045-routing-keys/spec.md` (revision 2, merged in #373). Baseline: master `8ee6b128`.
Branch `feat/spec-045-routing-keys` in a fresh worktree (`.claude/worktrees/spec-045-routing-keys`,
shared checkout rule). Delivery: **one PR, one commit per part** (no stacked PRs). This plan is
copied to `docs/plan/2026-11-13-spec-045-routing-keys.md` as the first commit.

## Context

Today a consumer's forward rule matches on event type only, so an event type with several targets
of the same kind (the customer's 10+ Business Central instances) can't be routed by data. Spec 045
adds a payload-derived routing key, keyed consumer filters, a required unmatched consumer, a
receive-side check, widen-before-narrow provisioning and keyed commands. **Phase 1 only**: the
Re-route action (spec §6 phase 2) is out of scope.

## Decisions taken while planning (deviations or precisions vs the spec)

1. **Receive check = an `IEventContextHandler` decorator** (`RoutingKeyGuardContextHandler`),
   modelled on `CloudEventValidatingContextHandler`, wrapped inside the inbox and CloudEvents
   decorators. It deserialises the payload a second time, but only for event types whose CLR type
   declares a routing key. Rejected alternative: threading a guard into `EventJsonHandler<T>`
   (`Activator.CreateInstance` construction makes that invasive).
2. **String-keyed (dynamic) handlers:** there is no payload-derived key, so the check is
   "the `RoutingKey` property, if present, is one the endpoint declared for that EventTypeId".
   The spec's "rule 2 only" wording can't apply literally; the spec gets a one-line correction.
3. **Permanent failure during Retry/Resubmission currently leaves the session blocked**
   (`StrictMessageHandler.HandleResubmissionRequest`/`HandleRetryRequest` don't catch
   `PermanentFailureException`; `MessageHandler` then dead-letters without unblocking). A
   resubmit-with-changes that alters the key would hit this. Fix generally: catch it in both
   paths, unblock if blocked by this event, continue deferred, rethrow. Regression test first.
4. **Filter-length (1,024) check lives in `NimBus.ServiceBus`** (the descriptor owns the filter
   text), as a provisioner pre-flight next to `PlatformValidation.EnsureValid`. All other rules
   live in `PlatformValidation` (Abstractions).
5. **New members on public records are `init` properties, not positional parameters**
   (`ExpectedRule.Routing`, `EndpointSummary.RoutingKeysConsumed`), to avoid breaking constructors.

## Part 1 — Abstractions (`src/NimBus.Abstractions`)

- `Events/RoutingKeyAttribute.cs` (class, inherited, single; `PropertyName`, `DropUnmatched`).
- `Events/IRoutingKeyProvider.cs`; `Event` implements it: `public virtual string? GetRoutingKey()`
  mirroring `GetSessionId()` (`Event.cs:23-35`): null when no attribute or null/empty value;
  `InvalidOperationException` for unknown, non-`string`, or `[Sensitive]` property (property or
  class-level `SensitiveAttribute`, checked with `IsDefined(..., inherit: true)`).
- `IEventType.RoutingKeyProperty => null` (default); `EventType` returns the attribute's property
  name, plus internal `DropUnmatched`.
- `Events/RoutingKeys.cs`: `IsValid(string)` (`^[A-Za-z0-9_.-]{1,64}$`), shared by validation and
  `TopologyDescriptor`.
- `Endpoint`: `Consumes<TEvent>(params string[] routingKeys)` (empty array, null/empty/invalid key
  throws), `ConsumesUnmatched<TEvent>()`; stores keys/unmatched per event type id. Both add to
  `EventTypesConsumed`; declaring a type twice keeps throwing.
- `IEndpoint`: `GetRoutingKeys(IEventType) => []`, `IsUnmatchedConsumer(IEventType) => false`;
  `Endpoint` overrides.
- `IPlatform.GetClaimedRoutingKeys(IEventType)` default: ordinal-sorted distinct union of
  consumers' keys. `Platform` caches it with the existing indexes.
- `src/NimBus.CommandLine/SingleEndpointPlatformView.cs` forwards it to the inner platform.
- Tests (`tests/NimBus.Core.Tests`, copying `SessionKeyAttributeTests.cs`): GetRoutingKey cases,
  EventType metadata, Endpoint declarations, platform union, single-endpoint view forwarding.

## Part 2 — Message and publish

- `UserPropertyName.RoutingKey`; `IMessage.RoutingKey => null` (default); `Message.RoutingKey`
  settable (serialised, so it survives the outbox like `CloudEvent`).
- `MessageHelper.ToServiceBusMessage`: stamp when non-empty (after `EventTypeId`, line ~26);
  also in `CreateDeferredMessage` (~157-186), which rebuilds properties by hand.
- `MessageContext.RoutingKey` via the non-throwing `_sbMessage.GetUserProperty(...)` pattern
  (`MessageContext.cs:114`); `InMemoryMessageContext` passes through.
- `PublisherClient.GetMessageStatic` (~440-457): `RoutingKey = (@event as IRoutingKeyProvider)?.GetRoutingKey()`.
  XML doc on `Publish(IMessage)` says the caller sets it.
- Tests: `MessageHelperTests` (stamped/absent, deferred copy), `PublisherClient*Tests` (every
  publish path incl. `Request`, `Schedule`, batches, both CloudEvents modes), outbox round-trip.

## Part 3 — Topology, provisioner, emulator (`src/NimBus.ServiceBus`, `src/NimBus.ServiceBusEmulator`)

- `TopologyDescriptor`:
  - `KeyedForwardFilter(eventTypeId, keys)` (`= 'k'` for one, `IN ('a', 'b')` for several, keys
    ordinal-sorted, re-validated with `RoutingKeys.IsValid`), `UnmatchedForwardFilter(eventTypeId, claimed)`
    (`... AND (user.RoutingKey IS NULL OR user.RoutingKey NOT IN (...))`, or plain `ForwardFilter`
    when nothing is claimed), `TryReadRoutingKeys(filter, eventTypeId, out mode, out keys)`.
  - `ConsumerForwarderSubscription` gains an overload taking per-event-type filters; the existing
    signature stays (unkeyed). `ForEndpointTopic` asks each consumer `GetRoutingKeys`/`IsUnmatchedConsumer`
    and `platform.GetClaimedRoutingKeys`. Rule names unchanged.
  - `ExpectedRule.Routing { get; init; }` (mode + final keys) so the provisioner can widen.
- `ServiceBusTopologyProvisioner.ApplyCoreAsync`, per endpoint topic:
  1. **Widen pass**: missing subscription → full `EnsureSubscriptionAsync`; existing → for each
     rule with `Routing`, read the existing rule, compute union (keyed) / intersection of
     exclusions (unmatched) via `TryReadRoutingKeys` (non-canonical → widest), and `EnsureRuleAsync`
     the widened filter only if it differs from both existing and final.
  2. **Narrow pass**: the current loop, unchanged.
  `ApplyAsync` calls `PlatformValidation.EnsureValid` + `TopologyDescriptor.ValidateFilterLengths`.
- `AsyncApiExporter.ForwardSubscription` (~311-322) takes the consumer's filter from the descriptor;
  add `x-nimbus-routing-key` (message), `x-nimbus-routing-keys` / `x-nimbus-unmatched` (channel).
- `SqlRuleEngine`: `user.X IN ('a', ...)`, `user.X NOT IN (...)` (false when missing), and one
  parenthesised `OR` group whose members are any existing predicate. Ordinal, as today.
- Tests:
  - `tests/NimBus.CommandLine.Tests/ServiceBusTopologyProvisionerTests.cs` (fake
    `RecordingAdministrationClient`): exact filters per spec table; unmatched with no keyed
    consumers stays plain; widen/narrow ordering for add, remove and move a key, with the
    unmatched endpoint sorting before and after the instances (assert the `Operations` order);
    second apply = zero mutations; paused/purge-marked forwarders still get rules.
  - `tests/NimBus.ServiceBusEmulator.Tests/SqlRuleEngineTests.cs`: IN, NOT IN, OR group, missing property.
  - `BrokerNamespaceTests` (in-process broker, copying `AssertForwarderTakesOnlyOriginalPublishes`):
    one producer, two keyed consumers, one unmatched; keys `01`, `02`, `99`, none → exactly one copy
    each on the right forwarder.
  - AsyncApi tests in `NimBus.CommandLine.Tests` and `NimBus.ServiceBus.Tests` updated/extended.

## Part 4 — Validation (`src/NimBus.Abstractions/PlatformValidation.cs`)

- `ValidateRoutingKeys(IPlatform)` errors per spec §8: invalid key, case-only duplicates, keys and
  unmatched on one endpoint/type, keyed type without unmatched consumer unless `DropUnmatched`,
  keys declared for a type whose class has no routing key (skip types without a CLR type),
  non-string or sensitive key property.
- `ValidateCommandConsumers` accepts the keyed shape (§7): each key claimed exactly once, every
  consumer keyed or unmatched, exactly one unmatched, no `DropUnmatched`.
- `EnsureValid(IPlatform)` runs both; `EnsureCommandConsumers` stays (bridge).
- Tests: `tests/NimBus.Core.Tests/PlatformValidationTests.cs`, one per error and both command shapes;
  `CrmErpDemo.AppHost.Tests/CommandCatalogTests.cs` still green.

## Part 5 — Receive check and unmatched handler (`src/NimBus.SDK`, `src/NimBus.Core`, `src/NimBus.Testing`)

- `RoutingKeyRejectedException` (Core, inner of `PermanentFailureException`, so the DLQ reason
  reads `Permanent failure: RoutingKeyRejectedException: Routing key '02' is not accepted by endpoint ErpInstance01 (accepts: 01)`).
- `RoutingKeyGuardContextHandler : IEventContextHandler` (SDK): per EventTypeId, a policy
  (declared keys | unmatched | none) and the CLR type from `builder.HandlerRegistrations`.
  Rule 1 (payload key ∈ declared, skipped for unmatched), rule 2 (property present ⇒ equals
  payload key); dynamic handlers per Decision 2. Ordinal.
- `NimBusSubscriberBuilder`: `EnforceRoutingKeys<TEndpoint>() where TEndpoint : IEndpoint, new()`,
  `EnforceRoutingKeys(params string[] keys)` (keys apply to every handled type whose class declares
  a routing key), `AddUnmatchedHandler<TEvent>()` (registers `UnmatchedRoutingKeyHandler<TEvent>`
  and marks the type unmatched).
- Composition: in `ServiceCollectionExtensions` ISubscriberClient factory (~291-311) wrap
  `eventHandlerProvider` before the CloudEvents/inbox decorators; policy from the explicit builder
  setting, else `sp.GetService<IPlatform>()` endpoint whose `Id == options.Endpoint`. Mirror in
  `src/NimBus.Testing/Extensions/ServiceCollectionExtensions.cs` (`AddNimBusTestTransport`, ~55-86).
- `StrictMessageHandler`: Decision 3 fix in `HandleRetryRequest` and `HandleResubmissionRequest`.
- Tests: decorator unit tests (copy `tests/NimBus.ServiceBus.Tests/CloudEventsValidationTests.cs`):
  accepted, foreign key, keyless resubmit with matching payload, payload edited to another key,
  property/payload mismatch, unmatched accepts any key, dynamic handler; composition test through
  `AddNimBusTestTransport` + `InMemoryMessageBus.DeliverAllWithResults` asserting
  `IsDeadLettered`/`DeadLetterReasonRecorded`; auto policy from a registered `IPlatform`;
  `StrictMessageHandlerTests` regression for the resubmission/retry unblock.

## Part 6 — Tooling, WebApp, MCP, simulator

- `EventCatalogExporter`: routing-key property on the event page; keys/unmatched on receives.
- `PlatformConfigExporter`: `RoutingKeyProperty` on event types; `RoutingKeys`/`Unmatched` on consumed entries (first tests for it).
- `api-spec.yaml`: `routingKeyProperty` on `EventType`; new `ConsumerRouting {endpoint, routingKeys[], unmatched}`
  and `consumerRouting[]` on `EventType` and `EventTypeDetails` (existing `consumers` unchanged).
  `Mapper.cs` (~131-190) and `EventTypeImplementation.cs` fill them.
- ClientApp: `pages/event-types-list.tsx` and `pages/event-type-details.tsx` show keys (or
  "unmatched") beside each consumer badge; tests in `event-types-list.test.tsx`, `event-type-details.test.tsx`.
- MCP `OperatorDiscoveryTools.EndpointSummary`: `RoutingKeysConsumed` init property
  (eventTypeId → keys, `"*unmatched"` marker documented); test in `McpOperatorEndpointTests`.
- Simulator: `FakePayloadSimulationEventFactory` takes optional `IPlatform`; sets the routing-key
  string property to a random claimed key; DI registration passes the platform. Test in `SimulatedPublisherTests`.

## Part 7 — Docs

- `docs/routing-keys.md` (multi-instance pattern, receive check, unmatched consumer, rollout order,
  capacity formula, dynamic-handler limitation); `website/sidebars.js` under "How it works" after
  `message-flows`; links from `docs/message-flows.md` (Flow 10) and `docs/building-adapters.md`
  (subscriber setup).
- Spec status → "implemented (phase 1)"; correct the dynamic-handler sentence (Decision 2) and the
  `SessionKeyProperty` mention (it's on `EventType`, not `IEventType`).

## Verification

- Worktree with `global.json` SDK pin honoured: `dotnet build src/NimBus.slnx -c Release` (0 CS
  warnings), `dotnet test src/NimBus.slnx -c Release --no-build` with `set -o pipefail`.
- Emulator-category tests run locally (in-repo emulator via `EmulatorProcess`), including
  `SdkSmokeTests.Stock_sdk_provisioner_second_apply_has_zero_mutations`.
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`; a Release build with
  NSwag generation enabled (not `SkipSpaBuild`) for the API-spec change.
- `npm --prefix website ci && npm --prefix website run build`.
- WebApp screenshot of the event-type page with keyed consumers (mock-API preview, neutral demo
  data) pushed to `pr-assets/routing-keys` for the PR body.
- **Not run, reported as skipped:** the real-namespace manual tests in spec §Testing (case
  sensitivity, live key move, Standard-tier load test) and live Cosmos/SQL conformance (no storage change).

## Changes during implementation

- **AsyncAPI**: the routing shape is `x-nimbus-routing` on each receive operation, not
  `x-nimbus-routing-keys` / `x-nimbus-unmatched` on the channel; a channel carries every event type
  the endpoint consumes.
- **MCP**: `EndpointSummary` gained two init properties, `RoutingKeysConsumed` and
  `UnmatchedEventTypes`, instead of an `"*unmatched"` marker inside one map.
- **EventCatalog**: keys are written as a "Routing keys" section in the service body, not as
  frontmatter, so EventCatalog's schema for `receives` is untouched.
- **Test transport**: the in-memory transport dead-letters a successful delivery after the handler
  ("Failed to handle message.", a known pre-existing NullReferenceException), so the receive-check
  tests assert handler side effects and the presence or absence of a `RoutingKeyRejectedException`
  reason rather than `IsDeadLettered`.
