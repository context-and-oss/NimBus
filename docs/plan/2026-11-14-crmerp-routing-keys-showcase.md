# CrmErpDemo routing-keys showcase

Show routing keys ([Spec 045](../spec/045-routing-keys/spec.md), [guide](../routing-keys.md))
in `samples/CrmErpDemo`: one source, several targets of the same kind, each receiving only its
own share. CRM account events carry the account's country as the routing key, and regional hub
endpoints each take the accounts of their own countries.

## Decisions

- **Scenario: regional hubs**, not an ERP instance per country. Splitting the ERP would mean a
  second erp-api, erp-web, database and adapter, and re-routing the credit-check request and the
  credit-hold command by key. The hubs show the same feature for one small worker project.
- **Key: `CountryCode`** on `CrmAccountCreated` and `CrmAccountUpdated`. `CrmAccountDeleted`
  carries no country and stays unrouted.
- **Three endpoints, one adapter.** `NordicHubEndpoint` (DK, SE, NO, FI), `DachHubEndpoint`
  (DE, AT, CH) and `UnroutedHubEndpoint` (the unmatched consumer). `RegionalHub/` is one worker
  project; the AppHost starts it three times (`hub-nordic`, `hub-dach`, `hub-unrouted`) and sets
  `RegionalHub__Endpoint` on each. Three instances cost three container apps in Azure, but they
  are what the guide describes: each endpoint with its own subscription, sessions and failures.
- **`ErpEndpoint` is unchanged.** It declares no keys, so it keeps receiving every account and
  the CRM → ERP → CRM round-trip does not depend on the hubs.
- **The unrouted hub dead-letters**, with the built-in `AddUnmatchedHandler<T>()`. That is the
  guide's behaviour and a visible moment in nimbus-ops. To keep it deliberate, crm-web's
  **Generate fake data** and simulator produce hub countries only.
- **A regional hub enforces its keys on receive** with `EnforceRoutingKeys(...)`.
- **crm-web explains the routing** under the Country code field, from `GET /api/regional-hubs`
  on crm-api. crm-api reads the hub list from the endpoint declarations (`RegionalHubs` in the
  contracts), so the hint cannot disagree with the topology.
- **No hub handler side effects.** A hub logs what it received. What matters is which endpoint
  the message reached, which nimbus-ops shows.

## Steps

1. **Contracts.** `[RoutingKey(nameof(CountryCode))]` on the two account events;
   `Endpoints/RegionalHubEndpoints.cs` with the three endpoints and the `RegionalHubs` catalog;
   register them in `CrmErpPlatformConfiguration`.
2. **Adapter.** `RegionalHub/` worker: resolves its hub from `RegionalHub:Endpoint`, registers the
   account handlers and the receive check, or the unmatched handlers for the unrouted hub.
3. **AppHost.** Three `RegionalHub` project resources, waiting for the provisioner, pinned to
   one replica in publish mode.
4. **crm-api and crm-web.** `GET /api/regional-hubs`; `RegionalHubHint` on the account form;
   hub countries in `fakeData.ts`.
5. **Tests.** `RegionalHubRoutingTests`: routing-key validation of the real catalog, each hub's
   keys, the forward rules on the `CrmEndpoint` topic, and `RegionalHubs.ForCountry`.
6. **Docs.** A "Showcase: Routing keys" section in the sample README.
7. **Demo-video scripts.** `act3` and `act4` create accounts in `NL` and `US`; move them to hub
   countries (`NO`, `DK`) so the film does not gain a dead-letter.

## Not in this change

- The nightly Playwright suite (`e2e/tests`) gets no routing spec, and its tests keep their
  `US`, `GB` and `FR` accounts. Those now also produce a dead-letter on `UnroutedHubEndpoint`,
  which the suite's failure baseline does not watch.
- Deploying the hubs to the Azure demo. The three container apps are new resources, so that
  needs a full `aspire deploy`, not an image update. It would also be the first run of routing
  keys against a real Service Bus namespace.

## Verification

- `dotnet test tests/CrmErpDemo.AppHost.Tests -c Release`.
- `npm run build` in `Crm.Web`.
- A local run of the stack on the in-repo emulator: accounts in DK, DE and FR, then two country
  changes, checked per endpoint through the nimbus-ops API.
