# ADR-017: One Cosmos DB Tracking Container, Partitioned by Endpoint

## Status
Proposed (2026-09). Supersedes [ADR-008](008-per-endpoint-cosmos-containers.md) when accepted.
Target release: v5.0.0. The repo owner settled the open questions on 2026-09-28 (Spec 036 §13).
The spec was revised on 2026-10-05 after a review. The design, migration and evaluation are in
[Spec 036](../spec/036-cosmos-single-tracking-container/spec.md).

## Context

ADR-008 gave every endpoint its own Cosmos container for its tracking rows, partitioned by `/id`.
Each benefit it cited has either lapsed or can be had another way:

- **Isolated queries.** A query for one endpoint doesn't read other endpoints' data. A partition key
  that starts with the endpoint gives the same routing inside one container.
- **Throughput per endpoint.** Never used. Nothing sets throughput on an endpoint container, so each
  one carries the 400 RU/s minimum of a dedicated container, busy or idle.
- **Purge as a container delete.** The deployed apps authenticate with Entra data-plane RBAC, which
  can't create or delete containers. Purge and the lazy re-creation that follows it depend on
  account keys.
- **Single-endpoint copy.** The copy tools already use filtered queries.

Meanwhile its costs grew:

- Every endpoint needs a control-plane provisioning step (`nb topology apply`). Under managed
  identity, the first message on an unprovisioned endpoint fails.
- Endpoint ids double as container names: 13 reserved names, checked at five call sites.
- Throughput cost scales with the number of endpoints.
- An account holds at most 500 databases and containers, which caps the endpoint count.
- Containers were never an access boundary. Cosmos roles are assigned at account scope, and
  per-endpoint authorization lives in the WebApp.
- The SQL Server provider (ADR-010) already keeps every endpoint's rows in one table with an
  `EndpointId` column.

Options considered:

1. **Keep per-endpoint containers on shared database throughput.** Rejected. Microsoft advises
   against shared database throughput for most workloads, caps it at 25 containers, and an existing
   database can't switch. It also fixes none of the provisioning problems.
2. **Keep per-endpoint containers on a serverless account.** Rejected. A provisioned account can't
   be converted to serverless, and the provisioning problems remain.
3. **One container partitioned by `/endpointId`.** Rejected. It caps each endpoint at 20 GB and
   10,000 RU/s.
4. **One container partitioned by `/id`, with the endpoint folded into the row id.** Rejected. It
   changes ids the API exposes, and every endpoint query fans out to all partitions.
5. **One container with a hierarchical key `/endpointId`, `/sessionId`, `/id`.** Rejected. Some
   lookups receive only the composite row id, from which the session can't be recovered reliably.
6. **One container with a hierarchical key `/endpointId`, `/id`.** Chosen.

## Decision

- **One container.** `unresolvedevents` holds every endpoint's tracking rows. Bicep declares it
  with TTL on (`defaultTtl: -1`, item-level expiry, as before) and autoscale throughput, with a
  default max of 4,000 RU/s. The name matches the SQL Server table.
- **Partition key.** The container uses the hierarchical key `/endpointId` then `/id`. Every row
  carries a top-level `endpointId`, which the store writes from the `endpointId` argument. Row ids
  (`{eventId}_{sessionId}`) are unchanged.
- **Endpoint-scoped access.** The store reaches the container only through endpoint scopes. A scope
  builds the full key for point operations. It also builds and runs every query, with
  `c.endpointId = @endpointId` as the first conjunct. The predicate is equality, never a prefix
  match, and it alone scopes and routes the query. The failed search and its histogram read several
  endpoints in one query through a set form of the scope, `ARRAY_CONTAINS(@endpointIds,
  c.endpointId)`, ordered by a stamped `updatedAtTicks`. That replaces the per-endpoint merge. The
  copy tools go through the same scopes.
- **Purge.** Purging an endpoint deletes its rows through a paged query and paced deletes, as a
  WebApp background operation, because it can outlast an HTTP request. Delete by partition key is in
  preview and accepts only full keys on hierarchical containers.
- **Provisioning.** `nb topology apply` and `nb setup` stop provisioning Cosmos containers.
  Endpoint ids no longer need to avoid container names.
- **Priority.** The account turns on priority-based execution. The WebApp, purges and the migration
  run at low priority, so the Resolver's writes go first when the shared budget runs short.
- **Migration.** Existing Cosmos deployments migrate once, offline, in v5.0.0, with
  `nb container migrate`. The command copies every row during the outage, converges on rerun,
  verifies per status and never touches the legacy containers, which stay in place for rollback
  until an operator deletes them. A layout guard keeps v5 apps from serving tracking data until a
  migration marker covers every legacy container, with no legacy writes past the recorded
  high-water mark. `nb deploy apps` and `nb setup` check it before deploying, and a rollback resets
  the marker.
- **Storage page.** Topology → Storage stays so operators can delete migrated
  legacy containers. A later 5.x minor deprecates it, and v6.0.0 removes it, together with its API
  and the WebApp's Cosmos DB Operator role assignment.
- **Out of scope.** The `messages` and `audits` containers, the other platform containers and the
  storage contracts' members and signatures are unchanged. A prerequisite fix made
  `GetEventsByFilter`'s endpoint filter exact on the SQL Server and in-memory providers, which
  matched it by prefix; it shipped in v4.3.0 (#196). The WebApp API changes only for the two purge
  operations, which return `202` (or `409` while a purge runs, `503` when its start can't be
  audited), and for a `503` on tracking operations while the migration is pending.

## Consequences

### Positive
- New endpoints need no Cosmos provisioning. The first-message failure under managed identity and
  the reserved-name rules go away.
- Tracking throughput cost follows aggregate load. With 20 endpoints, a fixed ~$467 a month at the
  400 RU/s minimum becomes one autoscale container at ~$35 to ~$350 a month (US list prices).
- Idle endpoints' capacity is available to busy ones, and the cross-endpoint failed search is one
  query against one container.
- Once the Storage page is retired, the WebApp no longer needs a control-plane Cosmos
  role.
- Endpoint-scoped queries stay routed to the endpoint's partitions as the container grows, and no
  endpoint can hit the 20 GB logical partition limit.
- Purge works under data-plane RBAC.
- The Cosmos layout matches the SQL Server provider, so their conformance behaviour converges.
- The 500-resource account limit no longer caps the number of endpoints.

### Negative
- **Shared budget.** A burst on one endpoint, a large WebApp scan or a purge can throttle Resolver
  writes for every endpoint. Mitigations: autoscale, paced purges, and priority-based execution
  with the WebApp, purges and the migration at low priority, which is best effort with no SLA.
- **Isolation in code.** Endpoint isolation depends on the scoped accessor. A unit test over every
  recorded query and cross-endpoint conformance tests guard it.
- **Lost options.** It gives up per-endpoint data-plane role assignments and resource tokens (unused
  today), per-endpoint throughput and TTL, and per-endpoint restore.
- **Slower purge.** Purge costs RUs in proportion to the endpoint's rows, runs in the background
  and can stop part-way. Deleting a container was free and all-or-nothing.
- **Hot-endpoint ceiling.** One endpoint's rows share a physical partition, and Cosmos divides
  RU/s evenly across partitions. So one endpoint tops out at the autoscale max divided by the
  physical partitions, capped at 10,000 RU/s: 4,000 RU/s at the default. Before, the ceiling was
  its container's provisioned RU/s.
- **Emulator dependence.** CI depends on the emulator's hierarchical-key support, which is recent.
  Spec 036 Phase 0 verifies it before any code lands.
- **Migration outage.** Existing deployments take a planned outage while the migration copies
  every row. The Resolver is paused through its trigger setting, and v5 is deployed before the
  copy.
- **Divergence from DIS.** Cosmos store changes from DIS, which keeps per-endpoint containers, no
  longer port mechanically.
