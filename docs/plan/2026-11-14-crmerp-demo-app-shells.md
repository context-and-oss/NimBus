# CrmErpDemo web app shells

Give `samples/CrmErpDemo/Crm.Web` the shell of a model-driven sales app and
`samples/CrmErpDemo/Erp.Web` the shell of a business-central-style role center, and move every
demo control out of the page body into one **Demo controls** pane per app.

The layouts borrow interaction patterns only. The apps keep their own names ("CRM Demo",
"ERP Demo"), colours and icons, with no vendor logos or product names.

## Decisions

- The Demo controls pane is **open by default**. Closing it is remembered per browser
  (`localStorage`), so a fresh browser, such as a Playwright demo run, always starts with it open.
- An active failure mode is always visible with the pane closed: a red or amber pill in the app
  bar plus the existing banners.
- Stay on Tailwind; no Fluent UI dependency. No API changes.
- CRM keeps Accounts as its landing page; no separate dashboard in this change.
- ERP gets a Home role center at `/` with count tiles and the notification alerts. `/customers`
  stays the customer list.
- Credit-hold customers are shown with a filter on the customer list, not a new page.

## Steps

1. **CRM shell.** App bar, left site map, `CommandBar`, `DemoPane` holding the simulator,
   CRM API outage (with circuit state), partner leads and data reset. Accounts and Contacts
   lists use the new grid style and status chips. Account and contact forms get a record
   header, a command bar (Save, Delete) and the audit log as a timeline beside the fields.
2. **ERP shell.** App bar, top navigation, action bar, `DemoPane` holding service mode, error
   mode and reason, pending handoff (with in-flight jobs), processing delay and data reset.
   Home role center with cue tiles (customers, from CRM today, credit holds, in-flight
   handoffs, alerts) and an alerts part. Customer list with a FactBox for the selected row;
   customer card with FastTab sections.

## Constraints from the demo-video scripts

`e2e/demo/act*.spec.ts` drive these UIs by accessible name, and Playwright locators are
strict, so each of these must stay **unique** on its page and keep its text:

- Buttons `Save`, `Run ERP credit check`, `Place credit hold in ERP`, `Error mode: ON/OFF`
  (matched by `/Error mode/`), `Pending-handoff mode: ON/OFF` (matched by
  `/Pending-handoff mode/`); link `New account`. No other button may contain those strings,
  so the app-bar status pills are not buttons and there is no "Save & close".
- `label:has-text("Legal name"|"Country code"|"Tax ID") input`, `#handoff-duration`,
  `credit-check-result`, `credit-hold-badge`, the text `In-flight handoff jobs` and
  `Hold command sent`.
- Lists remain `<table>` rows; routes `/accounts`, `/accounts/new`, `/accounts/:id`,
  `/contacts`, `/customers` stay; page titles keep "CRM Demo" / "ERP Demo".

The CI e2e suite (`e2e/tests`) drives the APIs and the NimBus WebApp, not these UIs.

## Verification

- `npm run build` (tsc + Vite) for both apps.
- Run the CrmErpDemo AppHost and check every page in a browser, with the pane open and closed,
  and with each mode on.
- Screenshots in the PR description. Re-cut the demo video separately.
