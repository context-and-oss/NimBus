# CrmErpDemo: CRM and ERP sign in with the nimbus-ops accounts

## Context

In the Azure deployment, nimbus-ops requires NimBus Identity sign-in, but `crm-api` / `erp-api` and the CRM and ERP web UIs they serve are open to anyone, including `DELETE /api/admin/data`.

Decisions (user, 2026-10-15):

| Topic | Decision |
|---|---|
| Accounts | CRM and ERP use the same NimBus Identity user store as nimbus-ops: same email and password, managed in one place. |
| Sign-in prompts | One per app (three in all). The apps are on separate `*.azurecontainerapps.io` hosts, and that domain is a public suffix, so a cookie can't be shared between them. |
| Who gets in | Any signed-in NimBus user. No role check. |
| Where it applies | The Azure deployment only. Local runs and the e2e Playwright suite stay anonymous, like nimbus-ops' local-dev bypass. |

## Design

### Users: NimBus Identity in both APIs

- When `NimBusIdentity:ConnectionString` is set, `Crm.Api` and `Erp.Api` call `AddNimBusIdentity`.
- The Identity database is nimbus-ops' database (`nimbus`). The publish-mode AppHost passes its connection to both APIs.
- No bootstrap settings are passed: nimbus-ops creates the admin. The APIs' Identity initializer only finds that the schema exists.
- The extension already provides what each API needs:
  - the `/account/login` pages, self-contained Razor views;
  - `/api/auth/me` and `/api/auth/logout`;
  - 401 instead of a redirect for `/api/*`.
- Each API adds `AddControllersWithViews` and `MapControllers` for these.

### Services: a shared demo service key

The adapters call the APIs over the same public URLs as browsers:

- `crm-adapter` → `crm-api` (3 clients);
- `erp-adapter` → `erp-api` (5 clients, including the notification webhook).

A user cookie can't cover these calls, so:

- The publish-mode AppHost generates a secret parameter `demo-service-key`, like `flow-webhook-key`. The APIs and both adapters get it as `Demo__ServiceKey`.
- The adapters send it in an `X-Demo-Service-Key` header. They send it only to their API's host, never to other hosts.
- The APIs accept it through a small authentication scheme:
  - the comparison is constant-time;
  - its challenge does nothing, so an unauthenticated browser is still redirected to sign-in by the Identity cookie.

### What requires sign-in

- An authorization policy, `DemoUser`, accepts either the Identity cookie or the service key.
- It is applied to every `/api/*` endpoint group and to the SPA fallback (`index.html`). The Identity pages, `/api/auth/*` and `/health`, `/alive`, `/ready` stay anonymous; the probes must keep working.
- JS and CSS bundles under `/assets` stay public. They contain no data.
- With no Identity connection string (local runs, e2e), no policy is applied: the behaviour is exactly as today.

### Shared code

- `CrmErpDemo.Contracts/Security/DemoSignIn.cs` holds:
  - `AddDemoSignIn`, `UseDemoSignIn` and `RequireDemoSignIn` for the APIs;
  - the service-key scheme;
  - `AddDemoServiceKey`, a `DelegatingHandler` for the adapters.
- It sits in the sample, not in `src/`. It is a demo convenience, not a NimBus feature.

### Web UIs

- `Crm.Web` and `Erp.Web` handle a 401 from `/api` by navigating to `/account/login?returnUrl=<current path>`.
- The header shows "Signed in as <name> · Sign out", using `/api/auth/me` and `/api/auth/logout`. It is hidden when `/api/auth/me` returns 404 (local runs).

## Verification

- **Unit tests** (in `CrmErpDemo.AppHost.Tests` or a new `CrmErpDemo.Contracts.Tests`) for the service-key scheme:
  - a valid key authenticates;
  - a wrong or missing key gives no result;
  - the challenge does not override the cookie's redirect;
  - the adapter handler adds the header only for its API's host.
- **Host tests** for a configured API:
  - anonymous `/api/accounts` gets 401;
  - anonymous `/` redirects to `/account/login`;
  - `/health` gives 200;
  - the service key reaches `/api/accounts`;
  - unconfigured, everything stays anonymous.
- **Frontend:** Vitest for the 401 redirect and the signed-in header in both UIs. `npm run build` for both.
- **Regression:** `dotnet build src/NimBus.slnx -c Release` and the full test suite.
- **Local run:** run mode with `aspire run` is unchanged, and the CRM → ERP round trip still completes.
- **Azure:** redeploy to `rg-crmerp-demo`.
  - Anonymous CRM/ERP URLs redirect to sign-in.
  - The adapters still complete the CRM → ERP → CRM round trip using the service key.
  - Signing in as the bootstrap admin works on both hosts. That step is for the user, since I don't enter passwords on hosted sites.
- README: update the Azure section's warning and smoke test.

## Not in scope

- Single sign-on across the three apps. It would need a custom domain plus a gateway, or Entra.
- Roles inside CRM and ERP.
