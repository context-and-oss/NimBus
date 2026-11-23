# Notification Channels

`NimBus.Extensions.Notifications` turns message-lifecycle failures into real-time alerts. The
package detects failures (handler exceptions), dead-letters, and session blocks and delivers them to
production channels — **Webhook**, **Microsoft Teams**, and **Email** — with per-channel severity
routing, rate limiting, and deduplication so a cascading outage cannot bury the alert channel.

> The detection half (the `Notification` model, the `INotificationChannel` contract, the
> `NotificationSeverity` enum, and `NotificationLifecycleObserver`) already shipped. This document
> covers the production delivery channels and the routing/throttling layer added on top of them.

> **Live example:** the [CRM/ERP sample](../samples/CrmErpDemo/README.md#showcase-notification-alerts-failure--webhook--operator-alert)
> wires the Webhook channel onto its ERP adapter — flip a failure toggle and watch alerts appear in
> the web UI.

> **Two alert mechanisms.** This extension runs inside an adapter and sends to recipients fixed
> in code. Separately, the Resolver notifies the subscribers on an endpoint's **Alerts** tab in
> the management WebApp, by mail or by a signed call to a webhook; see
> [Endpoint alerts (Alerts tab)](#endpoint-alerts-alerts-tab).

## Quick start

```csharp
services.AddNimBus(builder =>
{
    builder.AddInMemoryMessageStore(); // or your real store

    builder.AddNotifications(n =>
    {
        n.AddWebhook(opts =>
        {
            opts.Url = "https://incident-bot.example.com/nimbus";
            opts.MinSeverity = NotificationSeverity.Warning;
        });
        n.AddTeams(opts =>
        {
            opts.ConnectorUrl = configuration["Teams:WebhookUrl"];
            opts.MinSeverity = NotificationSeverity.Critical;
        });
        n.AddEmail(opts =>
        {
            opts.Provider = EmailProvider.SendGrid;
            opts.ApiKey = configuration["SendGrid:ApiKey"];
            opts.From = "alerts@example.com";
            opts.To = ["oncall@example.com"];
            opts.MinSeverity = NotificationSeverity.Critical;
        });
        n.WithRateLimit(maxPerMinute: 10, burstCapacity: 20);
    });
});
```

### Entry points

There are two equivalent ways to register the fluent channel builder:

| Entry point | Use when |
| --- | --- |
| `builder.AddNotifications(n => { … })` | You are already inside an `AddNimBus(builder => …)` block (the idiomatic NimBus path). |
| `services.AddNimBusNotifications(n => { … })` | You prefer to register notifications directly on the `IServiceCollection` after `services.AddNimBus(…)`. |

Both register the configured channels through the existing `INotificationChannel` path, wire an
`INotificationRouter`, and add the `NotificationLifecycleObserver`. They compose with the existing
`NotificationOptions` toggles (`NotifyOnFailure`, `NotifyOnDeadLetter`, …) — pass a second
`configureOptions` action to override them.

The original registration surfaces remain unchanged and fully supported:

```csharp
builder.AddNotifications();                               // console channel, dev only
builder.AddNotifications(configureOptions, configureChannels); // custom INotificationChannel registration
```

When no channels are configured, the package still falls back to `ConsoleNotificationChannel`.

## Channels

### Webhook

`WebhookChannel` POSTs to any HTTP endpoint — an incident bot, PagerDuty/Opsgenie Events API, a Slack
incoming webhook, etc. (PagerDuty and Opsgenie are reached through this channel rather than dedicated
ones.)

- **Body**: when `Template` is set, the placeholders below are substituted; otherwise a default JSON
  serialization of the `Notification` is sent. The content type is always `application/json`.
- **Failures**: a non-success HTTP status is logged (host + status code only — never the full URL or
  any auth header) and surfaced as a `NotificationDeliveryException`, which the router/observer catch
  so message processing is never affected.

```csharp
n.AddWebhook(opts =>
{
    opts.Url = "https://incident-bot.example.com/nimbus";
    opts.Template = "{\"summary\":\"{Title}\",\"severity\":\"{Severity}\",\"event\":\"{EventId}\"}";
    opts.MinSeverity = NotificationSeverity.Warning;
    opts.Timeout = TimeSpan.FromSeconds(5); // optional, default 10s
});
```

### Microsoft Teams

`TeamsChannel` posts an Adaptive Card (schema 1.4) to a Teams **Incoming Webhook (connector)** URL.
The card shows the title (colour-coded by severity), the message, and the key identifiers / error
details as facts. Oversized error details are truncated to stay within the Teams webhook size limit.
A custom `Template` overrides the card body.

```csharp
n.AddTeams(opts =>
{
    opts.ConnectorUrl = configuration["Teams:WebhookUrl"];
    opts.MinSeverity = NotificationSeverity.Critical;
});
```

| Severity | Card colour |
| --- | --- |
| Critical | `attention` |
| Error | `warning` |
| Warning | `accent` |
| Information | `good` |

### Email

`EmailChannel` supports two interchangeable providers selected by `EmailChannelOptions.Provider`.
Exactly one is active per registration. The subject is `[{Severity}] {Title}`; the body is the
message plus error details (or a custom `Template`).

**SendGrid** (HTTPS `v3/mail/send`, bearer auth — no SendGrid SDK dependency):

```csharp
n.AddEmail(opts =>
{
    opts.Provider = EmailProvider.SendGrid;
    opts.ApiKey = configuration["SendGrid:ApiKey"];
    opts.From = "alerts@example.com";
    opts.To = ["oncall@example.com", "backup@example.com"];
    opts.MinSeverity = NotificationSeverity.Critical;
});
```

**SMTP** (`System.Net.Mail.SmtpClient`):

```csharp
n.AddEmail(opts =>
{
    opts.Provider = EmailProvider.Smtp;
    opts.SmtpHost = "smtp.example.com";
    opts.SmtpPort = 587;          // default
    opts.SmtpUser = configuration["Smtp:User"];
    opts.SmtpPassword = configuration["Smtp:Password"];
    opts.SmtpUseSsl = true;       // default
    opts.From = "alerts@example.com";
    opts.To = ["oncall@example.com"];
    opts.MinSeverity = NotificationSeverity.Critical;
});
```

### Template placeholders

Supported in `WebhookChannelOptions.Template`, `TeamsChannelOptions.Template`, and
`EmailChannelOptions.Template`. Unknown placeholders are left literal; missing values resolve to an
empty string.

`{Severity}` · `{Title}` · `{Message}` · `{EventId}` · `{EventTypeId}` · `{MessageId}` ·
`{CorrelationId}` · `{ErrorDetails}`

For the JSON channels (Webhook, Teams) substituted values are automatically JSON-string-escaped, so
a title or error message containing quotes, backslashes or newlines keeps the rendered payload valid
JSON — write the placeholder inside a quoted string, e.g. `{"title":"{Title}"}`. The Email channel
substitutes values verbatim (plain-text body).

### Configuration validation (fail fast)

Each channel validates its required options at registration time (not on first send):

- Webhook: `Url` must be a non-empty absolute URL.
- Teams: `ConnectorUrl` must be a non-empty absolute URL.
- Email: `From` and at least one `To` are required; SendGrid additionally requires `ApiKey`, SMTP
  requires `SmtpHost`.

Secrets (`ApiKey`, `SmtpPassword`, `ConnectorUrl`) should come from `IConfiguration` / user-secrets /
Key Vault. They are never logged.

## Severity routing

Each channel has a `MinSeverity` (default `Warning`). The `INotificationRouter` delivers a
notification to a channel only when `notification.Severity >= channel.MinSeverity`, using the enum
ordinal order `Information(0) < Warning(1) < Error(2) < Critical(3)`.

For example, with a webhook at `MinSeverity = Warning` and an email channel at
`MinSeverity = Critical`:

- An **Error** notification → delivered to the webhook, **not** to email.
- A **Critical** notification → delivered to **both**.

If no channel qualifies for a notification's severity, the router short-circuits before doing any
dedup or rate-limit work (and no HTTP/SMTP call is made).

## Rate limiting & deduplication

Routing is always on once the fluent builder is used. Rate limiting is opt-in via `WithRateLimit`.

### Rate limiting (storm control)

`WithRateLimit(maxPerMinute, burstCapacity)` installs a global token bucket shared across all
channels:

- Up to `burstCapacity` notifications are delivered immediately.
- After the burst is exhausted, delivery is throttled to `maxPerMinute` (tokens replenish over time).
- Notifications that arrive with no token available are **suppressed** (not delivered, not queued)
  and counted per severity.
- When a token next becomes available, the router emits **one** summary notification
  (`"N notifications suppressed"`, at the highest suppressed severity, listing the counts per
  severity) so the storm is visible without flooding the channel.

```csharp
n.WithRateLimit(maxPerMinute: 10, burstCapacity: 20);
```

### Deduplication

Repeated notifications that map to the same `(EventId, Severity)` within a window (default 5 minutes)
are collapsed to a single delivery, without consuming a rate-limit token. When `EventId` is empty the
key falls back to `(MessageId, Severity)`; when both are empty the notification is always delivered.

Because the key includes severity, a meaningful escalation is **not** suppressed — e.g. an event that
first fails (`Error`) and is later dead-lettered (`Critical`) produces two distinct alerts.

Override the window with `WithDedupWindow(TimeSpan)`.

## Trigger sources

Notifications are raised for:

| Trigger | Severity | Default | Source |
| --- | --- | --- | --- |
| **Failed message** (handler exception) | Error | `NotifyOnFailure = true` | Existing lifecycle observer |
| **Dead-lettered message** | Critical | `NotifyOnDeadLetter = true` | Existing lifecycle observer |
| **Session blocked** | Critical | `NotifyOnSessionBlock` — `true` on the fluent path, `false` on legacy paths | New session-block trigger |
| Message received | Information | `NotifyOnReceived = false` | Existing lifecycle observer |
| Message completed | Information | `NotifyOnCompleted = false` | Existing lifecycle observer |

### Session blocks

NimBus has no dedicated "session blocked" hook — a session becomes blocked when a handler fails, and
later messages for that session raise a `SessionBlockedException` that the core handler swallows after
deferring the message. The notifications package hooks that point: when a message arrives for a
blocked session, it emits a `Critical` notification that names the **blocking event id**, so on-call
can see which incident is holding the session. The notification is keyed on the blocking event id, so
repeated arrivals on the same blocked session collapse to a single alert via dedup.

The fluent registration path (`AddNotifications(n => …)` / `AddNimBusNotifications(…)`) enables
session-block notifications by default. To turn them off, set the option explicitly:

```csharp
builder.AddNotifications(
    n => { /* channels */ },
    options => options.NotifyOnSessionBlock = false);
```

## How it fits together

```
message lifecycle event
        │
        ▼
NotificationLifecycleObserver   (builds the Notification, swallows all delivery errors)
        │
        ▼
INotificationRouter             (severity filter → dedup → rate limit → storm summary)
        │
        ├──► WebhookChannel  (IHttpClientFactory)
        ├──► TeamsChannel    (IHttpClientFactory, Adaptive Card)
        └──► EmailChannel    (SendGrid via HTTPS, or SMTP)
```

Adding a channel never requires touching the observer or the core handler — new channels are plain
`INotificationChannel` implementations registered through the builder.

## Endpoint alerts (Alerts tab)

An endpoint Owner subscribes an email address or a webhook URL to an endpoint, from the
endpoint's menu on the endpoint list (**Configure alerts…**). The endpoint page's **Alerts** tab
lists the subscriptions. The Resolver notifies those subscribers; no adapter code is involved.

| | Notification channels (above) | Endpoint alerts |
|---|---|---|
| Runs in | the adapter (`AddNimBusNotifications`) | the Resolver |
| Recipients | fixed in code (`EmailChannelOptions.To`) | each subscription's address or URL, managed in the WebApp |
| Triggers | handler failure, dead-letter, session block, circuit change | a recorded Failed or DeadLettered outcome |
| Delivery | Webhook, Teams, Email (SendGrid or SMTP) | Email over SMTP, or a signed HTTPS POST |

### When a subscriber is notified

After the Resolver records an outcome and completes the message, it notifies the endpoint's
subscribers when the outcome is **Failed** or **DeadLettered** and it changed the endpoint's
state. A late duplicate that changed nothing notifies nobody. A subscription is notified when:

- its type is `mail` and [alert mail](#mail-configuration) is on, or its type is `webhook` and
  [alert webhooks](#webhook-configuration) are on. A type can name both (`mail;webhook`);
- its event-type filter is empty or contains the event's type (exact match);
- it has no payload filter. Payload filters aren't evaluated, so those subscriptions are skipped
  with a warning in the Resolver log.

Teams subscriptions aren't delivered.

**Frequency.** Each subscription is notified at most once per its frequency (every 5 or 15
minutes, hourly, daily or weekly in the WebApp; stored in seconds). Failures inside the window
are not sent and not summarized later. Several Resolver instances can process failures at the
same time, so the Resolver claims the window in the store before it sends: a burst of failures
notifies once. If nothing could be delivered, the window is released and the next failure tries
again. A frequency of `0`, which only the REST API can set, notifies about every failure. The
time of the last notification is stored to the minute, so a short window is accurate to about a
minute.

Notifying is best-effort and happens after the message is settled: a slow or failing mail server
or webhook never delays or dead-letters a message. Failures are logged under
`NimBus.Resolver.Alerts`.

### Mail alerts

The subject is `[NimBus] {Endpoint}: {EventType} failed` (or `was dead-lettered`). The
mail has a plain-text and an HTML part with the endpoint, event type, event ID, session ID, status,
time, the error text (cut to 500 characters), and links to the event and the endpoint in the
WebApp. The event payload is never included.

### Webhook alerts

A webhook subscription is meant to wake something up: an automation, an incident bot, or an
agent that then reads the failure through the [operator MCP endpoint](mcp-server.md) or the REST
API under its own role. The request therefore carries ids only, never the payload or the error
text.

```http
POST /hooks/nimbus HTTP/1.1
Content-Type: application/json; charset=utf-8
User-Agent: NimBus-Alerts
X-NimBus-Subscription: 6f0c0a3e-…
X-NimBus-Delivery: 0b9a5c1e-…
X-NimBus-Timestamp: 1795348800
X-NimBus-Signature: v1=8412512ba26195f25e006213dfd021afb3ef4dba30a1860b413e4dffa5355a90

{"version":1,"endpointId":"ErpEndpoint","eventId":"c709d881-…","sessionId":"344b95f7-…","eventTypeId":"CrmAccountCreated","status":"Failed","occurredAtUtc":"2026-11-16T00:35:55.129Z"}
```

`status` is `Failed` or `DeadLettered`. `sessionId` and `eventTypeId` can be `null`. Any 2xx
answer counts as delivered. The Resolver waits 10 seconds, tries once and does not follow
redirects, so a receiver should also sweep on a timer: a lost request then delays its work and
does not lose it.

**The signing secret.** When a webhook subscription is created, the WebApp shows its signing
secret once (`nbwh_…`); the REST call returns it as `signingSecret`. It is not stored: it is
derived from the deployment's signing key and the subscription id. To replace one subscription's
secret, delete the subscription and create it again. Replacing the deployment's signing key
replaces every secret.

**Checking a request.** Compute HMAC-SHA256 over the timestamp header, a dot and the raw body,
keyed with the secret, and compare it in constant time with the hex after `v1=`. Reject a
timestamp more than a few minutes old. `X-NimBus-Delivery` is new for every request, for a
receiver that drops duplicates.

```csharp
static bool IsFromNimBus(string secret, string timestamp, string body, string signatureHeader)
{
    var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
    return CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signatureHeader))
           && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) <= 300;
}
```

**Where the Resolver connects.** The URL must be HTTPS. The Resolver connects directly, not
through a proxy, and only to public addresses: a host that resolves to a loopback, private or
link-local address is refused. A deployment whose receivers are on a private network, or that
needs a proxy, sets `AllowPrivateNetworks`.

**Receivers that want an Entra token.** Some receivers accept a bearer token and not a
signature, for example an [Azure SRE Agent HTTP trigger](https://learn.microsoft.com/azure/sre-agent/http-triggers).
An administrator lists the receiver's URL prefix and the token audience in the Resolver's
settings (`BearerTokens`). A request to a URL at or below that prefix then also carries
`Authorization: Bearer <token>` for the Resolver's managed identity, with the scope
`{Audience}/.default`. A subscription cannot choose the audience, so an endpoint Owner cannot
have a token sent to a URL of their own. The token is never sent over HTTP. The Resolver's
identity still needs whatever role the receiver asks for; NimBus assigns none. For an SRE Agent
trigger that is `Microsoft.App/agents/threads/write` on the agent, and Microsoft documents the
audience as `https://azuresre.dev`; confirm both against your agent.

Each call to such a trigger starts a run, so pick a frequency of 5 minutes or longer for it: a
burst of failures then starts one run.

### Mail configuration

Alert mail is off until both `Smtp:Host` and `From` are set. Settings live under
`NimBus:Alerts:Mail` on the Resolver, and for [alert rules](#alert-rules-alerts-tab) on the WebApp too (app settings use `__`, for example
`NimBus__Alerts__Mail__Smtp__Host`).

| Setting | Default | Notes |
|---|---|---|
| `Smtp:Host` | (unset) | SMTP server, e.g. `smtp.azurecomm.net` (Azure Communication Services), `smtp.office365.com`, `smtp.sendgrid.net` |
| `Smtp:Port` | `587` | |
| `Smtp:UseSsl` | `true` | |
| `Smtp:Username` | (unset) | Omit for an anonymous relay |
| `Smtp:Password` | (unset) | Keep it in a secret store |
| `From` | (unset) | Sender address; must be a valid address the server may send as |
| `FromName` | `NimBus` | Sender display name |
| `WebAppUrl` | (unset) | Public WebApp URL for the links; no links when unset |
| `Timeout` | `00:00:10` | Per delivery |
| `IncludeErrorText` | `true` | Set `false` if error text may contain personal data |

The Resolver logs at startup whether alert mail is on. Invalid settings (an invalid `From`, a port
outside 1–65535, a non-positive `Timeout`, a relative `WebAppUrl`) are logged as an error and leave
alert mail off; they never stop the Resolver.

**Locally**, both Aspire AppHosts (`src/NimBus.AppHost` and the CRM/ERP demo) start a
[Mailpit](https://mailpit.axllent.org/) container and point the Resolver and the WebApp at it. Mailpit accepts
every message, so any address works; open the `mailpit` resource's URL in the Aspire dashboard to
read the mail.

**In Azure**, pass the alert mail options to `nb setup` or `nb infra apply`, with the SMTP
password in `NIMBUS_ALERT_MAIL_SMTP_PASSWORD`:

```bash
export NIMBUS_ALERT_MAIL_SMTP_PASSWORD='<smtp password>'
nb setup --solution-id nimbus --environment prod --resource-group rg-nimbus-prod \
  --alert-mail-smtp-host smtp.azurecomm.net \
  --alert-mail-smtp-username '<smtp user name>' \
  --alert-mail-from alerts@contoso.com
```

The Resolver's app settings are replaced on every deploy, so pass the same options every time: a
run without them turns alert mail off. Deploying the Bicep directly takes the matching `alertMail*`
parameters ([deployment guide](deployment.md#41-core-infrastructure)). See the [CLI reference](cli.md)
for every option.

### Webhook configuration

Alert webhooks are off until `SigningKey` is set. Settings live under `NimBus:Alerts:Webhook`
(app settings use `__`, for example `NimBus__Alerts__Webhook__SigningKey`). The Resolver and the
management WebApp must have the same `SigningKey`: the Resolver signs with it, and the WebApp
needs it to show a new subscription its secret. Without it the WebApp refuses webhook
subscriptions.

| Setting | On | Default | Notes |
|---|---|---|---|
| `SigningKey` | Resolver, WebApp | (unset) | At least 32 characters, from a random generator. Keep it in a secret store |
| `Timeout` | Resolver, WebApp | `00:00:10` | Per delivery |
| `BearerTokens:<n>:UrlPrefix` | Resolver, WebApp | (none) | An https URL. Requests to a URL on the same host and port, at or below its path, carry a token |
| `BearerTokens:<n>:Audience` | Resolver, WebApp | (none) | The audience the token is requested for |
| `AllowPrivateNetworks` | Resolver, WebApp | `false` | Allow loopback, private and link-local addresses, and a configured proxy |
| `AllowInsecureHttp` | Resolver, WebApp | `false` | Accept `http` URLs. For local runs |

The Resolver logs at startup whether alert webhooks are on. Invalid settings (a short key, a
non-positive `Timeout`, an incomplete `BearerTokens` entry) are logged as an error and leave
alert webhooks off; they never stop the Resolver or alert mail.

**Locally**, both Aspire AppHosts set a development-only signing key on the Resolver and the
WebApp, and allow `http` and private addresses, so a receiver on your own machine works.

**In Azure**, put the key in `NIMBUS_ALERT_WEBHOOK_SIGNING_KEY` and, for a receiver that wants a
token, pass `--alert-webhook-token` to `nb setup` or `nb infra apply`:

```bash
export NIMBUS_ALERT_WEBHOOK_SIGNING_KEY="$(openssl rand -base64 48)"
nb setup --solution-id nimbus --environment prod --resource-group rg-nimbus-prod \
  --alert-webhook-token 'https://<agent host>/api/=https://azuresre.dev'
```

Keep the key: a run with another key changes every subscription's secret, and a run without it
turns alert webhooks off. Deploying the Bicep directly takes `alertWebhookSigningKey` on both
templates and `alertWebhookBearerTokens` on both (the WebApp uses them for alert rules)
([deployment guide](deployment.md#41-core-infrastructure)).

## Alert rules (Alerts tab)

A subscription tells someone about every failure. An **alert rule** tells someone when it is bad
and again when it is over: "5 or more events on `ErpEndpoint` are failed right now, with an error
containing `timeout`". The endpoint page's **Alerts** tab lists the rules above the subscriptions;
an endpoint Owner (or Site Owner) creates, changes, disables and deletes them there. Everyone who
can read the endpoint sees the rules and their state; only Owners see where notices go. The
endpoint list and the [Monitor wall](webapp-monitor.md) mark an endpoint with a firing rule, and the
MCP worklist ([`nimbus_get_worklist`](mcp-server.md)) lists firing rules.

| | Subscription | Alert rule |
|---|---|---|
| Fires on | every recorded Failed or DeadLettered outcome | its condition: a failure backlog, a burst of failures, a blocked session or silence |
| Tells its targets | at most once per frequency window, for as long as failures arrive | once when it starts firing, once when it resolves |
| Runs in | the Resolver | the WebApp |

### What a rule watches

Each rule has one condition. Every condition resolves when it has stayed false for the rule's
resolve time (1–60 minutes, default 5).

| Condition | Fires when | Fields |
|---|---|---|
| Failure backlog | N or more events on the endpoint are failed **now** | threshold 1–100; statuses, event types, error text |
| Failure occurrences | N or more attempts failed in the **last W minutes**, even if resolved since | threshold 1–100, window 5–1440 min; event types, error text |
| Blocked session | the oldest event holding a session has not changed for T minutes, **or** N or more events are deferred | minutes 1–1440, deferred events 1–1000 (either or both) |
| Silence | no message on the endpoint **completed** for T minutes | minutes 5–1440 |

**Failure backlog** counts the events that are failed now (status Failed, DeadLettered or
Unsupported), optionally only some statuses, some event types, or those whose error text contains
a phrase (case-insensitive). There is no time window.

- **Resolved means cleared.** The backlog goes down only when failed events are resubmitted and
  complete, skipped, or deleted. Five failures that stay failed keep the rule firing, however old
  they are.
- An event that fails again is still one event.
- A resubmit turns the event Pending until it is processed. A retry that fails again within the
  resolve time keeps the rule firing; a slower one can send a *resolved* and then a new *opened*
  notice. Raise the resolve time if that happens.

**Failure occurrences** counts failed attempts in message history: every failed or dead-lettered
attempt in the window counts, so an event that fails three times counts three times, and a
resubmit that succeeds doesn't take the failures back. The rule resolves once the failures have
aged out of the window. Use it for spikes ("20 failures in 10 minutes"); use the backlog for "work
is stuck". An error-text filter on a long window is the costly case on Cosmos DB: when
nothing matches, every failed attempt in the window is read (about 0.03 RU each), so prefer a
short window or no error text on a busy endpoint.

**Blocked session** watches ordered processing. A Failed event, or a Pending event waiting on an
external system (a handoff), holds its session: the events behind it wait as Deferred. The rule
fires when the oldest such event has not changed for the minutes you set, or when the endpoint has
at least the number of deferred events you set. Dead-lettered events don't block their session and
don't count.

**Silence** fires when nothing on the endpoint completed successfully for the minutes you set.
Failures are not activity: an endpoint whose every message fails is silent. Silence also fires on
an endpoint that receives no messages, so set it on endpoints that are expected to be busy.

A count (backlog, occurrences) is capped at the threshold: "≥ 5" means at least five. A blocked or
silent rule reports minutes.

### Classification categories

When [Integration Intelligence](integration-intelligence.md) is on, a backlog or occurrences rule
can count only failures classified into chosen categories, for example *transient dependency* or
*authentication / configuration*: "3 or more failures in 15 min classified as authentication
configuration". A failure counts only once an agent has classified it, with a result for that
attempt that is not *unknown* or *uncertain*; until then it doesn't count. So:

- such a rule lags the failure by however long classification takes, and never fires for failures
  nobody classifies;
- a retry is a new attempt, unclassified until an agent classifies it again, so the rule can dip
  between a retry and its classification.

Classifications are read in bulk, at most 1,000 failures a minute per rule. When they can't be read,
or more unclassified failures remain than that before the threshold is reached, the rule's state is
left as it is for that minute instead of resolving, and the evaluator logs it.

### Preview

**Preview** in the rule form evaluates the draft without saving it: whether it would fire now, and
its value. For **failure occurrences** and **silence** it also replays the last 24 hours from
message history, minute by minute with the draft's resolve time, and shows the value over the day
and when the rule would have been firing. That replay is exact. Backlog and blocked-session rules
get no replay: deleted events leave no trace in history, and there is no record of past deferred
counts, so a replay would be a guess. Rules with categories get no replay either, because
classifications arrive after the failure.

Anyone who can read the endpoint can preview. A preview reads a day of the endpoint's messages of
one type, about 170 RU on Cosmos DB over 2,000 failures and 310 RU over 17,000 completed messages, so
it has its own rate limit: 10 a minute per user by default (`RateLimiting:AlertRulePreview`).

### When rules are evaluated

The WebApp evaluates every enabled rule once a minute. Scaled-out WebApp instances share one lease
per minute, so each rule is evaluated once. A rule is saved only when its state changes. **Rules run
only while the WebApp runs**: keep Always On on (the App Service template does by default) and at
least one replica on Container Apps. The Rules section shows when the evaluator last ran and warns
when that was several minutes ago. For an endpoint that must never go unwatched, add a
subscription as well: subscriptions are delivered by the Resolver.

### Notices

Each target gets one notice per transition. A notice is saved before it is sent and retried every
minute, with the same notice id, until it is delivered or 24 hours have passed. Changing a firing
rule's condition, disabling it or deleting it sends a *resolved* notice with the reason
`rule_changed`, `rule_disabled` or `rule_deleted` (best-effort for a deleted rule).

**Mail:** the subject is `[NimBus] Firing: {Rule} on {Endpoint}` or `[NimBus] Resolved: …`. The
mail names the rule, the endpoint, the condition in words, the value (the count, or the minutes
blocked or silent), since when, and for a resolved notice why, with a link to the endpoint's failed
events (failure conditions) or the endpoint itself (blocked session, silence). It never carries
error text or a payload.

**Webhook:** a signed `POST` like a subscription's (see [Webhook alerts](#webhook-alerts)), with
these differences:

- The header `X-NimBus-Notice` carries the notice id instead of `X-NimBus-Subscription`. The id is
  the same on every retry; drop a request whose notice id you have already handled.
- The signing secret belongs to the rule's target. It is
  `nbwh_` + base64url(HMAC-SHA256(SigningKey, `"nimbus-alert-rule-webhook/v1/" + ruleId + "/" +
  targetId`)), shown once when the target is added. Changing other targets keeps it; removing the
  target and adding it again gives a new one.
- The body is version 2:

```json
{
  "version": 2,
  "kind": "alert_rule",
  "noticeId": "c1f0…:7:t2",
  "ruleId": "c1f0…",
  "ruleName": "ERP timeouts",
  "endpointId": "ErpEndpoint",
  "conditionKind": "failure_backlog",
  "status": "Firing",
  "reason": null,
  "value": 5,
  "threshold": 5,
  "sinceUtc": "2026-11-20T09:14:00.000Z",
  "webUiUrl": "https://nimbus.example.com/Failed?endpointId=ErpEndpoint&period=30d"
}
```

`conditionKind` is `failure_backlog`, `failure_occurrences`, `blocked_session` or `silence`.
`value` is a count capped at `threshold` for the failure conditions and minutes for the others
(`threshold` is then 1 and has no meaning). `status` is `Firing` or `Resolved`; `reason` is set on
a resolved notice. A bearer token is added
under the same [token prefixes](#webhook-configuration) as subscriptions, for the WebApp's identity.

### Settings

Rules use the same settings as subscriptions, **on the WebApp**: [mail](#mail-configuration)
(`NimBus:Alerts:Mail`, with `WebAppUrl` for the links) and [webhooks](#webhook-configuration)
(`NimBus:Alerts:Webhook`, including `BearerTokens` and `AllowPrivateNetworks`). `nb setup` and
`nb infra apply` pass the alert mail options and token prefixes to both the Resolver and the
WebApp. A rule with a mail target is refused while alert mail is off on the WebApp, and a webhook
target while alert webhooks are off. Rules need a storage provider with the alert rule store, and
the occurrence, blocked-session and silence conditions also need its alert signal reads; Cosmos DB,
SQL Server and the in-memory store have both. On a provider without the signal reads the form
offers only the failure backlog.
