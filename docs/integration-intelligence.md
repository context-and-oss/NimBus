# Integration Intelligence

Integration Intelligence is an optional, advisory failure-classification module for the management WebApp. It analyzes one persisted failed or dead-lettered message occurrence on operator request and stores the result with a revision history. It never retries, skips, resubmits, changes message state, or publishes a message.

The feature is disabled by default. Enable it only when the host has configured a provider, durable classification storage, and the required endpoint roles:

```json
{
  "NimBus": {
    "IntegrationIntelligence": {
      "Enabled": true,
      "FailureClassification": {
        "Enabled": true,
        "Provider": "TypeSafe",
        "TypeSafe": {
          "ApiKey": "${TYPESAFE_API_KEY}",
          "Model": "jev-1.13.0",
          "BaseUrl": "https://api.typesafe.ai",
          "TimeoutSeconds": 20
        },
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

The API exposes an endpoint capability check at `GET /api/integration-intelligence/status?endpointId=...`, the latest result at `GET /api/integration-intelligence/failures/{eventId}/{messageId}/classification`, revision history at `/history`, and on-demand analysis through `POST` on the classification route. Every POST requires a UUID `Idempotency-Key`; repeated requests return the same completed result, while `force: true` creates a new revision. The WebApp applies its intelligence rate-limit policy to the POST route.

Only users with Reader access can view results. Contributor access is required to start analysis. The target endpoint comes from the stored message occurrence, and access is checked against that endpoint. Every request is audited with `MessageAuditType.FailureClassified`, including access denials and provider errors.

Evidence is bounded and redacted before it leaves the process. Event payload inclusion is opt-in. The full redaction path replaces values marked as sensitive by event metadata, including values that were previously configured for partial reveal or hashing. Free-text credential patterns and embedded exception dumps are removed or omitted. Provider responses are validated before persistence, and guidance is deterministic:

1. Low category confidence → `Uncertain`.
2. A transient dependency with high retry likelihood → `RetryMayHelp`.
3. High change-required likelihood → `ChangeLikelyRequired`.
4. Otherwise → `Investigate`.

For Cosmos deployments, provision the `failureclassifications` and `intelligencesettings` containers by setting the deployment parameter `integrationIntelligenceEnabled` to `true`. SQL deployments use the message-store connection with extension-owned `dbo.FailureClassifications` and a separate DbUp journal, `dbo.IntelligenceSchemaVersions`. There is no in-memory fallback. Keep the feature disabled while changing provider credentials or storage configuration.

### Activation modes

| Mode | When | What is available |
|---|---|---|
| Off | `Enabled` is false, the configuration can't be bound, or saved Admin settings can't be read | Nothing. Classification storage is not read. |
| Read-only | Enabled with storage configured, but provider, threshold or evidence settings fail validation (for example no API key) | Saved classifications: the classification GET and `history` routes, the MCP `nimbus_get_classification` tool and Failed Message Insights enrichment. Storage, its schema and the retention reconciler run. Status reports `ProviderNotConfigured`. No new analysis: POST is not exposed and `nimbus_classify_failure` reports that no provider is configured. |
| Ready | Enabled and every setting is valid | Everything, including analysis. |

Read-only mode means a key rotation or a cleared key never hides results that were already saved.

## Settings

Site Owners can use **Settings → Failure intelligence** even when classification is
disabled. The panel edits activation, model, data-sharing, history, endpoint
allow-list, timeout and guidance settings, and can save the TypeSafe API key. The
provider base URL remains deployment-managed. No secret is ever returned by the
settings API.

### Provider API key

The `jev-*` models need a TypeSafe API key. Supply it either through deployment
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

Payload inclusion stays **off by default** for new installations. Enabling it sends
redacted payload evidence only on deliberately requested analyses, not old results.
Redaction cannot identify unmarked business information. The form's state example
is synthetic, not a redaction guarantee for real data.

Reservations and results commit atomically in one per-failure aggregate, fenced by SQL rowversion or Cosmos ETag. Completed revisions are immutable. Expired reservations are unknown outcomes: only explicit `force: true` with a new UUID can supersede them, potentially incurring another charge. Reuse the same UUID after a transport interruption. The aggregate is limited to 1.5 MB; capacity exhaustion returns 503 without discarding history or permitting replay.

Retention follows source-event existence, with no independent Cosmos TTL. While enabled, a background reconciler runs every 60 seconds and removes classifications for deleted events/endpoints. It retries storage outages without affecting successful admin purges. Active reservations have durable source coordinates too; cleanup tombstones fence late completions. Minimal failure-ID tombstones remain to prevent resurrection, without event/session coordinates, classification data or actor. Cleanup is eventual, pauses while disabled, and resumes on activation. GET and completion also check source existence; no classification can be read after its source is gone. Broker-only purges do not delete persisted events or their classifications.
