# Wake-up webhook for endpoint alerts (Spec 051 §2, plus an Entra token mode)

Date: 2026-11-16. Branch `feat/alert-webhook`.

## Context

An agent platform such as Azure SRE Agent starts a run from an HTTP call. NimBus can tell a
person about a failure (the Resolver mails an endpoint's alert subscribers), but it cannot call
a URL: a subscription of type `webhook` can be created in the WebApp and is never delivered.
On Cosmos DB it cannot even be stored (the store rejects the type, and the API answers 500).

Spec 051 §2 designs the missing delivery: an HTTPS POST with ids only, signed with HMAC,
best-effort, throttled by the subscription's frequency window. Two things are added here, both
decided on 2026-11-16 and recorded in the spec's revision 5:

- **An Entra token mode.** The SRE Agent trigger wants a bearer token, not a signature. The
  Resolver attaches a managed-identity token, but only for URL prefixes an administrator lists
  in the Resolver's settings. An endpoint Owner never picks the audience, so a token can't be
  sent to a URL they control.
- **5- and 15-minute windows** in the frequency list, so a burst of failures starts one agent
  run instead of one per failure (or one per hour).

Outcome: an Owner subscribes a URL in the WebApp, sees the signing secret once, and the
receiver gets one signed POST per window when an event on that endpoint fails or is
dead-lettered.

## Decisions

1. **Signing secret is derived, not stored.** `secret = "nbwh_" + base64url(HMAC-SHA256(SigningKey,
   "nimbus-alert-webhook/v1/" + subscriptionId))`. `NimBus:Alerts:Webhook:SigningKey` is set on
   the WebApp (to show the secret once) and on the Resolver (to sign). No store schema change,
   no secret at rest in the database, and no new store interface on three providers.
   Trade-off: replacing one subscription's secret means deleting and re-creating it; replacing
   the key replaces every secret. (Alternative rejected: a `SigningSecret` column. It needs
   migration 0029, a new store interface on every provider, and keeps a plaintext secret in the
   store, because the Resolver cannot read the WebApp's Data Protection key ring.)
2. **Webhook delivery is off until `SigningKey` is set**, as alert mail is off until SMTP is set.
   Without it the WebApp answers 400 to a webhook subscription and the Resolver skips them.
3. **Request.** `POST <url>`, `Content-Type: application/json`, body
   `{"version":1,"endpointId","eventId","sessionId","eventTypeId","status","occurredAtUtc"}`.
   No payload, no error text. Headers: `X-NimBus-Subscription` (id), `X-NimBus-Delivery` (new
   GUID), `X-NimBus-Timestamp` (Unix seconds), `X-NimBus-Signature: v1=<hex HMAC-SHA256(secret,
   "<timestamp>.<body>")>`. Any 2xx is success. Timeout 10 s, no retries, redirects not followed.
4. **Token mode.** `NimBus:Alerts:Webhook:BearerTokens:<i>:UrlPrefix` and `:Audience`. When a
   subscription's HTTPS URL has the same host and port and a path under the prefix, the Resolver
   adds `Authorization: Bearer <token for "<audience>/.default">` from `DefaultAzureCredential`
   (the Resolver's system-assigned identity in Azure). The signature headers are still sent.
   A token that can't be acquired fails the delivery; it is never sent over HTTP.
5. **Where the Resolver may connect.** HTTPS only; HTTP only with `AllowInsecureHttp` (local
   runs). Loopback, private, link-local, CGNAT and unique-local addresses are refused at connect
   time (checked on the resolved address in `SocketsHttpHandler.ConnectCallback`, so a DNS name
   can't dodge it) unless `AllowPrivateNetworks` is set (private-networking deployments, local
   runs).
6. **One window per subscription, whatever its type.** The notifier's window, claim and release
   logic is unchanged. A subscription can deliver by mail, webhook or both (`mail;webhook`); the
   claim is released only when no delivery succeeded.
7. **One PR, one branch** (`feat/alert-webhook`), one commit per part below.

## Changes

### 1. `feat(storage)`: accept webhook subscriptions on every provider

- New `src/NimBus.MessageStore.Abstractions/States/EndpointSubscriptionWebhook.cs` (public
  static, XML docs), the one implementation the Cosmos store, the WebApp and the Resolver share:
  `ParseTypes(string? type)` (parts of `mail` / `teams` / `webhook`, split on `;` `,`),
  `IsValidUrl(string? url, bool allowHttp)` (absolute, https, no user info, ≤ 2000 chars),
  `DeriveSigningSecret(signingKey, subscriptionId)`, `Sign(secret, timestamp, body)`.
- `src/NimBus.MessageStore.CosmosDb/CosmosDbSubscriptionStore.cs:43-49`: accept any combination
  of the three types; require a valid mail address only when the type includes `mail` or
  `teams` (unchanged for those); require a URL when it includes `webhook`.
- `src/NimBus.Testing/Conformance/SubscriptionStoreConformanceTests.cs`: a webhook subscription
  without a mail address is stored and read back. SQL and in-memory already pass.

### 2. `feat(resolver)`: deliver alerts to webhook subscriptions

Under `src/NimBus.Resolver/Alerts/`:

- `AlertWebhookOptions` (`NimBus:Alerts:Webhook`): `SigningKey`, `Timeout` (10 s),
  `AllowInsecureHttp`, `AllowPrivateNetworks`, `BearerTokens[]`. `IsConfigured`, `Validate()`
  (key ≥ 32 chars, positive timeout, every token entry has an absolute https prefix and an
  audience; empty entries are rejected). Same shape as `AlertMailOptions`.
- `IAlertWebhookSender` + `AlertWebhookSender`: builds the request of decision 3, attaches the
  token of decision 4 (`TokenCredential` injected), throws on a non-2xx status, logs host and
  status only. Owns its `HttpClient` over a `SocketsHttpHandler` (`AllowAutoRedirect = false`,
  the connect guard), as `HttpEndpointStateChangeNotifier` owns its client. It does not use
  `IHttpClientFactory`: the hosts add a retrying resilience handler to every factory client.
- `SubscriptionAlertNotifier`: mail and webhook become optional channels. `Matches` keeps the
  payload-filter and event-type checks and asks "does this subscription have a channel that is
  both in its type and configured". `NotifySubscriberAsync` keeps the window, claim and release
  and sends through each such channel.
- `ServiceExtensions.AddEndpointAlertNotifier` (`:114-156`): build each channel from its
  options; the no-op notifier only when neither is on. Startup log per channel. Invalid webhook
  settings turn webhooks off and leave mail on, and never stop the Resolver.

### 3. `feat(webapp)`: subscribe a webhook and see its secret once

- `src/NimBus.WebApp/api-spec.yaml`: `post-endpoint-subscribe` gets a 200 body
  `EndpointSubscriptionCreated { id, signingSecret }` (`signingSecret` only for a webhook).
  Generated files are rebuilt by NSwag, not edited.
- `Controllers/ApiContract/EndpointImplementation.cs` `PostEndpointSubscribeAsync` (`:499-526`):
  validate type, mail address and URL and answer 400 with problem details (today: nothing is
  validated, and Cosmos turns a bad type into a 500); 400 when a webhook is asked for and
  `SigningKey` is not set; return the derived secret. `GET` keeps returning no secret
  (`Mapper.SubscriptionsFromEndpointsubscriptions` is untouched).
- `ClientApp/src/components/endpoint-list/endpoint-alerts-modal.tsx`: https-only URL check;
  frequency options 300 and 900; after a webhook is created, a "copy it now, it won't be shown
  again" panel with Copy, following `components/admin/agent-keys-settings.tsx:308-320`.
  `components/endpoint-details/alert-modal.tsx`: the two new frequency options in the read-only
  view. Check `AlertMailComposer`'s footer wording for windows under an hour.

### 4. `feat(deploy)`: settings for the webhook

- `deploy/bicep/deploy.core.bicep`: `@secure() alertWebhookSigningKey` and
  `alertWebhookBearerTokens` (array of `{ urlPrefix, audience }`), next to the `alertMail*`
  parameters (`:178-193`, `:474-533`). The key goes to the Resolver's secret settings and the
  WebApp's settings; the token entries to the Resolver only.
- `src/NimBus.CommandLine`: `--alert-webhook-token <urlPrefix>=<audience>` (repeatable) on
  `nb setup` and `nb infra apply`, key from `NIMBUS_ALERT_WEBHOOK_SIGNING_KEY`, modelled on
  `AlertMailSelection.cs` / `DeploymentSecrets.cs` / `InfrastructureDeployer.cs:586-612`.
- `src/NimBus.AppHost/Program.cs` and `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs` (local
  branch only): a development signing key, `AllowInsecureHttp` and `AllowPrivateNetworks` on the
  Resolver and the WebApp.

### 5. `docs`

- `docs/notifications.md`, "Endpoint alerts": webhook delivery, the request, how a receiver
  checks the signature and timestamp, the settings table, the token mode with the SRE Agent as
  the example (the Resolver's identity needs `Microsoft.App/agents/threads/write` on the agent;
  the documented audience is `https://azuresre.dev`, to be confirmed in the pilot). Remove
  "Webhook and Teams subscriptions aren't delivered yet" (Teams still isn't).
- `docs/spec/051-operations-agent-support/spec.md`: revision 4 — §2 gains the derived secret,
  the token mode and the connect rules; §1 row and the status line say §2 is built; §13 entry.
- `docs/deployment.md`, `docs/cli.md`, `samples/CrmErpDemo/README.md`: the new settings.

## Tests (each written first and seen failing)

- `tests/NimBus.MessageStore.*.Tests`: type parsing, URL rules, a known-answer vector for the
  secret and for the signature; Cosmos accepts webhook-only and still rejects an unknown type.
- `tests/NimBus.Resolver.Tests` (patterns: `SubscriptionAlertNotifierTests`,
  `HttpEndpointStateChangeNotifierTests` stub handlers, `ResolverCompositionTests`):
  sender headers and body (ids only), signature verifies with the derived secret, token only
  under a listed prefix and only over https, non-2xx and timeout fail, http and non-public
  addresses refused unless allowed; notifier delivers a webhook once per window, shares the
  claim across instances, releases it when every delivery failed, skips a channel that is off;
  composition for mail only, webhook only, both, invalid webhook settings.
- `tests/NimBus.WebApp.Tests` (pattern: `EndpointMetadataWriteTests.cs:200-265`): the secret is
  returned on create and equals the derivation, never from `GET`; 400 for an http URL, a
  missing address, an unknown type, a missing signing key; 403 for a non-Owner.
- Vitest for `endpoint-alerts-modal.tsx`: the secret panel, the https check, the new options.
- `tests/NimBus.CommandLine.Tests`: option parsing, the option snapshot in
  `CliApplicationFactoryTests`, "every parameter the CLI passes is declared by the template",
  the key stays `@secure()`. `tests/CrmErpDemo.AppHost.Tests`: the local settings.

## Verification

1. `dotnet build src/NimBus.slnx -c Release` with the SDK pinned to 10.0.4xx and NSwag
   generation on (no `SkipSpaBuild`), then `dotnet test src/NimBus.slnx -c Release --no-build`.
2. `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`; the docs portal build.
3. Subscription conformance against SQL Server and the Cosmos emulator in local containers if
   Docker is available; otherwise reported as skipped (CI runs both and rejects skips).
4. End to end on the CrmErpDemo AppHost: a small local listener that checks the signature and
   timestamp; subscribe its URL on ErpEndpoint with the 5-minute window; turn on ERP error
   mode; expect exactly one valid POST for a burst, none for a second failure inside the
   window, one after it. Repeat with a URL prefix listed for a token and check the
   `Authorization` header arrives (locally the token comes from the developer credential).
5. Screenshot of the subscribe modal showing the secret panel, for the PR description.
6. Not verified by this change: a real call to an SRE Agent trigger. That needs the Azure
   deployment and a role assignment on the agent, and is the pilot's next step.

## Not in this change

- Teams delivery, payload filters, a "send test alert" button, "last notified" in the Alerts tab.
- A demo receiver in CrmErpDemo. Its existing `/api/webhooks/notifications` belongs to the
  adapter-side notification extension and checks no signature.
- Assigning the Resolver's identity a role on an SRE Agent from Bicep.

## As built

- The shared rules are two classes in `src/NimBus.MessageStore.Abstractions/States/`:
  `EndpointSubscriptionTypes` (type parsing) and `EndpointAlertWebhook` (URL rules, the derived
  secret, the signature, the header names).
- The subscribe response (`EndpointSubscriptionCreated`) also carries the subscription's `type`,
  `mail`, `url`, `authorId`, `eventTypes` and `frequency`. The call used to answer with the
  stored subscription although the API spec declared no body, so the fields a caller may have
  read are kept.
- The subscribe call also refuses a negative `frequency`.
- A URL with an encoded dot, slash or backslash in its path never gets a bearer token: a
  receiver could decode it into a path outside the listed prefix. It is still delivered.
- With the address rule on, the Resolver also ignores a configured proxy, because the rule would
  otherwise check the proxy's address. `AllowPrivateNetworks` restores both.
- The CLI passes `alertWebhookSigningKey` to both Bicep templates. `deploy.webapp.bicep` is a
  separate deployment and needs the key for the WebApp.
