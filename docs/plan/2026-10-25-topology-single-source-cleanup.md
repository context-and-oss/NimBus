# Topology single-source cleanup

Date: 2026-10-25. Scope: bounded refactorings from a maintainability review; no behavior,
public API or wire changes.

## Problem

`TopologyDescriptor` is meant to be the only declaration of the Service Bus topology. Two
hand-written copies remain, and each says in a comment that it must be kept byte-identical by
hand:

- `samples/CrmErpDemo/CrmErpDemo.Contracts/EmulatorTopologyConfigBuilder.cs` (376 lines) builds
  the Microsoft emulator's `UserConfig` JSON. Since the CrmErpDemo AppHost switched to
  `AddNimBusServiceBusEmulator` (spec 027), nothing calls it. Only its own tests and one doc
  link refer to it. Spec 027 ASP-4 already planned to retire it.
- `EndpointManagement.ClearEndpoint` (published `NimBus.Management.ServiceBus`) recreates the
  endpoint subscription's settings and its `to-{id}`, `continuation` and `retry` rules.
  `POST /api/endpoint/{endpointName}/purge` still calls it. The WebApp's other rebuild paths
  already go through `ITopologyRebuilder`.

The drift risk is real: the copies that each re-derived the topology once left out
`{endpoint}-reply`, and `nb topology remove-deprecated` then deleted it (3559f38).

## Changes

1. Delete `EmulatorTopologyConfigBuilder` and `EmulatorTopologyConfigBuilderTests`, and fix the
   partner-interop doc that links to it.
2. Make `EndpointImplementation.PostEndpointPurgeAsync` delete the subscription and rebuild it
   with `ITopologyRebuilder.EnsureSubscriptionAsync(endpoint,
   TopologyDescriptor.EndpointSubscription(endpoint))`. The end state is the same; failures
   still throw. Add a regression test. `ClearEndpoint` stays as public API, unchanged; whether
   to deprecate it is a separate decision. `Akaule.NimBus.WebApp` publishes
   `EndpointImplementation`, so its constructor keeps its signature: the rebuilder is resolved
   from the request's services, where the WebApp always registers it. A second constructor as a
   bridge would make DI constructor selection ambiguous (see `docs/versioning.md`).
3. `ResolverService.DetermineEndpoint`: replace the remarks, which describe Broker and
   Publisher-role rules the code doesn't have. Keep the `(endpointId, role)` return value: its
   tests are the only coverage of the stored `EndpointRole`, and moving that coverage isn't
   worth the churn for an internal method.

## Left unchanged

- `StrictMessageHandler`: the per-request flows look alike, but each difference is deliberate
  and commented (Spec 041 fixes #276/#277/#278). Merging them would risk ordering bugs for
  little gain.
- Request-type lists (`ResolverService.IsRequest`, `StaleWriteGuard.IsControlRequest`,
  `StalePendingReconciler`): each is a different set with a different meaning, not one
  duplicated rule.
- The purge endpoint's `env!.Equals("prod", …)` throws when `Environment` is unset. That denies
  the purge, so it fails safe; changing it is a security-relevant behavior change and is
  reported, not made.

## Verification

`dotnet build src/NimBus.slnx -c Release` (SDK 10.0.402), `dotnet test src/NimBus.slnx -c
Release --no-build`. The new purge test must fail before the change.
