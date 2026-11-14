import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import FailureDetailPanel, {
  type FailureDetailPanelProps,
} from "./failure-detail-panel";

const mocks = vi.hoisted(() => ({
  event: vi.fn(),
  history: vi.fn(),
  audits: vi.fn(),
  blocked: vi.fn(),
  resubmit: vi.fn(),
  eventTypes: vi.fn().mockResolvedValue([]),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getEventId = mocks.event;
    getEventDetailsHistoryId = mocks.history;
    getMessageAuditsEventId = mocks.audits;
    getEventBlockedId = mocks.blocked;
    postResubmitWithChangesEventIds = mocks.resubmit;
    getEventTypes = mocks.eventTypes;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// Failure intelligence calls its own endpoints; it has its own tests.
vi.mock("components/event-details/intelligence-card", () => ({
  default: () => null,
}));

const failed = new api.Event({
  eventId: "b5ec63b6",
  endpointId: "Crm",
  sessionId: "lead-1",
  lastMessageId: "m-last",
  eventTypeId: "PartnerLeadSubmitted",
  resolutionStatus: api.ResolutionStatus.Failed,
  retryCount: 2,
  retryLimit: 2,
  resubmitCount: 1,
  queueTimeMs: 41,
  processingTimeMs: 1020,
  updatedAt: moment("2026-09-28T18:06:31Z"),
  messageContent: new api.MessageContent({
    errorContent: new api.ErrorContent({
      errorText: "503 Service Unavailable",
    }),
    eventContent: new api.EventContent({ eventJson: '{"LeadId":"4d3c"}' }),
  }),
});

const message = (
  messageType: api.MessageType,
  iso: string,
  extra: Partial<api.IMessage> = {},
) =>
  new api.Message({
    messageId: `${messageType}-${iso}`,
    messageType,
    enqueuedTimeUtc: moment(iso),
    from: "Portal",
    to: "Crm",
    ...extra,
  });

const renderPanel = (props: Partial<FailureDetailPanelProps> = {}) => {
  const all: FailureDetailPanelProps = {
    endpointId: "Crm",
    eventId: "b5ec63b6",
    onClose: vi.fn(),
    onAct: vi.fn(),
    onShowSimilar: vi.fn(),
    ...props,
  };
  render(
    <MemoryRouter>
      <FailureDetailPanel {...all} />
    </MemoryRouter>,
  );
  return all;
};

beforeEach(() => {
  mocks.event.mockResolvedValue(failed);
  mocks.history.mockResolvedValue([
    message(api.MessageType.EventRequest, "2026-09-28T17:58:02.114Z"),
    message(api.MessageType.ErrorResponse, "2026-09-28T17:58:03.356Z", {
      errorContent: new api.ErrorContent({
        errorText: "first 503",
        errorType: "HttpRequestException",
      }),
    }),
    message(api.MessageType.ResubmissionRequest, "2026-09-28T18:06:30.455Z"),
    message(api.MessageType.ErrorResponse, "2026-09-28T18:06:31.479Z", {
      errorContent: new api.ErrorContent({
        errorText: "latest 503",
        exceptionStackTrace: "at Handler.Handle()",
      }),
    }),
  ]);
  mocks.audits.mockResolvedValue([]);
  mocks.blocked.mockResolvedValue(
    new api.BlockedEventsPage({
      items: [
        new api.BlockedEvent({ eventId: "0f3e91c2-x", status: "Deferred" }),
      ],
      total: 2,
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("FailureDetailPanel", () => {
  it("opens on the latest error, with its stack trace", async () => {
    renderPanel();

    const selected = await screen.findByRole("region", {
      name: "Selected entry",
    });
    expect(within(selected).getByText("latest 503")).toBeTruthy();
    expect(within(selected).getByText("at Handler.Handle()")).toBeTruthy();
    expect(mocks.event).toHaveBeenCalledWith("b5ec63b6", "Crm");
    expect(mocks.blocked).toHaveBeenCalledWith("Crm", "lead-1", 0, 5);
  });

  it("shows another entry when it is selected in the flow", async () => {
    renderPanel();
    const flow = await screen.findByRole("list", { name: "Messages" });

    const rows = within(flow).getAllByRole("button");
    expect(rows).toHaveLength(4);
    fireEvent.click(rows[1]);

    const selected = screen.getByRole("region", { name: "Selected entry" });
    expect(within(selected).getByText("first 503")).toBeTruthy();
    expect(within(selected).getByText("HttpRequestException")).toBeTruthy();
    // The request is a span up to its response.
    expect(within(rows[0]).getByText("1.24 s")).toBeTruthy();
  });

  it("shows properties, blocked siblings and the payload", async () => {
    renderPanel();

    const props = await screen.findByRole("region", { name: "Properties" });
    expect(within(props).getByText("2 of 2 used")).toBeTruthy();
    const blocked = screen.getByRole("region", { name: "Blocked in session" });
    expect(within(blocked).getByText("0f3e91c2…")).toBeTruthy();
    expect(within(blocked).getByText(/1 more\./)).toBeTruthy();
    expect(screen.getByText(/"LeadId": "4d3c"/)).toBeTruthy();
    expect(screen.getByText("2 blocked in session")).toBeTruthy();
  });

  it("resubmits and skips the loaded event", async () => {
    const props = renderPanel();
    await screen.findByRole("region", { name: "Properties" });

    fireEvent.click(screen.getByRole("button", { name: "Resubmit" }));
    fireEvent.click(screen.getByRole("button", { name: "Skip" }));

    expect(props.onAct).toHaveBeenNthCalledWith(1, "Resubmit", failed);
    expect(props.onAct).toHaveBeenNthCalledWith(2, "Skip", failed);
  });

  it("falls back to the event's own error without history", async () => {
    mocks.history.mockResolvedValue([]);
    renderPanel();

    expect(
      await screen.findByText("No message history available for this event."),
    ).toBeTruthy();
    const selected = screen.getByRole("region", { name: "Selected entry" });
    expect(within(selected).getByText("503 Service Unavailable")).toBeTruthy();
  });

  it("says so when the event cannot be loaded", async () => {
    mocks.event.mockRejectedValue(new Error("Not found"));
    renderPanel();

    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText("Not found")).toBeTruthy();
  });

  it("disables Previous and Next at the ends", async () => {
    const props = renderPanel({ onNext: vi.fn() });

    await waitFor(() =>
      expect(
        (screen.getByLabelText("Previous failure") as HTMLButtonElement)
          .disabled,
      ).toBe(true),
    );
    fireEvent.click(screen.getByLabelText("Next failure"));
    expect(props.onNext).toHaveBeenCalledTimes(1);
  });
});

it("edits and resubmits in the drawer without leaving the page", async () => {
  mocks.resubmit.mockResolvedValue(undefined);
  const props = renderPanel();
  await screen.findByRole("region", { name: "Properties" });
  fireEvent.click(
    screen.getByRole("button", { name: /Resubmit with changes/ }),
  );
  const dialog = screen.getByRole("dialog");
  const editor = within(dialog).getByRole("textbox", {
    name: "Modified event:",
  });
  expect((editor as HTMLTextAreaElement).value).toContain("\n");
  fireEvent.change(editor, {
    target: { value: '{\n  "LeadId": "changed"\n}' },
  });
  fireEvent.click(within(dialog).getByRole("button", { name: "Resubmit" }));
  await waitFor(() =>
    expect(mocks.resubmit).toHaveBeenCalledWith(
      "b5ec63b6",
      "m-last",
      expect.objectContaining({
        eventContent: '{\n  "LeadId": "changed"\n}',
        eventTypeId: "PartnerLeadSubmitted",
      }),
    ),
  );
  expect(await screen.findByText(/Resubmission accepted/)).toBeTruthy();
  expect(
    (screen.getByRole("button", { name: "Resubmit" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  expect(props.onClose).not.toHaveBeenCalled();
});
