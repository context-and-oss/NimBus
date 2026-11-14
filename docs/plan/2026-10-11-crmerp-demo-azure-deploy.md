# CrmErpDemo: deploy to Azure Container Apps

## Context
`samples/CrmErpDemo` runs only under `aspire run`. Its AppHost has no Azure resources and relies on local-only pieces:
- the in-process Service Bus emulator,
- a SQL Server container and DbGate,
- Vite dev servers,
- the `EnableLocalDevAuthentication` bypass.

Goal: `aspire deploy` publishes the whole demo to a dedicated resource group, and the run-mode behaviour does not change.

Decisions taken with the user (2026-10-11):

| Topic | Decision |
|---|---|
| Hosting | `aspire deploy` to Azure Container Apps. A publish-mode branch in the existing AppHost, not separate Bicep. |
| Isolation | A dedicated resource group with its own Service Bus namespace and Azure SQL server. Teardown is `aspire destroy`. |
| nimbus-ops auth | NimBus Identity (username and password). The admin credentials are deploy-time secret parameters. |
| Scope | Code, a README section, and one real deployment to verify end to end. No CI workflow. |

## Branch
- `claude/crm-erp-azure-deploy-e3a4ec`, cut from `master`.
- Commit this plan as `docs/plan/2026-10-11-crmerp-demo-azure-deploy.md`.
- Open a PR against `master`.

## Target topology (publish mode)

| Resource | Azure | Exposure |
|---|---|---|
| `servicebus` | `AddAzureServiceBus`, Standard SKU, local auth off, managed identity (MI) gets Data Owner | n/a |
| `sql` + `crm`, `erp`, `nimbus` | `AddAzureSqlServer`, Entra-only. Aspire's deployment script grants each app's MI database access. | n/a |
| `aca` | `AddAzureContainerAppEnvironment`, which also provisions ACR, the managed identity and the Aspire dashboard | dashboard |
| `crm-api` | Container app that also serves the built `Crm.Web` | **external** |
| `erp-api` | Container app that also serves the built `Erp.Web` | **external** |
| `nimbus-ops` | Container app (WebApp + NimBus Identity), 1 replica | **external** |
| `resolver`, `erp-adapter`, `dataplatform-adapter` | Functions on ACA (KEDA scaling) | internal |
| `crm-adapter`, `partner-portal` | Container apps | internal |
| `provisioner` | Container Apps **job** (`PublishAsAzureContainerAppJob`), manual trigger | n/a |
| `agent-zone`, `enrichment-agent` | **Not deployed** (see §8) | n/a |
| DbGate, SB emulator | Run mode only | n/a |

Region: `swedencentral`. Westeurope has refused new Azure SQL servers on this subscription.

## Changes

### 1. AppHost publish branch (`CrmErpDemo.AppHost`)
- Add packages: `Aspire.Hosting.Azure.AppContainers`, `Aspire.Hosting.Azure.ServiceBus` and `Aspire.Hosting.Azure.Sql`, all 13.5.4.
- Branch on `builder.ExecutionContext.IsPublishMode` only where the two modes differ:
  - Service Bus: the emulator or user-secret connection string in run mode; `AddAzureServiceBus("servicebus")` in publish mode.
  - SQL: the container plus DbGate in run mode; `AddAzureSqlServer("sql")` in publish mode. Database names stay the same.
  - Pinned ports 5080, 5090, 18080 and 18443 apply in run mode only; they exist for Playwright.
  - The E2E profile is refused in publish mode.
- Functions get `AzureWebJobsServiceBus__fullyQualifiedNamespace` in publish mode instead of the plain `AzureWebJobsServiceBus` connection string. Resolver and WebApp already have managed-identity branches keyed on that setting (`NimBus.Resolver/ServiceExtensions.cs:43`, `NimBus.WebApp/Startup.Platform.cs:98`).
- New parameters:
  - `ops-admin-email`
  - `ops-admin-password` (secret)
  - `flow-webhook-key` (secret, generated). Sets `EventGrid__WebhookKey` on both the Resolver and nimbus-ops, which keeps the realtime Flow page working outside Development (`StorageHookImplementation.cs:123`).
- nimbus-ops settings in publish mode:
  - `NIMBUS_IDENTITY` is forced on and `EnableLocalDevAuthentication` is never set. That flag throws in Production.
  - `NimBusIdentity__RequireEmailConfirmation=false`
  - `RateLimiting__TrustForwardedForHeader=true`
  - Pinned to one replica through `PublishAsAzureContainerApp`. SignalR has no backplane and simulation and agent state are in memory.
- `crm-api` and `erp-api` are also pinned to one replica, because their demo toggles live in memory.

### 2. Service Bus managed identity in the sample code
Five code paths accept only a connection string. Each one gets a small branch: if the value has no `SharedAccessKey=` or `UseDevelopmentEmulator=`, treat it as a namespace and use `DefaultAzureCredential`. Run mode keeps the connection-string path.
- `CrmErpDemo.Provisioner/Program.cs`: use `ServiceBusTopologyProvisioner`'s existing token-credential overload and a credential-based `ServiceBusAdministrationClient`.
- `PartnerPortal/Program.cs`: add `Azure.Identity`. The sample deliberately avoids NimBus, so this stays plain Azure SDK.
- `Erp.Adapter.Functions/Program.cs` and `DataPlatform.Adapter.Functions/Program.cs`: their `ServiceBusClient` reads `AzureWebJobsServiceBus:fullyQualifiedNamespace` when it is set.
- The duplicated detection logic is a few lines; keep it per project rather than adding a shared helper to NimBus.

### 3. SQL via Entra (managed identity)
Aspire hands out `Authentication=Active Directory Default` connection strings. SqlClient 7.x needs `Microsoft.Data.SqlClient.Extensions.Azure` for that, and no project references it yet.
- Add the package where SqlClient 7 is loaded: `NimBus.WebApp`, `NimBus.Resolver`, `Erp.Api` and `Crm.Adapter`. First confirm the minimal set and whether the package registers itself or needs a call (spike S2).
- This fixes a product gap as well: managed-identity SQL for `--sql-mode external` customers (Spec 034 §5.12).
- Schema creation (DbUp, EF `CreateTables`, inbox/outbox `EnsureTable`) needs DDL rights. Read the role Aspire's deployment script grants (expected `db_owner`) in the published Bicep. If it is narrower, grant the roles through the infrastructure callback.

### 4. Platform catalog in the nimbus-ops image
`NimBus__PlatformAssembly` is set to a local Windows path today, and the WebApp image does not contain `CrmErpDemo.Contracts.dll`.
- Ship the DLL in the image and set `NimBus__PlatformAssembly=CrmErpDemo.Contracts.dll`. This is the same file-name mechanism `nb deploy apps --platform-package` uses (`AppDeploymentService.cs:184`).
- How the DLL gets into the image depends on spike S1:
  - **(a)** An opt-in MSBuild item in `NimBus.WebApp.csproj`, `NimBusPlatformAssemblyPath`, copied to publish output. The AppHost supplies it through the publish environment. `WithContainerBuildOptions` exposes no MSBuild properties.
  - **(b)** If (a) is not reliable, use a two-line Dockerfile layered on the published nimbus-ops image.

### 5. Frontends served by their APIs
- `crmApi.PublishWithContainerFiles(crmWeb, "./wwwroot")`, and the same for ERP.
- `Crm.Api` and `Erp.Api` add `UseDefaultFiles`, `UseStaticFiles` and `MapFallbackToFile("index.html")`. All API routes are under `/api`, so nothing collides, and the SPAs' relative `/api` calls become same-origin.
- In run mode the Vite dev servers stay exactly as they are.

### 6. Bootstrap admin has no rights (product bug)
The NimBus Identity bootstrap user is created with no roles, so on a fresh store it is denied everywhere. This also affects local `NIMBUS_IDENTITY=true`.
- Fix in `IdentityInitializerHostedService`: create the `EIP_Management` role if it is missing and add the bootstrap user to it. That role maps to the site-Owner claim (`NimBusClaimsTransformation.cs:36`).
- Write the regression test first.

### 7. Provisioner as a job
`WaitForCompletion` has no effect in ACA.
- Publish the provisioner as a manually triggered Container Apps job. Document the post-deploy step: `az containerapp job start`, then wait for success.
- Subscribers that start before topics exist retry by themselves: the SB processors and the Functions listener restart. Verify this during deployment and record the outcome.

### 8. Out of scope for this deployment
- **EnrichmentAgent and AgentZone are skipped in publish mode.** The agent authenticates with only an `X-Agent-Id` header, and with Identity on, `/api/agent/*` returns 401. Without the agent, AgentZone would park every `CrmContactCreated` forever. Adding an agent auth scheme is a separate feature; open a follow-up issue.
- App Insights. The ACA Aspire dashboard provides OpenTelemetry logs and traces.
- CI or GitHub workflow, custom domains, private networking.

### 9. Known risks accepted for a demo
- `crm-api` and `erp-api` are public and anonymous, including `DELETE /api/admin/data`. The data is fictional and resettable. The README says so; an IP allowlist is a later option.
- Rough cost while running: SB Standard about $10/month, ACR Basic about $5/month, SQL serverless free offer, ACA consumption. Tear down with `aspire destroy` when not in use.

## Spikes (before the main changes)
- **S1**: `aspire publish -o <scratch>` with the publish branch stubbed. Confirm how project images are built (SDK container publish) and whether an environment variable reaches MSBuild, then choose 4a or 4b. Also confirm the Functions-on-ACA output and the FQNS value format the Functions extension accepts: host name versus `https://…:443/`.
- **S2**: Find which processes load SqlClient 7 and whether `Microsoft.Data.SqlClient.Extensions.Azure` needs an explicit registration call.
- **S3**: Read the generated Bicep for SQL role grants and Service Bus role assignments per app identity.

## Verification
1. `dotnet build src/NimBus.slnx -c Release` and `dotnet test src/NimBus.slnx -c Release --no-build`.
2. Build the CrmErpDemo AppHost with the sample solution.
3. Local regression: `aspire run` with the emulator still works. CRM account → ERP customer → CRM round trip, and the resolver shows Completed.
4. `aspire publish -o <scratch>`: review the Bicep. Only crm-api, erp-api and nimbus-ops are external, and no secrets appear as literals.
5. Real deployment (needs approval before provisioning): run `aspire deploy` against the resource group `rg-crmerp-demo` in `swedencentral`, then:
   - start the provisioner job and wait for it to succeed,
   - in the CRM web, create an account and see it reach ERP and come back,
   - sign in to nimbus-ops as the bootstrap admin and see the events Completed on Endpoints and Flow,
   - see a PartnerPortal lead arrive in CRM.
6. Capture screenshots of the deployed nimbus-ops UI for the PR.
7. Docs: add a "Deploy to Azure" section to `samples/CrmErpDemo/README.md` covering prerequisites, parameters, the deploy command, starting the provisioner job, smoke checks and teardown.

## Outcome (2026-10-11)
- **S1:** option 4a works. `NimBusPlatformAssemblyPath` reaches MSBuild because Aspire builds the images from the AppHost process, and the image contains `CrmErpDemo.Contracts.dll`. Functions settings take the namespace **host name** (`AzureServiceBusResource.HostName`), not the `https://…:443/` endpoint.
- **S2:** referencing `Microsoft.Data.SqlClient.Extensions.Azure` registers the Entra ID providers with no code. It was added to the four SQL libraries, with a regression test in each.
- **S3:** each app identity gets `db_owner`. Service Bus is Standard with `disableLocalAuth: true`.
- Found while deploying:
  - Crm.Web's production build had been broken since the Tailwind 4 bump in #230; it is now migrated.
  - `aspire deploy` runs in the Production environment, so parameters must be `Parameters__*` environment variables, not AppHost user secrets.
- Verified on `rg-crmerp-demo` (swedencentral):
  - The provisioner job succeeded over managed identity.
  - CRM → ERP → CRM round trip.
  - PartnerPortal CloudEvents leads arrive in CRM.
  - The Resolver executes successfully.
  - nimbus-ops `/health` is Healthy, and the bootstrap admin was created as platform admin.
  - No `fail:` log entries after provisioning.
- Not verified by the agent: signing in to nimbus-ops. Entering the admin password on a hosted site is left to the user.
