# Endpoint alert mail: port DIS subscription notifications

Date: 2026-11-05. Branch `feat/endpoint-alert-mail`.

## Problem

An endpoint's **Alerts** tab lets an Owner subscribe an email address to the endpoint. The
WebApp stores an `EndpointSubscription` (`src/NimBus.MessageStore.Abstractions/States/EndpointSubscription.cs`)
with the address, `type`, event-type filter, payload filter and `Frequency`. Nothing reads it.
`ISubscriptionStore.GetSubscriptionsOnEndpointWithEventtype` and `UpdateSubscription` have no
production caller; they have been unused since the source tree was imported (`9e6175b1`). No
mail is ever sent, while the modal promises "Alerts go to an email address or a webhook, no
more often than the frequency you pick"
(`src/NimBus.WebApp/ClientApp/src/components/endpoint-list/endpoint-alerts-modal.tsx:129`).

`NimBus.Extensions.Notifications` has an `EmailChannel`, but it covers a different scenario. It
runs in the adapter process, sends to a fixed `EmailChannelOptions.To` list set in code, and
never reads subscriptions.

### What DIS does

`BH.DIS.Notifications` (in `c:\git\kl\DIS\src`) has the missing consumer:

- `ResolverService.Handle` calls `NotifyObserversAsync(messageEntity, status)` after
  `UpdateState`, before `Complete()`. It swallows observer exceptions.
- `NotificationObserver.OnEventResolvedAsync` acts only on `ResolutionStatus.Failed`. It loads
  `GetSubscriptionsOnEndpointWithEventtype(endpointId, eventTypeId, "", "")` and skips
  subscriptions with a payload filter. It throttles on `Frequency` (seconds; ≤ 0 means
  "always") against `NotifiedAt` (`yyyy-MM-dd HH:mm`, invariant culture). It splits `type`
  on `;`/`,`, sends through the channel whose `ChannelType` matches, then stamps `NotifiedAt`
  through `UpdateSubscription`.
- `LogicAppMailChannel` posts `{ ToEmail, Subject, Body }` to a DIS-specific Logic App, with an
  HTML body naming the endpoint, event type, event id and the WebApp URL.

The Logic App transport is DIS infrastructure, so we port the observer logic and replace the
transport.

### Gaps found in NimBus that the port depends on

| Gap | Where | Effect on the port |
|---|---|---|
| SQL and in-memory `GetSubscriptionsOnEndpointWithEventtype` ignore the event-type filter and return every subscription on the endpoint | `SqlServerSubscriptionStore.cs:59`, `InMemoryMessageStore.cs:658` | The notifier filters by event type itself |
| SQL `UpdateSubscription` doesn't write `NotifiedAt`, although the column exists | `SqlServerSubscriptionStore.cs:62-75`, `Schema/0005_Subscriptions.sql:10` | Throttling through `UpdateSubscription` would never apply on SQL. The new compare-and-set writes the column directly |
| Cosmos `UpdateSubscription` stamps `NotifiedAt = now` and also re-queries the endpoint error list | `CosmosDbSubscriptionStore.cs:202-206` | Not usable as a conditional claim |
| `ResolverService` already runs `maxConcurrentSessions` sessions in parallel, and Flex Consumption scales out | `deploy/bicep/deploy.core.bicep` (`AzureFunctionsJobHost__extensions__serviceBus__maxConcurrentSessions`) | Read-then-stamp as in DIS sends one mail per concurrent failure during a burst |

## Decisions

1. **Trigger in the Resolver, on applied Failed and DeadLettered outcomes.** `ResolverService.Handle`
   (`src/NimBus.Resolver/Services/ResolverService.cs:94`) already gets `(status, applied)` from
   `UpdateState`. Alert only when `applied` is true: a stale copy that didn't change endpoint
   state must not mail. DIS alerts on Failed only. NimBus also alerts on `DeadLettered`, which
   `GetResultingStatus` returns when `DeadLetterErrorDescription` is set, because both statuses
   need operator action.
2. **Notify after `messageContext.Complete()`, inline, best-effort.** Running after settlement
   keeps the SMTP round-trip out of the message lock and out of the Resolver's
   `catch (Exception) → DeadLetter` path. Every notifier exception is caught and logged; a
   cancelled host token ends the notification quietly. A process crash between Complete and
   send loses that alert, which is acceptable for a best-effort feature. Fire-and-forget is not
   used: Flex Consumption can recycle the instance before a detached task finishes.
3. **Recipients come from the subscription.** Use each subscription whose `type`, split on `;`
   and `,`, contains `mail`, and send to `subscription.Mail`. Skip `teams` and `webhook` parts
   with a Debug log. Skip subscriptions with a non-empty `Payload` with a Warning (DIS parity:
   payload filters aren't evaluated).
4. **Filter by event type in the notifier.** Read `GetSubscriptionsOnEndpoint(endpointId)` and
   keep subscriptions whose `EventTypes` is null or empty or contains `EventTypeId` (ordinal,
   matching Cosmos `ARRAY_CONTAINS`). This sidesteps the provider divergence above without
   touching the unused query method.
5. **Throttle with a conditional claim through a new store interface.** Add
   `ISubscriptionNotificationStore` to `NimBus.MessageStore.Abstractions`:

   ```csharp
   /// Sets NotifiedAt to notifiedAt only when it still equals expectedNotifiedAt
   /// (null or empty = never notified). False when the subscription is gone or the value moved.
   Task<bool> TrySetNotifiedAtAsync(string subscriptionId, string? expectedNotifiedAt,
       string? notifiedAt, CancellationToken cancellationToken = default);
   ```

   The flow is: read the subscription, check the `Frequency` window, claim the window by
   setting `NotifiedAt` from the value just read to now, then send. If sending fails, release
   the claim with a second compare-and-set back to the previous value. For
   `Frequency <= 0`, send without a claim and then stamp best-effort. `NotifiedAt` keeps its
   `yyyy-MM-dd HH:mm` UTC format so existing documents still parse; an unparsable value counts
   as "never notified" (DIS parity).

   This is a separate interface, not a new `ISubscriptionStore` member, so third-party
   `ISubscriptionStore` implementations of the published Abstractions package don't break.
   No default interface method is involved. The OpenTelemetry decorator only wraps
   `IMessageTrackingStore`
   (`src/NimBus.OpenTelemetry/Instrumentation/InstrumentingMessageTrackingStoreDecorator.cs:23`),
   so nothing needs forwarding.
6. **SMTP transport behind `IAlertMailSender`.** Use `System.Net.Mail.SmtpClient`, as
   `NimBus.Extensions.Identity`'s `SmtpEmailSender` and `NimBus.Extensions.Notifications`'
   `EmailChannel` already do. SMTP covers Azure Communication Services (SMTP auth), Microsoft
   365, SendGrid's SMTP relay, and Mailpit for local runs. An ACS Email SDK sender using
   managed identity can be added later behind the same interface. The Notifications extension
   isn't reused: its channels take static recipients and run adapter-side.
7. **Configuration `NimBus:Alerts:Mail`**, bound to `AlertMailOptions`:
   `Smtp:Host`, `Smtp:Port` (587), `Smtp:UseSsl` (true), `Smtp:Username`, `Smtp:Password`,
   `From`, `FromName` ("NimBus"), `WebAppUrl`, `Timeout` (10 s), `IncludeErrorText` (true).
   When `Smtp:Host` or `From` is empty, the Resolver registers a no-op notifier and logs once
   at startup that alert mail is disabled. Existing deployments are unaffected until configured.
8. **Mail content.** HTML with a plain-text alternate view; every value is HTML-encoded.
   - Subject: `[NimBus] {EndpointId}: {EventTypeId} failed` (or `dead-lettered`).
   - Body: endpoint, event type, event id, session id, status, UTC time, and the error text
     truncated to 500 characters (omitted when `IncludeErrorText` is false).
   - Links: the event, `{WebAppUrl}/Message/Index/{endpointId}/{eventId}` (route at
     `ClientApp/src/app.tsx:44`), and the endpoint, `{WebAppUrl}/Endpoints/Details/{endpointId}`.
     Links are left out when `WebAppUrl` is unset.
   - Footer: why the recipient got the mail, the throttle window ("at most one alert per
     day"), and that alerts are managed on the endpoint's Alerts tab.
   - The event payload is never included: it may hold personal data, and the WebApp masks
     payloads for non-PiiReader users.

## Implementation

### PR 1: `feat(storage): compare-and-set a subscription's NotifiedAt`

- `src/NimBus.MessageStore.Abstractions/ISubscriptionNotificationStore.cs`: new interface, with
  XML docs.
- Cosmos (`CosmosDbSubscriptionStore`): `ReadItemAsync`, compare `NotifiedAt`, then
  `ReplaceItemAsync` with `IfMatchEtag`. Return false on 404 or 412. Follow
  `CosmosDbEndpointAcknowledgementStore.cs:60-84`. Partition key = subscription id.
- SQL (`SqlServerSubscriptionStore`): one statement,
  `UPDATE … SET NotifiedAt = @New WHERE Id = @Id AND (NotifiedAt = @Expected OR (NotifiedAt IS NULL AND @Expected IS NULL))`,
  through the existing transient-translation shim; return `rows > 0`.
- In-memory (`InMemoryMessageStore`): compare-and-swap with `ConcurrentDictionary.TryUpdate`
  on a copy. Readers hold references to the stored instances, so mutating in place would
  change a caller's earlier snapshot.
- The composites (`CosmosDbClient`, `SqlServerMessageStore`) implement the interface and
  forward. Register `ISubscriptionNotificationStore` next to `IHeartbeatHistoryStore` in
  `CosmosDbMessageStoreBuilderExtensions` and `SqlServerMessageStoreBuilderExtensions`, by the
  same cast. The in-memory store has no DI registration of store contracts.
- Conformance: a new `SubscriptionNotificationStoreConformanceTests` suite, separate from
  `SubscriptionStoreConformanceTests` so third-party providers' existing runs don't fail on a
  cast. Written first and failing:
  - a set from never-notified succeeds once; a second with expected null fails;
  - a set with a stale snapshot fails (catches live-reference stores);
  - a set with the current value succeeds;
  - setting back to null releases the window;
  - a set on a missing id returns false;
  - other fields are unchanged;
  - eight concurrent sets from the same value: exactly one wins.
- Registration tests assert the aggregate resolves as `ISubscriptionNotificationStore`.

### PR 2: `feat(resolver): mail endpoint alert subscribers on failure`

Code under `src/NimBus.Resolver/Alerts/`:

- `EndpointAlertContext`: endpoint id, event id, event type, session id, status, error text,
  and time.
- `IEndpointAlertNotifier`, with `NoopEndpointAlertNotifier` and `SubscriptionAlertNotifier`.
  The latter implements decisions 3 to 5 and isolates failures per subscription, so one bad
  address doesn't stop the rest.
- `IAlertMailSender` and `SmtpAlertMailSender`. Make `SendAsync` virtual or wrap it, so tests
  observe delivery without a server, as `EmailChannel.SendViaSmtpAsync` does.
- `AlertMailComposer`: builds the subject and the HTML and text bodies.
- `AlertMailOptions` and options validation.

Resolver changes:

- `ResolverService`: add an optional `IEndpointAlertNotifier? alertNotifier = null` constructor
  parameter, and call `NotifyAlertSubscribers(messageEntity, status, cancellationToken)` after
  `messageContext.Complete` when `applied && status is Failed or DeadLettered`. The Resolver
  project isn't packable (`Directory.Build.props:59`), so the signature change has no
  compatibility cost.
- `ServiceExtensions.AddResolver` (explicit factory, `ServiceExtensions.cs:30`) and
  `ResolverBuilderExtensions.AddResolver`: register the notifier. Choose real or no-op from
  configuration, the same pattern as `AddFlowStateChangeNotifier`.

Tests in `tests/NimBus.Resolver.Tests`, written failing first:

- `ResolverService` calls the notifier exactly once for an applied ErrorResponse and for a
  dead-letter replay. It doesn't call it for Completed, Deferred, Pending or Skipped, or for a
  stale (not applied) outcome.
- A notifier that throws doesn't change settlement: the message is completed, not
  dead-lettered or abandoned.
- `SubscriptionAlertNotifier`, against `InMemoryMessageStore` and a recording sender:
  - event-type match, empty filter, and non-matching filter;
  - a `mail;teams` type sends one mail; `webhook` and `teams` send none; a payload-filtered
    subscription is skipped;
  - with `Frequency = 3600`, a second failure inside the hour doesn't mail and one after it
    does (inject `TimeProvider`);
  - two concurrent failures produce one mail (claim contention);
  - a send failure releases the claim, so the next failure mails;
  - `Frequency = 0` mails every time;
  - a store read failure is logged and swallowed.
- `AlertMailComposer`: HTML encoding of endpoint, event type and error text, truncation, links
  with and without `WebAppUrl`, `IncludeErrorText = false`, and no payload.

### PR 3: `feat(deploy): configure alert mail for the Resolver`

- `src/NimBus.AppHost/Program.cs`: add a Mailpit container (`axllent/mailpit`, SMTP 1025, UI
  8025) through `AddContainer`, so there's no new package. Set `NimBus__Alerts__Mail__*` on the
  Resolver (no TLS for Mailpit) and `WebAppUrl` to the webapp endpoint, next to the existing
  `NimBus__Flow__WebAppUrl` (line 116). Do the same in
  `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` (Resolver at line 168).
- `deploy/bicep/deploy.core.bicep`: optional parameters `alertMailSmtpHost`, `alertMailSmtpPort`,
  `alertMailSmtpUsername`, `@secure() alertMailSmtpPassword`, `alertMailFrom` and
  `alertMailWebAppUrl`. Default the last to the management WebApp's URL. Add them to
  `sharedResolverSettings` only when the host is set. Resolver app settings are template-owned
  and replaced on every deploy (comment at `deploy.core.bicep:396-401`), so they must live in
  the template, not be set by hand.
- Docs: a "Endpoint alerts" section in `docs/notifications.md`, separating the two mechanisms
  (adapter-side `AddNimBusNotifications` channels versus Resolver-side Alerts-tab subscriptions);
  the configuration table; and the deployment guide.

## Verification

- `dotnet build src/NimBus.slnx -c Release` and `dotnet test src/NimBus.slnx -c Release --no-build`
  (pin SDK 10.0.4xx locally).
- Run the subscription conformance suite against live SQL Server and Cosmos DB
  (`NIMBUS_SQL_TEST_CONNECTION`, `NIMBUS_COSMOS_TEST_*`); report any skips.
- End to end: run the CrmErpDemo AppHost and subscribe an address on CrmEndpoint's Alerts tab
  with the Hourly frequency. Cause a failed event (the demo's ERP error mode;
  `docs/plan/2026-09-21-erp-error-mode-failure-reasons.md`). Check that one mail arrives in
  Mailpit with a working event link. Cause a second failure and check that no second mail
  arrives within the hour. Repeat once with `NIMBUS_STORAGE_PROVIDER=sqlserver`.

## Out of scope (follow-up issues)

- **Webhook and Teams delivery.** The modal offers "Webhook". Cosmos rejects `type = "webhook"`
  with `ArgumentException` (`CosmosDbSubscriptionStore.cs:41-45`, which reaches the controller
  unhandled), while SQL accepts it. DIS deliberately never posts to the stored `subscription.Url`
  and uses allowlisted, administrator-configured destinations (`WebhookUrlValidator`,
  `NotificationDestinationSecretStore`). Until that port exists, either hide the Webhook option
  or validate `type` in `PostEndpointSubscribeAsync`.
- **Recipient domain allowlist.** Any endpoint Owner can direct alerts to any address. Consider
  `NimBus:Alerts:Mail:AllowedRecipientDomains`, enforced at subscribe time and at send time.
- **Alerts tab feedback.** Show "Last notified" (`notifiedAt` isn't in `api-spec.yaml` yet) and
  add a "Send test alert" action.
- **Unused methods.** Fix or mark `[Obsolete]` the provider-divergent
  `GetSubscriptionsOnEndpointWithEventtype` and SQL `UpdateSubscription`, which drops
  `NotifiedAt`.
- **Digest mode.** Use the throttle window to send one summary of suppressed failures instead
  of dropping them.
