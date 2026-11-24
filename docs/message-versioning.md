# Evolving event contracts

An event class is a contract between a publisher and every consumer of that event type. NimBus
carries no version on the wire: the event type is the class name, and the payload is the class
serialized as JSON. This page is the policy for changing a contract without breaking consumers.

The short form:

1. **Add, never change.** New properties are optional. Existing properties are never renamed,
   removed or retyped.
2. **A breaking change is a new event type.** Publish `OrderPlacedV2` next to `OrderPlaced` until
   every consumer has moved.
3. **Old payloads come back.** Handlers must keep accepting every shape that is still stored.
4. **Gate it in CI** with `nb asyncapi diff`.

## Why old payloads come back

On most buses a message is gone once it is handled. NimBus keeps the payload of every tracked
event, and three paths deliver a stored payload to whatever handler code is deployed at that
moment:

- **Resubmit.** An operator resubmits a `Failed`, `DeadLettered` or `Unsupported` event days or
  weeks after it was published.
- **Deferred messages.** Events parked behind a blocked session are replayed when the session is
  released. See [Deferred messages](deferred-messages.md).
- **Retries and scheduled delivery.** A retry policy or a scheduled publish delays delivery past a
  deployment.

So deploying publisher and consumer together does not make a breaking change safe. The stored
events still have the old shape, and they keep it for as long as the store retains them (see
[Storage providers](storage-providers.md)).

## What the receive path does with a different shape

The handler's event class is filled from the JSON with Newtonsoft.Json:

| The payload… | Result |
|---|---|
| has a property the class doesn't | The property is ignored. The handler runs. |
| lacks a property the class has | The property keeps its default (`null`, `0`, `false`). The handler runs. |
| lacks a `[Required]` property or a C# `required` member | The same: it arrives as `null`. Neither is checked on receive. |
| has a value of the wrong type (`"two"` for an `int`) | Permanent failure before the handler runs. The event is dead-lettered. See [Error handling](error-handling.md). |

Data annotations such as `[Required]` and `[Range]` are checked when an event is **published**,
including one an operator composes in the WebApp. They are not checked when an event is received.
A handler therefore cannot assume that a property added after the first release has a value.

## Compatible changes

These keep the event type and need no coordination between publisher and consumers:

- **Add an optional property.** Make it nullable, or give it a default that means "not supplied".
  Don't mark it `[Required]` or `required`: older payloads don't have it.
- **Add an enum value**, with care. The contract check reports it as non-breaking, but consumers
  built before the value existed still receive it. Deploy the consumers that handle the new value
  before the publisher sends it.
- **Relax a validation bound**, such as a wider `[Range]`.
- **Change descriptions and other metadata.**

Either side may deploy first. A consumer that deploys first sees the new property as `null` until
the publisher follows. A publisher that deploys first sends a property older consumers ignore.

In the handler, treat a missing value as a normal case, not as an error:

```csharp
public sealed class OrderPlaced : Event
{
    public required string OrderId { get; init; }

    // Added in 1.1.0. Absent on events published before that.
    public string? PromoCode { get; init; }
}
```

## Breaking changes

These break a consumer or a stored payload, and none of them may be made in place:

- Rename or remove a property. A rename is a removal plus an addition: old payloads carry the old
  name, and the new property arrives as `null`.
- Change a property's type or format.
- Make an existing property required, or tighten a validation bound.
- Remove an enum value.
- Change what a property means while keeping its name and type. No tool detects this one.
- **Rename the event class.** The class name is the event type, so a rename creates a new event
  type. Stored events keep the old type and become `Unsupported` when resubmitted. Moving the class
  to another namespace is safe: the namespace is not part of the event type.

### Introduce a new event type

A breaking change gets a new class with a new name. The event type is global to the Service Bus
namespace, so the name must be unique across all projects.

```csharp
[AsyncApiMessage(Deprecated = true, Description = "Replaced by OrderPlacedV2.")]
public sealed class OrderPlaced : Event { /* unchanged */ }

public sealed class OrderPlacedV2 : Event
{
    public required string OrderId { get; init; }
    public required Money Total { get; init; }   // was: decimal Amount
}
```

`OrderPlacedV2` is a separate event type with its own subscriptions. It does not inherit from
`OrderPlaced`, and a handler for one never receives the other.

Migrate in this order:

1. **Add the new class** and mark the old one deprecated. The marker shows in the AsyncAPI
   document and as a banner in the EventCatalog export.
2. **Publish both.** The publishing endpoint declares `Produces<OrderPlacedV2>()` next to the old
   one and publishes both events for each change. Apply the topology.
3. **Move consumers one at a time.** A consumer declares `Consumes<OrderPlacedV2>()`, adds its
   handler, and drops `Consumes<OrderPlaced>()` in the same deployment, so it never handles the
   same change twice. Events with the same session key stay ordered, because both types reach the
   consumer through the same session.
4. **Keep the old handler and the old class** in each consumer and in the catalog until that
   endpoint has no unresolved `OrderPlaced` events left. Filter the WebApp's
   [Failed messages](webapp-failed-messages.md) page by event type to check. A stored event is
   resubmitted with its original event type, and without a handler it becomes `Unsupported`. The
   WebApp also needs the class to mask `[Sensitive]` fields: for a type missing from the catalog,
   operators without the PiiReader role see `[REDACTED:unknown-type]` in place of the payload.
5. **Stop publishing the old type** when no endpoint consumes it, then remove the class.

## Check contracts in CI

`nb asyncapi diff` compares two exported contract documents and exits non-zero on a breaking
change. Commit the export as a baseline and compare each build against it:

```bash
nb asyncapi export --assembly ./bin/Acme.Orders.dll -o ./asyncapi.new.yaml
nb asyncapi diff ./contracts/asyncapi.yaml ./asyncapi.new.yaml
```

`--assembly` points the export at your own catalog; the
[CLI reference](cli.md#nb-asyncapi-export) describes what the assembly must expose.

Update the baseline in the same pull request as a compatible change. The check fails once on
purpose, in the pull request that removes a retired event type at the end of a migration.

The [CLI reference](cli.md#nb-asyncapi-diff-old-file-new-file) lists exactly what counts as
breaking. The check sees the shape of a contract, not its meaning, and it cannot see the events
already stored.

## The `Version` field

`[AsyncApiMessage(Version = "1.1.0")]` is documentation. It appears in the AsyncAPI document and
pins the message version in the EventCatalog export (default `1.0.0`). It is not sent with the
message, and nothing reads it at runtime.

Raise it when a contract changes compatibly, so readers of the catalog can see that it did. A
breaking change is a new class, which starts again at its own first version.

## Agent-defined event types

Event types registered through the agent API follow the same rule. A registered schema is
immutable, so a changed contract is registered under a new id. Put the version in the id, as in
`crm.contact.enriched.v1`.
