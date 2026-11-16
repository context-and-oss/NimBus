# Remove the deprecated built-in failure-intelligence provider (v5.0.0)

## Context

Spec 044 (decision 1, `docs/spec/044-agent-first/spec.md` §5 and §6.10) deprecated the built-in
TypeSafe provider in 4.x and scheduled its removal for v5.0.0. The user asked to remove it now and
chose **merge to master, next release is v5.0.0**. The other "removed in v5" obsoletes (metrics
overloads, `ServiceBusAdapter`/`SubscriberClient` overloads) are out of scope.

Kept, per Spec 044 decision 2 and §13: stored classifications (including `Provider: "TypeSafe"`,
question set v1 records), the question set, guidance rules, Insights, the read routes, MCP
recording (`nimbus_get_failure_evidence`, `nimbus_record_classification`, `nimbus_get_classification`).

Work happens in worktree `C:\Git\NimBus\.claude\worktrees\remove-typesafe-provider`, branch
`refactor/remove-typesafe-provider` from `origin/master` (584caea3). One PR, a commit per part,
`refactor(intelligence)!:` style Conventional Commits. The plan is copied to
`docs/plan/2026-11-16-remove-typesafe-provider.md` as the first commit.

## Part 1: extension (`src/NimBus.Extensions.IntegrationIntelligence`)

Delete:
- `Providers/TypeSafeFailureIntelligenceProvider.cs` (`TypeSafeFailureIntelligenceProvider`, `IntelligenceProviderException`).
- From `IntegrationIntelligenceContracts.cs`: `IntegrationIntelligenceDeprecation`, `IFailureIntelligenceProvider`,
  `FailureIntelligenceProviderResult`, `TypeSafeProviderOptions`; on `FailureClassificationOptions`:
  `Provider`, `NoProvider`, `TypeSafeProvider`, `ProviderDisabled`, `ApiKey`, `BaseUrl`, `Model`,
  `TimeoutSeconds`, `TypeSafe`, `ValidateProvider()`. `Validate()` becomes `=> ValidateEvidence()`;
  `ApplyNestedSettings()` loses the TypeSafe copy.
- `FailureClassificationService` (whole class: its constructor needs the provider, and its two read
  methods only forward to `FailureClassificationReader`). Move `ClassificationServiceException` to
  its own file, unchanged.
- `IntegrationIntelligenceController`, `ClassificationRequest`, `ClassificationResponse` (POST analyze).
- `IntegrationIntelligenceAuditMiddleware` and `IntegrationIntelligenceRequestMarkers` (they only audit the POST).
- `FailureGuidanceRules.Compose(FailureIntelligenceProviderResult, …)` bridge overload.
- `FailureClassificationQuestionSet.ProviderItems` / `ProviderVersion` (nothing asks v1 any more;
  doc comment notes v1 records exist and stay readable). `FailureClassificationProviders.TypeSafe`
  stays, documented as the provider of results made before v5.0.0.

Change (`IntegrationIntelligenceRegistration.cs`):
- Drop Ready mode: no HttpClient, no provider, no service registration; feature provider loses `ready`.
- `IntegrationIntelligenceActivation(bool Enabled, IReadOnlyList<string> ValidationErrors) { Readable, Recordable }` (no `Ready`).
- `IntegrationIntelligenceStatus(Status, ContractVersion, EndpointId) { CanRecord }` — drop `Provider`,
  `Model`, `CanAnalyze`. Status values: `Recordable` or `ReadOnly` (renamed from
  `ProviderNotConfigured`, which no longer describes the state). Breaking, called out in the release note.
- New one-shot startup warning (registered with `AddSingleton<IHostedService>(factory)`, per the
  AddHostedService dedup memory) when the bound section still carries removed keys:
  `FailureClassification:Provider` other than `None`, `FailureClassification:TypeSafe:*`, or flat
  `ApiKey`/`Model`/`BaseUrl`/`TimeoutSeconds`. Text names keys only, never values. The binder
  ignores the keys, so such a deployment starts in Recordable mode instead of failing.
- `InvalidConfigurationWarning` text: "analysis is unavailable" → "recording is unavailable".

## Part 2: WebApp

- `Startup.Pipeline.cs:120`: remove `UseMiddleware<IntegrationIntelligenceAuditMiddleware>`.
- `Services/IntegrationIntelligence/`:
  - `IntelligenceAdminSettings`: drop `Model`, `TimeoutSeconds`, `IsValidApiKey`; `ApplyTo(configuration)`
    without the key and TypeSafe/flat-timeout keys; `FromConfiguration` without the pragma.
  - `IntelligenceSettingsDocument(Settings, Revision)` — drop `ProtectedApiKey`. Stores deserialize with
    Newtonsoft, which ignores the old `Model`/`TimeoutSeconds`/`ProtectedApiKey` members, so saved
    records stay loadable (regression test below). The next save drops them.
  - `IntelligenceSettingsBootstrap`: drop `ApiKeySourceKey`, key constants, `HasDeploymentCredential`,
    the protector overload and its Data Protection registration.
  - `IntelligenceSettingsSnapshot(Active, Revision, LoadFailed)`.
  - Delete `IntelligenceSecretProtector.cs`. **Keep** `services.AddDataProtection().SetApplicationName("NimBus.WebApp")`
    in `Startup.cs` (MCP action tokens in `Mcp/Operations/OperatorActionTokens.cs` and antiforgery rely
    on that key ring); move the constant next to it instead of deleting it.
- `Controllers/IntelligenceSettingsController.cs`: drop `ApiKey`/`ClearApiKey`, key validation, sealing,
  `savedApiKey`/`credentialConfigured`/`credentialSource` in the state, and `apiKey` in the audit data.
- MCP (`src/NimBus.WebApp/Mcp`): remove `nimbus_classify_failure` (`Tools/OperatorActionTools.cs`) and its
  `Access/McpToolAccess.cs` entry; `IOperatorClassificationSource` loses `CanAnalyze`/`AnalyzeAsync` and the
  `FailureClassificationService` dependency (`Operations/OperatorClassificationSource.cs`,
  `McpOperatorServiceCollectionExtensions.cs:106`); `|| CanAnalyze` goes from `OperatorWorklist`,
  `OperatorDiscoveryTools`, `OperatorMessageTools`; `FeatureInfo` drops `CanAnalyzeWithProvider`.
  Doc comments in `McpOperatorAuthorization.cs:61`, `McpAccessSettings.cs:118` (`Classify` switch now
  covers `nimbus_record_classification`), `OperatorInsightTools.cs` (provider description).
  The `AnalysisInProgress`/`AnalysisOutcomeUnknown` mappings stay: the recorder reserves operations too.
- Insights: `FailedInsightsService` `CanAnalyze` → `CanRecord` (from `Activation.Recordable`), so
  `analysisAvailable` means "agents can record new classifications". Update its `api-spec.yaml`
  description (line ~7185) and the provider description (~7352: TypeSafe = results made before 5.0.0).
  NSwag regenerates `ApiContract.g.cs` / `api-client/index.ts` on build.
- SPA (`src/NimBus.WebApp/ClientApp/src`):
  - `components/admin/failure-intelligence-settings.tsx`: remove the provider block (provider, model,
    key, remove-key, timeout, deprecation badge, credential line, missing-key notice); reword consent,
    review and payload texts without "deprecated provider".
  - `components/event-details/intelligence-card.tsx`: remove `analyze()`, the Analyze/Re-analyze button,
    charge note and `canAnalyze`; status check becomes `Recordable` | `ReadOnly`; keep GET handling of
    in-progress/unknown codes.
  - `pages/insights.tsx`: unavailable text → "Agents can't record new classifications …".
  - `components/admin/mcp-access-model.ts` (Classify tools/description), `models/manage-pages.ts` keywords.
- `samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs:196`: drop the `Provider=None` env var.

## Part 3: tests (RED first)

Add failing tests before implementing, confirm they fail on master's code:
- Extension: a config with `Provider: TypeSafe` + `TypeSafe:ApiKey` activates as Recordable, registers no
  `IHttpClientFactory` client `NimBus.TypeSafe`, logs the removed-settings warning; status JSON has no
  `canAnalyze`/`provider`/`model` and reads `ReadOnly` when evidence settings are invalid.
- WebApp: POST to `/api/v1/integration-intelligence/failures/{e}/{m}/classification` → 404/405 with the old
  TypeSafe config (`IntelligenceActivationTests` host); MCP `tools/list` lacks `nimbus_classify_failure`
  (`McpOperatorEndpointTests`); a saved settings JSON containing `model`, `timeoutSeconds`,
  `protectedApiKey` loads through `IntelligenceSettingsBootstrap.LoadAsync` and the PUT rejects `apiKey`.
- SPA: the card renders no Analyze button and never POSTs; settings form has no key field.

Delete or rewrite tests of removed code: `TypeSafeProviderTests.cs`, the POST/race parts of
`ApiAndRaceTests.cs`, provider cases in `ServiceAndEvidenceTests.cs`, `ActivationDiagnosticsTests.cs`,
`AuditMiddlewareAndTelemetryTests.cs`, `ContractsAndStoreTests.cs`, `RecorderTests.cs:316`,
`IntelligenceSettings*Tests.cs`, `FailedInsightsServiceTests.cs`, the MCP tests that call
`nimbus_classify_failure`, and the `.test.tsx` files for the touched components.

## Part 4: docs

`docs/integration-intelligence.md` (remove "Deprecated: the built-in provider", Ready mode, key and
provider config; status values), `docs/mcp-server.md`, `docs/features.md:65`,
`docs/private-networking.md:77` (drop `api.typesafe.ai`), `docs/building-adapters.md:601`,
`docs/extensions.md`, `docs/webapp-insights.md`, `docs/failure-agent.md`, `site/features.html:609`;
status lines of Spec 033 and 044 ("provider removed in v5.0.0, PR #…"). PR description carries the
⚠️ Breaking list for the v5.0.0 release note (removed types/members, POST route, MCP tool, status
fields and `ReadOnly` rename, settings PUT fields, ignored config keys).

## Verification

From the worktree, with the SDK pinned to 10.0.403 via a temp `global.json` (memory: default SDK 11 RC fails Release):
- `dotnet build src/NimBus.slnx -c Release` (0 errors; NSwag generation on) and
  `set -o pipefail; dotnet test src/NimBus.slnx -c Release --no-build`.
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`; `npm --prefix website ci && npm --prefix website run build`.
- `grep -rn "TypeSafe\|classify_failure\|IFailureIntelligenceProvider" src tests docs` returns only
  the historical `FailureClassificationProviders.TypeSafe` and its docs.
- Live: run the CrmErpDemo stack, open a failed event (card shows an agent result, no Analyze button),
  Settings → Failure intelligence (no provider block), MCP `tools/list`. Screenshot of the settings
  panel and card for the PR (WebApp UI change). Live SQL/Cosmos conformance tests skip locally unless
  configured; report that.
- Open the PR, enable auto-merge (memory), report skipped checks.

## Amendments while implementing

- **Intelligence rate limit removed.** The `RateLimiting:Intelligence` policy limited only the
  analyze route, so `RateLimitPolicyNames.Intelligence`, `RateLimitOptions.Intelligence` and its
  convention entry go too (`docs/rate-limiting.md` updated).
- **Settings list status.** `components/settings/feature-status.ts` reported the API-key state
  ("No API key configured", "Key unreadable"). It now reports Off / On / restart required /
  unavailable, and the Off row offers "Turn on".
- **Card states simplified.** Without the Analyze button the classification GET returns either the
  saved result or a 409, never both, so the "historical result" label and its two flags were
  unreachable and are removed. The card still polls while a recording is being stored and reports
  an unknown outcome.
- **Unused delegated-scope check removed.** `OperatorActionAccess.RequireDelegatedScopeAsync`
  existed only for `nimbus_classify_failure`.
- **MCP tool count.** The Settings → MCP access model lists 24 tools (was 25); Classify lists one.
- **Live check.** Instead of the CrmErpDemo stack, the PR screenshot comes from the production SPA
  build served with a mock API (neutral demo data). The MCP `tools/list` change is covered by
  `ProviderRemovalTests.The_Mcp_Server_Has_No_Classify_Failure_Tool` and the endpoint tests.
