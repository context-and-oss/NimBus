# Rate Limiting

The NimBus management WebApp applies request-rate policies to its
highest-cost surfaces. Everything else — the SignalR grid-events hub, the
health probes, the SPA's static assets, and every other `/api/*` route — carries
no rate-limiting metadata at all. There is no global limiter, so "not listed
here" means "not throttled".

Exceeding a limit returns **HTTP 429**.

## The policies

| Policy | Endpoint(s) | Limiter | Default | Partitioned by |
|---|---|---|---|---|
| `nimbus-agent-receive` | `GET /api/agent/receive` | Concurrency | 20 permits + 5 queued | nothing (global) |
| `nimbus-admin` | every `/api/admin/*` route | Fixed window | 60 per 60 s | user id |
| `nimbus-search` | `POST /api/messages/search`, `POST /api/audits/search` | Fixed window | 120 per 60 s | user id |
| `nimbus-login` | `POST /account/login` | Fixed window | 50 per 300 s | client IP |
| `nimbus-intelligence` | `POST /api/integration-intelligence/failures/{eventId}/{messageId}/classification` | Fixed window | 10 per 60 s | user id |
| `nimbus-mcp` | `/mcp` (operator MCP endpoint, when enabled) | Fixed window | 60 per 60 s | tenant + client application + user |

The operator MCP tools that change a message (resubmit, skip, report, classify) are further limited
to 5 per 60 s per tenant, client application and user (`RateLimiting:McpMutations`). Every tool
call shares the one `/mcp` route, so this limit is counted inside the tools and answered with a
`[RateLimited]` tool error rather than a 429. It is off when `Enabled` is false.

A site Owner can lower both MCP limits in **Settings → MCP access** without a restart (Spec 037). The
configured values are the ceiling. The effective limit is part of the partition key, so a changed
limit starts a new window rather than reusing the cached limiter.

`GET /account/login` — the sign-in *page* — is deliberately not throttled;
only the credential POST is.

## What a fixed window does and does not bound

`FixedWindowRateLimiter` replenishes its full permit count on a timer. That
gives exactly three guarantees, and it is worth being precise about them
because the third is the one that matters and the first is the one people
quote:

1. **Per window** — at most `PermitLimit` requests inside any one window.
2. **Boundary burst** — up to **2 × `PermitLimit`** inside an arbitrarily short
   interval that straddles a window edge, because the last permits of one
   window and the first of the next can be spent back to back.
3. **Sustained rate** — `PermitLimit` / `Window` over any long period.

| Policy | Per window | Across a window edge | Sustained |
|---|---|---|---|
| `nimbus-admin` | ≤ 60 per user | ≤ 120 | 3,600/hour per user |
| `nimbus-search` | ≤ 120 per user | ≤ 240 | 7,200/hour per user |
| `nimbus-login` | ≤ 50 per address | ≤ 100 | 600/hour per address |
| `nimbus-intelligence` | ≤ 10 per user | ≤ 20 | 600/hour per user |
| `nimbus-mcp` | ≤ 60 per caller and client | ≤ 120 | 3,600/hour per caller and client |
| MCP changes (`McpMutations`) | ≤ 5 per caller and client | ≤ 10 | 300/hour per caller and client |

**A caller pacing at or below the sustained rate is never rejected**, and
therefore never logged. These policies are rate ceilings, not intrusion
detectors — do not read the 429 warnings as spray detection.

Worked example for login: 50 per 300 s is 600 attempts/hour, so a patient
sprayer reaches 1,000 distinct accounts in about 100 minutes **with zero
rejections**. Without the limiter the same 1,000 accounts take seconds. The
control converts a burst attack into a slow one and buys time; it does not stop
a patient attacker. The controls that do are the per-account Identity lockout
(five failed attempts locks that account for 15 minutes) and MFA. If boundary
bursts ever matter, the sliding-window limiter is the upgrade path.

## What the receive limiter does and does not bound

The concurrency limiter bounds *simultaneous work*, not request rate. It
guarantees at most **20 executing** receive handlers plus **5 queued** =
**25 in-flight requests**; the 26th is rejected. Unlike the fixed windows there
is **no boundary burst** — permits are returned on completion, not replenished
on a clock — so 25 in flight is a true instantaneous invariant.

A queued request runs no handler code and issues zero store queries; it holds
only a request slot and its connection.

Separately, a poll *waiting* for an event issues 2 store queries per second (a
500 ms delay loop), so waiting polls contribute at most **40 queries/second** in
total. **That is not a general RU-per-second ceiling.** A request passing
`waitSeconds=0`, or one finding a parked event, returns immediately and releases
its permit, so a client looping such requests is bounded by 20-way concurrency
and round-trip time, not by 40/second. An absolute query-rate ceiling would need
a rate component on this endpoint, which is deliberately out of scope here.

Set `RateLimiting:AgentReceive:QueueLimit = 0` for a hard 20-slot cap, at the
cost of 429-ing an agent-host restart herd instead of absorbing it.

Agent API keys ([Spec 039](spec/039-agent-api-keys/spec.md)) are checked during
authentication, which runs before the rate limiter, so failed key attempts are not
throttled. Keys carry a 256-bit secret, which makes guessing one infeasible, and every
refusal is logged with the key id and client IP. A successful agent request is
partitioned by its agent (`agent:<agentId>`) wherever a policy partitions by user.

One pathological interaction worth knowing: if 20 callers each hold a 60-second
poll and a 21st queues, the client-observed time approaches 120 s while the SDK's
`HttpClient` uses the 100 s default timeout. In practice `ReceiveWaitSeconds`
defaults to 5, and the SDK's agent loop retries after its error backoff, so this
self-heals.

## Sizing the login limit for your egress

```
PermitLimit ≈ 3 × (operators who may sign in within one 5-minute window) × 2
```

The shipped default of 50 assumes **8 operators behind one NAT'd address**
(8 × 3 = 24 requests, 2.08× headroom). Two directions to adjust:

- **Lower to ~10** for a single-operator deployment or one without a shared
  egress. Note this tightens the sustained ceiling proportionally: 10 per 5 min
  is 120/hour.
- **Raise** for a larger team behind one NAT.

The default is sized for availability over maximum spray resistance on purpose:
a false 429 on `/account/login` locks the operations team out of the tool they
use during an incident.

## How a client is identified

The login partition key is the client's **full IP address**, canonicalised:

- IPv4-mapped IPv6 (`::ffff:203.0.113.7`) is unmapped, so a dual-stack socket
  does not give one client two buckets.
- The scope id is dropped (`fe80::1%12` and `fe80::1%13` are one peer).
- `IPAddress.ToString()` normalises casing and zero-compression, so
  `2001:0DB8:0000::0001` and `2001:db8::1` cannot become two buckets.

One client always lands in exactly one bucket, and two clients never share one.

### Forwarded headers

`RateLimiting:TrustForwardedForHeader` (default **false**) controls whether
`X-Forwarded-For` may supply the address. When false the header is ignored
entirely — an untrusted forwarded address must never be able to move a caller's
partition, or any caller could pick a fresh bucket per request.

When true, the **last** comma-separated hop is read, not the first. Azure App
Service *appends* the client address it observed (as `ip:port`) to any inbound
`X-Forwarded-For`, so the rightmost entry is the one the trusted proxy wrote.
Reading the leftmost entry — the common mistake — lets any caller prepend an
arbitrary partition and walk straight past the limiter.

**Only enable this behind a proxy that always rewrites the header.** If the app
is directly reachable, a caller can forge the whole header.

### Residual risk

A per-address limiter does not stop an attacker who can rotate source
addresses — a routed IPv6 /64, or a botnet. Each fresh address buys a fresh
budget. `RateLimiting:Login:IPv6PrefixBits` (default **128**, i.e. the full
address) can be set to `64` under such an attack to bucket IPv6 by prefix, at
the cost of merging a site's IPv6 users into one bucket.

## Configuration

All values bind from the `RateLimiting` section, so an operator can retune or
disable without a redeploy. Values are read once at startup; a change requires
a restart, which an App Service application-setting change triggers anyway.

| Key | Default |
|---|---|
| `RateLimiting:Enabled` | `true` |
| `RateLimiting:TrustForwardedForHeader` | `false` (Bicep sets `true` for App Service) |
| `RateLimiting:AgentReceive:PermitLimit` | `20` |
| `RateLimiting:AgentReceive:QueueLimit` | `5` |
| `RateLimiting:Admin:PermitLimit` | `60` |
| `RateLimiting:Admin:WindowSeconds` | `60` |
| `RateLimiting:Search:PermitLimit` | `120` |
| `RateLimiting:Search:WindowSeconds` | `60` |
| `RateLimiting:Login:PermitLimit` | `50` |
| `RateLimiting:Login:WindowSeconds` | `300` |
| `RateLimiting:Login:IPv6PrefixBits` | `128` |
| `RateLimiting:Intelligence:PermitLimit` | `10` |
| `RateLimiting:Intelligence:WindowSeconds` | `60` |
| `RateLimiting:Mcp:PermitLimit` | `60` |
| `RateLimiting:Mcp:WindowSeconds` | `60` |
| `RateLimiting:McpMutations:PermitLimit` | `5` |
| `RateLimiting:McpMutations:WindowSeconds` | `60` |

In App Service these are environment-variable style keys:
`RateLimiting__Login__PermitLimit`, and so on.

**`Enabled: false` is the kill switch** if a limit is mis-sized in production.
It stops the policies being *attached* to endpoints; they stay registered, so
the app still starts. It is not template-managed, so it survives a redeploy.

### What survives a redeploy

App-settings deployment is a full replace. `deploy.webapp.bicep` carries
operator-set `RateLimiting__*` settings forward, with one exception:

- **`RateLimiting__TrustForwardedForHeader` is template-owned** — it describes
  deployment topology (is there a trusted proxy?), not a tuning preference, so a
  portal edit to it does **not** survive `nb infra apply`.
- Every other `RateLimiting__*` key, including `RateLimiting__Enabled`, is
  preserved.

## On 429

Rejections carry a short `text/plain` body naming the policy — `POST
/account/login` is a browser form surface, where a bodyless 429 shows a human
nothing. The fixed-window policies also emit `Retry-After`; the
concurrency limiter does not, because it has no meaningful retry hint (permits
return on completion, not on a clock).

Every rejection *that occurs* is logged at warning level with the policy name
and partition key, which for login is a client IP — deliberate security
telemetry, since it is the only field making an attempt attributable. As above:
a caller pacing under the limit is never rejected and so never appears there.

Neither the SPA nor the `nb` CLI currently surfaces or retries on 429. The `nb`
CLI calls none of these surfaces, and the limits are sized so legitimate
SPA usage never reaches them.

## Scope

Enforcement is **per process**. The shipped Bicep provisions a single instance,
so per-process limits are the effective limits. If the WebApp is ever scaled
out, effective limits multiply by instance count and the login policy weakens
proportionally — a distributed/shared-state limiter would be needed at that
point.

## See also

- [`docs/webapp-rest-api.md`](webapp-rest-api.md) — the API surface these
  policies protect.
- [`docs/deployment.md`](deployment.md) — which app settings survive a
  redeploy.
