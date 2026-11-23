import { expect, test } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { waitFor } from "../helpers/wait-for.js";
import { resolveWebUrls, type WebUrls } from "./stage/demo-urls.js";
import { Pace, actChip, caption, narrate, recordFootage, spotlight, startNarration, titleCard, typeInto, visit } from "./stage/stage.js";

// ACT 1 — The happy path.
// Establishes what the system is and that integration works, so the audience has
// a baseline to lose in Act 2. Script: ../docs/demo-video-script.md#act-1.

const CHIP = "Act 1 · Happy path";
const LEGAL_NAME = `Acme GmbH ${Date.now().toString().slice(-5)}`;

let webs: WebUrls;
let crm: CrmApiClient;
let erp: ErpApiClient;

recordFootage("act1-happy-path.webm");

test.beforeAll(async () => {
  webs = await resolveWebUrls();
  crm = await CrmApiClient.create();
  erp = await ErpApiClient.create();
  // Every act opens from a known-clean failure state.
  await erp.resetFailureModes();
  await erp.resetHandoffMode();
});

test.afterAll(async () => {
  await crm.dispose();
  await erp.dispose();
});

test("Act 1 — an account created in CRM becomes a customer in ERP", async ({ page }) => {
  await visit(page, `${webs.crmWeb}/accounts`, CHIP);
  // The opening line runs across the title card and on into the first shot, so the
  // film starts on a voice rather than on four seconds of silent card.
  const openingLine = startNarration(page, "1.1");
  await titleCard(
    page,
    "Act 1",
    "The happy path",
    "A CRM and an ERP. Separate databases, separate APIs, separate hosting models.",
  );
  await openingLine;
  await caption(
    page,
    "CRM: where it starts",
    "Somebody creates a customer in the CRM. Nothing in this UI knows about messaging.",
  );
  await narrate(page, "1.2");

  // ── Create the account on camera ───────────────────────────────
  await page.getByRole("link", { name: "New account" }).click();
  await caption(page, "A plain business form", "Legal name, country, tax ID.");

  // The form fill runs under the narration — a silent typing shot is dead air.
  const describingTheForm = startNarration(page, "1.3");
  await typeInto(page.locator('label:has-text("Legal name") input'), LEGAL_NAME);
  await typeInto(page.locator('label:has-text("Country code") input'), "DE");
  await typeInto(page.locator('label:has-text("Tax ID") input'), "DE-811907980");
  await describingTheForm;

  await caption(
    page,
    "Save publishes CrmAccountCreated",
    "The CRM API writes its row and publishes the event — that is the whole integration contract.",
  );
  await page.waitForTimeout(Pace.read);
  await page.getByRole("button", { name: "Save & Close" }).click();

  // ── Watch the ERP sync column flip ─────────────────────────────
  const row = page.locator("tr").filter({ hasText: LEGAL_NAME });
  await expect(row).toBeVisible();
  await caption(
    page,
    "ERP sync: pending…",
    "The account is saved. The event is on its way across the bus.",
  );
  // Narrate over the spotlight and the wait: 1.4 -> 1.5 is the money shot and the
  // camera must not cut away, but the flip can arrive at any point in between.
  const pendingLine = startNarration(page, "1.4");
  await spotlight(row);
  await pendingLine;

  // The accounts list self-polls every 3s, so the flip happens without a reload.
  await expect(row).toContainText("✓", { timeout: 120_000 });
  await caption(
    page,
    "The ERP customer number, back in the CRM",
    "The ERP created a customer, published its own event back, and the CRM stamped the customer number on its row.",
  );
  const roundTripLine = startNarration(page, "1.5");
  await spotlight(row, 2200);
  await roundTripLine;

  // Resolve the ids we need for the ERP and nimbus-ops shots.
  const account = await waitFor(
    async () => (await crm.listAccounts()).find((a) => a.legalName === LEGAL_NAME && !a.isDeleted) ?? null,
    { description: `CRM account ${LEGAL_NAME}` },
  );
  const customer = await waitFor(async () => await erp.findCustomerByCrmAccountId(account.id), {
    timeoutMs: 120_000,
    description: `ERP customer for account ${account.id}`,
  });
  expect(customer.legalName).toBe(LEGAL_NAME);
  expect(account.erpCustomerId).toBeTruthy();

  // ── The same customer, on the ERP side ─────────────────────────
  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  await caption(
    page,
    "ERP: the same customer, arrived by event",
    "A different database, a different API, an Azure Functions adapter — tagged with the origin it came from.",
  );
  const erpRow = page.locator("tr").filter({ hasText: LEGAL_NAME });
  await expect(erpRow).toBeVisible();
  const erpSideLine = startNarration(page, "1.6");
  await spotlight(erpRow, 2200);
  await erpSideLine;

  // ── The audit trail ────────────────────────────────────────────
  await visit(page, `/Endpoints/Details/ErpEndpoint?sessionId=${account.id}`, CHIP);
  await caption(
    page,
    "Every message, audited by session",
    "This is what separates NimBus from raw Service Bus: a full audit trail, keyed by session, that nobody had to write logging code for.",
  );
  await narrate(page, "1.7");
  await actChip(page, CHIP);
  await caption(
    page,
    "One account, one session, in order",
    "Same session key on both events, so the round-trip can never be processed out of order.",
  );
  await page.waitForTimeout(Pace.hold);
});
