# Simulating traffic from the WebApp

The management WebApp can drive synthetic traffic through the real platform. A site Owner
switches on **simulate mode** in **Settings → Simulation**, and a **Simulate** page appears under
Manage. From there the simulator:

- publishes schema-valid generated events from every endpoint that produces event types, at
  a configurable rate per event type;
- hosts the handlers of the consuming endpoints the Owner has explicitly handed to it, each
  with a failure mode (healthy, random failures, transient, slow, poison, no handler);
- can be started, paused and stopped, with a speed multiplier, scenario presets, counters and
  a live feed of the last 100 deliveries.

Everything runs inside the WebApp process, so it works the same on a deployed dev site as in
the local Aspire stack. The messages are real: they go through Service Bus and the Resolver,
and they show up in Messages, Flow, Insights and the audit log like any other traffic.

## Where it may run

Simulation is gated on the `Environment` setting, which the Bicep deploy writes per
environment (`appsettings.json` defaults it to `dev`). The gate does not use
`ASPNETCORE_ENVIRONMENT`: App Service runs the deployed dev site as `Production`.

| `Environment` value | Result |
|---|---|
| missing or blank | blocked (`EnvironmentMissing`); the gate fails closed |
| `prod`, `production`, `prd`, `live` (any case) | blocked (`Production`), always |
| listed in `NimBus:Simulation:AllowedEnvironments` | allowed |
| anything else | blocked (`NotAllowed`) |

The production names are hard-coded and no setting can allow them. Putting one in
`AllowedEnvironments` fails options validation at startup, so the WebApp does not start.
Outside the gate every simulation endpoint except `GET /api/v1/admin/simulation` returns 403,
and the `GET` reports `allowed: false` with the reason.

All simulation endpoints require the site Owner role and share the admin rate limit (60
requests a minute per user by default, see [rate-limiting.md](rate-limiting.md)). The Simulate
page polls every 2 seconds, so keep one Simulate tab open per user. Every change is audited
(`UpdateSimulationSettings`, `ControlSimulation`, `UpdateSimulationConfig`), including denied
attempts.

## Who handles the messages

A simulated handler reads the endpoint's real subscription. If a real subscriber process for
the same endpoint is running, they compete for the same messages. So ownership is explicit:

- Every consuming endpoint is **External** by default. The simulator publishes to it but
  never hosts its handler; its own process handles the messages.
- An endpoint becomes **Simulated** only when an Owner ticks "Simulator owns this endpoint" in
  Settings → Simulation (with a confirmation), or when it is listed in
  `NimBus:Simulation:OwnedEndpoints`. Unknown or non-consuming endpoints in that list fail
  startup validation.
- Ownership changes apply at the next Start. They are refused (409) while a run is active.
- Heartbeat data is advisory only. When an owned endpoint answered a heartbeat recently while
  the simulator was not hosting it, the Simulation settings and the Simulate page warn that a live
  instance may be competing. Heartbeats never change ownership.

## Failure modes

| Mode | What the handler does | What the platform does |
|---|---|---|
| Healthy | completes after 20–80 ms | Completed |
| Random | throws with probability *rate* % | retried, then Failed and the session blocks |
| Transient | throws while the retry count is below *failAttempts*, then completes | retries, then Completed |
| Slow | completes after *latencyMinMs*–*latencyMaxMs* | exercises lock renewal and throughput |
| Poison | throws a permanent exception | dead-lettered without retrying |
| No handler | reports no handler for the event type | Unsupported |

A mode can be limited to some of the endpoint's event types and to sessions matching a glob
(`*` and `?` only), and can revert to Healthy after a number of minutes. The exception
message is configurable, so Insights groups simulated failures under stable text. Simulated
handlers use a fixed retry policy: 3 retries, 5 seconds apart.

## Marking simulated traffic

Every simulated message has a session id and a correlation id starting with the session
prefix (`sim-` by default). Each publisher rotates its event types through a pool of
`SessionPoolSize` session keys (`sim-StorefrontEndpoint-007`), so a failure blocks later
messages on the same session, as it would in production. Filter Messages by session to find
them.

The counters and feed are what the simulator itself saw: published, handled, handler errors,
poisoned, publish errors and abandoned sends. The Resolver's status in Messages is the
authoritative outcome.

## Configuration

Deploy-time options, bound from `NimBus:Simulation` and validated at startup:

| Key | Range | Default |
|---|---|---|
| `EnabledByDefault` | bool | `false` |
| `AllowedEnvironments` | list, no production names | `dev`, `development` |
| `OwnedEndpoints` | consuming endpoint ids | none |
| `AutoStopMinutes` | 1–240 | 60 |
| `MaxRateCeilingPerMinute` | 1–6000 | 1200 |
| `RateCeilingPerMinute` | 1–`MaxRateCeilingPerMinute` | 600 |
| `SessionPrefix` | 1–16 characters | `sim-` |
| `SessionPoolSize` | 1–1000 | 40 |
| `DrainSeconds` | 0–120 | 30 |
| `PauseDeadlineSeconds` | 1–120 | 10 |
| `StopDeadlineSeconds` | 1–120 | 15 |

`AllowedEnvironments` binds on top of the defaults, so configuring it adds environments
rather than replacing `dev` and `development`.

Runtime limits, enforced on every `PUT` (a 400 lists every violation):

| Setting | Range |
|---|---|
| speed | 0.5, 1, 2, 5, 10 or 20 |
| rate per event type | 1–rate ceiling, per minute, before speed |
| Random rate | 1–100 % |
| Transient fail attempts | 1–3, so the last attempt can succeed |
| latency bounds | 0–10,000 ms, minimum ≤ maximum |
| exception message | 1–512 characters |
| session pattern | up to 64 characters of letters, digits, `-_.:` and `*`/`?` |
| revert after | 1–240 minutes, or never |

The **rate ceiling applies to the whole simulation**, not per publisher. All publisher loops
share one token bucket (a one-second burst at most). When the summed rate × speed exceeds the
ceiling, throughput is capped, the loops share it fairly, and the page shows "capped at N/min".

## Pause, stop and shutdown

- **Pause** stops the publishers and the simulated handlers within `PauseDeadlineSeconds`.
  Messages already published stay in their subscriptions until Resume.
- **Stop** stops the publishers, lets the simulated handlers drain for up to `DrainSeconds`,
  then stops them within `StopDeadlineSeconds`.
- **WebApp shutdown** does one bounded stop without the drain, capped by the host's shutdown
  timeout.
- **Auto-stop** stops a run `AutoStopMinutes` after it started. Disabling simulate mode stops
  a running simulation.

Each publisher owns its Service Bus sender. When a send ignores cancellation past the
deadline, the loop is abandoned, its sender is disposed (which aborts the send), the
"abandoned sends" counter goes up and a warning is logged; the transition still completes.
The next Start builds fresh publishers, senders and handlers.

Stop does not delete anything. To clear a leftover backlog, use **Purge** in Topology → Subscriptions.

## State is per instance

Settings, ownership, config and counters are held in memory by each WebApp instance. A
restart starts again from configuration and stops any run. The dev site runs a single
instance; on a scaled-out plan each instance would run its own simulation.

## Local Aspire stack

Start the AppHost with `NIMBUS_SIMULATION=true` (or `--NIMBUS_SIMULATION true`). The AppHost
then leaves out the sample `publisher`, `subscriber` and `warehouse-subscriber` projects and
configures the WebApp with simulate mode on and BillingEndpoint and WarehouseEndpoint owned:

```powershell
$env:NIMBUS_SIMULATION = "true"
$env:NIMBUS_SB_EMULATOR = "true"   # optional: the local Service Bus emulator
dotnet run --project src/NimBus.AppHost
```

Without the switch the samples run as before, no endpoint is owned, and the Simulate page lists
Billing and Warehouse as External: simulated publishes are handled by the sample processes.
