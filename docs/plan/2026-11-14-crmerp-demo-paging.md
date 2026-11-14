# CrmErpDemo list paging

The CRM and ERP sample apps load whole tables. With a few thousand contacts the Contacts page
takes seconds to load and renders every row, and the lists reload everything every three
seconds. Page the four lists on the server, 100 rows a page: accounts and contacts in CRM,
customers and contacts in ERP.

## What loads whole tables today

| Caller | Loads | Needs |
|---|---|---|
| crm-web Accounts, Contacts; erp-web Customers, Contacts | the table, every 3 s | one page |
| crm-web Contacts, erp-web Contacts | every account or customer, for the name column | the names on the page |
| Account form and customer card, Contacts tab | every contact | the contacts of one parent |
| Contact forms, parent dropdown | every account or customer | a short list to choose from |
| crm-web simulator | every account or contact, per simulated action | any recent row |
| erp-web Home | every customer, for three counts and six recent rows | the counts and six rows |
| erp-web Customers details box | every contact, for one count | one count |
| e2e helpers `listAccounts`, `listCustomers` | the table | the table (they search it) |

## Decisions

- **Paging is opt-in with `?page=`.** A request without `page` answers as before, with every
  row. The e2e suite and the demo-video scripts search those full lists, and the suite only
  just went green again, so their contract does not change. The web apps always send `page`.
- **One response shape.** The body stays an array. A paged request adds the `X-Total-Count`
  response header. That avoids a second shape on the same route.
- **Page size 100**, the default and the maximum. `pageSize` can ask for less.
- **Filtering moves to the server**, because a client-side filter would only search the page
  in view: `q` (keyword), `deleted=false`, and per list `origin`, `accountId`, `customerId`,
  `creditHold`.
- **Order is `CreatedAt` descending, then `Id`**, so rows do not shift between pages when
  two were created in the same instant.
- **The parent's name comes with each contact** (`accountLegalName`, `customerLegalName`), so
  the Contacts lists stop loading every account or customer.
- **The parent dropdown on a contact form shows the 100 most recent parents**, plus the
  contact's current one. A dropdown of thousands was not usable before either.
- **erp-web Home gets `GET /api/customers/summary`** for its three counts, and reads its six
  recent customers as a page of six.

## Steps

1. **Contracts.** `PageRequest` (page and size normalisation, shared by both APIs) with tests.
2. **crm-api.** Paging and filters on `GET /api/accounts` and `GET /api/contacts`.
3. **erp-api.** The same on `GET /api/customers` and `GET /api/contacts`, plus the summary.
4. **crm-web.** `Pager` in the shell; Accounts and Contacts lists paged with a debounced
   server-side filter; Contacts tab, contact form and simulator on paged calls.
5. **erp-web.** The same for Customers and Contacts; views as server filters; Home on the
   summary; details box and customer card on filtered calls.
6. **Docs.** A short section in the sample README.

## Verification

- `dotnet test tests/CrmErpDemo.AppHost.Tests -c Release`.
- `npm run build` in `Crm.Web` and `Erp.Web`.
- A local run of the stack with a few hundred accounts and contacts: page boundaries, totals,
  filters, and the unpaged calls the e2e helpers make, checked over HTTP; the four lists
  looked at in a browser.
