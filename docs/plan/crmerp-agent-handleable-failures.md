# CrmErpDemo: failures an operations agent can handle

Status: proposed. Parts 1 and 2 are implemented in the PR that adds this plan; parts 3 and 4 are
planned here and not started.

## Why

An operations agent that works the demo through the NimBus MCP server handed almost every failure
to a person. On the deployed demo, over seven days, it closed 994 cases as handed over and 45 as
retried and succeeded. All 45 came from one ERP error-mode reason, "Downstream timeout (transient)".

The agent is not the reason. It acts on three kinds of diagnosis and passes everything else on:

| Diagnosis | What the agent does | What it needs |
|---|---|---|
| Transient fault in the target system | Resubmits | Evidence that reads as temporary |
| Bad source data | Files a data-quality ticket | Error and payload that agree; 3 messages with the same pattern |
| Defect in the adapter | Files an issue, may start a fix | The endpoint's repository address; 3 messages with the same pattern |

The sample's failures miss these for reasons that are the sample's own:

| Demo failure | Why it was handed over |
|---|---|
| CRM API outage | The 503 body named the demo switch, so it read as a setting a person must turn off. |
| Schema mismatch | The error says `CountryCode` was "Denmark"; the stored payload holds a valid code. |
| Customer not found | The error names a fixed account id; the payload's `AccountId` is null. |
| Null reference | `ErpEndpoint` has no ownership, so there is no repository to file against. |
| Expired credentials | Correct: a person has to renew a secret. |

A fabricated error text that contradicts the payload is also a poor showcase for a person reading
the Integration Intelligence card, so the changes below help the demo with or without an agent.

## Part 1: the CRM outage reads as a real outage (done)

`CrmOutageSimulationHandler` returns a 503 whose reason phrase and body come from
`CrmErpDemo.Contracts/Demo/CrmOutageResponse.cs`: a gateway's "temporarily unavailable" text and a
`Retry-After`, with no mention of the switch. `CrmOutageResponseTests` pins that.

A retry still fails while the switch is on. That is the right behaviour: the new attempt fails, and
the circuit breaker showcase is unchanged.

## Part 2: a "Mostly transient" error-mode mix (done)

`ErpFailureReasons.MostlyTransient` joins **Random** in the erp-web dropdown. About 7 in 10 failing
messages draw the transient failure and the rest draw one of the other six. With a failure rate
below 100% most failures are ones a resubmit gets past.

## Part 3: data failures that come from the data

Goal: a failure whose error text and payload agree, so it is diagnosed as bad source data with
confidence and reported once three messages share the pattern.

1. **CRM can publish a bad value.** Add a crm-web demo switch, "Sloppy data entry". While it is on,
   the simulated traffic and the account form may save a value that breaks the contract. The value
   is stored in CRM and published as it is; nothing is faked downstream.
2. **ERP rejects it for real.** `Erp.Api`'s customer upsert validates the field and answers 422
   with a problem body that quotes the field and the value it received. The ERP adapter's existing
   `EnsureSuccessOrThrowAsync` path turns that into the handler's exception, so the evidence
   carries the true value.
3. **Retire the fabricated variants.** Remove "Schema mismatch (contract)" and "Customer not found
   (missing reference)" from `ErpFailureReasons`, or rewrite them so they quote the message's own
   values. Keep ids stable if they are kept.
4. **Tests.** A unit test for the validation, an AppHost test that a bad account produces a failed
   message whose error text contains the published value, and an e2e spec for the showcase.

Open questions to settle before building:

- **Which field.** `CountryCode` is the obvious one and the old fake used it, but it is the
  routing key for the regional hubs: a full country name would also change where the event is
  routed. `TaxId` is masked (`[Sensitive]`), so the evidence would hide the value the diagnosis
  depends on. A country name in `CountryCode` is the most recognisable mistake; verify the routing
  effect on a local stack first, and fall back to an unmasked field such as `LegalName` length or a
  new field if the unrouted hub makes the showcase confusing.
- **Fixing the data.** Correcting a field and resubmitting is a separate recovery path. The showcase
  should end with the operator correcting the account in crm-web, which publishes a new
  `CrmAccountUpdated`, and skipping the failed message.

Files, roughly: `Crm.Api` (state, admin endpoint, traffic simulator), `Crm.Web` (switch),
`Erp.Api/Endpoints/CustomerEndpoints.cs`, `CrmErpDemo.Contracts/Demo/ErpFailureReasons.cs`,
`Erp.Web` (dropdown text), `README.md`, tests, one e2e spec.

## Part 4: ownership and a defect that exists

Goal: an adapter defect on the demo can be filed against a repository, and what is filed is true.

1. **Seed ownership.** The demo's provisioning records owner, team, source system, repository
   address and ticket-link template for `CrmEndpoint` and `ErpEndpoint` through the WebApp's
   endpoint metadata API, from AppHost parameters so a deployment can point at its own repository.
   Leave them unset when the parameters are empty.
2. **Replace the thrown `NullReferenceException` with a real one.** Today `ErrorModeGuard` throws
   an exception whose message describes a mapper that does not exist. Anyone who opens the
   repository finds the simulation, not a bug. Instead, add a small mapping step to the ERP adapter
   with a genuine defect that a specific, legitimate input triggers (for example a contact with no
   last name), switched on by the existing "Null reference (application defect)" reason so the
   default demo stays healthy.
3. **Tests.** A test that the defect path throws for the triggering input only while the reason is
   selected, and that ownership is written when the parameters are set.

Open questions:

- **Which repository the demo names.** The public NimBus repository is the honest answer for the
  sample, but an agent with issue rights would then file demo issues there. Default to unset, and
  document the parameter.
- **Whether a planted defect belongs in a sample.** It must be obviously marked in code as the
  showcase's defect and impossible to hit with error mode off.

## Out of scope

- "Expired credentials" and "Customer closed" stay hand-overs: a person has to act.
- "Customer not found", even when made real, is a source-data ordering problem, not a transient
  fault of the target. It is recovered by resubmitting after the account arrives, which today is a
  person's call.
- An outage that ends by itself after a set time. Worth adding if the retry showcase needs a
  scripted recovery; the failure rate slider covers it for now.

## Verification

- `dotnet test tests/CrmErpDemo.AppHost.Tests` for each part.
- Parts 3 and 4 on a local Aspire stack with the Service Bus emulator, then the CrmErpDemo e2e
  suite, which fails on any retried test.
- After deploying, read a week of an agent's closed cases and compare the share handed over with
  the 994 of 1,058 measured before these changes.
