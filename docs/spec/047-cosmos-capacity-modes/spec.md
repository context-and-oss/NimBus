# Spec 047 — Cosmos DB capacity: serverless accounts and autoscale containers

Status: **proposed** (2026-11-14). Nothing is implemented. Target release: a 4.x minor.
Implementation plan: [2026-11-14-spec-047-implementation-plan.md](../../plan/2026-11-14-spec-047-implementation-plan.md).
Baseline: master `d19894f1` (v4.16.0).
Replaces the cost goal of [Spec 036](../036-cosmos-single-tracking-container/spec.md), which is
deferred (Spec 036 §0).
Scope: `deploy/bicep/templates/cosmosDB.bicep`, `deploy/bicep/deploy.core.bicep`, the `nb` CLI
(`infra apply`, `setup`, `topology apply`), `deploy.yml`, the Azure Pipelines template and docs.
Unchanged: the Cosmos store, the container layout (ADR-008), the storage contracts, the WebApp,
the Resolver and the SQL Server provider.

## 1. Summary

A NimBus Cosmos account is provisioned today: every container carries dedicated manual throughput,
400 RU/s, whether it is busy or idle. That covers the 13 platform containers plus one container per
endpoint. This spec adds two cheaper capacity modes:

- **Serverless for new accounts.** `nb infra apply` and `nb setup` create new Cosmos accounts as
  serverless by default. A serverless account bills the request units used and has no
  per-container minimum. An existing account keeps its mode, which `nb infra apply` reads and pins.
- **Autoscale for provisioned accounts.** A new `--cosmos-throughput autoscale` option, recorded as
  a resource-group tag, migrates every manual container in `MessageDatabase` to autoscale with a
  1,000 RU/s maximum. Autoscale bills at least 10% of the maximum, so an idle container costs
  about 63% less. New endpoint containers are then created with autoscale. This is the lever for
  the existing Cosmos deployments, which can't become serverless in place.

No data moves, the layout stays, and no release has to be a major.

## 2. Current state

| Aspect | Today (`d19894f1`) |
|---|---|
| Account | `cosmosDB.bicep` declares `Microsoft.DocumentDB/databaseAccounts@2022-05-15`: Standard offer, Session consistency, one region, no `capabilities` (`cosmosDB.bicep:14-35`). It is always provisioned |
| Platform containers | `messages`, `audits` and 11 shared containers, plus the 2 Integration Intelligence containers when that feature is on (`cosmosDB.bicep:47-154`). None declares `options`, so each gets 400 RU/s of manual throughput |
| Endpoint containers | `nb topology apply` creates one per catalog endpoint with `az cosmosdb sql container create` and no throughput option (`EndpointContainerProvisioner.cs:85-97`), so each gets 400 RU/s manual. Lazy creation through the SDK (key auth, emulator) sets no throughput either |
| Code that sets throughput | None in `src/`, `deploy/`, `pipelines/` or `.github/` |
| Existing-resource pinning | `nb infra apply` lists the resource group's resources and pins locations and plans of existing ones (`InfrastructureDeployer.cs:78-90, 320`). Intent that the resources can't show is kept as resource-group tags (`ResourceGroupTags`, `NetworkIntent`, `ResourceNaming.PostfixTag`) |
| Docs | `docs/azure-requirements.md:13` says "Standard offer (provisioned throughput)" and that per-endpoint containers are created at runtime by the apps. The second is wrong under managed identity; `nb topology apply` creates them. No page states what the containers cost |

At US list prices ($0.008 per 100 RU/s-hour manual, single region), 400 RU/s costs about $23.36 a
month. So a provisioned NimBus account costs at least:

| Endpoints | Containers | Minimum per month |
|---|---|---|
| 0 | 13 | ~$304 |
| 10 | 23 | ~$537 |
| 20 | 33 | ~$771 |
| 50 | 63 | ~$1,472 |

Storage ($0.25 per GB-month) comes on top in every mode and isn't discussed further.

## 3. Goals

1. A new Cosmos deployment has no per-container throughput minimum.
2. An existing provisioned deployment can cut its per-container floor without moving data.
3. No rerun of `nb infra apply`, `nb setup` or `nb topology apply` changes an account's capacity
   mode or a container's throughput mode unless the operator asks.
4. Nothing in the store, the WebApp or the Resolver changes.

## 4. Non-goals

- Converting an existing provisioned account to serverless. Cosmos can't do that; it means a new
  account and moving every container's data.
- Spec 036's single tracking container.
- Shared database throughput. It caps a database at 25 containers, which 13 platform containers
  and a dozen endpoints reach, and an existing database can't switch to it.
- Per-endpoint throughput tuning, or raising autoscale maxima automatically.
- Multi-region accounts. The template deploys one region today.

## 5. Design

### 5.1 Capacity mode of the account

**Bicep.** `cosmosDB.bicep` gains `@allowed(['Provisioned', 'Serverless']) param capacityMode
string = 'Provisioned'`. The account gets `capabilities: [{ name: 'EnableServerless' }]` when it
is `Serverless`, and omits `capabilities` otherwise, through `union()` as the shared containers do
with `defaultTtl` (`cosmosDB.bicep:146-153`). Provisioned accounts get exactly the payload they get
today. `deploy.core.bicep` passes a new `cosmosCapacityMode` parameter, default `Provisioned`, so a
direct Bicep deployment against an existing account changes nothing. The template's default stays
provisioned; the CLI picks serverless (below).

The containers need no change. None declares throughput, which is what a serverless account
requires.

**CLI.** `nb infra apply` and `nb setup` gain `--cosmos-capacity-mode <serverless|provisioned>`,
used only with `--storage-provider cosmos`:

| Account | Flag | Effect |
|---|---|---|
| Doesn't exist | absent | `Serverless` |
| Doesn't exist | given | The flag |
| Exists | absent | The account's current mode, read with `az cosmosdb show --query "capabilities[].name"` (serverless when `EnableServerless` is listed). The run prints the pin, like the location pins |
| Exists | same as the account | Same as absent |
| Exists | differs | Refuses before deploying. Provisioned to serverless is impossible in place. Serverless to provisioned is a supported one-way conversion (§14 item 5); the message names the `az` command, and after it the pin follows the account |

The account itself records the intent. ARM can't add or drop `EnableServerless` on an existing
account, so the pin can't be flipped by a rerun or a transitional state, and no tag is needed.

**Why serverless is the new default:**

- **Cost follows use.** It bills about $0.25 per million RUs (US list) and has no minimum. A
  deployment whose Resolver and WebApp use 100 million RUs a month pays about $25 a month for
  throughput, against the $304 to $771 floor in §2.
- **It is reversible.** A serverless account can convert to provisioned later. A provisioned one
  can never become serverless. Starting serverless keeps both options open.
- **It covers NimBus's shape.** Many small containers with bursty, uneven load. A serverless
  container starts at 5,000 RU/s and gains 5,000 RU/s per physical partition.

**When to choose provisioned.** The operator passes `--cosmos-capacity-mode provisioned` for:

- sustained load where serverless costs more. A sustained 1 RU/s costs about $0.66 a month on
  serverless. The break-even against the account's autoscale floor (§5.2, $8.76 per container) is
  therefore an average of about 13 RU/s per container across the account. For 33 containers that
  is about 440 RU/s around the clock, or 1.1 billion RUs a month. Against today's manual floor
  it is about 35 RU/s per container. Phase 0 checks the arithmetic against current prices (§14
  item 6);
- bursts above what a serverless container's partitions deliver;
- a future multi-region account, which serverless doesn't support;
- predictable throughput. Serverless gives no throughput guarantee; §14 item 6 records its
  current latency and availability terms.

`docs/storage-providers.md` gets this as a short decision table.

**Older `nb` against a serverless account.** A `nb` from before this spec deploys the account
without `capabilities`. What ARM does with that on a serverless account decides the release note
(§14 item 2): if the deployment fails, the note says to upgrade every `nb` and pipeline that
targets a serverless deployment. `deploy.yml` builds `nb` from the dispatched ref, so it picks the
change up automatically.

### 5.2 Autoscale for provisioned accounts

**Intent.** `nb infra apply` and `nb setup` gain `--cosmos-throughput <autoscale|manual>`, valid
only for a provisioned Cosmos account. The run records the choice as the resource-group tag
`nimbus-cosmos-throughput` before it deploys. It writes the tag with the same `az tag update
--operation Merge` the network tags use (`InfrastructureDeployer.cs:733-760`), which touches only
the named tag, and later runs read it through `ResourceGroupTags.ReadAsync`. Later runs without
the flag use the tag. With neither flag nor tag:

- an existing account stays `manual`, which is today's behaviour;
- a new provisioned account gets `autoscale`, and the run writes the tag.

The tag is the persisted intent. Container state can't serve as intent, because a run that stops
part-way leaves containers in both modes.

**Reconcile after the deployment.** When the intent is `autoscale`, `nb infra apply` lists the
containers in `MessageDatabase` once the Bicep deployment has finished, so containers the template
has just created are included:

- For each container on manual throughput, it runs `az cosmosdb sql container throughput migrate
  --throughput-type autoscale`. Cosmos picks the starting max: the largest of 1,000 RU/s, the
  current manual RU/s, a tenth of the highest RU/s ever provisioned, and 10 × storage in GB,
  rounded to the nearest 1,000
  ([autoscale FAQ](https://learn.microsoft.com/azure/cosmos-db/autoscale-faq)). A default 400 RU/s
  container therefore lands at 1,000, and a container an operator raised keeps at least what it
  had.
- Containers already on autoscale are left alone, so an operator's tuned max survives.
- Endpoint containers are included. The account is NimBus's, so every container in
  `MessageDatabase` is in scope, including orphans of removed endpoints and an opt-in inbox
  container. The run lists each container it migrates and the max it got.
- A failure on one container is reported, and the run continues with the next one. The run then
  fails at the end. A rerun migrates only what is still manual.

**Back to manual.** `--cosmos-throughput manual` records the tag and migrates each autoscale
container back. Cosmos sets the manual RU/s to the autoscale max, so a 1,000 max becomes 1,000
manual, which costs more than either. The run then lowers each container whose max was exactly
1,000 to 400 RU/s, the value it had before (§14 item 4 confirms 400 is allowed after the round
trip). It leaves containers with any other max at that value and says so, because an operator chose
them.

**New endpoint containers.** `EndpointContainerProvisioner` reads the tag and the account's mode,
both of which `nb topology apply` can see:

| Account | Tag | `az cosmosdb sql container create` |
|---|---|---|
| Serverless | — | No throughput option (as today) |
| Provisioned | `autoscale` | `--max-throughput 1000` |
| Provisioned | `manual` or absent | No throughput option, so 400 RU/s manual (as today) |

Lazy creation through the SDK isn't changed. It only works with account keys, which means local
development and the emulator, where throughput doesn't matter.

**Cost.** Autoscale bills, for each hour, the highest RU/s it scaled to, never below 10% of the
max, at 1.5 × the manual rate ($0.012 per 100 RU/s-hour):

| Container | Manual 400 RU/s | Autoscale max 1,000 |
|---|---|---|
| Idle every hour | $23.36 | $8.76 |
| At its ceiling every hour | $23.36, throttled at 400 RU/s | $87.60, throttled at 1,000 RU/s |

A container is billed more only in the hours it uses more than 267 RU/s, and in those hours it
gets up to 2.5 times the capacity. For the 20-endpoint deployment in §2, the idle floor drops from
about $771 to about $289 a month.

**Template interaction.** Bicep keeps declaring the platform containers without `options`. A
container redeployed without `options` must keep its autoscale setting. That is the one Azure
behaviour this half of the spec depends on, and Phase 0 checks it before anything merges (§14
item 3). If Cosmos resets it after all, the reconcile pass migrates the container again on the same
run, but billing would flap. In that case the template has to declare `autoscaleSettings` for
existing autoscale containers, and the design changes before implementation.

### 5.3 What doesn't change

- The store, its container layout, its SDK calls and the WebApp's Topology → Storage page.
- `nb topology apply` on SQL Server deployments.
- The CrmErpDemo, the AppHost and the emulator. They don't provision Cosmos throughput.
- Existing accounts without the new flags. A rerun of `nb infra apply` passes `Provisioned` and
  `manual`, which reproduces today's deployment exactly.

## 6. Existing deployments

| Deployment | What to do |
|---|---|
| Cosmos, provisioned (every existing one) | Optionally run `nb infra apply --cosmos-throughput autoscale`. It is online and moves no data. Containers that run near their ceiling most hours cost more; check Normalized RU Consumption per container first |
| Cosmos, wants serverless | Not supported in place. It needs a new account and every container's data copied. Out of scope |
| SQL Server | Nothing |

The release notes describe the new default for new accounts under Compatibility.

## 7. Evaluation

**Gains.** New deployments drop a $300-plus fixed monthly floor, plus about $23 per endpoint.
Existing deployments can cut their idle floor by about 63%, online. Both changes stay in Bicep and
the CLI, ship in a minor, and leave the store, ADR-008 and the DIS alignment alone.

**Costs and risks.**

- **Serverless throughput.** There is no throughput guarantee, and the ceiling depends on
  partitions. Throttling works as it does on provisioned accounts: the SDK retries 429s, then
  `CosmosExceptionTranslation` turns them into `StorageProviderTransientException`, and the
  Resolver reschedules the message (`ResolverService.cs:178`). Heavy deployments choose
  provisioned (§5.1).
- **Autoscale can cost more** for a container that runs above 267 RU/s most hours. It is opt-in for
  existing accounts, and the decision table says how to check.
- **A pin read on every run.** `nb infra apply` reads the account's capabilities: one more `az`
  call per run.
- **Older `nb`** against a serverless account (§5.1, §14 item 2).
- **Template interaction** (§5.2, §14 item 3).

**Alternatives.**

| Alternative | Verdict |
|---|---|
| Spec 036, one tracking container | Deferred (Spec 036 §0). It removes the endpoint containers' floors, not the platform ones, and needs a major release and an offline migration |
| Shared database throughput | Rejected (§4) |
| Serverless as an opt-in, provisioned the default | Rejected. New deployments would keep paying the floor by default, and a provisioned account can never become serverless later |
| Autoscale declared in Bicep for every container | Rejected. A template can't switch an existing manual container to autoscale, so existing accounts would still need the migrate step, and the template would need to know each container's state |
| Autoscale on by default for existing accounts | Rejected. It changes an existing deployment's bill without the operator asking |

## 8. Tests

**CLI (`tests/NimBus.CommandLine.Tests`), beside `InfrastructureDeployerCapacityTests` and
`InfrastructureDeployerNetworkTests`:**

- A new Cosmos account gets `cosmosCapacityMode=Serverless` without the flag, and the flag's value
  when it is given.
- An existing account is pinned to its mode in both directions. A contradicting flag refuses
  before any `az deployment` call.
- SQL Server deployments pass neither parameter and read nothing from Cosmos.
- `--cosmos-throughput` is refused on a serverless account and on SQL Server.
- The tag is written before the deployment and read on a later run without the flag. Neither flag
  nor tag gives `manual` on an existing account and `autoscale` on a new provisioned one.
- The reconcile pass migrates only manual containers and leaves autoscale ones alone. It continues
  past a failing container and fails the run at the end. It does nothing on a serverless account.
- Back to manual: containers with a max of 1,000 end at 400, and others keep their max.
- `EndpointContainerProvisionerTests`: `--max-throughput 1000` with the `autoscale` tag on a
  provisioned account; no throughput option on a serverless account, or with `manual`, or with no
  tag.

**Template (`BicepTemplateProviderTests` or a new template test):** `cosmosDB.bicep` emits
`EnableServerless` only for `Serverless` and no `capabilities` for `Provisioned`, and
`deploy.core.bicep` passes the parameter through. `CosmosBicepContainerSyncTests` stays as is.

**Live, run by hand and recorded in the PR:** the §14 checks.

No store, conformance or WebApp tests change.

## 9. Phases

| Phase | Content | Exit |
|---|---|---|
| 0 | The §14 checks on a temporary account in a non-production subscription, deleted afterwards. No NimBus deployment is needed | Results recorded in this folder; the spec amended where they contradict it |
| 1 | Serverless for new accounts (§5.1), docs | Release build green; §14 items 1, 2 and 5 confirmed |
| 2 | Autoscale for provisioned accounts (§5.2), docs | Release build green; §14 items 3 and 4 confirmed |

Phases 1 and 2 are independent and can ship in the same minor or in consecutive ones.

## 10. Documentation

- `docs/storage-providers.md`: a "Cosmos DB capacity" section with the cost floor, the two modes,
  the decision table (§5.1) and the autoscale caveat (§5.2).
- `docs/azure-requirements.md`: the account row gives the capacity mode. It drops "per-endpoint
  containers are created at runtime by the apps" and says `nb topology apply` creates them.
- `docs/cli.md`: `--cosmos-capacity-mode` and `--cosmos-throughput` on `infra apply` and `setup`,
  and the `nimbus-cosmos-throughput` tag.
- `docs/deployment.md`: the choice at first deployment, and switching an existing deployment to
  autoscale.
- `.github/workflows/deploy.yml` and `pipelines/azure-pipelines-deploy.yml`: optional inputs for
  both flags, passed through only when set.
- Release notes: the new default for new accounts under Compatibility, both options under Added,
  and the older-`nb` note if §14 item 2 calls for it.

## 11. Residual risks

1. A serverless deployment that outgrows serverless has to convert to provisioned, which is one-way
   and takes a while. The decision table and the release notes say so.
2. Autoscale costs more for containers that are busy most hours (§5.2).
3. A container redeployed without `options` might not keep its autoscale setting (§14 item 3).
   Phase 0 settles this before Phase 2 is built.

## 12. Decisions

1. **Serverless is the default for new Cosmos accounts** (§5.1).
2. **Autoscale is opt-in for existing accounts and the default for new provisioned ones** (§5.2).
3. **Autoscale max of 1,000 RU/s** for containers that NimBus migrates or creates. It is the
   lowest max Cosmos allows, which gives the cheapest floor at 2.5 times today's per-container
   ceiling.

The repo owner confirms these when approving the spec.

## 13. Open questions

None.

## 14. Facts to verify before implementing (Phase 0)

On a temporary account in a non-production subscription, deleted afterwards:

1. **Serverless through the template.** `cosmosDB.bicep` with `EnableServerless` at API
   `2022-05-15` deploys a serverless account with every platform container. A rerun is a no-op
   (`az deployment group what-if`). With `publicNetworkAccess: Disabled` and a private endpoint, the
   Spec 034 private mode works on a serverless account.
2. **Older `nb` against serverless.** Deploying the current template, without `capabilities`,
   against a serverless account: does it fail, or leave the account unchanged? This decides the
   release note (§5.1).
3. **Autoscale survives a redeploy.** Migrate a template-declared container to autoscale, then
   redeploy the template, which declares it without `options`. The container must still be
   autoscale with the same max.
4. **Migration values.** `az cosmosdb sql container throughput migrate --throughput-type autoscale`
   on an empty 400 RU/s container is online and yields a max of 1,000. Migrating back yields 1,000
   manual, which can then be lowered to 400. `az cosmosdb sql container create --max-throughput
   1000` works on a provisioned account and is rejected on a serverless one.
5. **Serverless to provisioned.** The current conversion command (`az cosmosdb update
   --capacity-mode Provisioned` or its successor) works. Afterwards `capabilities` no longer lists
   `EnableServerless`, so the pin of §5.1 follows the account.
6. **Prices and terms.** Current US list prices for serverless RUs, manual and autoscale
   throughput, and serverless's per-container and per-partition throughput, latency and
   availability terms. These go into §2, §5.1, §5.2 and the docs as Phase 0 finds them.

## 15. References

- [Serverless](https://learn.microsoft.com/azure/cosmos-db/serverless) and
  [service quotas](https://learn.microsoft.com/azure/cosmos-db/concepts-limits): one region,
  5,000 RU/s per physical partition, 500 databases plus containers per account.
- [Change from serverless to provisioned](https://learn.microsoft.com/azure/cosmos-db/how-to-change-capacity-mode):
  one-way.
- [Autoscale FAQ](https://learn.microsoft.com/azure/cosmos-db/autoscale-faq): the starting max on
  migration, the billing floor and the lowest allowed max.
- [`az cosmosdb sql container throughput`](https://learn.microsoft.com/cli/azure/cosmosdb/sql/container/throughput):
  `migrate` and `update`.
- [Understanding your bill](https://learn.microsoft.com/azure/cosmos-db/understand-your-bill).
- ADR-008, Spec 034 (private networking, resource-group tags as intent), Spec 036 §0 (the
  evaluation that led here).
