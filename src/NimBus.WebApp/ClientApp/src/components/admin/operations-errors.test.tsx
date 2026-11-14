import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import type { ReactElement } from "react";
import { SwaggerException } from "api-client";
import {
  CopyEndpointCard,
  DeleteAllEventsCard,
  DeleteByStatusCard,
  DeleteMessagesByToCard,
  SkipMessagesCard,
  SubscriptionPurgeCard,
} from "./advanced-operations";
import { BulkResubmitCard, DeleteDeadLetteredCard } from "./bulk-operations";

// Issue #285: the Operations cards swallowed API errors, so a failed request looked the same as
// "nothing matched". Every preview and execute now shows the failure and drops stale results.

const mocks = vi.hoisted(() => ({
  storageProvider: { current: undefined as string | null | undefined },
  postAdminPurgePreview: vi.fn(),
  postAdminPurge: vi.fn(),
  postAdminDeleteByStatusPreview: vi.fn(),
  postAdminDeleteByStatus: vi.fn(),
  postAdminSkipPreview: vi.fn(),
  postAdminSkip: vi.fn(),
  postAdminDeleteByToPreview: vi.fn(),
  postAdminDeleteByTo: vi.fn(),
  postAdminCopy: vi.fn(),
  postAdminDeleteAll: vi.fn(),
  getAdminFailedPreview: vi.fn(),
  postAdminBulkResubmit: vi.fn(),
  getAdminDeadletteredPreview: vi.fn(),
  postAdminDeleteDeadlettered: vi.fn(),
}));

vi.mock("hooks/app-status", () => ({ useStorageProvider: () => mocks.storageProvider.current }));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    postAdminPurgePreview = mocks.postAdminPurgePreview;
    postAdminPurge = mocks.postAdminPurge;
    postAdminDeleteByStatusPreview = mocks.postAdminDeleteByStatusPreview;
    postAdminDeleteByStatus = mocks.postAdminDeleteByStatus;
    postAdminSkipPreview = mocks.postAdminSkipPreview;
    postAdminSkip = mocks.postAdminSkip;
    postAdminDeleteByToPreview = mocks.postAdminDeleteByToPreview;
    postAdminDeleteByTo = mocks.postAdminDeleteByTo;
    postAdminCopy = mocks.postAdminCopy;
    postAdminDeleteAll = mocks.postAdminDeleteAll;
    getAdminFailedPreview = mocks.getAdminFailedPreview;
    postAdminBulkResubmit = mocks.postAdminBulkResubmit;
    getAdminDeadletteredPreview = mocks.getAdminDeadletteredPreview;
    postAdminDeleteDeadlettered = mocks.postAdminDeleteDeadlettered;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const endpoints = [{ value: "crm", label: "crm" }];
const bulkResult = { processed: 3, succeeded: 3, failed: 0 };

function serverError(status = 500, detail = "The store is unavailable") {
  return new SwaggerException(
    "An unexpected server error occurred.",
    status,
    JSON.stringify({ title: "Server error", status, detail }),
    {},
    null,
  );
}

interface PreviewCard {
  name: string;
  render: () => ReactElement;
  /** Fills the inputs the Preview button needs. */
  prepare?: () => void;
  preview: ReturnType<typeof vi.fn>;
  previewValue: unknown;
  /** Text only the preview panel shows. */
  previewText: RegExp;
  /** The preview panel's button that opens the confirmation. */
  confirmOpener: RegExp | string;
  dialog: string;
  confirmText: string;
  confirmLabel: string;
  execute: ReturnType<typeof vi.fn>;
}

const previewCards: PreviewCard[] = [
  {
    name: "Purge subscription",
    render: () => <SubscriptionPurgeCard endpoints={endpoints} initialEndpoint="crm" />,
    preview: mocks.postAdminPurgePreview,
    previewValue: { totalScanned: 5, totalMatching: 3, sessionCount: 1 },
    previewText: /Matching:/,
    confirmOpener: /^Purge 3 messages/,
    dialog: "Purge Subscription Messages",
    confirmText: "crm",
    confirmLabel: "Purge subscription",
    execute: mocks.postAdminPurge,
  },
  {
    name: "Delete by status",
    render: () => <DeleteByStatusCard endpoints={endpoints} initialEndpoint="crm" />,
    prepare: () => fireEvent.click(screen.getByLabelText("Failed")),
    preview: mocks.postAdminDeleteByStatusPreview,
    previewValue: { count: 3, blockingCount: 0 },
    previewText: /Events matching/,
    confirmOpener: /^Delete 3 events/,
    dialog: "Delete Events by Status",
    confirmText: "crm",
    confirmLabel: "Delete events",
    execute: mocks.postAdminDeleteByStatus,
  },
  {
    name: "Skip messages",
    render: () => <SkipMessagesCard endpoints={endpoints} initialEndpoint="crm" />,
    prepare: () => fireEvent.click(screen.getByLabelText("Failed")),
    preview: mocks.postAdminSkipPreview,
    previewValue: { count: 3 },
    previewText: /Eligible to skip/,
    confirmOpener: /^Skip 3 messages/,
    dialog: "Skip Messages",
    confirmText: "crm",
    confirmLabel: "Skip messages",
    execute: mocks.postAdminSkip,
  },
  {
    name: "Delete by To field",
    render: () => <DeleteMessagesByToCard />,
    prepare: () => fireEvent.change(screen.getByPlaceholderText("e.g. CrmEndpoint"), { target: { value: "crm" } }),
    preview: mocks.postAdminDeleteByToPreview,
    previewValue: { count: 3 },
    previewText: /Messages matching/,
    confirmOpener: /^Delete 3 messages/,
    dialog: "Delete Messages by To",
    confirmText: "crm",
    confirmLabel: "Delete messages",
    execute: mocks.postAdminDeleteByTo,
  },
  {
    name: "Bulk resubmit failed",
    render: () => <BulkResubmitCard endpoints={endpoints} initialEndpoint="crm" />,
    preview: mocks.getAdminFailedPreview,
    previewValue: { totalFailedCount: 3, eligibleCount: 3, ageThresholdMinutes: 10 },
    previewText: /Total failed/,
    confirmOpener: /^Resubmit 3 messages/,
    dialog: "Bulk Resubmit Failed Messages",
    confirmText: "crm",
    confirmLabel: "Resubmit",
    execute: mocks.postAdminBulkResubmit,
  },
  {
    name: "Delete dead-lettered",
    render: () => <DeleteDeadLetteredCard endpoints={endpoints} initialEndpoint="crm" />,
    preview: mocks.getAdminDeadletteredPreview,
    previewValue: { count: 2, queueCount: 2, matchingQueueCount: 2 },
    previewText: /Stored dead-lettered events/,
    confirmOpener: "Delete dead-lettered messages",
    dialog: "Delete Dead-Lettered Messages",
    confirmText: "crm",
    confirmLabel: "Delete dead-lettered",
    execute: mocks.postAdminDeleteDeadlettered,
  },
];

interface ExecuteOnlyCard {
  name: string;
  render: () => ReactElement;
  prepare?: () => void;
  opener: string;
  dialog: string;
  confirmLabel: string;
  execute: ReturnType<typeof vi.fn>;
  executeValue: unknown;
  resultText: RegExp;
}

const executeOnlyCards: ExecuteOnlyCard[] = [
  {
    name: "Copy endpoint data",
    render: () => <CopyEndpointCard endpoints={endpoints} initialEndpoint="crm" />,
    prepare: () => fireEvent.change(screen.getByPlaceholderText("AccountEndpoint=..."), { target: { value: "AccountEndpoint=x" } }),
    opener: "Copy Data",
    dialog: "Copy Endpoint Data",
    confirmLabel: "Copy data",
    execute: mocks.postAdminCopy,
    executeValue: { eventsCopied: 1, messagesCopied: 2 },
    resultText: /Events copied/,
  },
  {
    name: "Delete all events",
    render: () => <DeleteAllEventsCard endpoints={endpoints} initialEndpoint="crm" />,
    opener: "Delete All",
    dialog: "Delete All Events",
    confirmLabel: "Delete all events",
    execute: mocks.postAdminDeleteAll,
    executeValue: bulkResult,
    resultText: /succeeded/,
  },
];

beforeEach(() => {
  mocks.storageProvider.current = undefined;
  for (const card of previewCards) {
    card.preview.mockReset().mockResolvedValue(card.previewValue);
    card.execute.mockReset().mockResolvedValue(bulkResult);
  }
  for (const card of executeOnlyCards) {
    card.execute.mockReset().mockResolvedValue(card.executeValue);
  }
});

afterEach(() => cleanup());

function clickPreview() {
  fireEvent.click(screen.getByRole("button", { name: "Preview" }));
}

function confirm(dialogName: string, confirmText: string, confirmLabel: string) {
  const dialog = screen.getByRole("dialog", { name: dialogName });
  fireEvent.change(within(dialog).getByPlaceholderText(confirmText), { target: { value: confirmText } });
  fireEvent.click(within(dialog).getByRole("button", { name: confirmLabel }));
}

describe.each(previewCards)("$name card", (card) => {
  async function previewSucceeds() {
    clickPreview();
    await screen.findByText(card.previewText);
  }

  async function execute() {
    fireEvent.click(await screen.findByRole("button", { name: card.confirmOpener }));
    confirm(card.dialog, card.confirmText, card.confirmLabel);
    await waitFor(() => expect(card.execute).toHaveBeenCalled());
  }

  it("shows a failed preview's status and detail and drops the earlier preview", async () => {
    render(card.render());
    card.prepare?.();
    await previewSucceeds();

    card.preview.mockRejectedValue(serverError());
    clickPreview();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("500");
    expect(alert.textContent).toContain("The store is unavailable");
    expect(screen.queryByText(card.previewText)).toBeNull();
    expect(screen.queryByRole("button", { name: card.confirmOpener })).toBeNull();
  });

  it("shows a failed execute's status and detail and drops the preview", async () => {
    render(card.render());
    card.prepare?.();
    await previewSucceeds();

    card.execute.mockRejectedValue(serverError(409, "Something changed"));
    await execute();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("409");
    expect(alert.textContent).toContain("Something changed");
    expect(screen.queryByText(card.previewText)).toBeNull();
    expect(screen.queryByRole("button", { name: card.confirmOpener })).toBeNull();
    expect(screen.queryByText(/succeeded/)).toBeNull();
  });

  it("drops an earlier result when a later preview fails", async () => {
    render(card.render());
    card.prepare?.();
    await previewSucceeds();
    await execute();
    await screen.findByText(/succeeded/);

    card.preview.mockRejectedValue(serverError());
    clickPreview();

    await screen.findByRole("alert");
    expect(screen.queryByText(/succeeded/)).toBeNull();
  });

  it("clears the error when a new preview starts, and shows the result of one that succeeds", async () => {
    render(card.render());
    card.prepare?.();
    card.preview.mockRejectedValueOnce(serverError());
    clickPreview();
    await screen.findByRole("alert");

    await previewSucceeds();

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("still says the request failed when it has no HTTP status", async () => {
    render(card.render());
    card.prepare?.();
    card.preview.mockRejectedValue(new TypeError("Failed to fetch"));
    clickPreview();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/request failed/i);
    expect(alert.textContent).toContain("Failed to fetch");
  });
});

describe.each(executeOnlyCards)("$name card", (card) => {
  async function execute() {
    fireEvent.click(screen.getByRole("button", { name: card.opener }));
    confirm(card.dialog, "crm", card.confirmLabel);
    await waitFor(() => expect(card.execute).toHaveBeenCalled());
  }

  it("shows a failed execute's status and detail and drops the earlier result", async () => {
    render(card.render());
    card.prepare?.();
    await execute();
    await screen.findByText(card.resultText);

    card.execute.mockRejectedValue(serverError());
    await execute();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("500");
    expect(alert.textContent).toContain("The store is unavailable");
    expect(screen.queryByText(card.resultText)).toBeNull();
  });

  it("clears the error when a new execute succeeds", async () => {
    render(card.render());
    card.prepare?.();
    card.execute.mockRejectedValueOnce(serverError());
    await execute();
    await screen.findByRole("alert");

    await execute();

    await screen.findByText(card.resultText);
    expect(screen.queryByRole("alert")).toBeNull();
  });
});

describe("error reset on input change", () => {
  it("clears the Delete by status error when a status is toggled", async () => {
    render(<DeleteByStatusCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.click(screen.getByLabelText("Failed"));
    mocks.postAdminDeleteByStatusPreview.mockRejectedValue(serverError());
    clickPreview();
    await screen.findByRole("alert");

    fireEvent.click(screen.getByLabelText("Pending"));

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("clears the Delete by To error when the To field changes", async () => {
    render(<DeleteMessagesByToCard />);
    const to = screen.getByPlaceholderText("e.g. CrmEndpoint");
    fireEvent.change(to, { target: { value: "crm" } });
    mocks.postAdminDeleteByToPreview.mockRejectedValue(serverError());
    clickPreview();
    await screen.findByRole("alert");

    fireEvent.change(to, { target: { value: "erp" } });

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("says a 403 needs the site Owner role, with the status", async () => {
    render(<DeleteAllEventsCard endpoints={endpoints} initialEndpoint="crm" />);
    mocks.postAdminDeleteAll.mockRejectedValue(new SwaggerException("Forbidden", 403, "", {}, null));
    fireEvent.click(screen.getByRole("button", { name: "Delete All" }));
    confirm("Delete All Events", "crm", "Delete all events");

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("403");
    expect(alert.textContent).toContain("Owner");
  });
});

// The Cosmos-only operations can't run on another store; the cards say so instead of failing.
describe("Cosmos-only cards", () => {
  it.each(["SQL Server", "InMemory"])("disable Delete by To field on %s", async (provider) => {
    mocks.storageProvider.current = provider;
    render(<DeleteMessagesByToCard />);
    fireEvent.change(screen.getByPlaceholderText("e.g. CrmEndpoint"), { target: { value: "crm" } });

    expect(screen.getByText(/only with the Cosmos DB storage provider/)).toBeTruthy();
    const preview = screen.getByRole("button", { name: "Preview" }) as HTMLButtonElement;
    expect(preview.disabled).toBe(true);
    fireEvent.click(preview);
    expect(mocks.postAdminDeleteByToPreview).not.toHaveBeenCalled();
  });

  it.each(["SQL Server", "InMemory"])("disable Copy endpoint data on %s", async (provider) => {
    mocks.storageProvider.current = provider;
    render(<CopyEndpointCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.change(screen.getByPlaceholderText("AccountEndpoint=..."), { target: { value: "AccountEndpoint=x" } });

    expect(screen.getByText(/only with the Cosmos DB storage provider/)).toBeTruthy();
    const copy = screen.getByRole("button", { name: "Copy Data" }) as HTMLButtonElement;
    expect(copy.disabled).toBe(true);
    fireEvent.click(copy);
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(mocks.postAdminCopy).not.toHaveBeenCalled();
  });

  it.each([["Cosmos DB"], [undefined], [null]])("leave Delete by To field enabled when the provider is %s", async (provider) => {
    mocks.storageProvider.current = provider;
    render(<DeleteMessagesByToCard />);
    fireEvent.change(screen.getByPlaceholderText("e.g. CrmEndpoint"), { target: { value: "crm" } });

    expect(screen.queryByText(/only with the Cosmos DB storage provider/)).toBeNull();
    clickPreview();
    await waitFor(() => expect(mocks.postAdminDeleteByToPreview).toHaveBeenCalled());
  });

  it.each([["Cosmos DB"], [undefined], [null]])("leave Copy endpoint data enabled when the provider is %s", async (provider) => {
    mocks.storageProvider.current = provider;
    render(<CopyEndpointCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.change(screen.getByPlaceholderText("AccountEndpoint=..."), { target: { value: "AccountEndpoint=x" } });

    expect(screen.queryByText(/only with the Cosmos DB storage provider/)).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Copy Data" }));
    confirm("Copy Endpoint Data", "crm", "Copy data");
    await waitFor(() => expect(mocks.postAdminCopy).toHaveBeenCalled());
  });

  it("says Delete by To field is Cosmos DB only in its description", () => {
    render(<DeleteMessagesByToCard />);

    expect(screen.getByText(/filtered by the "To" field \(Cosmos DB only\)/)).toBeTruthy();
  });
});
