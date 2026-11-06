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
> in code. Separately, the Resolver mails the people who subscribe on an endpoint's **Alerts** tab
> in the management WebApp; see [Endpoint alerts (Alerts tab)](#endpoint-alerts-alerts-tab).

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

The management WebApp's endpoint page has an **Alerts** tab where an endpoint Owner subscribes an
email address to the endpoint. The Resolver mails those subscribers; no adapter code is involved.

| | Notification channels (above) | Endpoint alerts |
|---|---|---|
| Runs in | the adapter (`AddNimBusNotifications`) | the Resolver |
| Recipients | fixed in code (`EmailChannelOptions.To`) | each subscription's address, managed in the WebApp |
| Triggers | handler failure, dead-letter, session block, circuit change | a recorded Failed or DeadLettered outcome |
| Delivery | Webhook, Teams, Email (SendGrid or SMTP) | Email over SMTP |

### When a subscriber is mailed

After the Resolver records an outcome and completes the message, it mails the endpoint's
subscribers when the outcome is **Failed** or **DeadLettered** and it changed the endpoint's
state. A late duplicate that changed nothing sends no mail. A subscription gets the mail when:

- its type includes `mail` (`mail`, or a `;`/`,` list such as `mail;teams`);
- its event-type filter is empty or contains the event's type (exact match);
- it has no payload filter. Payload filters aren't evaluated, so those subscriptions are skipped
  with a warning in the Resolver log.

Webhook and Teams subscriptions aren't delivered yet.

**Frequency.** Each subscription is mailed at most once per its frequency (hourly, daily or weekly
in the WebApp; stored in seconds). Failures inside the window are not mailed and not summarized
later. Several Resolver instances can process failures at the same time, so the Resolver claims the
window in the store before it sends: a burst of failures sends one mail. If delivery fails, the
window is released and the next failure tries again. A frequency of `0` mails every failure.

**Content.** The subject is `[NimBus] {Endpoint}: {EventType} failed` (or `was dead-lettered`). The
mail has a plain-text and an HTML part with the endpoint, event type, event ID, session ID, status,
time, the error text (cut to 500 characters), and links to the event and the endpoint in the
WebApp. The event payload is never included.

Notifying is best-effort and happens after the message is settled: a slow or failing mail server
never delays or dead-letters a message. Failures are logged under `NimBus.Resolver.Alerts`.

### Configuration

Alert mail is off until both `Smtp:Host` and `From` are set. Settings live under
`NimBus:Alerts:Mail` on the Resolver (app settings use `__`, for example
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
[Mailpit](https://mailpit.axllent.org/) container and point the Resolver at it. Mailpit accepts
every message, so any address works; open the `mailpit` resource's URL in the Aspire dashboard to
read the mail.

**In Azure**, pass the `alertMail*` parameters to `deploy/bicep/deploy.core.bicep`
([deployment guide](deployment.md#41-core-infrastructure)). `nb setup` doesn't pass them yet, and
the Resolver's app settings are replaced on every deploy, so a later `nb setup` turns alert mail
off again.
