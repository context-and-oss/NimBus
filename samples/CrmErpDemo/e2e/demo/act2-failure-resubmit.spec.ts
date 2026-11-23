import { expect, test } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { waitFor } from "../helpers/wait-for.js";
import { resolveWebUrls, type WebUrls } from "./stage/demo-urls.js";
import { Pace, actChip, caption, narrate, recordFootage, spotlight, spotlightStatTile, startNarration, titleCard, typeInto, visit } from "./stage/stage.js";

// ACT 2 — Something breaks.
// The strongest segment. Raw pub/sub gives you a dead-lettered message and a
// support ticket; NimBus gives you an operator who fixes it in a browser, with
// ordering preserved through the outage. Script: ../docs/demo-video-script.md#act-2.

const CHIP = "Act 2 · Failure & recovery";
const BASE_NAME = `Contoso Logistics ${Date.now().toString().slice(-5)}`;

let webs: WebUrls;
let crm: CrmApiClient;
let erp: ErpApiClient;
let nimbus: NimBusApiClient;

recordFootage("act2-failure-resubmit.webm");

test.beforeAll(async () => {
  webs = await resolveWebUrls();
  crm = await CrmApiClient.create();
  erp = await ErpApiClient.create();
  nimbus = await NimBusApiClient.create();
  await erp.resetFailureModes();
  await erp.resetHandoffMode();
});

test.afterAll(async () => {
  await erp.resetFailureModes();
  await crm.dispose();
  await erp.dispose();
  await nimbus.dispose();
});

test("Act 2 — a failed message blocks its session, and one click drains it", async ({ page }) => {
  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  await titleCard(
    page,
    "Act 2",
    "Something breaks",
    "The downstream system falls over mid-flow. What happens to the messages already in the air?",
  );

  // ── Break the ERP on purpose ───────────────────────────────────
  await caption(
    page,
    "Break the ERP on purpose",
    "Error mode makes every inbound ERP handler throw — the 2am outage, on demand.",
  );
  const breakingIt = startNarration(page, "2.1");
  const errorToggle = page.getByRole("button", { name: /Error mode/ });
  await spotlight(errorToggle);
  await errorToggle.click();
  await expect(page.getByRole("button", { name: "Error mode: ON" })).toBeVisible();
  await caption(page, "ERP is down", "Every message the ERP receives will now throw.");
  await breakingIt;

  // ── The CRM carries on, unaware ────────────────────────────────
  await visit(page, `${webs.crmWeb}/accounts/new`, CHIP);
  await caption(
    page,
    "The CRM doesn't know, and doesn't care",
    "It saves its row and publishes, exactly as before. That is the point of decoupling.",
  );
  const unawareCrm = startNarration(page, "2.2");
  await typeInto(page.locator('label:has-text("Legal name") input'), BASE_NAME);
  await typeInto(page.locator('label:has-text("Country code") input'), "SE");
  await page.getByRole("button", { name: "Save & Close" }).click();
  await unawareCrm;
  await expect(page.locator("tr").filter({ hasText: BASE_NAME })).toBeVisible();

  const account = await waitFor(
    async () => (await crm.listAccounts()).find((a) => a.legalName === BASE_NAME && !a.isDeleted) ?? null,
    { description: `CRM account ${BASE_NAME}` },
  );

  // ── Two more edits to the same account ─────────────────────────
  await caption(
    page,
    "Two more edits, same account",
    "Both published while the first message is still failing. Same session key.",
  );
  const twoMoreEdits = startNarration(page, "2.3");
  const finalName = `${BASE_NAME} rev2`;
  for (const [i, name] of [`${BASE_NAME} rev1`, finalName].entries()) {
    await visit(page, `${webs.crmWeb}/accounts/${account.id}`, CHIP);
    await caption(page, `Edit ${i + 1} of 2`, `Renaming to "${name}".`);
    await typeInto(page.locator('label:has-text("Legal name") input'), name);
    await page.getByRole("button", { name: "Save & Close" }).click();
    await page.waitForTimeout(Pace.beat);
  }
  await twoMoreEdits;

  // ── The operator surface: failed head, deferred siblings ───────
  const failedHead = await waitFor(
    async () => {
      const events = await nimbus.searchEvents("ErpEndpoint", {
        resolutionStatus: ["Failed", "DeadLettered"],
      });
      return events.find((e) => e.sessionId === account.id) ?? null;
    },
    { timeoutMs: 180_000, intervalMs: 2000, description: `head failure for session ${account.id}` },
  );

  // Don't narrate deferral until the store actually says so — otherwise the
  // caption claims something the footage doesn't show.
  await waitFor(
    async () => {
      const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Deferred"] });
      return events.some((e) => e.sessionId === account.id) ? true : null;
    },
    { timeoutMs: 180_000, intervalMs: 2000, description: `deferred siblings for session ${account.id}` },
  );

  await visit(page, `/Endpoints/Details/ErpEndpoint?sessionId=${account.id}`, CHIP);
  await caption(
    page,
    "Failed head, deferred siblings",
    "The first message failed. The two edits behind it did NOT overtake it — they are deferred, because they share a session key.",
  );

  const headRow = page.locator("table tbody tr").filter({ hasText: failedHead.eventId.substring(0, 8) });
  await expect(headRow).toBeVisible();
  const deferredRows = page.locator("table tbody tr").filter({ hasText: "Deferred" });
  await expect(deferredRows.first()).toBeVisible();

  // 2.4 is the conceptual heart of the film. The spotlights walk the viewer's eye
  // across the tiles and rows while the line explains what they are looking at.
  const theHeartOfIt = startNarration(page, "2.4");
  await spotlightStatTile(page, "FAILED");
  await spotlightStatTile(page, "DEFERRED");
  await spotlight(deferredRows.first(), 2000);
  await spotlight(headRow, 2400);
  await theHeartOfIt;

  await caption(
    page,
    "Order is preserved through the outage",
    "Without session-aware deferral, edit 2 could have landed before the create — and the ERP would end up with the wrong state.",
  );
  // Script shot 2.5 (the message-detail view, "the actual exception, kept") has a
  // narration line but no shot here yet — this act goes straight from the list to
  // the resubmit. Narrating it over the list would claim something off screen, so
  // the line stays uncued until someone adds the drill-in.
  await page.waitForTimeout(Pace.hold);

  // ── Fix the ERP, then resubmit ─────────────────────────────────
  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  await caption(page, "Fix the underlying system", "Error mode off — the ERP is healthy again.");
  const fixingIt = startNarration(page, "2.6");
  const offToggle = page.getByRole("button", { name: /Error mode/ });
  await spotlight(offToggle);
  await offToggle.click();
  await expect(page.getByRole("button", { name: "Error mode: OFF" })).toBeVisible();
  await fixingIt;

  await visit(page, `/Endpoints/Details/ErpEndpoint?sessionId=${account.id}`, CHIP);
  await caption(
    page,
    "One click: resubmit",
    "From a browser. No redeploy, no message surgery, no support ticket.",
  );
  const oneClick = startNarration(page, "2.7");
  const rowToResubmit = page.locator("table tbody tr").filter({ hasText: failedHead.eventId.substring(0, 8) });
  await expect(rowToResubmit).toBeVisible();
  await spotlight(rowToResubmit);

  const resubmitButton = rowToResubmit.getByRole("button", { name: /Resubmit/i });
  if (await resubmitButton.count()) {
    await resubmitButton.first().click();
  } else {
    // Fallback so the act still completes if the row action moved behind a menu.
    // eslint-disable-next-line no-console
    console.warn("[act2] no inline Resubmit button found; falling back to the REST API");
    await nimbus.resubmit(failedHead.eventId, failedHead.lastMessageId);
  }
  await oneClick;

  // ── The backlog drains, in order ───────────────────────────────
  await caption(
    page,
    "The backlog drains in order",
    "The head succeeds, the session unblocks, and the parked edits replay FIFO — automatically.",
  );
  await narrate(page, "2.8");

  const finalCustomer = await waitFor(
    async () => {
      const c = await erp.findCustomerByCrmAccountId(account.id);
      return c && c.legalName === finalName ? c : null;
    },
    { timeoutMs: 180_000, intervalMs: 2000, description: `ERP customer reached "${finalName}"` },
  );
  expect(finalCustomer.legalName).toBe(finalName);

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForTimeout(1200);
  await actChip(page, CHIP);
  await caption(
    page,
    "Session clean, nothing stuck",
    "Nobody had to touch the deferred messages — resubmitting the head was enough.",
  );
  await page.waitForTimeout(Pace.hold);

  // ── Final state is correct, not just recovered ─────────────────
  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  const erpRow = page.locator("tr").filter({ hasText: finalName });
  await expect(erpRow).toBeVisible();
  await caption(
    page,
    "Final state is correct",
    "The ERP ends up on the most recent edit — not whichever message happened to win a race.",
  );
  const correctFinalState = startNarration(page, "2.9");
  await spotlight(erpRow, 2400);
  await correctFinalState;
});
