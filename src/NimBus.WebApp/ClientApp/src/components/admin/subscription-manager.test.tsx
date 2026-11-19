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

/** Selects a topic's row, then opens its subscriptions from the inspector. */
async function openTopic(name: string) {
  fireEvent.click(await screen.findByRole("button", { name }));
  fireEvent.click(screen.getByRole("button", { name: "Open subscriptions" }));
}

/** Selects a subscription's row, which shows it and its actions in the inspector. */
async function selectSubscription(name: string) {
  fireEvent.click(await screen.findByRole("button", { name }));
}

describe("SubscriptionManager", () => {
  it("says what the table is for above it, as a named section", () => {
    render(<SubscriptionManager />);

    const section = screen.getByRole("region", { name: "Topics" });
    expect(within(section).getByText(/Live message counts straight from Service Bus/)).toBeTruthy();
    expect(within(section).getByRole("button", { name: "Refresh" })).toBeTruthy();
  });

  it("shows a topic's counts in the inspector before opening it", async () => {
    mocks.getAdminServicebusTopics.mockResolvedValue([
      topic("orders", { subscriptionCount: 4, activeMessageCount: 12 }),
    ]);
    render(<SubscriptionManager />);
    expect(screen.getByText("Select a topic to see its counts and open its subscriptions.")).toBeTruthy();

    const row = await screen.findByRole("button", { name: "orders" });
    fireEvent.click(row);

    expect(row.getAttribute("aria-pressed")).toBe("true");
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    expect(within(inspector).getByText("Subscriptions").nextElementSibling?.textContent).toBe("4");
    expect(mocks.getAdminServicebusSubscriptions).not.toHaveBeenCalled();
  });

  it("keeps a subscription's actions out of the table until it is selected", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("Deferred", { activeMessageCount: 3 }),
    ]);
    render(<SubscriptionManager />);
    await openTopic("orders");
    await screen.findByText("Deferred");

    expect(screen.getByText("Select a subscription to see its rules and act on it.")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Purge" })).toBeNull();

    await selectSubscription("Deferred");
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    expect(within(inspector).getByRole("button", { name: "Purge" })).toBeTruthy();
    expect(within(screen.getByRole("table", { name: "Subscriptions" })).queryByRole("button", { name: "Purge" })).toBeNull();
  });

  it("keeps a delete's outcome on screen after its row is gone", async () => {
    mocks.getAdminServicebusSubscriptions
      .mockResolvedValueOnce([subscription("old-sub")])
      .mockResolvedValue([]);
    mocks.deleteAdminServicebusSubscription.mockResolvedValue({ succeeded: true, message: "Deleted old-sub." });
    render(<SubscriptionManager />);
    await openTopic("orders");
    await selectSubscription("old-sub");

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    fireEvent.change(await screen.findByPlaceholderText("old-sub"), { target: { value: "old-sub" } });
    fireEvent.click(screen.getByRole("button", { name: "Delete permanently" }));

    expect((await screen.findByRole("status")).textContent).toContain("Deleted old-sub.");
    expect(screen.getByText("No subscriptions on this topic.")).toBeTruthy();
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

    await openTopic("orders");
    fireEvent.click(await screen.findByRole("button", { name: "All topics" }));
    await openTopic("billing");
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
    await openTopic("orders");
    await selectSubscription("DeferredProcessor");

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
    await openTopic("orders");
    await selectSubscription("orders");

    // The restorable rule is a button; the one-way one is inert text.
    expect(await screen.findByRole("button", { name: "to-orders ✕" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /\$Default/ })).toBeNull();
    expect(screen.getByText("$Default")).toBeTruthy();
  });

  it("shows each forward rule's routing keys next to the rule (Spec 045)", async () => {
    const keyedFilter =
      "user.EventTypeId = 'AccountCreated' AND user.RoutingKey IN ('AT', 'CH', 'DE')";
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("DachHub", {
        ruleNames: ["AccountCreated", "AccountUpdated"],
        detachableRuleNames: ["AccountCreated", "AccountUpdated"],
        ruleRouting: [
          { ruleName: "AccountCreated", routingKeys: ["AT", "CH", "DE"], unmatched: false, filter: keyedFilter },
        ],
      }),
      subscription("UnroutedHub", {
        ruleNames: ["AccountCreated"],
        detachableRuleNames: ["AccountCreated"],
        ruleRouting: [
          { ruleName: "AccountCreated", routingKeys: ["DE", "DK"], unmatched: true, filter: "…" },
        ],
      }),
    ]);

    render(<SubscriptionManager />);
    await openTopic("orders");

    await selectSubscription("DachHub");
    const keyed = await screen.findByText("keys AT, CH, DE");
    // The live filter is one hover away, for whoever wants the exact SQL.
    expect(keyed.getAttribute("title")).toBe(keyedFilter);
    // The detach button keeps its name; the keys sit beside it, not inside it.
    expect(screen.getByRole("button", { name: "AccountCreated ✕" })).toBeTruthy();
    // AccountUpdated takes every key, so it has no label.
    expect(screen.getAllByText(/^keys |^unmatched/)).toHaveLength(1);

    await selectSubscription("UnroutedHub");
    expect(screen.getByText("unmatched · not DE, DK")).toBeTruthy();
  });

  it("requires an extra acknowledgement to delete something it cannot rebuild", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("hand-made", { canRecreate: false }),
    ]);

    render(<SubscriptionManager />);
    await openTopic("orders");
    await selectSubscription("hand-made");
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
    await openTopic("orders");
    await selectSubscription("hand-made");
    expect(screen.getByRole("button", { name: "Delete" })).toBeTruthy();
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
    await openTopic("orders");
    await selectSubscription("Resolver");

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
    await openTopic("orders");
    await selectSubscription("Resolver");

    const table = screen.getByRole("table", { name: "Subscriptions" });
    expect(within(table).getByText("→ Resolver (detached)")).toBeTruthy();
    expect(within(table).getByText("Paused")).toBeTruthy();
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
    await openTopic("orders");
    await selectSubscription("orders");
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
    await openTopic("someone-elses");
    await selectSubscription("sub-1");

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
    await openTopic(topicName);
    await selectSubscription("Resolver");

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
    await openTopic("Resolver");
    await selectSubscription("Resolver");

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
    await openTopic("orders");
    await selectSubscription("billing");
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

  it("runs a subscription's actions from its row menu, through the same confirmation", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([
      subscription("billing", { deadLetterMessageCount: 2 }),
    ]);
    mocks.postAdminServicebusSubscriptionDeadlettersPurge.mockResolvedValue({
      processed: 2,
      succeeded: 2,
      failed: 0,
      errors: [],
    });

    render(<SubscriptionManager />);
    await openTopic("orders");
    fireEvent.click(await screen.findByRole("button", { name: "Actions for billing" }));
    const menu = screen.getByRole("menu");
    expect(within(menu).getAllByRole("menuitem").map((item) => item.textContent)).toEqual([
      "Pause",
      "Purge…",
      "Purge dead letters…",
      "Delete & recreate…",
      "Delete…",
    ]);
    fireEvent.click(within(menu).getByRole("menuitem", { name: "Purge dead letters…" }));

    const dialog = screen.getByRole("dialog", { name: "Purge dead letters" });
    fireEvent.change(within(dialog).getByPlaceholderText("billing"), { target: { value: "billing" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Purge dead letters now" }));
    await waitFor(() =>
      expect(mocks.postAdminServicebusSubscriptionDeadlettersPurge).toHaveBeenCalledWith("orders", "billing"),
    );
  });

  it("opens a topic's subscriptions from its row menu", async () => {
    render(<SubscriptionManager />);
    fireEvent.click(await screen.findByRole("button", { name: "Actions for orders" }));
    fireEvent.click(within(screen.getByRole("menu")).getByRole("menuitem", { name: "Open subscriptions" }));

    await waitFor(() => expect(mocks.getAdminServicebusSubscriptions).toHaveBeenCalledWith("orders"));
  });

  it("offers no dead-letter purge when both dead-letter queues are empty", async () => {
    mocks.getAdminServicebusSubscriptions.mockResolvedValue([subscription("billing", { activeMessageCount: 4 })]);

    render(<SubscriptionManager />);
    await openTopic("orders");
    await selectSubscription("billing");

    expect(screen.queryByRole("button", { name: "Purge dead letters" })).toBeNull();
  });
});

// The Topology page lists the topics beside this view and chooses the topic.
describe("SubscriptionManager with the topic chosen by the page", () => {
  it("opens the given topic and follows a change without its own back button", async () => {
    mocks.getAdminServicebusSubscriptions.mockImplementation((name: string) =>
      Promise.resolve([subscription(`${name}-sub`)]),
    );
    const { rerender } = render(<SubscriptionManager topic="orders" onTopicChange={vi.fn()} />);

    expect(await screen.findByText("orders-sub")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "All topics" })).toBeNull();

    rerender(<SubscriptionManager topic="billing" onTopicChange={vi.fn()} />);

    expect(await screen.findByText("billing-sub")).toBeTruthy();
    expect(screen.queryByText("orders-sub")).toBeNull();
    expect(mocks.getAdminServicebusSubscriptions).toHaveBeenLastCalledWith("billing");
  });

  it("asks the page to open a topic from the all-topics table instead of opening it itself", async () => {
    const onTopicChange = vi.fn();
    render(<SubscriptionManager topic={null} onTopicChange={onTopicChange} />);

    fireEvent.click(await screen.findByRole("button", { name: "orders" }));
    expect(onTopicChange).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Open subscriptions" }));

    expect(onTopicChange).toHaveBeenCalledWith("orders");
    expect(mocks.getAdminServicebusSubscriptions).not.toHaveBeenCalled();
  });

  it("reports each topics load, and an inline callback doesn't reload them", async () => {
    const loaded: unknown[] = [];
    const { rerender } = render(
      <SubscriptionManager topic={null} onTopicChange={vi.fn()} onTopicsLoaded={(t) => loaded.push(t)} />,
    );
    await waitFor(() => expect(loaded).toHaveLength(1));

    rerender(<SubscriptionManager topic={null} onTopicChange={vi.fn()} onTopicsLoaded={(t) => loaded.push(t)} />);

    expect(mocks.getAdminServicebusTopics).toHaveBeenCalledTimes(1);
  });
});
