import { expect, test } from "@playwright/test";
import { CrmApiClient, type CrmAccount } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { waitFor } from "../helpers/wait-for.js";
import { resolveWebUrls, type WebUrls } from "./stage/demo-urls.js";
import { Pace, caption, narrate, recordFootage, spotlight, startNarration, titleCard, visit } from "./stage/stage.js";

// ACT 4 — Beyond fire-and-forget.
// Kills the "so it's just events" objection. Request/reply and Command prove each
// other: the command sets the hold, the query observes it.
// Script: ../docs/demo-video-script.md#act-4.

const CHIP = "Act 4 · Sync & commands";
const LEGAL_NAME = `Initech Systems ${Date.now().toString().slice(-5)}`;

let webs: WebUrls;
let crm: CrmApiClient;
let erp: ErpApiClient;
let account: CrmAccount;

recordFootage("act4-request-reply-command.webm");

test.beforeAll(async () => {
  webs = await resolveWebUrls();
  crm = await CrmApiClient.create();
  erp = await ErpApiClient.create();
  await erp.resetFailureModes();
  await erp.resetHandoffMode();

  // Off-camera setup: this act is about the two synchronous interactions, not
  // about watching another account propagate.
  account = await crm.createAccount({ legalName: LEGAL_NAME, countryCode: "DK" });
  await waitFor(async () => await erp.findCustomerByCrmAccountId(account.id), {
    timeoutMs: 180_000,
    intervalMs: 2000,
    description: `ERP customer for ${LEGAL_NAME}`,
  });
  account = (await crm.getAccount(account.id))!;
});

test.afterAll(async () => {
  await crm.dispose();
  await erp.dispose();
});

test("Act 4 — a synchronous question and an imperative command", async ({ page }) => {
  // The credit-hold button asks for confirmation before sending the command.
  page.on("dialog", (dialog) => dialog.accept());

  await visit(page, `${webs.crmWeb}/accounts/${account.id}`, CHIP);
  await titleCard(
    page,
    "Act 4",
    "Beyond fire-and-forget",
    "Not everything is a notification. Some things are questions, and some are instructions.",
  );

  // ── Request/reply ──────────────────────────────────────────────
  await caption(
    page,
    "A question, not a notification",
    "'Can this customer buy on credit right now' needs an answer before we respond to the user.",
  );
  // The reply is sub-second, so this line has to start before the click or the
  // answer is already on screen by the time the question is asked.
  const theQuestion = startNarration(page, "4.1");
  const checkButton = page.getByRole("button", { name: "Run ERP credit check" });
  await spotlight(checkButton);
  await checkButton.click();
  await theQuestion;

  const result = page.getByTestId("credit-check-result");
  await expect(result).toBeVisible({ timeout: 60_000 });
  await expect(result).toContainText("Approved");
  await caption(
    page,
    "Request/reply — typed, sub-second",
    "Over a Service Bus session. The request is a normal audited NimBus event; the reply comes back on the endpoint's reply subscription.",
  );
  const typedAnswer = startNarration(page, "4.2");
  await spotlight(result, 2600);
  await typedAnswer;

  // ── Command ────────────────────────────────────────────────────
  await caption(
    page,
    "A command: imperative, exactly one consumer",
    "Not a notification, not a question — an instruction. The platform refuses to start if a Command has anything other than one declared consumer.",
  );
  const theCommand = startNarration(page, "4.3");
  const holdButton = page.getByRole("button", { name: "Place credit hold in ERP" });
  await spotlight(holdButton);
  await holdButton.click();
  await expect(page.getByText("Hold command sent")).toBeVisible({ timeout: 30_000 });
  await theCommand;

  // ── The ERP obeyed, and published nothing back ─────────────────
  await waitFor(
    async () => {
      const customer = await erp.findCustomerByCrmAccountId(account.id);
      return customer && customer.creditHold ? customer : null;
    },
    { timeoutMs: 120_000, intervalMs: 1500, description: "ERP customer on credit hold" },
  );

  await visit(page, `${webs.erpWeb}/customers`, CHIP);
  const erpRow = page.locator("tr").filter({ hasText: LEGAL_NAME });
  await expect(erpRow).toBeVisible();
  await expect(erpRow.getByTestId("credit-hold-badge")).toBeVisible();
  await caption(
    page,
    "The ERP obeyed — and published nothing back",
    "A command doesn't owe you an event. The effect is visible in the ERP's own state.",
  );
  const noEventBack = startNarration(page, "4.4");
  await spotlight(erpRow, 2600);
  await noEventBack;

  // ── The two showcases prove each other ─────────────────────────
  await visit(page, `${webs.crmWeb}/accounts/${account.id}`, CHIP);
  await caption(page, "Ask the same question again", "The command changed the answer.");
  const recheck = page.getByRole("button", { name: "Run ERP credit check" });
  await spotlight(recheck);
  await recheck.click();

  const secondResult = page.getByTestId("credit-check-result");
  await expect(secondResult).toBeVisible({ timeout: 60_000 });
  await expect(secondResult).toContainText("On hold");
  await caption(
    page,
    "On hold — the two patterns just validated each other",
    "The command wrote the state; the synchronous query read it back.",
  );
  const provingEachOther = startNarration(page, "4.5");
  await spotlight(secondResult, 2600);
  await provingEachOther;
});
