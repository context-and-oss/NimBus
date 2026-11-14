import { test, expect, directions } from "../helpers/lifecycle-fixture.js";
import { waitFor } from "../helpers/wait-for.js";
import { actOnEvent } from "../helpers/operator-actions.js";

for (const direction of directions) {
  test.describe(`${direction} message lifecycle`, () => {
    test("ordered updates preserve each business revision and delete propagates", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script([]);
      for (const revision of ["one", "two", "three"]) await flow.update(revision);
      await flow.status("Completed", 3);
      await flow.targetRevision("three");
      const revisions = (await flow.audit()).filter(row => row.fieldName === "LegalName")
        .sort((a, b) => a.timestamp.localeCompare(b.timestamp)).map(row => row.newValue);
      expect(revisions.slice(-3)).toEqual(["one", "two", "three"].map(revision => `${flow.name}-${revision}`));
      if (direction === "crm-to-erp") await harness.crm.deleteAccount(flow.session);
      else await harness.erp.deleteCustomer(flow.session);
      await waitFor(async () => (await flow.target())?.isDeleted ? true : null,
        { timeoutMs: 60_000, description: "Destination soft deletion" });
      await flow.clean();
    });

    test("failure blocks siblings; UI resubmit drains them and preserves FIFO", async ({ harness, page }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["fail"]);
      await flow.update("head");
      const [failed] = await flow.status("Failed");
      await flow.update("second"); await flow.update("third");
      await flow.status("Deferred", 2);
      expect((await flow.target())?.legalName).toBe(flow.name);
      expect(await flow.control.attempts(flow.session)).toHaveLength(1);
      const other = await harness.createFlow(direction);
      await other.update("independent"); await other.targetRevision("independent");
      await other.status("Completed");
      await actOnEvent(page, flow.endpoint, failed, "Resubmit");
      await flow.status("Completed", 3); await flow.targetRevision("third"); await flow.clean();
      const revisions = (await flow.audit()).filter(row => row.fieldName === "LegalName")
        .sort((a, b) => a.timestamp.localeCompare(b.timestamp)).map(row => row.newValue);
      expect(revisions.slice(-3)).toEqual(["head", "second", "third"].map(revision => `${flow.name}-${revision}`));
    });

    test("skip releases deferred work without applying the failed revision", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["fail"]); await flow.update("bad");
      const [failed] = await flow.status("Failed");
      await flow.update("good"); await flow.status("Deferred");
      await harness.nimbus.skipMessage(failed.eventId, failed.lastMessageId);
      await flow.status("Skipped"); await flow.status("Completed"); await flow.targetRevision("good");
      expect((await flow.audit()).some(row => row.newValue === `${flow.name}-bad`)).toBe(false);
      await flow.clean();
    });

    test("a second failure during replay keeps the remaining backlog blocked", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["fail", "continue", "fail"]);
      await flow.update("first"); const [head] = await flow.status("Failed");
      await flow.update("second"); await flow.update("third"); await flow.status("Deferred", 2);
      await harness.nimbus.resubmit(head.eventId, head.lastMessageId);
      const next = await waitFor(async () => (await flow.events("Failed")).find(event => event.eventId !== head.eventId) ?? null,
        { timeoutMs: 60_000, description: "Deferred sibling becomes the new blocked head" });
      await flow.status("Deferred"); await flow.targetRevision("first");
      await harness.nimbus.resubmit(next.eventId, next.lastMessageId);
      await flow.status("Completed", 3); await flow.targetRevision("third"); await flow.clean();
    });

    test("ordinary failure has no implicit retry", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["fail"]); await flow.update("failed"); await flow.status("Failed");
      // Longer than the configured 2s retry delay: the unmatched error must stay put.
      await new Promise(resolve => setTimeout(resolve, 5_000));
      expect(await flow.control.attempts(flow.session)).toHaveLength(1);
      expect((await flow.events())[0].resolutionStatus).toBe("Failed");
      expect((await flow.target())?.legalName).toBe(flow.name);
    });

    test("configured retry succeeds and automatically releases siblings", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["retry", "retry"]); await flow.update("head");
      await flow.update("tail");
      await flow.status("Completed", 2); await flow.targetRevision("tail");
      const attempts = await flow.control.attempts(flow.session);
      expect(attempts.map(attempt => attempt.action)).toEqual(["retry", "retry", "continue", "continue"]);
      expect(new Set(attempts.slice(0, 3).map(attempt => attempt.eventId)).size).toBe(1);
      expect(new Set(attempts.slice(0, 3).map(attempt => attempt.originatingMessageId)).size).toBe(1);
      await flow.clean();
    });

    test("exhausted retry budget stays Failed until recovery", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["retry", "retry", "retry", "retry"]); await flow.update("head");
      await waitFor(async () => (await flow.control.attempts(flow.session)).length === 3 ? true : null,
        { timeoutMs: 60_000, description: "Initial attempt plus exactly two configured retries" });
      await new Promise(resolve => setTimeout(resolve, 5_000));
      expect(await flow.control.attempts(flow.session)).toHaveLength(3);
      const [failed] = await flow.status("Failed");
      await flow.update("tail"); await flow.status("Deferred");
      await flow.script([]); await harness.nimbus.resubmit(failed.eventId, failed.lastMessageId);
      await flow.status("Completed", 2); await flow.targetRevision("tail"); await flow.clean();
    });

    test("transient failure redelivers the same event without a failed response", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["transient"]); await flow.update("redelivered");
      const [completed] = await flow.status("Completed"); await flow.targetRevision("redelivered");
      const attempts = await flow.control.attempts(flow.session);
      expect(attempts.map(attempt => attempt.action)).toEqual(["transient", "continue"]);
      expect(attempts[1].messageId).toBe(attempts[0].messageId);
      expect(await flow.events("Failed")).toHaveLength(0);
      const history = await harness.nimbus.history(flow.endpoint, completed.eventId);
      expect(history.some(message => /errorresponse|retryrequest/i.test(message.messageType))).toBe(false);
      await flow.clean();
    });

    test("resubmitting completed work produces no duplicate business audit", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script([]); await flow.update("same");
      const [completed] = await flow.status("Completed"); await flow.targetRevision("same");
      const auditBefore = await flow.audit();
      // The WebApp only acts on Failed, DeadLettered, Unsupported, Deferred and Handoff-pending
      // rows (#210, docs/plan/2026-10-06-mcp-operator-phase2a-plan.md): Completed work is refused.
      await expect(harness.nimbus.resubmit(completed.eventId, completed.lastMessageId))
        .rejects.toThrow(/409 A Completed message cannot be resubmitted/);
      // Longer than the configured 2s retry delay: nothing may be replayed.
      await new Promise(resolve => setTimeout(resolve, 5_000));
      expect(await flow.control.attempts(flow.session)).toHaveLength(1);
      const [still] = await flow.status("Completed");
      expect(still.eventId).toBe(completed.eventId);
      await flow.clean();
      expect(await flow.audit()).toEqual(auditBefore);
      await flow.targetRevision("same");
    });

    for (const fault of ["permanent", "discard"] as const) {
      test(`${fault} classification does not retry or block new siblings`, async ({ harness }) => {
        const flow = await harness.createFlow(direction);
        await flow.script([fault]); await flow.update("rejected");
        const [rejected] = await flow.status(fault === "permanent" ? "DeadLettered" : "Skipped");
        expect(JSON.stringify(rejected)).toContain(`E2E ${fault}`);
        if (fault === "permanent") {
          const letters = await waitFor(async () => {
            const letters = await flow.control.deadletters(flow.session);
            return letters.length === 1 ? letters : null;
          }, { timeoutMs: 60_000, description: "Permanent failure has one real broker dead letter" });
          expect(letters[0].deadLetterReason).toBeTruthy();
        } else expect(await flow.control.deadletters(flow.session)).toHaveLength(0);
        await flow.update("next"); await flow.status("Completed"); await flow.targetRevision("next");
        expect((await flow.audit()).some(row => row.newValue === `${flow.name}-rejected`)).toBe(false);
        expect((await flow.control.attempts(flow.session)).map(attempt => attempt.action)).toEqual([fault, "continue"]);
      });
    }

    test("discard during resubmit releases an already blocked session", async ({ harness }) => {
      const flow = await harness.createFlow(direction);
      await flow.script(["fail", "discard"]); await flow.update("bad");
      const [failed] = await flow.status("Failed");
      await flow.update("next"); await flow.status("Deferred");
      await harness.nimbus.resubmit(failed.eventId, failed.lastMessageId);
      await flow.status("Skipped"); await flow.status("Completed"); await flow.targetRevision("next"); await flow.clean();
    });

    for (const fault of ["fail", "validation"] as const) {
      test(`middleware ${fault} is exactly DeadLettered`, async ({ harness }) => {
        const flow = await harness.createFlow(direction);
        await flow.script([fault], "middleware"); await flow.update("middleware");
        await flow.status("DeadLettered");
        expect((await flow.target())?.legalName).toBe(flow.name);
        expect(await flow.events("Failed")).toHaveLength(0);
        await flow.update("next"); await flow.status("Completed"); await flow.targetRevision("next");
      });
    }
  });
}
