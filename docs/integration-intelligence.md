# Integration Intelligence

Integration Intelligence is an optional, advisory failure-classification module for the management WebApp. An agent that the customer runs, with its own model, reads the evidence of one persisted failed or dead-lettered message occurrence over MCP, answers a fixed question set and records the answers in NimBus. NimBus stores each result with its provenance and a revision history, and composes the guidance itself with fixed rules. NimBus calls no model. A classification never retries, skips, resubmits, changes message state, or publishes a message.

The built-in TypeSafe provider is **deprecated** and is removed in v5.0.0. See [Deprecated: the built-in provider](#deprecated-the-built-in-provider).

The feature is disabled by default. Enable it when the host has durable classification storage and the required endpoint roles. `Provider: "None"` is the documented value for new deployments:

```json
{
  "NimBus": {
    "IntegrationIntelligence": {
      "Enabled": true,
      "FailureClassification": {
        "Enabled": true,
        "Provider": "None",
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

The API exposes an endpoint capability check at `GET /api/integration-intelligence/status?endpointId=...`, the latest result at `GET /api/integration-intelligence/failures/{eventId}/{messageId}/classification`, and revision history at `/history`. The deprecated provider adds on-demand analysis through `POST` on the classification route; see [Deprecated: the built-in provider](#deprecated-the-built-in-provider). There is no REST route or Web UI form for recording a classification by hand.

Only users with Reader access can view results. Contributor access is required to record or analyze. The target endpoint comes from the stored message occurrence, and access is checked against that endpoint. Every request is audited with `MessageAuditType.FailureClassified`, including access denials, with the caller, the client application and the provider (`Agent` or `TypeSafe`).

### Recording classifications from an agent

Agents classify failures over the operator MCP endpoint (see [MCP server](mcp-server.md)):

1. `nimbus_find_messages` with `includeClassification: true` shows, for each Failed or DeadLettered message, whether its latest attempt has a saved classification.
2. `nimbus_get_failure_evidence` returns the scrubbed evidence for one attempt: its error, dead-letter reason, retry state, recent failures in the same session, the sending endpoint, a redacted payload when an Owner allows it, the current question set and the latest saved result. It needs the Reader role.
3. `nimbus_record_classification` stores the agent's answers for that attempt. It needs the `nimbus.classify` scope (or, for an approved workload, the `Nimbus.Classify` app role), the Contributor role and the **Classify failures** switch in **Settings → MCP access**.

When NimBus records an answer:

- it refuses an unknown category or origin, a number outside 0-1, an older `questionSetVersion`, a message on another endpoint, and a message that is no longer Failed or DeadLettered. A refusal stores nothing;
- it composes the guidance with the saved thresholds. The agent never supplies it;
- it stores `Provider: "Agent"`, the declared model (or `unspecified`), the client application, the requester, the fault origin and the rationale. The rationale is bounded to 1,000 characters and scrubbed like error text: values marked sensitive are replaced, credential-shaped text is redacted, and a stack trace is refused. Readers and other agents get it back marked as untrusted;
- a repeated `idempotencyKey` returns the stored result. With `supersede: false` (the default), an existing classification is returned and no revision is written. `supersede: true` writes a new revision and keeps the old ones in history.

A recording counts against the MCP request limit, not the change limit, because it changes no message and costs no provider call.

**Question set version 2** keeps the four version 1 questions and adds `fault_origin`: `source_data`, `adapter_code`, `target_system`, `configuration`, `platform` or `unknown`, with a confidence. Version 1 records have no origin and show as "not assessed". The `handle-failures` MCP prompt maps guidance and origin to an outcome: `RetryMayHelp` → retry; `ChangeLikelyRequired` with `source_data` → report data quality; `ChangeLikelyRequired` with `adapter_code` → register a bug and draft a fix in the adapter's repository; anything else → hand to a person.

Evidence is bounded and redacted before it leaves the process. Event payload inclusion is opt-in. The full redaction path replaces values marked as sensitive by event metadata, including values that were previously configured for partial reveal or hashing. Free-text credential patterns and embedded exception dumps are removed or omitted. Answers are validated before persistence, and guidance is deterministic:

1. Low category confidence → `Uncertain`.
2. A transient dependency with high retry likelihood → `RetryMayHelp`.
3. High change-required likelihood → `ChangeLikelyRequired`.
4. Otherwise → `Investigate`.

For Cosmos deployments, provision the `failureclassifications` and `intelligencesettings` containers by setting the deployment parameter `integrationIntelligenceEnabled` to `true`. SQL deployments use the message-store connection with extension-owned `dbo.FailureClassifications` and a separate DbUp journal, `dbo.IntelligenceSchemaVersions`. There is no in-memory fallback. Keep the feature disabled while changing provider credentials or storage configuration.

### Activation modes

| Mode | When | What is available |
|---|---|---|
| Off | `Enabled` is false, the configuration can't be bound, or saved Admin settings can't be read | Nothing. Classification storage is not read. |
| Read-only | Enabled with storage configured, but threshold or evidence settings fail validation | Saved classifications: the classification GET and `history` routes, the MCP `nimbus_get_classification` tool and Failed Message Insights enrichment. Storage, its schema and the retention reconciler run. Status reports `ProviderNotConfigured`. |
| Recordable | Storage configured and evidence and threshold settings valid | Saved results, plus `nimbus_get_failure_evidence` and `nimbus_record_classification`. Status reports `Recordable`. No provider is needed. |
| Ready | Recordable, and the deprecated provider's settings are valid (`Provider: "TypeSafe"` with a key) | Everything, including provider analysis, until v5.0.0. |

With `Provider: "None"` a missing key is not a configuration warning. With the default `Provider: "TypeSafe"` and no key, the deployment is Recordable and logs a warning that provider analysis is unavailable. Read-only mode means a key rotation or a cleared key never hides results that were already saved.

## Deprecated: the built-in provider

Spec 044 moves the model out of NimBus. In this 4.x minor the following are deprecated, and they are removed in v5.0.0. They keep working until then:

| Deprecated | Replacement |
|---|---|
| `TypeSafeFailureIntelligenceProvider`, `IFailureIntelligenceProvider`, `IntelligenceProviderException`, `TypeSafeProviderOptions` | An agent records over MCP with `nimbus_record_classification` |
| `FailureClassificationService.AnalyzeAsync` | `FailureClassificationRecorder.RecordAsync` |
| `FailureClassificationOptions.ApiKey`, `BaseUrl`, `Model`, `TimeoutSeconds` and `TypeSafe` | None; set `Provider` to `None` |
| `POST /api/integration-intelligence/failures/{eventId}/{messageId}/classification` and its controller | None; there is no manual recording |
| The `apiKey` and `clearApiKey` members of `PUT /api/admin/failure-intelligence`, and the sealed key in the saved settings record | None |
| MCP `nimbus_classify_failure` | `nimbus_get_failure_evidence` and `nimbus_record_classification` |
| `FailureGuidanceRules.Compose(FailureIntelligenceProviderResult, …)` | `FailureGuidanceRules.Compose(category, categoryConfidence, retryLikelihood, changeRequiredLikelihood, options)` |

The provider keeps asking question set version 1. Results stored with `Provider: "TypeSafe"` stay readable after the removal, and the question set, the guidance rules and Insights are unaffected.

## Settings

Site Owners can use **Settings → Failure intelligence** even when classification is
disabled. The panel edits activation, data-sharing, history, endpoint allow-list and
guidance settings, and, for the deprecated provider, its model, timeout and API key,
which it marks deprecated. The provider base URL remains deployment-managed. No secret
is ever returned by the settings API.

**Stack traces** (`Data.IncludeStackTrace`, **Settings → Failure intelligence → Agents may see stack
traces**, off by default; Spec 044 Phase 3). A subscriber that sets
`IncludeStackTraceInErrorResponses` stores the failing exception's frames on its error response.
With the switch on, `nimbus_get_failure_evidence` returns them when called with
`includeStackTrace: true`: file paths are cut to the file name, values marked sensitive and
credential-shaped text are replaced, and the trace is withheld when the payload's sensitive values
cannot be read. The deprecated provider never receives a trace. Saving the setting from a newer
WebApp while an older instance is still running is refused by the older instance, which does not
know the field; finish a rolling deploy before turning it on.

The payload switch (`Data.IncludeEventPayload`) means **agents may see redacted
payloads**: the evidence `nimbus_get_failure_evidence` returns, and the evidence the
deprecated provider receives, carries the event payload with every value marked
sensitive replaced. It stays off by default. The raw payload keeps its own gate on
`nimbus_get_message` (PiiReader, the `nimbus.payload.read` scope and the MCP payload
switch).

### Provider API key (deprecated)

The `jev-*` models of the deprecated provider need a TypeSafe API key. Supply it either through deployment
configuration (`NimBus__IntegrationIntelligence__FailureClassification__TypeSafe__ApiKey`,
a Key Vault reference or user secrets) or from the Settings panel:

- **API key** is a write-only password field. Leave it blank to keep the saved key;
  paste a new one to replace it. The value is trimmed and must be a single token of
  1–512 characters without whitespace. The page never displays a saved key.
- **Remove the saved key** deletes it from the shared record. The deployment key, if
  any, applies again after restart.
- The header shows where the running instance's key came from (`saved settings`,
  `deployment` or `not configured`), and the field shows whether the shared record
  holds a key. A saved key overrides the deployment key after restart.
- The key is sealed with ASP.NET Core Data Protection (application name
  `NimBus.WebApp`) before it is written to `dbo.IntelligenceAdminSettings` or the
  `intelligencesettings` Cosmos item, and is unsealed only during the startup
  bootstrap. Anyone who can read the settings store still cannot read the key. On
  Azure App Service the key ring lives in `%HOME%\ASP.NET\DataProtection-Keys` and is
  shared by every instance; locally it is the user's `%LOCALAPPDATA%` key ring, so a
  key saved on one developer machine is unreadable on another.
- If the key ring changes (new hosting plan, cleared key folder, a second
  environment reading the same database), the page reports the saved key as
  **unreadable**: the deployment key applies, other saved settings still load, and
  classification is not failed closed. Enter the key again or remove it.
- Every save is audited as `UpdateIntelligenceSettings` with
  `"apiKey": "replaced" | "cleared" | "unchanged"`. The value itself is never audited,
  logged, returned or echoed in error responses; a rejected key returns `InvalidApiKey`.
- Upgrading to a WebApp that sets the Data Protection application name invalidates
  existing sign-in cookies and antiforgery tokens once; users sign in again.

Use **Review changes**, acknowledge payload sharing when enabled, then **Save settings**.
Saves do not call TypeSafe or modify active requests. Restart **every WebApp instance**
to apply the shared revision. The page distinguishes saved settings from the current
instance's immutable startup settings; it cannot certify other instances restarted.
A conflicting edit returns 409. Reload before saving again, including after a lost response.

The stock WebApp reads settings in a bounded (15 second) bootstrap before MVC
controller discovery, reusing storage registration and credential precedence. No
hosted service or schema creation runs during that read. Saved non-secret values
replace deployment defaults, including whole allow-list/redaction arrays. The
bootstrap captures the effective intelligence settings (unrelated configuration
retains its existing provider/reload behavior); settings and credential changes require
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
- `GET/PUT /api/admin/failure-intelligence` require site Owner access. GET issues an
  antiforgery token; PUT requires its cookie and `X-NimBus-CSRF` header, a complete
  settings DTO, expected revision, and payload-sharing acknowledgment. PUT may also
  carry `apiKey` (replace) or `clearApiKey: true` (remove), never both; omitting
  both keeps the sealed key. Responses add `credentialSource` (`saved`, `deployment`,
  `none`) for the running instance and `savedApiKey` (`none`, `configured`,
  `unreadable`) for the shared record. The Admin rate-limit policy applies,
  respecting its kill switch. Changes are audited as `UpdateIntelligenceSettings`
  with sanitized outcomes, not credentials.

Payload inclusion stays **off by default** for new installations. Enabling it gives
redacted payloads to agents that read evidence, and to deliberately requested provider
analyses, not old results. A deployment that already had it on keeps it on after the
upgrade. Redaction cannot identify unmarked business information, and an agent may
copy what it reads into issues or reports. The form's state example is synthetic, not
a redaction guarantee for real data.

Reservations and results commit atomically in one per-failure aggregate, fenced by SQL rowversion or Cosmos ETag. Completed revisions are immutable. Expired reservations are unknown outcomes: only explicit `force: true` with a new UUID can supersede them, potentially incurring another charge. Reuse the same UUID after a transport interruption. The aggregate is limited to 1.5 MB; capacity exhaustion returns 503 without discarding history or permitting replay.

Retention follows source-event existence, with no independent Cosmos TTL. While enabled, a background reconciler runs every 60 seconds and removes classifications for deleted events/endpoints. It retries storage outages without affecting successful admin purges. Active reservations have durable source coordinates too; cleanup tombstones fence late completions. Minimal failure-ID tombstones remain to prevent resurrection, without event/session coordinates, classification data or actor. Cleanup is eventual, pauses while disabled, and resumes on activation. GET and completion also check source existence; no classification can be read after its source is gone. Broker-only purges do not delete persisted events or their classifications.
