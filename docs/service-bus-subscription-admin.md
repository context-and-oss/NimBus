# Topology → Subscriptions (Service Bus incident response)

Operator reference for the **Subscriptions** view of the NimBus WebApp's **Topology** page.
Use it when a producer floods the bus and you need to find, stop and clear a
backlog without collateral damage to other endpoints.

Access is site **Owner**, same as the rest of `/api/admin`. Every mutating action is
written to the audit trail as `ManageSubscription`.

## What the numbers mean

The first table lists every topic in the namespace — endpoint topics plus the
platform's own `Resolver` topic.

| Column | Meaning |
| --- | --- |
| **Active** | Messages waiting to be delivered, summed across the topic's subscriptions. A topic holds no messages of its own. |
| **Dead-letter** | Messages that exhausted delivery or expired, **plus** messages Service Bus could not auto-forward. Azure keeps the latter in a separate *transfer* dead-letter queue; the page adds the two and breaks them out as `(N fwd)` with the split in the tooltip. |
| **In transit** | Messages queued for auto-forward out of a subscription. A large number here means a forwarder is behind. |
| **Scheduled** | Messages with a future enqueue time. |

Columns sort on click; the table is alphabetical by default. Sorting on a count
column puts the backlog at the top. Click a topic to see the same counters per
subscription, plus its rules, its auto-forward destination and whether the
platform can rebuild it.

## Reading the NimBus topology

Each endpoint topic carries:

- the endpoint's **own subscription**, named after the endpoint — the
  session-enabled one its adapter actually receives from, carrying the
  `to-{endpoint}`, `continuation` and `retry` rules. Deleting this stops that
  adapter until it is back, so prefer Purge here;
- **`{endpoint}-reply`** — the session-enabled subscription request/reply
  responses land on;
- one **forwarder subscription per consuming endpoint**, auto-forwarding to that
  consumer's own topic, with one rule per event type it consumes;
- a **`Resolver`** subscription, auto-forwarding every audited message to the
  `Resolver` topic — this is a second copy of *everything*, so a flood shows up
  here as well as on the consumer;
- **`Deferred`** (sessions on) and **`DeferredProcessor`** (sessions off);
- **`AgentDyn-{target}`** forwarders for any dynamically-typed events declared as
  `DynamicForward`s (spec 022).

So one bad publishing run on `CrmEndpoint` inflates the `ErpEndpoint`
subscription on `CrmEndpoint` *and* the `Resolver` subscription on `CrmEndpoint`
*and* the `Resolver` topic itself. Check all three.

## Choosing an action

| You want to… | Use | Notes |
| --- | --- | --- |
| Stop a subscription taking anything new | **Detach rule** | Removes the matching rule, so nothing new enters. What's already queued still drains. Reverse with **Restore rules**. Only rules the platform topology can rebuild are detachable — a `$Default` that is a subscription's whole routing, or anything on a hand-made subscription, is shown greyed out because NimBus could not put it back. |
| Stop a subscription delivering / forwarding | **Pause** | Sets `ReceiveDisabled`. On an auto-forwarding subscription it *also* detaches the forward destination, so messages collect in the subscription instead of moving on. **Resume** puts the destination back. |
| Empty a normal subscription | **Purge** | Drains active and deferred messages one batch at a time. Fine for hundreds, slow for tens of thousands. Leaves both dead-letter queues alone (see **Purge dead letters**). A paused subscription is made receivable for the duration and returned to Paused afterwards. |
| Empty a subscription's dead-letter queues | **Purge dead letters** | Completes every message in the dead-letter queue and the transfer dead-letter queue. Service Bus never empties these on its own, and dead-lettered messages count toward the topic's size quota. The subscription's status is left alone: a paused subscription stays paused, and if Service Bus refuses the receive the result says so. Shown only when either queue holds messages. To clear an endpoint's own dead-lettered events, start with **Operations → Delete dead-lettered**: it deletes the stored events together with their own messages in the queue, and leaves every other message for this action. |
| Empty an auto-forwarding subscription | **Delete & recreate** | Service Bus refuses receive on a forwarding entity, so it can't be drained. Delete + re-provision discards the backlog in one management call. Alternatively **Pause** first (which detaches forwarding) and then **Purge**, if you'd rather not delete the entity. |
| Remove something that shouldn't exist | **Delete** | No rebuild. |

## Replaying Resolver dead letters

The terminal, session-enabled `Resolver/Resolver` subscription has an additional
**Inspect dead letters** action when its regular dead-letter queue is non-empty.
This action is Owner-only. It does not apply to endpoint-topic `Resolver`
forwarders, transfer dead letters, forwarding subscriptions, or non-session
subscriptions.

Inspection groups the first 500 regular-DLQ messages by exact, case-sensitive
dead-letter reason. A missing reason and an empty-string reason are separate
choices. If more messages exist, the dialog marks the snapshot as truncated;
replay the snapshot and repeat the operation for the next batch. Each request has
a 180-second server budget.

Replay keeps the message ID (Spec 030 §5.7: the tracking store's stale-write guard
recognises a replayed copy that lands after its own outcome only by that id) and
preserves the body, session, correlation, reply, content and ordinary application
metadata. It removes the broker dead-letter fields and `ThrottleRetryCount`, so a
replayed `CosmosDbThrottled` message starts a fresh throttle budget instead of
being dead-lettered again on its first 429, and adds `DeadLetterOriginalMessageId`
and `DeadLetterOriginalReason` for provenance. For each message, removing the source
from the DLQ and publishing the replacement to the Resolver topic are one Service
Bus transaction: both commit or neither does. This preserves per-session broker
ordering, but replaying an older message naturally places the replacement behind
messages already queued for that session.

`CosmosDbThrottled` means Resolver exhausted its shared ten-attempt budget while
Cosmos DB continued returning 429. The stable name is Cosmos-specific. SQL-backed
deployments can use the same replay workflow for their regular dead letters, but
generic SQL storage transients are not relabelled with this reason.

The management identity needs namespace-scoped Azure Service Bus Data Owner (the
production templates already grant it). Keep the management WebApp at one instance
while a replay runs: the per-subscription concurrency gate is process-local. The
NimBus emulator used by CrmErpDemo supports this narrow regular-DLQ transaction
shape as well as Azure Standard/Premium namespaces.

### Why "Delete & recreate" is safe

The subscription is rebuilt from `TopologyDescriptor` — the same declarative
topology `ServiceBusTopologyProvisioner` (and so `nb topology apply`) uses — so
rules, filters, actions, session flag, TTL and forward destination come back
exactly as provisioning would have created them. A test pins the descriptor and
the provisioner against each other so the two cannot drift.

Subscriptions the descriptor cannot describe show **no** recreate button, and
deleting one needs an extra acknowledgement.

There is a short window while the subscription does not exist. Messages published
in that window are not captured by it. During an incident that is usually the
point; outside one, prefer Pause.

## Cautions

- **Recreate loses the backlog on purpose.** The number on the confirmation is
  everything the subscription holds, dead-lettered messages included — a purge
  would have left those behind, a delete does not.
- **A pause left unresumed is not silent, but it is easy to forget.** A paused
  forwarding subscription shows `→ <destination> (detached)` and a *Paused*
  badge. Messages accumulate and will eventually hit the topic's size quota.
- **Dead-letter counts don't clear themselves.** If auto-forwarding failed
  because a destination was disabled or full, Service Bus dead-letters on the
  *source* — those messages need explicit handling, and Purge won't touch them.
  Transfer dead letters are deliberately not eligible for Resolver replay.
- **Topics the platform doesn't own are read-only.** They're listed so a stray
  topic sitting on a backlog is visible, but every action is disabled — manage
  those in the Azure portal.
- **`Deferred` and `{endpoint}-reply` are session-enabled.** Purging one walks
  sessions in turn and is slower than the flat count suggests.

## Related

- [architecture.md](architecture.md) — endpoint/event topology.
- **Topology → Catalog drift** — audits subscriptions and rules against the expected
  topology and removes deprecated leftovers.
- **Operations** page — message-store operations (resubmit, skip, delete
  by status). Those act on stored events, not on the bus.
- [stale-pending-reconcile.md](stale-pending-reconcile.md) — Operations →
  *Reconcile Stale Pending*, for rows a late request copy left `Pending` before
  3.7.0. It repairs the stored projection; it sends nothing to the bus.
- [cli.md](cli.md) — `nb topology apply` re-provisions everything the descriptor
  describes, and is the way back from a rebuild that failed.
