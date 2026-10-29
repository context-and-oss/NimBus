import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
import SubscriptionManager from "./subscription-manager";

const mocks = vi.hoisted(() => ({
  getAdminServicebusTopics: vi.fn(),
  getAdminServicebusSubscriptions: vi.fn(),
  postAdminServicebusSubscriptionStatus: vi.fn(),
  postAdminServicebusSubscriptionPurge: vi.fn(),
  postAdminServicebusSubscriptionDeadlettersPurge: vi.fn(),
  postAdminServicebusSubscriptionRecreate: vi.fn(),
  postAdminServicebusSubscriptionRestoreRules: vi.fn(),
  deleteAdminServicebusSubscription: vi.fn(),
  deleteAdminServicebusSubscriptionRule: vi.fn(),
  getAdminServicebusResolverDeadletters: vi.fn(),
  postAdminServicebusResolverDeadlettersResubmit: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminServicebusTopics = mocks.getAdminServicebusTopics;
    getAdminServicebusSubscriptions = mocks.getAdminServicebusSubscriptions;
    postAdminServicebusSubscriptionStatus = mocks.postAdminServicebusSubscriptionStatus;
    postAdminServicebusSubscriptionPurge = mocks.postAdminServicebusSubscriptionPurge;
    postAdminServicebusSubscriptionDeadlettersPurge = mocks.postAdminServicebusSubscriptionDeadlettersPurge;
    postAdminServicebusSubscriptionRecreate = mocks.postAdminServicebusSubscriptionRecreate;
    postAdminServicebusSubscriptionRestoreRules = mocks.postAdminServicebusSubscriptionRestoreRules;
    deleteAdminServicebusSubscription = mocks.deleteAdminServicebusSubscription;
    deleteAdminServicebusSubscriptionRule = mocks.deleteAdminServicebusSubscriptionRule;
    getAdminServicebusResolverDeadletters = mocks.getAdminServicebusResolverDeadletters;
    postAdminServicebusResolverDeadlettersResubmit = mocks.postAdminServicebusResolverDeadlettersResubmit;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

function topic(name: string, overrides: Record<string, unknown> = {}) {
  return {
    name,
    isSystemTopic: false,
    isKnownToPlatform: true,
    status: "Active",
    subscriptionCount: 2,
    activeMessageCount: 0,
    deadLetterMessageCount: 0,
    transferMessageCount: 0,
    transferDeadLetterMessageCount: 0,
    scheduledMessageCount: 0,
    sizeInBytes: 0,
    ...overrides,
  };
}

function subscription(name: string, overrides: Record<string, unknown> = {}) {
  return {
    name,
    topicName: "orders",
    status: "Active",
    requiresSession: false,
    forwardTo: null,
    expectedForwardTo: null,
    ruleNames: [],
    missingRuleNames: [],
    detachableRuleNames: [],
    canRecreate: true,
    activeMessageCount: 0,
    deadLetterMessageCount: 0,
    transferMessageCount: 0,
    transferDeadLetterMessageCount: 0,
    totalMessageCount: 0,
    ...overrides,
  };
}

beforeEach(() => {
  mocks.getAdminServicebusTopics
    .mockReset()
    .mockResolvedValue([topic("orders"), topic("billing")]);
  mocks.getAdminServicebusSubscriptions.mockReset().mockResolvedValue([]);
  mocks.postAdminServicebusSubscriptionStatus.mockReset().mockResolvedValue({});
  mocks.postAdminServicebusSubscriptionPurge.mockReset().mockResolvedValue({});
  mocks.postAdminServicebusSubscriptionDeadlettersPurge.mockReset().mockResolvedValue({});
  mocks.postAdminServicebusSubscriptionRecreate.mockReset().mockResolvedValue({});
  mocks.postAdminServicebusSubscriptionRestoreRules.mockReset().mockResolvedValue({});
  mocks.deleteAdminServicebusSubscription.mockReset().mockResolvedValue({});
  mocks.deleteAdminServicebusSubscriptionRule.mockReset().mockResolvedValue({});
  mocks.getAdminServicebusResolverDeadletters.mockReset().mockResolvedValue({
    totalMessageCount: 1,
    isTruncated: false,
    snapshotLimit: 500,
    reasons: [{ reason: "CosmosDbThrottled", count: 1 }],
  });
  mocks.postAdminServicebusResolverDeadlettersResubmit.mockReset().mockResolvedValue({
    processed: 1,
    succeeded: 1,
    failed: 0,
    errors: [],
  });
});

afterEach(() => cleanup());

describe("SubscriptionManager", () => {
  it("lets the subscription guidance use the available horizontal space", () => {
    render(<SubscriptionManager />);

    const guidance = screen.getByText(
      /Live message counts straight from Service Bus/,
    );

    expect(guidance.classList.contains("flex-1")).toBe(true);
    expect(guidance.classList.contains("basis-3xl")).toBe(true);
    expect(guidance.classList.contains("max-w-3xl")).toBe(false);
  });

  it("shows only the newest subscription response, whatever order they land in", async () => {
    // Resolver, Deferred and DeferredProcessor exist on every topic, so a stale
    // response paints a table that looks entirely valid while the operator is
    // looking at another topic — and the next Purge or Delete posts against that
    // other topic. Without the request-id guard this test fails.
    let resolveOrders: (value: unknown) => void = () => {};
    mocks.getAdminServicebusSubscriptions.mockImplementation((name: string) =>
      name === "orders"
        ? new Promise((resolve) => {
            resolveOrders = resolve;
          })
        : Promise.resolve([subscription("billing-only-sub")]),
    );

    render(<SubscriptionManager />);

    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    fireEvent.click(await screen.findByRole("button", { name: "All topics" }));
    fireEvent.click(await screen.findByRole("button", { name: "billing" }));
    await screen.findByText("billing-only-sub");

    // The first topic's response arrives late.
    resolveOrders([subscription("orders-only-sub")]);

    await waitFor(() => expect(screen.queryByText("orders-only-sub")).toBeNull());
    expect(screen.getByText("billing-only-sub")).toBeTruthy();
  });

  it("keeps the error next to a partly-failed purge instead of the summary alone", async () => {
    // "Purged 3 message(s)" on its own hides "the subscription was left Active".
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("DeferredProcessor", { activeMessageCount: 3 }),
    ]);
    mocks.postAdminServicebusSubscriptionPurge.mockResolvedValue({
      processed: 4,
      succeeded: 3,
      failed: 1,
      errors: ["Messages were drained, but the subscription is now Active"],
    });

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));

    fireEvent.click(await screen.findByRole("button", { name: "Purge" }));
    fireEvent.change(await screen.findByPlaceholderText("DeferredProcessor"), {
      target: { value: "DeferredProcessor" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Purge messages" }));

    const feedback = await screen.findByText(/is now Active/);
    expect(feedback.textContent).toContain("Purged 3 message(s).");
  });

  it("arms only the rules the platform can restore", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("orders", {
        ruleNames: ["to-orders", "$Default"],
        detachableRuleNames: ["to-orders"],
      }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));

    // The restorable rule is a button; the one-way one is inert text.
    expect(await screen.findByRole("button", { name: "to-orders ✕" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /\$Default/ })).toBeNull();
    expect(screen.getByText("$Default")).toBeTruthy();
  });

  it("requires an extra acknowledgement to delete something it cannot rebuild", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("hand-made", { canRecreate: false }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete" }));

    const confirm = await screen.findByRole("button", {
      name: "Delete subscription",
    });
    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    expect(mocks.deleteAdminServicebusSubscription).not.toHaveBeenCalled();

    fireEvent.click(
      screen.getByLabelText(
        "I understand this subscription cannot be restored automatically.",
      ),
    );
    fireEvent.click(confirm);

    await waitFor(() =>
      expect(mocks.deleteAdminServicebusSubscription).toHaveBeenCalledWith(
        "orders",
        "hand-made",
      ),
    );
  });

  it("offers no recreate for a subscription outside the platform topology", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("hand-made", { canRecreate: false }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    await screen.findByText("hand-made");

    expect(screen.queryByRole("button", { name: /Delete & recreate/ })).toBeNull();
  });

  it("disables purge on an auto-forwarding subscription", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("Resolver", {
        forwardTo: "Resolver",
        expectedForwardTo: "Resolver",
        activeMessageCount: 900,
      }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));

    const purge = (await screen.findByRole("button", {
      name: "Purge",
    })) as HTMLButtonElement;
    expect(purge.disabled).toBe(true);
  });

  it("shows a detached forward destination rather than reading as terminal", async () => {
    // A pause nobody resumed otherwise looks like an ordinary terminal
    // subscription quietly filling up.
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("Resolver", {
        status: "ReceiveDisabled",
        forwardTo: null,
        expectedForwardTo: "Resolver",
      }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));

    expect(await screen.findByText("→ Resolver (detached)")).toBeTruthy();
    expect(screen.getByText("Paused")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Resume" })).toBeTruthy();
  });

  it("counts a delete's blast radius including both dead-letter queues", async () => {
    // A purge leaves dead letters behind; a delete does not. Quoting the drainable
    // number on a delete confirmation would understate what is discarded.
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("orders", {
        activeMessageCount: 10,
        transferMessageCount: 1,
        deadLetterMessageCount: 2,
        transferDeadLetterMessageCount: 3,
      }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete" }));

    expect(await screen.findByText(/all 16 message\(s\)/)).toBeTruthy();
  });

  it("makes every action read-only on a topic the platform does not own", async () => {
    mocks.getAdminServicebusTopics.mockResolvedValue([
      topic("someone-elses", { isKnownToPlatform: false }),
    ]);
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("sub-1", { canRecreate: false }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "someone-elses" }));

    await screen.findByText(/is not part of the platform topology/);
    const pause = (await screen.findByRole("button", {
      name: "Pause",
    })) as HTMLButtonElement;
    expect(pause.disabled).toBe(true);
  });

  it("sorts topics by a count column on click, and by name by default", async () => {
    mocks.getAdminServicebusTopics.mockResolvedValue([
      topic("alpha", { activeMessageCount: 1 }),
      topic("bravo", { activeMessageCount: 99 }),
    ]);

    render(<SubscriptionManager />);
    await screen.findByRole("button", { name: "alpha" });

    const names = () =>
      screen
        .getAllByRole("row")
        .slice(1)
        .map((row) => within(row).getAllByRole("button")[0].textContent);

    expect(names()).toEqual(["alpha", "bravo"]);

    fireEvent.click(screen.getByRole("button", { name: /Active/ }));
    expect(names()).toEqual(["bravo", "alpha"]);
  });

  it("filters topics by platform membership", async () => {
    mocks.getAdminServicebusTopics.mockResolvedValue([
      topic("platform-topic"),
      topic("external-topic", { isKnownToPlatform: false }),
    ]);

    render(<SubscriptionManager />);
    await screen.findByRole("button", { name: "platform-topic" });

    const filter = screen.getByRole("combobox", {
      name: "Filter topics by platform membership",
    });

    expect(screen.getByRole("button", { name: "platform-topic" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "external-topic" })).toBeTruthy();

    fireEvent.change(filter, { target: { value: "platform" } });
    expect(screen.getByRole("button", { name: "platform-topic" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "external-topic" })).toBeNull();

    fireEvent.change(filter, { target: { value: "external" } });
    expect(screen.queryByRole("button", { name: "platform-topic" })).toBeNull();
    expect(screen.getByRole("button", { name: "external-topic" })).toBeTruthy();

    fireEvent.change(filter, { target: { value: "all" } });
    expect(screen.getByRole("button", { name: "platform-topic" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "external-topic" })).toBeTruthy();
  });

  it.each(["Resolver", "resolver", "RESOLVER"])("offers dead-letter inspection for terminal Resolver topic %s", async (topicName) => {
    mocks.getAdminServicebusTopics.mockResolvedValue([topic(topicName)]);
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("Resolver", {
        topicName,
        requiresSession: true,
        forwardTo: null,
        deadLetterMessageCount: 2,
      }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: topicName }));

    expect(await screen.findByRole("button", { name: "Inspect dead letters" })).toBeDefined();
    fireEvent.click(screen.getByRole("button", { name: "Inspect dead letters" }));
    expect(await screen.findByText(/All dead letters/)).toBeDefined();
    expect(mocks.getAdminServicebusResolverDeadletters).toHaveBeenCalledWith("Resolver");
  });

  it("does not offer replay for transfer-only dead letters", async () => {
    mocks.getAdminServicebusTopics.mockResolvedValue([topic("Resolver")]);
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("Resolver", {
        topicName: "Resolver",
        requiresSession: true,
        forwardTo: null,
        deadLetterMessageCount: 0,
        transferDeadLetterMessageCount: 2,
      }),
    ]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "Resolver" }));
    await screen.findByText("Resolver", { selector: "td *" });

    expect(screen.queryByRole("button", { name: "Inspect dead letters" })).toBeNull();
  });

  // Issue #286: Service Bus never empties a dead-letter queue on its own, and Purge leaves both
  // queues in place.
  it("purges a subscription's dead letters after the typed confirmation", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("billing", { deadLetterMessageCount: 2, transferDeadLetterMessageCount: 1 }),
    ]);
    mocks.postAdminServicebusSubscriptionDeadlettersPurge.mockResolvedValue({
      processed: 3,
      succeeded: 3,
      failed: 0,
      errors: [],
    });

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    fireEvent.click(await screen.findByRole("button", { name: "Purge dead letters" }));

    const dialog = screen.getByRole("dialog", { name: "Purge dead letters" });
    expect(dialog.textContent).toContain("3 dead-lettered message(s)");
    fireEvent.change(within(dialog).getByPlaceholderText("billing"), { target: { value: "billing" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Purge dead letters now" }));

    await waitFor(() =>
      expect(mocks.postAdminServicebusSubscriptionDeadlettersPurge).toHaveBeenCalledWith("orders", "billing"),
    );
    expect((await screen.findByText(/Purged 3 dead-lettered message/)).textContent).toContain("3");
  });

  it("offers no dead-letter purge when both dead-letter queues are empty", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([subscription("billing", { activeMessageCount: 4 })]);

    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    await screen.findByText("billing", { selector: "td *" });

    expect(screen.queryByRole("button", { name: "Purge dead letters" })).toBeNull();
  });
});
