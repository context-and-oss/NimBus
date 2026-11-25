# Reference implementation: CRM and ERP

The [CrmErpDemo sample](../samples/CrmErpDemo/README.md) is the reference implementation of
integrations on NimBus. Two business systems, a CRM and an ERP, keep accounts, customers and
contacts in sync through Azure Service Bus. Around them sit an analytics sink, an agent that
enriches new contacts, regional hubs routed by country and an external partner that does not
use NimBus at all.

Every NimBus pattern in the sample is wired the way a production integration would wire it, so
the sample doubles as a template: copy the shape that fits your system, then read the linked
guide for the details.

## Architecture

[![CrmErpDemo reference architecture: CRM and ERP exchange events through Service Bus topics, with the NimBus platform above and further subscribers below](images/crmerp-reference-architecture.svg)](images/crmerp-reference-architecture.svg)

Select the diagram to open it full size. The
[interactive version](https://context-and-oss.github.io/NimBus/reference-implementation.html)
lets you select a component to see what it does, or trace each flow below step by step.

The diagram has four layers.

- **CRM and ERP** are the business systems. Each has a web UI, an API that owns its data, a SQL
  database and an adapter. The API publishes the system's own events; the adapter receives the
  other system's events and applies them through the API.
- **Azure Service Bus** has one topic per endpoint. Each topic's subscriptions decide where an
  event goes: a session subscription for the endpoint's own adapter, forwarding subscriptions
  that copy events to the topics of their consumers, and the subscriptions NimBus uses for
  auditing (`Resolver`), parked messages (`Deferred`, `DeferredProcessor`) and replies
  (`CrmEndpoint-reply`).
- **The NimBus platform** is shared by every integration. The provisioner creates the topology,
  the Resolver records every message in the message store, and nimbus-ops is where operators see
  and recover failed work.
- **More subscribers** show that adding a consumer is a declaration, not a code change in the
  producer: an analytics sink, an agent zone, three regional hubs, and a partner that speaks raw
  CloudEvents.

## Components

| Component | Hosting | Role |
| --- | --- | --- |
| `crm-web`, `erp-web` | Vite and React SPA | User interfaces for each system. |
| `crm-api` | ASP.NET Core | Owns CRM data. Publishes CRM events directly to the CrmEndpoint topic. |
| `erp-api` | ASP.NET Core | Owns ERP data. Writes ERP events to a SQL outbox in the same transaction as the data and hosts the dispatcher that sends them. |
| `crm-adapter` | .NET worker | Receives ERP events in session order and applies them through `crm-api`. Deduplicates with a SQL inbox and reads partner CloudEvents. |
| `erp-adapter` | Azure Functions | Receives CRM events in session order and applies them through `erp-api`. Retries a rate-limited call with a NimBus retry rule and answers the credit-check request. |
| `dataplatform-adapter` | Azure Functions | Terminal subscriber for `ErpCustomerCreated`. Keeps its inbox in Azure Table Storage. |
| `agent-zone` | NimBus subscriber | Parks every new CRM contact as Pending (Handoff) for an agent. |
| `enrichment-agent` | Demo agent | Pulls parked contacts through the agent REST API on nimbus-ops, classifies them and publishes an enriched event. |
| `hub-nordic`, `hub-dach`, `hub-unrouted` | NimBus subscriber | One project started three times; each receives only the CRM accounts for its countries. |
| `partner-portal` | Console app, no NimBus | Simulated external partner that sends and receives raw CloudEvents. |
| `provisioner` | Console app (Container Apps job in Azure) | Creates every topic, subscription and filter rule from the platform configuration. |
| `resolver` | Azure Functions | Records every message, its outcome and its session state in the message store. |
| `nimbus-ops` | NimBus WebApp | Operator UI, operator MCP endpoint and agent REST API. |

## Patterns it demonstrates

| Pattern | Where in the sample | Guide |
| --- | --- | --- |
| Session-ordered processing | `[SessionKey]` on every event; both adapters receive from session subscriptions | [Message flows](message-flows.md) |
| Direct publishing | `crm-api` publishes after `SaveChangesAsync` | [Building adapters](building-adapters.md) |
| Transactional outbox | `erp-api` commits the entity and the outbox row together | [Building adapters](building-adapters.md) |
| Worker and Functions hosting with the same handlers | `crm-adapter` is a worker, `erp-adapter` is an Azure Functions app | [Azure Functions hosting](azure-functions-hosting.md) |
| Consumer inbox | SQL inbox in `crm-adapter`, Table Storage inbox in `dataplatform-adapter` | [Consumer inbox](inbox-pattern.md) |
| Retry rules | `erp-adapter` retries a rate-limited pricing call three times | [Error handling](error-handling.md) |
| Blocked sessions, resubmit and skip | Any handler failure; recovered in nimbus-ops | [Deferred messages](deferred-messages.md) |
| Pending handoff | `erp-api` handoff mode and the agent zone | [Pending handoff](pending-handoff.md) |
| Request/reply and commands | `ErpCreditCheckRequested` and `PlaceCustomerOnCreditHold` | [Building adapters](building-adapters.md) |
| Routing keys | CRM account events routed to a regional hub by country | [Routing keys](routing-keys.md) |
| CloudEvents interoperability | `partner-portal` exchanges events without NimBus | [CloudEvents interoperability](cloudevents.md) |
| Circuit breaker | `crm-adapter` pauses receiving while `crm-api` is down | [Circuit breaker](circuit-breaker.md) |
| Alerts and failure classification | Alert mail and webhooks, and agent-recorded failure reasons | [Notification channels](notifications.md), [Integration Intelligence](integration-intelligence.md) |

## Flows

### Account round trip

One account travels to ERP and comes back as an acknowledgment that links the two records, in
order, on one session.

1. A CRM user creates an account in `crm-web`. `crm-api` saves it in the crm database.
2. `crm-api` publishes `CrmAccountCreated` to the CrmEndpoint topic, with the account id as the
   session key.
3. The ErpEndpoint forwarding subscription copies the event to the ErpEndpoint topic.
4. `erp-adapter` receives it in session order and calls `PUT /api/customers/by-crm/{id}` on
   `erp-api`.
5. `erp-api` saves the customer and stages `ErpCustomerCreated` in its outbox in the same SQL
   transaction. The dispatcher publishes it within a second.
6. Forwarding subscriptions copy it to CrmEndpoint and DataPlatformEndpoint. `crm-adapter` links
   the CRM account to the new ERP customer through `crm-api`.
7. The Resolver records every message and outcome in the message store, so nimbus-ops shows the
   whole chain.

### Customer created in ERP

ERP publishes once and CRM creates a matching account. No acknowledgment is needed because CRM
had no record to link.

1. An ERP user creates a customer in `erp-web`.
2. `erp-api` writes the customer and `ErpCustomerCreated` (Origin = Erp) to the erp database in
   one transaction.
3. The outbox dispatcher publishes the event to the ErpEndpoint topic.
4. Forwarding subscriptions copy it to CrmEndpoint and DataPlatformEndpoint.
5. `crm-adapter` creates the account through `PUT /api/accounts/external/{erpId}`, and
   `dataplatform-adapter` logs it as an analytics sink.

### Failure and resubmit

When a handler fails, NimBus blocks the session instead of letting later events for the same
account overtake the failed one.

1. `erp-adapter` receives `CrmAccountCreated`, but `erp-api` is down and the call fails.
2. The Resolver records the message as Failed. Its session, the account id, is now blocked:
   later events for that account wait as Deferred.
3. An operator finds the blocked session in nimbus-ops, fixes the cause and selects Resubmit.
4. nimbus-ops publishes the message again to the ErpEndpoint topic, and `erp-adapter` processes
   it.
5. The `DeferredProcessor` subscription replays the waiting messages and the session unblocks.
   Every operator action is in the audit trail.

### Partner lead over CloudEvents

An external system with no NimBus code exchanges events with the sample in both directions.

1. `partner-portal` sends `PartnerLeadSubmitted` as a raw CloudEvents 1.0 message to the plain
   `PartnerInbound` topic.
2. A second receiver in `crm-adapter` reads it in AutoDetect mode and creates a Partner-origin
   contact through `crm-api`.
3. In the other direction, ERP publishes its events as binary-mode CloudEvents, and
   `partner-portal` reads them from the plain `PartnerPortalCapture` subscription on the
   ErpEndpoint topic.

### Agent enrichment

New CRM contacts are parked for an agent, which classifies them and publishes an enriched event
through the NimBus agent API.

1. Every `CrmContactCreated` is also forwarded to the AgentZoneEndpoint topic.
2. `agent-zone` parks it as Pending (Handoff) instead of processing it.
3. `enrichment-agent` pulls the parked work through the agent REST API on nimbus-ops and
   classifies the contact.
4. The agent publishes `crm.contact.enriched.v1` and settles the parked message. A dynamic
   forward delivers the new event to DataPlatformEndpoint.

### Regional routing by country

Routing keys send each account event to the one hub that serves its country, without a separate
event type per region.

1. `CrmAccountCreated` and `CrmAccountUpdated` carry the account's country code as a routing key.
2. The hub forwarding rules filter on it: DK, SE, NO and FI go to NordicHubEndpoint; DE, AT and
   CH go to DachHubEndpoint.
3. An account in any other country goes to UnroutedHubEndpoint, which dead-letters it with a
   reason that names the country.

## Event routes

Each event has exactly one producer, named by its prefix. The provisioner turns these
declarations in `CrmErpDemo.Contracts` into topics, forwarding subscriptions and filter rules.

| Event | Producer | Consumers | Session key |
| --- | --- | --- | --- |
| `CrmAccountCreated`, `CrmAccountUpdated` | CrmEndpoint | ErpEndpoint, plus one regional hub chosen by country | `AccountId` |
| `CrmAccountDeleted` | CrmEndpoint | ErpEndpoint | `AccountId` |
| `CrmContactCreated` | CrmEndpoint | ErpEndpoint, AgentZoneEndpoint | `ContactId` |
| `CrmContactUpdated`, `CrmContactDeleted` | CrmEndpoint | ErpEndpoint | `ContactId` |
| `ErpCreditCheckRequested` (request) | CrmEndpoint | ErpEndpoint, which answers on `CrmEndpoint-reply` | `AccountId` |
| `PlaceCustomerOnCreditHold` (command) | CrmEndpoint | ErpEndpoint only; a second consumer fails provisioning | `AccountId` |
| `ErpCustomerCreated` | ErpEndpoint | CrmEndpoint, DataPlatformEndpoint | `AccountId` |
| `ErpCustomerUpdated`, `ErpCustomerDeleted` | ErpEndpoint | CrmEndpoint | `AccountId` |
| `ErpContactCreated`, `ErpContactUpdated`, `ErpContactDeleted` | ErpEndpoint | CrmEndpoint | `ContactId` |
| `PartnerLeadSubmitted` | partner-portal | CrmEndpoint, read from the plain `PartnerInbound` topic | `LeadId` |
| `crm.contact.enriched.v1` (dynamic) | AgentZoneEndpoint | DataPlatformEndpoint, through a dynamic forward | — |

## Local run and Azure

The same Aspire AppHost runs the sample on your machine and deploys it to Azure Container Apps.

| Concern | Local run | Azure |
| --- | --- | --- |
| Service Bus | NimBus Service Bus emulator, in process | Azure Service Bus; services connect with managed identity |
| Databases | SQL Server container with `crm`, `erp` and `nimbus` databases; Cosmos DB optional for the message store | Azure SQL with Entra-only authentication |
| Hosting | Aspire on your machine | Azure Container Apps; stateful apps pinned to one replica |
| Sign-in | Local development bypass | NimBus Identity on nimbus-ops, `crm-api` and `erp-api` |
| Topology | Provisioner runs before the other services start | Container Apps job, started once after each deploy |
| Alerts | Mailpit catches alert mail | Alert mail and webhooks off |

To run it locally, from the repository root with a container runtime running:

```bash
npm --prefix samples/CrmErpDemo/Crm.Web install
npm --prefix samples/CrmErpDemo/Erp.Web install
aspire run --apphost samples/CrmErpDemo/CrmErpDemo.AppHost/CrmErpDemo.AppHost.csproj
```

Wait for `provisioner` to complete in the Aspire dashboard. The sample's
[README](../samples/CrmErpDemo/README.md) has the smoke tests for each showcase and the steps
to deploy it to Azure.

## Where to start in the code

| Concern | File |
| --- | --- |
| Event contracts | [`CrmErpDemo.Contracts/Events`](../samples/CrmErpDemo/CrmErpDemo.Contracts/Events/CrmAccountCreated.cs) |
| Endpoints and the platform catalog | [`CrmErpPlatformConfiguration.cs`](../samples/CrmErpDemo/CrmErpDemo.Contracts/CrmErpPlatformConfiguration.cs) |
| Publishing after a database save | [`Crm.Api/Endpoints/AccountEndpoints.cs`](../samples/CrmErpDemo/Crm.Api/Endpoints/AccountEndpoints.cs) |
| Entity and outbox in one transaction | [`Erp.Api/OutboxScope.cs`](../samples/CrmErpDemo/Erp.Api/OutboxScope.cs) |
| Worker subscriber with inbox | [`Crm.Adapter/Program.cs`](../samples/CrmErpDemo/Crm.Adapter/Program.cs) |
| Functions subscriber with a retry rule | [`Erp.Adapter.Functions/Program.cs`](../samples/CrmErpDemo/Erp.Adapter.Functions/Program.cs) |
| Request/reply and a command | [`Crm.Api/Endpoints/ErpIntegrationEndpoints.cs`](../samples/CrmErpDemo/Crm.Api/Endpoints/ErpIntegrationEndpoints.cs) |
| External partner without NimBus | [`PartnerPortal/Program.cs`](../samples/CrmErpDemo/PartnerPortal/Program.cs) |
| Service wiring for local and Azure | [`CrmErpDemo.AppHost/Program.cs`](../samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs) |
