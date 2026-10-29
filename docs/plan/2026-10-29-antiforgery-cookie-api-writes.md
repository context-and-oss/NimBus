# Antiforgery tokens on cookie-authenticated API writes

Date: 2026-10-29. Scope: [#296](https://github.com/context-and-oss/NimBus/issues/296).
Branch `fix/webapp-antiforgery-writes`.

## Problem

Only Agent keys and MCP access validate an antiforgery token (`X-NimBus-CSRF`). Every other write
in the generated API controllers rests on the sign-in cookie's `SameSite=Lax` default alone. CodeQL
reports `cs/web/missing-token-validation` on each of those actions.

## Constraints

- **Browser sessions** authenticate with a cookie: the Entra OpenID Connect cookie, or ASP.NET
  Identity's `NimBus.Identity`. These are the only requests a cross-site forgery can use, since a
  browser attaches them on its own.
- **Header-authenticated callers** must keep working without a token: Entra bearer tokens (a
  documented external shape, `docs/webapp-rest-api.md`), agent API keys, and MCP bearer tokens. A
  browser can't attach these headers cross-site, so they can't be forged.
- **The controllers are generated** (`ApiContract.g.cs`), so nothing can be declared on them. The
  repo already attaches policy to them by application-model convention:
  `AllowAnonymousActionsConvention` and `RateLimitPoliciesConvention`.
- **Test hosts** load these controllers with plain `AddControllers()` and post with an injected
  principal and no cookie. A framework `[AutoValidateAntiforgeryToken]` on every controller would
  break those hosts, because the filter's services aren't registered there. It would also refuse
  every bearer and agent-key write.

## Design

1. **`SignInCookies`** tells whether a request carries a cookie of a registered cookie
   authentication scheme. Names come from each scheme's `CookieAuthenticationOptions`, so
   `NimBus.Identity` and Microsoft.Identity.Web's `.AspNetCore.Cookies` (chunked or not) are both
   covered without hard-coding.
2. **`CookieAntiforgeryFilter`** is an authorization filter at the same order as the framework's
   antiforgery filters. On an unsafe method from a request that carries a sign-in cookie, it calls
   `IAntiforgery.ValidateRequestAsync` and answers a failure with the framework's 400
   (`AntiforgeryValidationFailedResult`). Safe methods and cookie-less requests pass through.
3. **`CookieAntiforgeryConvention`** attaches the filter to every generated controller (namespace
   `NimBus.WebApp.ManagementApi`) except:
   - `StorageHookApiController`: anonymous, authenticated by `X-Webhook-Key`.
   - `AgentKeysApiController` and `McpAccessApiController`: their `[AutoValidateAntiforgeryToken]`
     already requires the token on every write, cookie or not, and stays as it is.

   Keying on the namespace covers new generated controllers automatically.
4. **`AntiforgeryTokenCookieMiddleware`** hands the SPA the request token in a readable cookie,
   `NimBus.CSRF` (`SameSite=Strict`, not HttpOnly). This is the cookie-to-header pattern. It runs on
   every authenticated `GET /api/…` that carries a sign-in cookie, after `UseAuthorization`.
   Issuing it on API GETs rather than with `index.html` also covers the Vite dev server, and it
   picks up a change of user without a reload. `GetAndStoreTokens` marks those responses
   no-cache, which matches the API's existing `NoStore` choices.
5. **The SPA:** `ApiClientBase.transformOptions` (the NSwag `extensionCode`) reads `NimBus.CSRF`
   and sends it as `X-NimBus-CSRF` on every non-GET request. The Agent keys and MCP access screens
   keep sending the token their GET returns; both tokens are valid for the same session.

## Out of scope

- **Extension controllers:** the Identity extension's `AuthApiController` (sign-in, sign-out) and
  the Integration Intelligence controllers carry their own antiforgery decisions.
- **Local development bypass:** `EnableLocalDevAuthentication` authenticates without a cookie, so
  it isn't protected. It is Development-only by construction.
- **CodeQL:** its query recognizes antiforgery attributes, not a convention-applied filter, so
  `cs/web/missing-token-validation` alerts are expected to stay open. Once this ships, dismiss them
  with a reference to this plan.

## Tests

- **`CookieAntiforgeryFilterTests`** (real `DefaultAntiforgery`):
  - A cookie write without the token, or with another user's token, gets 400.
  - The same write with the token passes.
  - GETs, bearer requests and agent-key requests pass without a token.
- **`AntiforgeryTokenCookieMiddlewareTests`:**
  - The `NimBus.CSRF` cookie is issued on an authenticated cookie GET under `/api`, and the token
    it carries validates through the filter.
  - It is not issued for bearer, POST, anonymous or non-API requests.
- **`CookieAntiforgeryConventionTests`:** every generated controller except the three carries the
  filter, and those three don't.
- **Vitest:** the API client sends the header on writes when the cookie is present, and not on
  GETs.

## Verification

```bash
dotnet build src/NimBus.slnx -c Release
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
```

Not covered by tests: a real browser session through Entra or Identity sign-in. That's worth a
manual check in the local Aspire stack before merging.
