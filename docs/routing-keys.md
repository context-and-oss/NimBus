# Routing keys

By default NimBus routes an event to every endpoint that consumes its event type. A **routing
key** routes by data as well: the publisher takes a key from the payload, and a consumer can
declare that it takes an event type only for some keys. The filter runs on Service Bus, so other
consumers never receive the message.

The typical case is one source and several targets of the same kind. CRM publishes
`CustomerCreated`, and each country runs its own Business Central instance, all served by the same
adapter code. Each instance is its own endpoint, with its own circuit breaker, scaling and
maintenance window, and receives only its own customers.

The design is in [Spec 045](spec/045-routing-keys/spec.md).

## Declare the key on the event

```csharp
[RoutingKey(nameof(ErpInstance))]
[SessionKey(nameof(CustomerNo))]
public sealed class CustomerCreated : Event
{
    public required string CustomerNo { get; init; }
    public required string ErpInstance { get; init; }
}
```

- The property must be a `string` and must not be `[Sensitive]`: the key travels unmasked as the
  `RoutingKey` application property, and appears in traces and dead-letter reasons.
- An empty or null value means the message has no key.
- To compute a key, override `GetRoutingKey()` instead.
- The session key doesn't need to include the routing key. Each instance endpoint has its own
  sessions.

Every publish path stamps the key: `Publish`, `PublishBatch`, `PublishBatches`, `Schedule`,
`Request` and `PublishFromContext`, and the outbox keeps it. `Publish(IMessage)` sends a message as
you built it, so set `Message.RoutingKey` yourself there.

## Declare the keys on the consumers

`Endpoint.Id` is the class name, so each instance is a small class. A shared base keeps it to one
line each:

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

| Declaration | Receives |
|---|---|
| `Consumes<T>()` | Every message of `T`, as before |
| `Consumes<T>("01", "02")` | Messages of `T` with key `01` or `02` |
| `ConsumesUnmatched<T>()` | Messages of `T` with no key, or with a key no consumer claims |

A key is 1 to 64 characters from `[A-Za-z0-9_.-]`, and keys are compared case-sensitively.
`nb topology apply` validates the platform first and refuses to run when:

- a keyed event type has no unmatched consumer. Service Bus silently drops a message no rule
  matches, so a mistyped key would disappear. Set `[RoutingKey(..., DropUnmatched = true)]` to
  accept that instead.
- a key is malformed, or two keys differ only by case.
- one endpoint declares keys and unmatched for the same type.
- an endpoint declares keys for a type whose class provides no routing key.

### Commands

A command routed by key has exactly one consumer per key and exactly one unmatched consumer, and
can't use `DropUnmatched`. An unkeyed command keeps the rule of exactly one consumer.

A request (`Request<TRequest, TResponse>`) routed to the unmatched consumer is dead-lettered there,
so the caller sees its timeout.

## Check the key on receive

The filter on the bus is one layer. A rule rebuilt by an older `nb` or WebApp loses its filter
without an error, and the key can disagree with the payload after a resubmit with changes. So the
subscriber checks every message before the handler runs:

1. For an event type the endpoint takes only for some keys, the key in the payload must be one of
   them.
2. When the message carries a `RoutingKey` property, it must equal the key in the payload.

A message that fails either check is dead-lettered with a reason such as
`Routing key '02' is not accepted by endpoint ErpInstance01 (accepts: 01)`. It doesn't block its
session.

The check runs automatically when an `IPlatform` is registered in the adapter's container and
holds an endpoint whose id is the subscriber's endpoint. Otherwise, enable it in the builder:

```csharp
services.AddNimBusSubscriber(
    options => options.Endpoint = instanceEndpointId,
    subscriber =>
    {
        subscriber.AddHandler<CustomerCreated, CustomerCreatedHandler>();
        subscriber.EnforceRoutingKeys<ErpInstance01>();   // or EnforceRoutingKeys("01")
    });
```

An instance endpoint without the check relies on the bus filter alone.

A handler registered by string key (`AddDynamicHandler`) has no class to read the key from. For it
the check is that the `RoutingKey` property, when present, is one of the declared keys.

## The unmatched consumer

The unmatched consumer is an ordinary endpoint: its messages appear in the WebApp, the Resolver
audit and the MCP tools. NimBus ships a handler that dead-letters each message with a reason
naming the key:

```csharp
subscriber.AddUnmatchedHandler<CustomerCreated>();
```

A dead-lettered message there is one of two things:

| Cause | What to do |
|---|---|
| Wrong key in the source data | Correct it in the source system, which publishes a new event, then skip the stray |
| The key's instance isn't provisioned yet | Provision it, have the source republish, then skip the stray |

Resubmitting doesn't help: it returns the message to the unmatched endpoint.

## Change keys safely

`nb topology apply` widens every routing rule on a producer topic before it narrows any. While a
key moves, its messages can briefly reach both the old and the new owner, but never neither. The
receive check and the unmatched handler dead-letter the extra copy instead of handling it.

Rule names don't change when keys do, so existing rules are updated in place.

**Order of a rollout**

1. Upgrade `nb`, the WebApp, the Resolver and every adapter. Declare no keys before this is done.
2. Add `[RoutingKey]` to the event classes and deploy the publishers. Nothing is routed by key yet.
3. Let in-flight messages of those types drain: outbox rows, scheduled messages, forwarder
   backlogs. Keyless messages that remain go to the unmatched consumer.
4. Deploy the instance adapters and the unmatched endpoint, then run `nb topology apply`.
5. Coming from one unkeyed endpoint (`Erp`): `nb topology apply` leaves its forward rule in place.
   Stop the old adapter, apply, run `nb endpoint topics removeDeprecated <producer>`, and purge what
   collected on the old endpoint's subscription in between.

Deploy an adapter with new keys before applying the topology: its receive check reads the keys
from its own build.

## Capacity

Each consumer and event type is one SQL filter on the producer's topic, on top of 8 fixed filters
per topic:

```text
8 + (instances + 1 unmatched + other consumers) × routed event types
```

A real namespace allows 2,000 SQL filters per topic; the official Service Bus emulator allows 70.
With 10 instances, six routed event types already need 74. The in-repo emulator has no such cap.
On the Standard tier, SQL filters add to throttling cost whether or not they match.

## Where it shows

- **WebApp**: event-type pages show each consumer's keys, or `unmatched`, next to its name.
- **MCP**: `nimbus_list_endpoints` returns `routingKeysConsumed` and `unmatchedEventTypes`.
- **`nb asyncapi export`**: each forward subscription carries its real filter; receive operations
  carry `x-nimbus-routing`, and messages `x-nimbus-routing-key`.
- **`nb catalog export`**: event pages name the routing-key property; service pages list the keys.
- **`nb topology export`**: `routingKeyProperty`, `routingKeys` and `unmatched` per event type.
- **Simulator**: generated events get one of the keys their consumers claim.
