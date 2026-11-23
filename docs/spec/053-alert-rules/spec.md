# Spec 053 — Alert rules

Status: **approved** (2026-11-20) with every decision in §12 and the answers in §13. Revision 3
(2026-11-21): Phase 0 is done (§14). Revision 4 (2026-11-22, approved by the repo owner): the
changes from a Codex review (§16). Revision 5 (2026-11-23): Phase 1, built with one evaluator lease
per tick and a threshold of 1–100 after a live measurement (§7). Revision 6 (2026-11-23): Phase 2,
the conditions `failure_occurrences`, `blocked_session` and `silence` (§5.1), measured on a live
account (§7). Target: a 5.x minor.
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
store, for example "5 or more events on `ErpEndpoint` are failed right now, with an error text that
contains `timeout`". A rule is **Ok** or **Firing**. Its recipients are told once when it starts
firing and once when it resolves, with a link to the matching messages in the WebApp.

Phase 1 ships one condition, the **failure backlog**: how many events are failed now. It covers the
threshold case ("5 failed events") and the error-match case (an error-text filter with threshold 1),
and it resolves only when the failures are actually cleared. Phase 2 adds **failure occurrences**
("20 failures in 10 minutes", counted from the message history even if they are later resolved)
and the conditions only NimBus can evaluate: how long a failure has held its session blocked, and an
endpoint that has gone silent (§9).

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

1. An endpoint Owner can define rules on their endpoint: a failure-backlog condition with optional
   event-type, status and error-text filters and a threshold.
2. A rule has a state (Ok, Firing) and notifies its recipients when the state changes, not on every
   failure.
3. Rules reuse the existing mail and webhook channels, the webhook signature and the bearer-token
   mode. No new transport.
4. Scaled-out WebApp instances never evaluate a rule twice for one interval, and a write based on
   an older read never lands. An instance that stops after claiming a tick loses that interval;
   the next tick evaluates the rules again.
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
    "kind": "failure_backlog",
    "eventTypes": ["CustomerChanged"],          // optional; empty = all
    "statuses": ["Failed", "DeadLettered"],     // optional; empty = Failed, DeadLettered, Unsupported
    "errorTextContains": "timeout",             // optional; ≤ 200 chars, case-insensitive
    "threshold": 5                              // 1–100 (§7)
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

The failure backlog is the number of events on the endpoint that are failed now (status `Failed`,
`DeadLettered` or `Unsupported`, narrowed by the filters), however long ago they failed, read up
to the threshold (§5.3).

**What it measures, and what it doesn't** (revision 4, after the Codex review). Revisions 1–3
counted unresolved failures whose `UpdatedAt` fell in a window. That measured neither of the two
things people mean by a failure alert: five failures that stay broken aged out of the window and
resolved the rule, and the docs could not promise that *resolved* meant *recovered*. So:

- The backlog has no window. It goes down only when events stop being failed: resubmitted and
  completed, skipped, or deleted. *Resolved* means the matching failures were cleared.
- An event that fails again is still one event; repeated failures of one event count once.
- A resubmit turns the event `Pending` until it is processed, so it briefly leaves the backlog. A
  retry that fails again within `resolveAfterMinutes` puts it back before the rule can resolve; a
  slower one can produce a *resolved* and a new *opened* notice. The docs say so, and the default
  `resolveAfterMinutes` is 5.
- "N failures in a period", counted even if they are resolved later, is a different question. It is
  the Phase 2 condition `failure_occurrences`, read from the message history (§9).

**The Phase 2 conditions** (revision 6). Every kind resolves after `resolveAfterMinutes` of its
condition being false; the API keeps only the fields the kind uses.

| `kind` | Fields | Holds when | `value` in notices |
|---|---|---|---|
| `failure_backlog` | `threshold` 1–100, `statuses`, `eventTypes`, `errorTextContains` | the backlog above reaches `threshold` | the backlog, capped |
| `failure_occurrences` | `threshold` 1–100, `windowMinutes` 5–1440, `eventTypes`, `errorTextContains` | at least `threshold` stored `ErrorResponse` messages (dead-letters included) in `[now − window, now)` | the count, capped |
| `blocked_session` | `minutes` 1–1440 and/or `deferredThreshold` 1–1000 | the oldest Failed or handed-off event has not changed for `minutes`, or the endpoint has at least `deferredThreshold` deferred events | minutes blocked |
| `silence` | `minutes` 5–1440 | no `ResolutionResponse` on the endpoint since `now − minutes` | `minutes` |

- Occurrences count attempts: an event that fails three times counts three times, and a resubmit
  that succeeds doesn't take them back. The rule resolves when the failures age out of the window.
- DeadLettered events don't block their session, so `blocked_session` ignores them.
- Silence treats failures as no activity, and fires on an endpoint that receives nothing.
- A duration changes every minute, so the evaluator records it only with a transition (§5.4: a
  quiet rule is not written).
- The reads are an optional store capability, `IAlertSignalStore` (§5.7). Without it a deployment
  offers only `failure_backlog`; the rule list's `availableKinds` says which kinds it can evaluate.

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

Each tick (revision 5: one lease per tick instead of per-rule claims, decided from the Phase 1
measurement in §7):

1. Claim the tick: `TryClaimAlertRuleEvaluatorTick(dueBefore: now − 55 s, now, instanceId)`. Scaled-out
   instances all try; the store lets exactly one win per interval. A lost claim ends the tick.
2. List the rules to look at (`ListAlertRulesToEvaluate`): every enabled rule, and every disabled
   rule that still has pending notices, without their transition history.
3. Evaluate each enabled rule: `GetFailedEventsAcrossEndpoints(filter, [endpointId], null,
   maxItemCount: threshold)`, where `filter` maps `eventTypes`, `statuses` and `errorTextContains`,
   with no date bounds, and count the rows. The rule only needs to know whether the backlog reaches
   the threshold, so it asks for at most `threshold` failures and never follows the continuation
   token; a count of `threshold` means "at least". Measured at about 5 RU for thresholds up to 5
   whatever the backlog, and 16–30 RU at 100 (§7).
4. Apply the state machine (§5.4). Only when the state changed (a transition, the start of a false
   run, a new backlog value, a delivered or failed notice) save it, with a pending notice per target
   for a transition, conditional on the version read this tick:
   `TrySaveAlertRuleState(endpointId, ruleId, expectedVersion, state)`. An edit, a delete or another
   instance's newer write fails the save, and the next tick sees the new rule.
5. Deliver the rule's pending notices (§5.5). A delivered notice is removed with a second
   conditional save; a failed one stays pending and is retried on the following ticks with the same
   notice id, for up to 24 hours. A crash between saving and sending no longer loses the only
   *opened* notice.
6. Record the tick as completed (`CompleteAlertRuleEvaluatorTick`).

A quiet rule therefore costs one read a minute and no write.

**Hosting prerequisite.** Rules run only while a WebApp instance runs. The App Service template
keeps Always On on by default (`alwaysOnEnabled`), and Aspire/ACA deployments must keep at least one
replica. The Rules section shows when the evaluator last completed a tick, and warns when that is
more than a few minutes ago, so a stopped evaluator is visible.

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
  "lastValue": 5,                           // last count, capped at the threshold ("≥ 5")
  "conditionFalseSinceUtc": null,           // while Firing: first evaluation that found it false
  "updatedAtUtc": "2026-11-20T09:20:00Z",   // the last state write (not the last evaluation)
  "transitionSeq": 7,                       // numbers the transitions; part of each notice id
  "pendingNotices": [ { "noticeId": "c1f0…:7:t2", "target": { … }, "attempts": 1, "createdAtUtc": "…" } ],
  "transitions": [ { "seq": 7, "status": "Firing", "atUtc": "…", "value": 5 }, … ]   // last 20
}
```

The rule's `version`, outside the state, is bumped by every definition and state write. The
evaluator lease (§5.7) holds when the last tick was claimed and completed.

- **Ok → Firing** when `lastValue >= threshold`. Notice: *opened*.
- **Firing → Ok** when the condition has been false for `resolveAfterMinutes` of continuous
  observation. Notice: *resolved*, reason `cleared`. A true evaluation clears
  `conditionFalseSinceUtc`, and so does a gap of more than two intervals since the evaluator's
  previous completed tick: two false samples an hour apart don't prove the backlog stayed low.
- No repeated notices while Firing: one *opened* notice, and the receiving tool tracks the incident
  (§13, answer 3). Retrying an undelivered notice is not a reminder: it is the same notice id.
- Editing a Firing rule's condition, disabling it or deleting it closes the incident: a *resolved*
  notice with reason `rule_changed`, `rule_disabled` or `rule_deleted` goes to the targets the rule
  had, then the state resets to Ok (a deleted rule's notice is best-effort). The edit and the reset
  are one write (§5.7), and the version bump invalidates any outstanding claim.
- A rule whose endpoint is deleted is deleted with it.

### 5.5 Notices

Mail: subject `[NimBus] Firing: ERP timeouts on ErpEndpoint` (or `Resolved: …`), a body with the
rule's condition in words, the count, when it started, the resolve reason and a link to the
endpoint's failed-messages view filtered like the rule. `AlertMailComposer` gets a second template.

Every notice has a `noticeId` (`<ruleId>:<transitionSeq>:<targetId>`) that stays the same across
retries, so a receiver can drop a duplicate.

Webhook: the request of Spec 051 §2 (`POST`, `X-NimBus-*` headers, `v1=` HMAC signature, optional
bearer token for an administrator-listed URL prefix) with a version-2 body. Version 1 bodies stay
as they are for subscriptions.

```jsonc
{
  "version": 2,
  "kind": "alert_rule",
  "noticeId": "c1f0…:7:t2",      // stable across retries
  "ruleId": "c1f0…",
  "ruleName": "ERP timeouts",
  "endpointId": "ErpEndpoint",
  "conditionKind": "failure_backlog", // revision 6; or failure_occurrences, blocked_session, silence
  "status": "Firing",            // or "Resolved"
  "reason": null,                // on Resolved: "cleared", "rule_changed", "rule_disabled", "rule_deleted"
  "value": 5,                    // a count capped at the threshold ("at least 5"), or minutes (blocked, silence)
  "threshold": 5,
  "sinceUtc": "2026-11-20T09:14:00Z",
  "webUiUrl": "https://nimbus.example.com/…"   // failed messages filtered like the rule, or the endpoint
}
```

No payloads and no error text from messages. The rule's own `errorTextContains`, which the Owner
wrote, may appear in the mail body.

### 5.6 Delivery in the WebApp

The evaluator sends from the WebApp, so the senders move to a shared place:

- `AlertMailComposer`, `SmtpAlertMailSender`, `AlertWebhookSender`, `AlertWebhookConnectGuard`
  and their options moved from `NimBus.Resolver` to `NimBus.Alerts` in Phase 0 (a non-packable
  library, namespace `NimBus.Alerts`). The Resolver keeps using them through the same interfaces;
  the WebApp references the library in Phase 1.
- The WebApp gets the SMTP settings (`NimBus:Alerts:Mail:*`) and the bearer-token prefixes
  (`NimBus:Alerts:Webhook:BearerTokens`), which only the Resolver has today. It already has the
  signing key. Bicep, `nb setup` and `nb infra apply` pass them to both apps.
- The WebApp's identity needs the same role on an SRE Agent (or other token audience) as the
  Resolver's when a rule target uses the bearer-token mode.

### 5.7 Storage

`IAlertRuleStore` in `NimBus.MessageStore.Abstractions` (revision 5, as built in Phase 1). Every
rule operation names the endpoint as well as the rule: Cosmos point operations need the partition
key, and the API checks that the rule belongs to the endpoint in the URL (§5.8).

```csharp
Task<IReadOnlyList<AlertRule>> ListAlertRulesByEndpoint(string endpointId);
Task<IReadOnlyList<AlertRule>> ListAlertRulesToEvaluate(int limit);            // no transitions
Task<IReadOnlyList<string>> ListEndpointsWithFiringAlertRules();
Task<AlertRule?> GetAlertRule(string endpointId, string ruleId);
Task<AlertRule?> TrySaveAlertRuleDefinition(AlertRule rule, long? expectedVersion);
Task<long?> TrySaveAlertRuleState(string endpointId, string ruleId, long expectedVersion, AlertRuleState state);
Task<bool> DeleteAlertRule(string endpointId, string ruleId, long expectedVersion);
Task<bool> TryClaimAlertRuleEvaluatorTick(DateTime dueBefore, DateTime nowUtc, string instanceId);
Task<bool> CompleteAlertRuleEvaluatorTick(DateTime claimedAtUtc, DateTime completedAtUtc);
Task<AlertRuleEvaluatorStatus> GetAlertRuleEvaluatorStatus();
```

- Every rule write bumps the logical `version`; conditional writes compare it.
  `TrySaveAlertRuleDefinition` creates (`expectedVersion` null) or replaces the whole rule,
  including the state an edit resets (§5.4), in one write. `TrySaveAlertRuleState` replaces only
  the state.
- The evaluator lease is one record: a claim succeeds only when the last one is older than
  `dueBefore`, and concurrent claimants for one tick get exactly one success.
- **Cosmos DB:** a new `alertrules` container (lowercase, like the store's other containers)
  partitioned by `/endpointId`, also listed in `deploy/bicep/templates/cosmosDB.bicep`. Every
  conditional write also sends the ETag read with it (`IfMatchEtag`), as
  `CosmosDbEndpointMetadataStore` does for its revisions. The lease is a document in a reserved
  partition. `ListAlertRulesToEvaluate` is a cross-partition query projecting no transitions.
- **SQL Server:** migration `0029_AlertRules.sql`: `AlertRules` (definition and state as JSON, the
  transitions in their own column, plus `Version`, `Enabled`, `Status` and `HasPendingNotices`) and
  a one-row `AlertRuleEvaluator`; conditional writes are `UPDATE … WHERE Version = @expected`.
- **In-memory:** a dictionary under a lock.
- The conformance suite covers create, read, list, delete, the endpoint check, stale-version saves,
  an edit invalidating a state save, the edit-and-reset write, and concurrent lease claims.
- `InstrumentingMessageTrackingStoreDecorator` decorates `IMessageTrackingStore` only. The new store
  is a separate interface and needs no forwarding there (§14, fact 4); the evaluator has its own
  meters (`NimBus.AlertRules`).

**Alert signals** (revision 6, Phase 2): an optional `IAlertSignalStore` with three narrow reads
for one endpoint, matched exactly: `CountFailedAttempts(endpointId, from, to, eventTypes,
errorTextContains, cap)` over the stored `ErrorResponse` messages, `GetOldestBlockingEvent(endpointId)`
(non-deleted Failed, or Pending with sub-status Handoff, oldest by `UpdatedAt`), and
`HasCompletedMessageSince(endpointId, since)` over `ResolutionResponse` messages. Cosmos DB reads the
shared `messages` container by endpoint, type and widened date bounds, refined in code; SQL Server
seeks `IX_Messages_EndpointId_EnqueuedTimeUtc`. All three providers implement it, with a conformance
suite.

### 5.8 API and permissions

Under `/api/v1/endpoint/{endpointId}/alert-rules` in `api-spec.yaml` (singular `endpoint`, like
every other endpoint-scoped path; tag `AlertRules`; NSwag generates the controller and client):

| Method | Path | Who |
|---|---|---|
| GET | `/` | Anyone who can read the endpoint. Readers get each rule's condition and state, and only the id and kind of its targets. Owners also get mail addresses and webhook URLs; never a secret. The response also says when the evaluator last completed a tick and which channels are configured |
| POST | `/` | Endpoint Owner or Site Owner. Returns the rule and each new webhook target's secret, once |
| PUT | `/{ruleId}` | Same. Existing targets keep their id and secret; new ones get a secret |
| DELETE | `/{ruleId}` | Same |
| GET | `/api/v1/alert-rules/firing` | Anyone; the endpoints the caller can read that have a Firing rule, for the endpoint list |
| POST | `/{ruleId}/preview` | Same as PUT. The backlog over the last 24 h (Phase 3) |

Every `{ruleId}` call loads the rule by the endpoint in the URL and the rule id together, and
answers 404 when the rule belongs to another endpoint, as subscription deletion binds its id to the
authorized endpoint (`EndpointImplementation`). Recipient addresses stay Owner-only, as the
subscription listing does. Validation errors answer 400 with problem details. Every create, change
and delete is written to the audit trail.

### 5.9 The page

The endpoint's **Alerts** tab gets two sections: **Rules** (new) and **Every failure** (today's
subscriptions, unchanged).

- The rules table: name, condition in words ("≥ 5 failed events, error contains 'timeout'"), a
  status badge (Ok, Firing since 09:14), last value, last evaluated, targets (Owners only), and a ⋯
  menu (Edit, Disable, Delete), following the Manage pages' table and inspector drawer.
- **New rule** opens the form in the drawer: name, filters, threshold, resolve-after, targets. The
  form says in words what the backlog counts and that *resolved* means the failures were cleared. A
  webhook target's secret is shown once with Copy, as for subscriptions.
- The endpoint list shows a small Firing marker beside an endpoint with a firing rule.

## 6. Existing deployments

Nothing changes until someone creates a rule. The Cosmos container and the SQL table are created
on start-up like the other stores. Rules send mail only once the WebApp has SMTP settings. Without
them, a mail target is refused with 400, as a webhook subscription is without a signing key.

## 7. Evaluation cost

Each enabled rule costs one read a minute. With the limit of 20 rules per endpoint, 50 endpoints at
the limit means 1,000 reads a minute. That is acceptable on SQL Server.

On Cosmos DB, Phase 0 measured a full count (`GetFailedEventHistogram`, 15-minute window) on a live
serverless account ([plan](../../plan/2026-11-21-spec-053-phase-0.md)): 2.8 RU when nothing matches,
whatever the container's size, plus about 0.047 RU per matching failure, so about 470 RU for
10,000 failures in the window. A quiet rule costs about $0.03 a month on serverless. During an
outage, 20 full-count rules on one endpoint would need about 160 RU/s, competing with message
processing on a provisioned account. That is why the evaluator asks for at most `threshold`
failures (§5.3). Capping the rows returned is expected to cut the charge during an outage, but it
is not a proven bound: the Cosmos read loop reads whole service pages, and its projection is larger
than the histogram's. Revision 4 dropped the window, so the read no longer discards candidates at a
date boundary.

**Measured in Phase 1** on a live serverless account
([plan](../../plan/2026-11-22-spec-053-phase-1.md)). The backlog read costs about 5 RU at
thresholds up to 5 whether the endpoint has 10 or 10,000 failures, 16–30 RU at threshold 100, and
120–180 RU at threshold 1,000. A selective error-text filter costs 4.2 RU, or 58.7 RU over 1,000
failures with 4 KB error texts, which Cosmos scans. Rule bookkeeping: listing 900 rules costs
56.7 RU, a point read 1 RU, and a conditional replace of a 2 KB rule 13.4 RU.

Two decisions follow (repo owner, 2026-11-22):

- **One lease per tick** (§5.3). Per-rule claims and saves would cost about 27 RU of writes per rule
  per minute whether anything changed: about 450 RU/s for 1,000 rules on a provisioned account, or
  $0.29 per rule per month on serverless. With the lease, a quiet rule costs one read a minute and
  no write.
- **Threshold 1–100** (§5.1), so every read stays at about 30 RU or less.

**Measured in Phase 2** on a live serverless account
([plan](../../plan/2026-11-23-spec-053-phase-2.md)): 200,000 messages over 24 hours on ten
endpoints. The failed-attempt count, the completed-since check and the oldest blocking event each
cost 3.3–5.6 RU in the common case, and the count 8–10 RU at cap 100 over 24 hours. An error-text
filter that matches nothing scans the window's error texts: 59 RU for 2,000 failures in 24 hours,
about 0.03 RU each, so a 24-hour error-text rule on an endpoint with 20,000 failures a day costs
about 600 RU a minute; the docs name it. The deferred count reuses the endpoint state count, about
0.01 RU per event on the endpoint (49.5 RU at 5,000), read once per endpoint per tick.

The evaluation interval stays fixed at 60 s. A setting (`NimBus:Alerts:Rules:EvaluationInterval`,
minimum 60 s) is added only if a deployment needs it.

## 8. Tests

- Conformance: §5.7.
- Evaluator (in-memory store, fake clock): fires at the threshold and not below; resolves only
  after `resolveAfterMinutes` of continuous false; a true evaluation, or a gap of more than two
  intervals, restarts that time; one evaluation per minute when two evaluator instances share a
  store (one lease per tick); a quiet rule is not written; the read asks for at most `threshold`
  failures with no date bounds; a save based on a version an edit or another instance changed is
  dropped; a disabled rule is not evaluated, but its pending notices are still delivered.
- Notices: a failed send stays pending and is retried with the same notice id, and is dropped after
  24 hours; a crash after the state save still delivers the *opened* notice on the next tick;
  editing, disabling or deleting a Firing rule sends a *resolved* notice with its reason, and the
  edit and reset are one write.
- Semantics: five failures that stay failed keep the rule firing however old they are; a resubmit
  that fails again within `resolveAfterMinutes` doesn't resolve it; repeated failures of one event
  count once.
- Filters: each of event types, statuses and error text narrows the count on all three providers
  (conformance, because `ErrorText` matching is provider code).
- Notices: mail subject and body for both statuses; webhook v2 body, headers, signature with the
  derived rule-target secret, bearer token only under a listed prefix.
- API: 403 for a Reader on writes, 400 per validation rule, secret returned once and never from
  GET, audit entries written; a rule id from another endpoint answers 404 on get, update and delete;
  a Reader's GET carries no mail address or webhook URL.
- Frontend: the rules table, the form and its validation, the secret panel, the Firing marker.
- CrmErpDemo end to end: a rule of threshold 3 on `ErpEndpoint`, ERP error mode on, expect one
  *opened* POST to the signature-checking receiver and one mail in Mailpit; error mode off and
  resubmit, expect one *resolved* notice after the resolve time. It extends the endpoint-alert
  end-to-end test that subscriptions get first (2026-11-22).

## 9. Phases

| Phase | Content |
|---|---|
| 0 | **Done 2026-11-21.** Verify §14. Move the senders to the shared library with no behaviour change |
| 1 | **Built 2026-11-23.** Measure the backlog read (§7). Store, evaluator, `failure_backlog`, state machine, pending notices, mail and webhook notices, API, the Rules section, deploy settings, docs |
| 2 | **Built 2026-11-23.** `failure_occurrences`, `blocked_session` (age of the oldest blocking event, or the deferred count) and `silence` (no completed message), §5.1; read through the new `IAlertSignalStore` on all three providers instead of `GetEndpointMetrics`, which reads every endpoint's messages; measured (§7) |
| 3 | Preview over the last 24 h; a failure-classification category filter (only when Integration Intelligence is on, and only for classified failures); firing rules in the Monitor and in MCP `nimbus_get_worklist` |
| v6 | Subscriptions become a built-in rule (§13, answer 1): deprecated with `[Obsolete]` and a bridge in a 5.x minor once rules ship, migrated and removed in v6 (`docs/versioning.md`). How a subscription's per-window notice maps onto a rule needs its own spec before v6. Its acceptance criterion: a migrated subscriber keeps today's behaviour, including a new notice after each window while failures keep arriving, which a Firing rule does not send |

Each phase is its own pull request; a phase's parts are commits in it.

## 10. Documentation

- `docs/notifications.md`, "Endpoint alerts": rules next to subscriptions, when to use which, the
  backlog semantics of §5.1 (what *resolved* means, what a resubmit does), the state machine, the
  v2 webhook body, `noticeId` for de-duplication and how to verify the signature.
- `docs/deployment.md`: the hosting prerequisite of §5.3 (Always On, at least one replica).
- `docs/deployment.md` and `docs/cli.md`: the WebApp's new mail and token settings.
- `samples/CrmErpDemo/README.md`: a rule in the failure showcase.

## 11. Residual risks

- **WebApp down, no rules.** Rules depend on the WebApp's uptime. Mitigation: the docs recommend a
  per-failure subscription (Resolver) for critical endpoints, and an uptime check on the WebApp
  from outside NimBus.
- **Backlog, not occurrences.** A burst that operators resolve before the next evaluation never
  fires a backlog rule. That is intended; "N failures in a period" is Phase 2's
  `failure_occurrences`.
- **Resubmit flapping.** A resubmit that takes longer than `resolveAfterMinutes` and fails again
  sends a *resolved* and a new *opened* notice. The docs say so; raising `resolveAfterMinutes`
  avoids it.
- **Clock skew between WebApp instances.** Claims compare a stored version, not clocks, so skew
  only shifts when a rule is evaluated by a few seconds.
- **Mail volume when many rules fire at once** (one outage, many endpoints). Notices are per rule
  state change, so the volume is bounded by the number of rules, not of failures.

## 12. Decisions (approved by the repo owner, 2026-11-20)

1. Evaluate in the WebApp, not the Resolver (§5.3).
2. Rules own their targets; subscriptions are unchanged until v6 (§5.2, §13 answer 1).
3. Phase 1 has one condition kind, `failure_backlog`: the events failed now, with no window (§5.1).
   Changed in revision 4 from `failure_count` (unresolved failures updated in a window), approved
   by the repo owner on 2026-11-22; `failure_occurrences` follows in Phase 2.
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

## 14. Facts verified in Phase 0 (2026-11-21)

Details and the measurement: [Phase 0 plan](../../plan/2026-11-21-spec-053-phase-0.md).

1. **RU charge on Cosmos DB.** Measured on a live serverless account: 2.8 RU with no match at
   10,000 and 100,000 events, 42.6 RU for 873 failures in the window, 472.7 RU (750 ms) for 10,000.
   The charge follows the matching failures, not the container size. Led to the capped read in
   §5.3. Phase 1 measured that read (§7): the threshold is 1–100 and evaluators share one lease.
2. **`ErrorText` parity.** Case-insensitive substring on all three providers (SQL Server under the
   default CI collation, with `%`, `_` and `[` escaped). A new conformance test,
   `GetFailedEventHistogram_matches_error_text_as_a_case_insensitive_substring`, passes on all three.
   `GetFailedEventsAcrossEndpoints` already had one.
3. **Senders moved** to `NimBus.Alerts` with no new dependency for the WebApp (§5.6).
4. **OpenTelemetry.** `NimBusOpenTelemetryDecorators` wraps `IMessageTrackingStore` only;
   `IAlertRuleStore` is not routed through it. The evaluator gets its own meters in Phase 1
   (evaluations, transitions, failed sends).
5. **SQL migration.** `0029` is free (`0028_EventReportsByTicket.sql` is the latest).
6. **WebApp scale.** The App Service plan template sets only the SKU (one instance unless scaled by
   hand); Aspire/ACA deployments set replicas. The claims stay, and the tests run two evaluators on
   one store.

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
  classification filter stays in Phase 3. The repo owner approved every decision in §12.
- 2026-11-21, revision 3: Phase 0. §14 answered, with the Cosmos DB charge measured on a live
  account. Because a full count's charge grows with the failures in the window, the evaluator asks
  for at most `threshold` failures (§5.3) and the threshold limit is 1–1,000 (§5.1). The senders
  now live in `NimBus.Alerts` (§5.6). After a Codex review (2026-11-22): the capped read bounds
  rows, not RU, so its cost, the threshold limit and the interval are provisional until Phase 1
  measures them (§7); goal 4 says only the current claimant may commit a transition.
- 2026-11-22, revision 4: the rest of the Codex review of #434, approved by the repo owner.
  - §5.1, §12.3: `failure_count` becomes `failure_backlog` (failed now, no window), because the
    windowed count measured neither occurrences nor backlog; `failure_occurrences` joins Phase 2.
  - §5.3: a hosting prerequisite (Always On, at least one replica) and a visible last evaluation.
  - §5.3, §5.4, §5.5: notices are saved as pending with the state and retried with a stable
    `noticeId`; editing, disabling or deleting a Firing rule sends a *resolved* notice with a reason;
    a gap in evaluations restarts the resolve time.
  - §5.7: rules are addressed by `(endpointId, ruleId)`; claims return a token; an edit and its
    state reset are one write; the version maps onto the Cosmos ETag.
  - §5.8: every rule call checks the rule belongs to the endpoint in the URL; Readers don't see
    recipient addresses.
  - §9: the v6 migration keeps subscribers' per-window behaviour.
- 2026-11-23, revision 5: Phase 1 measured the backlog read and the rule bookkeeping on a live
  Cosmos DB account (§7). Per-rule claims became one evaluator lease per tick with writes only on a
  change (§5.3, §5.4, §5.7), the threshold limit became 1–100 (§5.1), and the API, container and
  store names follow what was built (§5.7, §5.8).
- 2026-11-23, revision 6: Phase 2. The repo owner chose `blocked_session` = the oldest blocking
  event's age **or** the deferred count, and `silence` = no completed message. The three conditions
  read a new optional `IAlertSignalStore` (§5.1, §5.7), measured on a live account (§7); the webhook
  body gains `conditionKind` (§5.5).
