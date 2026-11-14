# Private networking

How to run NimBus with every component inside your virtual network: Service Bus, the
message store, the Functions storage account and both apps reached only through private
endpoints, all outbound traffic through your firewall, and nothing answering the public
internet. The design, its reasoning and what is still to come are in
[Spec 034](spec/034-private-networking/spec.md); this page is the how-to.

Private mode is opt-in. Without `--network-mode private`, `nb` deploys exactly what it always
has.

## What changes in private mode

| Component | Public mode (default) | Private mode |
|---|---|---|
| Service Bus | Standard, public | **Premium**, private endpoint, public access off |
| Cosmos DB or Azure SQL | Public (SQL allows every Azure tenant) | Private endpoint, public access off, SQL allow-all rule deleted |
| Functions storage | Public | Private endpoints for blob, queue and table (plus file on Elastic Premium) |
| Resolver | Not in a VNet | VNet-integrated, private endpoint for its Kudu/scm site |
| WebApp | Public site and Kudu | VNet-integrated with all traffic routed, reached through its private endpoint |
| Deployments | Any machine, GitHub-hosted runners | A runner or agent inside the network |

Application Insights telemetry still leaves through your firewall to the public Azure
Monitor endpoints in this release (`--monitor-private-link none`); private monitoring
arrives later.

## Before you start

### Subnets

You provide three subnets. `nb` never changes your VNet; it checks each subnet before it
deploys anything.

| Subnet | Option | Delegation | Size |
|---|---|---|---|
| Private endpoints | `--private-endpoint-subnet-id` | none | /27 is ample; can be shared with other workloads |
| Resolver integration | `--resolver-subnet-id` | Flex Consumption: `Microsoft.App/environments`. Elastic Premium: `Microsoft.Web/serverFarms` | Flex /27 (one app); EP /24 recommended. No `_` in the name on Flex |
| WebApp integration | `--webapp-subnet-id` | `Microsoft.Web/serverFarms` | /26 recommended, /28 minimum |

The two integration subnets must be in the apps' region. The private-endpoint subnet may be
in another region; the endpoints are created in its VNet's region. NSGs on the Resolver
subnet must allow 443 and 445 to the file endpoint on Elastic Premium.

Flex Consumption is the preferred Resolver plan in private mode: it needs no Azure Files
content share, uses its identity for storage, and scales on the private Service Bus
natively.

### DNS

Choose who owns the `privatelink.*` zones with `--private-dns`:

| Mode | Use it when | What `nb` does |
|---|---|---|
| `create` | The VNet is dedicated to NimBus, or for evaluation | Creates the zones in the NimBus resource group and links them to the private-endpoint subnet's VNet (or `--private-dns-link-vnet-id`, repeatable). Links fall back to public resolution for names they have no record for |
| `existing` | Hub-and-spoke with zones in a hub resource group | Registers records in the zones under `--private-dns-zone-scope /subscriptions/<id>/resourceGroups/<rg>`. The deploying identity needs Private DNS Zone Contributor there |
| `external` | Your Azure Policy or DNS servers write the records | Creates endpoints without DNS registration and waits for the records to appear |

Do not use `create` in a VNet that other workloads share: a linked zone answers for every
name of its type in that VNet.

Every network whose DNS carries one of these zones (adapter VNets linked to your hub zones,
for example) needs the NimBus records too. Once a private endpoint exists, the public name
points into the zone, and a zone without the record answers NXDOMAIN even while public
access is still on.

### Egress

Both apps route **all** outbound traffic into the VNet, including managed-identity token
requests. Allow these before the first private deployment, or the apps lose Service Bus,
the store and storage the moment they join the VNet:

| Destination | Caller | Why |
|---|---|---|
| Microsoft Entra ID (service tag `AzureActiveDirectory`) | Both apps | Managed-identity tokens; WebApp sign-in |
| Azure Resource Manager (`AzureResourceManager`) | WebApp (Cosmos) | Container administration |
| Azure Monitor (`AzureMonitor`) | Both apps | Telemetry, with `--monitor-private-link none` |
| `api.typesafe.ai` | WebApp | Only if Integration Intelligence (TypeSafe) is enabled |

### Where `nb` runs

Topology provisioning and app deployment talk to Service Bus and to each app's Kudu site,
which private mode only exposes through private endpoints. Run `nb` from inside the
network: a self-hosted GitHub runner or Azure DevOps agent in (or peered to) the VNet, a
GitHub larger runner with Azure private networking, or an Azure DevOps Managed DevOps Pool
with VNet injection. The runner must resolve the privatelink zones and reach the
private-endpoint subnet, and needs egress to Azure Resource Manager, Entra ID, GitHub or
Azure DevOps, and NuGet.

Before each of those steps `nb` checks that the runner resolves the private endpoints (see
[Pre-flight check](#pre-flight-check)).

### Resource providers

Register `Microsoft.Network` (and `Microsoft.App` for Flex Consumption) in the subscription.
`nb` warns when it finds them unregistered.

### Permissions for the deploying identity

On top of the [usual resource-group roles](azure-requirements.md#rbac-for-the-deploying-identity-pipeline-or-user):

| Scope | Needs | Why |
|---|---|---|
| The three subnets (or their VNet) | Read, and `Microsoft.Network/virtualNetworks/subnets/join/action`, e.g. **Network Contributor** | Validate the subnets, place the private endpoints, join the apps to the VNet |
| The hub DNS resource group (`--private-dns existing`) | **Private DNS Zone Contributor** | Register the endpoints' records in your zones |
| The VNets to link (`--private-dns create`) | `Microsoft.Network/virtualNetworks/join/action` | Link the NimBus zones to them |
| The NimBus resource group | Tag write (included in Contributor) | Record the network setup |

## A new private environment

```bash
nb setup --solution-id acme --environment prod --resource-group rg-acme-prod \
  --network-mode private \
  --private-endpoint-subnet-id /subscriptions/<id>/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-acme/subnets/snet-private-endpoints \
  --resolver-subnet-id /subscriptions/<id>/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-acme/subnets/snet-resolver \
  --webapp-subnet-id /subscriptions/<id>/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-acme/subnets/snet-management \
  --private-dns existing \
  --private-dns-zone-scope /subscriptions/<id>/resourceGroups/rg-hub-dns \
  --monitor-private-link none
```

A fresh deployment is created locked: each data service is created with public access off,
and each app joins the VNet only after the private endpoints it depends on exist.

## Moving an existing deployment

### 1. Service Bus tier

Private endpoints need Premium, and Azure cannot convert a Standard namespace in place. `nb`
refuses private mode on a Standard namespace. Deploy a Premium namespace next to it under a
new name and move your adapters:

1. Quiesce publishers and let the Resolver and subscribers drain. Service Bus migrations do
   not copy messages, so scheduled retries, deferred and dead-lettered messages must be
   processed, resubmitted from the store, or knowingly written off; the WebApp's
   **Topology → Subscriptions** view shows what remains.
2. Run the transition pass below with `--service-bus-namespace-name sb-acme-prod-premium`.
   `nb` records the name, and `nb topology apply` provisions the topology on it.
3. Re-point your adapters to the new namespace, then delete the old one.

Spec 034 §6 describes Microsoft's in-place migration and the drain-and-recreate alternative
for non-production environments.

### 2. Two passes

A single switch would lock the data services before the apps join the VNet, so `nb` refuses
it for an existing deployment unless you pass `--skip-transition` and accept the downtime.
Use two passes instead.

**Pass 1: add the private paths, keep public access on.**

```bash
nb setup ... --network-mode private --allow-public-access \
  --private-endpoint-subnet-id ... --resolver-subnet-id ... --webapp-subnet-id ... \
  --private-dns existing --private-dns-zone-scope ... --monitor-private-link none
```

This creates the private endpoints, the DNS records and the VNet integration. Every public
path stays open, including storage's firewall default and the SQL allow-all rule.

**Check.** From both apps (Kudu console, Resolver logs) and from your adapter networks,
confirm the NimBus names resolve to private addresses.

**Pass 2: lock.**

```bash
nb setup ... --network-mode private
```

`nb` reuses the recorded subnets and DNS settings, checks once more that the runner resolves
every private endpoint, and only then turns public access off everywhere and deletes the SQL
allow-all rule.

## What `nb` records

Before it deploys, `nb infra apply` records the network setup as tags on the resource group:
`nimbus-network-mode` (`public`, `private-transition` or `private`), the subnet ids, the DNS
mode and zone scope, `nimbus-network-dns-link-vnet-<n>`, the monitoring choice and
`nimbus-service-bus-namespace`. So:

- a rerun without network options reproduces the recorded setup, including an unfinished
  transition;
- an option you pass overrides only that setting;
- `nb topology apply` follows the recorded namespace name;
- a run interrupted halfway converges on the next run.

`nb` never silently opens a resource: if Service Bus is more locked down than the record
says, it stops and asks for an explicit `--network-mode`. Deployments that never used private
mode get no tags. If an Azure Policy forbids tag changes on the resource group, `nb` stops
and says so; pass the full set of network options on every run instead.

Every network resource `nb` creates also carries the tag
`nimbus-deployment=<solution>-<environment>`; the rollback deletes only resources that do.

## Pre-flight check

Before provisioning the topology, deploying the apps, or locking a deployment at the end of
the transition, `nb` reads each private endpoint's host names and address from its network
interface and checks that this machine resolves the names to that address. It retries for up
to `--dns-wait` minutes (default 10; `0` checks once), because records written by a policy
and caching DNS forwarders can lag. It then names each host that still fails:

| Diagnosis | Meaning |
|---|---|
| Resolved to a public address | This machine does not use the private zone: run `nb` inside the network, or link its VNet to the zone |
| No record | The records are not written yet, or this machine's DNS cannot see the zone |
| Another private address | A stale record points elsewhere |

In the transition state a mismatch is only a warning, because public access still works.

Every endpoint must report at least one host name with a private address. An endpoint that
reports none, for example one created moments ago, fails the check instead of being skipped:
an empty list would otherwise pass without testing anything.

## Rolling back

```bash
nb setup ... --network-mode public
```

1. Both deployments reopen public access while the private endpoints still exist, so clients
   keep working on either path.
2. VNet integration is removed from both apps.
3. The private endpoints tagged `nimbus-deployment=<solution>-<environment>` are deleted,
   with the DNS records Azure wrote for them.
4. In `create` mode, the NimBus-tagged VNet links are deleted, then each NimBus zone left
   with no other links. A zone someone else has linked is kept.

Nothing without the tag is touched: your VNets, subnets and `existing` or `external` zones
stay. Leaving private mode also records `nimbus-network-cleanup=pending` on the resource
group, and `nb` removes it only once the cleanup has finished, so an interrupted rollback
continues on the next run. A deployment that never had a private network never runs the
cleanup, even if it records a namespace override. Any lookup that fails stops the cleanup
before anything else is deleted; rerun the same command to continue. Not reversed: the Premium namespace, and DNS records your own DNS
servers hold (remove those yourself).

## Pipelines

**GitHub Actions** ([Deploy NimBus](../.github/workflows/deploy.yml)):

- Set the `runs-on` input to a runner inside the network.
- Set the network setup as variables on the GitHub environment:
  - the mode and subnets: `NB_NETWORK_MODE`, `NB_PRIVATE_ENDPOINT_SUBNET_ID`,
    `NB_RESOLVER_SUBNET_ID`, `NB_WEBAPP_SUBNET_ID`;
  - DNS: `NB_PRIVATE_DNS`, `NB_PRIVATE_DNS_ZONE_SCOPE`, and `NB_PRIVATE_DNS_LINK_VNET_IDS`
    (space-separated);
  - the rest: `NB_MONITOR_PRIVATE_LINK`, `NB_SERVICE_BUS_CAPACITY`,
    `NB_SERVICE_BUS_NAMESPACE_NAME`, `NB_DNS_WAIT`.
- The `allow-public-access` and `skip-transition` inputs are per run: tick
  `allow-public-access` for pass 1 only.

**Azure DevOps** ([azure-pipelines-deploy.yml](../pipelines/azure-pipelines-deploy.yml)):

- Set `agentPool` to a pool inside the network.
- The same settings are pipeline parameters, including `allowPublicAccess` and
  `skipTransition`.

## After the switch

- Operators browse the WebApp's usual `*.azurewebsites.net` address from a network that
  resolves the privatelink zone (VPN, ExpressRoute, Bastion). The Entra sign-in setup does not
  change.
- Portal tools that call the data plane stop working from outside the network: Cosmos Data
  Explorer, Service Bus Explorer, Log Analytics queries with query access off, and Functions
  "Code + Test".
- Customer adapters must be VNet-integrated, resolve `privatelink.servicebus.windows.net`, and
  be allowed AMQP (5671/5672) to the private-endpoint subnet. Adapters on Functions Elastic
  Premium also need runtime scale monitoring switched on.
- The `nb endpoint` and `nb container` operator commands must run inside the network too.

## Not in this release

- Private monitoring (`--monitor-private-link existing` or `create`) and workspace-based
  Application Insights.
- A NimBus-created VNet for evaluation.
- SQL with managed identity, local authentication off, and Key Vault references.
- Support behind a reverse proxy: the login rate limiter's `TrustedProxyHops` setting.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `The Service Bus namespace '…' is on the Standard tier` | Private endpoints need Premium. Deploy a new namespace with `--service-bus-namespace-name` ([Service Bus tier](#1-service-bus-tier)). |
| `Switching an existing public deployment straight to private …` | Run the transition pass with `--allow-public-access` first, or pass `--skip-transition` to accept downtime. |
| `… must be delegated to Microsoft.App/environments …` | The Resolver subnet's delegation must match the Resolver plan: Flex needs `Microsoft.App/environments`, Elastic Premium `Microsoft.Web/serverFarms`. |
| `… has public network access disabled, but the recorded network mode is …` | Service Bus is more locked down than the record. Pass `--network-mode private` to keep it, or `--network-mode public` to reopen it. |
| `Stopped before …: this machine does not reach the private endpoints` | See [Pre-flight check](#pre-flight-check). Run `nb` from inside the network or fix the DNS it reports. |
| `Could not record the network setup as tags …` | A policy blocks tag changes on the resource group. Allow the `nimbus-network-*` tags, or pass all network options on every run. |
| Apps fail right after joining the VNet | Egress to Entra ID is blocked: all-traffic routing sends managed-identity token requests through your firewall ([Egress](#egress)). |
