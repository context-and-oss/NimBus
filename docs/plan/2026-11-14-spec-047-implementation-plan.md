# Spec 047 (Cosmos capacity modes): implementation plan

Status: **draft** (2026-11-14). Nothing is implemented. Implements
[Spec 047](../spec/047-cosmos-capacity-modes/spec.md). Planned against master `d19894f1`
(v4.16.0). Target: a 4.x minor. Where this plan and the spec disagree, the spec wins.

## Delivery

One PR, with one commit per step: steps 1 and 2 are independent, but they share the CLI option
plumbing and the docs, and NimBus doesn't stack PRs. Each step adds its failing tests first, then
the code. The PR follows [CONTRIBUTING.md](../../CONTRIBUTING.md) and Conventional Commits.

| Step | Content | Commit |
|---|---|---|
| 0 | Phase 0: the spec's §14 checks on a temporary account | `docs(spec): record Spec 047 phase 0 results` |
| 1 | Serverless for new accounts (spec §5.1) | `feat(cli): create new Cosmos accounts as serverless` |
| 2 | Autoscale for provisioned accounts (spec §5.2) | `feat(cli): migrate provisioned Cosmos containers to autoscale` |
| 3 | Docs, pipelines and release-note draft (spec §10) | `docs: Cosmos DB capacity modes` |

Before pushing, run the Release build and tests (below).

## Step 0: Phase 0

- Create a resource group in the non-production "Visual Studio Enterprise – MPN" subscription. Use
  one provisioned and one serverless account, deployed from a local copy of the step 1 template.
  Delete the resource group at the end.
- Run the six checks of spec §14. Items 3 and 4 need a container with real throughput history:
  migrate, redeploy, migrate back, lower to 400.
- Record the commands and their output in
  `docs/spec/047-cosmos-capacity-modes/phase0-results.md`. Amend the spec where the results
  contradict it, in particular §5.1's older-`nb` note (item 2) and §5.2's template paragraph
  (item 3).
- **Stop condition.** If item 3 fails (a redeploy resets autoscale), step 2 waits until the spec's
  §5.2 is redesigned. Step 1 can still go ahead.

## Step 1: serverless for new accounts

**Template.**

- `deploy/bicep/templates/cosmosDB.bicep`: the `capacityMode` parameter, and `capabilities`
  through `union()` on the account's `properties` (spec §5.1).
- `deploy/bicep/deploy.core.bicep`: the `cosmosCapacityMode` parameter, default `Provisioned`,
  passed to the `cosmosAccount` module (`deploy.core.bicep:354`).

**CLI.**

- `Commands/InfraCommands.cs` and `Commands/SetupCommand.cs`: the `--cosmos-capacity-mode` option,
  which is refused unless the storage provider is Cosmos.
- `InfrastructureDeployer`:
  - When the Cosmos account is in `existingLocations`, read its capabilities with `az cosmosdb
    show` and pin, next to the plan pins (`InfrastructureDeployer.cs:86-90`).
  - Refuse a contradicting flag before any deployment call, with the conversion command in the
    message.
  - Pass `cosmosCapacityMode` in `DeployCoreInfrastructureAsync`.
  - Print the pin with the location pins.
- `nb setup` forwards the option to the infrastructure step.

**Tests (RED first).** A new `InfrastructureDeployerCosmosCapacityTests`, built like
`InfrastructureDeployerCapacityTests` on the fake `IAzureCliRunner`, covers the capacity-mode
cases of spec §8. Add a template test that `cosmosDB.bicep` emits `EnableServerless` only for
`Serverless`.

## Step 2: autoscale for provisioned accounts

**Intent.**

- The `--cosmos-throughput` option on `infra apply` and `setup`. It is refused for SQL Server and
  for a serverless account (known after the step 1 pin).
- A `CosmosThroughputIntent` type beside `NetworkIntent`, with tag `nimbus-cosmos-throughput` and
  values `autoscale` and `manual`. It reads the tag through `ResourceGroupTags.ReadAsync`
  (`PrivateEndpointDnsCheck.cs:310`) and writes it with the network tags' `az tag update --operation
  Merge` helper (`InfrastructureDeployer.cs:733-760`), which touches only the named tag.
- The defaults of spec §5.2: `manual` for an existing account without a tag, `autoscale` for a new
  provisioned account. The tag is written before the core deployment.

**Reconcile.**

- A new `CosmosThroughputReconciler` in `NimBus.CommandLine`. `InfrastructureDeployer` calls it
  after the core deployment succeeds.
- It lists the containers with `az cosmosdb sql container list`, then reads each one's throughput
  with `az cosmosdb sql container throughput show`. Autoscale shows as `autoscaleSettings.maxThroughput`.
- It migrates in the direction of the intent. Going back to manual, it lowers containers whose max
  was 1,000 to 400. It continues past failures and fails at the end with the list.
- It prints one line per container changed and a summary.

**Provisioner.** `EndpointContainerProvisioner` resolves the account's mode and the tag through
the same helpers, and adds `--max-throughput 1000` only for a provisioned account tagged
`autoscale` (spec §5.2 table).

**Tests (RED first).**

- `CosmosThroughputIntentTests`.
- `CosmosThroughputReconcilerTests`: only manual containers migrated, autoscale ones untouched,
  continue-then-fail, the lowering rule on the way back, nothing done on a serverless account.
- `InfrastructureDeployer` tests: the tag is written before the deployment, and the reconciler
  runs after it, only on Cosmos.
- `EndpointContainerProvisionerTests`: the four rows of the spec §5.2 table.

## Step 3: docs and pipelines

- The pages of spec §10. Fix the wrong runtime-creation claim in `docs/azure-requirements.md:13`.
- `.github/workflows/deploy.yml` and `pipelines/azure-pipelines-deploy.yml`: optional inputs for
  both flags, passed only when set. Then a pipeline run that leaves them empty behaves like a local
  run without flags.
- Draft the release-note entries (spec §10) in the PR description, following the pattern in
  [versioning](../versioning.md#cutting-a-release).
- No new published pages, so `website/sidebars.js` doesn't change. Run the portal build anyway,
  because it fails on broken links.

## Verification gate

```bash
dotnet build src/NimBus.slnx -c Release      # pin SDK 10.0.4xx if the default SDK is newer
dotnet test src/NimBus.slnx -c Release --no-build
npm --prefix website ci && npm --prefix website run build
```

- The Cosmos and SQL conformance suites don't change. CI still runs them without skips.
- After step 2, run a manual `nb infra apply --cosmos-throughput autoscale` against the Phase 0
  provisioned account, then a rerun without the flag (no changes), then `--cosmos-throughput
  manual`. Record the output in the PR.
- Run a manual `nb infra apply` against a fresh resource group without flags (serverless) and
  `nb topology apply` against it. Then confirm the endpoint containers have no throughput.
- The PR changes no WebApp UI, so it needs no screenshot.

## Out of scope

- Spec 036 (deferred) and its review findings.
- The in-request endpoint purge noted in Spec 036 §0.6, tracked as #388.
