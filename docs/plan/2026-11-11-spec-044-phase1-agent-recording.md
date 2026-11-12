# Plan: Spec 044 Phase 1 — agents record failure classifications

Spec: [docs/spec/044-agent-first/spec.md](../spec/044-agent-first/spec.md) §6. Baseline: master
`1821c42e`. Branch: `feat/spec-044-phase1`. Delivery: one PR, one commit per part (§6.11).
Phases 2-4 are out of scope; each gets its own plan.

## Part 1 — Extension (`NimBus.Extensions.IntegrationIntelligence`)

**Validation split and modes (§6.7).**
- `FailureClassificationOptions`: add `ValidateEvidence()` (thresholds, history, lengths) and
  `ValidateProvider()` (provider name, key, URL, timeout). `Validate()` stays and returns both.
- `Provider` accepts `"None"` (case-insensitive) besides `"TypeSafe"`. With `"None"`
  `ValidateProvider()` returns nothing, so a missing key is no longer a warning. The default
  stays `"TypeSafe"` until v5.0.0.
- `IntegrationIntelligenceActivation` gains `Recordable` (init). Rules in
  `AddNimBusIntegrationIntelligence`:
  `readable = enabled && bound && storage && host`;
  `recordable = readable && ValidateEvidence() is empty`;
  `ready = recordable && Provider == TypeSafe && ValidateProvider() is empty`.
  The warning hosted service fires only for evidence errors, provider errors with
  `Provider = TypeSafe`, missing storage or a missing host.
- From Recordable up, register `IntelligenceDataRedactor`, `FailureEvidenceBuilder` and the new
  `FailureClassificationRecorder` (scoped). The provider, its `HttpClient` and
  `FailureClassificationService` stay Ready-only, and so does the POST controller.
- `IntegrationIntelligenceStatusService` reports `"Recordable"` for that mode. Read-only stays
  `"ProviderNotConfigured"` and Ready stays `"Ready"`. `CanAnalyze` stays Ready-only, and a new
  `CanRecord` is added to `IntegrationIntelligenceStatus`.

**Question set, version 2 (§6.2).**
- `FailureClassificationQuestionSet.Version = 2`. `Items` = the four v1 questions plus
  `fault_origin` (choice: `source_data`, `adapter_code`, `target_system`, `configuration`,
  `platform`, `unknown`, with a meaning and examples for each). Add `FaultOrigins`
  (`IReadOnlySet<string>`).
- Add `ProviderVersion = 1` and `ProviderItems` (exactly the v1 four questions). The TypeSafe
  provider sends `ProviderItems`, and the service stores `ProviderVersion`, so the provider's
  wire format and tests do not change.
- Criteria values become a `QuestionChoice(What, Examples)` record with
  `[JsonPropertyName("what"/"examples")]`. It serializes byte-identically to today's anonymous
  objects, and the MCP projection can read it typed.

**Record fields (§6.4).** `FailureClassification` gains the optional members `FaultOrigin`,
`FaultOriginConfidence`, `ClientId` and `Rationale`. They live in the JSON document, so there is
no migration. `FailureClassificationInput` gains the optional `SourceEndpoint` (`From`) and
`OriginatingEndpoint` (`OriginatingFrom` when it differs), read from the tracked event. The
provider's `OutboundFailureState` is unchanged.

**Guidance.** Add a `FailureGuidanceRules.Compose(string category, double categoryConfidence,
double retryLikelihood, double changeRequiredLikelihood, FailureClassificationOptions)`
overload. The existing overload forwards to it.

**`FailureClassificationRecorder`** (new, public):
- `GetEvidenceAsync(eventId, messageId)` checks Reader (`LoadAuthorizedMessageAsync`), the
  allow-list and eligibility (`BuildAsync` returns null → `FailureNotEligible`), then returns
  the input plus the latest saved classification.
- `RecordAsync(FailureClassificationRecording request)` validates first and stores nothing on
  any refusal: question-set version, category, origin, finite 0-1 numbers, a model of at most
  100 characters with no control characters, and a rationale of at most 1,000 characters with
  no control characters other than tab and newline. It then checks Contributor, rebuilds the
  evidence (eligibility, endpoint match, `EventPayloadIncluded`) and scrubs the rationale.
  Values the event's metadata marks sensitive are replaced, as error text is, and the secret
  patterns are applied. A rationale that looks like a stack trace is refused.
- It then calls `ReserveAsync(force: supersede)`. A cached result returns
  `(result, Existing: true)`. An active operation → `AnalysisInProgress`. An unknown outcome →
  `AnalysisOutcomeUnknown` (the tool tells the agent to use `supersede=true`).
- It builds the record with `Provider "Agent"`, `Model` (or `"unspecified"`), a single-entry
  `CategoryProbabilities`, null tokens, and `QuestionSetVersion = 2`, and composes the guidance
  with the new overload. It then calls `CompleteAsync`, with the same lost-acknowledgement
  read-back as the service.
- It audits `FailureClassified` (provider `Agent`, clientId, faultOrigin, outcome) in `finally`,
  denials included. It records telemetry through `NimBusIntelligenceTelemetry` with provider
  `Agent`.

**Tests** (`tests/NimBus.Extensions.IntegrationIntelligence.Tests`, new
`RecorderTests.cs`):
- With `Provider None` and no key the mode is Recordable, with no warning; with TypeSafe
  without a key it is Recordable plus a warning.
- Each refusal in §6.12 stores nothing.
- Repeating an idempotency key returns the stored result. `supersede=false` adds no revision;
  `supersede=true` adds one and keeps history.
- The guidance equals `Compose`.
- A sensitive value in the rationale is replaced.
- The new fields round-trip through `DurableStoreConformanceTests` (live SQL/Cosmos), and v1
  JSON reads unchanged.
- `ContractsAndStoreTests`' version assertion is updated (1 → 2, plus a `ProviderVersion == 1`
  assertion). `TypeSafeProviderTests` stays unchanged.

## Part 2 — MCP (`src/NimBus.WebApp/Mcp`)

- `IOperatorClassificationSource` gains `CanRecord`, `GetEvidenceAsync`, `RecordAsync` and
  `ReadLatestInBulkAsync`. `OperatorClassificationSource` resolves an optional
  `FailureClassificationRecorder` and maps `ClassificationServiceException` codes to
  `OperatorToolErrors` as `AnalyzeAsync` does.
- **`nimbus_get_failure_evidence`** (read tool, `OperatorInsightTools`):
  - Arguments: `endpointId`, `eventId`, optional `messageId` (defaults to the latest attempt).
  - Returns: the coordinates, the status, retry count and limit, the scrubbed exception, the
    dead-letter reason, recent history, the sending endpoints, the redacted payload when
    included, the question set (version, and each question with its answer argument, type,
    instructions and choices), the resolved `messageId`, the latest classification summary and
    an untrusted-data note.
  - Refusals: not readable → `[MessageNotFound]`; not eligible → `[ActionNotAllowed]`; mode
    below Recordable → `[FeatureUnavailable]`.
- **`nimbus_record_classification`** (`OperatorActionTools`; `ReadOnly=false, Idempotent=true,
  Destructive=false, OpenWorld=false`):
  - It checks scope and policy (`RequireScopeAsync(Classify)`), the endpoint's change scope, a
    fresh Contributor check and the endpoint match.
  - It calls the recorder with the client id (`azp`/`appid`). It does **not** take the
    mutation limiter.
  - It returns a `ClassificationResult` with `Existing`.
  - It is added to `McpToolAccess.ChangeTools` → `Classify`.
- **`nimbus_find_messages includeClassification`**: one bulk read of the page's
  Failed/DeadLettered rows, joined on `LastMessageId`. A snapshot whose endpoint differs or that
  is tombstoned counts as unclassified. `MessageSummary` gains a nullable `Classification`
  (`Saved`, `Category`, `Guidance`, `FaultOrigin`, `Provider`). `MessageListResult` gains
  `ClassificationStatus` (`available`/`unavailable`/`notEnabled`, null when not requested).
- `ClassificationInfo` gains `FaultOrigin`, `FaultOriginConfidence`, `ClientId` and
  `Rationale`, marked untrusted in the description.
- **Capabilities.** `FeatureInfo` gains `CanRecordClassifications` and
  `CanAnalyzeWithProvider`. `classify` in permitted and eligible actions follows `CanRecord`.
- **`nimbus_classify_failure`.** The description says it is deprecated and points to the two
  new tools. It keeps requiring the delegated scope; a workload role never admits it.
- **Prompt `handle-failures`** (`endpointId?`, `period`). Steps as in §6.9, with the outcome
  table of §6.2. It is listed only while `nimbus_record_classification` is available
  (`McpPromptAccess`).
- **`OperatorInstructions`.** Name the two tools and say a rationale is untrusted. The existing
  tests keep the 4,096 and 2,048 limits.
- **Tests:** `OperatorInsightToolsTests`, `OperatorActionToolsTests`, `OperatorPromptsTests`,
  `OperatorInstructionsTests` and `McpAccessPolicyEnforcementTests`. Cover refusals, the
  limiter not being taken, tool and prompt listing, and the `includeClassification` join with
  the unavailable case.

## Part 3 — Web UI

- **API spec.** `InsightOccurrence` gains `provider` and `faultOrigin` (nullable), and
  `FailedInsightsService.Occurrence` fills them. Agent results already count in coverage,
  causes and guidance because `Assess` checks only the category and guidance; a test pins this.
- **`intelligence-card.tsx`.**
  - Status `Recordable` shows the card when a result exists, without an Analyze button, and the
    note "Agents record classifications over MCP".
  - An agent result shows "Recorded by an agent", the model, the client, the requester, the
    fault origin and the rationale, rendered as text.
  - `lib/failure-classification.ts` gains the origin labels.
- **Insights group detail** (`pages/insights.tsx`): a Provider column (Agent / TypeSafe).
- **Settings → Failure intelligence:**
  - Mark the model, timeout and API-key fields "Deprecated — removed in v5.0.0".
  - Reword the payload switch to "Agents may see redacted payloads", with a note that only
    values marked sensitive are redacted.
- **Settings → MCP access:** a "Workloads may record classifications" toggle under
  **Unattended workloads**, disabled while workloads are refused (Part 4).
- **Tests:** Vitest for the card, insights and both settings panels. A screenshot of the card
  goes in the PR (CrmErpDemo or a mocked API preview).

## Part 4 — Workloads (§6.6)

- `McpOperatorPermissions.ClassifyRole = "Nimbus.Classify"`.
- `McpAccessSettings.AllowWorkloadClassification` (default false), stored in the JSON
  document:
  - `api-spec.yaml` `McpAccessSettings.allowWorkloadClassification`;
  - `McpAccessApiMapper` both ways;
  - `McpAccessRules.Changes` ("Let workloads record classifications" widens);
  - the conformance round-trip sample in `EndpointMetadataStoreConformanceTests`;
  - `mcp-access-model.ts` diff text.
- `McpAccessPolicy.AllowsWorkloadClassification` = `AllowWorkloads &&
  AllowWorkloadClassification`.
- `OperatorActionAccess.HoldsScope(Classify)` also accepts a workload (no `scp`) holding the
  `Nimbus.Classify` role while the policy allows workload classification. Classify's
  `RequireScopeAsync` message names both routes. `nimbus_classify_failure` checks the delegated
  scope itself. Contributor still comes from the ACL (workload `oid`).
- **Tests:** a workload token with the role and the switch on records. It is refused without the
  role, with the switch off, or when calling `nimbus_classify_failure`.

## Part 5 — Deprecation (§6.10) and docs

- `[Obsolete("… removed in v5.0.0; agents record classifications over MCP (Spec 044)")]` on
  `TypeSafeFailureIntelligenceProvider`, `TypeSafeProviderOptions`,
  `IntelligenceProviderException`, `IFailureIntelligenceProvider`,
  `FailureClassificationService.AnalyzeAsync`, `FailureClassificationOptions.ApiKey/BaseUrl/
  Model/TimeoutSeconds` and `FailureClassificationOptions.TypeSafe`.
- Release turns CS0618 into an error, so internal uses get file-level
  `#pragma warning disable CS0618` with a one-line reason: registration, the service, the
  provider, the controllers, `OperatorClassificationSource`, `IntelligenceAdminSettings` and
  tests.
- `SaveIntelligenceSettings.ApiKey/ClearApiKey`, the sealed key and the POST analyze route are
  documented as deprecated in XML docs and `docs/integration-intelligence.md`. They are
  WebApp-internal, not package API.
- Docs: `docs/integration-intelligence.md` (modes, `Provider: None`, recording, deprecation),
  `docs/mcp-server.md` (the two tools, `includeClassification`, the prompt, `Nimbus.Classify`
  and the switch), `docs/features.md`.
- Release note: the Compatibility text below goes in the PR description for the release
  cutter. Release notes live on the GitHub release (`docs/versioning.md`); there is no file.

### Public members and replacements

| Deprecated | Replacement |
|---|---|
| `TypeSafeFailureIntelligenceProvider`, `IFailureIntelligenceProvider`, `IntelligenceProviderException`, `TypeSafeProviderOptions` | An agent records over MCP: `nimbus_record_classification` |
| `FailureClassificationService.AnalyzeAsync` | `FailureClassificationRecorder.RecordAsync` |
| `FailureClassificationOptions.ApiKey/BaseUrl/Model/TimeoutSeconds/TypeSafe` | None; set `Provider` to `None` |
| `POST /api/integration-intelligence/failures/{eventId}/{messageId}/classification` | None (no manual recording, §12) |
| `PUT /api/admin/failure-intelligence` `apiKey`, `clearApiKey` | None |
| `nimbus_classify_failure` | `nimbus_get_failure_evidence` + `nimbus_record_classification` |
| `FailureGuidanceRules.Compose(FailureIntelligenceProviderResult, …)` | `Compose(category, confidence, retry, change, options)` |

### Compatibility (release note)

- Additive in 4.x. A TypeSafe key keeps working until v5.0.0.
- A deployment with classification enabled and no key moves from read-only to Recordable.
  Nothing is recorded until a caller holds `nimbus.classify` and Contributor.
- With `Data.IncludeEventPayload` on, redacted payloads now reach agents that can read evidence.
- Workloads need the new `Nimbus.Classify` app role and the Owner switch. Both are off by
  default.
- No schema migration, topology change or wire change.

## Verification

- `dotnet build src/NimBus.slnx -c Release`, pinned to the newest 10.0.4xx SDK with a temporary
  `global.json`, then `dotnet test -c Release --no-build`, gated on the build's exit code.
- `npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`.
- `npm --prefix website ci && run build` (docs links).
- Live SQL/Cosmos conformance is run if containers are available; otherwise it is reported as
  skipped.
- §6.12's last acceptance check (`handle-failures` from Claude Code on CrmErpDemo) is a manual
  check after merge. It needs the stack running with error mode on; it is reported if not done.
