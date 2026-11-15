import { expect, test } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { waitFor } from "../helpers/wait-for.js";
import { resolveWebUrls, type WebUrls } from "./stage/demo-urls.js";
import { Pace, actChip, caption, narrate, recordFootage, spotlight, spotlightStatTile, startNarration, titleCard, typeInto, visit } from "./stage/stage.js";

// ACT 3 — The slow external system.
// The scenario nobody else handles well: the downstream system accepts the work
// and finishes it minutes later. Models Dynamics/DMF-style imports.
// Script: ../docs/demo-video-script.md#act-3.
//
// Shot order note: the two sibling edits are published immediately after the
// create, before the operator-surface shots. The handoff window is capped at the
// slider's 60s maximum, and the nimbus-ops shots alone burn more than half of it
// — doing the edits later means the handoff has already settled and nothing
// defers, which would make the "siblings wait" caption a lie.
//
// Narration makes that budget tighter, so every line between the create (3.2) and
// the deferral assertion runs *concurrently* with the clicking rather than being
// awaited before it. Keep it that way when editing this act.

const CHIP = "Act 3 · Pending handoff";
const BASE_NAME = `Northwind Traders ${Date.now().toString().slice(-5)}`;
// The panel's slider maximum. Every shot after the create has to fit inside it.
const HANDOFF_SECONDS = 60;

let webs: WebUrls;
let crm: CrmApiClient;
let erp: ErpApiClient;
let nimbus: NimBusApiClient;

recordFootage("act3-pending-handoff.webm");

test.beforeAll(async () => {
  webs = await resolveWebUrls();
  crm = await CrmApiClient.create();
  erp = await ErpApiClient.create();
  nimbus = await NimBusApiClient.create();
  await erp.resetFailureModes();
  await erp.resetHandoffMode();
});

test.afterAll(async () => {
  await erp.resetHandoffMode();
  await crm.dispose();
  await erp.dispose();
  await nimbus.dispose();
});

test("Act 3 — an in-flight external job holds the session without failing it", async ({ page }) => {
  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  await titleCard(
    page,
    "Act 3",
    "The slow external system",
    "Real ERPs often don't do the work while you wait. They accept a job and finish it later.",
  );

  // ── Configure the simulated slow import ────────────────────────
  await caption(
    page,
    "Hand the work to an external job",
    "The ERP handler will accept the message, register an import job, and return without writing anything.",
  );
  // Before the create, so this line costs nothing against the 60s window.
  const acceptedNotDone = startNarration(page, "3.1");
  const durationSlider = page.locator("#handoff-duration");
  await spotlight(durationSlider);
  // Step the range with arrow keys rather than clicking it — a click on the
  // track jumps the thumb to wherever the pointer landed.
  const startValue = Number(await durationSlider.inputValue());
  const delta = HANDOFF_SECONDS - startValue;
  for (let i = 0; i < Math.abs(delta); i++) {
    await durationSlider.press(delta > 0 ? "ArrowRight" : "ArrowLeft");
  }
  await expect(durationSlider).toHaveValue(String(HANDOFF_SECONDS));

  const handoffToggle = page.getByRole("button", { name: /Pending-handoff mode/ });
  await spotlight(handoffToggle);
  await handoffToggle.click();
  await expect(page.getByRole("button", { name: "Pending-handoff mode: ON" })).toBeVisible();
  await caption(page, `The import will take ${HANDOFF_SECONDS} seconds`, "Zero percent chance of failure, for now.");
  await acceptedNotDone;

  // Confirm the ERP accepted the settings before publishing anything.
  await waitFor(
    async () => {
      const mode = await erp.getHandoffMode();
      return mode.enabled && mode.durationSeconds >= HANDOFF_SECONDS - 1 ? mode : null;
    },
    { timeoutMs: 20_000, description: "handoff mode enabled on the ERP" },
  );

  // ── Create an account, as usual ────────────────────────────────
  await visit(page, `${webs.crmWeb}/accounts/new`, CHIP);
  await caption(page, "Same flow as before", "Nothing about this create is special.");
  // From here to the deferral assertion, narration runs under the actions — the
  // handoff window is already ticking.
  void startNarration(page, "3.2");
  await typeInto(page.locator('label:has-text("Legal name") input'), BASE_NAME);
  await typeInto(page.locator('label:has-text("Country code") input'), "NO");
  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.locator("tr").filter({ hasText: BASE_NAME })).toBeVisible();

  const account = await waitFor(
    async () => (await crm.listAccounts()).find((a) => a.legalName === BASE_NAME && !a.isDeleted) ?? null,
    { description: `CRM account ${BASE_NAME}` },
  );

  // ── Two edits, straight behind the create ──────────────────────
  await caption(
    page,
    "Two edits, straight behind it",
    "Both published while the external import is still open.",
  );
  void startNarration(page, "3.3");
  const finalName = `${BASE_NAME} rev2`;
  for (const name of [`${BASE_NAME} rev1`, finalName]) {
    await visit(page, `${webs.crmWeb}/accounts/${account.id}`, CHIP);
    await typeInto(page.locator('label:has-text("Legal name") input'), name);
    await page.getByRole("button", { name: "Save" }).click();
  }

  // ── Pending, not failed, not complete ──────────────────────────
  const pending = await waitFor(
    async () => {
      const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Pending"] });
      return events.find(
        (e) => e.sessionId === account.id && (e.pendingSubStatus ?? "").toLowerCase() === "handoff",
      ) ?? null;
    },
    { timeoutMs: 90_000, intervalMs: 1500, description: `pending handoff row for session ${account.id}` },
  );
  // Prove the deferral before narrating it.
  await waitFor(
    async () => {
      const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Deferred"] });
      return events.some((e) => e.sessionId === account.id) ? true : null;
    },
    { timeoutMs: 90_000, intervalMs: 1500, description: `deferred siblings for session ${account.id}` },
  );

  await visit(page, `/Endpoints/Details/ErpEndpoint?sessionId=${account.id}`, CHIP);
  await caption(
    page,
    "Pending — work is in flight elsewhere",
    "Not failed. Not complete. The handler said 'an external job has this' and returned, so the broker message is settled but the audit trail knows it isn't finished.",
  );
  const pendingRow = page.locator("table tbody tr").filter({ hasText: pending.eventId.substring(0, 8) });
  await expect(pendingRow).toBeVisible();
  const pendingLine = startNarration(page, "3.4");
  await spotlightStatTile(page, "PENDING");
  await spotlight(pendingRow, 2200);
  await pendingLine;

  const deferredRows = page.locator("table tbody tr").filter({ hasText: "Deferred" });
  await expect(deferredRows.first()).toBeVisible();
  await caption(
    page,
    "And the siblings wait for it",
    "The session stays ordered across an asynchronous, out-of-process wait — not just across a retry.",
  );
  const siblingsWait = startNarration(page, "3.5");
  await spotlightStatTile(page, "DEFERRED");
  await spotlight(deferredRows.first(), 2200);
  await siblingsWait;

  // ── The job id you can actually chase ──────────────────────────
  await visit(page, `/Message/Index/ErpEndpoint/${pending.eventId}`, CHIP);
  await caption(
    page,
    "A job ID you can chase",
    "The external job id and the time we expect it back — so an operator chases the real import, not just the message.",
  );
  await narrate(page, "3.6");

  // ── The external job, ticking ──────────────────────────────────
  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  await caption(page, "The external job, ticking", "The simulated ERP import counting down to settlement.");
  const jobsPanel = page.locator("text=In-flight handoff jobs").locator("xpath=..");
  const ticking = startNarration(page, "3.7");
  await spotlight(jobsPanel, 3000);
  await ticking;

  // ── Settlement, then replay ────────────────────────────────────
  const settled = await waitFor(
    async () => {
      const c = await erp.findCustomerByCrmAccountId(account.id);
      return c && c.legalName === finalName ? c : null;
    },
    { timeoutMs: 240_000, intervalMs: 2000, description: `ERP customer settled on "${finalName}"` },
  );
  expect(settled.legalName).toBe(finalName);

  await visit(page, `/Endpoints/Details/ErpEndpoint?sessionId=${account.id}`, CHIP);
  await actChip(page, CHIP);
  await caption(
    page,
    "Settled — and the backlog replays",
    "The import finished, called back into NimBus, and the deferred edits replayed in order. No operator involvement at all.",
  );
  await narrate(page, "3.8");

  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  const erpRow = page.locator("tr").filter({ hasText: finalName });
  await expect(erpRow).toBeVisible();
  await spotlight(erpRow, 2400);
  await caption(page, "Correct final state", `Through a ${HANDOFF_SECONDS}-second external wait and two concurrent edits.`);
  await narrate(page, "3.9");
});
