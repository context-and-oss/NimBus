import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, render, waitFor, act } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as React from "react";
import * as api from "api-client";
import EndpointsList from "./endpoints-list";
import moment from "moment";

it("hydrates ISO timestamps from server-rendered endpoint state", () => {
  const raw = { eventTime: "2026-09-29T20:53:00Z", oldestFailureAt: "2026-09-28T19:00:00Z" } as unknown as api.EndpointStatusCount;
  const mapped = new EndpointsList({}).mapMomentInEndpointStatus(raw);
  expect(moment.isMoment(mapped.eventTime)).toBe(true);
  expect(mapped.eventTime?.toISOString()).toBe("2026-09-29T20:53:00.000Z");
  expect(mapped.oldestFailureAt?.toISOString()).toBe("2026-09-28T19:00:00.000Z");
});

it("does not reparse hydrated endpoint dates through Moment's string fallback", () => {
  const endpoint = new api.EndpointStatusCount({
    eventTime: moment("2026-09-29T20:53:00Z"),
  });
  const warning = vi.spyOn(console, "warn").mockImplementation(() => {});
  try {
    const mapped = new EndpointsList({}).mapMomentInEndpointStatus(endpoint);
    expect(mapped.eventTime?.valueOf()).toBe(endpoint.eventTime?.valueOf());
    expect(warning).not.toHaveBeenCalled();
  } finally {
    warning.mockRestore();
  }
});

// Shared fakes for the two api-client calls the endpoints list makes while
// resolving which statuses to show. Declared via vi.hoisted so the hoisted
// vi.mock factory below can close over them and the test can assert on them.
const mocks = vi.hoisted(() => ({
  getEndpointsAll: vi.fn(),
  postApiEndpointStatusCount: vi.fn(),
  cookieGet: vi.fn(() => "ep1" as string | undefined),
  cookieSet: vi.fn(),
  cookieRemove: vi.fn(),
}));

// The cookie seeds the initial "checked" filter. Returning a single id means
// mount fetches only ep1, so checking ep2/ep3 later exercises the "fetch only
// the ids we don't already hold" path.
vi.mock("js-cookie", () => ({
  default: {
    get: mocks.cookieGet,
    set: mocks.cookieSet,
    remove: mocks.cookieRemove,
  },
}));

// Capture the DataTable props instead of rendering the full TanStack table —
// the rows array and checkedEndpointIds are all we need to assert what the
// user would see for a given checkbox state.
const captured: { rows?: { id: string }[]; checked?: string[] } = {};
vi.mock("components/data-table", () => ({
  default: (props: {
    rows: { id: string }[];
    checkedEndpointIds?: string[];
  }) => {
    captured.rows = props.rows;
    captured.checked = props.checkedEndpointIds;
    return <div data-testid="data-table-stub" />;
  },
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    getEndpointsAll = mocks.getEndpointsAll;
    postApiEndpointStatusCount = mocks.postApiEndpointStatusCount;
    postApiMetadatashort = vi.fn().mockResolvedValue([]);
    getEndpointStatusCountId = vi.fn();
    getMetadataEndpoint = vi.fn();
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const rowIds = () => (captured.rows ?? []).map((r) => r.id).sort();

beforeEach(() => {
  captured.rows = undefined;
  captured.checked = undefined;
  mocks.cookieGet.mockReset().mockReturnValue("ep1");
  mocks.cookieSet.mockReset();
  mocks.cookieRemove.mockReset();
  mocks.getEndpointsAll.mockReset().mockResolvedValue(["ep1", "ep2", "ep3"]);
  mocks.postApiEndpointStatusCount
    .mockReset()
    .mockImplementation(async (ids: string[]) =>
      ids.map((id) =>
        Object.assign(new api.EndpointStatusCount(), {
          endpointId: id,
          failedCount: 0,
          deferredCount: 0,
          pendingCount: 0,
        }),
      ),
    );
});

afterEach(() => {
  cleanup();
});

describe("EndpointsList handleCheck (wave3)", () => {
  it("fetches only missing statuses on check, never refetches on uncheck, and never mutates state in place", async () => {
    const ref = React.createRef<InstanceType<typeof EndpointsList>>();

    render(
      <MemoryRouter>
        <EndpointsList ref={ref} />
      </MemoryRouter>,
    );

    // Mount fetches only the cookie-filtered id (ep1) into the status cache and
    // shows it; the checkbox list still spans every endpoint.
    await waitFor(() => expect(ref.current?.state.loading).toBe(false));
    expect(mocks.postApiEndpointStatusCount).toHaveBeenCalledTimes(1);
    expect(mocks.postApiEndpointStatusCount).toHaveBeenLastCalledWith(["ep1"]);
    expect(rowIds()).toEqual(["ep1"]);
    expect(captured.checked).toEqual(["ep1"]);

    // Check ep2: its status is fetched exactly once (ep1 is not refetched) and
    // it appears alongside ep1. The checked array is a brand-new reference.
    const checkedBeforeCheck = ref.current!.state.checked;
    const statusBeforeCheck = ref.current!.state.statusById;
    await act(async () => {
      await ref.current!.handleCheck("ep2", true);
    });
    expect(mocks.postApiEndpointStatusCount).toHaveBeenCalledTimes(2);
    expect(mocks.postApiEndpointStatusCount).toHaveBeenLastCalledWith(["ep2"]);
    expect(rowIds()).toEqual(["ep1", "ep2"]);
    expect(ref.current!.state.checked).not.toBe(checkedBeforeCheck);
    expect(ref.current!.state.statusById).not.toBe(statusBeforeCheck);
    // The prior snapshot was not mutated in place.
    expect(checkedBeforeCheck).toEqual(["ep1"]);

    // Check ep3: again only the newly-checked id is fetched.
    await act(async () => {
      await ref.current!.handleCheck("ep3", true);
    });
    expect(mocks.postApiEndpointStatusCount).toHaveBeenCalledTimes(3);
    expect(mocks.postApiEndpointStatusCount).toHaveBeenLastCalledWith(["ep3"]);
    expect(rowIds()).toEqual(["ep1", "ep2", "ep3"]);

    // Uncheck ep3: no refetch at all — the remaining rows come straight from
    // the cached statuses — and the checked set is a new array.
    const checkedBeforeUncheck = ref.current!.state.checked;
    await act(async () => {
      await ref.current!.handleCheck("ep3", false);
    });
    expect(mocks.postApiEndpointStatusCount).toHaveBeenCalledTimes(3);
    expect(rowIds()).toEqual(["ep1", "ep2"]);
    expect(captured.checked).toEqual(["ep1", "ep2"]);
    expect(ref.current!.state.checked).not.toBe(checkedBeforeUncheck);
    expect(checkedBeforeUncheck).toEqual(["ep1", "ep2", "ep3"]);
  });
});

describe("EndpointsList filter persistence", () => {
  const mount = async () => {
    const ref = React.createRef<InstanceType<typeof EndpointsList>>();
    render(
      <MemoryRouter>
        <EndpointsList ref={ref} />
      </MemoryRouter>,
    );
    await waitFor(() => expect(ref.current?.state.loading).toBe(false));
    return ref;
  };

  it("writes the ticked ids to the cookie so the filter survives navigation", async () => {
    const ref = await mount();

    await act(async () => {
      await ref.current!.handleCheck("ep2", true);
    });

    expect(mocks.cookieSet).toHaveBeenLastCalledWith(
      "endpointFilters",
      "ep1,ep2",
      expect.objectContaining({ sameSite: "lax" }),
    );
  });

  it("clears the cookie when the last box is unticked (show all)", async () => {
    const ref = await mount();

    await act(async () => {
      await ref.current!.handleCheck("ep1", false);
    });

    expect(mocks.cookieRemove).toHaveBeenCalledWith("endpointFilters");
    expect(rowIds()).toEqual(["ep1", "ep2", "ep3"]);
  });

  it("restores the persisted filter on a fresh mount", async () => {
    mocks.cookieGet.mockReturnValue("ep2,ep3");

    await mount();

    expect(mocks.postApiEndpointStatusCount).toHaveBeenLastCalledWith([
      "ep2",
      "ep3",
    ]);
    expect(captured.checked).toEqual(["ep2", "ep3"]);
    expect(rowIds()).toEqual(["ep2", "ep3"]);
  });

  it("drops persisted ids the catalog no longer knows about", async () => {
    mocks.cookieGet.mockReturnValue("ep1,gone");

    const ref = await mount();

    expect(mocks.postApiEndpointStatusCount).toHaveBeenLastCalledWith(["ep1"]);
    expect(ref.current!.state.checked).toEqual(["ep1"]);
    expect(mocks.cookieSet).toHaveBeenLastCalledWith(
      "endpointFilters",
      "ep1",
      expect.objectContaining({ sameSite: "lax" }),
    );
  });
});
