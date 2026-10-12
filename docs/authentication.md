# Authentication and Identity

How the NimBus management WebApp decides who is signed in, what to enable
where, and how operators sign in and out.

This is the operator-facing reference. For the SDK-level
`AddNimBusIdentity` API surface, see
[`sdk-api-reference.md` § Identity Extension](sdk-api-reference.md);
for storage prerequisites and the local Aspire walk-through, see
[`storage-providers.md`](storage-providers.md#local-sign-in-via-nimbus_identity).

## The three auth providers

`src/NimBus.WebApp/Startup.cs` picks exactly one of these per request,
based on what's configured:

| Provider | When it kicks in | Best for |
|---|---|---|
| **Local-dev bypass** | `EnableLocalDevAuthentication=true` AND the host is Development | One-keystroke local runs; never use in production |
| **Username / password** ([`NimBus.Extensions.Identity`](sdk-api-reference.md#identity-extension)) | `NimBusIdentity:ConnectionString` is set | SQL-backed deployments where Entra registration isn't available, plus all local sign-in testing |
| **Microsoft Entra ID** | `AzureAd:ClientId` is set | Organisations already on Entra; recommended default for production |
| **Dual (Identity + Entra)** | Both Identity and Entra are configured | Migration windows where some users live in Identity and some in Entra |

The selection ladder lives in `Startup.cs:60–165`. The fallback redirect
(`Startup.cs:400–412`) sends unauthenticated requests to
`/account/login` when Identity is present, otherwise `/login` for the
Entra OIDC flow.

> **Security guard.** `BypassEndpointAuthorization` and
> `EnableLocalDevAuthentication` throw `InvalidOperationException` at
> startup when set in a non-Development environment. There's no way to
> ship them to production accidentally.

## Agent API keys

Agents built on `NimBus.Agents` call the agent API (`/api/agent/*`) without a person signed in. A site Owner issues each agent its own key through the nimbus-ops admin API; nimbus-ops has no page for agent keys yet. The design is [Spec 039](spec/039-agent-api-keys/spec.md).

| Request | Does |
|---|---|
| `GET /api/admin/agent-keys` | Lists the keys, without their secrets, and returns the `csrfToken` that the requests below must send as the `X-NimBus-CSRF` header |
| `POST /api/admin/agent-keys` | Issues a key. Body: `agentId` (required), optional `name` and `expiresAtUtc`. The response's `key` is the full key |
| `POST /api/admin/agent-keys/{keyId}/revoke` | Revokes a key |
| `DELETE /api/admin/agent-keys/{keyId}` | Removes a revoked or expired key |

- **Works in every sign-in mode**, Identity-only included. The key travels in the `X-NimBus-Agent-Key` header and has a scheme of its own, `AgentApiKey`, which is never a default.
- **Admits only `/api/agent/*`.** A key gets 401 on every other endpoint, and its principal carries no admin marker and no object id, so it cannot hold a site or endpoint role. Signed-in people keep using the agent API as before.
- **The key names the agent.** Schema definitions, subscriptions and the audit row of a settled hand-off record the key's agent. A request whose `X-Agent-Id` names a different agent gets 403.
- **Shown once, stored hashed.** The full key (`nbak_<keyId>_<secret>`) appears once, when it is issued. nimbus-ops stores only a SHA-256 of the secret. Issuing, revoking and removing are Owner-only and always audited, with the key id but never the key.
- **Revocation takes effect within 30 seconds** on every instance, and at once on the instance that saved it. A key can also carry an expiry. "Last used" is updated at most every 5 minutes per instance.

Give the key to the agent through configuration, never source code:

```csharp
builder.Services.AddNimBusAgent<MyHandler, MyEvent>(o =>
{
    o.AgentId = "enrichment-agent";             // must match the key's agent
    o.ApiKey = builder.Configuration["NimBus:Agent:ApiKey"];
    o.Subscribe("MyEvent");
});
```

The local Aspire AppHost's development sign-in bypass admits agents without a key.

## Enabling username/password

The Identity provider needs three things: the package wired in (already
shipped via `NimBus.WebApp.csproj` ProjectReference), a SQL connection
string, and at least one user.

### Locally with Aspire

```powershell
$env:NIMBUS_IDENTITY = "true"
dotnet run --project src/NimBus.AppHost
```

Docker Desktop must be running — Aspire pulls the mssql container,
creates the `nimbusdb` database, applies the Identity schema, and seeds
the bootstrap admin on first run. No user-secrets configuration is
needed for the SQL side. The AppHost prints a banner with the default
credentials. See
[`storage-providers.md` § Local sign-in](storage-providers.md#local-sign-in-via-nimbus_identity)
for the full walk-through.

### On Azure (via the `nb` CLI)

```powershell
$env:NIMBUS_SQL_ADMIN_PASSWORD = '<sql-pwd>'
$env:NIMBUS_IDENTITY_ADMIN_PASSWORD = '<pwd>'
nb setup `
  --solution-id nimbus --environment dev `
  --resource-group rg-nimbus-dev --location northeurope `
  --storage-provider sqlserver --sql-mode provision `
  --sql-admin-login <sql-admin> `
  --identity-admin-email <admin-email>
Remove-Item Env:NIMBUS_SQL_ADMIN_PASSWORD
Remove-Item Env:NIMBUS_IDENTITY_ADMIN_PASSWORD
```

The bicep wires the WebApp app settings
(`deploy/bicep/deploy.webapp.bicep`), seeding `NimBusIdentity__*` from
the CLI input. `RequireEmailConfirmation` is forced to `false` so the
bootstrap admin can sign in without SMTP setup.

**Post-deploy hygiene.** Remove `NimBusIdentity__Bootstrap__Password`
from the WebApp app settings after the first sign-in. The hosted
service is idempotent — it no-ops once any user exists — so leaving
the settings in is functionally harmless, but the password sitting in
plain Azure config is a leak waiting to happen.

## Bootstrap admin

When `NimBusIdentity:Bootstrap:Email` and `Bootstrap:Password` are both
set AND the user store is empty,
`IdentityInitializerHostedService` (in `NimBus.Extensions.Identity`)
creates one `NimBusUser` with `EmailConfirmed=true` during host
start-up. The service also creates the Identity schema (default
`nimbus`) and the eight `AspNet*` tables if they're not there yet.

Idempotency means the bootstrap is fire-and-forget:

- Drop the `nimbus.AspNet*` tables and the next boot re-seeds.
- Change the password from the management UI after first sign-in;
  later boots no-op even with the original bootstrap env vars still set.

## Signing in and out

### Endpoints

| Method | Route | Purpose |
|---|---|---|
| GET | `/account/login` | Login form (Razor view from Identity package) |
| POST | `/account/login` | Submit credentials |
| GET | `/account/register` | Self-registration form |
| POST | `/account/register` | Create a user |
| GET | `/account/forgot-password` | Password-reset request (sends mail when SMTP configured) |
| POST | `/account/reset-password` | Apply a reset token |
| GET | `/account/confirm-email` | Email verification callback |
| POST | `/account/logout` | Razor form-post logout |
| GET | `/api/auth/me` | SPA-facing current-user JSON (200 + `isAuthenticated`) |
| POST | `/api/auth/logout` | SPA-facing logout (204) |

The management WebApp's sidebar shows the signed-in user's email plus
a sign-out icon button in its footer when `/api/auth/me` reports an
authenticated session. The button POSTs to `/api/auth/logout` and
hard-navigates to `/account/login` so cookie state on the next page is
clean. On Entra-only slots the endpoint returns 404 and the footer is
hidden — the sidebar looks the same as before.

### Cookie

| Property | Value |
|---|---|
| Name | `NimBus.Identity` |
| `HttpOnly` | true |
| `Secure` | always |
| `SameSite` | Lax (default) |
| Lifetime | 8 hours, sliding expiration |
| Login redirect | `/account/login` |
| Logout redirect | `/account/login` |

Configured at `src/NimBus.Extensions.Identity/ServiceCollectionExtensions.cs:54–64`.

### Password policy

Enforced by `AddIdentity` at registration time
(`ServiceCollectionExtensions.cs:39–50`):

- 8 characters minimum
- one lowercase, one uppercase, one digit
- non-alphanumeric not required
- unique email required
- account locks after 5 failed attempts for 15 minutes

Password policies aren't configurable via `NimBusIdentityOptions`
today. If you need to relax or tighten them, fork the registration
call.

## Configuration reference

All `NimBusIdentity:*` keys (read from any standard ASP.NET Core
configuration provider — `appsettings.json`, env vars with `__`
separator, user-secrets, Azure App Service settings):

| Key | Default | Notes |
|---|---|---|
| `NimBusIdentity:ConnectionString` | (unset) | When set, opts the WebApp into the Identity-only auth branch. The same SQL DB can host both the message store and Identity (different schemas). |
| `NimBusIdentity:Schema` | `nimbus` | Schema for the eight `AspNet*` tables. Must be a SQL-safe identifier. |
| `NimBusIdentity:RequireEmailConfirmation` | `true` | `false` lets the bootstrap admin sign in without SMTP. |
| `NimBusIdentity:EnableEntraIdLogin` | `false` | Shows a *Sign in with Microsoft* button on `/account/login`. |
| `NimBusIdentity:Bootstrap:Email` | (unset) | First-admin email — seeded once when the user store is empty. |
| `NimBusIdentity:Bootstrap:Password` | (unset) | First-admin password. |
| `NimBusIdentity:Bootstrap:DisplayName` | (unset) | Optional display name for the seeded admin. |
| `NimBusIdentity:Smtp:Host` | (unset) | SMTP server for confirmation / reset mail. Warns if empty. |
| `NimBusIdentity:Smtp:Port` | `587` | |
| `NimBusIdentity:Smtp:FromAddress` | (unset) | |
| `NimBusIdentity:Smtp:FromName` | `NimBus` | |
| `NimBusIdentity:Smtp:Username` | (unset) | Optional SMTP auth. |
| `NimBusIdentity:Smtp:Password` | (unset) | Optional SMTP auth. |
| `NimBusIdentity:Smtp:UseSsl` | `true` | |

### Authorization: storage-backed roles (spec 026)

Authorization (as opposed to authentication) uses storage-backed
access-control lists: **Reader < Contributor < Owner** site-wide and
per-endpoint, plus the orthogonal **PII Reader** capability (raw
payload reveal — not implied by Owner). Site Owners manage the lists on
the WebApp's **Access Control** page; entries are email addresses or
Entra object IDs. Grants are stored in the message store (Cosmos
container `accesscontrol` / SQL table `AccessControl`) and take effect
immediately on the mutating instance (≤45s elsewhere).

**Bootstrap / compat.** The internal `EIP_Management` marker claim maps
to site Owner, so the first Owner never locks themselves out of an
empty store. The marker is materialized by any of:

| Source | How |
|---|---|
| `Authorization:AdminGroupObjectIds` | Entra group **object IDs** (array or comma-separated) whose members become site Owners — requires the app registration to emit group claims (`groupMembershipClaims: SecurityGroup`) |
| `Authorization:AdminUserObjectIds` | Individual Entra user object IDs (oid) that become site Owners |
| `LocalDevAuthHandler` | Local dev sign-ins are site Owners (but NOT PII Readers) |
| Identity role mapping | An Identity user in a role named `EIP_Management` |

On App Service, set e.g. `Authorization__AdminUserObjectIds` =
`<user-object-id>` (applied by `EntraAdminClaimsTransformation` on the
Entra-only auth branch). The deploy bicep preserves `Authorization__*`
app settings across infra re-applies, same as `AzureAd__*`.
Code-defined `endpoint.RoleAssignments` object IDs map to endpoint
Owner. `Authorization:GrantPiiReaderInDevelopment` grants PII Reader in
Development only (startup fail-fast elsewhere).

Entra ID configuration (when `AzureAd:ClientId` is set, the WebApp
takes the Entra branch — and the dual branch when Identity is also
configured):

| Key | Required | Notes |
|---|---|---|
| `AzureAd:Instance` | yes | Usually `https://login.microsoftonline.com/` |
| `AzureAd:TenantId` | yes | Tenant GUID |
| `AzureAd:ClientId` | yes | App-registration client ID — the trigger for the Entra branch |
| `AzureAd:Domain` | yes | `<tenant>.onmicrosoft.com` |
| `AzureAd:CallbackPath` | yes | Typically `/signin-oidc` |
| `AzureAd:ClientSecret` | conditional | Confidential client; can be replaced by a federated credential |

When no provider is configured (no local-dev bypass, no Identity and an
empty `AzureAd:ClientId`), the WebApp refuses to start with
`No sign-in method is configured for the NimBus WebApp`. Before this
check it started and then failed every request with `IDW10106: The
'ClientId' option must be provided`. The fix is to configure one
provider: Identity for SQL-backed deployments without an Entra
registration, otherwise Entra. The Aspire AppHosts turn on the local-dev
bypass themselves unless Identity is enabled.

## Application Insights log queries

The management API's event logs endpoint
(`GET /api/event/details/{endpointId}/{id}/logs`) reads an event's traces
through the Application Insights query API
(`https://api.applicationinsights.io/v1/apps/{AppInsights:ApplicationId}/query`).
Microsoft retired API keys for that API on 2026-03-31
([announcement](https://learn.microsoft.com/answers/questions/2260881/api-keys-for-querying-data-from-azure-monitor-appl)),
so the WebApp sends a Microsoft Entra bearer token instead. It gets the
token from `DefaultAzureCredential` for the scope
`https://api.applicationinsights.io/.default`.

| Where the WebApp runs | Identity | Access it needs |
|---|---|---|
| App Service | The site's system-assigned managed identity | **Reader** on the Application Insights component. `deploy.webapp.bicep` grants it whenever `appInsightsAppId` is set, which `nb infra apply` always does |
| Locally | Your developer credential (Azure CLI, Visual Studio, …) | Reader, or any role with `Microsoft.Insights/components/query/read`, on the component |

Reader is the role Microsoft documents for
[Entra-authenticated queries](https://learn.microsoft.com/azure/azure-monitor/app/azure-ad-authentication#query-application-insights-by-using-microsoft-entra-authentication).
Monitoring Reader works too, but it adds permissions the WebApp doesn't need.

- With `AppInsights:ApplicationId` empty (the local default), the WebApp
  requests no token and the endpoint returns no logs.
- Locally, if the token request fails with `ManagedIdentityCredential
  authentication failed`, set `AZURE_TOKEN_CREDENTIALS=dev` so
  `DefaultAzureCredential` skips managed identity and uses your developer
  credential.
- `AppInsights:ApiKey` is no longer read. `nb infra apply` no longer creates
  or deletes the `management-app` API key. `deploy.webapp.bicep` still accepts
  its `apiKey` parameter but ignores it; the parameter is deprecated and is
  removed in the next major version.
- An existing `management-app` key can't query any more, and `nb infra apply`
  now leaves it in place. To remove it, run
  `az monitor app-insights api-key delete --app <component> --resource-group <rg> --api-key management-app`.
- When a query fails, the WebApp logs a `GetEventDetailsLogsIdAsync` warning
  and the endpoint returns an empty list. A 403 usually means the role assignment is missing or
  hasn't propagated yet (allow a few minutes after the deployment). If the
  component is workspace-based and its Log Analytics workspace uses the
  *Require workspace permissions* access control mode, Reader on the
  component isn't enough: also grant the identity read access to the
  workspace ([access control modes](https://learn.microsoft.com/azure/azure-monitor/logs/manage-access#access-control-mode)).

## Where things live (source pointers)

| Concern | File |
|---|---|
| Auth branch ladder | `src/NimBus.WebApp/Startup.cs:60–165` |
| Fallback redirect to login | `src/NimBus.WebApp/Startup.cs:400–412` |
| `AddNimBusIdentity` DI registration | `src/NimBus.Extensions.Identity/ServiceCollectionExtensions.cs` |
| Schema creation + bootstrap admin | `src/NimBus.Extensions.Identity/Services/IdentityInitializerHostedService.cs` |
| Razor login / register / reset views | `src/NimBus.Extensions.Identity/Controllers/AccountController.cs` |
| SPA-facing `/api/auth/*` | `src/NimBus.Extensions.Identity/Controllers/AuthApiController.cs` |
| Sidebar user footer + sign-out | `src/NimBus.WebApp/ClientApp/src/components/sidebar-user-footer.tsx` |
| Aspire opt-in (`NIMBUS_IDENTITY`) | `src/NimBus.AppHost/Program.cs` |
| Bicep app-setting wiring | `deploy/bicep/deploy.webapp.bicep` |
| Application Insights query token | `src/NimBus.WebApp/Services/ApplicationInsights/ApplicationInsightsAuthenticationHandler.cs` |
| WebApp role assignments (incl. Reader on Application Insights) | `deploy/bicep/templates/roleAssignments.bicep` |
| CLI flags for the bootstrap admin | `src/NimBus.CommandLine/Program.cs` (search `--identity-admin-`) |

## See also

- [`webapp-rest-api.md`](webapp-rest-api.md)
  — how to *call* the management API once you have a credential (cookie or
  bearer token); this page covers credential setup only
- [`storage-providers.md` § Local sign-in via NIMBUS_IDENTITY](storage-providers.md#local-sign-in-via-nimbus_identity)
  — the click-by-click local walk-through
- [`sdk-api-reference.md` § Identity Extension](sdk-api-reference.md)
  — the underlying `AddNimBusIdentity` API
- [`extensions.md`](extensions.md) — the wider NimBus extensions catalogue
