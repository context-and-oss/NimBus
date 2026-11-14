# Review of the Spec 036 implementation plan (2026-10-07)

Reviewer: Codex (`gpt-6-sol`), read-only, static. It ran no build and no live Azure checks.
Subject: [2026-10-07-spec-036-implementation-plan.md](2026-10-07-spec-036-implementation-plan.md),
first draft, against [Spec 036](../spec/036-cosmos-single-tracking-container/spec.md) and master
`7403c596`. Claude checked each finding against the code before it was folded into the plan; the
plan's [review trail](2026-10-07-spec-036-implementation-plan.md#review-trail) records the outcome.

**Verdict: needs rework.** The plan covers the main design. However, the proposed Phase 0 gate and
the PR 3 sequence don't support the plan's claim that each PR can merge with passing Release checks.

## Blocking

1. **Phase 0 and PRs 1–9: the verification gate moves to after implementation.** Spec §9 says
   "Phase 0: Prove the platform. Nothing ships" and requires "Every item in §14 answered". In the
   plan, PR 1 ships before Phase 0. Item 3 needs PR 2's template. Items 4, 5 and 8 and the full item 9
   rehearsal are deferred to PR 9, and item 5 is supposed to decide PR 2's indexes but runs after
   PR 2. **Correction:** build the prerequisite harness and template before the PRs that depend on
   them. Where a check really needs the finished v5 command and apps, amend the spec explicitly.
   Don't mark Phase 0 exited while those checks are deferred.
2. **PR 3: marking public members `[Obsolete]` conflicts with its stated scope.** PR 3 claims to touch
   only the Cosmos project, but it obsoletes three members whose callers stay until PRs 6–7
   (`EndpointContainerProvisioner.cs:43,84`, `Container.cs:80,90`, `AdminService.Copy.cs:22,33`).
   Release treats CS0618 as an error (`Directory.Build.props:48-51`), so PR 3 breaks the Release
   build. **Correction:** obsolete the members only after those callers are replaced.
3. **PR 3: existing fake-adapter tests fail under the new guard.** The plan makes an adapter without
   `GetContainersAsync` fail closed. Existing guarded-write and completion tests use fake adapters
   and endpoint-named containers (`CosmosDbClientGuardedWriteTests.cs:28`,
   `CosmosDbClientTryCompleteTests.cs:27`). **Correction:** PR 3 converts every affected fake and
   assertion, including guard enumeration and the shared container key.
4. **Open question 1: the private-network fallback contradicts the fail-closed preflight.** Spec §6.5
   says that when the commands can't read the marker, "they refuse and explain the flag".
   **Correction:** keep the refusal and run the preflight from inside the private network, or make it
   a spec decision.

## Should-fix

5. **Gaps 2–3 / PR 1: the public API and error behavior need a spec amendment.** §5.9 lists two
   adapter members; the plan adds `GetContainersAsync` and a public `TrackingLayoutPendingException`.
   It also leaves unclear whether the default `NotSupportedException` becomes the guard's transient
   exception with §6.5's one-minute behavior. **Correction:** update §5.9 and §8, specify the
   translation, and test it.
6. **Delivery / PRs 2–7: PR 2 and PR 3 can't merge in either order.** Managed-identity deployments
   can't create the tracking container on first use (§5.6). After PR 3 the CLI and WebApp copy paths
   still address endpoint-named containers until PRs 6 and 7. "No deployment runs a master build" is
   an assumption, not a control. **Correction:** require PR 2 before PR 3, state how interim master
   builds are kept from being deployed, or move the copy paths into an earlier PR.
7. **Gap 9 / PR 3: restarting on any invalid filter token changes mutation loops.** §5.4 allows the
   restart only for the failed search. `GetEventsByFilter` drives the CLI delete, resubmit and skip
   loops (`Container.cs:133,163,228`) and WebApp bulk operations (`AdminService.Purge.cs:549`).
   Silently returning page 1 can make those loops repeat work. **Correction:** limit the restart to
   UI paging, or give mutation callers an explicit invalid-token result.
8. **PR 1 tests: two required cases are unassigned.** §8 requires a host-built-client test for
   request-level priority. §14 item 2 requires the set query ordered by `updatedAtTicks` and paged
   across endpoints. **Correction:** add both to PR 1.
9. **PR 3 guard coverage: the subscription callback.** `CosmosDbSubscriptionStore.UpdateSubscription`
   reads `ErrorList` through the tracking store (`CosmosDbSubscriptionStore.cs:201-208`).
   **Correction:** document and test whether subscription updates get the pending exception.
10. **Open question 3: the reservation release conflicts with the spec.** The spec calls prerequisite
    (b) a "4.x patch"; the plan recommends v4.7.0. **Correction:** record the minor-release decision
    in the spec and cut the maintenance branch from that tag.
11. **PR 5 copy: copied rows never expire, and the plan doesn't say so.** The copy removes `ttl`
    (`AdminService.Copy.cs:139`, `Container.cs:352`), and the target has `defaultTtl: -1`.
    **Correction:** state the retention behavior and test it, or keep a chosen remaining lifetime.

## Nit

12. **Phase 0: the named live deployment isn't confirmed by anything in the branch.**
    **Correction:** make finding the resource and confirming access the first Phase 0 step.

## Claims confirmed in the code

- Prerequisite (a) landed in `a1266587` (#196) and is in v4.3.0; the cited SQL, in-memory and
  conformance lines match.
- 27 `_getEndpointContainer` call sites: 4 endpoint-state, 7 lookup, 6 search, 10 write.
- `ICosmosDatabaseAdapter` can't list containers.
- The WebApp maps no `StorageProviderTransientException` to an HTTP response.
- `CosmosDbHealthCheck` is registered through `AddCheck<T>` and has two public constructors.
- The account resource uses API `2022-05-15`, the containers `2021-06-15`, and CI the `vnext-latest`
  emulator.
- Release promotes compiler warnings, CS0618 included, to errors.
- The cited purge endpoints, CLI provisioning calls, API generation path and test names match.
