import {
  describe,
  it,
  expect,
  vi,
  beforeEach,
  afterEach,
} from "vitest";
import {
  act,
  render,
  screen,
  cleanup,
  waitFor,
  fireEvent,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import EventTypeExamplePayload, { UNDO_WINDOW_MS } from "./event-type-example-payload";
import type { Toast } from "components/ui/toast";
import * as api from "api-client";

// Toast is mounted from a provider in the real app; stub it so the component
// under test can call addToast / emitToast without bringing the provider into scope.
const addToast = vi.fn();
const { emitToastMock } = vi.hoisted(() => ({ emitToastMock: vi.fn() }));
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast }),
  emitToast: (toast: Omit<Toast, "id">) => emitToastMock(toast),
}));

// The api-client module is the call surface the Generate-fake-data button
// invokes. We replace the Client class so we can drive the response.
// Note: vi.mock is hoisted; using vi.hoisted lets us share the mock fn
// across the factory body and the test assertions.
const { getEventtypesEventtypeidFakeMock, postComposeNewEventMock } = vi.hoisted(() => ({
  getEventtypesEventtypeidFakeMock: vi.fn(),
  postComposeNewEventMock: vi.fn(),
}));
vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class MockClient {
    getEventtypesEventtypeidFake = getEventtypesEventtypeidFakeMock;
    postComposeNewEvent = postComposeNewEventMock;
  }
  return {
    ...actual,
    Client: MockClient,
    CookieAuth: () => ({}),
  };
});

function buildEventType(overrides: Partial<api.EventType> = {}): api.EventType {
  // The component reads .id / .name / .properties. We hand-roll the shape so
  // the test does not depend on the full nswag class graph.
  return {
    id: "TestEvent",
    name: "TestEvent",
    namespace: "Test.Events",
    description: "",
    properties: [
      { name: "CustomerId", typeName: "Guid" } as api.EventTypeProperty,
    ],
    ...overrides,
  } as api.EventType;
}

const SUCCESS_PAYLOAD = '{\n  "CustomerId": "00000000-0000-0000-0000-000000000123"\n}';

async function openModalAndClickGenerate() {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: /Compose Event/ }));
  // The Generate-fake-data button only exists inside the modal.
  await user.click(screen.getByRole("button", { name: /Generate fake data/ }));
  return user;
}

describe("EventTypeExamplePayload — example payload source", () => {
  afterEach(() => {
    cleanup();
  });

  it("renders the authored example payload when the API provides one", () => {
    const authored = '{\n  "CustomerId": "9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10"\n}';
    render(
      <EventTypeExamplePayload eventType={buildEventType({ examplePayload: authored })} />,
    );

    expect(screen.getByText(/9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10/)).toBeTruthy();
    expect(screen.queryByText(/"Guid"/)).toBeNull();
  });

  it("falls back to a type-name placeholder when no example is authored", () => {
    render(<EventTypeExamplePayload eventType={buildEventType({ examplePayload: null })} />);

    expect(screen.getByText(/"Guid"/)).toBeTruthy();
  });

  it("seeds the Compose textarea from the authored example", async () => {
    const authored = '{\n  "CustomerId": "9c1a6f2e-4c31-4f0e-9a1c-2f7d5b3e8a10"\n}';
    render(
      <EventTypeExamplePayload eventType={buildEventType({ examplePayload: authored })} />,
    );
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));

    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    expect(textarea.value).toBe(authored);
  });
});

describe("EventTypeExamplePayload — Generate fake data wiring", () => {
  beforeEach(() => {
    addToast.mockClear();
    getEventtypesEventtypeidFakeMock.mockReset();
  });

  afterEach(() => {
    cleanup();
  });

  it("populates the textarea when the API returns a payload", async () => {
    getEventtypesEventtypeidFakeMock.mockResolvedValue({
      payload: SUCCESS_PAYLOAD,
    });

    render(<EventTypeExamplePayload eventType={buildEventType()} />);
    await openModalAndClickGenerate();

    await waitFor(() => {
      expect(getEventtypesEventtypeidFakeMock).toHaveBeenCalledWith("TestEvent");
    });

    // The textarea should now hold the returned payload string verbatim.
    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    await waitFor(() => {
      expect(textarea.value).toBe(SUCCESS_PAYLOAD);
    });

    // No error toast on the happy path.
    expect(addToast).not.toHaveBeenCalled();
  });

  it("shows a non-blocking toast and leaves the textarea unchanged when payload is null", async () => {
    getEventtypesEventtypeidFakeMock.mockResolvedValue({ payload: null });

    render(<EventTypeExamplePayload eventType={buildEventType()} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    const before = textarea.value;
    await user.click(screen.getByRole("button", { name: /Generate fake data/ }));

    await waitFor(() => {
      expect(addToast).toHaveBeenCalledTimes(1);
    });
    expect(addToast.mock.calls[0]?.[0]).toMatchObject({
      title: expect.stringContaining("could not be generated"),
    });
    expect(textarea.value).toBe(before);
  });

  it("shows an error toast and leaves the textarea unchanged when the call fails (5xx / network)", async () => {
    getEventtypesEventtypeidFakeMock.mockRejectedValue(new Error("network blew up"));

    render(<EventTypeExamplePayload eventType={buildEventType()} />);
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    const textarea = screen.getByRole("textbox") as HTMLTextAreaElement;
    const before = textarea.value;
    await user.click(screen.getByRole("button", { name: /Generate fake data/ }));

    await waitFor(() => {
      expect(addToast).toHaveBeenCalledTimes(1);
    });
    expect(addToast.mock.calls[0]?.[0]).toMatchObject({
      title: expect.stringContaining("Could not generate fake data"),
      variant: "error",
    });
    expect(textarea.value).toBe(before);
  });
});


describe("EventTypeExamplePayload — Send Event", () => {
  const PUBLISHED = {
    eventTypeId: "TestEvent",
    publishedBy: "StorefrontEndpoint",
    messageId: "TestEvent-00c0ffee00c0ffee",
    sessionId: "sess-9",
  };
  const EDITED = '{\n  "CustomerId": "00000000-0000-0000-0000-000000000456"\n}';

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    postComposeNewEventMock.mockReset();
    getEventtypesEventtypeidFakeMock.mockReset();
    emitToastMock.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
    cleanup();
  });

  const renderInRouter = (consumers?: string[]) =>
    render(
      <MemoryRouter>
        <EventTypeExamplePayload eventType={buildEventType()} consumers={consumers} />
      </MemoryRouter>,
    );

  const setupUser = () => userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
  const sendButton = () =>
    screen.getByRole("button", { name: /Send Event/ }) as HTMLButtonElement;
  const editPayload = (value: string) =>
    fireEvent.change(screen.getByRole("textbox"), { target: { value } });
  const passUndoWindow = () =>
    act(async () => {
      await vi.advanceTimersByTimeAsync(UNDO_WINDOW_MS);
    });
  const countdownToast = () =>
    emitToastMock.mock.calls
      .map(([toast]) => toast as Omit<Toast, "id">)
      .find((toast) => toast.title?.startsWith("Publishing"));

  async function composeEditAndSend(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    editPayload(EDITED);
    await user.click(sendButton());
  }

  it("keeps Send disabled until the payload differs from the example", async () => {
    renderInRouter();
    const user = setupUser();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    const example = (screen.getByRole("textbox") as HTMLTextAreaElement).value;

    expect(sendButton().disabled).toBe(true);
    expect(screen.getByText(/Edit the payload or generate fake data to send/)).toBeTruthy();

    editPayload(EDITED);
    expect(sendButton().disabled).toBe(false);

    editPayload(`${example}\n`);
    expect(sendButton().disabled).toBe(true);
  });

  it("enables Send once fake data is generated", async () => {
    getEventtypesEventtypeidFakeMock.mockResolvedValue({ payload: SUCCESS_PAYLOAD });
    renderInRouter();
    const user = setupUser();
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));
    await user.click(screen.getByRole("button", { name: /Generate fake data/ }));

    await waitFor(() => expect(sendButton().disabled).toBe(false));
  });

  it("publishes only when the undo window has passed, naming the consumers first", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter(["BillingEndpoint", "ShippingEndpoint"]);
    const user = setupUser();
    await composeEditAndSend(user);

    expect(postComposeNewEventMock).not.toHaveBeenCalled();
    expect(sendButton().disabled).toBe(true);
    expect(countdownToast()).toMatchObject({
      variant: "warning",
      title: "Publishing TestEvent in 10 s",
      description: "To BillingEndpoint, ShippingEndpoint.",
      duration: UNDO_WINDOW_MS,
      progress: true,
      action: { label: "Undo" },
    });

    // The fake clock also follows real time (shouldAdvanceTime), so stop short by a margin.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(UNDO_WINDOW_MS - 500);
    });
    expect(postComposeNewEventMock).not.toHaveBeenCalled();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(500);
    });
    expect(postComposeNewEventMock).toHaveBeenCalledTimes(1);
    const body = postComposeNewEventMock.mock.calls[0]?.[0] as api.ResubmitWithChanges;
    expect(body.eventTypeId).toBe("TestEvent");
    expect(body.eventContent).toBe(EDITED);
  });

  it("publishes nothing when Undo is clicked", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);

    act(() => countdownToast()?.action?.onClick());
    await passUndoWindow();

    expect(postComposeNewEventMock).not.toHaveBeenCalled();
    expect(emitToastMock).toHaveBeenCalledWith(
      expect.objectContaining({ variant: "info", title: "TestEvent was not published" }),
    );
    expect(sendButton().disabled).toBe(false);
  });

  it("names what was published and links to it on each subscriber and in its session", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);
    await passUndoWindow();

    await waitFor(() =>
      expect(screen.getByText(/Published to StorefrontEndpoint/)).toBeTruthy(),
    );
    expect(screen.getByText(PUBLISHED.messageId)).toBeTruthy();
    expect(screen.getByText(PUBLISHED.sessionId)).toBeTruthy();
    expect(
      screen
        .getByRole("link", { name: /View on each subscriber/ })
        .getAttribute("href"),
    ).toBe(`/Messages?messageId=${encodeURIComponent(PUBLISHED.messageId)}`);
    expect(
      screen.getByRole("link", { name: /View session/ }).getAttribute("href"),
    ).toBe(`/Messages?sessionId=${encodeURIComponent(PUBLISHED.sessionId)}`);
    // Shown in the dialog, so no second notice in a toast.
    expect(emitToastMock).not.toHaveBeenCalledWith(
      expect.objectContaining({ variant: "success" }),
    );
  });

  it("needs another change before the same payload is sent again", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);
    await passUndoWindow();
    await waitFor(() => expect(screen.getByText(PUBLISHED.messageId)).toBeTruthy());

    expect(sendButton().disabled).toBe(true);
    editPayload(`${EDITED} `);
    expect(sendButton().disabled).toBe(true);
    editPayload(SUCCESS_PAYLOAD);
    expect(sendButton().disabled).toBe(false);
  });

  it("keeps the result on screen instead of hiding it after a few seconds", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);
    await passUndoWindow();
    await waitFor(() => expect(screen.getByText(PUBLISHED.messageId)).toBeTruthy());

    await act(async () => {
      vi.advanceTimersByTime(10_000);
    });

    expect(screen.getByText(PUBLISHED.messageId)).toBeTruthy();
  });

  it("still publishes after the dialog is closed, and says so in a toast", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);
    await user.click(screen.getAllByRole("button", { name: /^Close$/ })[0]);
    await passUndoWindow();

    expect(postComposeNewEventMock).toHaveBeenCalledTimes(1);
    expect(emitToastMock).toHaveBeenCalledWith(
      expect.objectContaining({
        variant: "success",
        title: "Published TestEvent",
        description: expect.stringContaining(PUBLISHED.messageId),
      }),
    );
  });

  it("clears the previous result when Compose is opened again", async () => {
    postComposeNewEventMock.mockResolvedValue(PUBLISHED);
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);
    await passUndoWindow();
    await waitFor(() => expect(screen.getByText(PUBLISHED.messageId)).toBeTruthy());

    await user.click(screen.getAllByRole("button", { name: /^Close$/ })[0]);
    await user.click(screen.getByRole("button", { name: /Compose Event/ }));

    expect(screen.queryByText(PUBLISHED.messageId)).toBeNull();
    expect(sendButton().disabled).toBe(true);
  });

  it("shows a failed publish in the dialog and lets the user send again", async () => {
    postComposeNewEventMock.mockRejectedValue(new Error("payload rejected"));
    renderInRouter();
    const user = setupUser();
    await composeEditAndSend(user);
    await passUndoWindow();

    await waitFor(() =>
      expect(screen.getByText(/Failed to compose event: payload rejected/)).toBeTruthy(),
    );
    expect(sendButton().disabled).toBe(false);
  });
});
