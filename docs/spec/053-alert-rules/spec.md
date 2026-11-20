# Spec 053 — Alert rules

Status: **proposed** (2026-11-20, revision 2). Nothing is implemented. Target: a 5.x minor.
The open questions were answered on 2026-11-20 (§13); the decisions in §12 await confirmation.
Baseline: master `108dd6c2` plus the endpoint alert webhooks delivered in the same pull request as
this spec ([plan](../../plan/2026-11-16-alert-webhook.md)).
Related: [Spec 051](../051-operations-agent-support/spec.md) §2 (the webhook this spec reuses),
[endpoint alert mail plan](../../plan/2026-11-05-endpoint-alert-mail.md).
Scope: a new alert-rule store on all three providers, a rule evaluator in the WebApp, the rule API
and its page on the endpoint's **Alerts** tab, mail and webhook delivery of alert state changes,
deploy settings and docs.
Unchanged: the Resolver's processing, and the existing per-failure alert subscriptions, which keep
working as they do today.

## 1. Summary

Today an endpoint Owner can subscribe a mail address or a webhook to an endpoint, and the Resolver
notifies it when an event on that endpoint fails or is dead-lettered, at most once per frequency
window. There is one fixed condition ("a failure happened") and no state: a long incident either
sends one notice per window for hours, or, with a long window, goes quiet while it is still going on.

This spec adds **alert rules**. A rule is a condition evaluated on a schedule against the message
store, for example "5 or more unresolved failures on `ErpEndpoint` in 15 minutes, whose error text
contains `timeout`". A rule is **Ok** or **Firing**. Its recipients are told once when it starts
firing and once when it resolves, with a link to the matching messages in the WebApp.

Phase 1 ships one condition, the failure count, which covers both the threshold and the error-match
use case. Phase 2 adds conditions only NimBus can evaluate: how long a failure has held its session
blocked, and an endpoint that has gone silent (§9).

## 2. Current state

| Aspect | Today (`108dd6c2` + alert webhooks) |
|---|---|
| Subscriptions | `EndpointSubscription` (`src/NimBus.MessageStore.Abstractions/States/EndpointSubscription.cs`): type (`mail`, `webhook`, `mail;webhook`), address or URL, event-type filter, `Frequency` in seconds, `NotifiedAt` |
| Trigger | `SubscriptionAlertNotifier` (`src/NimBus.Resolver/Alerts/`), called by the Resolver after it settles an applied `Failed` or `DeadLettered` outcome |
| Throttle | One notice per subscription per `Frequency` window, claimed with `ISubscriptionNotificationStore.TrySetNotifiedAtAsync` so scaled-out Resolvers send once |
| Channels | Mail over SMTP (`NimBus:Alerts:Mail`, Resolver only); signed webhook with an optional Entra bearer token (`NimBus:Alerts:Webhook`, Resolver signs, WebApp shows the secret once) |
| Conditions | None besides "an applied failure". `Payload` filters are stored but not evaluated: a subscription with one is skipped; `ErrorList` and `NotificationSeverity` are stored and not read by the notifier |
| State | None: no "firing" or "resolved", no history |
| Scheduling in the WebApp | `HeartbeatBackgroundService` ticks every 30 s; scaled-out instances agree on one sender per interval through store claims (`TryClaimHeartbeatSend`) |
| Adapter-side alerts | `NimBus.Extensions.Notifications` (email, Teams, webhook) runs in the adapter with recipients fixed in code; out of scope here |

Store queries a rule evaluator can reuse:

- `IMessageTrackingStore.GetFailedEventHistogram(EventFilter, endpointIds, fromUtc, toUtc, bucketSize)`
  counts unresolved failures (`Failed`, `DeadLettered`, `Unsupported`) whose `UpdatedAt` falls in a
  window, per endpoint and status. `EventFilter` carries `EventTypeId`, `ResolutionStatus` and
  `ErrorText` (case-insensitive substring).
- `IMonitorMetricsStore.GetBlockedSessionCount(endpointId)` estimates the sessions held by a
  failure or a pending handoff.
- `IMetricsStore.GetEndpointMetrics(from, to)` gives handled and failed counts per endpoint and
  event type.

## 3. Goals

1. An endpoint Owner can define rules on their endpoint: a failure-count condition with optional
   event-type, status and error-text filters, a threshold and a window.
2. A rule has a state (Ok, Firing) and notifies its recipients when the state changes, not on every
   failure.
3. Rules reuse the existing mail and webhook channels, the webhook signature and the bearer-token
   mode. No new transport.
4. Evaluation is exact once across scaled-out WebApp instances.
5. Rules are visible to everyone who can read the endpoint: what the rule is, whether it is firing,
   and since when.
6. The existing per-failure subscriptions keep working unchanged.

## 4. Non-goals

- On-call schedules, escalation, acknowledgement workflows or paging. A webhook to the team's
  incident tool (or an Azure Monitor action group) does that.
- A query language or arbitrary expressions. Conditions are a fixed set of kinds with parameters.
- Alerts on message payload content. Payloads stay out of alerting, as they are today.
- Site-wide rules and rules across endpoints in Phase 1 (§13, answer 2). A later phase may add them.
- Reminders while a rule is firing (§13, answer 3).
- Teams delivery. It remains unimplemented for subscriptions too.
- Changing how the Resolver processes or settles messages.

## 5. Design

### 5.1 The rule

```jsonc
{
  "id": "c1f0…",                 // GUID
  "endpointId": "ErpEndpoint",
  "name": "ERP timeouts",        // 1–100 chars
  "enabled": true,
  "condition": {
    "kind": "failure_count",
    "eventTypes": ["CustomerChanged"],          // optional; empty = all
    "statuses": ["Failed", "DeadLettered"],     // optional; empty = Failed, DeadLettered, Unsupported
    "errorTextContains": "timeout",             // optional; ≤ 200 chars, case-insensitive
    "threshold": 5,                             // 1–10,000
    "windowMinutes": 15                         // 5–1,440
  },
  "resolveAfterMinutes": 5,      // condition false this long before Resolved; 1–60
  "targets": [
    { "id": "t1", "type": "mail", "address": "erp-team@example.com" },
    { "id": "t2", "type": "webhook", "url": "https://hooks.example.com/nimbus" }
  ],
  "createdBy": "owner@example.com",
  "updatedAtUtc": "2026-11-20T09:00:00Z",
  "state": { … }                  // §5.4, written only by the evaluator
}
```

The failure count is the number of unresolved failures on the endpoint matching the filters whose
`UpdatedAt` is inside the window: one histogram query per rule (§5.3). It covers both use cases in
the request that led to this spec: "N failures in a period" (threshold N) and "a certain type of
error" (an error-text filter with threshold 1).

**The count is of unresolved failures.** An event that is resubmitted and completes, or skipped,
leaves the count. That is the behaviour an alert wants: when an operator clears the failures, the
rule resolves. It is not a count of failures that ever happened in the window; the docs say so.

Limits: 20 rules per endpoint, 10 targets per rule. Mail targets reuse the subscription's address
validation; webhook targets reuse `EndpointAlertWebhook.IsValidUrl` and the Resolver's connect
rules (§5.6).

### 5.2 Rules own their recipients

A rule carries its own targets instead of pointing at the endpoint's subscriptions. A subscription
has its own event-type filter and frequency window, which mean nothing for a rule, and a recipient
who wants "every failure" and one who wants "only when it is bad" are different people.

The webhook signing secret of a target is derived as for subscriptions, under its own label so a
rule target and a subscription never share a secret:

```text
secret = "nbwh_" + base64url(HMAC-SHA256(SigningKey, "nimbus-alert-rule-webhook/v1/" + ruleId + "/" + targetId))
```

It is shown once when the target is added, as for a subscription.

### 5.3 The evaluator

An `AlertRuleEvaluatorService : BackgroundService` in the WebApp, modelled on
`HeartbeatBackgroundService`: a `PeriodicTimer` every 60 s, one scope per tick, every failure logged
and swallowed.

Each tick:

1. List the enabled rules (`IAlertRuleStore.ListEnabledAsync`).
2. For each rule whose `state.nextEvaluationAtUtc` is due, claim it:
   `TryClaimEvaluationAsync(ruleId, expectedVersion, nextEvaluationAtUtc = now + 60 s)`. A claim
   that loses (another instance took it) skips the rule. This gives one evaluation per rule per
   minute however many WebApp instances run.
3. Evaluate: `GetFailedEventHistogram(filter, [endpointId], now - window, now, window)` and sum
   the buckets. `filter` maps `eventTypes`, `statuses` and `errorTextContains` to `EventFilter`.
4. Apply the state machine (§5.4) and save the new state with the claim's version.
5. If the state changed, send the notices (§5.5) after the state is saved. A failed send is logged.
   The state change stands; there is no retry, as for subscriptions.

**Why the WebApp and not the Resolver.** The Resolver runs on Service Bus triggers and, on Flex
Consumption, scales to zero when there is no traffic, which is exactly when a silence rule (Phase 2)
must still run. The WebApp already hosts scheduled work (the heartbeat and the operator-operation
reconciler; Spec 050 puts its SQL cleanup there too) and reads the same store. The cost: no rule is evaluated while
the WebApp is down. The Resolver's per-failure subscriptions stay independent of the WebApp, and the
docs recommend one for the critical endpoints (§11).

### 5.4 State

```jsonc
"state": {
  "status": "Ok" | "Firing",
  "since": "2026-11-20T09:14:00Z",          // when the current status began
  "lastValue": 7,                           // last count
  "lastEvaluatedAtUtc": "2026-11-20T09:20:00Z",
  "conditionFalseSinceUtc": null,           // while Firing: first evaluation that found it false
  "nextEvaluationAtUtc": "2026-11-20T09:21:00Z",
  "version": 412,                           // compare-and-set token for claims
  "transitions": [ { "status": "Firing", "atUtc": "…", "value": 7 }, … ]   // last 20
}
```

- **Ok → Firing** when `lastValue >= threshold`. Notice: *opened*.
- **Firing → Ok** when the condition has been false for `resolveAfterMinutes` without a break.
  Notice: *resolved*. A true evaluation during that time clears `conditionFalseSinceUtc`. This
  stops a count hovering around the threshold from sending an opened/resolved pair every minute.
- No repeated notices while Firing: one *opened* notice, and the receiving tool tracks the incident
  (§13, answer 3).
- Editing a rule's condition or disabling it resets the state to Ok without a notice. The edit is
  recorded in `transitions`.
- A rule whose endpoint is deleted is deleted with it.

### 5.5 Notices

Mail: subject `[NimBus] Firing: ERP timeouts on ErpEndpoint` (or `Resolved: …`), a body with the
rule's condition in words, the count, the window, when it started and a link to the endpoint's
failed-messages view filtered like the rule. `AlertMailComposer` gets a second template.

Webhook: the request of Spec 051 §2 (`POST`, `X-NimBus-*` headers, `v1=` HMAC signature, optional
bearer token for an administrator-listed URL prefix) with a version-2 body. Version 1 bodies stay
as they are for subscriptions.

```jsonc
{
  "version": 2,
  "kind": "alert_rule",
  "ruleId": "c1f0…",
  "ruleName": "ERP timeouts",
  "endpointId": "ErpEndpoint",
  "status": "Firing",
  "value": 7,
  "threshold": 5,
  "windowMinutes": 15,
  "sinceUtc": "2026-11-20T09:14:00Z",
  "webUiUrl": "https://nimbus.example.com/…"   // the endpoint's failed messages, filtered like the rule
}
```

No payloads and no error text from messages. The rule's own `errorTextContains`, which the Owner
wrote, may appear in the mail body.

### 5.6 Delivery in the WebApp

The evaluator sends from the WebApp, so the senders move to a shared place:

- `AlertMailComposer`, `SmtpAlertMailSender`, `AlertWebhookSender`, `AlertWebhookConnectGuard`
  and their options move from `NimBus.Resolver` to a library both hosts reference (for example
  `NimBus.Alerts`, or `NimBus.Core` if it has no new dependencies). The Resolver keeps using them
  through the same interfaces.
- The WebApp gets the SMTP settings (`NimBus:Alerts:Mail:*`) and the bearer-token prefixes
  (`NimBus:Alerts:Webhook:BearerTokens`), which only the Resolver has today. It already has the
  signing key. Bicep, `nb setup` and `nb infra apply` pass them to both apps.
- The WebApp's identity needs the same role on an SRE Agent (or other token audience) as the
  Resolver's when a rule target uses the bearer-token mode.

### 5.7 Storage

New `IAlertRuleStore` in `NimBus.MessageStore.Abstractions`:

```csharp
Task<IReadOnlyList<AlertRule>> ListByEndpointAsync(string endpointId, CancellationToken ct = default);
Task<IReadOnlyList<AlertRule>> ListEnabledAsync(CancellationToken ct = default);
Task<AlertRule?> GetAsync(string ruleId, CancellationToken ct = default);
Task<AlertRule> UpsertAsync(AlertRule rule, CancellationToken ct = default);   // definition only; keeps state
Task<bool> DeleteAsync(string ruleId, CancellationToken ct = default);
Task<bool> TryClaimEvaluationAsync(string ruleId, long expectedVersion, DateTime nextEvaluationAtUtc, CancellationToken ct = default);
Task<bool> TrySaveStateAsync(string ruleId, long expectedVersion, AlertRuleState state, CancellationToken ct = default);
```

- **Cosmos DB:** a new `alert-rules` container partitioned by `/endpointId`; claims use the item's
  ETag. `ListEnabledAsync` is a cross-partition query over a small container.
- **SQL Server:** migration `0029_AlertRules.sql`, a table with the definition and state as JSON
  columns plus `Version`, `EndpointId`, `Enabled` and `NextEvaluationAtUtc`; claims are a
  conditional `UPDATE … WHERE Version = @expected`.
- **In-memory:** a dictionary under a lock.
- The conformance suite covers create, read, list, delete, a lost claim, a state save with a stale
  version and the state kept by `UpsertAsync`.
- `InstrumentingMessageTrackingStoreDecorator` decorates `IMessageTrackingStore` only. The new store
  is a separate interface and needs no forwarding there (§14, fact 4).

### 5.8 API and permissions

Under `/api/v1/endpoints/{endpointId}/alert-rules` in `api-spec.yaml` (NSwag generates the
controller and client):

| Method | Path | Who |
|---|---|---|
| GET | `/` | Anyone who can read the endpoint. Targets show the address or host, never a secret |
| POST | `/` | Endpoint Owner or Site Owner. Returns the rule and each new webhook target's secret, once |
| PUT | `/{ruleId}` | Same. Existing targets keep their id and secret; new ones get a secret |
| DELETE | `/{ruleId}` | Same |
| POST | `/{ruleId}/preview` | Same. The count for the last 24 h in `windowMinutes` buckets (Phase 3) |

Validation errors answer 400 with problem details. Every create, change and delete is written to
the audit trail.

### 5.9 The page

The endpoint's **Alerts** tab gets two sections: **Rules** (new) and **Every failure** (today's
subscriptions, unchanged).

- The rules table: name, condition in words ("≥ 5 failures in 15 min, error contains 'timeout'"),
  a status badge (Ok, Firing since 09:14), last value, targets, and a ⋯ menu (Edit, Disable,
  Delete), following the Manage pages' table and inspector drawer.
- **New rule** opens the form in the drawer: name, filters, threshold, window, resolve-after,
  targets. A webhook target's secret is shown once with Copy, as for subscriptions.
- The endpoint list shows a small Firing marker beside an endpoint with a firing rule.

## 6. Existing deployments

Nothing changes until someone creates a rule. The Cosmos container and the SQL table are created
on start-up like the other stores. Rules send mail only once the WebApp has SMTP settings. Without
them, a mail target is refused with 400, as a webhook subscription is without a signing key.

## 7. Evaluation cost

Each enabled rule costs one histogram query a minute. With the limit of 20 rules per endpoint,
50 endpoints at the limit means 1,000 queries a minute. That is acceptable on SQL Server; on Cosmos
DB the RU charge of `GetFailedEventHistogram` over a 15-minute window must be measured (§14,
fact 1) before choosing the evaluation interval. If it is too high, the interval becomes
configurable (`NimBus:Alerts:Rules:EvaluationInterval`, minimum 60 s) and rules on the same
endpoint with the same filters share one query.

## 8. Tests

- Conformance: §5.7.
- Evaluator (in-memory store, fake clock): fires at the threshold and not below; resolves only
  after `resolveAfterMinutes` of false; a true evaluation during that time keeps it firing; one
  evaluation per minute when two evaluator instances share a store; a lost claim skips; a failed
  send keeps the new state; editing resets to Ok without a notice; a disabled rule is not evaluated.
- Filters: each of event types, statuses and error text narrows the count on all three providers
  (conformance, because `ErrorText` matching is provider code).
- Notices: mail subject and body for both statuses; webhook v2 body, headers, signature with the
  derived rule-target secret, bearer token only under a listed prefix.
- API: 403 for a Reader on writes, 400 per validation rule, secret returned once and never from
  GET, audit entries written.
- Frontend: the rules table, the form and its validation, the secret panel, the Firing marker.
- CrmErpDemo end to end: a rule of threshold 3 in 5 minutes on `ErpEndpoint`, ERP error mode on,
  expect one *opened* POST to a local signature-checking listener; error mode off and resubmit,
  expect one *resolved* POST after the resolve time.

## 9. Phases

| Phase | Content |
|---|---|
| 0 | Verify §14. Move the senders to the shared library with no behaviour change |
| 1 | Store, evaluator, `failure_count`, state machine, mail and webhook notices, API, the Rules section, deploy settings, docs |
| 2 | `blocked_session`: a Failed or handoff event has held its session for more than *T* minutes, or more than *N* events wait behind it. Needs a new store query for the oldest blocking event per endpoint (all providers, conformance). `silence`: no message handled on the endpoint for *T* minutes, from `GetEndpointMetrics(from, to)` |
| 3 | Preview over the last 24 h; a failure-classification category filter (only when Integration Intelligence is on, and only for classified failures); firing rules in the Monitor and in MCP `nimbus_get_worklist` |
| v6 | Subscriptions become a built-in rule (§13, answer 1): deprecated with `[Obsolete]` and a bridge in a 5.x minor once rules ship, migrated and removed in v6 (`docs/versioning.md`). How a subscription's per-window notice maps onto a rule needs its own spec before v6 |

Each phase is its own pull request; a phase's parts are commits in it.

## 10. Documentation

- `docs/notifications.md`, "Endpoint alerts": rules next to subscriptions, when to use which, the
  count semantics of §5.1, the state machine, the v2 webhook body and how to verify it.
- `docs/deployment.md` and `docs/cli.md`: the WebApp's new mail and token settings.
- `samples/CrmErpDemo/README.md`: a rule in the failure showcase.

## 11. Residual risks

- **WebApp down, no rules.** Rules depend on the WebApp's uptime. Mitigation: the docs recommend a
  per-failure subscription (Resolver) for critical endpoints, and an uptime check on the WebApp
  from outside NimBus.
- **Unresolved-only count.** A burst that operators resolve within the window never fires. That is
  intended, but some users may expect a raw failure rate. A Phase 3 `failure_rate` condition over
  `GetEndpointMetrics` would cover it.
- **Clock skew between WebApp instances.** Claims compare a stored version, not clocks, so skew
  only shifts when a rule is evaluated by a few seconds.
- **Mail volume when many rules fire at once** (one outage, many endpoints). Notices are per rule
  state change, so the volume is bounded by the number of rules, not of failures.

## 12. Decisions (proposed, for the repo owner)

1. Evaluate in the WebApp, not the Resolver (§5.3).
2. Rules own their targets; subscriptions are unchanged until v6 (§5.2, §13 answer 1).
3. Phase 1 has one condition kind, `failure_count`, counting unresolved failures (§5.1).
4. Notices only on state changes; no repeat while firing (§5.4, §13 answer 3).
5. Rules are per endpoint and managed by the endpoint's Owner (§5.8).

## 13. Open questions (answered 2026-11-20)

1. Should the subscriptions become a built-in rule ("any failure, notify per window") in v6, so
   there is one alert model?
   **Answer: yes.** Rules are the one alert model in v6; see the v6 row in §9.
2. Are site-wide rules needed ("any endpoint, ≥ 20 failures in 10 min")? They need Site Owner
   permission and a cross-endpoint histogram; the store call already takes several endpoint ids.
   **Answer: not in Phase 1.** A later phase may add them.
3. Should a firing rule remind its recipients (for example every 4 h), or is one *opened* notice
   enough when the receiving tool tracks the incident?
   **Answer: no reminders.** One *opened* and one *resolved* notice per incident.
4. Should the failure-classification category be a Phase 1 filter? It is the strongest signal
   ("infrastructure, not data"), but it exists only when Integration Intelligence is on and arrives
   after the failure, so a rule would count a failure only once it is classified.
   **Answer: no, Phase 3** (§9).

## 14. Facts to verify (Phase 0)

1. RU charge and latency of `GetFailedEventHistogram` on Cosmos DB for one endpoint and a
   15-minute window, with and without `ErrorText`.
2. `ErrorText` filtering in `GetFailedEventHistogram` behaves the same on Cosmos DB, SQL Server and
   in-memory (case-insensitivity, substring).
3. The senders can move out of `NimBus.Resolver` without new package dependencies on the WebApp.
4. No composite registration routes the new store through the OpenTelemetry decorator, and whether
   it should get its own instrumentation.
5. The SQL migration number is still free when Phase 1 starts (`0028` is the latest today).
6. How the WebApp is scaled in the Bicep templates (App Service plan instance count), to size the
   claim tests realistically.

## 15. References

- `src/NimBus.Resolver/Alerts/SubscriptionAlertNotifier.cs`: today's per-failure notifier.
- `src/NimBus.Resolver/Alerts/AlertWebhookSender.cs`, `AlertWebhookConnectGuard.cs`: the webhook
  channel.
- `src/NimBus.MessageStore.Abstractions/IMessageTrackingStore.cs`: `GetFailedEventHistogram`.
- `src/NimBus.MessageStore.Abstractions/IMonitorMetricsStore.cs`: `GetBlockedSessionCount`.
- `src/NimBus.WebApp/Services/Heartbeat/HeartbeatBackgroundService.cs`: the scheduling pattern.
- `src/NimBus.Extensions.IntegrationIntelligence/QuestionSet.cs`: the failure categories.

## 16. Review trail

- 2026-11-20: first draft.
- 2026-11-20, revision 2: the repo owner answered §13. Subscriptions become a built-in rule in v6;
  no site-wide rules in Phase 1; no reminders while firing (removed from Phase 3); the
  classification filter stays in Phase 3.
