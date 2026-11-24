import { test, expect } from "@playwright/test";
import { CrmApiClient } from "../helpers/crm-api-client.js";
import { ErpApiClient } from "../helpers/erp-api-client.js";
import { NimBusApiClient } from "../helpers/nimbus-api-client.js";
import { Timeouts } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * The ERP adapter's own retry rule (Erp.Adapter.Functions/ErpResilience.cs), not the scripted
 * E2E one: the "Rate limited" error-mode reason throws ErpPricingThrottledException, which the
 * rule retries three times, 5, 5 and 8 seconds apart.
 *
 * Error mode stays on at a 100% failure rate so the outcome does not depend on timing: every
 * attempt fails, the budget runs out and the message ends as Failed. A retry that succeeds is
 * covered by 12-message-lifecycle.
 */
test.describe("ERP adapter retry rule", () => {
  let crm: CrmApiClient;
  let erp: ErpApiClient;
  let nimbus: NimBusApiClient;

  // The Resolver's history holds one error response per failed attempt; the retries themselves
  // are scheduled messages that it does not list.
  const failedAttempts = (history: Array<{ messageType: string }>) =>
    history.filter((message) => /^errorresponse$/i.test(message.messageType)).length;

  test.beforeAll(async () => {
    crm = await CrmApiClient.create();
    erp = await ErpApiClient.create();
    nimbus = await NimBusApiClient.create();
    await erp.resetFailureModes();
  });

  test.afterAll(async () => {
    await erp.resetFailureModes();
    await crm.dispose();
    await erp.dispose();
    await nimbus.dispose();
  });

  test("rate-limited failure is retried three times, then stays Failed until resubmitted", async () => {
    await erp.setErrorMode(true, "throttled_dependency", 1);

    const legalName = `Throttled Ltd ${Date.now()}`;
    const crmAccount = await crm.createAccount({ legalName, countryCode: "DK" });

    // Each failed attempt is recorded as Failed before its retry is scheduled, so the status
    // alone does not say the budget is spent: wait for the third retry to have failed too.
    const failed = await waitFor(
      async () => {
        const events = await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Failed"] });
        const event = events.find((e) => e.sessionId === crmAccount.id);
        if (!event) return null;
        // The first attempt and its three retries.
        return failedAttempts(await nimbus.history("ErpEndpoint", event.eventId)) === 4 ? event : null;
      },
      { timeoutMs: Timeouts.propagationMs, description: `Four failed attempts for account ${crmAccount.id} on ErpEndpoint` },
    );
    expect(await erp.findCustomerByCrmAccountId(crmAccount.id)).toBeNull();

    // Longer than the rule's longest wait (8s): the budget is spent, so no fourth retry follows.
    await new Promise((resolve) => setTimeout(resolve, 12_000));
    expect(failedAttempts(await nimbus.history("ErpEndpoint", failed.eventId))).toBe(4);
    const [still] = (await nimbus.searchEvents("ErpEndpoint", { resolutionStatus: ["Failed"] }))
      .filter((e) => e.eventId === failed.eventId);
    expect(still, "the event is still Failed after its retries").toBeTruthy();

    // Recovery is the ordinary one: the cause goes away and an operator resubmits.
    await erp.setErrorMode(false);
    await nimbus.resubmit(still.eventId, still.lastMessageId);

    const erpCustomer = await waitFor(
      async () => await erp.findCustomerByCrmAccountId(crmAccount.id),
      { timeoutMs: Timeouts.propagationMs, description: `ERP customer created after resubmit for account ${crmAccount.id}` },
    );
    expect(erpCustomer.legalName).toBe(legalName);
  });
});
