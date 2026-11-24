# Integration Intelligence

Integration Intelligence is an optional, advisory failure-classification module for the management WebApp. An agent that the customer runs, with its own model, reads the evidence of one persisted failed or dead-lettered message occurrence over MCP, answers a fixed question set and records the answers in NimBus. NimBus stores each result with its provenance and a revision history, and composes the guidance itself with fixed rules. NimBus calls no model. A classification never retries, skips, resubmits, changes message state, or publishes a message.

NimBus 5.0.0 removed the built-in TypeSafe provider. See [Upgrading from 4.x](#upgrading-from-4x).

The feature is disabled by default. Enable it when the host has durable classification storage and the required endpoint roles:

```json
{
  "NimBus": {
    "IntegrationIntelligence": {
      "Enabled": true,
      "FailureClassification": {
        "Enabled": true,
        "Data": {
          "IncludeEventPayload": false,
          "IncludeRecentFailureHistory": true,
          "MaximumHistoryItems": 5,
          "MaximumErrorTextLength": 4000,
          "MaximumStateCharacters": 24000
        },
        "Thresholds": {
          "MinimumCategoryConfidence": 0.60,
          "RetryLikely": 0.75,
          "ChangeRequired": 0.75
        }
      }
    }
  }
}
```

To try it locally, the CrmErpDemo sample's erp-web **Error mode** toggle has a failure-reason dropdown that makes every ERP handler throw a realistic exception per classification category (transient dependency, expired credentials, schema mismatch, business rule, missing reference, application defect). See the sample README's [Failure reasons](../samples/CrmErpDemo/README.md#failure-reasons-integration-intelligence-showcase) section.

The API exposes an endpoint capability check at `GET /api/v1/integration-intelligence/status?endpointId=...`, the latest result at `GET /api/v1/integration-intelligence/failures/{eventId}/{messageId}/classification`, and revision history at `/history`. There is no REST route or Web UI form for recording a classification by hand.

Only users with Reader access can view results. Contributor access is required to record. The target endpoint comes from the stored message occurrence, and access is checked against that endpoint. Every recording is audited with `MessageAuditType.FailureClassified`, including access denials, with the caller, the client application and the provider (`Agent`).

### Recording classifications from an agent

Agents classify failures over the operator MCP endpoint (see [MCP server](mcp-server.md)):

1. `nimbus_find_messages` with `includeClassification: true` shows, for each Failed or DeadLettered message, whether its latest attempt has a saved classification.
2. `nimbus_get_failure_evidence` returns the scrubbed evidence for one attempt: its error, dead-letter reason, retry state, recent failures in the same session, the sending endpoint, a redacted payload when an Owner allows it, the current question set and the latest saved result. It needs the Reader role.
3. `nimbus_record_classification` stores the agent's answers for that attempt. It needs the `nimbus.classify` scope (or, for an approved workload, the `Nimbus.Workload.Classify` app role), the Contributor role and the **Classify failures** switch in **Settings → MCP access**.

When NimBus records an answer:

- it refuses an unknown category or origin, a number outside 0-1, an older `questionSetVersion`, a message on another endpoint, and a message that is no longer Failed or DeadLettered. A refusal stores nothing;
- it composes the guidance with the saved thresholds. The agent never supplies it;
- it stores `Provider: "Agent"`, the declared model (or `unspecified`), the client application, the requester, the fault origin and the rationale. The rationale is bounded to 1,000 characters and scrubbed like error text: values marked sensitive are replaced, credential-shaped text is redacted, and a stack trace is refused. Readers and other agents get it back marked as untrusted;
- a repeated `idempotencyKey` returns the stored result. With `supersede: false` (the default), an existing classification is returned and no revision is written. `supersede: true` writes a new revision and keeps the old ones in history.

A recording counts against the MCP request limit, not the change limit, because it changes no message.

**Question set version 2** keeps the four version 1 questions and adds `fault_origin`: `source_data`, `adapter_code`, `target_system`, `configuration`, `platform` or `unknown`, with a confidence. Version 1 records, made by the built-in provider before 5.0.0, have no origin and show as "not assessed". The `handle-failures` MCP prompt maps guidance and origin to an outcome: `RetryMayHelp` → retry; `ChangeLikelyRequired` with `source_data` → report data quality; `ChangeLikelyRequired` with `adapter_code` → register a bug and draft a fix in the adapter's repository; anything else → hand to a person.

Evidence is bounded and redacted before it leaves the process. Event payload inclusion is opt-in. The full redaction path replaces values marked as sensitive by event metadata, including values that were previously configured for partial reveal or hashing. Free-text credential patterns and embedded exception dumps are removed or omitted. Answers are validated before persistence, and guidance is deterministic:

1. Low category confidence → `Uncertain`.
2. A transient dependency with high retry likelihood → `RetryMayHelp`.
3. High change-required likelihood → `ChangeLikelyRequired`.
4. Otherwise → `Investigate`.

For Cosmos deployments, provision the `failureclassifications` and `intelligencesettings` containers by setting the deployment parameter `integrationIntelligenceEnabled` to `true`. SQL deployments use the message-store connection with extension-owned `dbo.FailureClassifications` and a separate DbUp journal, `dbo.IntelligenceSchemaVersions`. There is no in-memory fallback. Keep the feature disabled while changing storage configuration.

### Activation modes

| Mode | When | What is available |
|---|---|---|
| Off | `Enabled` is false, the configuration can't be bound, or saved Admin settings can't be read | Nothing. Classification storage is not read. |
| Read-only | Enabled with storage configured, but threshold or evidence settings fail validation | Saved classifications: the classification GET and `history` routes, the MCP `nimbus_get_classification` tool and the Failed page's classification groupings. Storage, its schema and the retention reconciler run. Status reports `ReadOnly`. |
| Recordable | Storage configured and evidence and threshold settings valid | Saved results, plus `nimbus_get_failure_evidence` and `nimbus_record_classification`. Status reports `Recordable`. |

Read-only mode means a bad threshold edit never hides results that were already saved.

## Upgrading from 4.x

NimBus 4.x deprecated the built-in TypeSafe provider (Spec 044); 5.0.0 removes it. Stored classifications, including those with `Provider: "TypeSafe"` and question set version 1, stay readable, and the question set, the guidance rules and the Failed page's groupings are unchanged.

| Removed in 5.0.0 | Use instead |
|---|---|
| `TypeSafeFailureIntelligenceProvider`, `IFailureIntelligenceProvider`, `IntelligenceProviderException`, `TypeSafeProviderOptions`, `FailureIntelligenceProviderResult` | An agent records over MCP with `nimbus_record_classification` |
| `FailureClassificationService` (with `AnalyzeAsync`) | `FailureClassificationRecorder.RecordAsync`; reads through `FailureClassificationReader`. `ClassificationServiceException` is unchanged |
| `FailureClassificationOptions.Provider`, `ApiKey`, `BaseUrl`, `Model`, `TimeoutSeconds`, `TypeSafe`, `ValidateProvider()` and the `NoProvider`/`TypeSafeProvider` constants | None. `Validate()` checks the evidence and threshold settings |
| `FailureGuidanceRules.Compose(FailureIntelligenceProviderResult, …)` | `FailureGuidanceRules.Compose(category, categoryConfidence, retryLikelihood, changeRequiredLikelihood, options)` |
| `FailureClassificationQuestionSet.ProviderItems` and `ProviderVersion` | `Items` and `Version` |
| `IntegrationIntelligenceActivation.Ready` | `Recordable` |
| `POST /api/v1/integration-intelligence/failures/{eventId}/{messageId}/classification`, its controller and `IntegrationIntelligenceAuditMiddleware` | None; there is no manual recording |
| `provider`, `model` and `canAnalyze` in the status response, and the `Ready` and `ProviderNotConfigured` status values | `canRecord`, and the status values `Recordable` and `ReadOnly` |
| The `apiKey` and `clearApiKey` members of `PUT /api/admin/failure-intelligence`, the `model` and `timeoutSeconds` settings, and `credentialConfigured`, `credentialSource` and `savedApiKey` in its responses | None. A PUT that still sends one of the removed members is rejected with 400 |
| MCP `nimbus_classify_failure` and `canAnalyzeWithProvider` in `nimbus_get_capabilities` | `nimbus_get_failure_evidence` and `nimbus_record_classification` |
| The `RateLimiting:Intelligence` policy | None; the analyze route it limited is gone |

Deployment settings of the removed provider (`FailureClassification:Provider` other than `None`, `FailureClassification:TypeSafe:*`, and the flat `ApiKey`, `BaseUrl`, `Model` and `TimeoutSeconds`) are ignored: the WebApp starts in Recordable mode and logs one warning naming the keys, never their values. Remove them, and the TypeSafe API key, from the deployment. A settings record saved by 4.x still loads; its model, timeout and sealed key are ignored and dropped by the next save. The `api.typesafe.ai` egress rule is no longer needed.

## Settings

Site Owners can use **Settings → Failure intelligence** even when classification is
disabled. The panel edits activation, data-sharing, history, endpoint allow-list and
guidance settings. No secret is part of these settings.

**Stack traces** (`Data.IncludeStackTrace`, **Settings → Failure intelligence → Agents may see stack
traces**, off by default; Spec 044 Phase 3). A subscriber that sets
`IncludeStackTraceInErrorResponses` stores the failing exception's frames on its error response.
With the switch on, `nimbus_get_failure_evidence` returns them when called with
`includeStackTrace: true`: file paths are cut to the file name, values marked sensitive and
credential-shaped text are replaced, and the trace is withheld when the payload's sensitive values
cannot be read. Saving the setting from a newer WebApp while an older instance is still running is
refused by the older instance, which does not know the field; finish a rolling deploy before turning
it on.

The payload switch (`Data.IncludeEventPayload`) means **agents may see redacted
payloads**: the evidence `nimbus_get_failure_evidence` returns carries the event payload
with every value marked sensitive replaced. It stays off by default. The raw payload keeps
its own gate on `nimbus_get_message` (PiiReader, the `nimbus.payload.read` scope and the
MCP payload switch).

Use **Review changes**, acknowledge payload sharing when enabled, then **Save settings**.
Saves do not modify active requests. Restart **every WebApp instance**
to apply the shared revision. The page distinguishes saved settings from the current
instance's immutable startup settings; it cannot certify other instances restarted.
A conflicting edit returns 409. Reload before saving again, including after a lost response.

The stock WebApp reads settings in a bounded (15 second) bootstrap before MVC
controller discovery, reusing storage registration and credential precedence. No
hosted service or schema creation runs during that read. Saved values
replace deployment defaults, including whole allow-list/redaction arrays. The
bootstrap captures the effective intelligence settings (unrelated configuration
retains its existing provider/reload behavior); settings changes require
restart. Custom hosts bypassing `Program.CreateHostBuilder` must explicitly load
settings before calling `AddNimBusIntegrationIntelligence`.

- SQL: a conditional revision in `dbo.IntelligenceAdminSettings`, created on first
  save under a transaction/application lock. First use needs permission to create
  that table. Reads never create it. It is independent of classification migrations.
- Cosmos: a `failure-classification` item in the separately provisioned
  `intelligencesettings` container, partition key `/id`, no TTL. Runtime never creates
  the container. Missing provisioning makes saves unavailable (503).
- Before settings exist, deployment defaults apply. A failed or invalid settings
  read disables classification until storage is restored and the WebApp restarts;
  ordinary management remains available. Back up this record: deleting it restores
  deployment defaults. Event retention does not delete configuration.
- `GET/PUT /api/v1/admin/failure-intelligence` require site Owner access. GET issues an
  antiforgery token; PUT requires its cookie and `X-NimBus-CSRF` header, a complete
  settings DTO, expected revision, and payload-sharing acknowledgment. Unknown members
  are rejected. The Admin rate-limit policy applies, respecting its kill switch. Changes
  are audited as `UpdateIntelligenceSettings` with sanitized outcomes.

Payload inclusion stays **off by default** for new installations. Enabling it gives
redacted payloads to agents that read evidence, not to old results. A deployment that
already had it on keeps it on after the upgrade. Redaction cannot identify unmarked
business information, and an agent may copy what it reads into issues or reports. The
form's state example is synthetic, not a redaction guarantee for real data.

Reservations and results commit atomically in one per-failure aggregate, fenced by SQL rowversion or Cosmos ETag. Completed revisions are immutable. An expired reservation is an unknown outcome: only `supersede: true` with a new idempotency key records a new revision over it. Reuse the same key after a transport interruption. The aggregate is limited to 1.5 MB; capacity exhaustion returns 503 without discarding history or permitting replay.

Retention follows source-event existence, with no independent Cosmos TTL. While enabled, a background reconciler runs every 60 seconds and removes classifications for deleted events/endpoints. It retries storage outages without affecting successful admin purges. Active reservations have durable source coordinates too; cleanup tombstones fence late completions. Minimal failure-ID tombstones remain to prevent resurrection, without event/session coordinates, classification data or actor. Cleanup is eventual, pauses while disabled, and resumes on activation. GET and completion also check source existence; no classification can be read after its source is gone. Broker-only purges do not delete persisted events or their classifications.
