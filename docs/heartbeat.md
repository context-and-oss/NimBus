# Platform Heartbeat

Operator reference for the platform heartbeat in the NimBus WebApp — the
**Heartbeat** page (`/Heartbeat`) and **Settings → Heartbeat probing**
(`/Settings/heartbeat`) — which answers two questions: *are my endpoints
reachable?* and *is NimBus itself running?*

The heartbeat probes every catalog endpoint without adapter authors writing a
handler. The WebApp sends an ordinary `EventRequest` with
`EventTypeId = "NimBus.Platform.Heartbeat"` (a reserved, dotted id — application
event ids are unqualified CLR type names and can never contain a dot, so no
business event can collide with the probe); a subscriber on a current SDK answers it inside
`StrictMessageHandler`, and an older subscriber answers `UnsupportedResponse`,
which still proves the endpoint is reachable.

The Heartbeat page is open to site Readers and above. Its **Platform services**
card and the **Configure probing** link are shown to site Owners only, and
**Settings → Heartbeat probing** is site **Owner** only, same as the rest of
`/api/admin`. Every mutating action is audited (`UpdateHeartbeatSettings`,
`SendHeartbeatNow`, `EnableEndpointHeartbeat`, `DisableEndpointHeartbeat`).

## Operator controls

The controls live in **Settings → Heartbeat probing**: the schedule and **Send
now** on its **Probing** tab, the per-endpoint switch on its **Endpoints** tab.

| Control | Meaning |
| --- | --- |
| **Enabled** | Global switch for the scheduled per-endpoint fan-out. **Off by default.** It does not govern the Resolver probe or the timeout sweep — both run regardless. |
| **Interval seconds** | Seconds between scheduled fan-outs. Default 300, clamped to a minimum of 30. |
| **Timeout seconds** | Seconds a probe may stay `Pending` before the sweep settles it to `Off`. Default 60, clamped to between 5 and the interval — a timeout longer than the interval could never elapse between probes. |
| **Send now** | Writes a `Pending` row per included endpoint and probes them straight away, without waiting for the next interval. |
| **Included** (per endpoint) | Opts one endpoint out of the fan-out. An endpoint with no stored preference is included. |

The background service ticks every 30 seconds. On each tick it sweeps timed-out
probes, probes the Resolver, and — only when `Enabled` is on and the interval has
elapsed — claims and sends the endpoint fan-out. Because the sweep is
unconditional, a probe that is never answered settles to `Off` within
`Timeout + 30` seconds even if someone turns the global switch off in the
meantime.

Editing the schedule never resets it: a settings write that carries no
`LastSentAtUtc` leaves the stored value alone, because that field is owned by the
send claim rather than by the operator form.

## Statuses

An endpoint's status is its **last settled outcome**. An in-flight probe never
masks it, so a dead endpoint keeps reading `Off` while the next probe is on its
way, and a healthy one keeps reading `On` between sends.

| Status | Meaning |
| --- | --- |
| `On` | The endpoint answered with the SDK auto-response. |
| `Unsupported` | The endpoint is reachable but its SDK predates heartbeat support. Reachability is proven; the adapter needs a package upgrade. |
| `Off` | The endpoint returned an error or a deferral, or a probe timed out unanswered. |
| `Pending` | A probe is in flight and no outcome has ever been recorded — the first probe only. |
| `Unknown` | Nothing has been recorded yet. |

The reply's `MessageType` decides the outcome: `ResolutionResponse` → `On`,
`UnsupportedResponse` → `Unsupported`, `ErrorResponse` and `DeferralResponse` →
`Off`, anything else → `Unknown`.

Round-trip time, last-seen timestamp and reported SDK version are all taken
from the last real response — a timed-out probe carries none. The Heartbeat
page's **Adapter status** table shows the SDK version; an `Unsupported` endpoint
that reported none reads *pre-heartbeat SDK*, meaning the adapter is running a
pre-heartbeat SDK. The per-endpoint round-trip and timestamps are returned by
`GET /api/admin/heartbeat/overview`; the last-sent time reflects the most recent
probe whatever its outcome.

Endpoints the store has never seen are still listed, synthesized as `Unknown`
from the platform catalog, so a fresh deployment shows the full endpoint list
rather than an empty table.

## Adapter behavior

Adapters register nothing. Taking an SDK/Core package that contains the
heartbeat auto-response is the whole integration.

Inside `StrictMessageHandler` the heartbeat branch runs immediately after the
inbound log line, **before** the inbox duplicate check and before the
blocked-session guard. Three consequences worth knowing:

- **A blocked session still answers.** An endpoint whose session is blocked by a
  failed event is not dead, and the probe reports it as alive. This is the case
  operators most often need the Heartbeat page for.
- **Probes never enter inbox deduplication.** Every probe carries a fresh id, so
  recording them would be pure waste.
- **User handlers never run.** The reply is built and the message completed
  without dispatching anything.

The Resolver diverts heartbeat traffic before it writes any tracking record, so
heartbeats never appear on the Events, Flow or Monitor pages and never enter
latency aggregates. If you see heartbeat rows in the Flow, the divert is broken.

## Fan-out routing

The WebApp sends each probe **straight to the destination endpoint's topic** with
`From = "Manager"`, rather than through a Manager topic. Endpoint topics already
carry the Resolver subscription and the from-/to- rules the reply needs, so the
heartbeat requires no topology change and no new subscription. Endpoint probes
share the session `"Heartbeat"`.

## Resolver liveness

The Resolver is an isolated-worker Function whose only trigger is a Service Bus
subscription. It has no HTTP surface to ping, and "the process is up" would not
answer the question that matters anyway. So the WebApp probes it exactly as it
probes an endpoint: a `Heartbeat` `EventRequest` addressed `To = "Resolver"`,
sent straight to the Resolver topic. Reaching `ResolverService` proves three
things at once — the host is running, it is draining its session subscription,
and it can write to the message store. The Resolver settles the probe itself
instead of replying over the bus.

- **Where**: the **Platform services** card at the top of the Heartbeat page (site Owners only).
  Status, round-trip, the Resolver's assembly version, last seen, last probe.
- **Independent of the global switch.** The probe goes out every interval whether
  or not `Enabled` is on. `Enabled` governs the per-endpoint fan-out, which is N
  messages per interval; the Resolver probe is one message, and an operator
  asking whether the Resolver is up should not have to start probing every
  adapter to find out.
- **Interval and timeout** are the heartbeat's own — one probe per interval,
  settled to `Off` by the same sweep after the timeout.
- **Statuses**: `On` (answered), `Off` (timed out), `Unknown` (nothing settled
  yet — expected for one interval after a fresh deployment or against an empty
  store). As with endpoints, the status is the last *settled* outcome.
- **Session**: the probe uses `"Heartbeat-Resolver"`, not the shared
  `"Heartbeat"` session. Resolver subscriptions are session-enabled, and sharing
  one session would queue the probe behind every endpoint heartbeat reply and
  inflate the reported round-trip.

Before this existed, every endpoint reading `Off` at once was the only hint that
the Resolver had stopped — it is the Resolver that records every heartbeat reply.
The Platform services card now says so directly.

## Storage

Heartbeat rows are keyed within an endpoint by the probe's `CorrelationId`,
falling back to its `MessageId` — stored as `Heartbeat.MessageId` — so the
`Pending` row written at send time and the answer that settles it share one row.

Two types are called `Heartbeat` and they are not interchangeable:
`NimBus.Core.Events.Heartbeat` is the event on the wire, and
`NimBus.MessageStore.States.Heartbeat` is the stored row. Code that needs both
imports one under an alias — do not derive the wire `EventTypeId` from `nameof`
on an aliased import, which is how "CoreHeartbeat" once reached the wire and
silently broke every adapter's auto-answer.

| | SQL Server | Cosmos DB |
| --- | --- | --- |
| Endpoint heartbeats | `Heartbeats` table (migration `0018_PlatformHeartbeat.sql`) | Embedded in the endpoint's document in the `Metadata` container, pruned to the last 20 |
| Per-endpoint opt-in and rollup | `EndpointMetadata.IsHeartbeatEnabled` / `.EndpointHeartbeatStatus` | Same fields on the endpoint document |
| Schedule | `HeartbeatSettings` single-row table | Singleton document in the `settings` container |
| Service liveness | `ServiceHealth` table | `servicehealth` container |
| Daily uptime | `HeartbeatUptimeDays` table (migration `0019_HeartbeatHistory.sql`) | `heartbeatuptimedays` container, partitioned by endpoint |
| Outage gaps | `HeartbeatGaps` table (migration `0019_HeartbeatHistory.sql`) | `heartbeatgaps` container, partitioned by endpoint |

Service liveness deliberately does **not** share the endpoint heartbeat store.
On Cosmos, heartbeats live inside the endpoint metadata document, so a
`"Resolver"` heartbeat would surface as a phantom endpoint in every scan of that
container. The schedule singleton is kept out of `Metadata` for the same reason.

A store that has never been written returns schedule defaults rather than null,
and the service-health row is created on first probe, so an empty database
behaves like a disabled-but-healthy one instead of erroring.

## Heartbeat history page

The top-level **Heartbeat** page is available to site Readers and above. It
shows current fleet reachability, weighted uptime, UTC-day history cells, SDK
versions, and recent or ongoing gaps. Use the 7, 30, or 90 day controls to
change the history window. **Settings → Heartbeat probing** is the place to change
the schedule, send a probe immediately, or opt an endpoint out.

History is folded from the retained per-probe rows after every timeout sweep.
The fold has its own durable interval claim, so it continues while scheduled
fan-out is disabled and runs only once in a scaled-out WebApp. Failures are
fail-soft for current liveness and emit both a warning and the
`nimbus.store.operation.failed` counter with operation
`heartbeat_history_fold`; persistent failures must be fixed before retained
probe rows age out.

The history has a few deliberate interpretation rules:

- Uptime is `received / expected` for probes actually sent. `Unsupported` and
  a settled `Unknown` prove reachability and count as received; only `Off`
  counts as missed. An in-flight `Pending` probe is not folded yet.
- Daily cells are UTC calendar days, not rolling 24-hour windows. Green means
  no misses and at least 90% of that UTC day was observed; incomplete coverage
  stays amber even when every observed probe answered.
- Observation coverage comes from the interval stored with each probe, so a
  schedule change does not rewrite earlier history. Legacy probes without an
  interval use the current clamped interval as a fallback.
- History starts empty on a new or upgraded database and fills only as sweeps
  run. No backfill is inferred for time when the WebApp was not observing.
- Closed daily rows and gaps are retained for 90 days. An ongoing gap is never
  pruned merely because it began before that window.

## Scale-out

Both send paths are claimed with a single atomic conditional write —
rows-affected on a SQL `UPDATE`, an ETag replace on Cosmos — so exactly one
WebApp instance sends per interval no matter how many are running. A claim also
records the probe as in flight; the Resolver's row is considered in flight
precisely while it carries a probe message id.

Status changes reach the browser live over SignalR, published from the Resolver
through the storage hook. If the Heartbeat page stops updating on its own but a
refresh shows fresh data, suspect the hook or the SignalR connection rather than
the heartbeat.

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Every endpoint flips to `Off` at once | The Resolver is down — it records every reply. Check the platform services card first. |
| One endpoint reads `Unsupported` | That adapter runs a pre-heartbeat SDK. Reachability is fine; upgrade the package. |
| An endpoint stays `Unknown` | It has never been probed: heartbeats disabled, the endpoint opted out, or no fan-out has run yet. |
| Everything stays `Pending` | Replies are not coming back. The probes are being sent, so look at the Resolver and the endpoint's Resolver subscription. |
| Heartbeat rows appear in the Flow or Monitor pages | The Resolver divert is not matching. Check that the probe carries the exact `EventTypeId` `"NimBus.Platform.Heartbeat"`. |
| Statuses only change on manual refresh | The storage hook or the SignalR connection is broken, not the heartbeat itself. |
