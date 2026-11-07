# Spec 033 — Integration Intelligence: Failure Classification

Status: proposed (2026-09-19), review corrections 2026-09-20, **packaging pivot 2026-09-20**. MVP scope.
Feature area: **NimBus Integration Intelligence**, first module **Failure Classification**.
Packaging: optional open-source extension **in this repository**
(`src/NimBus.Extensions.IntegrationIntelligence`, MIT, in `NimBus.sln`), off by default.
AI provider for MVP: TypeSafe **Jev** (System One API, https://docs.typesafe.ai/). Jev is a paid
third-party service; a deployment that enables the feature supplies its own API key. NimBus ships
no licence check and takes no revenue from it.
Baseline: NimBus 3.7.1 (`master`), WebApp access model from spec 026, audit contract from spec 008.

> **Amended by [Spec 043 §9](../043-intelligent-failed-message-insights/spec.md) (2026-11-07).**
> `ProviderNotConfigured` with readable storage is now a read-only mode: the classification GET
> and `history` routes, the MCP read tool and the WebApp card serve saved results, and only POST
> analyze is pruned. A configuration binding failure still exposes the status route only. The
> statements below that `ProviderNotConfigured` returns `404` for classification and history
> describe the behavior before that change.

### 2026-09-21 addendum — administrator-managed settings

The stock WebApp adds an always-discoverable, site-Owner-only Admin settings surface
independent of optional classification route pruning. Non-secret settings persist in
the selected SQL/Cosmos backend with conditional revision writes; deployment continues
to own provider keys and base URLs. Saved settings override deployment defaults only
after restart. All instances must restart to adopt a shared revision. Active request
options are never hot-mutated by an Admin save.

The bounded startup settings read occurs before MVC discovery. Missing settings use
deployment defaults; unavailable/invalid settings fail classification closed without
failing ordinary WebApp startup. Reads do not create schema, instantiate the provider,
or make AI calls. This deliberately adds one shared-settings read even when analysis
is disabled so a previously saved enable/disable decision can be discovered.

SQL owns `dbo.IntelligenceAdminSettings` (created on first Admin save); Cosmos owns
`intelligencesettings` with `/id`, provisioned with the existing opt-in deployment
parameter. Settings are not subject to event retention. Protect/back up this record;
deletion restores deployment defaults. Admin saves require CSRF protection, full
server validation, an expected revision and explicit payload-sharing consent.
Payload export remains default-off for new installations. See
[operator documentation](../../integration-intelligence.md#admin-settings) and the
[implementation plan](../../plan/2026-09-21-intelligence-admin-settings.md).

### 2026-09-22 addendum — administrator-saved provider key

The Admin page may also save the TypeSafe API key (issue #134). The key is sealed with
ASP.NET Core Data Protection (application name `NimBus.WebApp`, purpose
`NimBus.IntegrationIntelligence.AdminSettings.ProviderApiKey.v1`) and stored as
`ProtectedApiKey` in the same revision-fenced settings record; readers of the store
cannot recover it. The bootstrap registers Data Protection with the same application
name and unseals the key before `AddNimBusIntegrationIntelligence`; a saved key
overrides the deployment key, an unreadable one is ignored in favour of the deployment
key and surfaced as `unreadable` so a rotated key ring never fails classification
closed. `PUT` accepts write-only `apiKey` / `clearApiKey`; no response, audit row or
log carries the value. The base URL stays deployment-managed. Setting the application
name invalidates existing cookies and antiforgery tokens once at upgrade. See the
[implementation plan](../../plan/2026-09-22-intelligence-admin-api-key.md).

Related: ADR-004 (pipeline behaviors), `docs/extensions.md` (extension packages),
`docs/error-handling.md` (the deterministic retry/DLQ rules this feature must not touch).

This document is the refined version of the original product draft. §23 lists what changed from
that draft and why; §24 lists the decisions still open. §23.1 records the 2026-09-20 pivot from a
commercial, out-of-repository package to an in-repository optional extension, and what that
removed.

---

## 1. Purpose

An operator investigating a Failed or DeadLettered message gets, on request, a structured
assessment of *what kind* of failure it is and whether retrying the unchanged message is likely
to help. The assessment is produced by Jev from evidence NimBus already stores, and is shown as
an **advisory card** on the event-details page.

The feature never changes settlement, session ordering, retry policy, dead-lettering, failure
disposition, resubmit, skip, or any other deterministic NimBus rule. §19 states this as the
architectural invariant and the test that protects it.

## 2. Extension boundary

```
NimBus platform (existing projects)           NimBus.Extensions.IntegrationIntelligence (new project)
───────────────────────────────────           ───────────────────────────────────────────────────────
Messaging, ordering, retry, recovery,         Failure Classification
message history, management WebApp              ├── options + contained validation
  │                                             ├── input builder + redaction
  ├── WebApp ── ProjectReference, opt-in ──►    ├── IFailureIntelligenceProvider
  │     by configuration (Identity precedent)   │     └── TypeSafeFailureIntelligenceProvider
  ├── SPA "Integration Intelligence" card       ├── deterministic guidance rules
  │     (renders only when the status           ├── IFailureClassificationStore (SQL, Cosmos)
  │      endpoint answers)                      └── /api/integration-intelligence/* controllers
  ├── MessageAuditType.FailureClassified
  └── IEventJsonRedactor (NimBus.Core)
```

Same repository, same licence, same release train. The boundary is a **dependency and activation
boundary**, not a commercial one:

- Only `NimBus.WebApp` references the extension, the way it references
  `NimBus.Extensions.Identity` today. Core, SDK, Resolver, Manager, the message stores and every
  adapter never reference it. Among NimBus projects the extension references only `NimBus.Core`
  and `NimBus.MessageStore.Abstractions` (§19 pins this; §5 lists its third-party packages).
- **Off by default.** With `NimBus:IntegrationIntelligence:Enabled = false` (the default) no
  extension service is registered, its controllers are removed from MVC, no schema is created, no
  outbound call is possible, and the WebApp behaves exactly as it does today. The existing test
  suite, run with the feature disabled, is the regression gate for that (§20).
- Anything reusable by the platform regardless of this feature lives in the platform projects
  (§4: audit type, full redaction, SPA card). Everything provider-specific, plus classification
  execution and storage, lives in the extension project.
- Optional also means optional to *depend on*: the extension ships as its own NuGet package
  (`Akaule.NimBus.Extensions.IntegrationIntelligence`, public feed, with the other packages) so a
  custom host can add or omit it. The stock WebApp includes it and leaves it disabled.

## 3. What NimBus stores today (the evidence)

The classification is built from data the message store already holds. No new capture in the
processing path.

| Evidence | Where it lives today |
|---|---|
| Failure occurrence | A `MessageEntity` row with `MessageType = ErrorResponse` (or the dead-letter projection); its `MessageId` is unique per attempt |
| Exception type / message | `MessageContent.ErrorContent.ErrorType`, `.ErrorText` |
| Stack trace, source assembly | `ErrorContent.ExceptionStackTrace`, `.ExceptionSource` |
| Dead-letter cause | `MessageEntity.DeadLetterReason`, `.DeadLetterErrorDescription` |
| Attempt counters | `MessageEntity.RetryCount`, `.RetryLimit` |
| Previous attempts | `IMessageTrackingStore.GetEventHistory(eventId)` — one row per message on the event |
| Current status | `ResolutionStatus` on the event (`Failed`, `DeadLettered`, …) |
| Payload | `MessageContent.EventContent`, masked by `IEventJsonMasker` for non-PiiReaders |

`ResolutionStatus` is `Pending, Deferred, Failed, TooManyRequests, DeadLettered, Unsupported,
Published, Completed, Skipped`. Only `Failed` and `DeadLettered` are eligible (§6).

## 4. Platform-side changes (outside the extension project)

The WebApp is zip-deployed from a published build (`nb deploy apps`); operators do not compose
their own host project. Because the extension now lives in this repository, the stock WebApp
simply references it and ships it in that build, exactly as it ships
`NimBus.Extensions.Identity`. No dynamic assembly loading, no extension-host contract and no
deploy-tool change is needed (§23.1).

### 4.1 WebApp wiring

- `NimBus.WebApp.csproj` gains a `ProjectReference` to
  `..\NimBus.Extensions.IntegrationIntelligence\NimBus.Extensions.IntegrationIntelligence.csproj`.
- `Startup.ConfigureServices` calls `services.AddNimBusIntegrationIntelligence(Configuration,
  storageProvider)` after `AddStorage`. The method reads
  `NimBus:IntegrationIntelligence:Enabled`; when false it registers only the
  controllers-disabled feature provider and returns.
- Controllers live in the extension assembly and are removed from MVC when the feature is
  disabled, following `IdentityControllersDisabledFeatureProvider` in
  `src/NimBus.WebApp/IdentityControllersDisabledFeatureProvider.cs`. A disabled feature therefore answers `404` on every
  `/api/integration-intelligence/*` route, which the SPA reads as "hide the card" (precedent:
  `sidebar-user-footer.tsx` treats a 404 on `/api/auth/me` as "not wired in").
- MVC is already configured by `AddAuthenticationStack`. The extension attaches its feature
  provider through another `AddControllers().ConfigureApplicationPartManager(...)` call after
  `AddStorage`, independently of the authentication branches. Test controller discovery and
  suppression under LocalDev, Entra-only, Identity-only and dual authentication. In
  `ProviderNotConfigured`, suppress classification/history controllers but retain status.
- The extension is a WebApp feature, so it exposes a plain `IServiceCollection` method rather
  than an `INimBusBuilder` extension: it adds no pipeline behavior and no lifecycle observer,
  and §19 forbids it from doing so.

### 4.2 SPA card

The card component and its small fetch client live in the WebApp SPA
(`ClientApp/src/components/event-details/intelligence-card.tsx`). It probes
`GET /api/integration-intelligence/status?endpointId={endpointId}` for the endpoint being viewed;
a 403, 404 or a non-`Ready` status hides the card. Re-probe on endpoint navigation and discard stale
responses from the previous endpoint. The extension's routes are **not** added to `api-spec.yaml`:
NSwag generates a server interface the WebApp project itself must implement, which would pull the
controllers out of the extension assembly and defeat the disabled-means-absent rule. The Identity
extension's `/account/*` routes are outside `api-spec.yaml` for the same reason. The client is
hand-written against the contract in §14, and the status response carries `contractVersion` so a
custom host pairing a newer SPA with an older extension package hides the card instead of
rendering garbage.

### 4.3 Audit type

Append `FailureClassified` **last** to `MessageAuditType` (Cosmos persists numeric values), and
add `failureClassified` to all three `auditType` enum lists in `api-spec.yaml`;
`AuditTypeContractTests` pins the round-trip. The value is generic ("an analysis ran on this
event"); everything provider-specific goes in the audit `Data` field (§13).

### 4.4 Full payload redaction

Add a separate capability in `NimBus.Core.Messages.PII`. It belongs in Core, not the extension,
because it is a property of the PII contract and any future export path needs the same guarantee:

```csharp
public interface IEventJsonRedactor
{
    string Redact(string eventTypeId, string eventJson);
}
```

`EventJsonMasker` implements this alongside `IEventJsonMasker`, reusing its contract traversal,
but `Redact` replaces every sensitive leaf with `***` regardless of its annotation's `Redact`,
`PartialReveal` or `Hash` mode. It preserves the existing unknown-type and invalid-JSON
fail-closed markers. Existing `Mask` behavior and third-party `IEventJsonMasker` implementations
remain compatible; no new member is required on that interface. Register the new capability
explicitly. The extension's input builder must omit the payload if this capability is unavailable;
falling back to `Mask` or the raw JSON is forbidden.

## 5. Extension project

- Project/namespace: `src/NimBus.Extensions.IntegrationIntelligence` (the
  `NimBus.Extensions.{Name}` convention in `docs/extensions.md`), added to `src/NimBus.sln`.
  MIT, like the rest of the repository.
- NuGet id `Akaule.NimBus.Extensions.IntegrationIntelligence`, packed and published with the
  other `Akaule.NimBus.*` packages on the normal release train and versioned with the platform.
- Tests: `tests/NimBus.Extensions.IntegrationIntelligence.Tests` (§20), run by the normal
  `dotnet test`. No test in the default run contacts TypeSafe; the provider is exercised against
  mocked HTTP only.
- References: `NimBus.Core`, `NimBus.MessageStore.Abstractions`,
  `FrameworkReference Microsoft.AspNetCore.App`, `Microsoft.Extensions.Http.Resilience`,
  `Microsoft.Data.SqlClient` + `dbup-sqlserver`, `Microsoft.Azure.Cosmos`. It does **not**
  reference `NimBus.MessageStore.SqlServer` or `.CosmosDb`. WebApp supplies resolved SQL
  connection settings from registered SQL store options and the current Cosmos store's fixed
  `MessageDatabase` name through an
  extension-owned host contract. The extension opens its own SQL connections with those settings
  and reuses the existing DI `CosmosClient`, which remains host-owned. It does not duplicate
  connection-key precedence, credential selection or client configuration. Resolve only the
  selected backend when execution is enabled; verify adapter wiring in tests. Store projects stay
  unaware of the extension (§24.2 covers possible future storage satellites).
- Contents: `AddNimBusIntegrationIntelligence`, options and contained validation, input builder,
  redactor, provider abstraction + TypeSafe provider, question set, guidance rules, classification
  store (SQL Server, Cosmos DB), controllers, telemetry. The in-memory implementation and shared
  conformance suite live in `tests/NimBus.Extensions.IntegrationIntelligence.Tests`, not the
  shipped extension package or `NimBus.Testing`.
- Host authorization and audit integration also uses narrow extension-owned contracts implemented
  by WebApp adapters. These contracts carry no `HttpContext`; the WebApp adapter resolves it via
  `IHttpContextAccessor` and delegates to existing authorization/audit services. The extension
  never references WebApp or reimplements its ACL rules. Storage credentials are confined to the
  separate storage contract and never included in API responses or audit data.
- MVP hosts in the **WebApp only**. The Resolver never references the project; there is no
  automatic classification (§7), so nothing needs to run in the processing path.
- Documentation shipped with the feature: a new `docs/integration-intelligence.md`, an entry in
  `docs/extensions.md` and `docs/features.md`, and the project line in `CLAUDE.md`'s layout list.
  The doc must state plainly that enabling the feature sends failure evidence to a third-party
  service under the operator's own TypeSafe account (§9).

## 6. Eligibility and identity

A classification targets one **failure occurrence**, not an event. The same event fails
differently after a resubmit, and the operator wants the assessment of *this* attempt.

- Identity: `FailureMessageId` = the `MessageId` of the `ErrorResponse` row (or the dead-letter
  projection row) being viewed. Load it with `GetMessage(eventId, messageId)`.
- Derive `EndpointId` from that stored row, authorize against it, and load current event status
  with `GetEvent(endpointId, eventId)`. Never use a caller-supplied endpoint to authorize a row
  belonging to another endpoint. Loading coordinates is permitted before authorization; no
  evidence, cached classification or history is returned or sent to the provider before it.
- Eligible when the message is an error row **and** the event's current `ResolutionStatus` is
  `Failed` or `DeadLettered`.
- Rejected with `409 Conflict`: `Pending`, `Deferred`, `TooManyRequests`, `Unsupported`,
  `Published`, `Completed`, `Skipped`, and any non-error row on an eligible event.
- Retained for querying and display: `EventId`, `EndpointId`, `SessionId`, `EventTypeId`.

## 7. Execution model: on demand only

Classification starts when an operator presses **Analyze** on the event-details page. Nothing
classifies automatically. Reasons: zero latency in the processing path; Jev availability cannot
touch Resolver throughput; data leaves the environment only when someone decides it should;
provider cost accrues only for failures someone investigates; no durable work queue is needed;
and real-world accuracy is established before any automatic mode is considered.

`ClassificationMode = OnDemand` is the only value in MVP. `Automatic` is reserved.

## 8. Input assembled by the extension

```csharp
public sealed record FailureClassificationInput
{
    public required string MessageId { get; init; }        // FailureMessageId
    public required string EventId { get; init; }
    public required string EventTypeId { get; init; }
    public required string EndpointId { get; init; }
    public string? SessionId { get; init; }
    public required string ResolutionStatus { get; init; } // "Failed" | "DeadLettered"
    public int? RetryCount { get; init; }
    public int? RetryLimit { get; init; }
    public required FailureExceptionInfo Exception { get; init; }
    public string? DeadLetterReason { get; init; }
    public IReadOnlyList<FailureHistoryItem> RecentHistory { get; init; } = [];
    public string? EventPayloadJson { get; init; }         // masked JSON, null unless opted in
}

public sealed record FailureExceptionInfo(string? Type, string? Message, string? Source);

public sealed record FailureHistoryItem(
    int Attempt, string MessageType, string? ErrorType, string? ErrorMessage, DateTime EnqueuedTimeUtc);
```

Mapping: `Exception.Type ← ErrorContent.ErrorType`, `Exception.Message ← ErrorContent.ErrorText`
redacted before truncation to `MaximumErrorTextLength`, `Exception.Source ← ErrorContent.ExceptionSource`
(assembly name only). `RecentHistory` is built from `GetEventHistory(eventId)` as follows:

1. Filter by the target row's `EndpointId` (ordinal, case-insensitive) and `SessionId`
   (ordinal), then retain only failure rows (`ErrorResponse` or a dead-letter occurrence).
2. Exclude the target row and any row whose `EnqueuedTimeUtc` is greater than or equal to the
   target's timestamp. Equal timestamps have no proven causal order and are conservatively omitted.
3. Order by `EnqueuedTimeUtc`, then `MessageId` ordinal for stable selection; take the last
   `MaximumHistoryItems`. Apply this limit only after filtering. `Attempt` is a 1-based ordinal
   among the filtered earlier failures, not a transport retry counter. When history is disabled,
   send an empty list.
4. Redact before truncating history error text to 500 characters (§9).

`DeadLetterErrorDescription` is deliberately absent from the outbound input: the existing
`ResponseService.FormatDeadLetterDescription` uses `exception.ToString()`, which includes stack
traces and inner exceptions. Do not parse or fall back to that string, even when `ErrorContent`
is absent. Send the separately stored, scrubbed `DeadLetterReason` and safe exception fields only;
null exception fields are valid evidence for a dead-letter occurrence without `ErrorContent`.

The provider receives the input as a **JSON object state**, not a prompt. TypeSafe's guidance
is that descriptive top-level keys help the model relate parts of the state, so the state is:

```json
{
  "failure": {
    "eventType": "CustomerUpdated", "endpoint": "DynamicsToERP", "status": "Failed",
    "retryCount": 3, "retryLimit": 5,
    "exception": { "type": "CustomerNotFoundException", "message": "Customer 4711 does not exist", "source": "Erp.Adapter" },
    "deadLetterReason": null
  },
  "recentHistory": [
    { "attempt": 1, "outcome": "ErrorResponse", "errorType": "CustomerNotFoundException", "errorMessage": "Customer 4711 does not exist" },
    { "attempt": 2, "outcome": "ErrorResponse", "errorType": "CustomerNotFoundException", "errorMessage": "Customer 4711 does not exist" }
  ],
  "eventPayload": null
}
```

Jev's per-request budget is 64k tokens, 32k for state plus the longest question. The builder
enforces `MaximumStateCharacters` (default 24 000) after truncation rules, dropping history
first and payload second.

## 9. Data minimisation and redaction

Defaults are conservative because the state leaves the customer's environment:

```
IncludeEventPayload          = false
IncludeStackTrace            = false   (not configurable in MVP; always excluded)
IncludeRecentFailureHistory  = true
MaximumHistoryItems          = 5
MaximumErrorTextLength       = 4000
MaximumStateCharacters       = 24000
```

Two redaction layers, both applied before the provider call:

1. **PII redaction.** When `IncludeEventPayload = true`, pass the target failure's payload through
  `IEventJsonRedactor.Redact` (§4.4). Every `[Sensitive]` leaf is fully redacted, including fields
   annotated `PartialReveal` or `Hash`; the caller's PiiReader capability never changes this.
   Unknown types, invalid JSON or a missing redactor cause payload omission, never raw fallback.
   Set `EventPayloadIncluded` only when a redacted payload survives the final size limit.
   Independently of payload export, use `IEventJsonMasker.TryCollectSensitiveValues` on each
   source row's payload to scrub quoted sensitive values from its error text and dead-letter
   reason. If the payload is missing or cannot be analyzed, withhold those free-text fields.
   This local inspection does not opt the payload into export.
2. **Secret-key scrubbing.** `IIntelligenceDataRedactor` walks every string-valued field of the
   input (payload JSON, error text, history error text, dead-letter reason) and replaces the
   *value* of any object key matching, case-insensitively, one of:
   `password, secret, clientSecret, token, accessToken, refreshToken, authorization, apiKey,
   connectionString, cookie, bearer, sharedAccessKey`. Free-text error messages are scanned for
   `Authorization: Bearer …`, `SharedAccessKey=…`, `Password=…`, `AccountKey=…` patterns and
   the matched value is replaced with `[redacted]`. The list is extensible via configuration.

Apply both layers before length truncation so truncation cannot cut a secret pattern or sensitive
value before it is recognized. `ErrorContent.ExceptionStackTrace` and raw dead-letter descriptions
are never copied into the input. If an error-text field contains an embedded exception dump or
stack frames, omit the field rather than trying to remove only its frames; test this path too.

NimBus auth tokens, connection strings, Service Bus credentials, the TypeSafe API key, Entra
tokens and cookies are never read from host configuration into evidence.
Scrubbing known keys/patterns is defense in depth for evidence text, not a guarantee that arbitrary
unlabelled secrets can be recognized. Stack traces are excluded in MVP.

## 10. Provider abstraction and TypeSafe implementation

```csharp
public interface IFailureIntelligenceProvider
{
    string Name { get; }                       // "TypeSafe"
    Task<FailureIntelligenceProviderResult> ClassifyAsync(
        FailureClassificationInput input, CancellationToken cancellationToken = default);
}

public sealed record FailureIntelligenceProviderResult(
    string Model,                              // versioned id echoed by the provider
    string Category,
    double CategoryConfidence,
    IReadOnlyDictionary<string, double> CategoryProbabilities,
    double RetryLikelihood,
    double ChangeRequiredLikelihood,
    double ExternalDependencyLikelihood,
    int? InputTokens, int? OutputTokens);
```

`TypeSafeFailureIntelligenceProvider` (verified against https://docs.typesafe.ai/api.md on
2026-09-19):

- `POST https://api.typesafe.ai/v1/systemone`, header `Authorization: Bearer <ApiKey>`.
- Request body: `{ "model": "...", "state": <object>, "questions": { "<id>": { "type", "instructions", "criteria" } } }`.
  All four questions (§11) go in **one** request; Jev evaluates them in parallel.
- Response: `{ "model", "answers": { "<id>": … }, "usage": { "input_tokens", "output_tokens" } }`.
  A `choice` answer carries `choice`, `probabilities` (sums to 1.0) and `confidence` (0–1). A
  `noul` answer carries only `noul` (0–1, probability of "yes"); Noul has no confidence field.
- Errors: `401` bad key, `422` validation, `429` rate limited, `529` overloaded. Only 429 and
  529 are retried (bounded, exponential backoff, at most 2 retries); 4xx is never retried.
- Model: configuration default `jev-1.13.0`, not `jev-latest`. Aliases move when TypeSafe ships
  a release and would silently change stored results. The response `model` field is what gets
  persisted, whatever alias was configured.
- Uses `IHttpClientFactory` with a named client and the standard
  `Microsoft.Extensions.Http.Resilience` handler tuned as above. `TimeoutSeconds` defaults to
  20 and is the total provider-call budget including backoff and retries, not a per-attempt budget;
  validate it in the range 1–20 seconds, below the reservation's 60-second lifetime (§13).
  No Python, Node or sidecar.
- Pricing note for sizing: input tokens only are billed (output is free), so state size is the
  cost lever; §8's truncation rules are also the budget.

## 11. Question set (version 1)

Centralised in `FailureClassificationQuestionSet` with `Version = 1`, persisted with every
classification. Four narrow, independent questions; never one broad "what should we do" question.

### `failure_category` — type `choice`

`criteria` is a map of option → description object (`what`, `examples`), which is the documented
Choice shape. Options:

| Option | What | Examples |
|---|---|---|
| `transient_dependency` | A temporary downstream or infrastructure condition that may clear without changing message, config or code | timeout, connection reset, HTTP 429, temporary 5xx, throttling, deadlock, DNS/network blip |
| `authentication_configuration` | Credentials, permissions, endpoint URLs, certificates or environment settings are wrong or expired | 401/403, expired secret, wrong host, missing setting |
| `contract_schema` | The data does not satisfy the technical contract | malformed JSON, (de)serialisation error, missing required property, wrong type, unsupported version |
| `business_rule` | Technically valid, rejected by a domain rule | invalid state transition, credit limit, closed account |
| `missing_reference_data` | A referenced business record does not exist | customer/order/account not found, missing master data |
| `application_defect` | Evidence points at a programming error | NullReferenceException, IndexOutOfRange, unexpected internal exception |
| `messaging_platform` | NimBus, Service Bus, topology, sessions or transport behaviour | lock lost, session unavailable, entity not found, message too large |
| `unknown` | Insufficient evidence or none of the above fits | — |

`unknown` always exists; TypeSafe's own guidance is to include an "other" option so the model
is never forced into a wrong category.

### `retry_likely_to_succeed_unchanged` — type `noul`

> Given the supplied failure evidence, is processing the same message again, without changing
> its payload, configuration, application code or dependent data, likely to succeed?

### `change_required_before_success` — type `noul`

> Does the evidence indicate that data, configuration, permissions, application code or another
> persistent condition must change before this message can succeed?

### `external_dependency_involved` — type `noul`

> Does the evidence indicate that the failure originates primarily in an external dependency
> rather than in the handler's own logic?

Noul answers are used directly as probabilities. The question set is unit-tested as data: option
ids, instruction text and criteria are pinned so a change bumps `Version`.

## 12. Deterministic guidance

Jev is never asked "should NimBus retry, skip or dead-letter". Code composes the signals:

```csharp
public enum FailureGuidance { Uncertain, RetryMayHelp, ChangeLikelyRequired, Investigate }
```

```
if   categoryConfidence < Thresholds.MinimumCategoryConfidence          → Uncertain
elif category == transient_dependency
     && retryLikelihood >= Thresholds.RetryLikely                        → RetryMayHelp
elif changeRequiredLikelihood >= Thresholds.ChangeRequired              → ChangeLikelyRequired
else                                                                    → Investigate
```

Defaults: `MinimumCategoryConfidence 0.60`, `RetryLikely 0.75`, `ChangeRequired 0.75`. Thresholds
are configuration, because they must be tuned against real NimBus failures. `categoryConfidence`
is the provider's `confidence` field for the Choice answer, not the top probability; both are
stored. TypeSafe's published bands (below 0.5 "don't guess", 0.5–0.9 "proceed with caution")
are the reason the default floor sits at 0.60 rather than lower.

No code path calls Resubmit, Skip, Complete, Abandon or DeadLetter from these values (§19).

## 13. Result model, persistence, audit

```csharp
public sealed record FailureClassification
{
    public required string Id { get; init; }                 // "{FailureMessageId}:{Revision}"
    public required string FailureMessageId { get; init; }
    public required int Revision { get; init; }              // monotonically allocated; gaps allowed
    public required string EventId { get; init; }
    public required string EventTypeId { get; init; }
    public required string EndpointId { get; init; }
    public string? SessionId { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }              // versioned id from the response
    public required int QuestionSetVersion { get; init; }
    public required string Category { get; init; }
    public required double CategoryConfidence { get; init; }
    public required IReadOnlyDictionary<string, double> CategoryProbabilities { get; init; }
    public required double RetryLikelihood { get; init; }
    public required double ChangeRequiredLikelihood { get; init; }
    public required double ExternalDependencyLikelihood { get; init; }
    public required FailureGuidance Guidance { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public required bool EventPayloadIncluded { get; init; }
    public required string RequestedBy { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public int SchemaVersion { get; init; } = 1;
}
```

The full probability distribution is stored for later calibration, threshold tuning and model
comparison. Revisions are append-only; nothing is overwritten.

```csharp
public interface IFailureClassificationStore
{
    Task<FailureClassification?> GetLatestAsync(string failureMessageId, CancellationToken ct);
    Task<IReadOnlyList<FailureClassification>> GetHistoryAsync(string failureMessageId, CancellationToken ct);
    Task<ClassificationOperationState> GetOperationStateAsync(string failureMessageId, CancellationToken ct);
    Task<ClassificationBeginResult> TryBeginAsync(
        string failureMessageId, string requestId, bool force, CancellationToken ct);
    Task CompleteAsync(ClassificationReservation reservation,
        FailureClassification classification, CancellationToken ct);
    Task FailAsync(ClassificationReservation reservation,
        bool outcomeUnknown, CancellationToken ct);
}
```

`ClassificationBeginResult` is a discriminated result: `Cached` (classification), `Acquired`
(`ClassificationReservation`), `InProgress`, `PreviouslyFailed`, or `OutcomeUnknown`.
`PreviouslyFailed` replays the original failure as `503` without a provider call. A reservation contains the
failure id, request id, allocated revision, unguessable ownership token and expiry timestamp.
These types and their implementations belong to the extension project. No unguarded save or
upsert operation is exposed. Classification reads return completed results only;
`ClassificationOperationState` is `None`, `InProgress`, `Completed`, `Failed` or `OutcomeUnknown`
for the latest operation. Its read treats an expired pending reservation as `OutcomeUnknown`.

Durable coordination, shared by every WebApp instance:

- `TryBeginAsync` atomically checks the cache and operation state and, if eligible, reserves the
  next revision before any provider call. Only one unexpired reservation may exist per failure.
  A repeated request id returns its original result/state; it never creates another provider call,
  even with `force`. An active reservation returns `InProgress` to other callers, including force.
- A reservation expires after 60 seconds; the complete HTTP operation, including provider retries,
  has a 20-second budget. Expiry is an ambiguity boundary, not permission to replay automatically.
  A timeout, connection loss after sending, process crash or uncertain completion write produces
  `OutcomeUnknown` (an expired reservation is treated this way on read). A non-force request must
  surface that state without another provider call, even if an older classification exists.
- Only an explicit new force request with a new request id may supersede an expired or unknown
  operation. Warn that the previous request may have incurred provider cost. Atomically invalidate
  its ownership token when reserving a new revision. Late completions from the old owner cannot
  persist a result or release the new reservation. Definitively failed calls may release their
  reservation; retries with a new request id then follow the normal cache/force rule.
- `CompleteAsync` atomically inserts the immutable classification and marks the matching operation
  complete, conditional on its current, unexpired ownership token. Repeating an acknowledged or
  ambiguously acknowledged completion with the same reservation and result is idempotent.
  `FailAsync` is also conditional and must never downgrade a completed operation. Failed or
  superseded operations may leave gaps in revision numbers; revisions are never reused.

This prevents competing instances from calling the provider for the same active operation.
It does not promise exactly-once provider billing after a crash or an explicit re-analysis.

Storage, owned by the extension and separate from the core messaging schema. Living in the same
repository does not change this: a deployment that never enables the feature must not acquire its
tables or container, so the schema cannot ride the message store's migration sequence.

- **SQL Server**: table `dbo.FailureClassifications` (case-sensitive PK `FailureMessageId`,
  `StateJson`, `Version` rowversion). Each row is the atomic aggregate for one failure: operation
  history, immutable completed results, request-key bindings and source coordinates. Conditional
  insert/update commits each transition, including reservation and completion, in one statement.
  No transaction is held across HTTP. This replaces the draft's multi-table layout.
  The extension runs its own DbUp journal (`dbo.IntelligenceSchemaVersions`) against the same
  connection, with scripts embedded in the extension assembly, and only when the feature is
  enabled. It must not add scripts to `NimBus.MessageStore.SqlServer`'s `Schema/00NN_*.sql`
  sequence.
- **Cosmos DB**: container `failureclassifications`, partition key `/failureMessageId`, same
  database as the message store. A fixed `id=state` aggregate per partition uses conditional
  create and ETag-conditional replacement. One write commits the ownership transition and result;
  completed revisions cannot be changed by the protocol. No container TTL: expiring ownership
  or request-key records independently would permit unsafe replay.
- Aggregate admission is bounded at 1.5 MB serialized UTF-8, below Cosmos' document limit.
  Capacity exhaustion returns `503 ClassificationCapacityExceeded`; history and request identities
  are never silently evicted. This MVP bound is per failure occurrence, not per endpoint.
- **In-memory**: test-only implementation and shared conformance suite in
  `tests/NimBus.Extensions.IntegrationIntelligence.Tests`, mirroring `NimBus.Testing`'s pattern
  without adding an extension dependency to that production project.
- Storage provider selection follows the WebApp's resolved `storageProvider` (`sqlserver` /
  `cosmos`) so a SQL-backed site never needs a Cosmos account.

Audit: every POST writes one `MessageAuditType.FailureClassified` row through the existing
`IAuditLogService.LogAuditAsync` (best-effort, both success and access-denied branches), with
`Data` = JSON `{ messageId, provider, model, questionSetVersion, category, categoryConfidence,
guidance, inputTokens, eventPayloadIncluded, revision, cached }`. The API key is never in `Data`.

## 14. HTTP API

All routes are served by controllers in the extension assembly and are absent (`404`) when the
feature is disabled (the SPA reads that as "hide the card").

When enabled but `ProviderNotConfigured`, only the status controller is discovered. Every
classification/history route below returns `404` because its controller is pruned, rather than
failing activation or returning a provider error. The status route retains its endpoint
authorization and returns `200` with `canAnalyze = false` to an authorized Reader.

| Route | Purpose | Responses |
|---|---|---|
| `GET /api/integration-intelligence/status?endpointId={endpointId}` | Feature status and capability for this endpoint | `200 { status: Ready \| ProviderNotConfigured, provider, model, contractVersion, endpointId, canAnalyze }`, `400` missing endpoint, `401`, `403` no Reader role, `404` unknown endpoint or feature disabled |
| `GET /api/integration-intelligence/failures/{eventId}/{messageId}/classification` | Latest classification; also used for progress refresh | `200`, `404` none yet / disabled / ProviderNotConfigured, `403` no Reader role on the endpoint, `409` `AnalysisInProgress` / `AnalysisOutcomeUnknown`, `503` latest operation definitively failed |
| `GET …/classification/history` | All revisions | `200 []`, `404` disabled / ProviderNotConfigured |
| `POST …/classification` body `{ "force": false }`, header `Idempotency-Key: <UUID>` | Analyze (cached unless `force`) | `200` classification (`cached: true/false`), `400` missing/invalid key, `401`, `403`, `404` unknown message / disabled / ProviderNotConfigured, `409` ineligible / `AnalysisInProgress` / `AnalysisOutcomeUnknown`, `503` provider or classification store unavailable |

Routes carry `eventId` because `GetMessage(eventId, messageId)` needs both; the draft's
`/failures/{messageId}` alone cannot load the row.

The status endpoint requires Reader on the supplied, verified endpoint and computes `canAnalyze`
as `status == Ready && HasRoleAsync(Contributor, endpointId)`. Omitted endpoint context never
falls back to site-wide or any-endpoint capability. Non-Ready status handling uses host authorization
services without constructing provider or classification-store services.

The latest-classification GET checks operation state before returning a result: active, unknown or
failed latest operations produce the responses above, so progress refresh cannot mistake an older
revision for the result of an in-flight re-analysis. History still returns all completed revisions.
GET never reserves work or calls the provider. Poll with bounded backoff while in progress; stop
on completion, unknown outcome, failure or navigation. Error bodies carry a stable `code` matching
the names in the response table.

POST behaviour, in order: authenticate → load message coordinates → authorize on the stored
endpoint (§15) → status must be `Ready` → load that endpoint's event status → eligibility (§6) →
atomic cache check/reservation (§13) → build input (§8) → redact (§9) → provider call (§10) →
derive guidance (§12) → conditional completion (§13) → audit → return. Cached and rejected
requests still authorize and audit. Active or unknown operations take precedence over an older
cached result as specified in §13. Store unavailability prevents a provider call when no reservation
can be obtained. Request ids are scoped to the failure; a reused id with different force semantics
returns `409`. The client generates one UUID per deliberate Analyze/Re-analyze action and reuses
it for transport retries. An in-process lock may optimize contention but is not the correctness gate.

The POST is registered under the WebApp's existing rate-limiting policies
(`AddNimBusRateLimiting`) with a dedicated policy name, since each call costs provider tokens.
Add `RateLimitPolicyNames.Intelligence` and attach it through the WebApp's
`RateLimitPoliciesConvention.PolicyFor` branch for the extension controller's POST action.
Do not attach an `EnableRateLimiting` attribute in the extension: the convention's `Enabled`
kill switch must leave this policy unattached too. GET routes remain unaffected. Test endpoint
metadata and actual enforcement with limiting both enabled and disabled.

## 15. Authorization and activation

Reuse the spec 026 ladder (`AccessRole.None < Reader < Contributor < Owner`, plus the orthogonal
PiiReader capability) via `IEndpointAuthorizationService.HasRoleAsync`:

- `GET` classification/history: `Reader` on the endpoint (same as viewing the failure).
- `GET` status: `Reader` on the required endpoint; compute `canAnalyze` for that endpoint only.
- `POST` (analyze / re-analyze): `Contributor` on the endpoint (same tier as Resubmit/Skip).
- `IncludeEventPayload = true` does **not** require the caller to be a PiiReader, because the
  payload is fully redacted regardless of annotation mode before it leaves (§9). The response never echoes the
  payload back.

Activation replaces the draft's licence entitlement. There is no licence key, no signed token and
no entitlement check; the feature is open source and the only gate is deliberate configuration:

- `NimBus:IntegrationIntelligence:Enabled` (default `false`) is the switch. It is deployment
  configuration, not a WebApp setting: turning on an outbound data flow to a third party is an
  operator/infra decision, so there is no UI toggle and no admin API for it in MVP.
- Feature states: **disabled** → nothing registered, routes `404` (§4.1). **Enabled, provider
  options invalid or API key missing** → status `ProviderNotConfigured`; only status handling is
  registered, classification/history controllers are pruned (`404`), and no provider call can
  happen. **Enabled and valid** → `Ready`.
- Validation failures log one sanitized warning at startup and never prevent the WebApp from
  starting (§17).
- The operator's own TypeSafe API key is the only credential. Provider cost and TypeSafe's terms
  are between the operator and TypeSafe.

## 16. WebApp UI

An **Integration Intelligence** card on the event-details page, placed with the error section of
a Failed/DeadLettered message (`message-listing.tsx` renders `errorContent` there today).

- Feature disabled (`404`) / status not `Ready`: card not rendered.
- `Ready`, not analysed: title, "No analysis has been performed.", **Analyze failure** (visible
  only when `canAnalyze`, i.e. the caller is Contributor on the endpoint).
- Running: "Analyzing failure…" with the button disabled.
- `AnalysisInProgress`: show "Analysis is already in progress"; refresh the result with GET,
  without automatically issuing another POST. `AnalysisOutcomeUnknown`: show that the previous
  analysis may have incurred cost and allow a Contributor to explicitly Re-analyze with a new
  request id. Both Analyze and Re-analyze use the current endpoint's `canAnalyze` value.
- Result:

  ```
  Likely category          Missing reference data      Confidence 91 %
  Retry unchanged          14 %
  Change likely required   92 %
  External dependency      67 %
  Guidance                 Change likely required before retry
  Analyzed with TypeSafe jev-1.13.0 · revision 2 · 2026-09-19 10:42 UTC · by a.operator
  ▸ Classification details   (next two categories with probabilities, question set v1)
  AI-assisted assessment. Advisory only. NimBus processing and recovery decisions are unchanged.
  [ Re-analyze ]
  ```

- Provider failure: "Failure analysis is temporarily unavailable. The NimBus message and its
  processing state are unchanged."
- No Retry/Skip buttons inside the card. The existing recovery controls stay where they are.
- The disclaimer line is always rendered.

## 17. Configuration

```json
{
  "NimBus": {
    "IntegrationIntelligence": {
      "Enabled": false,
      "FailureClassification": {
        "Enabled": true,
        "Provider": "TypeSafe",
        "TypeSafe": { "ApiKey": "", "Model": "jev-1.13.0", "BaseUrl": "https://api.typesafe.ai", "TimeoutSeconds": 20 },
        "Data": {
          "IncludeEventPayload": false,
          "IncludeRecentFailureHistory": true,
          "MaximumHistoryItems": 5,
          "MaximumErrorTextLength": 4000,
          "MaximumStateCharacters": 24000,
          "AdditionalRedactedKeys": []
        },
        "Thresholds": { "MinimumCategoryConfidence": 0.60, "RetryLikely": 0.75, "ChangeRequired": 0.75 }
      }
    }
  }
}
```

`Enabled` defaults to `false`, and the repository's committed `appsettings*.json`, the Aspire
AppHost and the CrmErpDemo sample all leave it `false` so nothing in the repo ever calls TypeSafe
by default. `ApiKey` comes from normal `IConfiguration` providers (environment variables such as
`NimBus__IntegrationIntelligence__FailureClassification__TypeSafe__ApiKey`, Key Vault
references, user secrets in development) and is never committed. Bind and validate an
extension-owned immutable options snapshot inside a contained initialization step before registering
provider/store services. Catch binding and validation failures there, log a sanitized warning and
register only status handling with `ProviderNotConfigured`. Do not register `ValidateOnStart` or
another throwing host startup validator for these optional-extension options: catching exceptions
from `ConfigureServices` would not contain the later host-start validation phase.

Validate numeric limits, thresholds, provider selection and required provider settings without
including supplied secret values in diagnostics. The disabled state is resolved before provider
options are read, so invalid provider options on a disabled feature are never even bound.
Configuration changes require a WebApp restart
in MVP. Tests must start the actual host, resolve status and call an existing management endpoint
with malformed and out-of-range configuration, rather than testing `ConfigureServices` alone.

## 18. Failure behaviour and observability

Provider timeout, 429/529, 5xx, 401/403, malformed response or network failure → the POST
returns `503` with the message in §16, and nothing else happens: no status change, no resubmit,
no Resolver involvement, no session impact. The classification is not persisted for a failed
call; an audit row is still written with `outcome = ProviderError`. Update the reservation as
definitively failed or outcome unknown according to §13. A timeout or ambiguous persistence failure
must not silently make the request eligible for an automatic provider replay. On an ambiguous
completion write, first read back the operation/result; return success if committed, otherwise
surface unavailability and preserve the reservation/unknown outcome for subsequent requests.

Telemetry follows the existing naming (`NimBus.<Area>` meters/sources, `nimbus.<area>.*`
instruments and tags):

- Meter and ActivitySource `NimBus.Intelligence`; span `NimBus.Intelligence.FailureClassification`.
- Instruments: `nimbus.intelligence.failure_classification.requests` (counter),
  `.duration` (histogram, ms), `.errors` (counter), `.input_tokens` (counter).
- Tags: `nimbus.intelligence.provider`, `.model`, `.outcome` (`ok | cached | provider_error |
  rejected`), `.category`; reuse `nimbus.endpoint` and `nimbus.event_type`. Never `EventId`,
  `MessageId` or `SessionId` (cardinality), never payload or exception text.

## 19. Architectural invariant

```
AI may evaluate a failure.  Code owns the workflow.  The operator owns the recovery decision.
```

Being in the same repository makes this invariant easier to break by accident (one
`ProjectReference` away), so it is pinned by tests in
`tests/NimBus.Extensions.IntegrationIntelligence.Tests`:

- An architecture test asserts the extension assembly's referenced assemblies exclude
  `NimBus.Manager`, `NimBus.SDK`, `NimBus.ServiceBus` and `NimBus.Resolver` (the routes to
  Resubmit/Skip/handoff settlement and the processing path), and that no type in it implements
  `IFailureDispositionClassifier`, a pipeline behavior or a lifecycle observer.
- The reverse direction: among `src/` projects, only `NimBus.WebApp` references the extension.
- The guidance rule tests (§20) assert the output type is `FailureGuidance`, never a message
  control operation.

The invariant is also stated in the XML doc of `FailureGuidance`.

## 20. Tests

All tests live in this repository and run in the normal `dotnet test`. None contacts TypeSafe.

Extension (`tests/NimBus.Extensions.IntegrationIntelligence.Tests`):

- **Activation**: default configuration registers no extension service and every
  `/api/integration-intelligence/*` route returns `404`; enabled + valid is `Ready`; enabled with
  a missing API key is `ProviderNotConfigured`; each non-Ready state makes zero provider calls and
  creates no schema; a bad option never throws out of `AddNimBusIntegrationIntelligence`.
  In ProviderNotConfigured, classification/history routes return 404 while authorized status
  returns 200 with `canAnalyze = false`. Exercise all states under LocalDev, Entra-only,
  Identity-only and dual authentication, verifying actual discovery/suppression and using
  authenticated requests where needed to distinguish routing from challenges.
- **Host adapters**: resolved SQL settings and Cosmos database name match the configured store;
  the extension uses the same host-owned `CosmosClient`, does not dispose it, and resolves no
  unselected backend. Audit/actor adapters use the current request context without exposing it
  through their contracts. Missing adapters never grant access or enable provider calls.
- **Host startup**: start a real WebApp host with the feature enabled and a malformed timeout,
  invalid threshold, missing API key and invalid provider setting. In every case the host starts,
  an existing authorized management request succeeds, status is `ProviderNotConfigured`, and no
  provider/store execution services are constructed. Also cover a disabled feature with invalid
  provider options (still `404`, options never bound); diagnostics never contain supplied secret
  values.
- **Input builder**: Failed `ErrorResponse`; DeadLettered row; history limited to
  `MaximumHistoryItems`; `Completed`/`Pending`/`TooManyRequests` rejected with 409; payload
  absent by default; `MaximumStateCharacters` drops history before payload. Use one event shared
  by endpoints A/B with a Contributor on A only: outbound history contains no B rows. Include
  another session, a later resubmit failure and equal-timestamp rows; all are excluded before
  applying the history limit. History-disabled sends no history. Assert the exact outbound state.
- **Redaction**: `password`, `PASSWORD`, `apiKey`, `accessToken`, `authorization`,
  `connectionString`, `SharedAccessKey=` in free text; `[Sensitive]` fields fully redacted for
  all three annotation modes, including nested objects and collections. Missing redactor, unknown
  type and invalid JSON omit payload with `EventPayloadIncluded = false`. Quoted sensitive values
  in error text are removed even when payload export is disabled; missing/unresolvable source
  payload withholds free text. Scrubbing precedes truncation. Build a dead-letter description
  through `SendDeadLetterResponse` from a thrown exception with inner exception, stack trace and
  sentinel secret; assert the raw description and sentinels never reach HTTP. Also cover missing
  `ErrorContent` and an exception dump embedded in `ErrorText` (text omitted).
- **TypeSafe client** (mocked HTTP): one request with four questions; configured model sent,
  response `model` stored; `choice`/`probabilities`/`confidence` and `noul` deserialise;
  `usage` captured; timeout, 401, 422, 429 (retried then surfaced), 529, malformed body.
- **Guidance**: `0.59 → Uncertain`, `0.60` eligible; `transient + retry 0.75 → RetryMayHelp`;
  `transient + retry 0.74 → Investigate`; `changeRequired 0.75 → ChangeLikelyRequired`;
  non-transient with high retry → `Investigate`.
- **Store conformance** (in-memory, SQL container, Cosmos emulator): save; latest; history
  ordering; revisions never overwrite. Saving occurs only through a valid reservation's completion.
  Two independent clients race initial reservations and force reservations: only one acquires;
  completion and allocation are atomic; repeated completion is idempotent; stale owners cannot
  complete or fail a newer operation; revisions are never reused after failure. Test expiry,
  ambiguous completion read-back, repeated request ids and cache checks inside the atomic operation.
  Operation-state reads report expired pending reservations as unknown without starting work.
- **API**: 401 anonymous; 403 no endpoint role; 403 Reader on POST; 404 unknown message; 409
  non-failure; 200 first call; 200 `cached: true` second call; `force` creates revision 2;
  provider 503 leaves the message row byte-identical; audit row written with the expected `Data`.
- **Endpoint capability**: status requires endpoint context; a caller who is Contributor on A,
  Reader on B and has no site role receives `canAnalyze = true` for A and `false` for B; no role
  returns 403. POST and cached/history reads authorize against the stored message endpoint, so
  supplying another endpoint to status does not grant access to a message.
- **Cross-instance API**: start two WebApp instances sharing SQL (and separately Cosmos), with
  independent service providers and a counting fake provider. Race non-force requests before the
  first result exists: exactly one acquires and calls the provider, the other gets
  `AnalysisInProgress`, and a later request reuses the result. Repeat with the same idempotency
  key and with force requests. Simulate owner crash/timeout and a lost completion acknowledgment:
  no automatic provider replay; expired/unknown operations require explicit force with a new key;
  a late owner cannot commit over its successor. Verify unique increasing revisions, permitting gaps.
  GET progress refresh surfaces active/unknown/failed state rather than an older result and makes
  zero provider calls; history retains older completed revisions throughout.
- **Architecture**: §19.

Platform projects (existing test projects):

- Rate-limit metadata and enforcement: only the classification POST receives the Intelligence
  policy when limiting is enabled; no Intelligence policy is attached when the kill switch is
  off. GET endpoints and existing policies retain their behavior.
- `AuditTypeContractTests` covers `FailureClassified`.
- Full-redaction capability: all annotation modes become `***`; nested/collection traversal,
  unknown-type and malformed-JSON behavior match the existing fail-closed contract. Existing
  `IEventJsonMasker.Mask` mode behavior and third-party interface implementations remain compatible.
- SPA: card hidden on 404, hidden on status ≠ `Ready`, `Analyze` hidden when `canAnalyze` is
  false, disclaimer always present. Also hide on 403; test Contributor on A / Reader on B,
  endpoint navigation while A's response is in flight, both buttons gated, stable request id on
  transport retry, progress refresh using GET only, and explicit recovery from an unknown outcome.
- Regression: the full existing suite with the feature at its default (disabled), unchanged.

## 21. Acceptance criteria

1. Standard NimBus behaves exactly as before with the feature disabled (existing suite green,
   no new tables/containers, no outbound calls).
2. `IntegrationIntelligence.Enabled` defaults to `false`; disabled means no extension services,
   no controllers (`404`) and no calls. Invalid extension options never prevent host startup or
   existing management operations.
3. The extension is a project in `NimBus.sln`, MIT-licensed, published as
   `Akaule.NimBus.Extensions.IntegrationIntelligence`; it contains no licence or entitlement
   check. Only `NimBus.WebApp` references it. Non-Ready states never call the provider.
4. An operator with Contributor on the endpoint can open a Failed or DeadLettered message and
   request analysis; Readers can view an existing result. Status capability and both action buttons
   are endpoint-specific; cached results and history retain the same authorization checks.
5. One TypeSafe request evaluates all four questions.
6. The stored result contains category, provider confidence, full probability distribution,
   retry / change-required / external-dependency likelihoods, model, question-set version, tokens.
7. Repeated opens and non-force POSTs reuse a completed result unless a newer active/unknown
   operation requires the explicit state in §13. Across WebApp instances, at most one reservation
   owns each active analysis; repeated request ids never initiate another provider call.
8. Payloads are absent by default; opted-in payloads fully redact all sensitive annotation modes
   and are secret-scrubbed. Stack-trace fields and raw dead-letter descriptions are always omitted.
   History contains only authorized target-endpoint/session failures strictly earlier than the target.
9. Re-analyze creates a new, atomically allocated revision; successful history is immutable and
   retained. Crashes/unknown outcomes never trigger automatic provider replay; explicit recovery
   warns about possible prior cost, and late owners cannot persist stale results.
10. Provider failures never touch NimBus message state.
11. No code path from a Jev answer to Resubmit, Skip, Complete, Abandon or DeadLetter (§19 test).
12. Existing recovery controls unchanged.
13. The card is labelled advisory.
14. Token usage and request metrics are recorded under `NimBus.Intelligence`.
15. All tests in §20 pass.

## 22. Non-goals (MVP)

Automatic classification, automatic retry/skip/dead-letter, changes to
`IFailureDispositionClassifier` or retry policies, semantic message validation, anomaly
detection, sequence analysis beyond the recent-history window, incident/ticket creation,
provider selection UI, customer-specific training, natural-language explanations, an autonomous
agent, Resolver-side hosting, a `nb` CLI verb.

Future modules the design leaves room for: Semantic Message Validation, Message Anomaly
Detection, Event-Sequence Analysis, Recovery Advisor, Incident Correlation, Intelligent Alert
Routing. Failure Classification establishes the reusable pieces: config-gated activation,
provider abstraction, AI-safe state construction, redaction, probability results, intelligence
persistence, card pattern, telemetry. Later modules join the same extension project behind their
own `Enabled` flag.

## 23. Changes from the original draft

| Draft | Refined | Why |
|---|---|---|
| "The commercial package references NimBus contracts" and is added to the host | Superseded by §23.1: in-repo project referenced by the WebApp, enabled by configuration | See §23.1. |
| Classification API "added to the WebApp" | Routes live in the extension assembly; the SPA card probes a status endpoint; routes are **not** in `api-spec.yaml` | NSwag generates a server interface from `api-spec.yaml` that the WebApp project must implement, which would move the controllers out of the extension and break disabled-means-absent. Identity's routes follow the same rule. |
| `GET /failures/{messageId}/classification` | `/failures/{eventId}/{messageId}/…` | `IMessageTrackingStore.GetMessage` needs both ids. |
| Audit type `IntegrationIntelligenceFailureClassified` | `MessageAuditType.FailureClassified`, appended last, plus the three `api-spec.yaml` enum lists | Enum values persist numerically in Cosmos and round-trip through the generated API enums; this bit spec 026 (`GrantRole`/`RevokeRole`). |
| "Do not classify Pending, Completed, Deferred, Skipped, Unsupported" | Also `TooManyRequests`, `Published` | Those states exist in `ResolutionStatus`. |
| Exception `Type`/`Message` | Mapped from `ErrorContent.ErrorType`/`.ErrorText`, plus `ExceptionSource`; raw dead-letter description omitted | The description can contain `exception.ToString()` and stack traces; use separate reason and safe fields. |
| New redactor with a key list | Add `IEventJsonRedactor` using the existing traversal, with unconditional full redaction; key-list scrubber on top | Existing `Mask` honors PartialReveal/Hash and cannot enforce the export policy. |
| Model `jev-latest` | Default `jev-1.13.0`, response `model` persisted | Aliases move on release and would silently change stored results. |
| "Category confidence" undefined | Provider `confidence` field of the Choice answer, stored alongside probabilities | TypeSafe reports confidence separately from the distribution; Noul answers carry no confidence. |
| Retry on any provider error | Retry only 429/529, bounded | TypeSafe's documented retryable statuses. |
| Own tables "may" be separate | Own DbUp journal and own Cosmos container, never in the message store's `Schema/` sequence | A deployment that never enables the feature must not acquire its schema. |
| Authorization "Contributor or equivalent" | Concrete `AccessRole` mapping via `HasRoleAsync`; PiiReader interaction defined | Spec 026 is the existing model. |
| Resolver start-up must survive licence failure | Resolver never references the extension in MVP; no licence exists | On-demand only; nothing runs in the processing path. |
| Metrics named ad hoc | `NimBus.Intelligence` meter/source, `nimbus.intelligence.*` instruments, reuse `nimbus.endpoint`/`nimbus.event_type` tags | Matches `NimBusActivitySources` and existing instrument naming. |
| Broad tests list | Split into extension-project tests and platform-project tests, plus architecture tests for §19 | Different test projects own different guarantees. |
| Event-wide recent history | Filter endpoint/session and strictly earlier failures before limiting | Prevent unauthorized endpoint evidence and later attempts from entering an occurrence's assessment. |
| Global status capability | Required endpoint context and endpoint-specific `canAnalyze` | Endpoint-only Contributors must be able to analyze their endpoint without gaining access to another. |
| `ValidateOnStart` for optional options | Contained snapshot binding/validation before service registration | Host-start validation exceptions would prevent the entire WebApp from starting. |
| In-process request lock | Durable reservations, idempotency keys and atomic revision allocation | Coordinate multiple instances and fence late owners; unknown external outcomes require explicit recovery. |

### 23.1 Packaging pivot (2026-09-20)

Decision: Failure Classification is **not** a commercial package in a private repository. It is an
optional, MIT-licensed extension project in this repository, off by default.

| Removed | Replaced by | Effect |
|---|---|---|
| Separate private repository, private NuGet feed | `src/NimBus.Extensions.IntegrationIntelligence` in `NimBus.sln`, published with the other `Akaule.NimBus.*` packages | One build, one CI gate, one release train. |
| Licence entitlement: JWS/ES256 token, embedded public key, `Unlicensed`/`Expired` states, clock-skew grace, entitlement tests | Nothing. `Enabled` + a valid provider configuration is the whole gate | Status shrinks to `Ready \| ProviderNotConfigured`; "disabled" is a `404`. |
| WebApp extension host: `IWebAppExtension`, `NimBus:WebApp:Extensions`, `Assembly.LoadFrom`, MVC application-part loading, load-failure handling | A `ProjectReference` from `NimBus.WebApp` plus `AddNimBusIntegrationIntelligence`, controllers removed when disabled (Identity precedent) | No reflection loading, no new public contract to version, no assembly-path configuration. |
| `nb deploy apps --webapp-extension` and the manual zip step | Nothing; the extension is in the published WebApp build | No deploy-tool change. |
| Two test suites gated in two repositories | One extension test project here, plus additions to existing projects | The §19 architecture test gains a reverse check, since an accidental reference is now one line away. |

Unchanged by the pivot: on-demand execution, evidence and redaction rules, the question set,
deterministic guidance, durable reservations, the HTTP contract (minus the three removed status
values), authorization, the separate schema journal, telemetry, and the architectural invariant.

What "optional" still guarantees: default-off; zero registrations, routes, schema and outbound
calls when off; no reference from any project other than the WebApp; a separate NuGet package a
custom host can omit.

New obligation that comes with open-sourcing it: the repository now contains code whose purpose
is to send operational data to a third party. `docs/integration-intelligence.md` must say so up
front, list exactly which fields leave (§8, §9), and state that nothing is sent unless an operator
enables the feature and supplies their own key.

## 24. Open decisions

1. **Cosmos container creation under Entra data-plane RBAC.** Creating `failureclassifications`
   is a control-plane operation. Confirm how the message store creates its containers on an
   RBAC-only deployment and ship a Bicep snippet if provisioning must own it. With the feature
   in-repo the Bicep can carry the container behind a parameter that defaults to off.
2. **One extension package or storage satellites.** A single
   `NimBus.Extensions.IntegrationIntelligence` package carries both `Microsoft.Data.SqlClient`
   and `Microsoft.Azure.Cosmos`. Recommendation: single package for MVP; the WebApp already ships
   both SDKs, and splitting into `.SqlServer` / `.CosmosDb` satellites is mechanical later if a
   custom host objects to the weight.
3. **ADR.** The pivot sets a precedent ("AI-assisted features ship in-repo, off by default,
   bring-your-own provider key, advisory only"). Recommendation: record it as ADR-016 alongside
   the implementation PR, since ADR-015 already covers distribution and should cross-reference it.
4. **Per-endpoint allow-list.** With no licence gate, `Enabled` exposes Analyze on every endpoint
   to its Contributors. An optional `AllowedEndpoints` list (empty = all) would let an operator
   keep sensitive endpoints' evidence in-house. Recommendation: include it; it is one filter in
   the status/POST path and a natural companion to default-off.
5. **Storage scope for MVP.** Both SQL Server and Cosmos, or SQL first (the dev environment is
   SQL-backed). Recommendation: both, because the in-memory conformance suite makes the second
   provider cheap and the Cosmos emulator recipe exists.
6. **Retention — resolved during remediation.** Remove classifications with their source evidence,
   eventually, without coupling successful admin deletion to the optional store. While enabled,
   reconcile persisted operation coordinates against authoritative event existence every 60 seconds.
   Retry unavailable stores; do not interpret transient errors as missing events. Event/session/
   endpoint deletion and age-based source expiry are covered regardless of which admin path deleted
   them; broker-only subscription purges leave stored events and classifications unchanged.
   Tombstone the per-failure aggregate atomically, clearing results, actors, request keys and source
   coordinates but retaining the failure ID and deletion marker to fence late completion/resurrection.
   Source absence also denies GET and completion. Cleanup resumes after reactivation if disabled.
   There is no independent TTL for results or idempotency records.
