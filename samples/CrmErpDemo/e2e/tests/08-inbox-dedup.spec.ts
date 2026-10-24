import { test, expect, directions } from "../helpers/lifecycle-fixture.js";
import { waitFor } from "../helpers/wait-for.js";

for (const direction of directions) {
  test(`${direction} duplicate broker MessageId cannot duplicate a business write`, async ({ harness }) => {
    const flow = await harness.createFlow(direction);
    await flow.script([]); await flow.update("original");
    const [completed] = await flow.status("Completed"); await flow.targetRevision("original");
    const [attempt] = await flow.control.attempts(flow.session);
    const auditBefore = await flow.audit();
    let payload: unknown;
    if (direction === "crm-to-erp") {
      const account = (await harness.crm.getAccount(flow.session))!;
      payload = { ...account, accountId: account.id };
    } else {
      const customer = (await harness.erp.getCustomer(flow.session))!;
      payload = { ...customer, accountId: customer.crmAccountId ?? customer.id, erpCustomerId: customer.id };
    }
    // Republish the successful identity. Operator resubmit allocates a NEW MessageId.
    await flow.control.replay(flow.session, attempt, payload);
    if (direction === "erp-to-crm") {
      // The inbox answers with a DuplicateDetected skip, which reaches the history but must
      // not rewrite the Completed row (#278).
      await waitFor(async () => (await harness.nimbus.history(flow.endpoint, completed.eventId))
        .some(message => /skipresponse/i.test(message.messageType)) ? true : null,
        { timeoutMs: 60_000, description: "inbox duplicate skip reaches the event history" });
      await flow.status("Completed");
      expect(await flow.events("Skipped")).toHaveLength(0);
      expect(await flow.control.attempts(flow.session)).toHaveLength(1);
    } else {
      await waitFor(async () => (await flow.control.attempts(flow.session)).length === 2 ? true : null,
        { timeoutMs: 60_000, description: "ERP idempotent handler receives duplicate delivery" });
      await flow.status("Completed");
    }
    expect(await flow.audit()).toEqual(auditBefore);
    await flow.targetRevision("original");
  });
}
