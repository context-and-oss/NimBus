# CrmErpDemo: showcase a NimBus retry rule

Status: implemented, in the PR that adds this plan.

## Why

The showcase demo registered no NimBus retry rule. The only one in the sample was test scaffolding
behind the E2E switch (`CrmErpDemo.Contracts/E2E/E2eExecution.cs`), so a reader had to leave the
sample to see `ConfigureRetryPolicies` used.

The obvious place for a rule is the "Downstream timeout (transient)" error-mode reason. It is the
wrong place. [crmerp-agent-handleable-failures.md](crmerp-agent-handleable-failures.md) leaves
exactly those failures in Failed so an operations agent can resubmit them: they were all 45 of the
agent's "retried and succeeded" cases, and "Mostly transient" was added to produce more of them. An
automatic retry on that reason would recover most of them before the agent sees them.

So the rule gets a failure reason of its own, and the timeout reason is left alone.

## What

A new ERP error-mode reason, **Rate limited (retried automatically)** (`throttled_dependency`),
throws a typed exception that the ERP adapter has one retry rule for.

- **Exception.** `ErpPricingThrottledException : Exception, IRetryAfterHint` in
  `CrmErpDemo.Contracts/Demo/`: HTTP 429 from the pricing service, `RetryAfter` of 5 seconds. Its
  text quotes no field of the payload.
- **Rule.** `Erp.Adapter.Functions/ErpResilience.cs`, `ConfigureRetries(DefaultRetryPolicyProvider)`:
  one `AddExceptionRule<ErpPricingThrottledException>`, 3 retries, exponential from 2 seconds,
  capped at 30. With the hint the waits are 5, 5 and 8 seconds. This is the shape
  `docs/building-adapters.md` recommends.
- **Wiring.** `sub.ConfigureRetryPolicies(...)` in `Erp.Adapter.Functions/Program.cs`.
- **Outcome.** Each delivery rolls against the failure rate again, so below 100% most messages get
  through on a retry; at 100% the budget runs out and the message ends as Failed.
- **Catalog.** The reason is in `ErpFailureReasons.All`, so erp-web's dropdown shows it with no
  front-end change and **Random** and **Mostly transient** can draw it as one of the other
  failures. The 70% timeout share is unchanged.

The CRM adapter is not touched: it already showcases the circuit breaker.

## The E2E rule

`AddNimBusSubscriber` uses the provider built by `ConfigureRetryPolicies` instead of an
`IRetryPolicyProvider` registered in DI (`NimBus.SDK/Extensions/ServiceCollectionExtensions.cs`).
The E2E profile registers its scripted rule through DI, so wiring the ERP adapter the documented
way would drop that rule on ErpEndpoint without an error and break `12-message-lifecycle.spec.ts`.

The scripted rule is therefore `E2eExecution.AddE2eRetryRule(this DefaultRetryPolicyProvider)`.
The DI registration stays for the CRM adapter, and the ERP adapter adds the rule inside its own
`ConfigureRetryPolicies` when `E2eSettings.IsEnabled`.

## Tests

- `tests/CrmErpDemo.AppHost.Tests/ErpRetryRuleTests.cs`: the rate-limited failure resolves a policy
  with three retries, every other catalog reason resolves none, the exception carries the
  `Retry-After` its text quotes, and the E2E rule resolves beside the adapter's.
- `samples/CrmErpDemo/e2e/tests/21-retry-rule.spec.ts`: error mode on with the reason at a 100%
  failure rate, the event ends Failed with exactly four failed attempts in its history (the first
  and three retries) and no ERP customer, then error mode off and a resubmit completes it. A retry that succeeds is not asserted
  on a timer; `12-message-lifecycle.spec.ts` covers that with the scripted rule.
- `resetFailureModes` in the e2e ERP client now also puts the reason back to the generic one, so
  the other error-mode specs never inherit a reason that is retried.

## Out of scope

- A retry rule for failures of `erp-api` itself. The handlers throw `HttpRequestException` for any
  non-2xx, and the e2e suite and the README walkthrough depend on those failing at once.
- A classifier that dead-letters permanent failures on ErpEndpoint.
