import { describe, it, expect, afterEach, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter, useNavigate } from "react-router-dom";
import * as api from "api-client";
import type { ITableRow } from "components/data-table";
import MessagesList from "./messages-list";

vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: vi.fn() }),
}));

it("links an event ID to details while retaining an explicit filter action", async () => {
  render(
    <MemoryRouter>
      <MessagesList />
    </MemoryRouter>,
  );
  await waitFor(() => expect(mocks.deferreds).toHaveLength(1));
  await act(async () =>
    mocks.deferreds[0].resolve({
      messages: [
        new api.Message({
          messageId: "message-1",
          eventId: "event-1",
          endpointId: "Crm",
        }),
      ],
    }),
  );
  render(
    <MemoryRouter>
      {captured.rows?.[0].data.get("eventId")?.value}
    </MemoryRouter>,
  );
  expect(
    screen.getByRole("link", { name: "event-1" }).getAttribute("href"),
  ).toBe("/Message/Index/Crm/event-1/0");
  fireEvent.click(
    screen.getByRole("button", { name: "Filter by event event-1" }),
  );
  await waitFor(() => expect(mocks.deferreds).toHaveLength(2));
});

// A manually-controlled queue of pending searches. Every postMessagesSearch call
// parks a deferred whose promise the test resolves by hand, so we can make an
// EARLIER request resolve AFTER a later one (the out-of-order race).
const mocks = vi.hoisted(() => {
  interface Deferred {
    promise: Promise<unknown>;
    resolve: (value: unknown) => void;
  }
  const deferreds: Deferred[] = [];
  const enqueueSearch = (): Promise<unknown> => {
    let resolve!: (value: unknown) => void;
    const promise = new Promise<unknown>((r) => {
      resolve = r;
    });
    deferreds.push({ promise, resolve });
    return promise;
  };
  return { deferreds, enqueueSearch };
});

// Capture the rows DataTable is asked to render so we can assert which response
// won without depending on the table's internal markup.
const captured: { rows?: ITableRow[] } = {};
vi.mock("components/data-table", async () => {
  const actual = await vi.importActual<typeof import("components/data-table")>(
    "components/data-table",
  );
  return {
    ...actual,
    default: (props: { rows: ITableRow[] }) => {
      captured.rows = props.rows;
      return <div data-testid="data-table-stub" />;
    },
  };
});

// Stub the filter bar so it doesn't fire its own catalog/endpoint API calls;
// the page still drives fetches through the URL-derived filter. Keep the real
// EMPTY_MESSAGE_FILTER / toMessageSearchFilter exports the page relies on.
vi.mock("components/messages/message-filter-bar", async () => {
  const actual = await vi.importActual<
    typeof import("components/messages/message-filter-bar")
  >("components/messages/message-filter-bar");
  return {
    ...actual,
    default: () => <div data-testid="filter-bar-stub" />,
  };
});

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    postMessagesSearch = vi.fn(() => mocks.enqueueSearch());
  }
  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

// Grabs the router's navigate fn so the test can change the URL filter mid-flight.
let navigate: ReturnType<typeof useNavigate> | undefined;
function NavigationProbe() {
  navigate = useNavigate();
  return null;
}

function searchResponse(messageId: string) {
  const message = Object.assign(new api.Message(), {
    messageId,
    eventId: messageId,
  });
  return { messages: [message], continuationToken: undefined };
}

afterEach(() => {
  cleanup();
  captured.rows = undefined;
  mocks.deferreds.length = 0;
  navigate = undefined;
  vi.clearAllMocks();
});

describe("MessagesList out-of-order response guard", () => {
  it("keeps the newer response when a slower earlier request resolves last", async () => {
    render(
      <MemoryRouter initialEntries={["/messages"]}>
        <MessagesList />
        <NavigationProbe />
      </MemoryRouter>,
    );

    // Mount fired the first (soon-to-be-stale) search.
    await waitFor(() => expect(mocks.deferreds).toHaveLength(1));

    // Change the applied filter (as browser Back/forward or a new search would)
    // to fire a second, newer search while the first is still in flight.
    await act(async () => {
      navigate!("/messages?eventId=newer");
    });
    await waitFor(() => expect(mocks.deferreds).toHaveLength(2));

    // The NEWER request (2nd) resolves FIRST with the fresh data...
    await act(async () => {
      mocks.deferreds[1].resolve(searchResponse("fresh"));
    });

    // ...then the older, slower request resolves LAST with stale data. Without a
    // stale-response guard this clobbers the fresh rows we just committed.
    await act(async () => {
      mocks.deferreds[0].resolve(searchResponse("stale"));
    });

    // The table must still show the fresh result — the stale response is dropped.
    expect(captured.rows?.map((r) => r.id)).toEqual(["fresh"]);
  });
});

describe("MessagesList type column", () => {
  const typeCell = (messageType: string) => {
    const message = Object.assign(new api.Message(), {
      messageId: "m1",
      eventId: "m1",
      messageType,
    });
    return { messages: [message], continuationToken: undefined };
  };

  const renderWith = async (messageType: string) => {
    render(
      <MemoryRouter initialEntries={["/messages"]}>
        <MessagesList />
      </MemoryRouter>,
    );
    await waitFor(() => expect(mocks.deferreds).toHaveLength(1));
    await act(async () => {
      mocks.deferreds[0].resolve(typeCell(messageType));
    });
    return captured.rows?.[0].data.get("messageType");
  };

  it("shows the operator label instead of the raw contract value", async () => {
    const cell = await renderWith("resolutionResponse");

    expect(cell?.value).toBe("Completed");
    // The raw value stays searchable so an existing query keeps matching.
    expect(cell?.searchValue).toContain("resolutionResponse");
    expect(cell?.searchValue).toContain("Completed");
  });

  it("labels the legacy PascalCase spelling stored history still carries", async () => {
    const cell = await renderWith("ResolutionResponse");

    expect(cell?.value).toBe("Completed");
  });

  it("falls back to the raw value for a type the catalog does not name", async () => {
    const cell = await renderWith("brandNewResponse");

    expect(cell?.value).toBe("brandNewResponse");
  });
});
